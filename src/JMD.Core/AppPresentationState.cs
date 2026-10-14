using System.Globalization;
using System.Text;

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
    public static bool Matches(string? configuredPhrase, string? recognizedPhrase)
    {
        var configured = Normalize(configuredPhrase);
        return configured.Length > 0 && configured == Normalize(recognizedPhrase);
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

    public static bool HasDuplicate(IEnumerable<string> phrases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in phrases)
        {
            var normalized = Normalize(phrase);
            if (normalized.Length > 0 && !seen.Add(normalized)) return true;
        }
        return false;
    }

    public static bool ConflictsWith(IEnumerable<string> phrases, IEnumerable<string> existingPhrases)
    {
        var existing = existingPhrases.Select(Normalize).Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return phrases.Select(Normalize).Any(value => value.Length > 0 && existing.Contains(value));
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
