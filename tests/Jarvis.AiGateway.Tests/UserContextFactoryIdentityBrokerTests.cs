using System.Security.Claims;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Confirms UserContextFactory correctly carries the broker's jarvis:group_id and
/// jarvis:group_name claims into UserContext.GroupIds and UserContext.Groups respectively
/// so the policy engine sees the right inputs.
/// </summary>
public sealed class UserContextFactoryIdentityBrokerTests
{
    [Fact]
    public void Group_id_claims_populate_group_ids_set()
    {
        var principal = NewPrincipal(claims:
        [
            new(IdentityBrokerMiddleware.GroupIdClaim, "00000000-0000-0000-0000-000000000001"),
            new(IdentityBrokerMiddleware.GroupIdClaim, "00000000-0000-0000-0000-000000000002")
        ]);

        var ctx = NewFactory().Create(principal);

        Assert.Contains("00000000-0000-0000-0000-000000000001", ctx.GroupIds);
        Assert.Contains("00000000-0000-0000-0000-000000000002", ctx.GroupIds);
    }

    [Fact]
    public void Group_name_claims_populate_groups_set_separately_from_ids()
    {
        var principal = NewPrincipal(claims:
        [
            new(IdentityBrokerMiddleware.GroupIdClaim, "00000000-0000-0000-0000-000000000001"),
            new(IdentityBrokerMiddleware.GroupNameClaim, "Engineering")
        ]);

        var ctx = NewFactory().Create(principal);

        Assert.Contains("Engineering", ctx.Groups);
        Assert.DoesNotContain("Engineering", ctx.GroupIds);
        Assert.Contains("00000000-0000-0000-0000-000000000001", ctx.GroupIds);
    }

    [Fact]
    public void Role_claims_are_dual_added_to_groups_and_group_ids()
    {
        var principal = NewPrincipal(claims:
        [
            new(ClaimTypes.Role, "00000000-0000-0000-0000-000000000001")
        ]);

        var ctx = NewFactory().Create(principal);

        Assert.Contains("00000000-0000-0000-0000-000000000001", ctx.Groups);
        Assert.Contains("00000000-0000-0000-0000-000000000001", ctx.GroupIds);
    }

    [Fact]
    public void Legacy_idp_group_claim_still_populates_groups()
    {
        var principal = NewPrincipal(claims:
        [
            new("groups", "AI-General-Users,AI-Admin")
        ]);

        var ctx = NewFactory().Create(principal);

        Assert.Contains("AI-General-Users", ctx.Groups);
        Assert.Contains("AI-Admin", ctx.Groups);
        Assert.Empty(ctx.GroupIds);
    }

    [Fact]
    public void Subject_falls_back_through_name_identifier_then_identity_name()
    {
        var withSub = NewPrincipal(claims: [new("sub", "subject-1")]);
        var withNameId = NewPrincipal(claims: [new(ClaimTypes.NameIdentifier, "subject-2")]);
        var noClaims = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Equal("subject-1", NewFactory().Create(withSub).Subject);
        Assert.Equal("subject-2", NewFactory().Create(withNameId).Subject);
        Assert.Equal("unknown", NewFactory().Create(noClaims).Subject);
    }

    private static UserContextFactory NewFactory() =>
        new(MsOptions.Create(new GatewayOptions()));

    private static ClaimsPrincipal NewPrincipal(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        return new ClaimsPrincipal(identity);
    }
}
