using System.Text.Json;
using System.Text.RegularExpressions;

namespace JMD.Core;

public static class VoiceCommandGrammar
{
    public static string? CreateVoskWakeWordGrammar(bool enabled, IEnumerable<string> additionalWords)
    {
        if (!enabled) return null;
        var words = new[] { "jay" }.Concat(additionalWords)
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Select(VoicePhrasePolicy.Normalize)
            .Where(word => word.Length > 0 && !word.Contains(' '))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return JsonSerializer.Serialize(words);
    }

    public static string[] CreateSherpaHotwords(bool enabled, string modelId, IEnumerable<string> phrases)
    {
        if (!enabled || modelId is not (VoiceModelOptions.SherpaParakeetPtBr or VoiceModelOptions.SherpaZipformerEn)) return [];
        return phrases.Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Select(phrase => Regex.Replace(Regex.Replace(phrase, @"\{\{[A-Za-z][A-Za-z0-9_]{0,39}\}\}", " "), @"\s+", " ").Trim())
            .Where(phrase => phrase.Length > 0)
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
        if (commands.Any(VoicePhrasePolicy.HasTemplateVariables)) return null;
        return commands.Length == 0 ? null : JsonSerializer.Serialize(commands);
    }

    public static bool IsConfidenceAccepted(int minimumPercent, double? confidence)
    {
        var minimum = Math.Clamp(minimumPercent, 0, 100);
        return minimum == 0 || confidence is { } value && double.IsFinite(value) && value * 100 >= minimum;
    }
}
