using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace InvoiceApi.Services;

/// <summary>
/// Client-IP resolution (forwarded headers) and the rate-limit policies, wired up
/// in <c>Program.cs</c>. The two belong together: the per-IP partitions are only as
/// good as the IP the forwarded-headers middleware resolves.
/// </summary>
public static class RateLimiting
{
    public const string AuthIp = "auth-ip";
    public const string AuthSession = "auth-session";
    public const string ApiUser = "api-user";

    // Peers whose X-Forwarded-For is trusted: the Docker networks the Next.js
    // container and Coolify's Traefik sit on (plus loopback for local dev). The
    // exact proxy IPs aren't known up front, but a public peer — a client hitting
    // a published API port directly — must never choose its own rate-limit bucket.
    private static readonly System.Net.IPNetwork[] TrustedProxyNetworks =
    [
        System.Net.IPNetwork.Parse("10.0.0.0/8"),
        System.Net.IPNetwork.Parse("172.16.0.0/12"),
        System.Net.IPNetwork.Parse("192.168.0.0/16"),
        System.Net.IPNetwork.Parse("127.0.0.0/8"),
        System.Net.IPNetwork.Parse("::1/128"),
        System.Net.IPNetwork.Parse("fc00::/7"),
    ];

    // Behind the reverse proxy the socket peer is the Next.js server (auth calls,
    // with the client IP it forwards) or Traefik — not the client. Resolve the
    // client from X-Forwarded-For so the per-IP partitions below are per client.
    // ForwardLimit stays 1: only the entry our own peer appended is used.
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions opts)
    {
        opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // (KnownIPNetworks replaced the obsolete KnownNetworks in ASP.NET Core 10.)
        opts.KnownIPNetworks.Clear();
        opts.KnownProxies.Clear();
        foreach (var network in TrustedProxyNetworks)
            opts.KnownIPNetworks.Add(network);
    }

    // Per client IP for auth, per user for the invoice API
    public static void ConfigurePolicies(RateLimiterOptions opts)
    {
        // Credential and mail-triggering endpoints: tight, against guessing and mail bombing
        opts.AddPolicy(AuthIp, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ClientIp(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // Refresh/logout: every session refreshes each 15 min, several times at once
        // across tabs and server renders. Sharing the 5/min login bucket turned a
        // burst into 429 → forced logout. The tokens are 512-bit random values,
        // so this limit only bounds load, it doesn't guard a guessable secret.
        opts.AddPolicy(AuthSession, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ClientIp(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1)
                }));

        opts.AddPolicy(ApiUser, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.User.FindFirst("sub")?.Value ?? ClientIp(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1)
                }));

        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    }

    private static string ClientIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
}
