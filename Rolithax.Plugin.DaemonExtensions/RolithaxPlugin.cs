using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rolithax.Plugin.DaemonExtensions.Pairing;
using Rolithax.Plugin.DaemonExtensions.ServerIcon;
using Rolithax.Plugin.DaemonExtensions.Resources;
using MSLX.SDK;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

[assembly: ApplicationPart("Rolithax.Plugin.DaemonExtensions")]

namespace Rolithax.Plugin.DaemonExtensions;

/// <summary>
/// Rolithax Launcher 扩展插件：为 Rolithax Launcher 提供统一服务端扩展能力的第三方插件。
///
/// Daemon 的 PluginManager 对每个程序集只识别第一个 IPlugin 实现，因此这里用单一入口，
/// 在 OnRegisterEndpoints 中按插件 ID 挂载规范端点。
/// </summary>
public sealed class RolithaxPlugin : IPlugin
{
    /// <summary>
    /// 插件唯一标识须与前端 package.json.name 和 pluginConfig.name 一致。
    /// DLL 文件名是程序集名称，不要求等于 ID；旧插件须先卸载，避免重复注册端点。
    /// </summary>
    public const string PluginId = "mslx-plugin-rolithax";

    // 按官方规范将公开 API 放在 /api/plugin/{plugin-id}/ 下，防止插件之间路由冲突。
    public const string ApiPrefix = "/api/plugin/" + PluginId;

    public string Id => PluginId;

    public string Name => "Rolithax Launcher Daemon 扩展";

    public string Description =>
        "Rolithax Launcher 配套扩展，提供扫码配对、设备图标与服务器本机资源安装。";

    public string Version => GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string MinSDKVersion => "1.7.3";

    public string Developer => "WLudy1012";

    public string AuthorUrl => "https://github.com/WLudy1012";

    public string PluginUrl => "https://github.com/WLudy1012/Rolithax-MSLX-plugin";

    public string Icon => "https://cdnjson.com/images/2026/10/10/ic_launcher_foreground.png";

    private PairingService? _pairing;
    private ServerIconService? _serverIcon;
    private ResourceInstallService? _resourceInstaller;
    private bool _pairingInitialized;

    public void OnLoad()
    {
        // 配对：安装密钥准备 + 过期清理定时器（幂等，热加载多次仅初始化一次）
        _pairing ??= new PairingService();
        if (!_pairingInitialized)
        {
            _pairingInitialized = true;
            _pairing.Initialize();
        }

        // 图标：无状态，仅准备实例
        _serverIcon ??= new ServerIconService();
        _resourceInstaller ??= new ResourceInstallService();
    }

    public void OnUnload()
    {
        _pairing?.Shutdown();
        _resourceInstaller?.Shutdown();
        _pairingInitialized = false;
    }

    public void OnRegisterEndpoints(IEndpointRouteBuilder endpoints)
    {
        _pairing ??= new PairingService();
        _serverIcon ??= new ServerIconService();
        _resourceInstaller ??= new ResourceInstallService();

        // 三组端点共用一道请求期授权校验：配对设备凭据到期/撤销/签发链失效后立即拒绝。
        var pairing = _pairing;
        var api = endpoints.MapGroup(ApiPrefix);
        api.AddEndpointFilter(async (context, next) =>
            (object?)DeviceLeaseGuard.Reject(context.HttpContext, pairing) ?? await next(context));

        var features = new FeatureSettings();
        FeatureEndpoints.Map(api, features);
        PairingEndpoints.Map(features.Guard(api.MapGroup("/pair"), "pairing"), _pairing, "");
        ServerIconEndpoints.Map(features.Guard(api.MapGroup("/icon"), "serverIcon"), _serverIcon, "");
        ResourceInstallEndpoints.Map(features.Guard(api.MapGroup("/resources"), "resourceInstall"), _resourceInstaller, "");

        global::MSLX.SDK.MSLX.Logger.Info($"[{PluginId}] Rolithax 扩展已挂载配对、图标和资源安装接口");
    }
}
