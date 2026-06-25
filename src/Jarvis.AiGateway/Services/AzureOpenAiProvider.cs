using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// <see cref="IAiProvider"/> for Azure OpenAI (including Azure Government).  Talks to the data
/// plane over HTTP using deployment names (not model IDs): requests target
/// <c>{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=…</c>.
///
/// <para>The request/response contract exposed to callers is unchanged — this provider maps the
/// gateway's provider-neutral <see cref="AiChatRequest"/>/<see cref="AiChatResult"/> and stream
/// events to and from the Azure wire format.  Inbound ITAR/global redaction is applied with the
/// same rules as the Bedrock path before any content leaves the gateway boundary.</para>
/// </summary>
public sealed class AzureOpenAiProvider : IAiProvider, IStreamingAiProvider, IEmbeddingProvider, ICompletionProvider
{
    public const string ProviderKey = "azure-openai";
    public const string HttpClientName = "azure-openai";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureOpenAiCredential _credential;
    private readonly IContentRedactor _redactor;
    private readonly AzureOpenAiOptions _azureOptions;
    private readonly GatewayOptions _gatewayOptions;
    private readonly ILogger<AzureOpenAiProvider> _logger;

    public AzureOpenAiProvider(
        IHttpClientFactory httpClientFactory,
        IAzureOpenAiCredential credential,
        IContentRedactor redactor,
        IOptions<AzureOpenAiOptions> azureOptions,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<AzureOpenAiProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _redactor = redactor;
        _azureOptions = azureOptions.Value;
        _gatewayOptions = gatewayOptions.Value;
        _logger = logger;
    }

    public string ProviderName => ProviderKey;

    // Azure OpenAI chat deployments always support streaming; the only requirement is a deployment.
    public bool CanStream(GatewayModel model, AiChatRequest request) =>
        !string.IsNullOrWhiteSpace(model.AzureDeploymentName);

    public string StreamInvocationName(GatewayModel model, AiChatRequest request) => "azure-openai-stream";

    public async Task<AiChatResult> CompleteAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var body = BuildPayload(model, request, context, stream: false);
        using var httpRequest = await BuildHttpRequestAsync(BuildRequestUri(model, "chat/completions"), body, cancellationToken);

        _logger.LogInformation("Invoking Azure OpenAI deployment {Deployment} ({Alias}).", model.AzureDeploymentName, model.Alias);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        stopwatch.Stop();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogProviderError(response, content, model, context);
            throw new AzureOpenAiException(response.StatusCode);
        }

        return ParseCompletion(content, stopwatch.ElapsedMilliseconds, RequestId(response));
    }

    public async IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildPayload(model, request, context, stream: true);
        using var httpRequest = await BuildHttpRequestAsync(BuildRequestUri(model, "chat/completions"), body, cancellationToken);

        _logger.LogInformation("Streaming Azure OpenAI deployment {Deployment} ({Alias}).", model.AzureDeploymentName, model.Alias);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            LogProviderError(response, errorBody, model, context);
            throw new AzureOpenAiException(response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var finishReason = "stop";
        TokenUsage? usage = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            // A malformed chunk is skipped rather than aborting an in-progress stream.
            if (!TryParseChunk(data, out var delta, out var chunkFinish, out var chunkUsage)) continue;

            if (!string.IsNullOrEmpty(delta))
            {
                yield return new AiChatTextDeltaEvent(delta!);
            }

            if (!string.IsNullOrWhiteSpace(chunkFinish)) finishReason = chunkFinish!;
            if (chunkUsage is not null) usage = chunkUsage;
        }

        yield return new AiChatCompletionEvent(finishReason, usage);
    }

    public async Task<AiEmbeddingsResult> EmbedAsync(GatewayModel model, AiEmbeddingsRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        // Bulk source-code egress: redact inputs (ITAR/global) before they leave the boundary.
        var redactForThisRequest = _gatewayOptions.Redaction.RedactBeforeBedrock
            || context.ItarMode
            || DataLabelClassifier.IsItar(context.DataLabel);
        var inputs = request.Inputs.Select(i => redactForThisRequest ? _redactor.Redact(i).Text : i).ToList();

        var payload = new Dictionary<string, object?> { ["input"] = inputs };
        if (request.Dimensions is { } dimensions)
        {
            payload["dimensions"] = dimensions;
        }

        var body = JsonSerializer.Serialize(payload, SerializerOptions);
        using var httpRequest = await BuildHttpRequestAsync(BuildRequestUri(model, "embeddings"), body, cancellationToken);

        _logger.LogInformation("Embedding via Azure OpenAI deployment {Deployment} ({Alias}); inputs={Count}.", model.AzureDeploymentName, model.Alias, inputs.Count);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        stopwatch.Stop();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogProviderError(response, content, model, context);
            throw new AzureOpenAiException(response.StatusCode);
        }

        return ParseEmbeddings(content, stopwatch.ElapsedMilliseconds, RequestId(response));
    }

    private static AiEmbeddingsResult ParseEmbeddings(string content, long latencyMs, string? requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var data = new List<AiEmbedding>();
            if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataElement.EnumerateArray())
                {
                    var index = item.TryGetProperty("index", out var idxElement) && idxElement.TryGetInt32(out var idx) ? idx : data.Count;
                    var vector = new List<float>();
                    if (item.TryGetProperty("embedding", out var embElement) && embElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var number in embElement.EnumerateArray())
                        {
                            if (number.TryGetSingle(out var value)) vector.Add(value);
                        }
                    }

                    data.Add(new AiEmbedding(index, vector));
                }
            }

            var usage = ParseUsage(root.TryGetProperty("usage", out var usageElement) ? usageElement : default);
            return new AiEmbeddingsResult(data, usage, new ProviderInvocationMetadata(ProviderKey, ProviderKey, latencyMs, requestId));
        }
        catch (JsonException ex)
        {
            throw new ProviderResponseParseException("Azure OpenAI embeddings response could not be parsed.", ex);
        }
    }

    public async Task<AiCompletionResult> CompleteTextAsync(GatewayModel model, AiCompletionRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        // Autocomplete/FIM is high-egress: redact prompt and suffix before they leave the boundary.
        var redactForThisRequest = _gatewayOptions.Redaction.RedactBeforeBedrock
            || context.ItarMode
            || DataLabelClassifier.IsItar(context.DataLabel);

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = Redact(request.Prompt, redactForThisRequest),
            ["temperature"] = request.Temperature ?? 0.2f,
            ["top_p"] = request.TopP ?? 0.9f,
            ["max_tokens"] = Math.Min(request.MaxTokens ?? model.MaxOutputTokens, model.MaxOutputTokens)
        };
        if (!string.IsNullOrEmpty(request.Suffix))
        {
            payload["suffix"] = Redact(request.Suffix, redactForThisRequest);
        }
        if (request.StopSequences.Count > 0)
        {
            payload["stop"] = request.StopSequences;
        }

        var body = JsonSerializer.Serialize(payload, SerializerOptions);
        using var httpRequest = await BuildHttpRequestAsync(BuildRequestUri(model, "completions"), body, cancellationToken);

        _logger.LogInformation("Completing via Azure OpenAI deployment {Deployment} ({Alias}); fim={Fim}.", model.AzureDeploymentName, model.Alias, !string.IsNullOrEmpty(request.Suffix));
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        stopwatch.Stop();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogProviderError(response, content, model, context);
            throw new AzureOpenAiException(response.StatusCode);
        }

        return ParseTextCompletion(content, stopwatch.ElapsedMilliseconds, RequestId(response));
    }

    private static AiCompletionResult ParseTextCompletion(string content, long latencyMs, string? requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var text = string.Empty;
            var finishReason = "stop";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    text = textElement.GetString() ?? string.Empty;
                }

                if (first.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString() ?? "stop";
                }
            }

            var usage = ParseUsage(root.TryGetProperty("usage", out var usageElement) ? usageElement : default);
            return new AiCompletionResult(text, usage, finishReason, new ProviderInvocationMetadata(ProviderKey, ProviderKey, latencyMs, requestId));
        }
        catch (JsonException ex)
        {
            throw new ProviderResponseParseException("Azure OpenAI completion response could not be parsed.", ex);
        }
    }

    private async Task<HttpRequestMessage> BuildHttpRequestAsync(Uri uri, string body, CancellationToken cancellationToken)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await _credential.ApplyAsync(httpRequest, cancellationToken);
        return httpRequest;
    }

    private Uri BuildRequestUri(GatewayModel model, string operation)
    {
        if (string.IsNullOrWhiteSpace(_azureOptions.Endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI endpoint is not configured.");
        }

        if (string.IsNullOrWhiteSpace(model.AzureDeploymentName))
        {
            throw new NotSupportedException($"Model '{model.Alias}' is routed to Azure OpenAI but has no AzureDeploymentName configured.");
        }

        var baseUri = _azureOptions.Endpoint.TrimEnd('/');
        var deployment = Uri.EscapeDataString(model.AzureDeploymentName);
        var apiVersion = Uri.EscapeDataString(_azureOptions.ApiVersion);
        return new Uri($"{baseUri}/openai/deployments/{deployment}/{operation}?api-version={apiVersion}");
    }

    private string BuildPayload(GatewayModel model, AiChatRequest request, RequestContext context, bool stream)
    {
        // Redaction parity with the Bedrock path: redact when configured globally OR whenever the
        // request is ITAR — ITAR content must always be redacted before leaving the gateway.
        var redactForThisRequest = _gatewayOptions.Redaction.RedactBeforeBedrock
            || context.ItarMode
            || DataLabelClassifier.IsItar(context.DataLabel);

        var messages = new List<object>();
        foreach (var input in request.Messages)
        {
            var role = string.IsNullOrWhiteSpace(input.Role) ? "user" : input.Role;
            var text = redactForThisRequest ? _redactor.Redact(input.Content).Text : input.Content;

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                // Tool result returned to the model. The content was redacted above like any
                // untrusted prompt content before leaving the gateway boundary.
                messages.Add(new Dictionary<string, object?> { ["role"] = "tool", ["tool_call_id"] = input.ToolCallId, ["content"] = text });
                continue;
            }

            if (input.ToolCalls is { Count: > 0 })
            {
                var toolCalls = input.ToolCalls.Select(c => (object)new Dictionary<string, object?>
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = c.Name,
                        ["arguments"] = Redact(c.ArgumentsJson, redactForThisRequest)
                    }
                }).ToList();
                messages.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = string.IsNullOrEmpty(text) ? null : text, ["tool_calls"] = toolCalls });
                continue;
            }

            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add(new Dictionary<string, object?> { ["role"] = role, ["content"] = text });
        }

        if (messages.Count == 0)
        {
            throw new InvalidOperationException("At least one non-empty message is required.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["temperature"] = request.Options.Temperature ?? 0.2f,
            ["top_p"] = request.Options.TopP ?? 0.9f,
            ["stream"] = stream
        };

        // Deployment-aware token-limit parameter: GPT-5 deployments get max_completion_tokens,
        // everything else max_tokens.  Exactly one (or none) is ever added — never both.
        var tokenParameter = AzureOpenAiRequestCompatibility.NormalizeForDeployment(model, request);
        if (tokenParameter is not null)
        {
            payload[tokenParameter.Name] = tokenParameter.Value;
        }

        if (request.Options.StopSequences.Count > 0)
        {
            payload["stop"] = request.Options.StopSequences;
        }

        // Tool/function calling. Descriptions (free text) and arguments/results (data) are redacted;
        // JSON-schema parameters are structural and passed through unchanged.
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(t => (object)new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description is null ? null : Redact(t.Description, redactForThisRequest),
                    ["parameters"] = t.Parameters
                }
            }).ToList();

            if (request.ToolChoice is { } choice)
            {
                payload["tool_choice"] = choice.Mode == "function" && choice.FunctionName is not null
                    ? new Dictionary<string, object?> { ["type"] = "function", ["function"] = new Dictionary<string, object?> { ["name"] = choice.FunctionName } }
                    : choice.Mode;
            }
        }

        if (stream)
        {
            // Ask Azure to emit a final usage chunk so streamed token counts can be audited.
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }

        // Operator diagnostic (Debug only): the outbound request SHAPE — never message content.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Azure OpenAI outbound shape. deployment={Deployment} apiVersion={ApiVersion} tokenParam={TokenParam} tokenValue={TokenValue} temperature={Temperature} topP={TopP} messages={MessageCount} stop={StopCount} stream={Stream}",
                model.AzureDeploymentName,
                _azureOptions.ApiVersion,
                tokenParameter?.Name ?? "(none)",
                tokenParameter?.Value,
                request.Options.Temperature ?? 0.2f,
                request.Options.TopP ?? 0.9f,
                messages.Count,
                request.Options.StopSequences.Count,
                stream);
        }

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private string Redact(string value, bool redact) => redact ? _redactor.Redact(value).Text : value;

    private static AiChatResult ParseCompletion(string content, long latencyMs, string? requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var text = string.Empty;
            var finishReason = "stop";
            List<AiToolCall>? toolCalls = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                    {
                        text = contentElement.GetString() ?? string.Empty;
                    }

                    toolCalls = ExtractToolCalls(message);
                }

                if (first.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString() ?? "stop";
                }
            }

            var usage = ParseUsage(root.TryGetProperty("usage", out var usageElement) ? usageElement : default);
            return new AiChatResult(
                text,
                usage,
                finishReason,
                new ProviderInvocationMetadata(ProviderKey, ProviderKey, latencyMs, requestId),
                toolCalls);
        }
        catch (JsonException ex)
        {
            throw new ProviderResponseParseException("Azure OpenAI response could not be parsed.", ex);
        }
    }

    private static List<AiToolCall>? ExtractToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
        {
            return null;
        }

        var result = new List<AiToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            var id = call.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : string.Empty;
            if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
            var name = fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString()! : string.Empty;
            var args = fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString()! : string.Empty;
            if (!string.IsNullOrEmpty(name)) result.Add(new AiToolCall(id, name, args));
        }

        return result.Count > 0 ? result : null;
    }

    private static bool TryParseChunk(string data, out string? delta, out string? finishReason, out TokenUsage? usage)
    {
        delta = null;
        finishReason = null;
        usage = null;

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var deltaElement) &&
                    deltaElement.TryGetProperty("content", out var contentElement) &&
                    contentElement.ValueKind == JsonValueKind.String)
                {
                    delta = contentElement.GetString();
                }

                if (first.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString();
                }
            }

            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = ParseUsage(usageElement);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TokenUsage ParseUsage(JsonElement usageElement)
    {
        if (usageElement.ValueKind != JsonValueKind.Object)
        {
            return new TokenUsage(0, 0, 0);
        }

        var prompt = usageElement.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
        var completion = usageElement.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
        var total = usageElement.TryGetProperty("total_tokens", out var t) && t.TryGetInt32(out var tv) ? tv : prompt + completion;
        return new TokenUsage(prompt, completion, total);
    }

    // Safe operator diagnostics for an Azure failure: status, Azure error code/message (redacted),
    // Azure request id, deployment, and gateway correlation id.  Never logs the raw body, headers,
    // credentials, or prompt content.
    private void LogProviderError(HttpResponseMessage response, string body, GatewayModel model, RequestContext context)
    {
        var error = AzureOpenAiDiagnostics.ParseError(body);
        _logger.LogWarning(
            "Azure OpenAI request failed. status={Status} azureCode={AzureCode} azureMessage={AzureMessage} requestId={RequestId} deployment={Deployment} correlationId={CorrelationId}",
            (int)response.StatusCode,
            error.Code,
            error.Message,
            RequestId(response),
            model.AzureDeploymentName,
            context.CorrelationId);
    }

    private static string? RequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("apim-request-id", out var apim)) return apim.FirstOrDefault();
        if (response.Headers.TryGetValues("x-ms-request-id", out var ms)) return ms.FirstOrDefault();
        return null;
    }
}
