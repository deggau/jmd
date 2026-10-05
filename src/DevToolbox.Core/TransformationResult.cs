namespace DevToolbox.Core;

public sealed record TransformationResult(bool Success, string? Value, string? Error)
{
    public static TransformationResult Ok(string value) => new(true, value, null);
    public static TransformationResult Fail(string error) => new(false, null, error);
}

public interface ITextTransformation
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    TransformationResult Transform(string input);
}

public interface IClipboardText
{
    Task<string?> TryGetTextAsync(CancellationToken cancellationToken = default);
    Task<bool> TrySetTextAsync(string value, CancellationToken cancellationToken = default);
    uint SequenceNumber { get; }
}

public interface IKeyboardAutomation
{
    IntPtr GetForegroundWindow();
    bool SendCut();
    bool SendPaste();
}

public interface IAsyncDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class ClipboardTransformationService(IClipboardText clipboard)
{
    public async Task<TransformationResult> ExecuteAsync(Func<string, TransformationResult> transform, CancellationToken cancellationToken = default)
    {
        var input = await clipboard.TryGetTextAsync(cancellationToken);
        if (input is null) return TransformationResult.Fail("O clipboard não contém texto Unicode.");
        var result = transform(input);
        if (!result.Success) return result;
        if (!await clipboard.TrySetTextAsync(result.Value!, cancellationToken))
            return TransformationResult.Fail("Não foi possível gravar o resultado no clipboard.");
        return result;
    }
}

public sealed record SelectionTransformResult(bool Success, string Message);

public sealed class SelectionTransformer(IClipboardText clipboard, IKeyboardAutomation keyboard, IAsyncDelay? delay = null)
{
    private readonly IAsyncDelay _delay = delay ?? new SystemAsyncDelay();

    public async Task<SelectionTransformResult> ExecuteAsync(Func<string, TransformationResult> transform, CancellationToken cancellationToken = default)
    {
        var sourceWindow = keyboard.GetForegroundWindow();
        if (sourceWindow == IntPtr.Zero) return new(false, "Não foi possível identificar a janela ativa.");
        var originalClipboard = await clipboard.TryGetTextAsync(cancellationToken);
        var sequenceBeforeCut = clipboard.SequenceNumber;
        if (!keyboard.SendCut()) return new(false, "O Windows não permitiu enviar Ctrl+X à janela ativa.");

        string? selectedText = null;
        var clipboardChanged = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await _delay.WaitAsync(TimeSpan.FromMilliseconds(25), cancellationToken);
            if (clipboard.SequenceNumber == sequenceBeforeCut) continue;
            clipboardChanged = true;
            selectedText = await clipboard.TryGetTextAsync(cancellationToken);
            break;
        }

        if (selectedText is null)
        {
            if (clipboardChanged && keyboard.GetForegroundWindow() == sourceWindow)
            {
                var restored = keyboard.SendPaste();
                if (restored && originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
                return new(false, restored ? "A seleção não é texto Unicode; ela foi restaurada sem transformação." : "A seleção não é texto Unicode. O conteúdo recortado continua no clipboard para recuperação manual.");
            }
            return new(false, "Não foi possível confirmar uma seleção de texto; nenhuma colagem foi enviada.");
        }

        var result = transform(selectedText);
        if (!result.Success)
        {
            var restored = await RestoreSelectionAsync(sourceWindow, selectedText, cancellationToken);
            if (restored && originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, restored ? result.Error ?? "A transformação falhou; a seleção foi restaurada." : $"{result.Error ?? "A transformação falhou."} Não consegui restaurar o texto ao editor; ele continua no clipboard.");
        }
        if (keyboard.GetForegroundWindow() != sourceWindow)
        {
            await clipboard.TrySetTextAsync(selectedText, cancellationToken);
            return new(false, "A janela ativa mudou. O texto original foi mantido no clipboard, mas não foi colado.");
        }
        if (!await clipboard.TrySetTextAsync(result.Value!, cancellationToken))
        {
            var restored = await RestoreSelectionAsync(sourceWindow, selectedText, cancellationToken);
            return new(false, restored ? "Não foi possível atualizar o clipboard; a seleção original foi restaurada." : "Não foi possível atualizar o clipboard nem restaurar a seleção. O texto original continua em memória nesta operação.");
        }
        if (keyboard.GetForegroundWindow() != sourceWindow || !keyboard.SendPaste())
        {
            await clipboard.TrySetTextAsync(selectedText, cancellationToken);
            return new(false, "Não foi possível colar o resultado. O texto recortado foi restaurado no clipboard.");
        }
        return new(true, "Texto selecionado substituído.");
    }

    private async Task<bool> RestoreSelectionAsync(IntPtr sourceWindow, string selectedText, CancellationToken cancellationToken)
    {
        if (!await clipboard.TrySetTextAsync(selectedText, cancellationToken)) return false;
        return keyboard.GetForegroundWindow() == sourceWindow && keyboard.SendPaste();
    }
}

public static class ShortcutParser
{
    public static bool TryParse(string text, out ShortcutBinding? binding, out string? error)
    {
        binding = null; error = null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { error = "Use ao menos um modificador e uma tecla."; return false; }
        var modifiers = ShortcutModifiers.None;
        foreach (var modifier in parts[..^1]) modifiers |= modifier.ToLowerInvariant() switch
        {
            "ctrl" or "control" => ShortcutModifiers.Control, "alt" => ShortcutModifiers.Alt,
            "shift" => ShortcutModifiers.Shift, "win" or "windows" => ShortcutModifiers.Windows, _ => ShortcutModifiers.None
        };
        if (modifiers == ShortcutModifiers.None) { error = "A combinação precisa incluir Ctrl, Alt, Shift ou Win."; return false; }
        var keyName = parts[^1].ToUpperInvariant();
        var key = keyName.Length == 1 && char.IsAsciiLetterOrDigit(keyName[0]) ? keyName[0] : keyName switch
        {
            "SPACE" => 0x20, "TAB" => 0x09, "ENTER" => 0x0D, "ESC" => 0x1B,
            "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "END" => 0x23, "HOME" => 0x24,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            _ when keyName.Length > 1 && keyName[0] == 'F' && int.TryParse(keyName[1..], out var f) && f is >= 1 and <= 24 => 0x6F + f, _ => 0
        };
        if (key == 0) { error = "A tecla não é reconhecida. Use uma letra, número, F1–F24 ou tecla de navegação."; return false; }
        binding = new ShortcutBinding(modifiers, key); return true;
    }
}

public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record ShortcutBinding(ShortcutModifiers Modifiers, int VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyNames.FromVirtualKey(VirtualKey));
        return string.Join("+", parts);
    }
}

public static class KeyNames
{
    public static string FromVirtualKey(int key)
    {
        if (key is >= 0x41 and <= 0x5A) return ((char)key).ToString();
        if (key is >= 0x30 and <= 0x39) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x6F}";
        return key switch
        {
            0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc",
            0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            _ => $"VK {key:X2}"
        };
    }
}
