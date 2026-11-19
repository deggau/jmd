using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class JmdroidRemoteCommandPolicyTests
{
    [Theory]
    [InlineData("todoCreate")]
    [InlineData("todoPanel")]
    [InlineData("todoPanelClose")]
    [InlineData("about")]
    [InlineData("hibernate")]
    [InlineData("suspend")]
    [InlineData("lock")]
    [InlineData("screenShare")]
    [InlineData("desktopScreenShare")]
    public void Allows_selected_desktop_actions_from_jmdroid(string commandId)
    {
        Assert.True(JmdroidRemoteCommandPolicy.IsRemoteCommand(commandId));
        Assert.True(JmdroidRemoteCommandPolicy.RequiresEmptyInput(commandId));
    }

    [Theory]
    [InlineData("clipboard")]
    [InlineData("upperCase")]
    [InlineData("lowerCase")]
    [InlineData("titleCase")]
    [InlineData("jsonPretty")]
    [InlineData("jsonCompact")]
    public void Preserves_text_input_for_existing_transform_commands(string commandId)
    {
        Assert.True(JmdroidRemoteCommandPolicy.IsRemoteCommand(commandId));
        Assert.False(JmdroidRemoteCommandPolicy.RequiresEmptyInput(commandId));
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("transcribe")]
    [InlineData("createCustomShortcut")]
    [InlineData("checkUpdates")]
    public void Keeps_interactive_or_unsupported_commands_out_of_the_remote_catalog(string commandId)
        => Assert.False(JmdroidRemoteCommandPolicy.IsRemoteCommand(commandId));
}
