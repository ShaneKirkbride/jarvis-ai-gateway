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
    private readonly IEnumerable<IBedrockInvocationStrategy> strategies;
    private readonly IContentRedactor redactor;
    private readonly IAuditLogger auditLogger;
    private readonly IOpenAiErrorMapper errorMapper;
    private readonly IGatewayMetrics metrics;
    private readonly GatewayOptions _options;
    private readonly ResiliencePipeline _resilience;

    // Convenience constructor for tests (no metrics, no custom resilience pipeline).
    public ChatCompletionOrchestrator(
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
        IOpenAiChatRequestValidator validator,
        IPolicyEngine policyEngine,
        IEnumerable<IBedrockInvocationStrategy> strategies,
        IContentRedactor redactor,
        IAuditLogger auditLogger,
        IOpenAiErrorMapper errorMapper,
        IOptions<GatewayOptions> options)
        : this(userContextFactory, requestContextFactory, validator, policyEngine, strategies, redactor, auditLogger, errorMapper, new NoOpGatewayMetrics(), options, new ResiliencePipelineBuilder().Build())
    {
    }

    // Full constructor used by the DI container in production.
    public ChatCompletionOrchestrator(
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
        IOpenAiChatRequestValidator validator,
        IPolicyEngine policyEngine,
        IEnumerable<IBedrockInvocationStrategy> strategies,
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
        this.strategies = strategies;
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

            if (request.Stream && !_options.Streaming.FallbackToNonStreaming)
            {
                var streamValidation = OpenAiChatValidationResult.Failure(new OpenAiValidationError("streaming_unsupported", "Streaming responses are not implemented by this gateway.", "stream"));
                metrics.RecordPolicyDenial(streamValidation.Code ?? "streaming_unsupported", request.Model ?? string.Empty);
                return WriteMapped(audit, stopwatch, errorMapper.MapValidation(streamValidation));
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

            var orderedStrategies = strategies.OrderBy(s => s is BedrockConverseInvocationStrategy ? 0 : 1).ToArray();
            var strategy = orderedStrategies.FirstOrDefault(s => s.CanHandle(decision.Model, validation.AiRequest));
            if (strategy is null)
            {
                throw new NotSupportedException(BedrockInvokeModelTextInvocationStrategy.UnsupportedAdapterMessage);
            }

            audit.InvocationStrategy = strategy.Name;
            metrics.RecordRequest(request.Model ?? string.Empty);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProviderTimeoutSeconds)));

            AiChatResult result;
            var providerStopwatch = Stopwatch.StartNew();
            try
            {
                result = await _resilience.ExecuteAsync(
                    async ct => await strategy.InvokeAsync(decision.Model, validation.AiRequest, requestContext, ct),
                    timeoutCts.Token);
                metrics.RecordBedrockInvocation(strategy.Name, providerStopwatch.Elapsed, success: true);
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
                }
            }

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
        PromptCharacters = aiRequest is null ? 0 : string.Join("\n", aiRequest.Messages.Select(m => m.Content)).Length
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
