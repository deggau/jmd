using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WorkflowArgumentPolicyTests
{
    [Fact]
    public void Shortcut_arguments_are_preserved_before_workflow_arguments()
    {
        var combined = WorkflowArgumentPolicy.Combine(
            ["--profile", "C:\\VPN Profile\\user.conf"],
            ["--connect"]);

        Assert.Equal(["--profile", "C:\\VPN Profile\\user.conf", "--connect"], combined);
    }
}
