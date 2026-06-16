using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class OwuiTrustedHeaderValidatorTests
{
    [Fact]
    public async Task Email_header_present_returns_normalized_email()
    {
        var validator = CreateValidator();
        var input = NewInput(("X-OpenWebUI-User-Email", "  Alice@Example.Test  "));

        var result = await validator.ValidateAsync(input, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("alice@example.test", result.Email);
        Assert.Equal(OwuiTrustedHeaderValidator.Kind, result.AssertionKind);
    }

    [Fact]
    public async Task Email_header_missing_returns_weak_identity()
    {
        var validator = CreateValidator();
        var input = NewInput();

        var result = await validator.ValidateAsync(input, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenWeakIdentity, result.FailureReason);
    }

    [Fact]
    public async Task Disabled_validator_fails_closed()
    {
        var validator = CreateValidator(options => options.Enabled = false);
        var input = NewInput(("X-OpenWebUI-User-Email", "alice@example.test"));

        var result = await validator.ValidateAsync(input, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Can_handle_returns_true_only_when_email_header_present_and_enabled()
    {
        var validator = CreateValidator();

        Assert.True(validator.CanHandle(NewInput(("X-OpenWebUI-User-Email", "alice@example.test"))));
        Assert.False(validator.CanHandle(NewInput()));
        Assert.False(validator.CanHandle(NewInput(("X-OpenWebUI-User-Email", ""))));
    }

    [Fact]
    public void Can_handle_returns_false_when_validator_disabled_even_with_email_header()
    {
        var validator = CreateValidator(options => options.Enabled = false);
        Assert.False(validator.CanHandle(NewInput(("X-OpenWebUI-User-Email", "alice@example.test"))));
    }

    [Fact]
    public async Task Custom_header_names_are_honored()
    {
        var validator = CreateValidator(options =>
        {
            options.EmailHeader = "X-Corp-User-Email";
        });
        var input = NewInput(("X-Corp-User-Email", "alice@example.test"));

        var result = await validator.ValidateAsync(input, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("alice@example.test", result.Email);
    }

    [Fact]
    public async Task Malformed_email_too_long_returns_weak_identity()
    {
        var validator = CreateValidator();
        var input = NewInput(("X-OpenWebUI-User-Email", new string('a', 300) + "@example.test"));

        var result = await validator.ValidateAsync(input, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenWeakIdentity, result.FailureReason);
    }

    [Fact]
    public async Task Upn_header_picked_up_when_present()
    {
        var validator = CreateValidator();
        var input = NewInput(
            ("X-OpenWebUI-User-Email", "alice@example.test"),
            ("X-OpenWebUI-User-Upn", "alice.principal@example.test"));

        var result = await validator.ValidateAsync(input, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("alice.principal@example.test", result.Upn);
    }

    private static OwuiTrustedHeaderValidator CreateValidator(Action<OwuiTrustedHeaderOptions>? configure = null)
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                OwuiTrustedHeader = new OwuiTrustedHeaderOptions { Enabled = true }
            }
        };
        configure?.Invoke(options.IdentityBroker.OwuiTrustedHeader);
        return new OwuiTrustedHeaderValidator(MsOptions.Create(options));
    }

    private static IdentityAssertionInput NewInput(params (string Key, string Value)[] headers)
    {
        var bag = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            bag[key] = value;
        }
        return new IdentityAssertionInput(null, bag);
    }
}
