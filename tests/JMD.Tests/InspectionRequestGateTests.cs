using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class InspectionRequestGateTests
{
    [Fact]
    public void Repeated_pointer_position_is_inspected_only_once()
    {
        var gate = new InspectionRequestGate<(int X, int Y)>();

        Assert.True(gate.TryAccept((10, 20)));
        Assert.False(gate.TryAccept((10, 20)));
        Assert.True(gate.TryAccept((11, 20)));
    }

    [Fact]
    public void Reset_allows_inspecting_the_same_position_again()
    {
        var gate = new InspectionRequestGate<string>();

        Assert.True(gate.TryAccept("field"));
        gate.Reset();
        Assert.True(gate.TryAccept("field"));
    }

    [Fact]
    public void Pointer_must_be_stationary_before_inspection_and_same_target_is_not_repeated()
    {
        var gate = new InspectionRequestGate<(int X, int Y)>();
        var quietPeriod = TimeSpan.FromMilliseconds(300);

        Assert.False(gate.TryAcceptAfterDelay((10, 20), TimeSpan.Zero, quietPeriod));
        Assert.False(gate.TryAcceptAfterDelay((11, 20), TimeSpan.FromMilliseconds(200), quietPeriod));
        Assert.False(gate.TryAcceptAfterDelay((11, 20), TimeSpan.FromMilliseconds(400), quietPeriod));
        Assert.True(gate.TryAcceptAfterDelay((11, 20), TimeSpan.FromMilliseconds(501), quietPeriod));
        Assert.False(gate.TryAcceptAfterDelay((11, 20), TimeSpan.FromMilliseconds(900), quietPeriod));
    }

    [Fact]
    public void Reset_after_entering_inspector_allows_rechecking_same_control()
    {
        var gate = new InspectionRequestGate<string>();
        var quietPeriod = TimeSpan.FromMilliseconds(300);
        Assert.False(gate.TryAcceptAfterDelay("field", TimeSpan.Zero, quietPeriod));
        Assert.True(gate.TryAcceptAfterDelay("field", TimeSpan.FromMilliseconds(301), quietPeriod));

        gate.Reset();

        Assert.False(gate.TryAcceptAfterDelay("field", TimeSpan.FromSeconds(1), quietPeriod));
        Assert.True(gate.TryAcceptAfterDelay("field", TimeSpan.FromMilliseconds(1301), quietPeriod));
    }
}
