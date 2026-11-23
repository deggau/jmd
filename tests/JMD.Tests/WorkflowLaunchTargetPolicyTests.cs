using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WorkflowLaunchTargetPolicyTests
{
    [Theory]
    [InlineData("https://example.com", "OpenUrl")]
    [InlineData(" HTTP://example.com/orders ", "OpenUrl")]
    [InlineData("notepad.exe", "OpenProgram")]
    [InlineData("C:\\Program Files\\App\\app.exe --profile test", "OpenProgram")]
    [InlineData("", "OpenProgram")]
    public void Legacy_targets_are_classified_as_url_or_program(string target, string expectedAction)
        => Assert.Equal(expectedAction, WorkflowLaunchTargetPolicy.ClassifyLegacyTarget(target));

    [Theory]
    [InlineData("https://example.com/orders/42", true)]
    [InlineData("https://example.com/orders/{{orderId}}", true)]
    [InlineData("http://{{host}}/orders/{{orderId}}", true)]
    [InlineData("ftp://example.com/file", false)]
    [InlineData("notepad.exe", false)]
    [InlineData("https://", false)]
    public void Url_step_requires_an_http_or_https_address(string target, bool expected)
        => Assert.Equal(expected, WorkflowLaunchTargetPolicy.IsValidUrlTarget(target));

    [Theory]
    [InlineData("notepad.exe", true)]
    [InlineData("C:\\Program Files\\App\\app.exe --profile test", true)]
    [InlineData("https://example.com", false)]
    [InlineData("", false)]
    public void Program_step_rejects_urls_and_empty_values(string target, bool expected)
        => Assert.Equal(expected, WorkflowLaunchTargetPolicy.IsValidProgramTarget(target));
}
