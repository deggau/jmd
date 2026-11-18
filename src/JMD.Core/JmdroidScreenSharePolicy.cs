namespace JMD.Core;

public static class JmdroidScreenSharePolicy
{
    public const int MaxFrameBytes = 2 * 1024 * 1024;
    public const int DefaultMaxFramesPerSecond = 12;
    public const int MaxFramesPerSecond = 60;
    public const int MaxDimension = 2400;
    public const int DesktopToAndroidMaxFrameBytes = 512 * 1024;
    public const int DesktopToAndroidMaxDimension = 1440;
    public const int DesktopToAndroidJpegQuality = 55;

    public static bool IsValidJpegFrame(ReadOnlySpan<byte> frame)
        => frame.Length is >= 4 and <= MaxFrameBytes &&
           frame[0] == 0xFF && frame[1] == 0xD8 &&
           frame[^2] == 0xFF && frame[^1] == 0xD9;

    public static int NormalizeFramesPerSecond(int framesPerSecond)
        => Math.Clamp(framesPerSecond, DefaultMaxFramesPerSecond, MaxFramesPerSecond);

    public static (int Width, int Height) FitDimensions(int width, int height, int maxDimension)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        var scale = Math.Min(1d, (double)maxDimension / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static (int Width, int Height) FitScreenToViewport(
        int screenWidth, int screenHeight, int viewportWidth, int viewportHeight, int maxDimension)
    {
        if (screenWidth <= 0) throw new ArgumentOutOfRangeException(nameof(screenWidth));
        if (screenHeight <= 0) throw new ArgumentOutOfRangeException(nameof(screenHeight));
        if (viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        var scale = Math.Min(1d, Math.Min(
            Math.Min((double)viewportWidth / screenWidth, (double)viewportHeight / screenHeight),
            (double)maxDimension / Math.Max(screenWidth, screenHeight)));
        return (Math.Max(1, (int)Math.Round(screenWidth * scale)), Math.Max(1, (int)Math.Round(screenHeight * scale)));
    }
}

public sealed class JmdroidScreenShareFrameThrottle(int framesPerSecond = JmdroidScreenSharePolicy.DefaultMaxFramesPerSecond)
{
    private readonly object _sync = new();
    private DateTimeOffset _lastAcceptedAt;

    public bool TryAccept(DateTimeOffset now)
    {
        if (framesPerSecond is < JmdroidScreenSharePolicy.DefaultMaxFramesPerSecond or > JmdroidScreenSharePolicy.MaxFramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        lock (_sync)
        {
            if (_lastAcceptedAt != default && now - _lastAcceptedAt < TimeSpan.FromSeconds(1d / framesPerSecond))
                return false;
            _lastAcceptedAt = now;
            return true;
        }
    }
}
