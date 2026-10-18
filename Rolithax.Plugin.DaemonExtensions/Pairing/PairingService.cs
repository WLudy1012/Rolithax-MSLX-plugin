using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MSLX.SDK.Models;
using Newtonsoft.Json.Linq;

namespace Rolithax.Plugin.DaemonExtensions.Pairing;

/// <summary>
/// 扫码配对核心服务：
/// - 配对码：一次性、TTL 2 分钟、仅内存保存，HMAC-SHA256 签名防篡改；
/// - 兑换：按 IP 失败计数 + 临时锁定，创建用户成功后消费配对码；
/// - 设备：兑换后创建**独立受限用户**（随设备记录可撤销、可过期），API Key 只存前缀；
///   设备记录的读-改-写全部在同一把锁内完成，先落记录再建用户，避免并发覆盖与孤儿 Key；
/// - 授权期限：设备签发出的新设备不得晚于签发设备到期，签发者被删除/撤销/到期时级联失效；
///   期限在插件的每次请求与定时清理中都会校验，插件卸载时撤销全部配对设备；
/// - 审计：所有关键操作写 Daemon 日志（密钥绝不落日志）。
/// </summary>
public sealed class PairingService
{
    private const string ConfigId = "mslx-pair";
    public const string PayloadPrefix = "mslxp1:";

    private const int CodeTtlSeconds = 120;
    private const int MaxCodePerMinutePerUser = 10;
    private const int MaxRedeemAttemptsPerMinutePerIp = 10;
    private const int MaxRedeemFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private const long NewDeviceUserGraceSeconds = 300;
    private static readonly Regex PairedUsernamePattern = new("^pair_d[0-9a-z]{10}$", RegexOptions.Compiled);
    private const string PairedNamePrefix = "配对设备 ";
    private const int DefaultDeviceTtlDays = 30;
    private const int MaxDeviceTtlDays = 365;
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class PairingCodeEntry
    {
        public string Url = string.Empty;
        public long ExpiresAt;
        public string Scope = "full";
        public List<string> Resources = new();
        public int DeviceTtlDays = DefaultDeviceTtlDays;
        public string CreatedBy = string.Empty;
    }

    private sealed class IpState
    {
        public int Failures;
        public DateTime FailureWindowStart = DateTime.UtcNow;
        public DateTime? LockedUntil;
        public readonly Queue<DateTime> Attempts = new();
    }

    private readonly ConcurrentDictionary<string, PairingCodeEntry> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _redeemingCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IpState> _ipStates = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _codeIssued = new();
    // 设备记录与 publicUrl 共用同一份插件配置；静态锁保证热重载期间新旧实例也不会交错写入。
    private static readonly object StoreLock = new();

    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private string _installSecret = string.Empty;
    private Timer? _sweeper;

    private static MSLX.SDK.Interfaces.IPluginConfigBridge Config =>
        global::MSLX.SDK.MSLX.Config.GetPluginConfig(ConfigId);

    private static MSLX.SDK.Interfaces.IUserConfigBridge Users => global::MSLX.SDK.MSLX.Config.Users;

    private static MSLX.SDK.Interfaces.IMSLXLogger Log => global::MSLX.SDK.MSLX.Logger;

    /// <summary>插件加载后调用：准备安装密钥并启动过期清理定时器。</summary>
    public void Initialize()
    {
        lock (StoreLock)
        {
            var existing = Config.ReadConfigKey("installSecret")?.ToString();
            if (string.IsNullOrWhiteSpace(existing))
            {
                _installSecret = Base64Url(RandomNumberGenerator.GetBytes(32));
                Config.WriteConfigKey("installSecret", _installSecret);
                Log.Info($"[Pairing] 已生成本插件安装密钥（用于配对载荷签名）");
            }
            else
            {
                _installSecret = existing;
            }
        }
        // 加载即清理一次：插件停用期间到期、或签发者已被删除的授权不等下一个定时周期。
        SafeSweep();
        _sweeper = new Timer(_ => SafeSweep(), null, SweepInterval, SweepInterval);
        Log.Info("[Pairing] 扫码配对插件已就绪");
    }

    /// <summary>
    /// 插件卸载（停用、移除、覆盖更新）：停止清理定时器，并撤销全部配对设备。
    /// Daemon 的用户鉴权不识别设备到期时间，卸载后无人再按期清理，因此必须在此收回凭据。
    /// </summary>
    public void Shutdown()
    {
        _sweeper?.Dispose();
        _sweeper = null;
        _codes.Clear();
        try
        {
            lock (StoreLock)
            {
                var devices = ReadDevices();
                var changed = false;
                foreach (var d in devices.Where(d => !d.Revoked))
                {
                    if (!TryDeleteUser(d.UserId))
                    {
                        Log.Error($"[Pairing] 卸载时无法撤销设备 {d.DeviceId}，请在用户管理中手动删除 {d.Username}");
                        continue;
                    }
                    d.Revoked = true;
                    d.RevokeReason = "plugin-unloaded";
                    changed = true;
                }
                if (changed)
                {
                    WriteDevices(devices);
                    Log.Info("[Pairing] 插件卸载，已撤销全部配对设备");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Pairing] 卸载时撤销配对设备异常：{ex.Message}", ex);
        }
    }

    public (int Code, string Message, object? Data) GetConfig(bool canEditAddress)
    {
        return (200, "ok", new
        {
            publicUrl = ReadPublicUrl(),
            canEditAddress,
            canManageDevices = canEditAddress,
        });
    }

    public (int Code, string Message, object? Data) SetPublicUrl(string? publicUrl)
    {
        var normalized = NormalizeUrl(publicUrl, string.Empty);
        if (!IsValidPublicUrl(normalized))
        {
            return (400, "Daemon 地址必须是有效的 http:// 或 https:// 地址", null);
        }

        lock (StoreLock)
        {
            Config.WriteConfigKey("publicUrl", normalized);
        }
        Log.Info($"[Pairing] Daemon 地址已更新为 {normalized}");
        return (200, "Daemon 地址已保存", new { publicUrl = normalized });
    }

    // ---------------- 配对码 ----------------

    /// <summary>生成一次性配对码并返回二维码载荷。scope=full 创建 admin 级配对用户；limited 创建受限 user。</summary>
    public (int Code, string Message, object? Data) CreateCode(
        CreateCodeRequest request,
        UserInfo creator)
    {
        var createdBy = creator.Id;
        if (!CheckAndTrackCodeIssue(createdBy))
        {
            return (429, "生成过于频繁，请稍后再试", null);
        }

        var url = ReadPublicUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            return (400, "尚未配置 Daemon 地址，请先由管理员保存", null);
        }

        var now = Now();
        long? issuerDeviceExpiresAt = null;
        lock (StoreLock)
        {
            var issuerDevice = ReadDevices().FirstOrDefault(d => d.UserId == creator.Id);
            if (issuerDevice == null && IsPairedDeviceAccount(creator))
            {
                return (403, "配对设备记录缺失，无法确认授权期限，请重新配对", null);
            }
            if (issuerDevice != null)
            {
                if (issuerDevice.Revoked || issuerDevice.ExpiresAt <= now)
                {
                    return (403, "当前设备授权已失效", null);
                }
                issuerDeviceExpiresAt = issuerDevice.ExpiresAt;
            }
        }

        var isAdmin = string.Equals(creator.Role, "admin", StringComparison.OrdinalIgnoreCase);
        var scope = isAdmin && string.Equals(request.Scope, "limited", StringComparison.OrdinalIgnoreCase)
            ? "limited"
            : (isAdmin ? "full" : "limited");
        var resources = isAdmin && scope == "limited"
            ? NormalizeResources(request.Resources)
            : (isAdmin ? new List<string>() : NormalizeResources(creator.Resources));
        var ttlDays = Math.Clamp(request.DeviceTtlDays ?? DefaultDeviceTtlDays, 1, MaxDeviceTtlDays);
        if (issuerDeviceExpiresAt.HasValue)
        {
            // 配对设备签发的新设备不得活得比自己久；精确截止时间在兑换时再按签发设备当时的到期时间取小。
            var remainingDays = (int)Math.Clamp((issuerDeviceExpiresAt.Value - now + 86399) / 86400, 1, MaxDeviceTtlDays);
            ttlDays = Math.Min(ttlDays, remainingDays);
        }
        var expiresAt = now + CodeTtlSeconds;
        var code = GenerateCode();

        _codes[code] = new PairingCodeEntry
        {
            Url = url,
            ExpiresAt = expiresAt,
            Scope = scope,
            Resources = resources,
            DeviceTtlDays = ttlDays,
            CreatedBy = createdBy,
        };

        var payload = new PairingPayload
        {
            Url = url,
            Code = code,
            ExpiresAt = expiresAt,
        };
        payload.Signature = Sign(payload.Url, payload.Code, payload.ExpiresAt);
        var text = PayloadPrefix + Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions)));

        Log.Info($"[Pairing] 已生成配对码（scope={scope}, 有效期 {CodeTtlSeconds}s, 操作者 {createdBy}）");
        return (200, "配对码已生成", new
        {
            code,
            expiresAt = Iso(expiresAt),
            expiresInSeconds = CodeTtlSeconds,
            scope,
            deviceTtlDays = ttlDays,
            payload = text,
            url,
        });
    }

    // ---------------- 兑换 ----------------

    /// <summary>兑换配对码：校验签名/时效/一次性，成功后创建受限用户并返回 API Key。</summary>
    public (int Code, string Message, object? Data) Redeem(RedeemRequest request, string clientIp)
    {
        if (!CheckRedeemRate(clientIp, out var retrySeconds))
        {
            return (429, $"尝试过于频繁或已临时锁定，请 {Math.Max(1, retrySeconds / 60)} 分钟后再试", null);
        }

        var deviceName = Sanitize(request.DeviceName, 64, "未命名设备");
        var fingerprint = Sanitize(request.DeviceFingerprint, 128, string.Empty);

        // 1) 解析载荷
        if (!TryDecodePayload(request.Payload, out var payload, out var parseError))
        {
            RegisterFailure(clientIp);
            Log.Warn($"[Pairing] 兑换失败（{clientIp}）：{parseError}");
            return (400, parseError, null);
        }

        // 2) 验签（常量时间比较）
        var expected = Sign(payload.Url, payload.Code, payload.ExpiresAt);
        if (!FixedTimeEquals(expected, payload.Signature))
        {
            RegisterFailure(clientIp);
            Log.Warn($"[Pairing] 兑换失败（{clientIp}）：载荷签名不合法");
            return (400, "配对载荷签名不合法", null);
        }

        // 3) 时效（以服务端时间为准）
        var now = Now();
        if (payload.ExpiresAt < now - 5)
        {
            RegisterFailure(clientIp);
            return (410, "配对码已过期，请在服务端重新生成", null);
        }

        // 4) 校验配对码；待创建用户成功后再消费
        if (!_codes.TryGetValue(payload.Code, out var entry))
        {
            RegisterFailure(clientIp);
            Log.Warn($"[Pairing] 兑换失败（{clientIp}）：配对码不存在或已被使用");
            return (410, "配对码不存在或已被使用", null);
        }
        if (entry.ExpiresAt < now - 5)
        {
            _codes.TryRemove(payload.Code, out _);
            RegisterFailure(clientIp);
            return (410, "配对码已过期，请在服务端重新生成", null);
        }
        if (!string.Equals(entry.Url, payload.Url, StringComparison.OrdinalIgnoreCase))
        {
            RegisterFailure(clientIp);
            Log.Warn($"[Pairing] 兑换失败（{clientIp}）：地址与签发记录不一致");
            return (400, "配对载荷与签发记录不一致", null);
        }

        if (!_redeemingCodes.TryAdd(payload.Code, 0))
        {
            return (409, "配对码正在处理中，请稍后重试", null);
        }

        try
        {
            if (!_codes.TryGetValue(payload.Code, out var currentEntry) || !ReferenceEquals(entry, currentEntry))
            {
                RegisterFailure(clientIp);
                return (410, "配对码不存在或已被使用", null);
            }

            // 签发者在出码之后可能已被删除、降权，或其自身的设备授权已被撤销/到期：此时配对码一并作废。
            var issuer = Users.GetUserById(entry.CreatedBy);
            if (issuer == null)
            {
                _codes.TryRemove(payload.Code, out _);
                Log.Warn($"[Pairing] 兑换被拒（{clientIp}）：配对码签发者已不存在");
                return (410, "配对码的签发者已被删除，配对码已失效", null);
            }
            var issuerIsAdmin = IsAdmin(issuer);
            if (entry.Scope == "full" && !issuerIsAdmin)
            {
                _codes.TryRemove(payload.Code, out _);
                Log.Warn($"[Pairing] 兑换被拒（{clientIp}）：配对码签发者已不再是管理员");
                return (410, "配对码签发者的权限已变更，配对码已失效", null);
            }

            var deviceId = "d" + RandomString(10);
            var username = $"pair_{deviceId}";
            var apiKey = Base64Url(RandomNumberGenerator.GetBytes(24));
            var role = entry.Scope == "full" ? "admin" : "user";
            var resources = issuerIsAdmin
                ? entry.Resources
                : entry.Resources.Intersect(NormalizeResources(issuer.Resources), StringComparer.OrdinalIgnoreCase).ToList();

            var user = new UserInfo
            {
                Username = username,
                Name = PairedNamePrefix + deviceName,
                PasswordHash = Base64Url(RandomNumberGenerator.GetBytes(32)),
                Role = role,
                ApiKey = apiKey,
                Resources = resources,
                Avatar = string.Empty,
            };
            var record = new DeviceRecord
            {
                DeviceId = deviceId,
                Name = deviceName,
                Fingerprint = fingerprint,
                UserId = user.Id,
                Username = username,
                IssuedBy = issuer.Id,
                Role = role,
                Resources = resources,
                ApiKeyPrefix = apiKey.Length > 8 ? apiKey[..8] : apiKey,
                CreatedAt = now,
                CreatedIp = clientIp,
            };

            // 替换旧设备必须出示旧设备仍有效的 API Key，不能只凭客户端自报的指纹。
            var proofUser = string.IsNullOrWhiteSpace(request.PreviousApiKey)
                ? null
                : Users.GetUserByApiKey(request.PreviousApiKey.Trim());

            lock (StoreLock)
            {
                var devices = ReadDevices();
                var issuerDevice = devices.FirstOrDefault(d => d.UserId == issuer.Id);
                if (issuerDevice == null ? IsPairedDeviceAccount(issuer) : (issuerDevice.Revoked || issuerDevice.ExpiresAt <= now))
                {
                    _codes.TryRemove(payload.Code, out _);
                    Log.Warn($"[Pairing] 兑换被拒（{clientIp}）：签发设备的授权已失效");
                    return (410, "签发该配对码的设备授权已失效，配对码已失效", null);
                }

                // 新设备绝不晚于签发设备到期。
                record.ExpiresAt = Math.Min(now + (long)entry.DeviceTtlDays * 86400, issuerDevice?.ExpiresAt ?? long.MaxValue);

                // 先落设备记录再建用户：任何时刻都不会出现「Key 有效但管理列表里没有」的设备。
                devices.Add(record);
                WriteDevices(devices);

                var userCreated = false;
                try
                {
                    if (!Users.CreateUser(user))
                    {
                        Log.Error($"[Pairing] 创建配对用户失败：{username}");
                        RollbackNewDevice(record, user, userCreated);
                        return (500, "服务端创建配对用户失败", null);
                    }
                    userCreated = true;

                    if (!_codes.TryRemove(payload.Code, out _))
                    {
                        RollbackNewDevice(record, user, userCreated);
                        return (410, "配对码已过期或已被使用，请重新生成", null);
                    }

                    var replaced = devices.FirstOrDefault(d =>
                        proofUser != null && fingerprint.Length > 0 &&
                        !d.Revoked && d.ExpiresAt > now &&
                        d.UserId == proofUser.Id && d.UserId != issuer.Id &&
                        string.Equals(d.Fingerprint, fingerprint, StringComparison.Ordinal));
                    if (replaced != null && TryDeleteUser(replaced.UserId))
                    {
                        replaced.Revoked = true;
                        replaced.RevokeReason = "replaced";
                        foreach (var child in devices.Where(d => d.IssuedBy == replaced.UserId && d.DeviceId != record.DeviceId))
                        {
                            child.IssuedBy = record.UserId;
                        }
                        WriteDevices(devices);
                        Log.Info($"[Pairing] 同一设备重新配对，旧记录已撤销：{replaced.DeviceId}");
                    }
                }
                catch
                {
                    RollbackNewDevice(record, user, userCreated);
                    throw;
                }
            }

            ClearFailures(clientIp);
            Log.Info($"[Pairing] 设备配对成功：{deviceName}（{deviceId}, role={role}, 过期 {Iso(record.ExpiresAt)}）");

            return (200, "配对成功", new
            {
                daemonUrl = entry.Url,
                apiKey,
                deviceId,
                userId = user.Id,
                role,
                resources,
                expiresAt = Iso(record.ExpiresAt),
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[Pairing] 兑换处理异常：{ex.Message}", ex);
            return (500, "服务端处理异常", null);
        }
        finally
        {
            _redeemingCodes.TryRemove(payload.Code, out _);
        }
    }

    /// <summary>回滚尚未交付给客户端的新设备：删除用户并移除记录（调用方已持有 StoreLock）。</summary>
    private void RollbackNewDevice(DeviceRecord record, UserInfo user, bool userCreated)
    {
        try
        {
            if (userCreated && !TryDeleteUser(user.Id))
            {
                Log.Error($"[Pairing] 清理未完成配对用户失败：{user.Id}");
                return; // 用户仍在，保留记录以便管理员撤销/定时清理，不能留下无记录的有效 Key
            }
            var devices = ReadDevices();
            devices.RemoveAll(d => d.DeviceId == record.DeviceId);
            WriteDevices(devices);
        }
        catch (Exception ex)
        {
            Log.Error($"[Pairing] 回滚未完成配对设备失败：{ex.Message}", ex);
        }
    }

    // ---------------- 设备管理（管理员） ----------------

    public (int Code, string Message, object? Data) ListDevices()
    {
        var now = Now();
        List<DeviceRecord> snapshot;
        lock (StoreLock)
        {
            snapshot = ReadDevices();
            if (ReconcileLocked(snapshot, now)) WriteDevices(snapshot);
        }
        var list = snapshot
            .Select(d => new
            {
                d.DeviceId,
                d.Name,
                fingerprintPrefix = d.Fingerprint.Length > 12 ? d.Fingerprint[..12] + "…" : d.Fingerprint,
                d.Role,
                d.Resources,
                apiKeyPrefix = d.ApiKeyPrefix + "…",
                createdAt = Iso(d.CreatedAt),
                expiresAt = Iso(d.ExpiresAt),
                status = d.Revoked ? (d.RevokeReason ?? "revoked") : (d.ExpiresAt <= now ? "expired" : "active"),
            })
            .OrderByDescending(d => d.createdAt)
            .ToList();
        return (200, "ok", list);
    }

    /// <summary>撤销设备：删除对应配对用户（其 API Key 即时失效）；该设备签发的下级设备一并撤销。</summary>
    public (int Code, string Message, object? Data) Revoke(string deviceId)
    {
        lock (StoreLock)
        {
            var devices = ReadDevices();
            var target = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (target == null)
            {
                return (404, "未找到该设备记录", null);
            }
            if (!target.Revoked)
            {
                // 确认 Key 已经失效才改记录状态，避免列表显示已撤销而凭据仍然有效。
                if (!TryDeleteUser(target.UserId))
                {
                    Log.Error($"[Pairing] 撤销配对设备失败：{target.DeviceId}");
                    return (500, "撤销失败：无法删除设备对应的用户，请重试", null);
                }
                target.Revoked = true;
                target.RevokeReason = "revoked";
                ReconcileLocked(devices, Now());
                WriteDevices(devices);
                Log.Info($"[Pairing] 已撤销配对设备：{target.Name}（{target.DeviceId}）");
            }
            return (200, "已撤销", null);
        }
    }

    /// <summary>
    /// 请求期授权校验：凭据属于配对设备时，核对其是否撤销、到期，或签发链是否已失效；失效则当场收回。
    /// 返回 false 表示该凭据不应再被接受。非配对设备（普通用户）恒为 true。
    /// </summary>
    public bool IsCredentialActive(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return true;
        lock (StoreLock)
        {
            var devices = ReadDevices();
            var mine = devices.FirstOrDefault(d => d.UserId == userId);
            if (mine == null)
            {
                // 设备记录丢失（旧版本并发覆盖遗留）的配对账号：Key 仍有效却无法管理，按失效处理并删除。
                var account = userId == "system-admin" ? null : Users.GetUserById(userId);
                if (account != null && IsPairedDeviceAccount(account) && TryDeleteUser(account.Id))
                {
                    Log.Warn($"[Pairing] 已删除无设备记录的配对账号 {account.Username}");
                    return false;
                }
                return true;
            }
            if (ReconcileLocked(devices, Now())) WriteDevices(devices);
            return !mine.Revoked;
        }
    }

    // ---------------- 内部 ----------------

    private long Now() => Clock().ToUnixTimeSeconds();

    private static bool IsAdmin(UserInfo user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>用户名与显示名都符合本插件配对账号的命名（避免误伤恰好同名前缀的普通用户）。</summary>
    private static bool IsPairedDeviceAccount(UserInfo user) =>
        PairedUsernamePattern.IsMatch(user.Username ?? string.Empty) &&
        (user.Name ?? string.Empty).StartsWith(PairedNamePrefix, StringComparison.Ordinal);

    /// <summary>删除用户并确认其已不存在；仅当凭据确实失效时返回 true。</summary>
    private static bool TryDeleteUser(string userId)
    {
        try
        {
            Users.DeleteUser(userId);
            return Users.GetUserById(userId) == null;
        }
        catch (Exception ex)
        {
            Log.Error($"[Pairing] 删除用户失败：{ex.Message}", ex);
            return false;
        }
    }

    private static string? GetInvalidReason(List<DeviceRecord> devices, DeviceRecord d, long now)
    {
        if (d.ExpiresAt <= now) return "expired";
        if (!string.IsNullOrEmpty(d.IssuedBy))
        {
            var parent = devices.FirstOrDefault(p => p.UserId == d.IssuedBy);
            if (parent != null && (parent.Revoked || parent.ExpiresAt <= now)) return "issuer-revoked";
            if (Users.GetUserById(d.IssuedBy) == null) return "issuer-removed";
        }
        if (now - d.CreatedAt > NewDeviceUserGraceSeconds && Users.GetUserById(d.UserId) == null) return "user-removed";
        return null;
    }

    /// <summary>
    /// 撤销所有已失效的设备（到期、签发者被删除/撤销、用户被外部删除），并沿签发链级联到下级设备。
    /// 调用方须持有 StoreLock；返回是否修改了记录。
    /// </summary>
    private bool ReconcileLocked(List<DeviceRecord> devices, long now)
    {
        var changed = false;
        for (var pass = 0; pass <= devices.Count; pass++)
        {
            var passChanged = false;
            foreach (var d in devices.Where(d => !d.Revoked))
            {
                var reason = GetInvalidReason(devices, d, now);
                if (reason == null) continue;
                if (!TryDeleteUser(d.UserId))
                {
                    Log.Error($"[Pairing] 无法收回失效设备的凭据：{d.DeviceId}（{reason}），将在下次清理时重试");
                    continue;
                }
                d.Revoked = true;
                d.RevokeReason = reason;
                passChanged = true;
                Log.Info($"[Pairing] 配对设备授权失效并已移除：{d.DeviceId}（{reason}）");
            }
            if (!passChanged) break;
            changed = true;
        }
        return changed;
    }

    /// <summary>立即清理一次：失效设备、已作废的配对码、无记录的孤儿配对账号、过期的 IP 状态。</summary>
    internal void SweepNow() => SafeSweep();

    private void SafeSweep()
    {
        try
        {
            var now = Now();
            foreach (var kv in _codes.ToList())
            {
                if (kv.Value.ExpiresAt < now - 60 || Users.GetUserById(kv.Value.CreatedBy) == null)
                {
                    _codes.TryRemove(kv.Key, out _);
                }
            }

            lock (StoreLock)
            {
                var devices = ReadDevices();
                if (ReconcileLocked(devices, now)) WriteDevices(devices);

                var known = devices.Select(d => d.UserId).ToHashSet();
                foreach (var account in Users.GetAllUsers().Where(u => u != null && !known.Contains(u.Id) && IsPairedDeviceAccount(u)))
                {
                    if (TryDeleteUser(account!.Id)) Log.Warn($"[Pairing] 已删除无设备记录的配对账号 {account.Username}");
                }
            }

            var stale = DateTime.UtcNow - FailureWindow - LockDuration;
            foreach (var key in _ipStates.Where(kv => kv.Value.LockedUntil == null && kv.Value.FailureWindowStart < stale)
                         .Select(kv => kv.Key).ToList())
            {
                _ipStates.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Pairing] 过期清理异常：{ex.Message}", ex);
        }
    }

    private bool CheckAndTrackCodeIssue(string createdBy)
    {
        var now = DateTime.UtcNow;
        var queue = _codeIssued.GetOrAdd(createdBy, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && (now - queue.Peek()) > TimeSpan.FromMinutes(1)) queue.Dequeue();
            if (queue.Count >= MaxCodePerMinutePerUser) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private bool CheckRedeemRate(string clientIp, out int retrySeconds)
    {
        retrySeconds = 0;
        var state = _ipStates.GetOrAdd(clientIp, _ => new IpState());
        lock (state)
        {
            if (state.LockedUntil.HasValue)
            {
                if (state.LockedUntil.Value > DateTime.UtcNow)
                {
                    retrySeconds = (int)(state.LockedUntil.Value - DateTime.UtcNow).TotalSeconds;
                    return false;
                }
                state.LockedUntil = null;
                state.Failures = 0;
            }
            var now = DateTime.UtcNow;
            while (state.Attempts.Count > 0 && (now - state.Attempts.Peek()) > TimeSpan.FromMinutes(1)) state.Attempts.Dequeue();
            if (state.Attempts.Count >= MaxRedeemAttemptsPerMinutePerIp)
            {
                retrySeconds = 60;
                return false;
            }
            state.Attempts.Enqueue(now);
            return true;
        }
    }

    private void RegisterFailure(string clientIp)
    {
        var state = _ipStates.GetOrAdd(clientIp, _ => new IpState());
        lock (state)
        {
            var now = DateTime.UtcNow;
            if ((now - state.FailureWindowStart) > FailureWindow)
            {
                state.FailureWindowStart = now;
                state.Failures = 0;
            }
            state.Failures++;
            if (state.Failures >= MaxRedeemFailures)
            {
                state.LockedUntil = now + LockDuration;
                state.Failures = 0;
                Log.Warn($"[Pairing] IP {clientIp} 连续失败达到上限，已临时锁定 {LockDuration.TotalMinutes} 分钟");
            }
        }
    }

    private void ClearFailures(string clientIp)
    {
        if (_ipStates.TryGetValue(clientIp, out var state))
        {
            lock (state)
            {
                state.Failures = 0;
                state.LockedUntil = null;
            }
        }
    }

    private List<DeviceRecord> ReadDevices()
    {
        lock (StoreLock)
        {
            var token = Config.ReadConfigKey("devices") as JArray ?? new JArray();
            return token.ToObject<List<DeviceRecord>>() ?? new List<DeviceRecord>();
        }
    }

    private void WriteDevices(List<DeviceRecord> devices)
    {
        lock (StoreLock)
        {
            Config.WriteConfigKey("devices", JArray.FromObject(devices));
        }
    }

    private static List<string> NormalizeResources(IEnumerable<string>? resources) =>
        (resources ?? Array.Empty<string>())
            .Select(resource => resource?.Trim())
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Select(resource => resource!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string ReadPublicUrl()
    {
        lock (StoreLock)
        {
            var stored = Config.ReadConfigKey("publicUrl")?.ToString();
            var normalized = NormalizeUrl(stored, string.Empty);
            return IsValidPublicUrl(normalized) ? normalized : string.Empty;
        }
    }

    private bool TryDecodePayload(string? text, out PairingPayload payload, out string error)
    {
        payload = new PairingPayload();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(PayloadPrefix, StringComparison.Ordinal))
        {
            error = "无法识别的配对二维码";
            return false;
        }
        try
        {
            var json = Encoding.UTF8.GetString(FromBase64Url(text[PayloadPrefix.Length..]));
            var parsed = JsonSerializer.Deserialize<PairingPayload>(json, JsonOptions);
            if (parsed == null || parsed.Version != 1 ||
                string.IsNullOrWhiteSpace(parsed.Url) ||
                string.IsNullOrWhiteSpace(parsed.Code) ||
                string.IsNullOrWhiteSpace(parsed.Signature))
            {
                error = "配对二维码内容不完整";
                return false;
            }
            payload = parsed;
            return true;
        }
        catch
        {
            error = "配对二维码解析失败";
            return false;
        }
    }

    private string Sign(string url, string code, long expiresAt)
    {
        using var hmac = new HMACSHA256(FromBase64Url(_installSecret));
        var data = Encoding.UTF8.GetBytes($"{url.ToLowerInvariant()}|{code.ToUpperInvariant()}|{expiresAt}");
        return Base64Url(hmac.ComputeHash(data));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string GenerateCode()
    {
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(CrockfordAlphabet[RandomNumberGenerator.GetInt32(CrockfordAlphabet.Length)]);
        }
        return sb.ToString();
    }

    private static string RandomString(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(CrockfordAlphabet[RandomNumberGenerator.GetInt32(CrockfordAlphabet.Length)].ToString().ToLowerInvariant());
        }
        return sb.ToString();
    }

    private static string NormalizeUrl(string? explicitUrl, string fallback)
    {
        var url = string.IsNullOrWhiteSpace(explicitUrl) ? fallback : explicitUrl!;
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0) return string.Empty;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }

    private static bool IsValidPublicUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
               !string.IsNullOrWhiteSpace(parsed.Host);
    }

    private static string Sanitize(string? value, int maxLength, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) return fallback;
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string Iso(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
        return Convert.FromBase64String(s);
    }
}
