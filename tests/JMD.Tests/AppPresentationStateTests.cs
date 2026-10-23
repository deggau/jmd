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
    [MemberData(nameof(CommandLineInputs))]
    public void Command_line_arguments_parse_to_help_list_or_search(string[] args, JmdCommandLineRequest expected)
        => Assert.Equal(expected, JmdCommandLinePolicy.Parse(args));

    public static IEnumerable<object[]> CommandLineInputs =>
    [
        [Array.Empty<string>(), new JmdCommandLineRequest("help")],
        [new[] { "-help" }, new JmdCommandLineRequest("help")],
        [new[] { "--ls" }, new JmdCommandLineRequest("list")],
        [new[] { "-LS" }, new JmdCommandLineRequest("list")],
        [new[] { "-ls", "--verbose" }, new JmdCommandLineRequest("list", Verbose: true)],
        [new[] { "-search", "upper", "case" }, new JmdCommandLineRequest("search", "upper case")],
        [new[] { "-run", "2", "UpperCase" }, new JmdCommandLineRequest("run", "UpperCase", 2)],
        [new[] { "-run", "2" }, new JmdCommandLineRequest("run", Index: 2)],
        [new[] { "-run", "zero" }, new JmdCommandLineRequest("invalid", "Use -run <número> [texto da busca].")],
        [new[] { "-complete", "Upper" }, new JmdCommandLineRequest("complete", "Upper")],
        [new[] { "-install-completion" }, new JmdCommandLineRequest("installCompletion")],
        [new[] { "-uninstall-completion" }, new JmdCommandLineRequest("uninstallCompletion")],
        [new[] { "formatar", "clipboard" }, new JmdCommandLineRequest("execute", "formatar clipboard")]
    ];

    [Theory]
    [InlineData("run", 1, 0)]
    [InlineData("run", 3, 2)]
    [InlineData("execute", null, 0)]
    [InlineData("search", null, 0)]
    [InlineData("RUN", 2, 1)]
    public void Command_line_resolves_one_based_run_numbers_to_zero_based_result_indexes(
        string operation, int? requestedIndex, int expectedIndex)
        => Assert.Equal(expectedIndex, JmdCommandLinePolicy.ResolveSelectedIndex(operation, requestedIndex));

    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(-1, 3, false)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    [InlineData(0, 0, false)]
    public void Command_line_checks_selected_result_against_available_results(
        int selectedIndex, int resultCount, bool expected)
        => Assert.Equal(expected, JmdCommandLinePolicy.IsValidSelectedIndex(selectedIndex, resultCount));

    [Theory]
    [InlineData("-search", "", "search", "")]
    [InlineData("--search", "texto com espaço", "search", "texto com espaço")]
    [InlineData("-complete", "Upper", "complete", "Upper")]
    [InlineData("-run", "01", "run", "")]
    public void Command_line_keeps_empty_queries_and_completion_prefixes_intact(
        string option, string value, string expectedOperation, string expectedQuery)
    {
        var request = JmdCommandLinePolicy.Parse([option, value]);
        Assert.Equal(expectedOperation, request.Operation);
        Assert.Equal(expectedQuery, request.Query);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Command_line_rejects_invalid_run_indexes(string index)
        => Assert.Equal("invalid", JmdCommandLinePolicy.Parse(["-run", index]).Operation);

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
        Assert.Equal("Ctrl+Shift+U", ShortcutDefaults.ForCommand("upperCase"));
        Assert.Equal("Ctrl+Shift+L", ShortcutDefaults.ForCommand("lowerCase"));
        Assert.Equal("Ctrl+Shift+T", ShortcutDefaults.ForCommand("titleCase"));
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

    [Theory]
    [InlineData("já é HORA DE MUDAR", "JÁ É HORA DE MUDAR")]
    public void Upper_case_transforms_all_letters(string input, string expected)
        => Assert.Equal(expected, TextCaseTransform.Upper(input));

    [Fact]
    public void Lower_case_transforms_all_letters()
        => Assert.Equal("ação rápida", TextCaseTransform.Lower("Ação RÁPIDA"));

    [Theory]
    [InlineData("um TESTE de TÍTULO para o JMD", "Um Teste de Título para o Jmd")]
    [InlineData("TEXTO, SOBRE A VIDA", "Texto, sobre a Vida")]
    public void Title_case_keeps_portuguese_prepositions_lowercase(string input, string expected)
        => Assert.Equal(expected, TextCaseTransform.Title(input));

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
    public void Voice_phrase_template_captures_a_dynamic_value_preserving_case()
    {
        Assert.True(VoicePhrasePolicy.TryMatchTemplate("Abra a tarefa {{tarefaId}}", "abra a tarefa AbC-12345", out var variables));
        Assert.Equal("AbC 12345", variables["tarefaId"]);
    }

    [Fact]
    public void Voice_phrase_template_captures_multiple_values_between_literal_words()
    {
        Assert.True(VoicePhrasePolicy.TryMatchTemplate("Abra {{tipo}} número {{id}}", "Abra tarefa número 12345", out var variables));
        Assert.Equal("tarefa", variables["tipo"]);
        Assert.Equal("12345", variables["id"]);
    }

    [Fact]
    public void Voice_phrase_template_requires_literal_words_and_nonempty_values()
    {
        Assert.False(VoicePhrasePolicy.TryMatchTemplate("Abra a tarefa {{tarefaId}}", "Abra a tarefa", out _));
        Assert.False(VoicePhrasePolicy.TryMatchTemplate("Abra a tarefa {{tarefaId}}", "Abra a nota 12345", out _));
    }

    [Fact]
    public void Voice_phrase_policy_detects_duplicate_templates_even_when_variable_names_differ()
    {
        Assert.True(VoicePhrasePolicy.HasDuplicate(["Abra tarefa {{id}}", "Abra tarefa {{numero}}"]));
    }

    [Fact]
    public void Voice_phrase_policy_recognizes_and_removes_command_prefix()
    {
        Assert.True(VoicePhrasePolicy.StartsWith("transcrever", "Transcrever... Exemplo de teste!"));
        Assert.Equal("exemplo de teste", VoicePhrasePolicy.RemovePrefix("transcrever", "Transcrever... Exemplo de teste!"));
        Assert.False(VoicePhrasePolicy.StartsWith("transcrever", "transcrever"));
        Assert.False(VoicePhrasePolicy.StartsWith("transcrever", "transcrevendo texto"));
    }

    [Fact]
    public void Voice_dictation_policy_ends_after_three_seconds_without_speech()
    {
        var lastSpeechAt = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        Assert.False(VoiceDictationPolicy.HasSilenceTimedOut(lastSpeechAt, lastSpeechAt.AddMilliseconds(2999)));
        Assert.True(VoiceDictationPolicy.HasSilenceTimedOut(lastSpeechAt, lastSpeechAt.AddSeconds(3)));
        Assert.False(VoiceDictationPolicy.HasReachedMaximumSegment(25 * VoiceDictationPolicy.PcmBytesPerSecond - 1));
        Assert.True(VoiceDictationPolicy.HasReachedMaximumSegment(25 * VoiceDictationPolicy.PcmBytesPerSecond));
    }

    [Fact]
    public void Voice_model_idle_policy_unloads_at_fifteen_minutes()
    {
        var lastUsedAt = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        Assert.False(VoiceModelIdlePolicy.ShouldUnload(lastUsedAt, lastUsedAt.AddMinutes(14).AddSeconds(59)));
        Assert.True(VoiceModelIdlePolicy.ShouldUnload(lastUsedAt, lastUsedAt.AddMinutes(15)));
    }

    [Theory]
    [InlineData("Jey", true)]
    [InlineData("JAY", true)]
    [InlineData("J.", true)]
    [InlineData("SQL", false)]
    public void Wake_word_policy_accepts_jey_jay_and_the_confidence_gated_letter_j(string token, bool expected)
        => Assert.Equal(expected, VoiceWakeWordPolicy.IsWakeWordToken(token));

    [Theory]
    [InlineData("J", 0, 80)]
    [InlineData("J.", 90, 90)]
    [InlineData("Jey", 0, 0)]
    [InlineData("Jay", 65, 65)]
    public void Letter_j_wake_word_uses_a_confidence_floor(string token, int configuredMinimum, int expectedMinimum)
        => Assert.Equal(expectedMinimum, VoiceWakeWordPolicy.GetMinimumConfidencePercent(token, configuredMinimum));

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

    [Fact]
    public void Closed_vosk_grammar_falls_back_to_open_vocabulary_for_dynamic_voice_phrases()
    {
        Assert.Null(VoiceCommandGrammar.CreateVoskGrammar(true, "vosk", ["Abra a tarefa {{tarefaId}}"]));
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
