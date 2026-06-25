using System.Diagnostics;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using Polly;

namespace Jarvis.AiGateway.Services;

public interface IEmbeddingsOrchestrator
{
    Task<IResult> EmbedAsync(HttpContext httpContext, OpenAiEmbeddingsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates <c>POST /v1/embeddings</c>.  Embeddings are a bulk source-code egress surface, so
/// the request runs through the SAME identity, policy/ITAR/group, blocked-pattern, prompt-size, and
/// audit controls as chat (via <see cref="IPolicyEngine.AuthorizeEmbeddingsAsync"/>), and the
/// provider redacts inputs before they leave the boundary.  Capability-gated: a model must be
/// configured with SupportsEmbeddings and routed to a provider that implements
/// <see cref="IEmbeddingProvider"/>, otherwise the request fails closed.
/// </summary>
public sealed class EmbeddingsOrchestrator : IEmbeddingsOrchestrator
{
    private readonly IUserContextFactory _userContextFactory;
    private readonly IRequestContextFactory _requestContextFactory;
    private readonly IPolicyEngine _policyEngine;
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly IAuditLogger _auditLogger;
    private readonly IOpenAiErrorMapper _errorMapper;
    private readonly IGatewayMetrics _metrics;
    private readonly GatewayOptions _options;
    private readonly ResiliencePipeline _resilience;

    public EmbeddingsOrchestrator(
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
        IPolicyEngine policyEngine,
        IEnumerable<IAiProvider> providers,
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
        _auditLogger = auditLogger;
        _errorMapper = errorMapper;
        _metrics = metrics;
        _options = options.Value;
        _resilience = resilience;
    }

    public async Task<IResult> EmbedAsync(HttpContext httpContext, OpenAiEmbeddingsRequest request, CancellationToken cancellationToken)
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

            if (!TryParseInputs(request.Input, out var inputs, out var inputError))
            {
                return WriteMapped(audit, stopwatch, _errorMapper.MapValidation(inputError!));
            }

            audit.EmbeddingInputCount = inputs.Count;
            audit.PromptCharacters = inputs.Sum(i => i.Length);

            var aiRequest = new AiEmbeddingsRequest(request.Model, inputs, request.Dimensions);
            var decision = await _policyEngine.AuthorizeEmbeddingsAsync(user, requestContext, aiRequest, cancellationToken);
            PopulatePolicyAudit(audit, decision);
            if (!decision.Allowed || decision.Model is null)
            {
                _metrics.RecordPolicyDenial(decision.RuleId, request.Model);
                return WriteMapped(audit, stopwatch, _errorMapper.MapPolicyDenied(decision));
            }

            var provider = ResolveProvider(decision.Model);
            if (provider is not IEmbeddingProvider embeddingProvider)
            {
                // Capability-gated: the model's provider does not implement embeddings.
                throw new NotSupportedException("Embeddings are not supported for the requested model's provider.");
            }

            _metrics.RecordRequest(request.Model);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProviderTimeoutSeconds)));

            AiEmbeddingsResult result;
            try
            {
                result = await _resilience.ExecuteAsync(
                    async ct => await embeddingProvider.EmbedAsync(decision.Model, aiRequest, requestContext, ct),
                    timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new ProviderTimeoutException("Provider invocation timed out.", ex);
            }

            var response = MapResponse(decision.Model.Id, result);
            PopulateSuccessAudit(audit, result, stopwatch.ElapsedMilliseconds);
            _auditLogger.Write(audit);
            _metrics.RecordTokenUsage(request.Model, result.Usage.PromptTokens, 0);
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
            _metrics.RecordServerError("embeddings");
            return Results.Json(mapping.Response, statusCode: mapping.StatusCode);
        }
    }

    private bool TryParseInputs(JsonElement input, out List<string> inputs, out OpenAiChatValidationResult? error)
    {
        inputs = [];
        error = null;

        if (input.ValueKind == JsonValueKind.String)
        {
            var value = input.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(value))
            {
                error = Fail("input_required", "The 'input' field must not be empty.", "input");
                return false;
            }

            inputs.Add(value);
            return true;
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    // Token-array inputs (arrays of numbers) are not supported — text only.
                    error = Fail("unsupported_input", "The 'input' field must be a string or an array of strings.", "input");
                    return false;
                }

                inputs.Add(item.GetString() ?? string.Empty);
            }

            if (inputs.Count == 0)
            {
                error = Fail("input_required", "The 'input' field must contain at least one string.", "input");
                return false;
            }

            if (inputs.Count > _options.RequestValidation.MaxEmbeddingInputs)
            {
                error = Fail("too_many_inputs", $"The 'input' field may contain at most {_options.RequestValidation.MaxEmbeddingInputs} items.", "input");
                return false;
            }

            return true;
        }

        error = Fail("input_required", "The 'input' field is required and must be a string or array of strings.", "input");
        return false;
    }

    private IAiProvider ResolveProvider(GatewayModel model)
    {
        var match = _providers.FirstOrDefault(p => p.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        if (_providers.Count == 1) return _providers[0];
        throw new NotSupportedException($"No AI provider is registered for ProviderName '{model.ProviderName}'.");
    }

    private static OpenAiEmbeddingsResponse MapResponse(string modelId, AiEmbeddingsResult result) => new()
    {
        Model = modelId,
        Data = result.Data.Select(d => new OpenAiEmbeddingData { Index = d.Index, Embedding = d.Vector }).ToList(),
        Usage = new OpenAiEmbeddingsUsage
        {
            PromptTokens = result.Usage.PromptTokens,
            TotalTokens = result.Usage.TotalTokens == 0 ? result.Usage.PromptTokens : result.Usage.TotalTokens
        }
    };

    private GatewayAuditEvent CreateAudit(UserContext user, RequestContext requestContext, OpenAiEmbeddingsRequest request) => new()
    {
        EventType = "AI_EMBEDDINGS",
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

    private static void PopulateSuccessAudit(GatewayAuditEvent audit, AiEmbeddingsResult result, long latencyMs)
    {
        audit.Decision = "ALLOW";
        audit.InputTokens = result.Usage.PromptTokens;
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
