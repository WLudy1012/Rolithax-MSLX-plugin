using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MSLX.SDK.Models;
using Rolithax.Plugin.DaemonExtensions.Pairing;
using Rolithax.Schema.HostTasks;

namespace Rolithax.Plugin.DaemonExtensions.Schema;

internal static class SchemaEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly SemaphoreSlim ProbeSlots = new(2, 2);

    public static void Map(RouteGroupBuilder group, FeatureSettings settings, PairingService pairing)
    {
        group.RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var principal = context.HttpContext.User;
            var id = principal.FindFirst("UserId")?.Value;
            return string.IsNullOrWhiteSpace(id) || principal.HasClaim(claim => claim.Type == "Scope")
                ? Fail(403, "需要普通 Daemon 凭据")
                : await next(context);
        });
        group.MapGet("/capabilities", async Task<IResult> (HttpContext context) =>
        {
            var hostTasks = HostEnabled(settings) && await WorkerSupportsAsync("ping", context.RequestAborted);
            return Ok(new
            {
                pluginVersion = RolithaxPlugin.AssemblyVersion,
                protocolVersion = RolithaxPlugin.SchemaProtocolVersion,
                capabilities = new
                {
                    mcdrLayout = OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
                    versionProbe = OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
                    hostTasks,
                    commandVersionProbe = hostTasks && await WorkerSupportsAsync("probeCapabilities", context.RequestAborted),
                },
            });
        });
        group.MapGet("/instances/{id:int}/layout", (int id, HttpContext context) =>
        {
            var error = Instance(context, id, out var instance);
            if (error is not null) return error;
            try { return Ok(InstanceMetadata.Layout(instance!)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                return Fail(422, "无法识别当前实例的目录布局");
            }
        });
        group.MapPost("/instances/{id:int}/preflight", async (int id, HttpContext context) =>
        {
            var error = Instance(context, id, out var instance);
            if (error is not null) return error;
            ProbeRequest? request;
            try
            {
                var bytes = await ReadRequestAsync(context.Request, context.RequestAborted);
                request = JsonSerializer.Deserialize<ProbeRequest>(bytes, Json);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return Fail(400, "版本探测请求无效");
            }
            if (request is null || !WorkerProtocol.IsProbeKind(request.Kind) || !WorkerProtocol.IsProbeTarget(request.Target) ||
                request.CommandProbeId is { } probeId && !WorkerProtocol.IsProbeConfirmation(probeId))
                return Fail(400, "版本探测请求无效");
            if (!ProbeSlots.Wait(0)) return Fail(429, "版本探测任务繁忙");
            VersionProbe result;
            try
            {
                result = InstanceMetadata.Probe(instance!, request.Kind!, request.Target!, context.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                result = new("unknown");
            }
            finally { ProbeSlots.Release(); }
            if (result.Status != "unknown" || !HostEnabled(settings)) return Ok(result);
            var userId = context.User.FindFirst("UserId")!.Value;
            if (!pairing.IsCredentialActive(userId)) return Fail(401, "设备授权已失效，请重新配对");
            var repeatedPermission = Instance(context, id, out _);
            if (repeatedPermission is not null) return repeatedPermission;
            try
            {
                var command = request.CommandProbeId is null ? "describeVersionProbe" : "runVersionProbe";
                var reply = await WorkerClient.RequestAsync(new(command, userId, id, request.CommandProbeId, request.Kind, request.Target),
                    context.RequestAborted);
                if (request.CommandProbeId is null) return Ok(result with { CommandProbe = reply.CommandProbe });
                return reply.Code switch
                {
                    200 when reply.Version is { } version => Ok(new VersionProbe("found", version, "hostCommand")),
                    200 => Ok(result),
                    403 => Fail(403, "版本探测授权已失效，请重新检查"),
                    429 => Fail(429, "宿主机任务繁忙"),
                    _ => Fail(503, "宿主机 Worker 暂不可用"),
                };
            }
            catch (Exception ex) when (WorkerUnavailable(ex))
            {
                return request.CommandProbeId is null ? Ok(result) : Fail(503, "宿主机 Worker 暂不可用");
            }
        });
        group.MapGet("/instances/{id:int}/tasks", (int id, HttpContext context) =>
            TaskRequestAsync(id, null, context, settings, pairing));
        group.MapPost("/instances/{id:int}/tasks/{taskId}/run", (int id, string taskId, HttpContext context) =>
            TaskRequestAsync(id, taskId, context, settings, pairing));
    }

    private static async Task<IResult> TaskRequestAsync(
        int id, string? taskId, HttpContext context, FeatureSettings settings, PairingService pairing)
    {
        var error = Instance(context, id, out _);
        if (error is not null) return error;
        if (!HostEnabled(settings)) return Fail(403, "宿主机任务未启用");
        if (taskId is not null)
        {
            if (!WorkerProtocol.IsTaskId(taskId)) return Fail(400, "任务标识无效");
            try
            {
                var bytes = await ReadRequestAsync(context.Request, context.RequestAborted);
                if (bytes.Length != 0)
                {
                    using var body = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
                    if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Any())
                        return Fail(400, "宿主机任务不接收执行参数");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { return Fail(400, "任务请求无效"); }
        }
        var userId = context.User.FindFirst("UserId")!.Value;
        try
        {
            if (!pairing.IsCredentialActive(userId)) return Fail(401, "设备授权已失效，请重新配对");
            var repeatedPermission = Instance(context, id, out _);
            if (repeatedPermission is not null) return repeatedPermission;
            var reply = await WorkerClient.RequestAsync(new(taskId is null ? "list" : "run", userId, id, taskId), context.RequestAborted);
            if (taskId is not null) global::MSLX.SDK.MSLX.Logger.Info($"[Schema] user={userId} instance={id} task={taskId} status={reply.Code}");
            return reply.Code switch
            {
                200 when taskId is null => Ok(reply.Tasks ?? []),
                200 when reply.Result is not null => Ok(reply.Result),
                403 => Fail(403, "没有此宿主机任务的权限"),
                429 => Fail(429, "宿主机任务繁忙"),
                _ => Fail(503, "宿主机 Worker 暂不可用"),
            };
        }
        catch (Exception ex) when (WorkerUnavailable(ex))
        {
            return Fail(503, "宿主机 Worker 暂不可用");
        }
    }

    private static bool HostEnabled(FeatureSettings settings)
    {
        try { return settings.Get()["hostTasks"]; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<bool> WorkerSupportsAsync(string operation, CancellationToken cancellation)
    {
        try { return (await WorkerClient.RequestAsync(new(operation), cancellation)).Code == 200; }
        catch (Exception ex) when (WorkerUnavailable(ex))
        {
            return false;
        }
    }

    private static bool WorkerUnavailable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or JsonException or System.Net.Sockets.SocketException or
        System.Security.Cryptography.CryptographicException or TimeoutException;

    private static IResult? Instance(HttpContext context, int id, out McServerInfo.ServerInfo? instance)
    {
        instance = null;
        if (id <= 0) return Fail(400, "实例编号无效");
        var userId = context.User.FindFirst("UserId")?.Value;
        var config = global::MSLX.SDK.MSLX.Config;
        if (string.IsNullOrWhiteSpace(userId) || !config.Users.HasResourcePermission(userId, "server", id))
            return Fail(403, "没有该实例的访问权限");
        instance = config.Servers.GetServer((uint)id);
        return instance is null ? Fail(404, "实例不存在") : null;
    }

    private static async Task<byte[]> ReadRequestAsync(HttpRequest request, CancellationToken cancellation)
    {
        var buffer = new byte[1025];
        var size = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation);
        if (size > 1024) throw new InvalidDataException();
        return buffer[..size];
    }

    private static IResult Ok(object data) => Results.Json(new { code = 200, message = "ok", data }, Json);
    private static IResult Fail(int code, string message) => Results.Json(new { code, message }, Json, statusCode: code);
    private sealed record ProbeRequest(string? Kind, string? Target, string? CommandProbeId = null);
}
