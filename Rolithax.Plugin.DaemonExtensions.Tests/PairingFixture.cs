using System.Text.Json;
using MSLX.SDK.Models;
using Newtonsoft.Json.Linq;
using Rolithax.Plugin.DaemonExtensions.Pairing;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

internal sealed record PairedDevice(string ApiKey, string DeviceId, string UserId);

/// <summary>每个测试一套全新的 Daemon 替身、时钟和 <see cref="PairingService"/>。</summary>
internal sealed class PairingFixture : IDisposable
{
    private static int _ipCounter;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public PairingService Service { get; }

    public PairingFixture()
    {
        TestHost.Reset();
        TestHost.PluginConfig.WriteConfigKey("publicUrl", "https://daemon.example.com");
        Service = new PairingService { Clock = () => _now };
        Service.Initialize();
    }

    public DateTimeOffset Now => _now;

    public void Advance(TimeSpan span) => _now += span;

    public static string NextIp()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        return $"10.1.{n / 250}.{n % 250 + 1}";
    }

    public static JsonElement Json(object? data) => JsonSerializer.SerializeToElement(data);

    public string NewPayload(UserInfo creator, string scope = "full", int ttlDays = 30, params string[] resources)
    {
        var (code, message, data) = Service.CreateCode(
            new CreateCodeRequest { Scope = scope, DeviceTtlDays = ttlDays, Resources = resources.ToList() }, creator);
        Assert.True(code == 200, message);
        return Json(data).GetProperty("payload").GetString()!;
    }

    public (int Code, string Message, JsonElement Data) RedeemPayload(
        string payload, string? fingerprint = null, string? previousApiKey = null, string deviceName = "phone")
    {
        var (code, message, data) = Service.Redeem(new RedeemRequest
        {
            Payload = payload,
            DeviceName = deviceName,
            DeviceFingerprint = fingerprint,
            PreviousApiKey = previousApiKey,
        }, NextIp());
        return (code, message, data == null ? default : Json(data));
    }

    public PairedDevice Pair(UserInfo creator, string scope = "full", int ttlDays = 30,
        string? fingerprint = null, string? previousApiKey = null, params string[] resources)
    {
        var payload = NewPayload(creator, scope, ttlDays, resources);
        var (code, message, data) = RedeemPayload(payload, fingerprint ?? "fp-" + Guid.NewGuid().ToString("N"), previousApiKey);
        Assert.True(code == 200, message);
        return new PairedDevice(
            data.GetProperty("apiKey").GetString()!,
            data.GetProperty("deviceId").GetString()!,
            data.GetProperty("userId").GetString()!);
    }

    public UserInfo UserOf(PairedDevice device) => TestHost.Users.GetUserById(device.UserId)
        ?? throw new InvalidOperationException("paired user is gone");

    public static List<DeviceRecord> Records() =>
        (TestHost.PluginConfig.ReadConfigKey("devices") as JArray)?.ToObject<List<DeviceRecord>>() ?? new List<DeviceRecord>();

    public static DeviceRecord Record(PairedDevice device) => Records().Single(d => d.DeviceId == device.DeviceId);

    public void Dispose() => Service.Shutdown();
}
