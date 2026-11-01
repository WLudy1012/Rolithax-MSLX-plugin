using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json.Linq;

namespace Rolithax.Plugin.DaemonExtensions;

internal sealed class FeatureSettings
{
    private const string ConfigKey = "enabledApis";
    private static readonly string[] Names = ["pairing", "serverIcon", "resourceInstall"];
    private readonly object _lock = new();

    private static MSLX.SDK.Interfaces.IPluginConfigBridge Config =>
        global::MSLX.SDK.MSLX.Config.GetPluginConfig(RolithaxPlugin.PluginId);

    public Dictionary<string, bool> Get()
    {
        lock (_lock)
        {
            var stored = Config.ReadConfigKey(ConfigKey);
            if (stored is not null && stored.Type != JTokenType.Object)
                throw new InvalidOperationException("扩展 API 配置无效");
            var values = stored as JObject;
            return Names.ToDictionary(name => name, name =>
            {
                var value = values?[name];
                if (value is null) return true;
                if (value.Type != JTokenType.Boolean) throw new InvalidOperationException("扩展 API 配置无效");
                return value.Value<bool>();
            });
        }
    }

    public Dictionary<string, bool>? Set(string name, bool enabled)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) return null;
        lock (_lock)
        {
            var values = Get();
            values[name] = enabled;
            Config.WriteConfigKey(ConfigKey, JObject.FromObject(values));
            return values;
        }
    }

    public RouteGroupBuilder Guard(RouteGroupBuilder group, string name)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            var taskContinuation = name == "resourceInstall" && request.RouteValues.ContainsKey("id") &&
                (HttpMethods.IsGet(request.Method) ||
                 (HttpMethods.IsPost(request.Method) && request.Path.Value?.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) == true));
            try
            {
                if (!Get()[name] && !taskContinuation)
                    return Results.Json(new { code = 403, message = "该扩展 API 已关闭" }, statusCode: 403);
            }
            catch (Exception)
            {
                return Results.Json(new { code = 503, message = "扩展 API 配置不可用" }, statusCode: 503);
            }
            return await next(context);
        });
        return group;
    }
}

internal sealed record FeatureUpdate(bool? Enabled);

internal static class FeatureEndpoints
{
    public static void Map(RouteGroupBuilder api, FeatureSettings features)
    {
        api.MapGet("/features", () => Results.Ok(new { code = 200, message = "ok", data = features.Get() }))
            .RequireAuthorization();
        api.MapPut("/features/{feature}", (string feature, FeatureUpdate request) =>
        {
            if (request.Enabled is null)
                return Results.Json(new { code = 400, message = "请指定开关状态" }, statusCode: 400);
            var values = features.Set(feature, request.Enabled.Value);
            return values is null
                ? Results.Json(new { code = 404, message = "未知扩展 API" }, statusCode: 404)
                : Results.Ok(new { code = 200, message = "ok", data = values });
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });
    }
}
