namespace JMD.Core;

/// <summary>Runs slow inspection work off-thread and delivers results only through the UI dispatcher.</summary>
public sealed class AsyncInspectionCoordinator<TInput, TResult>(
    Func<TInput, TResult> inspect,
    Func<TResult> failureResult,
    Action<Action> dispatchToUi,
    Action<TResult> publishOnUi)
    where TResult : notnull
{
    private int _busy;
    private int _requestId;
    private int _closed;

    public Task Request(TInput input)
    {
        if (Volatile.Read(ref _closed) != 0 || Interlocked.Exchange(ref _busy, 1) != 0) return Task.CompletedTask;
        var requestId = Interlocked.Increment(ref _requestId);
        return InspectAndPublishAsync(input, requestId);
    }

    public void Close()
    {
        Volatile.Write(ref _closed, 1);
        Interlocked.Increment(ref _requestId);
    }

    private async Task InspectAndPublishAsync(TInput input, int requestId)
    {
        TResult result;
        try
        {
            result = await Task.Run(() => inspect(input)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = failureResult();
        }

        Interlocked.Exchange(ref _busy, 0);
        if (!IsCurrent(requestId)) return;
        try
        {
            dispatchToUi(() =>
            {
                if (IsCurrent(requestId)) publishOnUi(result);
            });
        }
        catch (Exception)
        {
            // The dispatcher may be shutting down while the inspector closes.
        }
    }

    private bool IsCurrent(int requestId)
        => Volatile.Read(ref _closed) == 0 && requestId == Volatile.Read(ref _requestId);
}
