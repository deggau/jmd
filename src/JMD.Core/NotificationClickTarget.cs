namespace JMD.Core;

public enum NotificationClickTargetKind
{
    History,
    TodoTask
}

public sealed record NotificationClickTarget(NotificationClickTargetKind Kind, long? HistoryId = null, string? TodoTaskId = null)
{
    public static NotificationClickTarget ForHistory(long? historyId)
        => new(NotificationClickTargetKind.History, HistoryId: historyId);

    public static NotificationClickTarget ForTodoTask(string taskId)
        => new(NotificationClickTargetKind.TodoTask, TodoTaskId: taskId);
}

public static class NotificationClickTargetPolicy
{
    public static void Dispatch(NotificationClickTarget target, Action<long?> openHistory, Action<string> openTodoTask)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(openHistory);
        ArgumentNullException.ThrowIfNull(openTodoTask);

        if (target.Kind == NotificationClickTargetKind.TodoTask && !string.IsNullOrWhiteSpace(target.TodoTaskId))
            openTodoTask(target.TodoTaskId);
        else
            openHistory(target.HistoryId);
    }
}
