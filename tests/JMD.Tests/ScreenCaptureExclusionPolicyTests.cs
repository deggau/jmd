using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class ScreenCaptureExclusionPolicyTests
{
    [Theory]
    [InlineData(6, 3, 9600, false)]
    [InlineData(10, 0, 19040, false)]
    [InlineData(10, 0, 19041, true)]
    [InlineData(10, 0, 19045, true)]
    [InlineData(11, 0, 22000, true)]
    public void IsSupported_UsesWindows10Version2004AsMinimum(
        int major, int minor, int build, bool expected)
    {
        Assert.Equal(expected, ScreenCaptureExclusionPolicy.IsSupported(new Version(major, minor, build)));
    }
}
