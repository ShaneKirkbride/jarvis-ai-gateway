using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Pins the security invariant that policy authorization keys on Entra group object IDs,
/// not on display names.  A user with a matching display name but a mismatching ID must
/// be denied — this is the regression guard for the broker's group-ID design.
/// </summary>
public sealed class PolicyEngineGroupIdAuthorizationTests
{
    private const string EngineeringId = "00000000-0000-0000-0000-000000000001";
    private const string OtherId = "00000000-0000-0000-0000-000000000099";

    [Fact]
    public void User_with_matching_group_id_is_allowed()
    {
        var model = new GatewayModel { RequiredGroupIds = [EngineeringId] };
        var user = NewUser(groupIds: [EngineeringId], groups: []);

        Assert.True(PolicyEngine.IsUserInAllowedGroup(user, model));
    }

    [Fact]
    public void User_with_matching_display_name_but_mismatching_id_is_denied()
    {
        // The display name "Engineering" matches model.RequiredGroups, BUT RequiredGroupIds
        // is non-empty so display names are ignored entirely.  The user's IDs do not
        // match the required ID — deny.
        var model = new GatewayModel
        {
            RequiredGroupIds = [EngineeringId],
            RequiredGroups = ["Engineering"]
        };
        var user = NewUser(groupIds: [OtherId], groups: ["Engineering"]);

        Assert.False(PolicyEngine.IsUserInAllowedGroup(user, model));
    }

    [Fact]
    public void Legacy_display_name_match_works_when_no_group_ids_configured()
    {
        var model = new GatewayModel { RequiredGroups = ["Engineering"] };
        var user = NewUser(groupIds: [], groups: ["Engineering"]);

        Assert.True(PolicyEngine.IsUserInAllowedGroup(user, model));
    }

    [Fact]
    public void No_group_requirements_allows_any_user()
    {
        var model = new GatewayModel();   // both lists empty
        var user = NewUser(groupIds: [], groups: []);

        Assert.True(PolicyEngine.IsUserInAllowedGroup(user, model));
    }

    [Fact]
    public void Itar_model_without_required_group_ids_fails_options_validation()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                AuditSubjectSalt = "salt",
                OwuiSessionJwt = new OwuiSessionJwtOptions { Enabled = true, SigningKey = "key-key-key-key-key-padding" },
                Graph = new GraphOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" }
            },
            Models =
            [
                new ModelRouteOptions
                {
                    Alias = "itar-bad",
                    BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
                    Enabled = true,
                    ItarApproved = true,
                    AllowedGroups = ["ITAR-Approved"]   // display names only — not allowed for ITAR
                }
            ]
        };

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("ITAR") && f.Contains("itar-bad"));
    }

    [Fact]
    public void Non_guid_in_group_id_list_fails_options_validation()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                AuditSubjectSalt = "salt",
                OwuiSessionJwt = new OwuiSessionJwtOptions { Enabled = true, SigningKey = "key-key-key-key-key-padding" },
                Graph = new GraphOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" }
            },
            Models =
            [
                new ModelRouteOptions
                {
                    Alias = "bad-id",
                    BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
                    Enabled = true,
                    AllowedGroupIds = ["not-a-guid"]
                }
            ]
        };

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("non-GUID") || f.Contains("not-a-guid"));
    }

    private static UserContext NewUser(IEnumerable<string> groupIds, IEnumerable<string> groups) =>
        new UserContext(
            "user@example.test",
            "user@example.test",
            new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>())
        {
            GroupIds = new HashSet<string>(groupIds, StringComparer.OrdinalIgnoreCase)
        };
}
