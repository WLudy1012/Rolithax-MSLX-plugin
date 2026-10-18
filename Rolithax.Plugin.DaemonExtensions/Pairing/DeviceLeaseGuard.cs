using Microsoft.AspNetCore.Http;

namespace Rolithax.Plugin.DaemonExtensions.Pairing;

/// <summary>
/// 插件全部端点共用的请求期授权校验：配对设备的凭据在每次请求时核对到期、撤销和签发链，
/// 不依赖定时清理是否已经运行。
/// </summary>
internal static class DeviceLeaseGuard
{
    /// <summary>凭据已失效时返回 401 结果；匿名请求或有效凭据返回 null。</summary>
    public static IResult? Reject(HttpContext context, PairingService service)
    {
        if (context.User?.Identity?.IsAuthenticated != true) return null;
        var userId = context.User.FindFirst("UserId")?.Value;
        return service.IsCredentialActive(userId)
            ? null
            : Results.Json(new { code = 401, message = "设备授权已失效，请重新配对" }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
