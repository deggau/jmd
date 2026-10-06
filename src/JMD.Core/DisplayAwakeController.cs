namespace JMD.Core;

[Flags]
public enum ExecutionStateRequest : uint
{
    Continuous = 0x80000000,
    SystemRequired = 0x00000001,
    DisplayRequired = 0x00000002
}

public sealed class DisplayAwakeController(Func<ExecutionStateRequest, bool> applyRequest) : IDisposable
{
    public bool IsEnabled { get; private set; }

    public bool SetEnabled(bool enabled)
    {
        if (enabled == IsEnabled) return true;

        var request = enabled
            ? ExecutionStateRequest.Continuous | ExecutionStateRequest.SystemRequired | ExecutionStateRequest.DisplayRequired
            : ExecutionStateRequest.Continuous;
        if (!applyRequest(request)) return false;

        IsEnabled = enabled;
        return true;
    }

    public void Dispose()
    {
        if (IsEnabled) SetEnabled(false);
    }
}
