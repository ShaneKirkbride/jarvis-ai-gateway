using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class ValidationErrorMapperAndTimeoutTests
{
    [Fact]
    public void Validator_rejects_unsupported_content_and_limits_before_provider_invocation()
    {
        var validator = new OpenAiChatRequestValidator(MsOptions.Create(new GatewayOptions
        {
            RequestLimits = new RequestLimitOptions
            {
                MaxGatewayHeaderLength = 4,
                MaxMetadataEntries = 1,
                MaxMetadataKeyLength = 8,
                MaxMetadataValueLength = 8,
                MaxStopSequenceCount = 1,
                MaxStopSequenceLength = 4,
                MaxMessageCount = 2
            }
        }));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Jarvis-Workspace-Id"] = "too-long";
        var request = new OpenAiChatCompletionRequest
        {
            Model = "general",
            Temperature = 1.5F,
            TopP = -0.1F,
            MaxTokens = 0,
            Stop = JsonSerializer.SerializeToElement(new[] { "valid", "second" }),
            Metadata = new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement("value"), ["other"] = JsonSerializer.SerializeToElement("value") },
            Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new { type = "image_url", image_url = new { url = "https://example.invalid/a.png" } }) }]
        };

        var result = validator.Validate(httpContext, request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "unsupported_content");
        Assert.Contains(result.Errors, e => e.Code == "header_too_large");
        Assert.Contains(result.Errors, e => e.Code == "temperature_out_of_range");
        Assert.Contains(result.Errors, e => e.Code == "top_p_out_of_range");
        Assert.Contains(result.Errors, e => e.Code == "max_tokens_invalid");
        Assert.Contains(result.Errors, e => e.Code == "stop_too_many");
        Assert.Contains(result.Errors, e => e.Code == "metadata_too_many");
    }

    [Fact]
    public void Validator_rejects_max_tokens_above_resolved_model_limit()
    {
        var validator = new OpenAiChatRequestValidator(MsOptions.Create(new GatewayOptions()));
        var request = new OpenAiChatCompletionRequest
        {
            Model = "general",
            MaxTokens = 20,
            Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }]
        };
        var model = new GatewayModel { Id = "general", Alias = "general", MaxOutputTokens = 10 };

        var result = validator.Validate(new DefaultHttpContext(), request, model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "max_tokens_exceeds_model_limit");
    }

    [Fact]
    public void Error_mapper_returns_non_leaky_openai_compatible_responses()
    {
        var mapper = new OpenAiErrorMapper();
        var timeout = mapper.MapException(new ProviderTimeoutException("secret provider detail"));
        var unsupported = mapper.MapException(new NotSupportedException("raw adapter detail"));
        var policy = mapper.MapPolicyDenied(new PolicyDecision(false, "denied", null) { RuleId = PolicyRuleIds.UserGroupDenied });

        Assert.Equal(StatusCodes.Status504GatewayTimeout, timeout.StatusCode);
        Assert.Equal("provider_timeout", timeout.Response.Error.Code);
        Assert.DoesNotContain("secret", timeout.Response.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusCodes.Status501NotImplemented, unsupported.StatusCode);
        Assert.Equal("unsupported_model", unsupported.Response.Error.Code);
        Assert.DoesNotContain("raw", unsupported.Response.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusCodes.Status403Forbidden, policy.StatusCode);
        Assert.Equal(PolicyRuleIds.UserGroupDenied, policy.Response.Error.Code);
        // Generic message — must not reveal internal policy state to callers.
        Assert.Equal("Request is not allowed by policy.", policy.Response.Error.Message);
    }

    [Fact]
    public void Error_mapper_returns_502_for_provider_response_parse_exception()
    {
        var mapper = new OpenAiErrorMapper();

        var result = mapper.MapException(new ProviderResponseParseException("internal bedrock payload detail"));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.Equal("provider_parse_error", result.Response.Error.Code);
        Assert.DoesNotContain("internal", result.Response.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bedrock", result.Response.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gateway_options_validator_fails_invalid_regex_at_startup()
    {
        var options = new GatewayOptions
        {
            BlockedPromptPatterns = ["["],
            Policy = new GatewayPolicyOptions { AllowedModelIdPatterns = ["*"] }
        };

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("Gateway:BlockedPromptPatterns[0]"));
        Assert.Contains(result.Failures!, f => f.Contains("Gateway:Policy:AllowedModelIdPatterns[0]"));
    }
}
