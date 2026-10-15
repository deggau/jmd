using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace JMD.Core;

public readonly record struct TimestampedAudioFrame(byte[] Audio, DateTimeOffset CapturedAt);

/// <summary>Buffers microphone frames so capture callbacks can return while audio is processed elsewhere.</summary>
public sealed class AudioFrameQueue : IDisposable
{
    private readonly Channel<TimestampedAudioFrame> _frames;

    public AudioFrameQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _frames = Channel.CreateBounded<TimestampedAudioFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public bool TryWrite(ReadOnlySpan<byte> frame)
        => TryWrite(frame, DateTimeOffset.Now);

    public bool TryWrite(ReadOnlySpan<byte> frame, DateTimeOffset capturedAt)
        => frame.Length > 0 && _frames.Writer.TryWrite(new TimestampedAudioFrame(frame.ToArray(), capturedAt));

    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in ReadTimestampedFramesAsync(cancellationToken))
            yield return frame.Audio;
    }

    public IAsyncEnumerable<TimestampedAudioFrame> ReadTimestampedFramesAsync(CancellationToken cancellationToken = default)
        => _frames.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _frames.Writer.TryComplete();

    public void Dispose() => Complete();
}
