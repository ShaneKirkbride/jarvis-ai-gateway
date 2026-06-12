using System.Text.RegularExpressions;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IContentRedactor
{
    RedactionResult Redact(string text);
}

public sealed class RegexContentRedactor(IOptions<GatewayOptions> options) : IContentRedactor
{
    private readonly GatewayOptions _options = options.Value;

    private static readonly (Regex Regex, string Replacement)[] BuiltInPatterns =
    [
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "[REDACTED_AWS_ACCESS_KEY]"),
        (new Regex(@"(?i)(aws_secret_access_key\s*[=:]\s*)[A-Za-z0-9/+=]{32,}", RegexOptions.Compiled), "$1[REDACTED_AWS_SECRET]"),
        (new Regex(@"(?i)(api[_-]?key\s*[=:]\s*)[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "$1[REDACTED_API_KEY]"),
        (new Regex(@"(?i)(password\s*[=:]\s*)\S+", RegexOptions.Compiled), "$1[REDACTED_PASSWORD]"),
        (new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled), "[REDACTED_SSN]"),
        (new Regex(@"\b(?:\d[ -]*?){13,16}\b", RegexOptions.Compiled), "[REDACTED_POSSIBLE_CARD]"),
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled), "[REDACTED_PRIVATE_KEY]")
    ];

    public RedactionResult Redact(string text)
    {
        if (!_options.Redaction.Enabled || string.IsNullOrEmpty(text))
        {
            return new RedactionResult(text, 0);
        }

        var count = 0;
        var redacted = text;

        foreach (var (regex, replacement) in BuiltInPatterns)
        {
            var matches = regex.Matches(redacted).Count;
            if (matches > 0)
            {
                redacted = regex.Replace(redacted, replacement);
                count += matches;
            }
        }

        return new RedactionResult(redacted, count);
    }
}
