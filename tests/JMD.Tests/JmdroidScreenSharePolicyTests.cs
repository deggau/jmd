using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class JmdroidScreenSharePolicyTests
{
    [Fact]
    public void Accepts_complete_jpeg_frames_within_the_size_limit()
        => Assert.True(JmdroidScreenSharePolicy.IsValidJpegFrame([0xFF, 0xD8, 0x11, 0xFF, 0xD9]));

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x11, 0xFF })]
    [InlineData(new byte[] { 0x00, 0xD8, 0x11, 0xFF, 0xD9 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x11, 0x00, 0xD9 })]
    public void Rejects_incomplete_or_non_jpeg_frames(byte[] frame)
        => Assert.False(JmdroidScreenSharePolicy.IsValidJpegFrame(frame));

    [Fact]
    public void Frame_limits_are_bounded_for_lan_streaming()
    {
        Assert.Equal(2 * 1024 * 1024, JmdroidScreenSharePolicy.MaxFrameBytes);
        Assert.Equal(12, JmdroidScreenSharePolicy.DefaultMaxFramesPerSecond);
        Assert.Equal(60, JmdroidScreenSharePolicy.MaxFramesPerSecond);
        Assert.Equal(2400, JmdroidScreenSharePolicy.MaxDimension);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    public void Normalizes_configured_fps_to_supported_range(int input, int expected)
        => Assert.Equal(expected, JmdroidScreenSharePolicy.NormalizeFramesPerSecond(input));

    [Fact]
    public void Frame_throttle_limits_updates_to_the_configured_rate()
    {
        var throttle = new JmdroidScreenShareFrameThrottle(15);
        var start = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

        Assert.True(throttle.TryAccept(start));
        Assert.False(throttle.TryAccept(start.AddMilliseconds(30)));
        Assert.True(throttle.TryAccept(start.AddMilliseconds(67)));
    }
}
