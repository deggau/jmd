namespace JMD.Core;

public enum AppPage
{
    Palette,
    ShortcutSettings,
    History,
    About
}

public sealed class AppNavigationState
{
    public AppPage CurrentPage { get; private set; } = AppPage.Palette;
    public bool IsEditingShortcuts => CurrentPage == AppPage.ShortcutSettings;

    public void ShowPalette() => CurrentPage = AppPage.Palette;
    public void ShowShortcutSettings() => CurrentPage = AppPage.ShortcutSettings;
    public void ShowHistory() => CurrentPage = AppPage.History;
    public void ShowAbout() => CurrentPage = AppPage.About;
}

public static class AppStartupOptions
{
    public const string OpenSettingsArgument = "--settings";

    public static bool StartsInShortcutSettings(IEnumerable<string> arguments)
        => arguments.Any(argument => string.Equals(argument, OpenSettingsArgument, StringComparison.OrdinalIgnoreCase));
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
        _ => throw new ArgumentOutOfRangeException(nameof(commandId), commandId, "Comando desconhecido.")
    };

    public static void MigratePreviousDefaults(IDictionary<string, string> shortcuts)
    {
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
