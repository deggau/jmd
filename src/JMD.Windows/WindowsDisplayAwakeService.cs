using System.Runtime.InteropServices;
using JMD.Core;

namespace JMD.Windows;

public sealed class WindowsDisplayAwakeService : IDisposable
{
    private readonly DisplayAwakeController _controller = new(request =>
        SetThreadExecutionState((uint)request) != 0);

    public bool IsEnabled => _controller.IsEnabled;

    public bool SetEnabled(bool enabled) => _controller.SetEnabled(enabled);

    public void Dispose() => _controller.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);
}
