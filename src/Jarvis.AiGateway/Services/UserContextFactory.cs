using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IUserContextFactory
{
    UserContext Create(ClaimsPrincipal principal);
}

public sealed class UserContextFactory(IOptions<GatewayOptions> options) : IUserContextFactory
{
    private readonly GatewayOptions _options = options.Value;

    public UserContext Create(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity?.Name
            ?? "unknown";

        var email = _options.EmailClaimNames
            .Select(name => principal.FindFirstValue(name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? subject;

        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Display-name groups (legacy path).  When the identity broker is active these come
        // from the broker's jarvis:group_name claims; when JwtBearer is active they come
        // from the configured IdP claim names.  Either way they feed the legacy
        // RequiredGroups path that is being phased out.
        foreach (var name in EnumerateGroupClaimNames())
        {
            foreach (var claim in principal.FindAll(name))
            {
                AddGroupValues(groups, claim.Value);
            }
        }

        // Roles are how the broker exposes group object IDs to standard ASP.NET role-based
        // authorization.  We pull them into both Groups (legacy) AND GroupIds (the modern
        // policy input) so existing code continues to compile while new code prefers IDs.
        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in principal.FindAll(ClaimTypes.Role))
        {
            AddGroupValues(groups, role.Value);
            AddGroupValues(groupIds, role.Value);
        }

        // Belt-and-suspenders: the broker also writes a dedicated jarvis:group_id claim per
        // resolved group.  Reading that claim directly means policy authorization works even
        // if a future change reshuffles role claims.
        foreach (var claim in principal.FindAll(IdentityBrokerMiddleware.GroupIdClaim))
        {
            AddGroupValues(groupIds, claim.Value);
        }

        var claims = principal.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(c => c.Value)), StringComparer.OrdinalIgnoreCase);

        return new UserContext(subject, email, groups, claims) { GroupIds = groupIds };
    }

    private IEnumerable<string> EnumerateGroupClaimNames()
    {
        foreach (var name in _options.GroupClaimNames)
        {
            yield return name;
        }
        // Always look at the broker's display-name claim too — it is independent of the
        // configured IdP claim names and is what the broker actually writes.
        yield return IdentityBrokerMiddleware.GroupNameClaim;
    }

    private static void AddGroupValues(HashSet<string> groups, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        // Handles either a single group claim or a JSON-ish / comma-separated group list.
        foreach (var part in value.Trim('[', ']', '"').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            groups.Add(part.Trim('"'));
        }
    }
}
