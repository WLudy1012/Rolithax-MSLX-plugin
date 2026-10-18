using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MSLX.SDK.Models;
using Rolithax.Plugin.DaemonExtensions.Pairing;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

/// <summary>审核问题 1：设备记录并发更新互相覆盖。</summary>
public class DeviceRecordAtomicityTests
{
    [Fact]
    public async Task Concurrent_redeems_keep_every_device_record()
    {
        using var f = new PairingFixture();
        var payloads = Enumerable.Range(0, 12)
            .Select(i => f.NewPayload(TestHost.AddUser($"admin{i}", "admin")))
            .ToList();

        var results = await Task.WhenAll(payloads.Select((p, i) => Task.Run(() => f.RedeemPayload(p, $"fp-{i}"))));

        Assert.All(results, r => Assert.Equal(200, r.Code));
        var records = PairingFixture.Records();
        Assert.Equal(12, records.Count);
        foreach (var result in results)
        {
            // 每个已签发的 API Key 都必须对应一条可管理的设备记录。
            var user = TestHost.Users.GetUserByApiKey(result.Data.GetProperty("apiKey").GetString()!);
            Assert.NotNull(user);
            Assert.Contains(records, d => d.UserId == user!.Id && !d.Revoked);
        }
    }

    [Fact]
    public async Task Concurrent_revoke_and_redeem_do_not_overwrite_each_other()
    {
        using var f = new PairingFixture();
        var admin = TestHost.AddUser("root", "admin");
        var existing = Enumerable.Range(0, 5)
            .Select(i => f.Pair(TestHost.AddUser($"seed{i}", "admin")))
            .ToList();
        var payloads = Enumerable.Range(0, 5)
            .Select(i => f.NewPayload(TestHost.AddUser($"late{i}", "admin")))
            .ToList();

        var tasks = existing.Select(d => Task.Run(() => f.Service.Revoke(d.DeviceId).Code))
            .Concat(payloads.Select((p, i) => Task.Run(() => f.RedeemPayload(p, $"late-fp-{i}").Code)));
        var codes = await Task.WhenAll(tasks);

        Assert.All(codes, c => Assert.Equal(200, c));
        var records = PairingFixture.Records();
        Assert.Equal(10, records.Count);
        Assert.Equal(5, records.Count(d => d.Revoked));
        Assert.Equal(5, records.Count(d => !d.Revoked));
        Assert.All(records.Where(d => !d.Revoked), d => Assert.NotNull(TestHost.Users.GetUserById(d.UserId)));
        Assert.All(records.Where(d => d.Revoked), d => Assert.Null(TestHost.Users.GetUserById(d.UserId)));
        _ = admin;
    }

    [Fact]
    public void Failed_user_creation_leaves_neither_user_nor_record()
    {
        using var f = new PairingFixture();
        var payload = f.NewPayload(TestHost.AddUser("root", "admin"));
        var usersBefore = TestHost.Users.Count;

        TestHost.Users.FailCreate = true;
        var (code, _, _) = f.RedeemPayload(payload, "fp");

        Assert.Equal(500, code);
        Assert.Empty(PairingFixture.Records());
        Assert.Equal(usersBefore, TestHost.Users.Count);

        // 配对码没有被消耗，宿主恢复后可以重试。
        TestHost.Users.FailCreate = false;
        Assert.Equal(200, f.RedeemPayload(payload, "fp").Code);
    }

    [Fact]
    public void Revoke_only_marks_record_after_the_credential_is_really_gone()
    {
        using var f = new PairingFixture();
        var device = f.Pair(TestHost.AddUser("root", "admin"));

        TestHost.Users.FailDelete = true;
        Assert.Equal(500, f.Service.Revoke(device.DeviceId).Code);
        Assert.False(PairingFixture.Record(device).Revoked);
        Assert.NotNull(TestHost.Users.GetUserByApiKey(device.ApiKey));

        TestHost.Users.FailDelete = false;
        Assert.Equal(200, f.Service.Revoke(device.DeviceId).Code);
        Assert.True(PairingFixture.Record(device).Revoked);
        Assert.Null(TestHost.Users.GetUserByApiKey(device.ApiKey));
    }
}

/// <summary>审核问题 2：配对设备签发更长期限的新凭据，且原设备撤销/到期后新凭据仍有效。</summary>
public class DerivedCredentialTests
{
    [Fact]
    public void Device_cannot_issue_a_credential_that_outlives_itself()
    {
        using var f = new PairingFixture();
        var parent = f.Pair(TestHost.AddUser("root", "admin"), ttlDays: 30);
        f.Advance(TimeSpan.FromDays(10));

        var child = f.Pair(f.UserOf(parent), ttlDays: 365);

        var parentRecord = PairingFixture.Record(parent);
        var childRecord = PairingFixture.Record(child);
        Assert.Equal(parentRecord.ExpiresAt, childRecord.ExpiresAt);
        Assert.Equal(parentRecord.UserId, childRecord.IssuedBy);
    }

    [Fact]
    public void Revoking_the_issuing_device_revokes_everything_it_issued()
    {
        using var f = new PairingFixture();
        var parent = f.Pair(TestHost.AddUser("root", "admin"));
        var child = f.Pair(f.UserOf(parent));
        var grandchild = f.Pair(f.UserOf(child));

        Assert.Equal(200, f.Service.Revoke(parent.DeviceId).Code);

        foreach (var device in new[] { parent, child, grandchild })
        {
            Assert.Null(TestHost.Users.GetUserByApiKey(device.ApiKey));
            Assert.True(PairingFixture.Record(device).Revoked);
        }
        Assert.Equal("issuer-revoked", PairingFixture.Record(child).RevokeReason);
    }

    [Fact]
    public void Expired_issuer_invalidates_children_on_next_request()
    {
        using var f = new PairingFixture();
        var parent = f.Pair(TestHost.AddUser("root", "admin"), ttlDays: 30);
        var child = f.Pair(f.UserOf(parent), ttlDays: 5);

        // 把子设备的到期时间人为拉到父设备之后（例如旧数据），父设备到期仍应带走子设备。
        var records = PairingFixture.Records();
        records.Single(d => d.DeviceId == child.DeviceId).ExpiresAt += 90 * 86400;
        TestHost.PluginConfig.WriteConfigKey("devices", Newtonsoft.Json.Linq.JArray.FromObject(records));
        f.Advance(TimeSpan.FromDays(31));

        Assert.False(f.Service.IsCredentialActive(child.UserId));
        Assert.Null(TestHost.Users.GetUserByApiKey(child.ApiKey));
        Assert.Null(TestHost.Users.GetUserByApiKey(parent.ApiKey));
    }

    [Fact]
    public void Revoked_or_unrecorded_paired_device_cannot_issue_codes()
    {
        using var f = new PairingFixture();
        var device = f.Pair(TestHost.AddUser("root", "admin"));
        var deviceUser = f.UserOf(device);

        // 记录丢失的旧配对账号（无法确认期限）也不能签发。
        var orphan = TestHost.AddUser("pair_dabcdefghij", "admin");
        orphan.Name = "配对设备 legacy";
        Assert.Equal(403, f.Service.CreateCode(new CreateCodeRequest(), orphan).Code);

        f.Service.Revoke(device.DeviceId);
        Assert.Equal(403, f.Service.CreateCode(new CreateCodeRequest(), deviceUser).Code);
    }
}

/// <summary>审核问题 3：设备替换只凭客户端提交的指纹。</summary>
public class DeviceReplacementTests
{
    [Fact]
    public void Same_fingerprint_without_proof_never_touches_other_devices()
    {
        using var f = new PairingFixture();
        var victim = f.Pair(TestHost.AddUser("root", "admin"), fingerprint: "shared-fp");
        var bob = TestHost.AddUser("bob", "user", "server:1");

        var attacker = f.Pair(bob, scope: "limited", fingerprint: "shared-fp");

        Assert.NotNull(TestHost.Users.GetUserByApiKey(victim.ApiKey));
        Assert.False(PairingFixture.Record(victim).Revoked);
        Assert.False(PairingFixture.Record(attacker).Revoked);
    }

    [Fact]
    public void Same_fingerprint_with_a_wrong_key_is_not_a_replacement()
    {
        using var f = new PairingFixture();
        var victim = f.Pair(TestHost.AddUser("root", "admin"), fingerprint: "shared-fp");

        f.Pair(TestHost.AddUser("bob", "user"), scope: "limited", fingerprint: "shared-fp", previousApiKey: "not-a-real-key");

        Assert.False(PairingFixture.Record(victim).Revoked);
        Assert.NotNull(TestHost.Users.GetUserByApiKey(victim.ApiKey));
    }

    [Fact]
    public void A_valid_key_of_another_device_cannot_replace_a_fingerprint_it_does_not_own()
    {
        using var f = new PairingFixture();
        var victim = f.Pair(TestHost.AddUser("root", "admin"), fingerprint: "victim-fp");
        var mine = f.Pair(TestHost.AddUser("bob", "admin"), fingerprint: "my-fp");

        f.Pair(TestHost.AddUser("eve", "admin"), fingerprint: "victim-fp", previousApiKey: mine.ApiKey);

        Assert.False(PairingFixture.Record(victim).Revoked);
        Assert.False(PairingFixture.Record(mine).Revoked);
    }

    [Fact]
    public void Presenting_the_old_key_replaces_the_old_device_and_keeps_its_children()
    {
        using var f = new PairingFixture();
        var old = f.Pair(TestHost.AddUser("root", "admin"), fingerprint: "my-fp");
        var child = f.Pair(f.UserOf(old));

        var renewed = f.Pair(TestHost.AddUser("root2", "admin"), fingerprint: "my-fp", previousApiKey: old.ApiKey);

        Assert.True(PairingFixture.Record(old).Revoked);
        Assert.Equal("replaced", PairingFixture.Record(old).RevokeReason);
        Assert.Null(TestHost.Users.GetUserByApiKey(old.ApiKey));
        Assert.NotNull(TestHost.Users.GetUserByApiKey(renewed.ApiKey));
        // 子设备改挂到新设备名下，不会因为原设备被替换而被级联撤销。
        Assert.Equal(PairingFixture.Record(renewed).UserId, PairingFixture.Record(child).IssuedBy);
        Assert.True(f.Service.IsCredentialActive(child.UserId));
    }

    [Fact]
    public void Empty_fingerprint_is_never_a_replacement_target()
    {
        using var f = new PairingFixture();
        var old = f.Pair(TestHost.AddUser("root", "admin"), fingerprint: " ");

        f.Pair(TestHost.AddUser("root2", "admin"), fingerprint: " ", previousApiKey: old.ApiKey);

        Assert.False(PairingFixture.Record(old).Revoked);
    }
}

/// <summary>审核问题 4：到期只靠定时清理，插件停用/移除后凭据仍有效。</summary>
public class CredentialLifetimeTests
{
    private static DefaultHttpContext ContextFor(string? userId)
    {
        var context = new DefaultHttpContext();
        if (userId != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("UserId", userId) }, "ApiKey"));
        }
        return context;
    }

    [Fact]
    public void Guard_rejects_an_expired_device_without_waiting_for_the_sweeper()
    {
        using var f = new PairingFixture();
        var device = f.Pair(TestHost.AddUser("root", "admin"), ttlDays: 3);
        Assert.Null(DeviceLeaseGuard.Reject(ContextFor(device.UserId), f.Service));

        f.Advance(TimeSpan.FromDays(3) + TimeSpan.FromSeconds(1));
        var rejection = DeviceLeaseGuard.Reject(ContextFor(device.UserId), f.Service);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(rejection);
        Assert.Equal(401, status.StatusCode);
        Assert.Null(TestHost.Users.GetUserByApiKey(device.ApiKey));
        Assert.Equal("expired", PairingFixture.Record(device).RevokeReason);
    }

    [Fact]
    public void Guard_ignores_anonymous_and_ordinary_users()
    {
        using var f = new PairingFixture();
        var human = TestHost.AddUser("alice", "user");

        Assert.Null(DeviceLeaseGuard.Reject(ContextFor(null), f.Service));
        Assert.Null(DeviceLeaseGuard.Reject(ContextFor(human.Id), f.Service));
        Assert.Null(DeviceLeaseGuard.Reject(ContextFor("system-admin"), f.Service));
    }

    [Fact]
    public void Loading_the_plugin_again_collects_devices_that_expired_while_it_was_off()
    {
        using var f = new PairingFixture();
        var device = f.Pair(TestHost.AddUser("root", "admin"), ttlDays: 2);
        f.Advance(TimeSpan.FromDays(3));

        var reloaded = new PairingService { Clock = () => f.Now };
        reloaded.Initialize();
        try
        {
            Assert.Null(TestHost.Users.GetUserByApiKey(device.ApiKey));
            Assert.Equal("expired", PairingFixture.Record(device).RevokeReason);
        }
        finally
        {
            reloaded.Shutdown();
        }
    }

    [Fact]
    public void Unloading_the_plugin_revokes_every_paired_device()
    {
        using var f = new PairingFixture();
        var a = f.Pair(TestHost.AddUser("root", "admin"));
        var b = f.Pair(TestHost.AddUser("root2", "admin"), scope: "limited");
        var human = TestHost.AddUser("alice", "user");

        f.Service.Shutdown();

        Assert.Null(TestHost.Users.GetUserByApiKey(a.ApiKey));
        Assert.Null(TestHost.Users.GetUserByApiKey(b.ApiKey));
        Assert.NotNull(TestHost.Users.GetUserById(human.Id));
        Assert.All(PairingFixture.Records(), d => Assert.Equal("plugin-unloaded", d.RevokeReason));
    }

    [Fact]
    public void Paired_accounts_without_a_record_are_removed_but_lookalike_users_are_not()
    {
        using var f = new PairingFixture();
        var orphan = TestHost.AddUser("pair_dqwertyuiop", "admin");
        orphan.Name = "配对设备 lost";
        var lookalike = TestHost.AddUser("pair_dasdfghjklz", "user"); // 显示名不符合配对账号命名

        Assert.False(f.Service.IsCredentialActive(orphan.Id));
        Assert.Null(TestHost.Users.GetUserById(orphan.Id));

        f.Service.SweepNow();
        Assert.NotNull(TestHost.Users.GetUserById(lookalike.Id));
    }

    [Fact]
    public void Sweeper_removes_orphans_even_if_nobody_calls_the_plugin()
    {
        using var f = new PairingFixture();
        var orphan = TestHost.AddUser("pair_dzxcvbnmasd", "admin");
        orphan.Name = "配对设备 lost";

        f.Service.SweepNow();

        Assert.Null(TestHost.Users.GetUserById(orphan.Id));
    }
}

/// <summary>审核问题 5：签发者被删除后，未过期的配对码仍可兑换。</summary>
public class IssuerLifecycleTests
{
    [Fact]
    public void Code_cannot_be_redeemed_after_its_issuer_is_deleted()
    {
        using var f = new PairingFixture();
        var issuer = TestHost.AddUser("temp-admin", "admin");
        var payload = f.NewPayload(issuer);
        var usersBefore = TestHost.Users.Count;
        TestHost.Users.DeleteUser(issuer.Id);

        var (code, _, _) = f.RedeemPayload(payload, "fp");

        Assert.Equal(410, code);
        Assert.Empty(PairingFixture.Records());
        Assert.Equal(usersBefore - 1, TestHost.Users.Count);
    }

    [Fact]
    public void Full_scope_code_dies_when_the_issuer_loses_admin()
    {
        using var f = new PairingFixture();
        var issuer = TestHost.AddUser("demoted", "admin");
        var payload = f.NewPayload(issuer, scope: "full");
        issuer.Role = "user";

        Assert.Equal(410, f.RedeemPayload(payload, "fp").Code);
        Assert.Empty(PairingFixture.Records());
    }

    [Fact]
    public void Redeemed_resources_never_exceed_what_the_issuer_still_has()
    {
        using var f = new PairingFixture();
        var issuer = TestHost.AddUser("bob", "user", "server:1", "server:2");
        var payload = f.NewPayload(issuer, scope: "limited");
        issuer.Resources = new List<string> { "server:1" };

        var (code, _, data) = f.RedeemPayload(payload, "fp");

        Assert.Equal(200, code);
        Assert.Equal(new[] { "server:1" }, data.GetProperty("resources").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Deleting_the_issuer_revokes_devices_it_already_issued()
    {
        using var f = new PairingFixture();
        var issuer = TestHost.AddUser("temp-admin", "admin");
        var device = f.Pair(issuer);
        TestHost.Users.DeleteUser(issuer.Id);

        f.Service.SweepNow();

        Assert.Null(TestHost.Users.GetUserByApiKey(device.ApiKey));
        Assert.Equal("issuer-removed", PairingFixture.Record(device).RevokeReason);
    }

    [Fact]
    public void Guard_also_catches_a_device_whose_issuer_is_gone()
    {
        using var f = new PairingFixture();
        var issuer = TestHost.AddUser("temp-admin", "admin");
        var device = f.Pair(issuer);
        TestHost.Users.DeleteUser(issuer.Id);

        Assert.False(f.Service.IsCredentialActive(device.UserId));
    }

    [Fact]
    public void Pending_codes_of_a_deleted_issuer_are_swept()
    {
        using var f = new PairingFixture();
        var issuer = TestHost.AddUser("temp-admin", "admin");
        var payload = f.NewPayload(issuer);
        TestHost.Users.DeleteUser(issuer.Id);

        f.Service.SweepNow();

        var (code, message, _) = f.RedeemPayload(payload, "fp");
        Assert.Equal(410, code);
        Assert.Contains("不存在或已被使用", message);
    }
}
