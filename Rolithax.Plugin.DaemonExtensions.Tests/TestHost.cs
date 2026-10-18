using MSLX.SDK.Interfaces;
using MSLX.SDK.Models;
using MSLX.SDK.Models.Instance;
using Newtonsoft.Json.Linq;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Rolithax.Plugin.DaemonExtensions.Tests;

/// <summary>
/// 进程内的 Daemon 替身：MSLX.SDK.MSLX 只允许初始化一次，因此全进程共用一套内存实现，
/// 每个测试开始前调用 <see cref="Reset"/> 清空用户与插件配置。
/// </summary>
internal static class TestHost
{
    public static readonly FakeUserStore Users = new();
    public static readonly FakePluginConfig PluginConfig = new();
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void Reset()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                global::MSLX.SDK.MSLX.Initialize(new FakeConfig(), new FakeLogger(), null!, null!, null!, null!);
                _initialized = true;
            }
        }
        Users.Clear();
        PluginConfig.Clear();
    }

    public static UserInfo AddUser(string username, string role = "user", params string[] resources)
    {
        var user = new UserInfo
        {
            Username = username,
            Name = username,
            Role = role,
            PasswordHash = "$2a$fake",
            ApiKey = "key-" + Guid.NewGuid().ToString("N"),
            Resources = resources.ToList(),
        };
        if (!Users.CreateUser(user)) throw new InvalidOperationException("duplicate test user");
        return user;
    }
}

internal sealed class FakeLogger : IMSLXLogger
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
    public void Debug(string message) { }
}

internal sealed class FakeConfig : IMSLXConfig
{
    public string GetAppDataPath() => Path.GetTempPath();
    public string GetAppConfigPath() => Path.GetTempPath();
    public T LoadJson<T>(string path) where T : JToken => throw new NotSupportedException();
    public void SaveJson<T>(string path, T data) where T : JToken => throw new NotSupportedException();
    public IMainConfigBridge Main => throw new NotSupportedException();
    public IServerConfigBridge Servers => throw new NotSupportedException();
    public IFrpConfigBridge Frp => throw new NotSupportedException();
    public ITaskConfigBridge Tasks => throw new NotSupportedException();
    public IUserConfigBridge Users => TestHost.Users;
    public IPluginConfigBridge GetPluginConfig(string pluginId) => TestHost.PluginConfig;
}

/// <summary>
/// 插件配置替身。读写之间故意放一个很短的停顿：真实 Daemon 的文件读写同样不是瞬时的，
/// 未加锁的「读-改-写」在这里会稳定地丢更新，从而让并发回归测试有判别力。
/// </summary>
internal sealed class FakePluginConfig : IPluginConfigBridge
{
    private readonly object _gate = new();
    private JObject _content = new();

    public void Clear()
    {
        lock (_gate) _content = new JObject();
    }

    public string GetDataPath() => Path.GetTempPath();

    public JObject ReadConfig()
    {
        lock (_gate) return (JObject)_content.DeepClone();
    }

    public JToken? ReadConfigKey(string key)
    {
        JToken? value;
        lock (_gate) value = _content[key]?.DeepClone();
        Thread.Sleep(2);
        return value;
    }

    public void WriteConfig(JObject content)
    {
        lock (_gate) _content = (JObject)content.DeepClone();
    }

    public void WriteConfigKey(string key, JToken value)
    {
        Thread.Sleep(2);
        lock (_gate) _content[key] = value.DeepClone();
    }
}

internal sealed class FakeUserStore : IUserConfigBridge
{
    private readonly object _gate = new();
    private readonly List<UserInfo> _users = new();

    /// <summary>模拟宿主把用户列表写盘的耗时（锁外等待），让读-改-写之间的竞争窗口与真实环境一样可观。</summary>
    public int IoDelayMs { get; set; } = 5;

    /// <summary>模拟宿主写盘失败。</summary>
    public bool FailCreate { get; set; }

    /// <summary>模拟宿主无法删除用户。</summary>
    public bool FailDelete { get; set; }

    public void Clear()
    {
        lock (_gate) _users.Clear();
        FailCreate = false;
        FailDelete = false;
    }

    public UserInfo? GetUserByApiKey(string apiKey)
    {
        lock (_gate) return _users.FirstOrDefault(u => u.ApiKey == apiKey);
    }

    public UserInfo? GetUserByUsername(string username)
    {
        lock (_gate) return _users.FirstOrDefault(u => u.Username == username);
    }

    public UserInfo? GetUserByOpenId(string openId) => null;
    public bool BindUserOpenId(string userId, string openId) => false;
    public bool UnbindUserOpenId(string userId) => false;
    public bool ValidateUser(string username, string rawPassword) => false;

    public bool CreateUser(UserInfo user)
    {
        Thread.Sleep(IoDelayMs);
        lock (_gate)
        {
            if (FailCreate || _users.Any(u => u.Username == user.Username)) return false;
            _users.Add(user);
            return true;
        }
    }

    public void UpdateLastLoginTime(string username) { }

    public UserInfo? GetUserById(string id)
    {
        if (id == "system-admin") return new UserInfo { Id = "system-admin", Username = "MSLX Manager", Role = "admin" };
        lock (_gate) return _users.FirstOrDefault(u => u.Id == id);
    }

    public List<UserInfo?> GetAllUsers()
    {
        lock (_gate) return _users.Cast<UserInfo?>().ToList();
    }

    public bool UpdateUser(UserInfo updatedUser)
    {
        lock (_gate)
        {
            var index = _users.FindIndex(u => u.Id == updatedUser.Id);
            if (index < 0) return false;
            _users[index] = updatedUser;
            return true;
        }
    }

    public bool DeleteUser(string userId)
    {
        Thread.Sleep(IoDelayMs);
        lock (_gate)
        {
            if (FailDelete) return false;
            return _users.RemoveAll(u => u.Id == userId) > 0;
        }
    }

    public bool UpdateUserResources(string userId, List<string> newResources) => false;

    public bool HasResourcePermission(string userId, string type, int id) => false;

    public int Count
    {
        get { lock (_gate) return _users.Count; }
    }

    public List<UserInfo> Snapshot()
    {
        lock (_gate) return _users.ToList();
    }
}
