using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class NotificationClickTargetPolicyTests
{
    [Fact]
    public void Task_reminder_click_routes_to_task_editor_instead_of_history()
    {
        long? openedHistoryId = null;
        string? openedTaskId = null;

        NotificationClickTargetPolicy.Dispatch(NotificationClickTarget.ForTodoTask("task-42"),
            historyId => openedHistoryId = historyId,
            taskId => openedTaskId = taskId);

        Assert.Null(openedHistoryId);
        Assert.Equal("task-42", openedTaskId);
    }

    [Fact]
    public void Regular_notification_click_keeps_opening_the_requested_history_entry()
    {
        long? openedHistoryId = null;
        string? openedTaskId = null;

        NotificationClickTargetPolicy.Dispatch(NotificationClickTarget.ForHistory(123),
            historyId => openedHistoryId = historyId,
            taskId => openedTaskId = taskId);

        Assert.Equal(123, openedHistoryId);
        Assert.Null(openedTaskId);
    }
}
