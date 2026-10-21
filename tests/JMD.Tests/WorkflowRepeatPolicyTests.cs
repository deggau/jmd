using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WorkflowRepeatPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void AcceptsRepeatCountsWithinRange(int count)
    {
        Assert.True(WorkflowRepeatPolicy.IsValidCount(count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsRepeatCountsOutsideRange(int count)
    {
        Assert.False(WorkflowRepeatPolicy.IsValidCount(count));
    }
}
