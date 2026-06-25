using System.Net;
using System.Text;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class AzureOpenAiProviderTests
{
    [Fact]
    public async Task CompleteAsync_targets_deployment_url_and_parses_response()
    {
        var handler = new StubHandler(_ => Ok(
            """{"choices":[{"message":{"content":"hello world"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""));
        var provider = Provider(handler);

        var result = await provider.CompleteAsync(Model(), Request("hi"), Context(), CancellationToken.None);

        Assert.Equal("hello world", result.Text);
        Assert.Equal(3, result.Usage.PromptTokens);
        Assert.Equal(2, result.Usage.CompletionTokens);
        Assert.Equal(5, result.Usage.TotalTokens);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("azure-openai", result.ProviderMetadata.Provider);

        // Deployment name (not a model ID) is in the path, with the configured api-version.
        Assert.Equal(
            "https://azr-gov-jarvis2-openai.openai.azure.us/openai/deployments/jarvis2-chat/chat/completions?api-version=2025-04-01-preview",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.True(handler.LastRequest.Headers.TryGetValues("api-key", out var keys));
        Assert.Equal("secret-key", Assert.Single(keys!));
    }

    [Fact]
    public async Task CompleteAsync_redacts_itar_content_before_sending()
    {
        var handler = new StubHandler(_ => Ok("""{"choices":[{"message":{"content":"ok"}}]}"""));
        // Default GatewayOptions redacts before provider; the ITAR flag forces it regardless.
        var provider = Provider(handler, gatewayOptions: new GatewayOptions());

        await provider.CompleteAsync(Model(), Request("my key is AKIAIOSFODNN7EXAMPLE end"), Context(itar: true), CancellationToken.None);

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", handler.LastBody);
        Assert.Contains("REDACTED", handler.LastBody);
    }

    [Fact]
    public async Task CompleteAsync_non_success_throws_azure_exception_with_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") });
        var provider = Provider(handler);

        var ex = await Assert.ThrowsAsync<AzureOpenAiException>(() =>
            provider.CompleteAsync(Model(), Request("hi"), Context(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_unparseable_body_throws_parse_exception()
    {
        var handler = new StubHandler(_ => Ok("this is not json"));
        var provider = Provider(handler);

        await Assert.ThrowsAsync<ProviderResponseParseException>(() =>
            provider.CompleteAsync(Model(), Request("hi"), Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_missing_endpoint_throws()
    {
        var handler = new StubHandler(_ => Ok("{}"));
        var provider = Provider(handler, azureOptions: new AzureOpenAiOptions { Endpoint = "", ApiKey = "k" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteAsync(Model(), Request("hi"), Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_missing_deployment_throws_unsupported()
    {
        var handler = new StubHandler(_ => Ok("{}"));
        var provider = Provider(handler);
        var model = Model();
        model.AzureDeploymentName = "";

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.CompleteAsync(model, Request("hi"), Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_empty_messages_throws()
    {
        var handler = new StubHandler(_ => Ok("{}"));
        var provider = Provider(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteAsync(Model(), Request("   "), Context(), CancellationToken.None));
    }

    [Fact]
    public async Task StreamAsync_parses_deltas_finish_reason_and_usage()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"role":"assistant"}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"Hel"}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"lo"}}]}""",
            "",
            """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "",
            "garbage-without-data-prefix",
            "data: {malformed json}",
            """data: {"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""",
            "",
            "data: [DONE]",
            "");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        var provider = Provider(handler);

        var events = new List<AiChatStreamEvent>();
        await foreach (var ev in provider.StreamAsync(Model(), Request("hi"), Context(), CancellationToken.None))
        {
            events.Add(ev);
        }

        var deltas = events.OfType<AiChatTextDeltaEvent>().Select(d => d.Text).ToList();
        Assert.Equal(new[] { "Hel", "lo" }, deltas);
        var completion = Assert.IsType<AiChatCompletionEvent>(events[^1]);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(5, completion.Usage?.TotalTokens);

        Assert.Contains("\"stream\":true", handler.LastBody);
        Assert.Contains("include_usage", handler.LastBody);
    }

    [Fact]
    public async Task StreamAsync_non_success_throws_azure_exception()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
        var provider = Provider(handler);

        await Assert.ThrowsAsync<AzureOpenAiException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(Model(), Request("hi"), Context(), CancellationToken.None)) { }
        });
    }

    [Fact]
    public void CanStream_and_invocation_name()
    {
        var provider = Provider(new StubHandler(_ => Ok("{}")));
        Assert.True(provider.CanStream(Model(), Request("hi")));
        var noDeployment = Model();
        noDeployment.AzureDeploymentName = "";
        Assert.False(provider.CanStream(noDeployment, Request("hi")));
        Assert.Equal("azure-openai-stream", provider.StreamInvocationName(Model(), Request("hi")));
        Assert.Equal("azure-openai", provider.ProviderName);
    }

    [Fact]
    public async Task Gpt5_deployment_sends_max_completion_tokens_and_never_max_tokens()
    {
        var handler = new StubHandler(_ => Ok("""{"choices":[{"message":{"content":"ok"}}]}"""));
        var provider = Provider(handler);

        await provider.CompleteAsync(Gpt5Model(), Request("hi"), Context(), CancellationToken.None);

        Assert.Contains("max_completion_tokens", handler.LastBody);
        Assert.DoesNotContain("\"max_tokens\"", handler.LastBody);
    }

    [Fact]
    public async Task Gpt41_mini_deployment_still_sends_max_tokens()
    {
        var handler = new StubHandler(_ => Ok("""{"choices":[{"message":{"content":"ok"}}]}"""));
        var provider = Provider(handler);

        await provider.CompleteAsync(Gpt41MiniModel(), Request("hi"), Context(), CancellationToken.None);

        Assert.Contains("\"max_tokens\"", handler.LastBody);
        Assert.DoesNotContain("max_completion_tokens", handler.LastBody);
    }

    [Fact]
    public async Task Failure_logs_safe_redacted_diagnostics()
    {
        var azureErrorBody = """{"error":{"code":"unsupported_parameter","message":"api-key: leaked-secret-xyz rejected"}}""";
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(azureErrorBody) };
            resp.Headers.TryAddWithoutValidation("apim-request-id", "req-abc-123");
            return resp;
        });
        var logger = new CapturingLogger<AzureOpenAiProvider>();
        var provider = Provider(handler, logger: logger);

        await Assert.ThrowsAsync<AzureOpenAiException>(() =>
            provider.CompleteAsync(Gpt5Model(), Request("hi"), Context(), CancellationToken.None));

        var errorLog = Assert.Single(logger.Messages, m => m.Contains("Azure OpenAI request failed"));
        Assert.Contains("status=400", errorLog);
        Assert.Contains("unsupported_parameter", errorLog);
        Assert.Contains("deployment=jarvis2-chat", errorLog);
        Assert.Contains("correlationId=cid", errorLog);
        Assert.Contains("req-abc-123", errorLog);
        // Secret embedded in the Azure message must be redacted in logs.
        Assert.DoesNotContain("leaked-secret-xyz", errorLog);
    }

    [Fact]
    public async Task Failure_does_not_leak_raw_provider_body_to_clients()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("RAW_AZURE_DIAGNOSTIC_DETAIL")
        });
        var provider = Provider(handler);

        var ex = await Assert.ThrowsAsync<AzureOpenAiException>(() =>
            provider.CompleteAsync(Model(), Request("hi"), Context(), CancellationToken.None));

        Assert.DoesNotContain("RAW_AZURE_DIAGNOSTIC_DETAIL", ex.Message);
        var mapping = new OpenAiErrorMapper().MapException(ex);
        Assert.Equal("provider_validation_error", mapping.Response.Error.Code);
        Assert.DoesNotContain("RAW_AZURE_DIAGNOSTIC_DETAIL", mapping.Response.Error.Message);
    }

    [Fact]
    public async Task Tools_and_tool_messages_are_sent_in_outbound_payload()
    {
        var handler = new StubHandler(_ => Ok("""{"choices":[{"message":{"content":"ok"}}]}"""));
        var provider = Provider(handler);

        await provider.CompleteAsync(Model(), ToolRequest(), Context(), CancellationToken.None);

        Assert.Contains("\"tools\"", handler.LastBody);
        Assert.Contains("get_weather", handler.LastBody);
        Assert.Contains("\"tool_choice\":\"auto\"", handler.LastBody);
        Assert.Contains("\"tool_calls\"", handler.LastBody);
        Assert.Contains("\"tool_call_id\":\"call_1\"", handler.LastBody);
        Assert.Contains("\"role\":\"tool\"", handler.LastBody);
    }

    [Fact]
    public async Task Tool_calls_in_response_are_parsed()
    {
        var handler = new StubHandler(_ => Ok(
            """{"choices":[{"message":{"tool_calls":[{"id":"call_9","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"NYC\"}"}}]},"finish_reason":"tool_calls"}]}"""));
        var provider = Provider(handler);

        var result = await provider.CompleteAsync(Model(), ToolRequest(), Context(), CancellationToken.None);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("call_9", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("{\"city\":\"NYC\"}", call.ArgumentsJson);
        Assert.Equal("tool_calls", result.FinishReason);
    }

    [Fact]
    public async Task Tool_arguments_and_results_are_redacted_outbound()
    {
        var handler = new StubHandler(_ => Ok("""{"choices":[{"message":{"content":"ok"}}]}"""));
        var provider = Provider(handler);
        var request = new AiChatRequest(
            "chat",
            [
                new AiMessage("user", "go"),
                new AiMessage("assistant", "", ToolCalls: [new AiToolCall("call_1", "store", "{\"secret\":\"AKIAIOSFODNN7EXAMPLE\"}")]),
                new AiMessage("tool", "result key AKIAIOSFODNN7EXAMPLE done", ToolCallId: "call_1")
            ],
            new AiGenerationOptions(0.2f, 0.9f, 100, []),
            new Dictionary<string, string>(),
            false,
            Tools: [new AiToolDefinition("store", "desc", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }))]);

        await provider.CompleteAsync(Model(), request, Context(itar: true), CancellationToken.None);

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", handler.LastBody);
        Assert.Contains("REDACTED", handler.LastBody);
    }

    [Fact]
    public async Task Embeddings_target_the_embeddings_url_and_parse_vectors()
    {
        var handler = new StubHandler(_ => Ok(
            """{"data":[{"index":0,"embedding":[0.1,0.2]},{"index":1,"embedding":[0.3]}],"usage":{"prompt_tokens":4,"total_tokens":4}}"""));
        var provider = Provider(handler);

        var result = await provider.EmbedAsync(Model(), new AiEmbeddingsRequest("chat", ["alpha", "beta"], 256), Context(), CancellationToken.None);

        Assert.Equal(2, result.Data.Count);
        Assert.Equal(0.1f, result.Data[0].Vector[0]);
        Assert.Equal(4, result.Usage.PromptTokens);
        Assert.EndsWith("/openai/deployments/jarvis2-chat/embeddings?api-version=2025-04-01-preview", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"input\":[\"alpha\",\"beta\"]", handler.LastBody);
        Assert.Contains("\"dimensions\":256", handler.LastBody);
    }

    [Fact]
    public async Task Completion_targets_the_completions_url_and_parses_text()
    {
        var handler = new StubHandler(_ => Ok(
            """{"choices":[{"index":0,"text":"a, b):","finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""));
        var provider = Provider(handler);

        var result = await provider.CompleteTextAsync(Model(), new AiCompletionRequest("chat", "def add(", "    return total", 32, 0.1f, 0.9f, []), Context(), CancellationToken.None);

        Assert.Equal("a, b):", result.Text);
        Assert.Equal(5, result.Usage.TotalTokens);
        Assert.EndsWith("/openai/deployments/jarvis2-chat/completions?api-version=2025-04-01-preview", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"prompt\":\"def add(\"", handler.LastBody);
        Assert.Contains("\"suffix\":\"    return total\"", handler.LastBody);
    }

    [Fact]
    public async Task Completion_inputs_are_redacted_outbound()
    {
        var handler = new StubHandler(_ => Ok("""{"choices":[{"index":0,"text":"ok"}]}"""));
        var provider = Provider(handler);

        await provider.CompleteTextAsync(Model(), new AiCompletionRequest("chat", "token AKIAIOSFODNN7EXAMPLE", null, null, null, null, []), Context(itar: true), CancellationToken.None);

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", handler.LastBody);
        Assert.Contains("REDACTED", handler.LastBody);
    }

    [Fact]
    public async Task Embeddings_inputs_are_redacted_outbound()
    {
        var handler = new StubHandler(_ => Ok("""{"data":[{"index":0,"embedding":[0.1]}],"usage":{"prompt_tokens":1,"total_tokens":1}}"""));
        var provider = Provider(handler);

        await provider.EmbedAsync(Model(), new AiEmbeddingsRequest("chat", ["key AKIAIOSFODNN7EXAMPLE here"], null), Context(itar: true), CancellationToken.None);

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", handler.LastBody);
        Assert.Contains("REDACTED", handler.LastBody);
    }

    private static AiChatRequest ToolRequest()
    {
        var parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", properties = new { city = new { type = "string" } } });
        return new AiChatRequest(
            "chat",
            [
                new AiMessage("user", "weather in NYC?"),
                new AiMessage("assistant", "", ToolCalls: [new AiToolCall("call_1", "get_weather", "{\"city\":\"NYC\"}")]),
                new AiMessage("tool", "72F", ToolCallId: "call_1")
            ],
            new AiGenerationOptions(0.2f, 0.9f, 100, []),
            new Dictionary<string, string>(),
            false,
            Tools: [new AiToolDefinition("get_weather", "Get weather", parameters)],
            ToolChoice: new AiToolChoice("auto"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static AzureOpenAiProvider Provider(
        StubHandler handler,
        AzureOpenAiOptions? azureOptions = null,
        GatewayOptions? gatewayOptions = null,
        ILogger<AzureOpenAiProvider>? logger = null)
    {
        azureOptions ??= new AzureOpenAiOptions
        {
            Endpoint = "https://azr-gov-jarvis2-openai.openai.azure.us/",
            ApiVersion = "2025-04-01-preview",
            ApiKey = "secret-key"
        };
        var gateway = gatewayOptions ?? new GatewayOptions();
        var azure = MsOptions.Create(azureOptions);
        return new AzureOpenAiProvider(
            new StubHttpClientFactory(handler),
            new ApiKeyAzureOpenAiCredential(azure),
            new RegexContentRedactor(MsOptions.Create(gateway)),
            azure,
            MsOptions.Create(gateway),
            logger ?? NullLogger<AzureOpenAiProvider>.Instance);
    }

    private static GatewayModel Gpt5Model() => new()
    {
        Alias = "jarvis2-chat",
        ProviderName = "azure-openai",
        AzureDeploymentName = "jarvis2-chat",
        AzureModelName = "gpt-5.1",
        MaxOutputTokens = 100
    };

    private static GatewayModel Gpt41MiniModel() => new()
    {
        Alias = "jarvis2-fast",
        ProviderName = "azure-openai",
        AzureDeploymentName = "jarvis2-fast",
        AzureModelName = "gpt-4.1-mini",
        MaxOutputTokens = 100
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GatewayModel Model() => new()
    {
        Alias = "chat",
        ProviderName = "azure-openai",
        AzureDeploymentName = "jarvis2-chat",
        MaxOutputTokens = 100
    };

    private static AiChatRequest Request(string content) =>
        new("chat", [new AiMessage("user", content)], new AiGenerationOptions(0.3f, 0.8f, 50, ["STOP"]), new Dictionary<string, string>(), false);

    private static RequestContext Context(bool itar = false) => new("rid", "cid", "ws", itar ? "ITAR_APPROVED" : "GENERAL", itar);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responder(request);
        }
    }
}
