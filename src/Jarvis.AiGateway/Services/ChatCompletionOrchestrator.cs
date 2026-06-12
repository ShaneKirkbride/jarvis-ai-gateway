using System.Diagnostics;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IChatCompletionOrchestrator
{
    Task<IResult> CompleteAsync(HttpContext httpContext, OpenAiChatCompletionRequest request, CancellationToken cancellationToken);
}

public sealed class ChatCompletionOrchestrator(
    IUserContextFactory userContextFactory,
    IRequestContextFactory requestContextFactory,
    IOpenAiChatRequestValidator validator,
    IPolicyEngine policyEngine,
    IEnumerable<IBedrockInvocationStrategy> strategies,
    IContentRedactor redactor,
    IAuditLogger auditLogger,
    IOpenAiErrorMapper errorMapper,
    IOptions<GatewayOptions> options) : IChatCompletionOrchestrator
{
    private readonly GatewayOptions _options = options.Value;

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
                return WriteMapped(audit, stopwatch, errorMapper.MapValidation(validation));
            }

            if (request.Stream && !_options.Streaming.FallbackToNonStreaming)
            {
                var streamValidation = OpenAiChatValidationResult.Failure(new OpenAiValidationError("streaming_unsupported", "Streaming responses are not implemented by this gateway.", "stream"));
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
                return WriteMapped(audit, stopwatch, errorMapper.MapPolicyDenied(decision));
            }

            validation = validator.Validate(httpContext, request, decision.Model);
            if (!validation.IsValid || validation.AiRequest is null)
            {
                return WriteMapped(audit, stopwatch, errorMapper.MapValidation(validation));
            }

            var orderedStrategies = strategies.OrderBy(s => s is BedrockConverseInvocationStrategy ? 0 : 1).ToArray();
            var strategy = orderedStrategies.FirstOrDefault(s => s.CanHandle(decision.Model, validation.AiRequest));
            if (strategy is null)
            {
                throw new NotSupportedException(BedrockInvokeModelTextInvocationStrategy.UnsupportedAdapterMessage);
            }

            audit.InvocationStrategy = strategy.Name;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProviderTimeoutSeconds)));

            AiChatResult result;
            try
            {
                result = await strategy.InvokeAsync(decision.Model, validation.AiRequest, requestContext, timeoutCts.Token);
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
