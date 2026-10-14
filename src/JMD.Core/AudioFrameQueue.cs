using System.Threading.Channels;

namespace JMD.Core;

/// <summary>Buffers microphone frames so capture callbacks can return while audio is processed elsewhere.</summary>
public sealed class AudioFrameQueue : IDisposable
{
    private readonly Channel<byte[]> _frames;

    public AudioFrameQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public bool TryWrite(ReadOnlySpan<byte> frame)
        => frame.Length > 0 && _frames.Writer.TryWrite(frame.ToArray());

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken cancellationToken = default)
        => _frames.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _frames.Writer.TryComplete();

    public void Dispose() => Complete();
}
