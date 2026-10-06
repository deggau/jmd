using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class DisplayAwakeControllerTests
{
    [Fact]
    public void Enabling_requests_display_and_system_awake_and_dispose_clears_request()
    {
        var requests = new List<ExecutionStateRequest>();
        var controller = new DisplayAwakeController(request =>
        {
            requests.Add(request);
            return true;
        });

        Assert.True(controller.SetEnabled(true));
        Assert.True(controller.IsEnabled);
        Assert.Equal(
            ExecutionStateRequest.Continuous | ExecutionStateRequest.DisplayRequired | ExecutionStateRequest.SystemRequired,
            Assert.Single(requests));

        controller.Dispose();

        Assert.False(controller.IsEnabled);
        Assert.Equal(
            new[]
            {
                ExecutionStateRequest.Continuous | ExecutionStateRequest.DisplayRequired | ExecutionStateRequest.SystemRequired,
                ExecutionStateRequest.Continuous
            },
            requests);
    }

    [Fact]
    public void Failed_power_request_does_not_change_enabled_state()
    {
        var controller = new DisplayAwakeController(_ => false);

        Assert.False(controller.SetEnabled(true));
        Assert.False(controller.IsEnabled);
        Assert.True(controller.SetEnabled(false));
        Assert.False(controller.IsEnabled);
    }

    [Fact]
    public void Repeating_same_state_does_not_send_redundant_power_requests()
    {
        var requestCount = 0;
        var controller = new DisplayAwakeController(_ =>
        {
            requestCount++;
            return true;
        });

        Assert.True(controller.SetEnabled(true));
        Assert.True(controller.SetEnabled(true));
        Assert.Equal(1, requestCount);
    }
}
