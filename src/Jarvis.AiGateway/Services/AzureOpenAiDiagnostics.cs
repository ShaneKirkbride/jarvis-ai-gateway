using System.Text.Json;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Parses Azure OpenAI error responses into safe, structured diagnostic fields for operator
/// logging.  Never returns the raw body; the extracted code/message are redacted of any
/// secret-looking values before use.
/// </summary>
public static class AzureOpenAiDiagnostics
{
    public readonly record struct AzureErrorInfo(string? Code, string? Message);

    /// <summary>
    /// Extracts <c>error.code</c> and <c>error.message</c> from an Azure error body.  Returns
    /// nulls when the body is empty or not the expected shape.  The message is secret-redacted.
    /// </summary>
    public static AzureErrorInfo ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AzureErrorInfo(null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                return new AzureErrorInfo(SecretRedactor.Redact(code), SecretRedactor.Redact(message));
            }
        }
        catch (JsonException)
        {
            // Not JSON / unexpected shape: fall through to empty info; the raw body is never logged.
        }

        return new AzureErrorInfo(null, null);
    }
}
