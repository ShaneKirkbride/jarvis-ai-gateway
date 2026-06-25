using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Security;

public sealed class ServiceApiKeyMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
{
    // Authentication type stamped on the service principal.  A ClaimsIdentity with a
    // non-null authentication type reports IsAuthenticated=true, which is what satisfies
    // RequireAuthorization() for the machine-to-machine (Open WebUI → Envoy → gateway) path.
    public const string AuthenticationScheme = "JarvisServiceApiKey";

    // Stable identity for the Open WebUI service caller.  Used as NameIdentifier so the
    // rate limiter and audit have a consistent subject for service-to-service traffic.
    public const string ServiceSubject = "openwebui-service";
    public const string ServiceDisplayName = "OpenWebUI Service";

    // Marks how the principal authenticated so downstream code and audit can distinguish a
    // service-key caller from a user identity established by JWT or the identity broker.
    public const string AuthTypeClaimType = "jarvis_auth_type";
    public const string ServiceApiKeyAuthType = "service_api_key";

    private readonly GatewayOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequireServiceApiKey)
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/healthz") || context.Request.Path.StartsWithSegments("/readyz"))
        {
            await next(context);
            return;
        }

        // A developer API key (validated upstream by DeveloperApiKeyMiddleware) is a first-class
        // user credential and satisfies gateway entry on its own — it does NOT also require the
        // service-to-service key. The flag is set server-side after a validated lookup, so this is
        // not a bypass a client can trigger.
        if (DeveloperApiKeyContext.IsAuthenticated(context))
        {
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ServiceApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Gateway service API key is required but not configured." });
            return;
        }

        if (!context.Request.Headers.TryGetValue(_options.ServiceApiKeyHeader, out var provided) ||
            !FixedTimeEquals(provided.ToString(), _options.ServiceApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid service-to-service gateway key." });
            return;
        }

        // Valid key: establish an authenticated service principal so RequireAuthorization()
        // is satisfied for service-to-service calls.  When a user identity is also present,
        // the downstream UseAuthentication() (legacy JwtBearer) or IdentityBrokerMiddleware
        // overwrites this with the richer user principal, so user context still wins — the
        // service identity is the authorization floor, not a ceiling.
        context.User = CreateServicePrincipal(_options.ServiceApiKeyGroups);

        await next(context);
    }

    // Groups are emitted as jarvis:group_name claims — the same claim type the identity
    // broker uses for display-name groups — so UserContextFactory picks them up via its
    // unconditional EnumerateGroupClaimNames() path regardless of GroupClaimNames config.
    internal static ClaimsPrincipal CreateServicePrincipal(IReadOnlyList<string> groups)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ServiceSubject),
            new(ClaimTypes.Name, ServiceDisplayName),
            new(AuthTypeClaimType, ServiceApiKeyAuthType)
        };

        foreach (var group in groups)
        {
            if (!string.IsNullOrWhiteSpace(group))
            {
                claims.Add(new Claim(IdentityBrokerMiddleware.GroupNameClaim, group));
            }
        }

        var identity = new ClaimsIdentity(claims,
            authenticationType: AuthenticationScheme,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
