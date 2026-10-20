using System.Collections.Concurrent;
using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class AsyncInspectionCoordinatorTests
{
    [Fact]
    public async Task Request_dispatches_result_before_publishing_it()
    {
        var dispatched = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = string.Empty;
        var coordinator = new AsyncInspectionCoordinator<string, string>(
            input => $"inspected:{input}",
            () => "inspection failed",
            action => dispatched.TrySetResult(action),
            result => published = result);

        var request = coordinator.Request("button");
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        var uiAction = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, published);
        uiAction();
        Assert.Equal("inspected:button", published);
    }

    [Fact]
    public async Task Closing_discards_result_waiting_for_ui_dispatch()
    {
        var inspectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishInspection = new ManualResetEventSlim();
        var dispatched = new ConcurrentQueue<Action>();
        var published = false;
        var coordinator = new AsyncInspectionCoordinator<string, string>(
            input =>
            {
                inspectionStarted.SetResult();
                finishInspection.Wait(TimeSpan.FromSeconds(5));
                return input;
            },
            () => "inspection failed",
            action => dispatched.Enqueue(action),
            _ => published = true);

        var request = coordinator.Request("field");
        await inspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Close();
        finishInspection.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(dispatched);
        Assert.False(published);
    }

    [Fact]
    public async Task Inspection_errors_are_delivered_as_fallback_results()
    {
        var dispatched = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = string.Empty;
        var coordinator = new AsyncInspectionCoordinator<string, string>(
            _ => throw new InvalidOperationException("provider failed"),
            () => "inspection failed",
            action => dispatched.TrySetResult(action),
            result => published = result);

        var request = coordinator.Request("textbox");
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        var uiAction = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        uiAction();

        Assert.Equal("inspection failed", published);
    }
}
