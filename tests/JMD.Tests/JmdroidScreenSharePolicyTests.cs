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
        Assert.Equal(512 * 1024, JmdroidScreenSharePolicy.DesktopToAndroidMaxFrameBytes);
        Assert.Equal(1440, JmdroidScreenSharePolicy.DesktopToAndroidMaxDimension);
        Assert.Equal(2400, JmdroidScreenSharePolicy.DesktopToAndroidZoomMaxDimension);
    }

    [Theory]
    [InlineData(1080, 2400, 1440, 648, 1440)]
    [InlineData(1920, 1080, 1440, 1440, 810)]
    [InlineData(640, 480, 1440, 640, 480)]
    public void Fits_capture_to_mobile_target_without_distorting_aspect_ratio(
        int width, int height, int maxDimension, int expectedWidth, int expectedHeight)
    {
        var result = JmdroidScreenSharePolicy.FitDimensions(width, height, maxDimension);
        Assert.Equal((expectedWidth, expectedHeight), result);
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 2400, 1440, 1080, 608)]
    [InlineData(2560, 1440, 1080, 2400, 1440, 1080, 608)]
    [InlineData(1920, 1080, 400, 400, 1440, 400, 225)]
    [InlineData(640, 480, 1200, 1200, 1440, 640, 480)]
    public void Fits_desktop_capture_inside_phone_viewport_without_upscaling_or_distortion(
        int screenWidth, int screenHeight, int viewportWidth, int viewportHeight, int maxDimension,
        int expectedWidth, int expectedHeight)
    {
        var result = JmdroidScreenSharePolicy.FitScreenToViewport(
            screenWidth, screenHeight, viewportWidth, viewportHeight, maxDimension);
        Assert.Equal((expectedWidth, expectedHeight), result);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(0.25, 0.25, 0.5, 0.5)]
    [InlineData(0.01, 0.01, 0.02, 0.02)]
    public void Accepts_full_screen_and_valid_normalized_viewports(double x, double y, double width, double height)
    {
        Assert.True(JmdroidScreenSharePolicy.TryCreateDesktopViewport(x, y, width, height, out var viewport));
        Assert.Equal(new JmdroidScreenShareViewport(x, y, width, height), viewport);
    }

    [Theory]
    [InlineData(-0.01, 0, 0.5, 0.5)]
    [InlineData(0, -0.01, 0.5, 0.5)]
    [InlineData(0.8, 0, 0.3, 0.5)]
    [InlineData(0, 0.8, 0.5, 0.3)]
    [InlineData(0, 0, 0.019, 1)]
    [InlineData(0, 0, 1, 0)]
    public void Rejects_invalid_or_too_small_normalized_viewports(double x, double y, double width, double height)
        => Assert.False(JmdroidScreenSharePolicy.TryCreateDesktopViewport(x, y, width, height, out _));

    [Fact]
    public void Rejects_non_finite_normalized_viewports()
    {
        Assert.False(JmdroidScreenSharePolicy.TryCreateDesktopViewport(double.NaN, 0, 1, 1, out _));
        Assert.False(JmdroidScreenSharePolicy.TryCreateDesktopViewport(0, double.PositiveInfinity, 1, 1, out _));
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
