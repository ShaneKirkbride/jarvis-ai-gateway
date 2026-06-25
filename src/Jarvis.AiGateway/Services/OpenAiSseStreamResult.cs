using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.AspNetCore.Http.Features;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Writes an OpenAI-compatible Server-Sent Events stream for a chat completion.
///
/// The result is only constructed after authentication, policy authorization, model
/// resolution, and request validation have all passed — so a policy denial returns its 403
/// (or other error) before any stream begins.  The first provider event is fetched before any
/// response bytes are written; if the provider fails at that point (e.g. AccessDenied) the
/// failure is mapped to the standard OpenAI error envelope with the correct HTTP status, just
/// like the non-streaming path.  Once the first byte is sent the status is locked, so a later
/// provider failure is surfaced as a trailing SSE error event followed by [DONE].
///
/// Audit metadata is emitted exactly once when the stream terminates (success, cancellation,
/// or error).  Token counts are recorded only if the provider reported usage; otherwise they
/// are left null per the streaming contract.
/// </summary>
public sealed class OpenAiSseStreamResult : IResult
{
    private readonly IAiProvider _provider;
    private readonly string _invocationStrategy;
    private readonly GatewayModel _model;
    private readonly AiChatRequest _request;
    private readonly RequestContext _context;
    private readonly string _requestedModel;
    private readonly GatewayAuditEvent _audit;
    private readonly IAuditLogger _auditLogger;
    private readonly IOpenAiErrorMapper _errorMapper;
    private readonly IGatewayMetrics _metrics;
    private readonly IContentRedactor _redactor;
    private readonly GatewayOptions _options;
    private readonly Stopwatch _stopwatch;

    private readonly string _id = $"chatcmpl-{Guid.NewGuid():N}";
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public OpenAiSseStreamResult(
        IAiProvider provider,
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        string requestedModel,
        GatewayAuditEvent audit,
        IAuditLogger auditLogger,
        IOpenAiErrorMapper errorMapper,
        IGatewayMetrics metrics,
        IContentRedactor redactor,
        GatewayOptions options,
        Stopwatch stopwatch,
        string invocationStrategy)
    {
        _provider = provider;
        _invocationStrategy = invocationStrategy;
        _model = model;
        _request = request;
        _context = context;
        _requestedModel = requestedModel;
        _audit = audit;
        _auditLogger = auditLogger;
        _errorMapper = errorMapper;
        _metrics = metrics;
        _redactor = redactor;
        _options = options;
        _stopwatch = stopwatch;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // Requirement: cancellation is driven by the request lifetime, not a fixed provider
        // timeout — a long-lived stream is normal, and aborting it mid-flight would corrupt
        // the SSE response.
        var cancellationToken = httpContext.RequestAborted;
        var enumerator = _provider.StreamAsync(_model, _request, _context, cancellationToken).GetAsyncEnumerator(cancellationToken);

        bool hasEvent;
        try
        {
            hasEvent = await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync(enumerator);
            FinalizeCancelledAudit();
            return;
        }
        catch (Exception ex)
        {
            await DisposeAsync(enumerator);
            // Nothing written yet — map to the standard error envelope and status code.
            var mapping = _errorMapper.MapException(ex);
            FinalizeErrorAudit(mapping);
            _metrics.RecordBedrockError(_requestedModel);
            _metrics.RecordServerError("chat_completions");
            await Results.Json(mapping.Response, statusCode: mapping.StatusCode).ExecuteAsync(httpContext);
            return;
        }

        // Committed to streaming: set SSE headers and disable buffering so each chunk is
        // flushed to Open WebUI as it is produced.
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            // Initial chunk advertises the assistant role, matching the OpenAI streaming contract.
            await WriteChunkAsync(response, new OpenAiChunkDelta { Role = "assistant" }, finishReason: null, cancellationToken);

            var finishReason = "stop";
            TokenUsage? usage = null;

            while (hasEvent)
            {
                switch (enumerator.Current)
                {
                    case AiChatTextDeltaEvent textDelta:
                        var text = textDelta.Text;
                        if (_options.Redaction.Enabled)
                        {
                            // Best-effort per-chunk redaction.  Patterns spanning a chunk boundary
                            // cannot be matched here; inbound redaction to the provider remains the
                            // primary control.  See docs/streaming.md.
                            var redacted = _redactor.Redact(text);
                            text = redacted.Text;
                            _audit.RedactionCount += redacted.RedactionCount;
                        }

                        if (text.Length > 0)
                        {
                            await WriteChunkAsync(response, new OpenAiChunkDelta { Content = text }, finishReason: null, cancellationToken);
                        }

                        break;

                    case AiChatCompletionEvent completion:
                        finishReason = completion.FinishReason;
                        usage = completion.Usage;
                        break;
                }

                hasEvent = await enumerator.MoveNextAsync();
            }

            // Final chunk: empty delta + mapped finish reason, then the [DONE] sentinel.
            await WriteChunkAsync(response, new OpenAiChunkDelta(), MapFinishReason(finishReason), cancellationToken);
            await WriteRawAsync(response, "data: [DONE]\n\n", cancellationToken);

            FinalizeSuccessAudit(usage);
            if (usage is not null)
            {
                _metrics.RecordTokenUsage(_requestedModel, usage.PromptTokens, usage.CompletionTokens);
            }

            _metrics.RecordLatency(_requestedModel, _stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected mid-stream; the response has already started so there is
            // nothing more to send. Record the outcome and stop.
            FinalizeCancelledAudit();
        }
        catch (Exception ex)
        {
            // Provider failed after the response started. The HTTP status is already 200 and
            // cannot change, so surface the error inside the stream and close it cleanly.
            var mapping = _errorMapper.MapException(ex);
            FinalizeErrorAudit(mapping);
            _metrics.RecordBedrockError(_requestedModel);
            _metrics.RecordServerError("chat_completions");
            await TryWriteTrailingErrorAsync(response, mapping, cancellationToken);
        }
        finally
        {
            await DisposeAsync(enumerator);
        }
    }

    private Task WriteChunkAsync(HttpResponse response, OpenAiChunkDelta delta, string? finishReason, CancellationToken cancellationToken)
    {
        var chunk = new OpenAiChatCompletionChunk
        {
            Id = _id,
            Created = _created,
            Model = _requestedModel,
            Choices = [new OpenAiChunkChoice { Index = 0, Delta = delta, FinishReason = finishReason }]
        };

        return WriteRawAsync(response, $"data: {JsonSerializer.Serialize(chunk)}\n\n", cancellationToken);
    }

    private static async Task WriteRawAsync(HttpResponse response, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task TryWriteTrailingErrorAsync(HttpResponse response, OpenAiErrorMapping mapping, CancellationToken cancellationToken)
    {
        try
        {
            await WriteRawAsync(response, $"data: {JsonSerializer.Serialize(mapping.Response)}\n\n", cancellationToken);
            await WriteRawAsync(response, "data: [DONE]\n\n", cancellationToken);
        }
        catch
        {
            // The connection is already gone; nothing more can be written.
        }
    }

    private static async ValueTask DisposeAsync(IAsyncEnumerator<AiChatStreamEvent> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch
        {
            // Disposal failures on an already-faulted provider stream are non-actionable.
        }
    }

    private void FinalizeSuccessAudit(TokenUsage? usage)
    {
        _audit.Decision = "ALLOW";
        _audit.InputTokens = usage?.PromptTokens;
        _audit.OutputTokens = usage?.CompletionTokens;
        _audit.TotalTokens = usage?.TotalTokens;
        _audit.TokenEstimate = usage?.TotalTokens;
        _audit.LatencyMs = _stopwatch.ElapsedMilliseconds;
        _audit.Provider = _model.ProviderName;
        _audit.InvocationStrategy = _invocationStrategy;
        _auditLogger.Write(_audit);
    }

    private void FinalizeCancelledAudit()
    {
        _audit.Decision = "CANCELLED";
        _audit.ErrorType = "request_cancelled";
        _audit.ErrorCategory = "cancellation";
        _audit.LatencyMs = _stopwatch.ElapsedMilliseconds;
        _auditLogger.Write(_audit);
    }

    private void FinalizeErrorAudit(OpenAiErrorMapping mapping)
    {
        _audit.Decision = "ERROR";
        _audit.DenyReason = mapping.Response.Error.Message;
        _audit.ErrorType = mapping.ErrorType;
        _audit.ErrorCategory = mapping.ErrorCategory;
        _audit.LatencyMs = _stopwatch.ElapsedMilliseconds;
        _auditLogger.Write(_audit);
    }

    // Maps Bedrock stop reasons to OpenAI finish_reason values.  Unknown reasons fall back to
    // "stop" so the contract always carries a valid value.
    private static string MapFinishReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "stop";

        return reason.ToLowerInvariant() switch
        {
            "max_tokens" or "length" => "length",
            "content_filtered" or "guardrail_intervened" => "content_filter",
            "tool_use" => "tool_calls",
            _ => "stop"
        };
    }
}
