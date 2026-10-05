using DevToolbox.Core;
using DevToolbox.Tools;
using Xunit;

namespace DevToolbox.Tests;

public sealed class UseCaseTests
{
    private readonly SqlInListTransformation _sql = new();

    // UC-006: delimiters, escaping, whitespace, edge separators and ambiguous input.
    [Theory]
    [InlineData("abc,def,jeg", "'abc','def','jeg'")]
    [InlineData("1233456;asdasdas;asdsadas", "'1233456','asdasdas','asdsadas'")]
    [InlineData("abc|def", "'abc','def'")]
    [InlineData("abc\r\ndef", "'abc','def'")]
    [InlineData("abc\ndef", "'abc','def'")]
    [InlineData("abc\rdef", "'abc','def'")]
    [InlineData("  abc , def  ", "'abc','def'")]
    [InlineData(",abc,def,", "'abc','def'")]
    [InlineData("O'Brien, x", "'O''Brien','x'")]
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
        Assert.Equal("'a,b','c'", _sql.Transform("a,b;c", ";").Value);
        Assert.False(_sql.Transform("a,b", "~").Success);
    }

    // UC-001 and UC-003: clipboard orchestration preserves source on transform/write failure.
    [Fact]
    public async Task Clipboard_use_case_reads_transforms_and_writes_result()
    {
        var clipboard = new FakeClipboard { Text = "alpha,beta" };
        var result = await new ClipboardTransformationService(clipboard).ExecuteAsync(_sql.Transform);
        Assert.True(result.Success, result.Error);
        Assert.Equal("'alpha','beta'", clipboard.Text);
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
    }

    // UC-002 / UC-005: shortcut editor values, including supported aliases and invalid combinations.
    [Theory]
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
        Assert.Equal("'abc','def'", keyboard.PastedText);
        Assert.Equal(new[] { "cut", "paste" }, keyboard.Events);
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
        Assert.False((await new SelectionTransformer(noChangeClipboard, noChange, new ImmediateDelay()).ExecuteAsync(_sql.Transform)).Success);
        Assert.Empty(noChange.Events.Where(item => item == "paste"));
    }

    private sealed class FakeClipboard : IClipboardText
    {
        public string? Text { get; set; }
        public bool FailWrites { get; set; }
        public uint SequenceNumber { get; set; }
        public Task<string?> TryGetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task<bool> TrySetTextAsync(string value, CancellationToken cancellationToken = default)
        {
            if (FailWrites) return Task.FromResult(false);
            Text = value; SequenceNumber++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeKeyboard(FakeClipboard clipboard) : IKeyboardAutomation
    {
        public int Window { get; set; } = 1;
        public bool CutSucceeds { get; set; } = true;
        public bool PasteSucceeds { get; set; } = true;
        public string? SelectedTextOnCut { get; set; }
        public string? PastedText { get; private set; }
        public List<string> Events { get; } = [];
        public IntPtr GetForegroundWindow() => new(Window);
        public bool SendCut()
        {
            if (!CutSucceeds) return false;
            Events.Add("cut");
            if (SelectedTextOnCut is not null) { clipboard.Text = SelectedTextOnCut; clipboard.SequenceNumber++; }
            return true;
        }
        public bool SendPaste()
        {
            if (!PasteSucceeds) return false;
            Events.Add("paste"); PastedText = clipboard.Text; return true;
        }
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
