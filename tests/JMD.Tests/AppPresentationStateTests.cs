using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class AppPresentationStateTests
{
    [Theory]
    [InlineData("--settings", true)]
    [InlineData("--SETTINGS", true)]
    [InlineData("--other", false)]
    public void Startup_option_opens_shortcut_settings_only_when_requested(string argument, bool expected)
    {
        Assert.Equal(expected, AppStartupOptions.StartsInShortcutSettings([argument]));
    }

    [Fact]
    public void Startup_without_arguments_opens_the_palette_default()
    {
        Assert.False(AppStartupOptions.StartsInShortcutSettings([]));
    }

    [Fact]
    public void Shortcut_defaults_assign_selection_and_clipboard_to_requested_bindings()
    {
        Assert.Equal("Ctrl+Alt+I", ShortcutDefaults.ForCommand("selection"));
        Assert.Equal("Ctrl+Shift+I", ShortcutDefaults.ForCommand("clipboard"));
        Assert.Equal("Alt+J", ShortcutDefaults.ForCommand("palette"));
        Assert.Equal("Ctrl+Alt+B", ShortcutDefaults.ForCommand("jsonPretty"));
        Assert.Equal("Alt+Shift+B", ShortcutDefaults.ForCommand("jsonCompact"));
        Assert.Equal("Ctrl+Shift+Up", ShortcutDefaults.ForCommand("draftSave"));
        Assert.Equal("Ctrl+Shift+Down", ShortcutDefaults.ForCommand("draftRestore"));
        Assert.True(ShortcutParser.TryParse(ShortcutDefaults.ForCommand("jsonPretty"), out _, out _));
        Assert.True(ShortcutParser.TryParse(ShortcutDefaults.ForCommand("jsonCompact"), out _, out _));
        Assert.True(ShortcutParser.TryParse(ShortcutDefaults.ForCommand("draftSave"), out var saveBinding, out _));
        Assert.Equal(ShortcutModifiers.Control | ShortcutModifiers.Shift, saveBinding?.Modifiers);
        Assert.Equal(0x26, saveBinding?.VirtualKey);
        Assert.True(ShortcutParser.TryParse(ShortcutDefaults.ForCommand("draftRestore"), out var restoreBinding, out _));
        Assert.Equal(ShortcutModifiers.Control | ShortcutModifiers.Shift, restoreBinding?.Modifiers);
        Assert.Equal(0x28, restoreBinding?.VirtualKey);
    }

    [Fact]
    public void Shortcut_migration_updates_only_the_old_draft_defaults()
    {
        var shortcuts = new Dictionary<string, string>
        {
            ["draftSave"] = "Shift+Up",
            ["draftRestore"] = "Ctrl+Alt+Down"
        };

        ShortcutDefaults.MigratePreviousDefaults(shortcuts);

        Assert.Equal("Ctrl+Shift+Up", shortcuts["draftSave"]);
        Assert.Equal("Ctrl+Alt+Down", shortcuts["draftRestore"]);
    }

    [Fact]
    public void Shortcut_migration_swaps_old_defaults_but_preserves_custom_bindings()
    {
        var oldDefaults = new Dictionary<string, string>
        {
            ["palette"] = "Win+J",
            ["clipboard"] = "Ctrl+Alt+I",
            ["selection"] = "Ctrl+Shift+I"
        };

        ShortcutDefaults.MigratePreviousDefaults(oldDefaults);

        Assert.Equal("Alt+J", oldDefaults["palette"]);
        Assert.Equal("Ctrl+Shift+I", oldDefaults["clipboard"]);
        Assert.Equal("Ctrl+Alt+I", oldDefaults["selection"]);

        var custom = new Dictionary<string, string>
        {
            ["palette"] = "Ctrl+Alt+P",
            ["clipboard"] = "Ctrl+Alt+I",
            ["selection"] = "Ctrl+F12"
        };
        ShortcutDefaults.MigratePreviousDefaults(custom);
        Assert.Equal("Ctrl+Alt+P", custom["palette"]);
        Assert.Equal("Ctrl+Alt+I", custom["clipboard"]);
        Assert.Equal("Ctrl+F12", custom["selection"]);
    }

    [Fact]
    public void Navigation_enters_shortcut_edit_mode_then_leaves_it_for_palette_or_history()
    {
        var navigation = new AppNavigationState();
        Assert.False(navigation.IsEditingShortcuts);

        navigation.ShowShortcutSettings();
        Assert.True(navigation.IsEditingShortcuts);

        navigation.ShowPalette();
        Assert.False(navigation.IsEditingShortcuts);
        Assert.Equal(AppPage.Palette, navigation.CurrentPage);

        navigation.ShowHistory();
        Assert.False(navigation.IsEditingShortcuts);
        Assert.Equal(AppPage.History, navigation.CurrentPage);
    }

    [Theory]
    [InlineData("0.1.2", "v0.1.3", true)]
    [InlineData("0.1.2+build.4", "0.1.2", false)]
    [InlineData("0.2.0", "v0.1.9", false)]
    public void Release_comparison_detects_only_newer_github_versions(string installed, string tag, bool expected)
    {
        Assert.Equal(expected, ReleaseVersionComparison.IsUpdateAvailable(installed, tag));
    }

    [Fact]
    public void Release_comparison_rejects_malformed_version_tags()
    {
        Assert.Throws<FormatException>(() => ReleaseVersionComparison.IsUpdateAvailable("0.1.2", "latest"));
    }
}
