using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class SubjectHasherTests
{
    [Fact]
    public void Same_input_with_same_salt_produces_same_hash()
    {
        var hasher = new SubjectHasher(MsOptions.Create(WithSalt("salt-1")));

        var h1 = hasher.Hash("user@example.test");
        var h2 = hasher.Hash("user@example.test");

        Assert.NotNull(h1);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Different_salt_produces_different_hash()
    {
        var a = new SubjectHasher(MsOptions.Create(WithSalt("salt-A"))).Hash("user@example.test");
        var b = new SubjectHasher(MsOptions.Create(WithSalt("salt-B"))).Hash("user@example.test");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_is_case_insensitive_on_value()
    {
        var hasher = new SubjectHasher(MsOptions.Create(WithSalt("salt-1")));

        Assert.Equal(hasher.Hash("USER@example.test"), hasher.Hash("user@example.test"));
    }

    [Fact]
    public void Null_or_empty_value_returns_null()
    {
        var hasher = new SubjectHasher(MsOptions.Create(WithSalt("salt-1")));
        Assert.Null(hasher.Hash(null));
        Assert.Null(hasher.Hash(""));
    }

    [Fact]
    public void Missing_salt_returns_null()
    {
        var hasher = new SubjectHasher(MsOptions.Create(WithSalt(null)));
        Assert.Null(hasher.Hash("user@example.test"));
    }

    [Fact]
    public void Hash_output_is_lowercase_hex_64_chars()
    {
        var hasher = new SubjectHasher(MsOptions.Create(WithSalt("salt-1")));
        var hash = hasher.Hash("user@example.test");

        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    private static GatewayOptions WithSalt(string? salt) => new()
    {
        IdentityBroker = new IdentityBrokerOptions { AuditSubjectSalt = salt }
    };
}
