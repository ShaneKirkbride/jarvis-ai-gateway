using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class AzureOpenAiDiagnosticsTests
{
    [Fact]
    public void Parses_code_and_message_from_azure_error_body()
    {
        const string body = """{"error":{"code":"unsupported_parameter","message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.","param":"max_tokens"}}""";

        var info = AzureOpenAiDiagnostics.ParseError(body);

        Assert.Equal("unsupported_parameter", info.Code);
        Assert.Contains("max_completion_tokens", info.Message);
    }

    [Fact]
    public void Redacts_secrets_embedded_in_error_message()
    {
        const string body = """{"error":{"code":"bad","message":"api-key: leaked-secret-value rejected"}}""";

        var info = AzureOpenAiDiagnostics.ParseError(body);

        Assert.DoesNotContain("leaked-secret-value", info.Message);
        Assert.Contains("[REDACTED]", info.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"something\":\"else\"}")]
    [InlineData("\"a string\"")]
    public void Returns_nulls_for_unparseable_or_unexpected_body(string body)
    {
        var info = AzureOpenAiDiagnostics.ParseError(body);

        Assert.Null(info.Code);
        Assert.Null(info.Message);
    }
}
