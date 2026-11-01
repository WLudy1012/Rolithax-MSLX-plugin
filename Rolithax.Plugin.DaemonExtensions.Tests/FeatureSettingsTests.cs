using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using MSLX.SDK.Attributes;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

public sealed class FeatureSettingsTests
{
    [Fact]
    public void DefaultsToEnabledAndPersistsIndependentChanges()
    {
        TestHost.Reset();
        var settings = new FeatureSettings();
        Assert.All(settings.Get().Values, Assert.True);
        settings.Set("pairing", false);
        settings.Set("serverIcon", false);
        var reloaded = new FeatureSettings().Get();
        Assert.False(reloaded["pairing"]);
        Assert.False(reloaded["serverIcon"]);
        Assert.True(reloaded["resourceInstall"]);
        Assert.Null(settings.Set("unknown", true));
    }

    [Fact]
    public void RejectsInvalidPersistedValues()
    {
        TestHost.Reset();
        TestHost.PluginConfig.WriteConfigKey("enabledApis", JObject.Parse("{\"pairing\":\"false\"}"));
        Assert.Throws<InvalidOperationException>(() => new FeatureSettings().Get());
    }

    [Fact]
    public async Task EndpointsExposeOnlyIntendedAuthorizationAndMediaScope()
    {
        TestHost.Reset();
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        var plugin = new RolithaxPlugin();
        plugin.OnRegisterEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        var icon = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/plugin/mslx-plugin-rolithax/icon/server/{id:long}");
        Assert.Contains(TokenScopes.Media, icon.Metadata.GetMetadata<AllowTokenScopeAttribute>()!.AllowedScopes);
        var features = endpoints.Where(endpoint => endpoint.RoutePattern.RawText == "/api/plugin/mslx-plugin-rolithax/features").ToList();
        Assert.Single(features);
        Assert.NotEmpty(features[0].Metadata.GetOrderedMetadata<IAuthorizeData>());
        var update = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/plugin/mslx-plugin-rolithax/features/{feature}");
        Assert.Contains(update.Metadata.GetOrderedMetadata<IAuthorizeData>(), value => value.Roles == "admin");
    }
}
