using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using Rolithax.Schema.HostTasks;

namespace Rolithax.Plugin.DaemonExtensions.Schema;

internal static class WorkerClient
{
    private sealed record LinkConfig(string Secret, string? SocketPath = null);

    public static async Task<WorkerReply> RequestAsync(WorkerRequest request, CancellationToken cancellation)
    {
        var configFile = Environment.GetEnvironmentVariable("ROLITHAX_SCHEMA_WORKER_LINK");
        if (string.IsNullOrEmpty(configFile)) throw new InvalidOperationException("宿主机 Worker 未配置");
        var config = WorkerProtocol.ReadConfig<LinkConfig>(configFile);
        var key = WorkerProtocol.ReadKey(config.Secret);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        call.CancelAfter(TimeSpan.FromSeconds(request.Op is "run" or "runVersionProbe" ? 34 : 2));
        try
        {
            await using var connection = await ConnectAsync(config.SocketPath, call.Token);
            var nonce = await WorkerProtocol.ReadFrameAsync(connection, call.Token);
            if (nonce.Length != 32) throw new InvalidDataException("Worker 握手无效");
            await WorkerProtocol.WriteSignedAsync(connection, key, nonce, false, request, call.Token);
            return await WorkerProtocol.ReadSignedAsync<WorkerReply>(connection, key, nonce, true, call.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Worker 请求超时");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<Stream> ConnectAsync(string? path, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", WorkerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(cancellation); return pipe; }
            catch { await pipe.DisposeAsync(); throw; }
        }
        if (OperatingSystem.IsLinux() && path is not null && Path.IsPathFullyQualified(path))
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
        throw new InvalidOperationException("Worker 本机通道未配置");
    }
}
