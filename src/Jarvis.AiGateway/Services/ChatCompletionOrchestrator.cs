using System.Diagnostics;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using Polly;

namespace Jarvis.AiGateway.Services;

public interface IChatCompletionOrchestrator
{
    Task<IResult> CompleteAsync(HttpContext httpContext, OpenAiChatCompletionRequest request, CancellationToken cancellationToken);
}

public sealed class ChatCompletionOrchestrator : IChatCompletionOrchestrator
{
    private readonly IUserContextFactory userContextFactory;
    private readonly IRequestContextFactory requestContextFactory;
    private readonly IOpenAiChatRequestValidator validator;
    private readonly IPolicyEngine policyEngine;
    private readonly IReadOnlyList<IAiProvider> providers;
    private readonly IContentRedactor redactor;
    private readonly IAuditLogger auditLogger;
    private readonly IOpenAiErrorMapper errorMapper;
    private readonly IGatewayMetrics metrics;
    private readonly GatewayOptions _options;
    private readonly ResiliencePipeline _resilience;

    // Convenience constructor for tests (no metrics, no custom resilience pipeline).  Accepts the
    // Bedrock strategy collections directly and wraps them in a BedrockProvider so existing
    // strategy-based tests keep working unchanged.  streamingStrategies is optional so call sites
    // that exercise only the non-streaming path do not need to supply one.
    public ChatCompletionOrchestrator(
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
        IOpenAiChatRequestValidator validator,
        IPolicyEngine policyEngine,
        IEnumerable<IBedrockInvocationStrategy> strategies,
        IContentRedactor redactor,
        IAuditLogger auditLogger,
        IOpenAiErrorMapper errorMapper,
        IOptions<GatewayOptions> options,
        IEnumerable<IBedrockStreamingStrategy>? streamingStrategies = null)
        : this(userContextFactory, requestContextFactory, validator, policyEngine,
            [new BedrockProvider(strategies, streamingStrategies ?? [])],
            redactor, auditLogger, errorMapper, new NoOpGatewayMetrics(), options, new ResiliencePipelineBuilder().Build())
    {
    }

    // Full constructor used by the DI container in production (registered via an explicit factory
    // in Program.cs, since this is not a parameter superset of the strategy-based convenience
    // constructor and the built-in container would otherwise see the two as ambiguous).
    // Providers are routed by GatewayModel.ProviderName.
    public ChatCompletionOrchestrator(
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
        this.userContextFactory = userContextFactory;
        this.requestContextFactory = requestContextFactory;
        this.validator = validator;
        this.policyEngine = policyEngine;
        this.providers = providers.ToList();
        this.redactor = redactor;
        this.auditLogger = auditLogger;
        this.errorMapper = errorMapper;
        this.metrics = metrics;
        _options = options.Value;
        _resilience = resilience;
    }

    public async Task<IResult> CompleteAsync(HttpContext httpContext, OpenAiChatCompletionRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var user = userContextFactory.Create(httpContext.User);
        var validation = validator.Validate(httpContext, request);
        var requestContext = requestContextFactory.Create(httpContext, request);
        var audit = CreateAudit(user, requestContext, request, validation.AiRequest);

        try
        {
            if (!validation.IsValid || validation.AiRequest is null)
            {
                metrics.RecordPolicyDenial(validation.Code ?? "validation_error", request.Model ?? string.Empty);
                return WriteMapped(audit, stopwatch, errorMapper.MapValidation(validation));
            }

            var promptText = string.Join("\n", validation.AiRequest.Messages.Select(m => m.Content));
            var logRedaction = _options.Redaction.RedactBeforeLogging ? redactor.Redact(promptText) : new RedactionResult(promptText, 0);
            audit.RedactionCount = logRedaction.RedactionCount;
            audit.PromptCharacters = promptText.Length;

            var decision = await policyEngine.AuthorizeAsync(user, requestContext, validation.AiRequest, cancellationToken);
            PopulatePolicyAudit(audit, decision);
            if (!decision.Allowed || decision.Model is null)
            {
                metrics.RecordPolicyDenial(decision.RuleId, request.Model ?? string.Empty);
                return WriteMapped(audit, stopwatch, errorMapper.MapPolicyDenied(decision));
            }

            validation = validator.Validate(httpContext, request, decision.Model);
            if (!validation.IsValid || validation.AiRequest is null)
            {
                metrics.RecordPolicyDenial(validation.Code ?? "validation_error", request.Model ?? string.Empty);
                return WriteMapped(audit, stopwatch, errorMapper.MapValidation(validation));
            }

            // Route to the provider for the resolved model.  ProviderName ("aws-bedrock",
            // "azure-openai", …) selects the implementation; everything above this line is
            // provider-agnostic.
            var provider = ResolveProvider(decision.Model);

            // Streaming is decided here — after authentication, policy authorization, and model
            // resolution — so a policy denial still returns 403 before any stream is opened.
            if (validation.AiRequest.Stream)
            {
                if (provider is IStreamingAiProvider streaming && streaming.CanStream(decision.Model, validation.AiRequest))
                {
                    var streamName = streaming.StreamInvocationName(decision.Model, validation.AiRequest);
                    audit.InvocationStrategy = streamName;
                    metrics.RecordRequest(request.Model ?? string.Empty);
                    return new OpenAiSseStreamResult(
                        provider,
                        decision.Model,
                        validation.AiRequest,
                        requestContext,
                        request.Model ?? string.Empty,
                        audit,
                        auditLogger,
                        errorMapper,
                        metrics,
                        redactor,
                        _options,
                        stopwatch,
                        streamName);
                }

                // No streaming-capable provider for this model.  Either fall back to a single
                // non-streaming completion (backwards-compatible behaviour) or reject.
                if (!_options.Streaming.FallbackToNonStreaming)
                {
                    var streamValidation = OpenAiChatValidationResult.Failure(new OpenAiValidationError("streaming_not_supported", "Streaming is not supported for the requested model.", "stream"));
                    metrics.RecordPolicyDenial(streamValidation.Code ?? "streaming_not_supported", request.Model ?? string.Empty);
                    return WriteMapped(audit, stopwatch, errorMapper.MapValidation(streamValidation));
                }
            }

            audit.InvocationStrategy = provider.ProviderName;
            metrics.RecordRequest(request.Model ?? string.Empty);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProviderTimeoutSeconds)));

            AiChatResult result;
            var providerStopwatch = Stopwatch.StartNew();
            try
            {
                result = await _resilience.ExecuteAsync(
                    async ct => await provider.CompleteAsync(decision.Model, validation.AiRequest, requestContext, ct),
                    timeoutCts.Token);
                metrics.RecordBedrockInvocation(result.ProviderMetadata.InvocationStrategy, providerStopwatch.Elapsed, success: true);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new ProviderTimeoutException("Provider invocation timed out.", ex);
            }

            var response = OpenAiResponseFactory.FromResult(decision.Model.Id, result);
            if (_options.Redaction.Enabled)
            {
                foreach (var choice in response.Choices)
                {
                    var redacted = redactor.Redact(choice.Message.Content);
                    choice.Message.Content = redacted.Text;
                    audit.RedactionCount += redacted.RedactionCount;

                    // Tool-call arguments are model output too — redact them outbound so a model
                    // that echoes a secret into an argument cannot leak it past the gateway.
                    foreach (var toolCall in choice.Message.ToolCalls ?? [])
                    {
                        var redactedArgs = redactor.Redact(toolCall.Function.Arguments);
                        toolCall.Function.Arguments = redactedArgs.Text;
                        audit.RedactionCount += redactedArgs.RedactionCount;
                    }
                }
            }

            PopulateToolAudit(audit, validation.AiRequest, result);
            PopulateSuccessAudit(audit, result, stopwatch.ElapsedMilliseconds);
            auditLogger.Write(audit);
            metrics.RecordTokenUsage(request.Model ?? string.Empty, audit.InputTokens ?? 0, audit.OutputTokens ?? 0);
            metrics.RecordLatency(request.Model ?? string.Empty, stopwatch.Elapsed);
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            var mapping = errorMapper.MapException(ex);
            audit.Decision = mapping.ErrorCategory == "cancellation" ? "CANCELLED" : "ERROR";
            audit.DenyReason = mapping.Response.Error.Message;
            audit.ErrorType = mapping.ErrorType;
            audit.ErrorCategory = mapping.ErrorCategory;
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);
            metrics.RecordBedrockError(request.Model ?? string.Empty);
            metrics.RecordServerError("chat_completions");
            return Results.Json(mapping.Response, statusCode: mapping.StatusCode);
        }
    }

    // Resolves the provider for a model by ProviderName.  Falls back to the single registered
    // provider when exactly one exists (Bedrock-only deployments and the unit-test harness) so an
    // unmatched-but-unambiguous name still routes; otherwise it fails closed.
    private IAiProvider ResolveProvider(GatewayModel model)
    {
        var match = providers.FirstOrDefault(p => p.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        if (providers.Count == 1) return providers[0];
        throw new NotSupportedException($"No AI provider is registered for ProviderName '{model.ProviderName}'.");
    }

    private GatewayAuditEvent CreateAudit(UserContext user, RequestContext requestContext, OpenAiChatCompletionRequest request, AiChatRequest? aiRequest) => new()
    {
        RequestId = requestContext.RequestId,
        CorrelationId = requestContext.CorrelationId,
        UserSubject = user.Subject,
        UserEmail = user.Email,
        UserGroups = user.Groups.ToArray(),
        WorkspaceId = requestContext.WorkspaceId,
        DataLabel = requestContext.DataLabel,
        ItarMode = requestContext.ItarMode,
        RequestedModelAlias = request?.Model ?? string.Empty,
        Region = _options.AwsRegion,
        EndpointMode = string.IsNullOrWhiteSpace(_options.BedrockRuntimeEndpointDns) ? "regional-dns" : "vpce-override",
        PromptCharacters = aiRequest is null ? 0 : string.Join("\n", aiRequest.Messages.Select(m => m.Content)).Length,
        // Auth provenance (e.g. developer API key id) for compliance review — never the raw key.
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
        auditLogger.Write(audit);
        return Results.Json(mapping.Response, statusCode: mapping.StatusCode);
    }

    private static void PopulatePolicyAudit(GatewayAuditEvent audit, PolicyDecision decision)
    {
        audit.Decision = decision.Allowed ? "ALLOW" : "DENY";
        audit.DenyReason = decision.Allowed ? null : decision.Reason;
        audit.PolicyRuleId = decision.RuleId;
        audit.PolicyDecision = decision.Reason;
        audit.ResolvedBedrockModelId = decision.Model?.BedrockModelId;
        audit.Provider = decision.Model?.ProviderName ?? "aws-bedrock";
        audit.SupportsConverse = decision.Model?.SupportsConverse;
        audit.StreamingSupported = decision.Model?.ResponseStreamingSupported;
    }

    private sealed class NoOpGatewayMetrics : IGatewayMetrics
    {
        public void RecordRequest(string modelAlias) { }
        public void RecordLatency(string modelAlias, TimeSpan elapsed) { }
        public void RecordPolicyDenial(string ruleId, string modelAlias) { }
        public void RecordBedrockInvocation(string strategy, TimeSpan elapsed, bool success) { }
        public void RecordBedrockError(string modelAlias) { }
        public void RecordServerError(string route) { }
        public void RecordTokenUsage(string modelAlias, int inputTokens, int outputTokens) { }
        public void RecordIdentityLookupCacheHit() { }
        public void RecordIdentityLookupGraphCall(TimeSpan elapsed, bool success) { }
        public void RecordIdentityLookupFailure(string reason) { }
        public void RecordIdentityPreAuthRateLimited(string partition) { }
    }

    // Records tool-calling activity for compliance review: counts and function NAMES only.
    // Tool-call arguments and tool results are content and are never written to audit.
    private static void PopulateToolAudit(GatewayAuditEvent audit, AiChatRequest? aiRequest, AiChatResult result)
    {
        if (aiRequest?.Tools is { Count: > 0 } tools)
        {
            audit.ToolsOffered = tools.Count;
        }

        if (result.ToolCalls is { Count: > 0 } calls)
        {
            audit.ToolCallsReturned = calls.Count;
            audit.ToolCallNames = calls.Select(c => c.Name).ToArray();
        }
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
