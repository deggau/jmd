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
}
