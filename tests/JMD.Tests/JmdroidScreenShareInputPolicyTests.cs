using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class JmdroidScreenShareInputPolicyTests
{
    [Theory]
    [InlineData("tap", 0, 0, 0, 0)]
    [InlineData("tap", 1, 1, 0, 0)]
    [InlineData("scroll", 0.5, 0.5, 0, 120)]
    [InlineData("scroll", 0.5, 0.5, -1200, -1200)]
    public void IsValid_AcceptsSupportedGesture(string action, double x, double y, int deltaX, int deltaY)
        => Assert.True(JmdroidScreenShareInputPolicy.IsValid(action, x, y, deltaX, deltaY));

    [Theory]
    [InlineData("rightClick", 0.5, 0.5, 0, 0)]
    [InlineData("tap", -0.01, 0.5, 0, 0)]
    [InlineData("tap", 0.5, 1.01, 0, 0)]
    [InlineData("tap", 0.5, 0.5, 0, 120)]
    [InlineData("scroll", 0.5, 0.5, 0, 1201)]
    public void IsValid_RejectsUnsupportedGesture(string action, double x, double y, int deltaX, int deltaY)
        => Assert.False(JmdroidScreenShareInputPolicy.IsValid(action, x, y, deltaX, deltaY));

    [Fact]
    public void IsValid_RejectsNonFiniteCoordinates()
    {
        Assert.False(JmdroidScreenShareInputPolicy.IsValid("tap", double.NaN, 0.5, 0, 0));
        Assert.False(JmdroidScreenShareInputPolicy.IsValid("scroll", 0.5, double.PositiveInfinity, 0, 120));
    }
}
