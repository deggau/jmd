using System.Drawing;
using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WorkflowAutomationPoliciesTests
{
    [Theory]
    [InlineData("Open")]
    [InlineData("Key")]
    [InlineData("Type")]
    [InlineData("Wait")]
    [InlineData("Focus")]
    [InlineData("WaitWindow")]
    [InlineData("WaitControl")]
    [InlineData("IfControl")]
    [InlineData("Click")]
    [InlineData("PositionWindow")]
    public void Workflow_step_action_policy_preserves_old_actions_and_accepts_new_actions(string action)
        => Assert.True(WorkflowStepActionPolicy.IsSupported(action));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Workflow_step_action_policy_rejects_unknown_actions(string? action)
        => Assert.False(WorkflowStepActionPolicy.IsSupported(action));

    [Theory]
    [InlineData(100, true)]
    [InlineData(120000, true)]
    [InlineData(99, false)]
    [InlineData(120001, false)]
    public void Workflow_timeout_policy_enforces_bounds(int timeout, bool expected)
        => Assert.Equal(expected, WorkflowTimeoutPolicy.IsValid(timeout));

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 2, 4)]
    [InlineData(1, 50, 5)]
    public void False_control_condition_skips_the_configured_number_of_following_steps(int current, int skip, int expectedNext)
        => Assert.Equal(expectedNext, WorkflowConditionalPolicy.NextStepIndexWhenFalse(current, skip, 5));

    [Theory]
    [InlineData("id:loginButton", WorkflowControlIdentifierKind.AutomationId, "loginButton")]
    [InlineData("AutomationId: save", WorkflowControlIdentifierKind.AutomationId, "save")]
    [InlineData("name:Salvar", WorkflowControlIdentifierKind.Name, "Salvar")]
    [InlineData("Usuário", WorkflowControlIdentifierKind.NameOrAutomationId, "Usuário")]
    public void Control_identifier_supports_explicit_name_and_automation_id(string input,
        WorkflowControlIdentifierKind expectedKind, string expectedValue)
    {
        Assert.True(WorkflowControlIdentifierPolicy.TryParse(input, out var identifier));
        Assert.Equal(expectedKind, identifier.Kind);
        Assert.Equal(expectedValue, identifier.Value);
    }

    [Theory]
    [InlineData("id:")]
    [InlineData("")]
    [InlineData("   ")]
    public void Control_identifier_rejects_empty_values(string input)
        => Assert.False(WorkflowControlIdentifierPolicy.TryParse(input, out _));

    [Theory]
    [InlineData(WorkflowControlCondition.Exists, true, true, false, null, true)]
    [InlineData(WorkflowControlCondition.Missing, false, false, true, null, true)]
    [InlineData(WorkflowControlCondition.Enabled, true, true, false, null, true)]
    [InlineData(WorkflowControlCondition.Disabled, true, false, false, null, true)]
    [InlineData(WorkflowControlCondition.Visible, true, true, false, null, true)]
    [InlineData(WorkflowControlCondition.Visible, true, true, true, null, false)]
    [InlineData(WorkflowControlCondition.Checked, true, true, false, true, true)]
    [InlineData(WorkflowControlCondition.Unchecked, true, true, false, false, true)]
    [InlineData(WorkflowControlCondition.Checked, false, false, false, true, false)]
    public void Control_condition_matches_ui_automation_state(WorkflowControlCondition condition,
        bool exists, bool enabled, bool offscreen, bool? isChecked, bool expected)
        => Assert.Equal(expected, WorkflowControlConditionPolicy.Matches(condition, exists, enabled, offscreen, isChecked));

    [Theory]
    [InlineData("0.25,0.75", 0.25, 0.75, 125, 275)]
    [InlineData("1,0", 1, 0, 200, 200)]
    public void Relative_click_position_parses_and_maps_to_window(string input, double x, double y, int expectedX, int expectedY)
    {
        Assert.True(WorkflowRelativePoint.TryParse(input, out var point));
        Assert.Equal(new WorkflowRelativePoint(x, y), point);
        Assert.Equal(new Point(expectedX, expectedY), point.ToScreenPoint(new Rectangle(100, 200, 101, 101)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("1.1,0.5")]
    [InlineData("-0.1,0.5")]
    [InlineData("0.5,1.1")]
    [InlineData("0.5,NaN")]
    public void Relative_click_position_rejects_invalid_values(string input)
    {
        Assert.False(WorkflowRelativePoint.TryParse(input, out _));
    }

    [Fact]
    public void Relative_click_position_can_be_captured_from_window_coordinates()
    {
        var point = WorkflowRelativePoint.FromScreenPoint(new Point(125, 275), new Rectangle(100, 200, 101, 101));
        Assert.Equal(new WorkflowRelativePoint(0.25, 0.75), point);
        Assert.Equal("0.25,0.75", point.ToString());
    }

    [Theory]
    [InlineData("LeftClick", WorkflowMouseAction.LeftClick)]
    [InlineData("DoubleClick", WorkflowMouseAction.DoubleClick)]
    [InlineData("RightClick", WorkflowMouseAction.RightClick)]
    [InlineData("ScrollUp", WorkflowMouseAction.ScrollUp)]
    [InlineData("ScrollDown", WorkflowMouseAction.ScrollDown)]
    public void Mouse_action_selector_accepts_each_supported_action(string value, WorkflowMouseAction expected)
    {
        Assert.True(WorkflowMouseActionPolicy.TryParse(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("process:TeamViewer.exe", true, "TeamViewer")]
    [InlineData("TeamViewer - Remote Control", false, "TeamViewer - Remote Control")]
    public void Window_target_parses_title_or_process(string target, bool isProcess, string expected)
    {
        var parsed = WorkflowWindowTargetPolicy.Parse(target);
        Assert.Equal(isProcess, parsed.IsProcess);
        Assert.Equal(expected, parsed.Value);
    }

    [Fact]
    public void Monitor_placement_centers_window_and_clamps_oversized_windows()
    {
        Assert.Equal(new Rectangle(1200, 150, 600, 500),
            WorkflowMonitorPlacementPolicy.CenterAndClamp(new Rectangle(1000, 0, 1000, 800), new Size(600, 500)));
        Assert.Equal(new Rectangle(1000, 0, 1000, 800),
            WorkflowMonitorPlacementPolicy.CenterAndClamp(new Rectangle(1000, 0, 1000, 800), new Size(1400, 900)));
    }
}
