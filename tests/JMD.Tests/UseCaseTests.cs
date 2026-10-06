using JMD.Core;
using JMD.Tools;
using Xunit;

namespace JMD.Tests;

public sealed class UseCaseTests
{
    private readonly SqlInListTransformation _sql = new();

    // UC-006: delimiters, escaping, whitespace, edge separators and ambiguous input.
    [Theory]
    [InlineData("abc,def,jeg", "'abc',\r\n'def',\r\n'jeg'")]
    [InlineData("1233456;asdasdas;asdsadas", "'1233456',\r\n'asdasdas',\r\n'asdsadas'")]
    [InlineData("abc|def", "'abc',\r\n'def'")]
    [InlineData("abc\r\ndef", "'abc',\r\n'def'")]
    [InlineData("abc\ndef", "'abc',\r\n'def'")]
    [InlineData("abc\rdef", "'abc',\r\n'def'")]
    [InlineData("  abc , def  ", "'abc',\r\n'def'")]
    [InlineData(",abc,def,", "'abc',\r\n'def'")]
    [InlineData("O'Brien, x", "'O''Brien',\r\n'x'")]
    [InlineData("one", "'one'")]
    public void SqlTransformation_formats_supported_cases(string input, string expected)
    {
        var result = _sql.Transform(input);
        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Value);
        Assert.DoesNotContain(",", result.Value![^1..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(",,,")]
    [InlineData("a,,b")]
    [InlineData("a,b;c")]
    public void SqlTransformation_rejects_empty_or_ambiguous_input(string input)
    {
        Assert.False(_sql.Transform(input).Success);
    }

    [Fact]
    public void SqlTransformation_honors_preferred_delimiter_and_rejects_unknown_one()
    {
        Assert.Equal("'a,b',\r\n'c'", _sql.Transform("a,b;c", ";").Value);
        Assert.False(_sql.Transform("a,b", "~").Success);
    }

    [Fact]
    public void SqlTransformation_removes_exact_duplicates_and_keeps_first_occurrence_order()
    {
        var result = _sql.Transform("beta,alpha,beta,ALPHA,alpha");

        Assert.True(result.Success, result.Error);
        Assert.Equal("'beta',\r\n'alpha',\r\n'ALPHA'", result.Value);
        Assert.Equal(5, result.InputItemCount);
        Assert.Equal(3, result.OutputItemCount);
    }

    [Fact]
    public void SqlTransformation_applies_distinct_to_large_lists_too()
    {
        var tenThousand = string.Join(',', Enumerable.Repeat("x", 10_000));
        var tenThousandAndOne = string.Join(',', Enumerable.Repeat("x", 10_001));

        Assert.Equal("'x'", _sql.Transform(tenThousand).Value);
        var aboveLimit = _sql.Transform(tenThousandAndOne);
        Assert.True(aboveLimit.Success, aboveLimit.Error);
        Assert.Equal("'x'", aboveLimit.Value);
    }

    [Fact]
    public void SqlTransformation_breaks_values_into_lines_using_repetition_thresholds()
    {
        var input = string.Join(',', Enumerable.Range(1, 31));

        var result = _sql.Transform(input);

        Assert.True(result.Success, result.Error);
        var lineSizes = result.Value!.Split("\r\n").Select(line => line.Split("','", StringSplitOptions.None).Length);
        Assert.Equal(new[] { 6, 6, 6, 6, 6, 1 }, lineSizes);
        Assert.False(result.Value.EndsWith(",", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 3)]
    [InlineData(10, 3)]
    [InlineData(11, 4)]
    [InlineData(30, 4)]
    [InlineData(31, 6)]
    [InlineData(60, 6)]
    [InlineData(61, 10)]
    [InlineData(100, 10)]
    [InlineData(101, 12)]
    [InlineData(200, 12)]
    [InlineData(201, 25)]
    [InlineData(500, 25)]
    [InlineData(501, 50)]
    [InlineData(1_000, 50)]
    [InlineData(1_001, 100)]
    [InlineData(2_000, 100)]
    [InlineData(2_001, 250)]
    [InlineData(5_000, 250)]
    [InlineData(5_001, 500)]
    [InlineData(10_000, 500)]
    [InlineData(10_001, 800)]
    [InlineData(15_000, 800)]
    [InlineData(15_001, 1_000)]
    [InlineData(20_000, 1_000)]
    [InlineData(20_001, 2_000)]
    public void SqlTransformation_uses_the_configured_items_per_line_threshold(int repetitions, int expected)
        => Assert.Equal(expected, SqlInListTransformation.GetItemsPerLine(repetitions));

    // UC-001 and UC-003: clipboard orchestration preserves source on transform/write failure.
    [Fact]
    public async Task Clipboard_use_case_reads_transforms_and_writes_result()
    {
        var clipboard = new FakeClipboard { Text = "alpha,beta" };
        var result = await new ClipboardTransformationService(clipboard).ExecuteAsync(_sql.Transform);
        Assert.True(result.Success, result.Error);
        Assert.Equal("'alpha',\r\n'beta'", clipboard.Text);
    }

    [Fact]
    public async Task Clipboard_use_case_does_not_overwrite_on_failure()
    {
        var clipboard = new FakeClipboard { Text = "a,b;c" };
        var result = await new ClipboardTransformationService(clipboard).ExecuteAsync(_sql.Transform);
        Assert.False(result.Success);
        Assert.Equal("a,b;c", clipboard.Text);
    }

    [Fact]
    public async Task Clipboard_use_case_reports_unavailable_clipboard_and_write_failure()
    {
        var absent = new FakeClipboard { Text = null };
        Assert.False((await new ClipboardTransformationService(absent).ExecuteAsync(_sql.Transform)).Success);
        var blocked = new FakeClipboard { Text = "a,b", FailWrites = true };
        Assert.False((await new ClipboardTransformationService(blocked).ExecuteAsync(_sql.Transform)).Success);
        Assert.Equal("a,b", blocked.Text);

        var ignored = new FakeClipboard { Text = "a,b", IgnoreWrites = true };
        var ignoredResult = await new ClipboardTransformationService(ignored).ExecuteAsync(_sql.Transform);
        Assert.False(ignoredResult.Success);
        Assert.Equal("a,b", ignored.Text);
    }

    // UC-002 / UC-005: shortcut editor values, including supported aliases and invalid combinations.
    [Theory]
    [InlineData("Alt+J", ShortcutModifiers.Alt, 0x4A)]
    [InlineData("Win+J", ShortcutModifiers.Windows, 0x4A)]
    [InlineData("Ctrl+Alt+I", ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x49)]
    [InlineData("Ctrl+Shift+I", ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x49)]
    [InlineData("Control + F12", ShortcutModifiers.Control, 0x7B)]
    public void Shortcut_parser_reads_default_and_supported_bindings(string text, ShortcutModifiers modifiers, int key)
    {
        Assert.True(ShortcutParser.TryParse(text, out var binding, out var error), error);
        Assert.Equal(new ShortcutBinding(modifiers, key), binding);
    }

    [Theory]
    [InlineData("I")]
    [InlineData("Ctrl+MadeUp")]
    [InlineData("Ctrl+F25")]
    [InlineData("Nope+J")]
    public void Shortcut_parser_rejects_invalid_bindings(string text)
    {
        Assert.False(ShortcutParser.TryParse(text, out var binding, out _));
        Assert.Null(binding);
    }

    // UC-004: selection replace pipeline and recovery paths, with fake OS boundaries.
    [Fact]
    public async Task Selection_use_case_cuts_transforms_and_pastes()
    {
        var clipboard = new FakeClipboard { Text = "previous" };
        var keyboard = new FakeKeyboard(clipboard) { SelectedTextOnCut = "abc,def" };
        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(_sql.Transform);
        Assert.True(result.Success, result.Message);
        Assert.Equal("abc,def", result.BeforeValue);
        Assert.Equal("'abc',\r\n'def'", keyboard.PastedText);
        Assert.Equal("'abc',\r\n'def'", result.AfterValue);
        Assert.Equal(2, result.InputItemCount);
        Assert.Equal(2, result.OutputItemCount);
        Assert.Equal(new[] { "cut", "paste" }, keyboard.Events);
        Assert.Equal(new[] { "wait", "cut", "paste" }, keyboard.CallOrder);
    }

    [Fact]
    public async Task Selection_use_case_waits_until_hotkey_modifiers_are_released_before_cutting()
    {
        var clipboard = new FakeClipboard { Text = "alpha,beta" };
        var keyboard = new FakeKeyboard(clipboard) { ModifiersReleased = false };

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(_sql.Transform);

        Assert.False(result.Success);
        Assert.Contains("Solte Ctrl, Alt", result.Message);
        Assert.Empty(keyboard.Events);
        Assert.Equal(new[] { "wait" }, keyboard.CallOrder);
        Assert.Equal("alpha,beta", clipboard.Text);
    }

    [Fact]
    public async Task Selection_use_case_does_not_cut_if_foreground_window_changes_while_waiting()
    {
        var clipboard = new FakeClipboard { Text = "alpha,beta" };
        var keyboard = new FakeKeyboard(clipboard) { WindowAfterWait = 2 };

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(_sql.Transform);

        Assert.False(result.Success);
        Assert.Contains("janela ativa mudou", result.Message);
        Assert.Equal(new[] { "wait" }, keyboard.CallOrder);
        Assert.Empty(keyboard.Events);
        Assert.Equal("alpha,beta", clipboard.Text);
    }

    [Fact]
    public async Task Selection_use_case_restores_text_instead_of_converting_the_previous_output_again()
    {
        var clipboard = new FakeClipboard { Text = "prior clipboard" };
        var keyboard = new FakeKeyboard(clipboard) { SelectedTextOnCut = "'alpha','beta'" };
        var guard = new SqlInConversionGuard();
        guard.Remember("alpha,beta", "'alpha','beta'");

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay())
            .ExecuteAsync(value => guard.TransformUnlessAlreadyConverted(value, _sql.Transform));

        Assert.False(result.Success);
        Assert.Contains("alpha,beta", result.Message);
        Assert.Equal("'alpha','beta'", keyboard.PastedText);
        Assert.Equal(new[] { "cut", "paste" }, keyboard.Events);
    }

    [Fact]
    public async Task Selection_use_case_formats_existing_clipboard_when_no_selection_is_detected()
    {
        var clipboard = new FakeClipboard { Text = "alpha,beta" };
        var keyboard = new FakeKeyboard(clipboard);

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(_sql.Transform);

        Assert.True(result.Success, result.Message);
        Assert.True(result.UsedClipboardFallback);
        Assert.Equal("alpha,beta", result.BeforeValue);
        Assert.Equal("'alpha',\r\n'beta'", result.AfterValue);
        Assert.Equal("'alpha',\r\n'beta'", clipboard.Text);
        Assert.Null(keyboard.PastedText);
        Assert.Equal(new[] { "cut" }, keyboard.Events);
        Assert.Equal(new[] { "wait", "cut" }, keyboard.CallOrder);
    }

    [Theory]
    [InlineData("{\"a\":1}", StructuredDataLayout.Pretty, StructuredDataFormat.Json)]
    [InlineData("{ \"a\": 1 }", StructuredDataLayout.Compact, StructuredDataFormat.Json)]
    [InlineData("<root><item>1</item></root>", StructuredDataLayout.Pretty, StructuredDataFormat.Xml)]
    [InlineData("<root>\n <item>1</item>\n</root>", StructuredDataLayout.Compact, StructuredDataFormat.Xml)]
    public async Task Structured_data_shortcut_uses_cut_transform_paste_pipeline(
        string selectedText, StructuredDataLayout layout, StructuredDataFormat expectedFormat)
    {
        var formatter = new StructuredDataFormatter();
        var clipboard = new FakeClipboard { Text = "previous clipboard" };
        var keyboard = new FakeKeyboard(clipboard) { SelectedTextOnCut = selectedText };
        StructuredDataFormat? detectedFormat = null;

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(value =>
        {
            var formatted = formatter.Transform(value, layout);
            detectedFormat = formatted.Format;
            return formatted.Transformation;
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedFormat, detectedFormat);
        Assert.Equal(selectedText, result.BeforeValue);
        Assert.Equal(result.AfterValue, keyboard.PastedText);
        Assert.Equal(new[] { "wait", "cut", "paste" }, keyboard.CallOrder);
        Assert.Contains("paste", keyboard.Events);
    }

    [Theory]
    [InlineData("{\"a\":1}", StructuredDataLayout.Pretty)]
    [InlineData("<root><item>1</item></root>", StructuredDataLayout.Compact)]
    public async Task Structured_data_shortcut_uses_clipboard_when_no_selection_exists(
        string clipboardText, StructuredDataLayout layout)
    {
        var formatter = new StructuredDataFormatter();
        var clipboard = new FakeClipboard { Text = clipboardText };
        var keyboard = new FakeKeyboard(clipboard);

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(
            value => formatter.Transform(value, layout).Transformation);

        Assert.True(result.Success, result.Message);
        Assert.True(result.UsedClipboardFallback);
        Assert.Equal(clipboardText, result.BeforeValue);
        Assert.Equal(result.AfterValue, clipboard.Text);
        Assert.Null(keyboard.PastedText);
        Assert.Contains("cut", keyboard.Events);
        Assert.DoesNotContain("paste", keyboard.Events);
    }

    [Fact]
    public async Task Selection_use_case_keeps_clipboard_when_fallback_conversion_fails()
    {
        var clipboard = new FakeClipboard { Text = "a,,b" };
        var keyboard = new FakeKeyboard(clipboard);

        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(_sql.Transform);

        Assert.False(result.Success);
        Assert.False(result.UsedClipboardFallback);
        Assert.Equal("a,,b", clipboard.Text);
        Assert.Null(keyboard.PastedText);
    }

    [Fact]
    public async Task Selection_use_case_restores_original_selection_when_transformation_fails()
    {
        var clipboard = new FakeClipboard { Text = "prior" };
        var keyboard = new FakeKeyboard(clipboard) { SelectedTextOnCut = "a,,b" };
        var result = await new SelectionTransformer(clipboard, keyboard, new ImmediateDelay()).ExecuteAsync(_sql.Transform);
        Assert.False(result.Success);
        Assert.Equal("a,,b", keyboard.PastedText);
        Assert.Equal("prior", clipboard.Text);
    }

    [Fact]
    public async Task Selection_use_case_does_not_paste_if_cut_fails_or_selection_is_not_detected()
    {
        var noCutClipboard = new FakeClipboard { Text = "prior" };
        var noCut = new FakeKeyboard(noCutClipboard) { CutSucceeds = false };
        Assert.False((await new SelectionTransformer(noCutClipboard, noCut, new ImmediateDelay()).ExecuteAsync(_sql.Transform)).Success);
        Assert.Empty(noCut.Events);

        var noChangeClipboard = new FakeClipboard { Text = "prior" };
        var noChange = new FakeKeyboard(noChangeClipboard);
        var fallback = await new SelectionTransformer(noChangeClipboard, noChange, new ImmediateDelay()).ExecuteAsync(_sql.Transform);
        Assert.True(fallback.Success, fallback.Message);
        Assert.True(fallback.UsedClipboardFallback);
        Assert.Empty(noChange.Events.Where(item => item == "paste"));

        var emptyClipboard = new FakeClipboard { Text = null };
        var noText = new FakeKeyboard(emptyClipboard);
        Assert.False((await new SelectionTransformer(emptyClipboard, noText, new ImmediateDelay()).ExecuteAsync(_sql.Transform)).Success);
        Assert.Empty(noText.Events.Where(item => item == "paste"));
    }

    private sealed class FakeClipboard : IClipboardText
    {
        public string? Text { get; set; }
        public bool FailWrites { get; set; }
        public bool IgnoreWrites { get; set; }
        public uint SequenceNumber { get; set; }
        public Task<string?> TryGetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task<bool> TrySetTextAsync(string value, CancellationToken cancellationToken = default)
        {
            if (FailWrites) return Task.FromResult(false);
            if (IgnoreWrites) return Task.FromResult(true);
            Text = value; SequenceNumber++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeKeyboard(FakeClipboard clipboard) : IKeyboardAutomation
    {
        public int Window { get; set; } = 1;
        public bool CutSucceeds { get; set; } = true;
        public bool PasteSucceeds { get; set; } = true;
        public bool ModifiersReleased { get; set; } = true;
        public int? WindowAfterWait { get; set; }
        public string? SelectedTextOnCut { get; set; }
        public string? PastedText { get; private set; }
        public List<string> Events { get; } = [];
        public List<string> CallOrder { get; } = [];
        public IntPtr GetForegroundWindow() => new(Window);
        public Task<bool> WaitForModifiersReleasedAsync(CancellationToken cancellationToken = default)
        {
            CallOrder.Add("wait");
            if (WindowAfterWait is { } window) Window = window;
            return Task.FromResult(ModifiersReleased);
        }
        public bool SendCut()
        {
            CallOrder.Add("cut");
            if (!CutSucceeds) return false;
            Events.Add("cut");
            if (SelectedTextOnCut is not null) { clipboard.Text = SelectedTextOnCut; clipboard.SequenceNumber++; }
            return true;
        }
        public bool SendPaste()
        {
            CallOrder.Add("paste");
            if (!PasteSucceeds) return false;
            Events.Add("paste"); PastedText = clipboard.Text; return true;
        }
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
