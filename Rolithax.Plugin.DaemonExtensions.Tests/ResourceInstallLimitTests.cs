using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Rolithax.Plugin.DaemonExtensions.Resources;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

/// <summary>审核问题 6：资源安装缺少完整的下载和解压总量限制，整合包覆盖文件未纳入统计。</summary>
public sealed class ResourceInstallLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rlx-install-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _stage;

    public ResourceInstallLimitTests()
    {
        _stage = Path.Combine(_dir, "stage");
        Directory.CreateDirectory(_stage);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string BuildPack(IEnumerable<(string Path, string Entry, int Size)> files, params (string Name, int Size)[] entries)
    {
        var manifest = new
        {
            files = files.Select(f => new
            {
                path = f.Path,
                hashes = new { sha1 = new string('0', 40) },
                downloads = new[] { "https://cdn.modrinth.com/data/" + f.Entry },
                fileSize = f.Size,
            }),
        };
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mrpack");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var index = zip.CreateEntry("modrinth.index.json");
            using (var writer = new StreamWriter(index.Open())) writer.Write(JsonSerializer.Serialize(manifest));
            foreach (var (name, size) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(new byte[size]);
            }
        }
        return path;
    }

    private sealed class FakeDownloader
    {
        public int Calls;

        public Task Download(string address, string path, JsonElement hashes, long expectedSize,
            InstallBudget budget, CancellationToken token, Action<int> onProgress)
        {
            Calls++;
            budget.Charge(expectedSize);
            File.WriteAllBytes(path, new byte[expectedSize]);
            return Task.CompletedTask;
        }
    }

    private Task Expand(string pack, InstallBudget budget, FakeDownloader downloader, List<ResourceInstallService.PendingFile>? pending = null) =>
        ResourceInstallService.ExpandModpackAsync(pack, _stage, pending ?? new(), budget, downloader.Download, CancellationToken.None);

    [Fact]
    public async Task Override_files_count_towards_the_same_total_as_downloaded_files()
    {
        var pack = BuildPack(new[] { ("mods/a.jar", "a", 60) },
            ("overrides/config/a.txt", 30), ("overrides/config/b.txt", 30), ("overrides/config/c.txt", 30));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Expand(pack, new InstallBudget(100), new FakeDownloader()));

        Assert.Contains("上限", error.Message);
    }

    [Fact]
    public async Task Overrides_alone_cannot_exceed_the_total()
    {
        var pack = BuildPack(Array.Empty<(string, string, int)>(), ("overrides/a.bin", 30), ("overrides/b.bin", 30));

        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(pack, new InstallBudget(50), new FakeDownloader()));
    }

    [Fact]
    public async Task Pack_within_the_limit_is_expanded_and_counted()
    {
        var pack = BuildPack(new[] { ("mods/a.jar", "a", 60) }, ("overrides/config/a.txt", 30));
        var budget = new InstallBudget(100);
        var pending = new List<ResourceInstallService.PendingFile>();

        await Expand(pack, budget, new FakeDownloader(), pending);

        Assert.Equal(2, pending.Count);
        Assert.Equal(90, budget.Used);
        Assert.All(pending, p => Assert.True(File.Exists(p.StagedPath)));
    }

    [Fact]
    public async Task Server_overrides_replace_the_client_overrides()
    {
        var pack = BuildPack(Array.Empty<(string, string, int)>(), ("overrides/client.txt", 40), ("server-overrides/server.txt", 10));
        var pending = new List<ResourceInstallService.PendingFile>();

        await Expand(pack, new InstallBudget(100), new FakeDownloader(), pending);

        var only = Assert.Single(pending);
        Assert.Equal("server.txt", only.RelativeTarget);
    }

    [Fact]
    public async Task Oversized_declared_entry_is_rejected_before_anything_is_extracted()
    {
        var pack = BuildPack(Array.Empty<(string, string, int)>(), ("overrides/big.bin", 200));

        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(pack, new InstallBudget(100), new FakeDownloader()));

        Assert.Empty(Directory.GetFiles(_stage));
    }

    [Fact]
    public async Task Oversized_declared_download_is_rejected_before_it_starts()
    {
        var pack = BuildPack(new[] { ("mods/a.jar", "a", 500) });
        var downloader = new FakeDownloader();

        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(pack, new InstallBudget(100), downloader));

        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task Downloads_of_several_items_share_one_budget()
    {
        var budget = new InstallBudget(100);
        var first = new byte[60];
        var second = new byte[60];
        Task Run(string name, byte[] data) => ResourceInstallService.StreamToFileAsync(
            new MemoryStream(data), Path.Combine(_stage, name), HashAlgorithmName.SHA256,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), data.Length, budget, CancellationToken.None, _ => { });

        await Run("one.bin", first);
        await Assert.ThrowsAsync<InvalidDataException>(() => Run("two.bin", second));
    }

    [Fact]
    public async Task Stream_longer_than_declared_size_is_cut_off_early()
    {
        var budget = new InstallBudget(1000);
        var data = new byte[200_000];

        await Assert.ThrowsAsync<InvalidDataException>(() => ResourceInstallService.StreamToFileAsync(
            new MemoryStream(data), Path.Combine(_stage, "x.bin"), HashAlgorithmName.SHA256,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), 100, budget, CancellationToken.None, _ => { }));

        Assert.True(new FileInfo(Path.Combine(_stage, "x.bin")).Length <= 65536);
    }

    [Fact]
    public async Task Hash_mismatch_is_still_rejected()
    {
        var data = new byte[10];

        await Assert.ThrowsAsync<InvalidDataException>(() => ResourceInstallService.StreamToFileAsync(
            new MemoryStream(data), Path.Combine(_stage, "x.bin"), HashAlgorithmName.SHA256,
            new string('a', 64), data.Length, new InstallBudget(100), CancellationToken.None, _ => { }));
    }

    [Fact]
    public async Task Copy_counts_real_bytes_not_the_declared_length()
    {
        var budget = new InstallBudget(100);

        await Assert.ThrowsAsync<InvalidDataException>(() => ResourceInstallService.CopyLimitedAsync(
            new MemoryStream(new byte[150]), Stream.Null, long.MaxValue, budget, CancellationToken.None));
    }

    [Fact]
    public void Released_quota_can_be_used_again()
    {
        var budget = new InstallBudget(100);
        budget.Charge(80);
        Assert.Throws<InvalidDataException>(() => budget.EnsureFits(30));

        budget.Release(80);

        budget.EnsureFits(100);
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void File_count_is_capped()
    {
        var budget = new InstallBudget(100);
        for (var i = 0; i < InstallBudget.MaxFiles; i++) budget.AddFile();

        Assert.Throws<InvalidDataException>(budget.AddFile);
    }

    [Fact]
    public void Install_stops_when_the_host_disk_runs_low()
    {
        long free = 10L * 1024 * 1024 * 1024;
        var budget = new InstallBudget(InstallBudget.DefaultTotalLimit, () => free);
        budget.EnsureFits(1024);

        // 单文件声明大小会吃掉保留余量。
        free = InstallBudget.DefaultMinFreeBytes + 100;
        Assert.Throws<InvalidDataException>(() => budget.EnsureFits(1000));

        // 写入过程中磁盘被别的进程占满。
        free = InstallBudget.DefaultMinFreeBytes - 1;
        Assert.Throws<InvalidDataException>(() => budget.Charge(128L * 1024 * 1024));
    }

    [Fact]
    public void Default_total_is_four_gigabytes_and_single_file_one_gigabyte()
    {
        Assert.Equal(4L * 1024 * 1024 * 1024, InstallBudget.DefaultTotalLimit);
        Assert.Equal(1024L * 1024 * 1024, InstallBudget.SingleFileLimit);
        var budget = new InstallBudget();
        budget.EnsureFits(InstallBudget.SingleFileLimit);
        Assert.Throws<InvalidDataException>(() => budget.EnsureFits(InstallBudget.SingleFileLimit + 1));
    }
}
