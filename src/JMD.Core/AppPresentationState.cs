using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JMD.Core;

public enum AppPage
{
    Palette,
    ShortcutSettings,
    History,
    About,
    JeySettings
}

public sealed class AppNavigationState
{
    public AppPage CurrentPage { get; private set; } = AppPage.Palette;
    public bool IsEditingShortcuts => CurrentPage == AppPage.ShortcutSettings;

    public void ShowPalette() => CurrentPage = AppPage.Palette;
    public void ShowShortcutSettings() => CurrentPage = AppPage.ShortcutSettings;
    public void ShowHistory() => CurrentPage = AppPage.History;
    public void ShowAbout() => CurrentPage = AppPage.About;
    public void ShowJeySettings() => CurrentPage = AppPage.JeySettings;
}

public static class AppStartupOptions
{
    public const string OpenSettingsArgument = "--settings";
    public const string UpdateArgument = "--update";

    public static bool StartsInShortcutSettings(IEnumerable<string> arguments)
        => arguments.Any(argument => string.Equals(argument, OpenSettingsArgument, StringComparison.OrdinalIgnoreCase));

    public static bool StartsUpdate(IEnumerable<string> arguments)
        => arguments.Any(argument => string.Equals(argument, UpdateArgument, StringComparison.OrdinalIgnoreCase));
}

public static class ShortcutDefaults
{
    public static string ForCommand(string commandId) => commandId switch
    {
        "palette" => "Alt+J",
        "clipboard" => "Ctrl+Shift+I",
        "selection" => "Ctrl+Alt+I",
        "jsonPretty" => "Ctrl+Alt+B",
        "jsonCompact" => "Alt+Shift+B",
        "snippets" => "Ctrl+Shift+Y",
        "saveSnippet" => "Ctrl+Alt+Shift+Y",
        "draftSave" => "Ctrl+Shift+Up",
        "draftRestore" => "Ctrl+Shift+Down",
        _ => throw new ArgumentOutOfRangeException(nameof(commandId), commandId, "Comando desconhecido.")
    };

    public static void MigratePreviousDefaults(IDictionary<string, string> shortcuts)
    {
        if (shortcuts.TryGetValue("draftSave", out var draftSave) && draftSave == "Shift+Up")
            shortcuts["draftSave"] = ForCommand("draftSave");
        if (shortcuts.TryGetValue("draftRestore", out var draftRestore) && draftRestore == "Shift+Down")
            shortcuts["draftRestore"] = ForCommand("draftRestore");

        if (shortcuts.TryGetValue("palette", out var palette) && palette == "Win+J")
            shortcuts["palette"] = ForCommand("palette");

        if (shortcuts.TryGetValue("clipboard", out var clipboard) &&
            shortcuts.TryGetValue("selection", out var selection) &&
            clipboard == "Ctrl+Alt+I" && selection == "Ctrl+Shift+I")
        {
            shortcuts["clipboard"] = ForCommand("clipboard");
            shortcuts["selection"] = ForCommand("selection");
        }
    }
}

public static class VoicePhrasePolicy
{
    private static readonly Regex TemplateVariable = new(@"\{\{([A-Za-z][A-Za-z0-9_]{0,39})\}\}", RegexOptions.Compiled);

    public static bool Matches(string? configuredPhrase, string? recognizedPhrase)
    {
        var configured = Normalize(configuredPhrase);
        var recognized = Normalize(recognizedPhrase);
        return configured.Length > 0 && string.Equals(configured, recognized, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryMatchTemplate(string? configuredPhrase, string? recognizedPhrase,
        out IReadOnlyDictionary<string, string> variables)
    {
        variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configuredPhrase) || string.IsNullOrWhiteSpace(recognizedPhrase)) return false;
        var placeholders = TemplateVariable.Matches(configuredPhrase);
        if (placeholders.Count == 0) return false;

        var pattern = new StringBuilder("^");
        var names = new List<string>();
        var position = 0;
        foreach (Match placeholder in placeholders)
        {
            var literal = Normalize(configuredPhrase[position..placeholder.Index]);
            if (literal.Length > 0)
            {
                if (pattern.Length > 1) pattern.Append(@"\s+");
                pattern.Append(Regex.Escape(literal).Replace(@"\ ", @"\s+"));
            }
            if (pattern.Length > 1) pattern.Append(@"\s+");
            var groupName = $"value{names.Count}";
            names.Add(placeholder.Groups[1].Value);
            pattern.Append($"(?<{groupName}>[\\p{{L}}\\p{{N}}]+(?:\\s+[\\p{{L}}\\p{{N}}]+)*)");
            position = placeholder.Index + placeholder.Length;
        }

        var finalLiteral = Normalize(configuredPhrase[position..]);
        if (finalLiteral.Length > 0)
        {
            pattern.Append(@"\s+");
            pattern.Append(Regex.Escape(finalLiteral).Replace(@"\ ", @"\s+"));
        }
        pattern.Append('$');

        Match match;
        try { match = Regex.Match(NormalizePreservingCase(recognizedPhrase), pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success) return false;

        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < names.Count; index++)
            captured[names[index]] = match.Groups[$"value{index}"].Value.Trim();
        variables = captured;
        return true;
    }

    public static bool HasTemplateVariables(string? phrase)
        => !string.IsNullOrWhiteSpace(phrase) && TemplateVariable.IsMatch(phrase);

    private static string NormalizePreservingCase(string phrase)
    {
        var decomposed = phrase.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (char.IsLetterOrDigit(character)) result.Append(character);
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().Trim();
    }

    public static string Normalize(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return string.Empty;
        var decomposed = phrase.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().Trim();
    }

    private static string NormalizeForConflict(string? phrase)
        => Normalize(string.IsNullOrWhiteSpace(phrase) ? phrase : TemplateVariable.Replace(phrase, " variavel "));

    public static bool HasDuplicate(IEnumerable<string> phrases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in phrases)
        {
            var normalized = NormalizeForConflict(phrase);
            if (normalized.Length > 0 && !seen.Add(normalized)) return true;
        }
        return false;
    }

    public static bool ConflictsWith(IEnumerable<string> phrases, IEnumerable<string> existingPhrases)
    {
        var existing = existingPhrases.Select(NormalizeForConflict).Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return phrases.Select(NormalizeForConflict).Any(value => value.Length > 0 && existing.Contains(value));
    }

    public static bool StartsWith(string? configuredPhrase, string? recognizedPhrase)
    {
        var configured = Normalize(configuredPhrase);
        var recognized = Normalize(recognizedPhrase);
        return configured.Length > 0 && recognized.Length > configured.Length &&
            recognized.StartsWith(configured, StringComparison.OrdinalIgnoreCase) &&
            recognized[configured.Length] == ' ';
    }

    public static string RemovePrefix(string? configuredPhrase, string? recognizedPhrase)
    {
        if (!StartsWith(configuredPhrase, recognizedPhrase)) return string.Empty;
        var configured = Normalize(configuredPhrase);
        var recognized = Normalize(recognizedPhrase);
        return recognized[(configured.Length + 1)..].Trim();
    }
}

public static class VoiceDictationPolicy
{
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaximumSegmentDuration = TimeSpan.FromSeconds(25);
    public const int PcmBytesPerSecond = 32_000;

    public static bool HasSilenceTimedOut(DateTimeOffset lastSpeechAt, DateTimeOffset now)
        => now - lastSpeechAt >= SilenceTimeout;

    public static bool HasReachedMaximumSegment(int pcmByteLength)
        => pcmByteLength >= MaximumSegmentDuration.TotalSeconds * PcmBytesPerSecond;
}

public static class VoiceModelIdlePolicy
{
    public static readonly TimeSpan UnloadTimeout = TimeSpan.FromMinutes(15);

    public static bool ShouldUnload(DateTimeOffset lastUsedAt, DateTimeOffset now)
        => now - lastUsedAt >= UnloadTimeout;
}

public static class VoiceWakeWordPolicy
{
    public const int AmbiguousLetterConfidenceFloorPercent = 80;

    public static bool IsWakeWordToken(string? token)
    {
        var normalized = VoicePhrasePolicy.Normalize(token);
        return normalized is "jay" or "jey" or "jei" or "j";
    }

    public static int GetMinimumConfidencePercent(string? token, int configuredMinimum)
    {
        var minimum = Math.Clamp(configuredMinimum, 0, 100);
        return VoicePhrasePolicy.Normalize(token) == "j"
            ? Math.Max(minimum, AmbiguousLetterConfidenceFloorPercent)
            : minimum;
    }
}

public static class ReleaseVersionComparison
{
    public static bool IsUpdateAvailable(string installedVersion, string releaseTag)
    {
        var normalizedTag = releaseTag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(installedVersion.Split('+')[0], out var installed) ||
            !Version.TryParse(normalizedTag, out var latest))
            throw new FormatException("A versão instalada ou a versão da release é inválida.");

        return latest > installed;
    }
}
