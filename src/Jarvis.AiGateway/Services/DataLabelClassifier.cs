namespace Jarvis.AiGateway.Services;

public static class DataLabelClassifier
{
    public static bool IsItar(string? dataLabel)
    {
        if (string.IsNullOrWhiteSpace(dataLabel)) return false;

        var normalized = dataLabel.Trim().Replace('-', '_').ToUpperInvariant();
        return normalized == "ITAR" || normalized.StartsWith("ITAR_", StringComparison.Ordinal);
    }
}
