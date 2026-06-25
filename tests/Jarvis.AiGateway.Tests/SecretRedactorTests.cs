using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJ.payload.sig", "Bearer [REDACTED]")]
    [InlineData("api-key: super-secret-value", "api-key: [REDACTED]")]
    [InlineData("\"api_key\":\"abc123\"", "[REDACTED]")]
    [InlineData("client_secret=topsecret", "[REDACTED]")]
    [InlineData("signing-key = aaaa.bbbb", "[REDACTED]")]
    [InlineData("X-Jarvis-Gateway-Key: svc-key-123", "[REDACTED]")]
    [InlineData("password=hunter2", "[REDACTED]")]
    public void Redacts_secret_values(string input, string expectedContains)
    {
        var result = SecretRedactor.Redact(input);

        Assert.Contains(expectedContains, result);
        Assert.DoesNotContain("super-secret-value", result);
        Assert.DoesNotContain("topsecret", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("svc-key-123", result);
        Assert.DoesNotContain("abc123", result);
    }

    [Fact]
    public void Leaves_non_secret_text_unchanged()
    {
        const string input = "Unsupported parameter: 'max_tokens' is not supported with this model.";
        Assert.Equal(input, SecretRedactor.Redact(input));
    }

    [Fact]
    public void Handles_null_and_empty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }
}
