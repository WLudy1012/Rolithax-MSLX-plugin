using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rolithax.Plugin.DaemonExtensions.Resources;

public sealed record ResourceInstallItem(string ProjectId, string VersionId, string Type, string Provider = "modrinth");
public sealed record ResourceInstallRequest(long InstanceId, string GameVersion, string Loader, List<ResourceInstallItem> Items);
public sealed record ResourceInstallTaskData(string Id, string Status, int Progress, string Message, List<string> InstalledFiles);

public sealed class ResourceInstallService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, InstallJob> _jobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _jobGate = new();

    public ResourceInstallTaskData Start(string userId, string instanceBase, ResourceInstallRequest request)
    {
        if (request.InstanceId <= 0 || request.Items is null || request.Items.Count is 0 or > 100)
            throw new ArgumentException("实例编号或资源数量无效");
        if (request.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.ProjectId) || string.IsNullOrWhiteSpace(item.VersionId)))
            throw new ArgumentException("资源项目或版本编号为空");
        if (request.Items.Any(item => !string.Equals(item.Provider, "modrinth", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("当前版本的 Daemon 扩展仅支持 Modrinth 来源");
        if (!Directory.Exists(instanceBase)) throw new DirectoryNotFoundException("实例目录不存在");
        var id = Guid.NewGuid().ToString("N");
        var job = new InstallJob(id, userId, Path.GetFullPath(instanceBase));
        lock (_jobGate)
        {
            PruneJobs();
            var active = _jobs.Values.Select(value => value.Snapshot()).Where(value => value.Status == "running").ToList();
            if (active.Count >= 8 || active.Count(value => _jobs.TryGetValue(value.Id, out var activeJob) && activeJob.UserId == userId) >= 2)
                throw new InvalidOperationException("安装任务过多，请等待当前任务完成");
            while (_jobs.Count >= 256)
            {
                var oldest = _jobs.Values.Where(value => value.CanPrune).OrderBy(value => value.CreatedAt).FirstOrDefault();
                if (oldest == null) throw new InvalidOperationException("安装任务记录已满，请稍后重试");
                _jobs.TryRemove(oldest.Id, out _);
            }
            _jobs[id] = job;
        }
        _ = Task.Run(() => InstallAsync(job, request));
        return job.Snapshot();
    }

    private void PruneJobs()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var (id, job) in _jobs)
            if (job.CanPrune && job.FinishedAt < cutoff) _jobs.TryRemove(id, out _);
    }

    public ResourceInstallTaskData? Get(string id, string userId) =>
        _jobs.TryGetValue(id, out var job) && job.UserId == userId ? job.Snapshot() : null;

    public bool Cancel(string id, string userId) =>
        _jobs.TryGetValue(id, out var job) && job.UserId == userId && job.Cancel();

    public void Shutdown()
    {
        foreach (var job in _jobs.Values) job.Cancel();
    }

    private async Task InstallAsync(InstallJob job, ResourceInstallRequest request)
    {
        var stage = Path.Combine(job.Root, $".rolithax-install-{job.Id}");
        var pending = new List<PendingFile>();
        var committed = new List<(string Target, string? Backup)>();
        var versions = new Dictionary<string, JsonDocument>(StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, JsonDocument>(StringComparer.OrdinalIgnoreCase);
        var instanceLock = _instanceLocks.GetOrAdd(job.Root, _ => new SemaphoreSlim(1, 1));
        var lockHeld = false;
        var preserveStage = false;
        var budget = new InstallBudget(freeSpace: () => GetFreeSpace(stage));
        try
        {
            await instanceLock.WaitAsync(job.Token);
            lockHeld = true;
            Directory.CreateDirectory(stage);
            var selected = request.Items.ToDictionary(item => item.ProjectId, StringComparer.OrdinalIgnoreCase);
            foreach (var item in request.Items)
            {
                job.Token.ThrowIfCancellationRequested();
                ValidateType(item.Type);
                var version = await GetJsonAsync($"https://api.modrinth.com/v3/version/{Uri.EscapeDataString(item.VersionId)}", job.Token);
                var projectId = version.RootElement.TryGetProperty("project_id", out var projectIdValue) ? projectIdValue.GetString() : null;
                if (!string.Equals(projectId, item.ProjectId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"资源版本与项目不匹配：{item.ProjectId}");
                versions[item.ProjectId] = version;
                projects[item.ProjectId] = await GetJsonAsync($"https://api.modrinth.com/v3/project/{Uri.EscapeDataString(item.ProjectId)}", job.Token);
            }
            ValidateDependencies(request, selected, versions, request.GameVersion, request.Loader);

            var index = 0;
            foreach (var item in request.Items)
            {
                job.Token.ThrowIfCancellationRequested();
                var project = projects[item.ProjectId].RootElement;
                var environment = project.TryGetProperty("project_loader_fields", out var fields) && fields.TryGetProperty("environment", out var env)
                    ? env
                    : default;
                if (environment.ValueKind == JsonValueKind.Array && environment.EnumerateArray().Any(value => value.GetString() == "client_only"))
                    throw new InvalidDataException($"{item.ProjectId} 仅支持客户端");
                if (project.TryGetProperty("server_side", out var serverSide) && serverSide.GetString() == "unsupported")
                    throw new InvalidDataException($"{item.ProjectId} 不支持服务端");

                var version = versions[item.ProjectId].RootElement;
                ValidateCompatibility(version, request.GameVersion, request.Loader, item.ProjectId);
                var files = version.GetProperty("files").EnumerateArray().ToList();
                var file = files.FirstOrDefault(candidate => candidate.TryGetProperty("primary", out var primary) && primary.GetBoolean());
                if (file.ValueKind == JsonValueKind.Undefined) file = files.FirstOrDefault();
                if (file.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"{item.ProjectId} 没有可下载文件");
                var name = SafeLeaf(file.GetProperty("filename").GetString() ?? "resource.jar");
                var url = file.GetProperty("url").GetString() ?? throw new InvalidDataException("下载地址为空");
                var destination = Destination(item.Type, name, job.Root);
                var archive = Path.Combine(stage, $"package-{index++}.mrpack");
                budget.AddFile();
                await DownloadVerifiedAsync(url, archive, file.GetProperty("hashes"), ReadOptionalSize(file), budget, job.Token,
                    progress => job.SetProgress(Math.Min(80, 5 + (int)(progress / (float)Math.Max(1, request.Items.Count) * 70))));
                if (item.Type == "modpack")
                {
                    await ExpandModpackAsync(archive, stage, pending, budget, DownloadVerifiedAsync, job.Token);
                    // 展开后整合包本体不再需要，立即释放磁盘和配额。
                    var archiveSize = new FileInfo(archive).Length;
                    File.Delete(archive);
                    budget.Release(archiveSize);
                }
                else
                    pending.Add(new PendingFile(archive, destination));
                job.SetProgress(Math.Min(85, 5 + index * 80 / request.Items.Count));
            }

            EnsureNoDuplicateTargets(pending);
            Commit(job, stage, pending, committed);
            job.Finish("completed", "资源已直接安装到 Daemon 实例", committed.Select(row => Path.GetRelativePath(job.Root, row.Target)).ToList());
        }
        catch (OperationCanceledException)
        {
            preserveStage = !Rollback(committed);
            job.Finish("cancelled", preserveStage ? "安装已取消，但部分文件未能恢复；备份保留在实例临时目录" : "安装已取消", []);
        }
        catch (Exception error)
        {
            preserveStage = !Rollback(committed);
            job.Finish("failed", preserveStage ? "安装失败且未能完整恢复；备份保留在实例临时目录" : error.Message, []);
        }
        finally
        {
            foreach (var document in versions.Values) document.Dispose();
            foreach (var document in projects.Values) document.Dispose();
            if (lockHeld) instanceLock.Release();
            if (!preserveStage) try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            job.Dispose();
        }
    }

    internal delegate Task DownloadFileAsync(string address, string path, JsonElement hashes, long expectedSize,
        InstallBudget budget, CancellationToken token, Action<int> onProgress);

    private const int MaxManifestBytes = 4 * 1024 * 1024;

    internal static async Task ExpandModpackAsync(string archive, string stage, List<PendingFile> pending, InstallBudget budget,
        DownloadFileAsync download, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > InstallBudget.MaxFiles * 2) throw new InvalidDataException("整合包条目数量过多");
        var manifestEntry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("整合包缺少 modrinth.index.json");
        if (manifestEntry.Length > MaxManifestBytes) throw new InvalidDataException("整合包清单过大");
        using var manifestBuffer = new MemoryStream();
        await using (var manifestStream = manifestEntry.Open())
            await CopyLimitedAsync(manifestStream, manifestBuffer, MaxManifestBytes, null, token);
        manifestBuffer.Position = 0;
        using var manifest = await JsonDocument.ParseAsync(manifestBuffer, cancellationToken: token);
        var root = manifest.RootElement;
        var fileIndex = 0;
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (item.TryGetProperty("env", out var env) && env.TryGetProperty("server", out var server) && server.GetString() == "unsupported") continue;
            var size = item.TryGetProperty("fileSize", out var length) ? length.GetInt64() : 0;
            var downloads = item.GetProperty("downloads").EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (downloads.Count == 0) throw new InvalidDataException("整合包文件没有下载地址");
            var hashes = item.GetProperty("hashes");
            var relative = SafeRelative(item.GetProperty("path").GetString() ?? string.Empty);
            var staged = Path.Combine(stage, $"file-{fileIndex++}.bin");
            budget.AddFile();
            // 按声明大小提前拒绝；实际下载字节仍会在 download 内逐块计入总量。
            budget.EnsureFits(Math.Max(0, size));
            await download(downloads[0]!, staged, hashes, size, budget, token, _ => { });
            pending.Add(new PendingFile(staged, relative));
        }

        var serverOverrides = zip.Entries.Where(entry => entry.FullName.StartsWith("server-overrides/", StringComparison.Ordinal)).ToList();
        var overridePrefix = serverOverrides.Count > 0 ? "server-overrides/" : "overrides/";
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(overridePrefix, StringComparison.Ordinal) && !entry.FullName.EndsWith('/')))
        {
            token.ThrowIfCancellationRequested();
            var relative = SafeRelative(entry.FullName[overridePrefix.Length..]);
            var staged = Path.Combine(stage, $"override-{fileIndex++}.bin");
            budget.AddFile();
            budget.EnsureFits(entry.Length);
            await using (var input = entry.Open())
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await CopyLimitedAsync(input, output, InstallBudget.SingleFileLimit, budget, token);
            pending.Add(new PendingFile(staged, relative));
        }
    }

    private static void ValidateDependencies(ResourceInstallRequest request, Dictionary<string, ResourceInstallItem> selected,
        Dictionary<string, JsonDocument> versions, string gameVersion, string loader)
    {
        foreach (var item in request.Items)
        {
            var root = versions[item.ProjectId].RootElement;
            ValidateCompatibility(root, gameVersion, loader, item.ProjectId);
            if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array) continue;
            foreach (var dependency in dependencies.EnumerateArray())
            {
                var projectId = dependency.TryGetProperty("project_id", out var project) ? project.GetString() : null;
                var kind = dependency.TryGetProperty("dependency_type", out var type) ? type.GetString() : null;
                if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(kind)) continue;
                if (kind == "required" && !selected.ContainsKey(projectId))
                    throw new InvalidDataException($"缺少必需依赖 {projectId}");
                if (kind == "incompatible" && selected.ContainsKey(projectId))
                    throw new InvalidDataException($"资源 {item.ProjectId} 与 {projectId} 不兼容");
                if (kind == "required" && dependency.TryGetProperty("version_id", out var requiredVersion) &&
                    selected.TryGetValue(projectId, out var chosen) && chosen.VersionId != requiredVersion.GetString())
                    throw new InvalidDataException($"依赖 {projectId} 需要指定版本");
            }
        }
    }

    private static void ValidateCompatibility(JsonElement version, string gameVersion, string loader, string projectId)
    {
        if (!string.IsNullOrWhiteSpace(gameVersion) && version.TryGetProperty("game_versions", out var gameVersions) &&
            gameVersions.ValueKind == JsonValueKind.Array && !gameVersions.EnumerateArray().Any(value => value.GetString() == gameVersion))
            throw new InvalidDataException($"{projectId} 不支持 Minecraft {gameVersion}");
        if (!string.IsNullOrWhiteSpace(loader) && version.TryGetProperty("loaders", out var loaders) &&
            loaders.ValueKind == JsonValueKind.Array && !loaders.EnumerateArray().Any(value => string.Equals(value.GetString(), loader, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{projectId} 不支持 {loader}");
    }

    private static async Task<JsonDocument> GetJsonAsync(string address, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateApiUrl(address));
        using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }

    private static async Task DownloadVerifiedAsync(string address, string path, JsonElement hashes, long expectedSize,
        InstallBudget budget, CancellationToken token, Action<int> onProgress)
    {
        var uri = ValidateDownloadUrl(address);
        var (algorithm, expectedHash) = GetHash(hashes);
        if (expectedSize < 0 || expectedSize > InstallBudget.SingleFileLimit) throw new InvalidDataException("单个资源大小无效或超过 1 GB");
        budget.EnsureFits(expectedSize);
        using var response = await SendDownloadAsync(uri, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } declared)
        {
            if (declared > InstallBudget.SingleFileLimit) throw new InvalidDataException("下载文件超过 1 GB");
            budget.EnsureFits(declared);
        }
        var stream = await response.Content.ReadAsStreamAsync(token);
        await StreamToFileAsync(stream, path, algorithm, expectedHash, expectedSize, budget, token, onProgress);
    }

    internal static async Task StreamToFileAsync(Stream stream, string path, HashAlgorithmName algorithm, string expectedHash,
        long expectedSize, InstallBudget budget, CancellationToken token, Action<int> onProgress)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[65536];
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            count += read;
            if (count > InstallBudget.SingleFileLimit) throw new InvalidDataException("下载文件超过 1 GB");
            if (expectedSize > 0 && count > expectedSize) throw new InvalidDataException("资源文件长度校验失败");
            budget.Charge(read);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (expectedSize > 0) onProgress((int)Math.Clamp(count * 100 / expectedSize, 0, 100));
        }
        if (expectedSize > 0 && count != expectedSize) throw new InvalidDataException("资源文件长度校验失败");
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expectedHash)))
            throw new InvalidDataException($"资源 {algorithm.Name} 校验失败");
    }

    private static async Task<HttpResponseMessage> SendDownloadAsync(Uri uri, CancellationToken token)
    {
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var redirect = response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            if (!redirect) return response;
            if (attempt == 5 || response.Headers.Location == null)
            {
                response.Dispose();
                throw new InvalidDataException("资源下载重定向次数过多或地址无效");
            }
            var next = ValidateDownloadUrl(new Uri(uri, response.Headers.Location).AbsoluteUri);
            response.Dispose();
            uri = next;
        }
        throw new InvalidDataException("资源下载重定向次数过多");
    }

    private static (HashAlgorithmName Algorithm, string Hash) GetHash(JsonElement hashes)
    {
        foreach (var name in new[] { "sha512", "sha256", "sha1" })
        {
            if (hashes.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var hash = value.GetString()!.ToLowerInvariant();
                var expectedLength = name switch { "sha512" => 128, "sha256" => 64, _ => 40 };
                if (hash.Length != expectedLength || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("资源摘要格式无效");
                var algorithm = name switch { "sha512" => HashAlgorithmName.SHA512, "sha256" => HashAlgorithmName.SHA256, _ => HashAlgorithmName.SHA1 };
                return (algorithm, hash);
            }
        }
        throw new InvalidDataException("缺少可信摘要，拒绝安装");
    }

    private static Uri ValidateApiUrl(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Modrinth API 地址无效");
        if (!string.Equals(uri.IdnHost, "api.modrinth.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Modrinth API 域名无效");
        return uri;
    }

    private static Uri ValidateDownloadUrl(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new InvalidDataException("只允许安全的 HTTPS 下载地址");
        var host = uri.IdnHost.ToLowerInvariant();
        if (!DownloadHosts.Contains(host)) throw new InvalidDataException("资源下载域名不在允许列表");
        return uri;
    }

    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "cdn.modrinth.net", "edge.forgecdn.net", "mediafilez.forgecdn.net",
    };

    private static string Destination(string type, string filename, string root)
    {
        if (type == "mod") return $"mods/{filename}";
        if (type == "plugin") return $"plugins/{filename}";
        if (type == "datapack") return $"{GetWorldName(root)}/datapacks/{filename}";
        return filename;
    }

    private static string GetWorldName(string root)
    {
        var file = Path.Combine(root, "server.properties");
        if (!File.Exists(file)) return "world";
        var line = File.ReadLines(file).FirstOrDefault(row => row.StartsWith("level-name=", StringComparison.Ordinal));
        var name = line?.Split('=', 2)[1].Trim();
        return string.IsNullOrWhiteSpace(name) ? "world" : SafeRelative(name);
    }

    private static string SafeLeaf(string value)
    {
        var name = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new InvalidDataException("文件名无效");
        return name;
    }

    private static string SafeRelative(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("资源路径包含非法片段");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void EnsureNoDuplicateTargets(List<PendingFile> files)
    {
        var duplicate = files.GroupBy(file => file.RelativeTarget, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) throw new InvalidDataException($"多个资源将写入同一文件：{duplicate.Key}");
    }

    private static void Commit(InstallJob job, string stage, List<PendingFile> files, List<(string Target, string? Backup)> committed)
    {
        var rootPath = Path.GetFullPath(job.Root);
        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("实例目录不能是符号链接");
        var index = 0;
        foreach (var file in files)
        {
            job.Token.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(job.Root, SafeRelative(file.RelativeTarget)));
            var relative = Path.GetRelativePath(rootPath, target);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidDataException("安装目标越过实例目录");
            EnsureNoReparsePoints(rootPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string? backup = null;
            if (File.Exists(target))
            {
                backup = Path.Combine(stage, $"backup-{index}");
                File.Move(target, backup);
            }
            try
            {
                File.Move(file.StagedPath, target);
                committed.Add((target, backup));
                index++;
            }
            catch
            {
                if (backup != null && File.Exists(backup)) File.Move(backup, target);
                throw;
            }
        }
    }

    private static bool Rollback(List<(string Target, string? Backup)> committed)
    {
        var restored = true;
        foreach (var (target, backup) in committed.AsEnumerable().Reverse())
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                if (backup != null && File.Exists(backup)) File.Move(backup, target);
            }
            catch { restored = false; }
        }
        return restored;
    }

    private static void EnsureNoReparsePoints(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装目标经过符号链接，已拒绝写入");
        }
    }

    private static long ReadOptionalSize(JsonElement file) =>
        file.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0;

    private static void ValidateType(string type)
    {
        if (type is not ("mod" or "plugin" or "datapack" or "modpack")) throw new ArgumentException("资源类型暂不支持");
    }

    private static long? GetFreeSpace(string path)
    {
        try { return new DriveInfo(path).AvailableFreeSpace; }
        catch { return null; }
    }

    /// <summary>按实际读到的字节复制：超过 <paramref name="maxBytes"/> 或总量配额即中止，不信任压缩条目声明的长度。</summary>
    internal static async Task CopyLimitedAsync(Stream input, Stream output, long maxBytes, InstallBudget? budget, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) return;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("整合包文件过大");
            budget?.Charge(read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    internal sealed record PendingFile(string StagedPath, string RelativeTarget);

    private sealed class InstallJob(string id, string userId, string root) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly object _gate = new();
        private string _status = "running";
        private string _message = "正在解析资源与依赖";
        private int _progress;
        private List<string> _files = [];
        private bool _disposed;

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset FinishedAt { get; private set; } = DateTimeOffset.MaxValue;
        public string Id => id;
        public string UserId => userId;
        public string Root => root;
        public CancellationToken Token => _cancellation.Token;
        public bool CanPrune
        {
            get { lock (_gate) return _status != "running"; }
        }

        public void SetProgress(int progress)
        {
            lock (_gate) _progress = Math.Clamp(progress, 0, 99);
        }

        public void Finish(string status, string message, List<string> files)
        {
            lock (_gate)
            {
                _status = status;
                _message = message;
                _progress = status == "completed" ? 100 : _progress;
                _files = files;
                FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool Cancel()
        {
            lock (_gate)
            {
                if (_status != "running" || _disposed) return false;
                _cancellation.Cancel();
                return true;
            }
        }

        public ResourceInstallTaskData Snapshot()
        {
            lock (_gate) return new ResourceInstallTaskData(id, _status, _progress, _message, [.. _files]);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }

    private static class Http
    {
        private static readonly SocketsHttpHandler Handler = new() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        public static readonly HttpClient Client = new(Handler) { Timeout = TimeSpan.FromMinutes(5) };

        static Http()
        {
            Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rolithax-Launcher", "1.0"));
            Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }
}

public static class ResourceInstallEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Map(IEndpointRouteBuilder endpoints, ResourceInstallService service, string prefix)
    {
        var group = endpoints.MapGroup(prefix);
        group.MapPost("/install", (ResourceInstallRequest request, HttpContext context) =>
        {
            if (request is null) return Fail(400, "安装请求为空");
            var user = GetCurrentUser(context);
            if (user == null) return Unauthorized();
            if (!HasPermission(context, request.InstanceId)) return Forbidden();
            try
            {
                var server = global::MSLX.SDK.MSLX.Config.Servers.GetServer(checked((uint)request.InstanceId));
                if (server == null || string.IsNullOrWhiteSpace(server.Base)) return Fail(404, "实例目录不存在");
                var job = service.Start(user.Id, server.Base, request);
                return Results.Json(new { code = 202, message = "已开始在 Daemon 所在机直接下载并安装", data = job }, JsonOptions, statusCode: 202);
            }
            catch (ArgumentException error) { return Fail(400, error.Message); }
            catch (DirectoryNotFoundException error) { return Fail(404, error.Message); }
            catch (InvalidOperationException error) { return Fail(429, error.Message); }
            catch (Exception error) { return Fail(500, error.Message); }
        }).RequireAuthorization();

        group.MapGet("/install/{id}", (string id, HttpContext context) =>
        {
            var user = GetCurrentUser(context);
            if (user == null) return Unauthorized();
            var job = service.Get(id, user.Id);
            return job == null ? Fail(404, "安装任务不存在") : Results.Json(new { code = 200, message = "ok", data = job }, JsonOptions);
        }).RequireAuthorization();

        group.MapPost("/install/{id}/cancel", (string id, HttpContext context) =>
        {
            var user = GetCurrentUser(context);
            if (user == null) return Unauthorized();
            return service.Cancel(id, user.Id)
                ? Results.Json(new { code = 200, message = "已请求取消" }, JsonOptions)
                : Fail(404, "任务不存在或已结束");
        }).RequireAuthorization();
    }

    private static global::MSLX.SDK.Models.UserInfo? GetCurrentUser(HttpContext context)
    {
        var userId = context.User?.FindFirst("UserId")?.Value;
        return string.IsNullOrWhiteSpace(userId) ? null : global::MSLX.SDK.MSLX.Config.Users.GetUserById(userId);
    }

    private static bool HasPermission(HttpContext context, long instanceId)
    {
        var userId = context.User?.FindFirst("UserId")?.Value ?? string.Empty;
        return instanceId is > 0 and <= int.MaxValue &&
            global::MSLX.SDK.MSLX.Config.Users.HasResourcePermission(userId, "server", checked((int)instanceId));
    }

    private static IResult Unauthorized() => Results.Json(new { code = 401, message = "用户不存在或登录已过期" }, JsonOptions, statusCode: 401);
    private static IResult Forbidden() => Results.Json(new { code = 403, message = "没有该实例的访问权限" }, JsonOptions, statusCode: 403);
    private static IResult Fail(int code, string message) => Results.Json(new { code, message }, JsonOptions, statusCode: code);
}
