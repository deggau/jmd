namespace JMD.Core;

public sealed record TransformationResult(
    bool Success,
    string? Value,
    string? Error,
    int? InputItemCount = null,
    int? OutputItemCount = null)
{
    public static TransformationResult Ok(string value, int? inputItemCount = null, int? outputItemCount = null)
        => new(true, value, null, inputItemCount, outputItemCount);
    public static TransformationResult Fail(string error) => new(false, null, error);
}

public sealed class SqlInConversionGuard
{
    private string? _originalValue;
    private string? _convertedValue;

    public void Remember(string originalValue, string convertedValue)
    {
        _originalValue = originalValue;
        _convertedValue = convertedValue;
    }

    public bool TryGetOriginalForConvertedValue(string value, out string? originalValue)
    {
        if (_originalValue is not null && _convertedValue is not null &&
            !string.Equals(_originalValue, _convertedValue, StringComparison.Ordinal) &&
            string.Equals(value, _convertedValue, StringComparison.Ordinal))
        {
            originalValue = _originalValue;
            return true;
        }

        originalValue = null;
        return false;
    }

    public TransformationResult TransformUnlessAlreadyConverted(string value, Func<string, TransformationResult> transform)
    {
        if (TryGetOriginalForConvertedValue(value, out var originalValue))
            return TransformationResult.Fail($"Esse valor já foi convertido para SQL IN. Valor original: {originalValue}");
        return transform(value);
    }
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
    Task<bool> WaitForModifiersReleasedAsync(CancellationToken cancellationToken = default);
    bool SendCopy();
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
        if (!string.Equals(await clipboard.TryGetTextAsync(cancellationToken), result.Value, StringComparison.Ordinal))
            return TransformationResult.Fail("O Windows não confirmou a atualização do clipboard. O resultado não foi aplicado.");
        return result;
    }
}

public sealed record SelectionTransformResult(
    bool Success,
    string Message,
    string? BeforeValue = null,
    string? AfterValue = null,
    bool UsedClipboardFallback = false,
    int? InputItemCount = null,
    int? OutputItemCount = null);

public sealed class SelectionTransformer(IClipboardText clipboard, IKeyboardAutomation keyboard, IAsyncDelay? delay = null)
{
    private readonly IAsyncDelay _delay = delay ?? new SystemAsyncDelay();

    public async Task<SelectionTransformResult> ExecuteAsync(Func<string, TransformationResult> transform, CancellationToken cancellationToken = default, bool allowClipboardFallback = true)
    {
        var sourceWindow = keyboard.GetForegroundWindow();
        if (sourceWindow == IntPtr.Zero) return new(false, "Não foi possível identificar a janela ativa.");
        var originalClipboard = await clipboard.TryGetTextAsync(cancellationToken);
        if (!await keyboard.WaitForModifiersReleasedAsync(cancellationToken))
            return new(false, "Solte Ctrl, Alt, Shift e Win para executar a substituição.");
        if (keyboard.GetForegroundWindow() != sourceWindow)
            return new(false, "A janela ativa mudou antes da cópia; nenhuma tecla foi enviada.");
        var sequenceBeforeCopy = clipboard.SequenceNumber;
        if (!keyboard.SendCopy()) return new(false, "O Windows não permitiu enviar Ctrl+C à janela ativa.");

        string? selectedText = null;
        var clipboardChanged = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await _delay.WaitAsync(TimeSpan.FromMilliseconds(25), cancellationToken);
            if (clipboard.SequenceNumber == sequenceBeforeCopy) continue;
            clipboardChanged = true;
            selectedText = await clipboard.TryGetTextAsync(cancellationToken);
            break;
        }

        if (selectedText is null)
        {
            if (clipboardChanged)
            {
                if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
                return new(false, "A seleção continua intacta, mas não contém texto Unicode.");
            }
            if (!clipboardChanged)
            {
                if (keyboard.GetForegroundWindow() != sourceWindow)
                    return new(false, "A janela ativa mudou. A seleção e o clipboard original foram mantidos.");
                if (originalClipboard is null)
                    return new(false, "Nenhuma seleção de texto foi detectada e o clipboard não contém texto Unicode.");
                if (!allowClipboardFallback)
                    return new(false, "Nenhuma seleção de texto foi detectada.");

                var clipboardResult = transform(originalClipboard);
                if (!clipboardResult.Success)
                    return new(false, clipboardResult.Error ?? "Não foi possível converter o texto do clipboard.");
                if (!await clipboard.TrySetTextAsync(clipboardResult.Value!, cancellationToken) ||
                    !string.Equals(await clipboard.TryGetTextAsync(cancellationToken), clipboardResult.Value, StringComparison.Ordinal))
                {
                    await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
                    return new(false, "Nenhuma seleção foi detectada e não foi possível atualizar o clipboard.");
                }

                return new(true, "Nenhuma seleção detectada; o texto do clipboard foi formatado para SQL IN.",
                    originalClipboard, clipboardResult.Value, UsedClipboardFallback: true,
                    InputItemCount: clipboardResult.InputItemCount, OutputItemCount: clipboardResult.OutputItemCount);
            }
            return new(false, "Não foi possível confirmar uma seleção de texto; nenhuma colagem foi enviada.");
        }

        if (keyboard.GetForegroundWindow() != sourceWindow)
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, "A janela ativa mudou. A seleção original permanece intacta.");
        }

        TransformationResult result;
        try
        {
            result = transform(selectedText);
        }
        catch (OperationCanceledException)
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, $"A transformação falhou: {exception.Message} A seleção original permanece intacta.");
        }
        if (!result.Success)
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, $"{result.Error ?? "A transformação falhou."} A seleção original permanece intacta.");
        }
        if (keyboard.GetForegroundWindow() != sourceWindow)
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, "A janela ativa mudou. A seleção original permanece intacta.");
        }
        if (!await clipboard.TrySetTextAsync(result.Value!, cancellationToken))
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, "Não foi possível atualizar o clipboard. A seleção original permanece intacta.");
        }
        if (!string.Equals(await clipboard.TryGetTextAsync(cancellationToken), result.Value, StringComparison.Ordinal))
        {
            if (originalClipboard is not null) await clipboard.TrySetTextAsync(originalClipboard, cancellationToken);
            return new(false, "O Windows não confirmou a atualização do clipboard. A seleção original permanece intacta.");
        }
        if (keyboard.GetForegroundWindow() != sourceWindow || !keyboard.SendPaste())
        {
            return new(false, "Não foi possível colar o resultado. A seleção original permanece intacta e o texto transformado continua no clipboard para colagem manual.");
        }
        return new(true, "Texto selecionado substituído.", selectedText, result.Value,
            InputItemCount: result.InputItemCount, OutputItemCount: result.OutputItemCount);
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
