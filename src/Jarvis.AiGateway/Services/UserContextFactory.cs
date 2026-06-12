using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
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
        foreach (var claimName in _options.GroupClaimNames)
        {
            foreach (var claim in principal.FindAll(claimName))
            {
                AddGroupValues(groups, claim.Value);
            }
        }

        foreach (var role in principal.FindAll(ClaimTypes.Role))
        {
            AddGroupValues(groups, role.Value);
        }

        var claims = principal.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(c => c.Value)), StringComparer.OrdinalIgnoreCase);

        return new UserContext(subject, email, groups, claims);
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
