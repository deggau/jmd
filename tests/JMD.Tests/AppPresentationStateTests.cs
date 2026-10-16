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

    [Theory]
    [InlineData("--update", true)]
    [InlineData("--UPDATE", true)]
    [InlineData("--settings", false)]
    public void Startup_option_requests_update_only_when_requested(string argument, bool expected)
    {
        Assert.Equal(expected, AppStartupOptions.StartsUpdate([argument]));
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

        navigation.ShowJeySettings();
        Assert.False(navigation.IsEditingShortcuts);
        Assert.Equal(AppPage.JeySettings, navigation.CurrentPage);
    }

    [Fact]
    public void Voice_phrase_policy_detects_duplicate_phrases_across_commands()
    {
        Assert.True(VoicePhrasePolicy.ConflictsWith(["formatar json"], ["Formatar, JSON!"]));
        Assert.True(VoicePhrasePolicy.ConflictsWith(["acao"], ["ação"]));
        Assert.False(VoicePhrasePolicy.ConflictsWith(["compactar"], ["formatar"]));
    }

    [Fact]
    public void Voice_phrase_policy_detects_duplicates_inside_one_command()
    {
        Assert.True(VoicePhrasePolicy.HasDuplicate(["formatar JSON", "Formatar, JSON!"]));
        Assert.False(VoicePhrasePolicy.HasDuplicate(["formatar", "compactar"]));
    }

    [Theory]
    [InlineData("abrir sobre", "Abrir sobre!")]
    [InlineData("verificar atualizações", "Verificar atualizações!!!")]
    [InlineData("abrir sobre!", "abrir, sobre?")]
    [InlineData("abrir histórico", "Abrir Histórico.")]
    [InlineData("ABRIR HISTÓRICO", "abrir histórico.")]
    public void Voice_phrase_matching_ignores_punctuation(string configuredPhrase, string recognizedPhrase)
    {
        Assert.True(VoicePhrasePolicy.Matches(configuredPhrase, recognizedPhrase));
    }

    [Fact]
    public void Voice_phrase_matching_rejects_different_words()
    {
        Assert.False(VoicePhrasePolicy.Matches("abrir sobre", "abrir configurações!"));
    }

    [Fact]
    public void Closed_vosk_command_grammar_contains_only_distinct_nonempty_configured_phrases()
    {
        var grammar = VoiceCommandGrammar.CreateVoskGrammar(
            true, "vosk", [" Abrir sobre ", "", "Atualizar JMD", "abrir sobre"]);

        Assert.NotNull(grammar);
        Assert.Equal(["Abrir sobre", "Atualizar JMD"],
            System.Text.Json.JsonSerializer.Deserialize<string[]>(grammar));
    }

    [Theory]
    [InlineData(false, "vosk")]
    [InlineData(true, "whisperTiny")]
    public void Closed_command_grammar_is_not_used_when_disabled_or_transcribing_with_another_model(
        bool enabled, string modelId)
    {
        Assert.Null(VoiceCommandGrammar.CreateVoskGrammar(enabled, modelId, ["abrir sobre"]));
    }

    [Fact]
    public void Closed_command_grammar_falls_back_to_open_vocabulary_when_no_phrases_are_configured()
    {
        Assert.Null(VoiceCommandGrammar.CreateVoskGrammar(true, "vosk", ["", "  "]));
    }

    [Theory]
    [InlineData("sherpaParakeetPtBr")]
    [InlineData("sherpaZipformerEn")]
    public void Closed_command_grammar_creates_distinct_sherpa_hotwords(string modelId)
    {
        var hotwords = VoiceCommandGrammar.CreateSherpaHotwords(true, modelId,
            ["abrir configurações", "Abrir configurações", "  fechar janela  "]);
        Assert.Equal(["abrir configurações", "fechar janela"], hotwords);
    }

    [Fact]
    public void Closed_command_grammar_does_not_create_sherpa_hotwords_when_disabled_or_using_other_models()
    {
        Assert.Empty(VoiceCommandGrammar.CreateSherpaHotwords(false, "sherpaParakeetPtBr", ["abrir configurações"]));
        Assert.Empty(VoiceCommandGrammar.CreateSherpaHotwords(true, "whisperTiny", ["abrir configurações"]));
        Assert.Empty(VoiceCommandGrammar.CreateSherpaHotwords(true, "sherpaZipformerEn", ["", "  "]));
    }

    [Theory]
    [InlineData(0, null, true)]
    [InlineData(70, 0.70, true)]
    [InlineData(70, 0.69, false)]
    [InlineData(1, null, false)]
    [InlineData(100, 1.0, true)]
    public void Command_minimum_confidence_accepts_only_candidates_at_or_above_the_threshold(
        int minimumPercent, double? confidence, bool expected)
    {
        Assert.Equal(expected, VoiceCommandGrammar.IsConfidenceAccepted(minimumPercent, confidence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("¿…?!")]
    public void Voice_phrase_normalization_treats_empty_or_punctuation_only_transcripts_as_empty(string transcript)
    {
        Assert.Equal(string.Empty, VoicePhrasePolicy.Normalize(transcript));
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
