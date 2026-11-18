namespace JMD.Core;

public static class JmdroidScreenSharePolicy
{
    public const int MaxFrameBytes = 2 * 1024 * 1024;
    public const int DefaultMaxFramesPerSecond = 12;
    public const int MaxFramesPerSecond = 60;
    public const int MaxDimension = 2400;

    public static bool IsValidJpegFrame(ReadOnlySpan<byte> frame)
        => frame.Length is >= 4 and <= MaxFrameBytes &&
           frame[0] == 0xFF && frame[1] == 0xD8 &&
           frame[^2] == 0xFF && frame[^1] == 0xD9;

    public static int NormalizeFramesPerSecond(int framesPerSecond)
        => Math.Clamp(framesPerSecond, DefaultMaxFramesPerSecond, MaxFramesPerSecond);
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
