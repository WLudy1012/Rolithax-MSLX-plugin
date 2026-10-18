using System.Text.Json.Serialization;

namespace Rolithax.Plugin.DaemonExtensions.Pairing;

/// <summary>扫码配对载荷（二维码内容，前缀 mslxp1: + Base64Url(JSON)）。</summary>
public sealed class PairingPayload
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>Daemon 对外地址（生成配对码时记录）。</summary>
    [JsonPropertyName("u")] public string Url { get; set; } = string.Empty;

    /// <summary>一次性配对码（Crockford Base32，8 位）。</summary>
    [JsonPropertyName("c")] public string Code { get; set; } = string.Empty;

    /// <summary>配对码过期时间（Unix 秒）。</summary>
    [JsonPropertyName("e")] public long ExpiresAt { get; set; }

    /// <summary>HMAC-SHA256(installSecret, "url|code|exp") 的 Base64Url 签名。</summary>
    [JsonPropertyName("s")] public string Signature { get; set; } = string.Empty;
}

/// <summary>生成配对码请求。</summary>
public sealed class CreateCodeRequest
{
    public string Scope { get; set; } = "full";
    public List<string> Resources { get; set; } = new();
    public int? DeviceTtlDays { get; set; }
    public string? DeviceNameHint { get; set; }
}

public sealed class DaemonAddressRequest
{
    public string? PublicUrl { get; set; }
}

/// <summary>兑换配对码请求。</summary>
public sealed class RedeemRequest
{
    public string? Payload { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceFingerprint { get; set; }

    /// <summary>
    /// 原设备当前仍有效的 API Key。仅当它能证明属于同指纹的已配对设备时，才会替换旧设备记录；
    /// 不提供或不匹配时，新设备与现有设备并存，绝不依据指纹单独撤销他人设备。
    /// </summary>
    public string? PreviousApiKey { get; set; }
}

/// <summary>一台已配对设备的持久化记录（不含明文 API Key）。</summary>
public sealed class DeviceRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>签发该设备配对码的用户 ID；签发者被删除、撤销或到期时，本设备授权随之失效。</summary>
    public string IssuedBy { get; set; } = string.Empty;

    public string Role { get; set; } = "user";
    public List<string> Resources { get; set; } = new();

    /// <summary>API Key 前缀（仅用于管理页展示识别，绝不存完整 Key）。</summary>
    public string ApiKeyPrefix { get; set; } = string.Empty;

    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public bool Revoked { get; set; }

    /// <summary>撤销/过期原因：revoked / expired / replaced / issuer-removed / issuer-revoked / user-removed / plugin-unloaded。</summary>
    public string? RevokeReason { get; set; }

    public string? CreatedIp { get; set; }
}
