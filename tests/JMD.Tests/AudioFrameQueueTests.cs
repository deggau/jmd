using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class AudioFrameQueueTests
{
    [Fact]
    public async Task Capture_can_enqueue_new_frames_while_the_processor_is_busy()
    {
        using var queue = new AudioFrameQueue(capacity: 2);
        var firstFrameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processedFrames = new List<byte[]>();
        var processor = Task.Run(async () =>
        {
            await foreach (var frame in queue.ReadAllAsync())
            {
                processedFrames.Add(frame);
                if (processedFrames.Count == 1)
                {
                    firstFrameStarted.TrySetResult();
                    await releaseProcessor.Task;
                }
            }
        });

        Assert.True(queue.TryWrite([1, 2, 3]));
        await firstFrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var captureWrite = Task.Run(() => queue.TryWrite([4, 5, 6]));
        Assert.True(await captureWrite.WaitAsync(TimeSpan.FromSeconds(2)));

        releaseProcessor.TrySetResult();
        queue.Complete();
        await processor.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 1, 2, 3 }, processedFrames[0]);
        Assert.Equal(new byte[] { 4, 5, 6 }, processedFrames[1]);
    }

    [Fact]
    public async Task Completed_queue_rejects_new_frames()
    {
        using var queue = new AudioFrameQueue(capacity: 1);
        queue.Complete();

        Assert.False(queue.TryWrite([1]));
        Assert.Empty(await ReadFramesAsync(queue));
    }

    private static async Task<List<byte[]>> ReadFramesAsync(AudioFrameQueue queue)
    {
        var frames = new List<byte[]>();
        await foreach (var frame in queue.ReadAllAsync()) frames.Add(frame);
        return frames;
    }
}
