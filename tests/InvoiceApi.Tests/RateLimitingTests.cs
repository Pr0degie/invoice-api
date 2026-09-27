using System.Net;
using System.Reflection;
using FluentAssertions;
using InvoiceApi.Controllers;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceApi.Tests;

// Runs the real forwarded-headers + rate-limit configuration in an in-memory server.
// Behind Coolify every auth call reaches the API from the Next.js container, so the
// per-client partitions only work with the X-Forwarded-For the frontend sends —
// and only if a client can't pick its own bucket by sending that header itself.
public class RateLimitingTests : IAsyncLifetime
{
    private const string FrontendContainer = "172.18.0.5"; // peer on the Docker network
    private const string PublicPeer = "203.0.113.7";

    private IHost _host = default!;
    private TestServer _server = default!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.Configure<ForwardedHeadersOptions>(RateLimiting.ConfigureForwardedHeaders);
                    services.AddRateLimiter(RateLimiting.ConfigurePolicies);
                })
                .Configure(app =>
                {
                    app.UseForwardedHeaders();
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/login", () => "ok").RequireRateLimiting(RateLimiting.AuthIp);
                        endpoints.MapPost("/refresh", () => "ok").RequireRateLimiting(RateLimiting.AuthSession);
                    });
                }))
            .StartAsync();
        _server = _host.GetTestServer();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Login_ClientsForwardedByTheFrontend_HaveSeparateBuckets()
    {
        for (var i = 0; i < 5; i++)
            (await Post("/login", FrontendContainer, "198.51.100.1")).Should().Be(200);

        (await Post("/login", FrontendContainer, "198.51.100.1")).Should().Be(429);
        (await Post("/login", FrontendContainer, "198.51.100.2")).Should().Be(200);
    }

    [Fact]
    public async Task ForwardedFor_FromIpv4MappedPrivatePeer_IsHonored()
    {
        // Kestrel's dual-mode socket (ASPNETCORE_URLS=http://+:8080) reports IPv4 peers as ::ffff:a.b.c.d
        for (var i = 0; i < 5; i++)
            await Post("/login", "::ffff:" + FrontendContainer, "198.51.100.1");

        (await Post("/login", "::ffff:" + FrontendContainer, "198.51.100.2")).Should().Be(200);
    }

    [Fact]
    public async Task ForwardedFor_FromPublicPeer_IsIgnored()
    {
        // A directly reachable API port must not let a client choose a fresh bucket per request
        for (var i = 0; i < 5; i++)
            await Post("/login", PublicPeer, $"198.51.100.{10 + i}");

        (await Post("/login", PublicPeer, "198.51.100.99")).Should().Be(429);
    }

    [Fact]
    public async Task Refresh_AllowsFrequentCalls_WithoutTouchingTheLoginBucket()
    {
        // One user refreshes every 15 min per tab/device — and a lost race must not log them out
        for (var i = 0; i < 30; i++)
            (await Post("/refresh", FrontendContainer, "198.51.100.3")).Should().Be(200);

        (await Post("/login", FrontendContainer, "198.51.100.3")).Should().Be(200);
    }

    [Theory]
    [InlineData(nameof(AuthController.Refresh), RateLimiting.AuthSession)]
    [InlineData(nameof(AuthController.Logout), RateLimiting.AuthSession)]
    [InlineData(nameof(AuthController.Login), RateLimiting.AuthIp)]
    [InlineData(nameof(AuthController.Register), RateLimiting.AuthIp)]
    [InlineData(nameof(AuthController.ForgotPassword), RateLimiting.AuthIp)]
    [InlineData(nameof(AuthController.ResetPassword), RateLimiting.AuthIp)]
    [InlineData(nameof(AuthController.VerifyEmail), RateLimiting.AuthIp)]
    [InlineData(nameof(AuthController.ResendVerification), RateLimiting.AuthIp)]
    [InlineData(nameof(AuthController.ChangePassword), RateLimiting.AuthIp)]
    public void AuthEndpoints_UseTheExpectedPolicy(string action, string policy)
    {
        typeof(AuthController).GetMethod(action)!
            .GetCustomAttribute<EnableRateLimitingAttribute>()!
            .PolicyName.Should().Be(policy);
    }

    private async Task<int> Post(string path, string peer, string forwardedFor)
    {
        var ctx = await _server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Post;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            c.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });
        return ctx.Response.StatusCode;
    }
}
