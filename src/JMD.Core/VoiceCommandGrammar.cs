using System.Text.Json;

namespace JMD.Core;

public static class VoiceCommandGrammar
{
    public static string[] CreateSherpaHotwords(bool enabled, string modelId, IEnumerable<string> phrases)
    {
        if (!enabled || modelId is not (VoiceModelOptions.SherpaParakeetPtBr or VoiceModelOptions.SherpaZipformerEn)) return [];
        return phrases.Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Select(phrase => phrase.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? CreateVoskGrammar(bool enabled, string modelId, IEnumerable<string> phrases)
    {
        if (!enabled || !string.Equals(modelId, "vosk", StringComparison.Ordinal)) return null;

        var commands = phrases
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Select(phrase => phrase.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return commands.Length == 0 ? null : JsonSerializer.Serialize(commands);
    }

    public static bool IsConfidenceAccepted(int minimumPercent, double? confidence)
    {
        var minimum = Math.Clamp(minimumPercent, 0, 100);
        return minimum == 0 || confidence is { } value && double.IsFinite(value) && value * 100 >= minimum;
    }
}
