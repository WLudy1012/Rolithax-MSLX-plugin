using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Rolithax.Schema.HostTasks;

namespace Rolithax.Schema.Worker;

internal sealed record Preset(string Id, string Title, string Description, string Program, string[] Arguments,
    string[] Users, int[] Instances, int TimeoutSeconds = 10, VersionProbePreset? VersionProbe = null);
internal sealed record VersionProbePreset(string Kind, string Target);
internal sealed record WorkerConfig(string Secret, string ExecutableDirectory, string WorkDirectory, Preset[] Tasks,
    string? SocketPath = null, uint? DaemonUid = null, string? DaemonSid = null);

internal sealed class Program
{
    private static readonly byte[] ProbeGeneration = RandomNumberGenerator.GetBytes(32);
    private const int MaxClients = 8;
    private static readonly SemaphoreSlim ActiveTask = new(1, 1);
    private static readonly SemaphoreSlim Connections = new(MaxClients, MaxClients);

    private static async Task<int> Main(string[] arguments)
    {
        if (arguments is not ["--config", var configPath])
        {
            Console.Error.WriteLine("用法：Rolithax.Schema.Worker --config <绝对配置路径>");
            return 2;
        }
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        using var termination = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            })
            : null;

        byte[] key = [];
        try
        {
            var config = WorkerProtocol.ReadConfig<WorkerConfig>(configPath);
            key = WorkerProtocol.ReadKey(config.Secret);
            Validate(config);
            if (OperatingSystem.IsWindows()) await ListenWindowsAsync(config, key, shutdown.Token);
            else if (OperatingSystem.IsLinux()) await ListenLinuxAsync(config, key, shutdown.Token);
            else throw new PlatformNotSupportedException("Worker 仅支持 Windows 和 Linux");
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or SocketException or JsonException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Rolithax Worker 无法启动（{ex.GetType().Name}）");
            return 1;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void Validate(WorkerConfig config)
    {
        if (!LocalDirectory(config.ExecutableDirectory) || !LocalDirectory(config.WorkDirectory) ||
            config.Tasks is null || config.Tasks.Length is 0 or > 32)
            throw new InvalidDataException("任务目录或数量无效");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var probes = new HashSet<(int Instance, VersionProbePreset Probe)>();
        foreach (var task in config.Tasks)
        {
            if (task is null || task.Id is null || !WorkerProtocol.IsTaskId(task.Id) || !seen.Add(task.Id) ||
                !DisplayText(task.Title, 80) || !DisplayText(task.Description, 240) ||
                task.Arguments is null || task.Arguments.Length > 24 ||
                task.Arguments.Any(value => value is null || value.Length > 256 || value.Any(char.IsControl)) ||
                task.Users is null || task.Users.Length is 0 or > 128 ||
                task.Users.Any(value => value is null || !WorkerProtocol.IsUserId(value)) ||
                task.Instances is null || task.Instances.Length is 0 or > 128 || task.Instances.Any(id => id <= 0) ||
                task.TimeoutSeconds is < 1 or > 30)
                throw new InvalidDataException("宿主任务配置无效");
            if (task.VersionProbe is { } probe &&
                (task.Instances.Length != 1 || !WorkerProtocol.IsProbeKind(probe.Kind) ||
                 !WorkerProtocol.IsProbeTarget(probe.Target) || !probes.Add((task.Instances[0], probe))))
                throw new InvalidDataException("版本探测必须对应一个实例和唯一的插件或模组");
            _ = ProgramPath(config, task);
        }
    }

    private static bool LocalDirectory([NotNullWhen(true)] string? path) => path is not null &&
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        Directory.Exists(path) && NoLinks(path);

    private static bool NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static bool DisplayText(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static string ProgramPath(WorkerConfig config, Preset task)
    {
        if (task.Program is null || task.Program.Length is 0 or > 80 ||
            !task.Program.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-') ||
            task.Program is "." or "..")
            throw new InvalidDataException("任务程序名无效");
        var path = Path.Combine(config.ExecutableDirectory, task.Program);
        if (!File.Exists(path) || !NoLinks(path) || OperatingSystem.IsWindows() &&
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("任务程序必须来自管理员配置的二进制目录");
        using var file = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        file.ReadExactly(magic);
        if (OperatingSystem.IsWindows() ? !magic[..2].SequenceEqual("MZ"u8) :
            !magic.SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }))
            throw new InvalidDataException("任务程序必须是本机可执行二进制文件");
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static async Task ListenWindowsAsync(WorkerConfig config, byte[] key, CancellationToken cancellation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new SecurityIdentifier(config.DaemonSid ?? throw new InvalidDataException("缺少 Daemon 账户 SID"));
        var self = identity.User ?? throw new InvalidOperationException();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (administrator || identity.IsSystem || self == expected) throw new InvalidDataException("Worker 需要独立低权限账户");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(expected, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (true)
        {
            await Connections.WaitAsync(cancellation);
            NamedPipeServerStream? stream = null;
            try
            {
                stream = NamedPipeServerStreamAcl.Create(WorkerProtocol.PipeName, PipeDirection.InOut,
                    MaxClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, security);
                await stream.WaitForConnectionAsync(cancellation);
                _ = ServeAsync(stream, config, key, cancellation);
            }
            catch
            {
                stream?.Dispose();
                Connections.Release();
                throw;
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ListenLinuxAsync(WorkerConfig config, byte[] key, CancellationToken cancellation)
    {
        var uid = LinuxEffectiveUid();
        var status = File.ReadAllLines("/proc/self/status");
        if (uid == 0 || config.DaemonUid is null || uid == config.DaemonUid ||
            !status.Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is ["NoNewPrivs:", "1"]) ||
            status.Any(line => (line.StartsWith("CapEff:", StringComparison.Ordinal) || line.StartsWith("CapPrm:", StringComparison.Ordinal) ||
                line.StartsWith("CapAmb:", StringComparison.Ordinal)) && line.Split(':')[1].Trim().Any(character => character != '0')))
            throw new InvalidDataException("Worker 需要独立低权限账户和 NoNewPrivileges");
        var path = config.SocketPath;
        if (path is null || !Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } directory ||
            !LocalDirectory(directory) || (File.GetUnixFileMode(directory) &
                (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidDataException("Worker socket 需要独立受保护目录");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        listener.Listen(MaxClients);
        try
        {
            while (true)
            {
                await Connections.WaitAsync(cancellation);
                Socket? connection = null;
                try
                {
                    connection = await listener.AcceptAsync(cancellation);
                    var length = (uint)Marshal.SizeOf<PeerCredentials>();
                    if (GetPeerCredentials(connection.SafeHandle, 1, 17, out var peer, ref length) != 0 || peer.Uid != config.DaemonUid)
                    {
                        connection.Dispose();
                        Connections.Release();
                        continue;
                    }
                    _ = ServeAsync(new NetworkStream(connection, ownsSocket: true), config, key, cancellation);
                }
                catch
                {
                    connection?.Dispose();
                    Connections.Release();
                    throw;
                }
            }
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    private static async Task ServeAsync(Stream connection, WorkerConfig config, byte[] key, CancellationToken cancellation)
    {
        try
        {
            await using var stream = connection;
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            handshake.CancelAfter(TimeSpan.FromSeconds(2));
            var nonce = RandomNumberGenerator.GetBytes(32);
            await WorkerProtocol.WriteFrameAsync(stream, nonce, handshake.Token);
            var request = await WorkerProtocol.ReadSignedAsync<WorkerRequest>(stream, key, nonce, false, handshake.Token);
            var response = await DispatchAsync(config, request, cancellation);
            using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            send.CancelAfter(TimeSpan.FromSeconds(2));
            await WorkerProtocol.WriteSignedAsync(stream, key, nonce, true, response, send.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            OperationCanceledException or CryptographicException or SocketException) { }
        finally { Connections.Release(); }
    }

    internal static async Task<WorkerReply> DispatchAsync(WorkerConfig config, WorkerRequest request, CancellationToken cancellation)
    {
        if (request is { UserId: null, InstanceId: 0, TaskId: null, Kind: null, Target: null })
        {
            if (request.Op == "ping") return new(200);
            if (request.Op == "probeCapabilities")
                return new(config.Tasks.Any(task => task.VersionProbe is not null) ? 200 : 404);
        }
        if (request.UserId is null || !WorkerProtocol.IsUserId(request.UserId) || request.InstanceId <= 0)
            return new(403);
        var allowed = config.Tasks.Where(task => task.Users.Contains(request.UserId, StringComparer.Ordinal) &&
            task.Instances.Contains(request.InstanceId));
        if (request.Op is "describeVersionProbe" or "runVersionProbe")
        {
            if (!WorkerProtocol.IsProbeKind(request.Kind) || !WorkerProtocol.IsProbeTarget(request.Target)) return new(403);
            var probe = new VersionProbePreset(request.Kind!, request.Target!);
            var preset = allowed.SingleOrDefault(task => task.VersionProbe == probe);
            if (preset is null) return new(403);
            var confirmation = ProbeConfirmation(preset.Id);
            if (request.Op == "describeVersionProbe" && request.TaskId is null)
                return new(200, CommandProbe: new(confirmation, preset.Title, preset.Description));
            if (request.Op != "runVersionProbe" || request.TaskId != confirmation) return new(403);
            return await RunPresetAsync(config, request, preset, cancellation);
        }
        if (request.Kind is not null || request.Target is not null) return new(403);
        var tasks = allowed.Where(task => task.VersionProbe is null);
        if (request.Op == "list" && request.TaskId is null)
            return new(200, tasks.Select(task => new TaskSummary(task.Id, task.Title, task.Description)).ToArray());
        if (request.Op != "run" || request.TaskId is null || !WorkerProtocol.IsTaskId(request.TaskId)) return new(403);
        var chosen = tasks.SingleOrDefault(task => task.Id == request.TaskId);
        return chosen is null ? new(403) : await RunPresetAsync(config, request, chosen, cancellation);
    }

    private static string ProbeConfirmation(string id) =>
        "probe-" + Convert.ToHexStringLower(HMACSHA256.HashData(ProbeGeneration, Encoding.UTF8.GetBytes(id)).AsSpan(0, 16));

    private static async Task<WorkerReply> RunPresetAsync(WorkerConfig config, WorkerRequest request, Preset preset,
        CancellationToken cancellation)
    {
        if (!ActiveTask.Wait(0, cancellation)) return new(429);
        try
        {
            var result = await RunAsync(config, preset, cancellation);
            Console.WriteLine(FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} user={request.UserId} instance={request.InstanceId} task={preset.Id} status={result.Status}"));
            if (preset.VersionProbe is null) return new(200, Result: result);
            var version = result is { Status: "completed", Truncated: false }
                ? WorkerProtocol.ParseProbeVersion(result.Output) : null;
            return new(200, Version: version);
        }
        finally { ActiveTask.Release(); }
    }

    private static async Task<TaskResult> RunAsync(WorkerConfig config, Preset task, CancellationToken cancellation)
    {
        try
        {
            if (!LocalDirectory(config.WorkDirectory)) throw new InvalidDataException();
            var start = new ProcessStartInfo(ProgramPath(config, task))
            {
                UseShellExecute = false,
                WorkingDirectory = config.WorkDirectory,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.Environment.Clear();
            if (OperatingSystem.IsWindows()) start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            else start.Environment["LANG"] = "C.UTF-8";
            foreach (var argument in task.Arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));
            var stdout = ReadOutputAsync(process.StandardOutput, deadline.Token, preserveControls: task.VersionProbe is not null);
            var stderr = ReadOutputAsync(process.StandardError, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr);
                if (task.VersionProbe is not null)
                    return new(process.ExitCode == 0 ? "completed" : "failed", process.ExitCode,
                        stdout.Result.Text, stdout.Result.Truncated);
                var output = stdout.Result.Text + (stderr.Result.Text.Length > 0 ? "\n" + stderr.Result.Text : "");
                var truncated = stdout.Result.Truncated || stderr.Result.Truncated || output.Length > WorkerProtocol.OutputLimit;
                return new(process.ExitCode == 0 ? "completed" : "failed", process.ExitCode,
                    output[..Math.Min(output.Length, WorkerProtocol.OutputLimit)], truncated);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return new("timedOut", null, "", false);
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                try { await Task.WhenAll(stdout, stderr); }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return new("failed", null, "无法启动预设任务", false);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadOutputAsync(TextReader stream, CancellationToken cancellation, bool preserveControls = false)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        var truncated = false;
        while (await stream.ReadAsync(buffer, cancellation) is var read && read > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (!preserveControls && char.IsControl(value) && value is not ('\n' or '\t')) continue;
                if (text.Length < WorkerProtocol.OutputLimit) text.Append(value);
                else truncated = true;
            }
        }
        return (text.ToString(), truncated);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint LinuxEffectiveUid();
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetPeerCredentials(SafeSocketHandle socket, int level, int name, out PeerCredentials value, ref uint length);
}
