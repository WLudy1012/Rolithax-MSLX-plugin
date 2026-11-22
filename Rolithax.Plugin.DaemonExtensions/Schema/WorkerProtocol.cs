using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Rolithax.Schema.HostTasks;

internal sealed record WorkerRequest(string Op, string? UserId = null, int InstanceId = 0, string? TaskId = null,
    string? Kind = null, string? Target = null);
internal sealed record WorkerReply(int Code, TaskSummary[]? Tasks = null, TaskResult? Result = null,
    TaskSummary? CommandProbe = null, string? Version = null);
internal sealed record TaskSummary(string Id, string Title, string Description);
internal sealed record TaskResult(string Status, int? ExitCode, string Output, bool Truncated);

internal static partial class WorkerProtocol
{
    public const string PipeName = "rolithax-schema-worker-v1";
    public const int OutputLimit = 4096;
    private const int FrameLimit = 72 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex(@"^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskId();
    public static bool IsTaskId(string value) => TaskId().IsMatch(value);
    public static bool IsProbeKind(string? kind) => kind is "mod" or "minecraftPlugin" or "mcdrPlugin";
    public static bool IsProbeTarget(string? target) => target is not null && ProbeTarget().IsMatch(target);
    public static bool IsProbeConfirmation(string value) => ProbeConfirmation().IsMatch(value);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeTarget();
    [GeneratedRegex(@"\Aprobe-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeConfirmation();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.+\-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeVersion();

    public static string? ParseProbeVersion(string stdout)
    {
        if (stdout.EndsWith("\r\n", StringComparison.Ordinal)) stdout = stdout[..^2];
        else if (stdout.EndsWith('\n')) stdout = stdout[..^1];
        return ProbeVersion().IsMatch(stdout) ? stdout : null;
    }

    public static bool IsUserId(string value) => value.Length is > 0 and <= 80 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static byte[] ReadKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("缺少 Worker 密钥");
        try
        {
            var result = Convert.FromBase64String(value);
            return result.Length == 32 ? result : throw new InvalidDataException("Worker 密钥必须为 32 字节");
        }
        catch (FormatException ex) { throw new InvalidDataException("Worker 密钥格式无效", ex); }
    }

    public static T ReadConfig<T>(string file)
    {
        if (!Path.IsPathFullyQualified(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Worker 配置必须是本机文件");
        using var stream = File.OpenRead(file);
        var bytes = new byte[128 * 1024 + 1];
        var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length == bytes.Length) throw new InvalidDataException("Worker 配置过大");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), Json) ?? throw new InvalidDataException("Worker 配置无效");
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is < 1 or > FrameLimit) throw new InvalidDataException("Worker 消息大小无效");
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, cancellation);
        return data;
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] data, CancellationToken cancellation)
    {
        if (data.Length is < 1 or > FrameLimit) throw new InvalidDataException("Worker 消息大小无效");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(data, cancellation);
        await stream.FlushAsync(cancellation);
    }

    public static async Task WriteSignedAsync<T>(Stream stream, byte[] key, byte[] nonce, bool reply,
        T message, CancellationToken cancellation)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var envelope = new byte[32 + payload.Length];
        Signature(key, nonce, reply, payload).CopyTo(envelope, 0);
        payload.CopyTo(envelope, 32);
        await WriteFrameAsync(stream, envelope, cancellation);
    }

    public static async Task<T> ReadSignedAsync<T>(Stream stream, byte[] key, byte[] nonce, bool reply,
        CancellationToken cancellation)
    {
        var envelope = await ReadFrameAsync(stream, cancellation);
        if (envelope.Length <= 32 ||
            !CryptographicOperations.FixedTimeEquals(envelope.AsSpan(0, 32), Signature(key, nonce, reply, envelope.AsSpan(32))))
            throw new CryptographicException("Worker 身份验证失败");
        return JsonSerializer.Deserialize<T>(envelope.AsSpan(32), Json) ??
            throw new InvalidDataException("Worker 消息无效");
    }

    private static byte[] Signature(byte[] key, byte[] nonce, bool reply, ReadOnlySpan<byte> payload)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData("RolithaxSchemaHostTasks/1"u8);
        hmac.AppendData([reply ? (byte)2 : (byte)1]);
        hmac.AppendData(nonce);
        hmac.AppendData(payload);
        return hmac.GetHashAndReset();
    }
}
