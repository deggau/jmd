using System.Runtime.InteropServices;
using JMD.Core;

namespace JMD.Windows;

public sealed class KeyboardAutomation : IKeyboardAutomation
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkX = 0x58;
    private const ushort VkV = 0x56;
    private static readonly int[] ModifierKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    public IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    public async Task<bool> WaitForModifiersReleasedAsync(CancellationToken cancellationToken = default)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (ModifierKeys.Any(IsKeyDown))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= timeout) return false;
            await Task.Delay(10, cancellationToken);
        }
        return true;
    }

    public bool SendCut() => SendChord(VkX);
    public bool SendPaste() => SendChord(VkV);

    private static bool SendChord(ushort key)
    {
        var inputs = new[]
        {
            Key(VkControl, false), Key(key, false), Key(key, true), Key(VkControl, true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = keyUp ? KeyUp : 0 } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] private MouseInput _mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
