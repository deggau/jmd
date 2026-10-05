using System.Runtime.InteropServices;
using System.Windows.Interop;
using DevToolbox.Core;

namespace DevToolbox.Windows;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyIdBase = 0x4A00;
    private readonly HwndSource _source;
    private readonly Dictionary<string, (int Id, ShortcutBinding Binding)> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _commandsById = [];
    private int _nextId = HotkeyIdBase;

    public event Action<string>? HotkeyPressed;

    public GlobalHotkeyService(HwndSource source)
    {
        _source = source;
        _source.AddHook(WindowHook);
    }

    public bool TryRegister(string commandId, ShortcutBinding binding, out string? error)
    {
        if (_registrations.TryGetValue(commandId, out var current) && current.Binding == binding)
        {
            error = null;
            return true;
        }
        var id = _nextId++;
        var modifiers = ModNoRepeat;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= ModAlt;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Control)) modifiers |= ModControl;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= ModShift;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Windows)) modifiers |= ModWindows;

        if (!RegisterHotKey(_source.Handle, id, modifiers, (uint)binding.VirtualKey))
        {
            error = "Atalho indisponível: o Windows ou outro aplicativo já pode estar usando essa combinação.";
            return false;
        }

        _registrations[commandId] = (id, binding);
        _commandsById[id] = commandId;
        if (current.Id != 0)
        {
            UnregisterHotKey(_source.Handle, current.Id);
            _commandsById.Remove(current.Id);
        }
        error = null;
        return true;
    }

    public void Unregister(string commandId)
    {
        if (!_registrations.Remove(commandId, out var registration)) return;
        UnregisterHotKey(_source.Handle, registration.Id);
        _commandsById.Remove(registration.Id);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _commandsById.TryGetValue(wParam.ToInt32(), out var commandId))
        {
            HotkeyPressed?.Invoke(commandId);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WindowHook);
        foreach (var commandId in _registrations.Keys.ToArray()) Unregister(commandId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
