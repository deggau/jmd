using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class AppPresentationStateTests
{
    [Fact]
    public void Shortcut_defaults_assign_selection_and_clipboard_to_requested_bindings()
    {
        Assert.Equal("Ctrl+Alt+I", ShortcutDefaults.ForCommand("selection"));
        Assert.Equal("Ctrl+Shift+I", ShortcutDefaults.ForCommand("clipboard"));
        Assert.Equal("Alt+J", ShortcutDefaults.ForCommand("palette"));
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
