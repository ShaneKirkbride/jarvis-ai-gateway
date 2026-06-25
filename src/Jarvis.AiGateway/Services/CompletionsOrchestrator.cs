using System.Diagnostics;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using Polly;

namespace Jarvis.AiGateway.Services;

public interface ICompletionsOrchestrator
{
    Task<IResult> CompleteAsync(HttpContext httpContext, OpenAiCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates <c>POST /v1/completions</c> (text completion / fill-in-the-middle for IDE
/// autocomplete).  This is the highest-egress surface — it can carry surrounding file context on
/// every keystroke-batch — so it is the most constrained: autocomplete is DISABLED for ITAR
/// requests by default, the prompt+suffix is hard-capped, and the request still runs through the
/// full group/ITAR/blocked-pattern/prompt-size policy plus inbound redaction.  Capability-gated:
/// the model must be configured SupportsFim and routed to an <see cref="ICompletionProvider"/>.
/// </summary>
public sealed class CompletionsOrchestrator : ICompletionsOrchestrator
{
    private readonly IUserContextFactory _userContextFactory;
    private readonly IRequestContextFactory _requestContextFactory;
    private readonly IPolicyEngine _policyEngine;
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly IContentRedactor _redactor;
    private readonly IAuditLogger _auditLogger;
    private readonly IOpenAiErrorMapper _errorMapper;
    private readonly IGatewayMetrics _metrics;
    private readonly GatewayOptions _options;
    private readonly ResiliencePipeline _resilience;

    public CompletionsOrchestrator(
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
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
        _policyEngine = policyEngine;
        _providers = providers.ToList();
        _redactor = redactor;
        _auditLogger = auditLogger;
        _errorMapper = errorMapper;
        _metrics = metrics;
        _options = options.Value;
        _resilience = resilience;
    }

    public async Task<IResult> CompleteAsync(HttpContext httpContext, OpenAiCompletionRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var user = _userContextFactory.Create(httpContext.User);
        var requestContext = _requestContextFactory.Create(httpContext);
        var audit = CreateAudit(user, requestContext, request);

        try
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return WriteMapped(audit, stopwatch, _errorMapper.MapValidation(Fail("model_required", "The 'model' field is required.", "model")));
            }

            // Autocomplete is disabled for ITAR requests by default — fail closed BEFORE any egress.
            if (requestContext.ItarMode && _options.Completions.DisableForItar)
            {
                var denied = new PolicyDecision(false, "Autocomplete is disabled for ITAR workspaces.", null, "AUTOCOMPLETE_DISABLED_FOR_ITAR");
                _metrics.RecordPolicyDenial(denied.RuleId, request.Model);
                return WriteMapped(audit, stopwatch, _errorMapper.MapPolicyDenied(denied));
            }

            if (!TryParsePrompt(request.Prompt, out var prompt, out var promptError))
            {
                return WriteMapped(audit, stopwatch, _errorMapper.MapValidation(promptError!));
            }

            var contextLength = prompt.Length + (request.Suffix?.Length ?? 0);
            audit.PromptCharacters = contextLength;
            if (contextLength > _options.Completions.MaxContextCharacters)
            {
                return WriteMapped(audit, stopwatch, _errorMapper.MapValidation(
                    Fail("context_too_large", $"The completion prompt+suffix exceeds the {_options.Completions.MaxContextCharacters}-character limit. Send a smaller, selected context.", "prompt")));
            }

            var aiRequest = new AiCompletionRequest(request.Model, prompt, request.Suffix, request.MaxTokens, request.Temperature, request.TopP, []);
            var decision = await _policyEngine.AuthorizeCompletionAsync(user, requestContext, aiRequest, cancellationToken);
            PopulatePolicyAudit(audit, decision);
            if (!decision.Allowed || decision.Model is null)
            {
                _metrics.RecordPolicyDenial(decision.RuleId, request.Model);
                return WriteMapped(audit, stopwatch, _errorMapper.MapPolicyDenied(decision));
            }

            var provider = ResolveProvider(decision.Model);
            if (provider is not ICompletionProvider completionProvider)
            {
                throw new NotSupportedException("Completions are not supported for the requested model's provider.");
            }

            _metrics.RecordRequest(request.Model);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProviderTimeoutSeconds)));

            AiCompletionResult result;
            try
            {
                result = await _resilience.ExecuteAsync(
                    async ct => await completionProvider.CompleteTextAsync(decision.Model, aiRequest, requestContext, ct),
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

            var response = new OpenAiCompletionResponse
            {
                Model = decision.Model.Id,
                Choices = [new OpenAiCompletionChoice { Index = 0, Text = text, FinishReason = NormalizeFinishReason(result.FinishReason) }],
                Usage = new OpenAiUsage
                {
                    PromptTokens = result.Usage.PromptTokens,
                    CompletionTokens = result.Usage.CompletionTokens,
                    TotalTokens = result.Usage.TotalTokens == 0 ? result.Usage.PromptTokens + result.Usage.CompletionTokens : result.Usage.TotalTokens
                }
            };

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
            _metrics.RecordServerError("completions");
            return Results.Json(mapping.Response, statusCode: mapping.StatusCode);
        }
    }

    private static bool TryParsePrompt(JsonElement prompt, out string value, out OpenAiChatValidationResult? error)
    {
        value = string.Empty;
        error = null;

        if (prompt.ValueKind == JsonValueKind.String)
        {
            value = prompt.GetString() ?? string.Empty;
        }
        else if (prompt.ValueKind == JsonValueKind.Array && prompt.GetArrayLength() == 1 && prompt[0].ValueKind == JsonValueKind.String)
        {
            // Autocomplete is single-prompt; batched/array prompts are not supported.
            value = prompt[0].GetString() ?? string.Empty;
        }
        else
        {
            error = Fail("unsupported_prompt", "The 'prompt' field must be a single string (token-array and batched prompts are not supported).", "prompt");
            return false;
        }

        if (string.IsNullOrEmpty(value))
        {
            error = Fail("prompt_required", "The 'prompt' field must not be empty.", "prompt");
            return false;
        }

        return true;
    }

    private IAiProvider ResolveProvider(GatewayModel model)
    {
        var match = _providers.FirstOrDefault(p => p.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        if (_providers.Count == 1) return _providers[0];
        throw new NotSupportedException($"No AI provider is registered for ProviderName '{model.ProviderName}'.");
    }

    private static string NormalizeFinishReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "stop";
        return reason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) || reason.Equals("length", StringComparison.OrdinalIgnoreCase) ? "length" : "stop";
    }

    private GatewayAuditEvent CreateAudit(UserContext user, RequestContext requestContext, OpenAiCompletionRequest request) => new()
    {
        EventType = "AI_COMPLETION",
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

    private IResult WriteMapped(GatewayAuditEvent audit, Stopwatch stopwatch, OpenAiErrorMapping mapping)
    {
        audit.Decision = mapping.ErrorCategory == "policy" ? "DENY" : "ERROR";
        audit.DenyReason = mapping.Response.Error.Message;
        audit.ErrorType = mapping.ErrorType;
        audit.ErrorCategory = mapping.ErrorCategory;
        audit.LatencyMs = stopwatch.ElapsedMilliseconds;
        _auditLogger.Write(audit);
        return Results.Json(mapping.Response, statusCode: mapping.StatusCode);
    }

    private static void PopulatePolicyAudit(GatewayAuditEvent audit, PolicyDecision decision)
    {
        audit.Decision = decision.Allowed ? "ALLOW" : "DENY";
        audit.DenyReason = decision.Allowed ? null : decision.Reason;
        audit.PolicyRuleId = decision.RuleId;
        audit.PolicyDecision = decision.Reason;
        audit.Provider = decision.Model?.ProviderName ?? "aws-bedrock";
    }

    private static void PopulateSuccessAudit(GatewayAuditEvent audit, AiCompletionResult result, long latencyMs)
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
    }

    private static OpenAiChatValidationResult Fail(string code, string message, string field) =>
        OpenAiChatValidationResult.Failure(new OpenAiValidationError(code, message, field));
}
