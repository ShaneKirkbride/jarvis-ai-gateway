using System.Diagnostics;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using Polly;

namespace Jarvis.AiGateway.Services;

public interface IMessagesOrchestrator
{
    Task<IResult> CreateMessageAsync(HttpContext httpContext, AnthropicMessagesRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates the Anthropic-compatible <c>POST /v1/messages</c> endpoint.  This is a wire-format
/// adapter over the SAME chat pipeline: the Anthropic request is mapped to the provider-neutral
/// chat request, validated, authorized, redacted, and invoked through the existing validator /
/// policy / provider / audit stack, then the result is mapped back to the Anthropic response shape.
/// Text content only for now — image/tool content blocks (multimodal) are deferred and require
/// policy approval.  Streaming is not yet supported.
/// </summary>
public sealed class MessagesOrchestrator : IMessagesOrchestrator
{
    private readonly IUserContextFactory _userContextFactory;
    private readonly IRequestContextFactory _requestContextFactory;
    private readonly IOpenAiChatRequestValidator _validator;
    private readonly IPolicyEngine _policyEngine;
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly IContentRedactor _redactor;
    private readonly IAuditLogger _auditLogger;
    private readonly IOpenAiErrorMapper _errorMapper;
    private readonly IGatewayMetrics _metrics;
    private readonly GatewayOptions _options;
    private readonly ResiliencePipeline _resilience;

    public MessagesOrchestrator(
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
        IOpenAiChatRequestValidator validator,
        IPolicyEngine policyEngine,
        IEnumerable<IAiProvider> providers,
        IContentRedactor redactor,
        IAuditLogger auditLogger,
        IOpenAiErrorMapper errorMapper,
        IGatewayMetrics metrics,
        IOptions<GatewayOptions> options,
        ResiliencePipeline resilience)
    {
        _userContextFactory = userContextFactory;
        _requestContextFactory = requestContextFactory;
        _validator = validator;
        _policyEngine = policyEngine;
        _providers = providers.ToList();
        _redactor = redactor;
        _auditLogger = auditLogger;
        _errorMapper = errorMapper;
        _metrics = metrics;
        _options = options.Value;
        _resilience = resilience;
    }

    public async Task<IResult> CreateMessageAsync(HttpContext httpContext, AnthropicMessagesRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var user = _userContextFactory.Create(httpContext.User);
        var requestContext = _requestContextFactory.Create(httpContext);
        var audit = CreateAudit(user, requestContext, request);

        try
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return AnthropicError(audit, stopwatch, StatusCodes.Status400BadRequest, "The 'model' field is required.");
            }

            if (request.Stream)
            {
                return AnthropicError(audit, stopwatch, StatusCodes.Status400BadRequest, "Streaming is not supported on /v1/messages in this gateway version. Retry with \"stream\": false.");
            }

            if (request.MaxTokens is null or <= 0)
            {
                return AnthropicError(audit, stopwatch, StatusCodes.Status400BadRequest, "The 'max_tokens' field is required and must be positive.");
            }

            if (!TryMapToOpenAi(request, out var openAiRequest, out var mapError))
            {
                return AnthropicError(audit, stopwatch, StatusCodes.Status400BadRequest, mapError!);
            }

            // Reuse the full chat validator (content rules, limits, header checks).
            var validation = _validator.Validate(httpContext, openAiRequest);
            if (!validation.IsValid || validation.AiRequest is null)
            {
                var mapping = _errorMapper.MapValidation(validation);
                return AnthropicError(audit, stopwatch, mapping.StatusCode, mapping.Response.Error.Message, mapping.ErrorType, "validation");
            }

            audit.PromptCharacters = string.Join("\n", validation.AiRequest.Messages.Select(m => m.Content)).Length;

            var decision = await _policyEngine.AuthorizeAsync(user, requestContext, validation.AiRequest, cancellationToken);
            PopulatePolicyAudit(audit, decision);
            if (!decision.Allowed || decision.Model is null)
            {
                _metrics.RecordPolicyDenial(decision.RuleId, request.Model);
                var mapping = _errorMapper.MapPolicyDenied(decision);
                return AnthropicError(audit, stopwatch, mapping.StatusCode, mapping.Response.Error.Message, mapping.ErrorType, "policy");
            }

            // Re-validate against the resolved model (e.g. max_tokens vs the model's limit).
            validation = _validator.Validate(httpContext, openAiRequest, decision.Model);
            if (!validation.IsValid || validation.AiRequest is null)
            {
                var mapping = _errorMapper.MapValidation(validation);
                return AnthropicError(audit, stopwatch, mapping.StatusCode, mapping.Response.Error.Message, mapping.ErrorType, "validation");
            }

            var provider = ResolveProvider(decision.Model);
            _metrics.RecordRequest(request.Model);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProviderTimeoutSeconds)));

            AiChatResult result;
            try
            {
                result = await _resilience.ExecuteAsync(
                    async ct => await provider.CompleteAsync(decision.Model, validation.AiRequest, requestContext, ct),
                    timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new ProviderTimeoutException("Provider invocation timed out.", ex);
            }

            var text = result.Text;
            if (_options.Redaction.Enabled)
            {
                var redacted = _redactor.Redact(text);
                text = redacted.Text;
                audit.RedactionCount += redacted.RedactionCount;
            }

            var response = MapResponse(decision.Model.Id, result, text);
            PopulateSuccessAudit(audit, result, stopwatch.ElapsedMilliseconds);
            _auditLogger.Write(audit);
            _metrics.RecordTokenUsage(request.Model, result.Usage.PromptTokens, result.Usage.CompletionTokens);
            _metrics.RecordLatency(request.Model, stopwatch.Elapsed);
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            var mapping = _errorMapper.MapException(ex);
            audit.Decision = mapping.ErrorCategory == "cancellation" ? "CANCELLED" : "ERROR";
            audit.DenyReason = mapping.Response.Error.Message;
            audit.ErrorType = mapping.ErrorType;
            audit.ErrorCategory = mapping.ErrorCategory;
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            _auditLogger.Write(audit);
            _metrics.RecordBedrockError(request.Model ?? string.Empty);
            _metrics.RecordServerError("messages");
            return Results.Json(ToAnthropic(mapping.StatusCode, mapping.Response.Error.Message), statusCode: mapping.StatusCode);
        }
    }

    private bool TryMapToOpenAi(AnthropicMessagesRequest request, out OpenAiChatCompletionRequest openAi, out string? error)
    {
        error = null;
        openAi = new OpenAiChatCompletionRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Stream = false
        };

        if (request.System is { } system && system.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (!TryExtractText(system, out var systemText))
            {
                error = "Unsupported 'system' content; only text is supported.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(systemText))
            {
                openAi.Messages.Add(new OpenAiMessage { Role = "system", Content = JsonSerializer.SerializeToElement(systemText) });
            }
        }

        foreach (var message in request.Messages)
        {
            if (!TryExtractText(message.Content, out var text))
            {
                // Image / tool-use content blocks are not supported yet (multimodal deferred).
                error = "Unsupported message content; only text content is supported on this gateway.";
                return false;
            }

            openAi.Messages.Add(new OpenAiMessage { Role = message.Role, Content = JsonSerializer.SerializeToElement(text) });
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            openAi.Stop = JsonSerializer.SerializeToElement(stops);
        }

        return true;
    }

    private static bool TryExtractText(JsonElement content, out string text)
    {
        text = string.Empty;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? string.Empty;
            return true;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !block.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase) ||
                    !block.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                parts.Add(textEl.GetString() ?? string.Empty);
            }

            text = string.Join("\n", parts);
            return true;
        }

        return false;
    }

    private IAiProvider ResolveProvider(GatewayModel model)
    {
        var match = _providers.FirstOrDefault(p => p.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        if (_providers.Count == 1) return _providers[0];
        throw new NotSupportedException($"No AI provider is registered for ProviderName '{model.ProviderName}'.");
    }

    private static AnthropicMessagesResponse MapResponse(string modelId, AiChatResult result, string text) => new()
    {
        Model = modelId,
        Content = [new AnthropicContentBlock { Type = "text", Text = text }],
        StopReason = MapStopReason(result.FinishReason),
        Usage = new AnthropicUsage { InputTokens = result.Usage.PromptTokens, OutputTokens = result.Usage.CompletionTokens }
    };

    private static string MapStopReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "max_tokens" or "length" => "max_tokens",
        "tool_use" or "tool_calls" => "tool_use",
        "stop_sequence" => "stop_sequence",
        _ => "end_turn"
    };

    private IResult AnthropicError(GatewayAuditEvent audit, Stopwatch stopwatch, int status, string message, string? errorType = null, string? category = null)
    {
        audit.Decision = category == "policy" ? "DENY" : "ERROR";
        audit.DenyReason = message;
        audit.ErrorType = errorType;
        audit.ErrorCategory = category;
        audit.LatencyMs = stopwatch.ElapsedMilliseconds;
        _auditLogger.Write(audit);
        return Results.Json(ToAnthropic(status, message), statusCode: status);
    }

    private static AnthropicErrorResponse ToAnthropic(int status, string message) =>
        AnthropicErrorResponse.Create(status switch
        {
            StatusCodes.Status400BadRequest => "invalid_request_error",
            StatusCodes.Status401Unauthorized => "authentication_error",
            StatusCodes.Status403Forbidden => "permission_error",
            StatusCodes.Status404NotFound => "not_found_error",
            StatusCodes.Status429TooManyRequests => "rate_limit_error",
            _ => "api_error"
        }, message);

    private GatewayAuditEvent CreateAudit(UserContext user, RequestContext requestContext, AnthropicMessagesRequest request) => new()
    {
        EventType = "AI_MESSAGES",
        RequestId = requestContext.RequestId,
        CorrelationId = requestContext.CorrelationId,
        UserSubject = user.Subject,
        UserEmail = user.Email,
        UserGroups = user.Groups.ToArray(),
        WorkspaceId = requestContext.WorkspaceId,
        DataLabel = requestContext.DataLabel,
        ItarMode = requestContext.ItarMode,
        RequestedModelAlias = request.Model ?? string.Empty,
        Region = _options.AwsRegion,
        AuthType = user.Claims.GetValueOrDefault("jarvis_auth_type"),
        ApiKeyId = user.Claims.GetValueOrDefault("jarvis:apikey_id")
    };

    private static void PopulatePolicyAudit(GatewayAuditEvent audit, PolicyDecision decision)
    {
        audit.Decision = decision.Allowed ? "ALLOW" : "DENY";
        audit.DenyReason = decision.Allowed ? null : decision.Reason;
        audit.PolicyRuleId = decision.RuleId;
        audit.PolicyDecision = decision.Reason;
        audit.ResolvedBedrockModelId = decision.Model?.BedrockModelId;
        audit.Provider = decision.Model?.ProviderName ?? "aws-bedrock";
    }

    private static void PopulateSuccessAudit(GatewayAuditEvent audit, AiChatResult result, long latencyMs)
    {
        audit.Decision = "ALLOW";
        audit.InputTokens = result.Usage.PromptTokens;
        audit.OutputTokens = result.Usage.CompletionTokens;
        audit.TotalTokens = result.Usage.TotalTokens;
        audit.TokenEstimate = result.Usage.TotalTokens;
        audit.LatencyMs = latencyMs;
        audit.ProviderLatencyMs = result.ProviderMetadata.LatencyMs;
        audit.ProviderRequestId = result.ProviderMetadata.ProviderRequestId;
        audit.Provider = result.ProviderMetadata.Provider;
        audit.InvocationStrategy = result.ProviderMetadata.InvocationStrategy;
    }
}
