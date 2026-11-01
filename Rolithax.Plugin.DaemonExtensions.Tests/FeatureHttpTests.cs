using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

public class FeatureHttpTests
{
    [Fact]
    public async Task AuthorizationPersistenceAndInFlightTaskRemainEnforced()
    {
        TestHost.Reset();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, FakeAuth>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup(RolithaxPlugin.ApiPrefix);
        var settings = new FeatureSettings();
        FeatureEndpoints.Map(api, settings);
        settings.Guard(api.MapGroup("/pair"), "pairing").MapGet("/config", () => Results.Ok());
        settings.Guard(api.MapGroup("/icon"), "serverIcon").MapGet("/server/{id:long}", (long id) => Results.Ok(id));
        var tasks = settings.Guard(api.MapGroup("/resources"), "resourceInstall");
        tasks.MapPost("/install", () => Results.Ok());
        tasks.MapGet("/install/{id}", (string id) => id == "active" ? Results.Ok() : Results.NotFound());
        tasks.MapPost("/install/{id}/cancel", (string id) => id == "active" ? Results.Ok() : Results.NotFound());
        var cancellation = TestContext.Current.CancellationToken;
        await app.StartAsync(cancellation);

        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        const string prefix = "/api/plugin/mslx-plugin-rolithax";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(prefix + "/features", cancellation)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "user");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(prefix + "/features", cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(prefix + "/features/pairing", new { enabled = false }, cancellation)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        foreach (var feature in new[] { "pairing", "serverIcon", "resourceInstall" })
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(prefix + "/features/" + feature, new { enabled = false }, cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(prefix + "/features/pairing", new { }, cancellation)).StatusCode);
        Assert.False(new FeatureSettings().Get()["pairing"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/pair/config", cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/icon/server/1", cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(prefix + "/resources/install", null, cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(prefix + "/resources/install/active", cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(prefix + "/resources/install/active/cancel", null, cancellation)).StatusCode);
        await app.StopAsync(cancellation);
    }

    private sealed class FakeAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role is not ("admin" or "user")) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test"), new Claim(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
