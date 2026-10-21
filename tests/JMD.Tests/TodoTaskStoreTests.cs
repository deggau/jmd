using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class TodoTaskStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "JMD.Tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "tasks.db");

    [Fact]
    public void Tasks_are_persisted_with_creation_and_completion_dates()
    {
        var created = new DateTimeOffset(2026, 10, 21, 11, 30, 0, TimeSpan.FromHours(-3));
        var completed = created.AddHours(2);
        var lastReminder = completed.AddMinutes(-15);
        var task = new TodoTaskEntry("task-1", "Revisar PR", "Verificar os comentários", created, completed, 4, 30, "Laranja", lastReminder);
        var store = new TodoTaskStore(DatabasePath);

        store.SaveAll([task]);

        Assert.Equal(task, Assert.Single(new TodoTaskStore(DatabasePath).GetAll()));
    }

    [Fact]
    public void Reminder_interval_and_category_are_persisted_and_reminders_can_be_marked_sent()
    {
        var store = new TodoTaskStore(DatabasePath);
        var task = store.Create("Ligar para cliente", "", DateTimeOffset.Now, 20, "Vermelho");
        var sentAt = DateTimeOffset.Now.AddMinutes(20);

        store.MarkReminderSent(task.Id, sentAt);

        var loaded = Assert.Single(new TodoTaskStore(DatabasePath).GetAll());
        Assert.Equal(20, loaded.ReminderIntervalMinutes);
        Assert.Equal("Vermelho", loaded.CategoryColor);
        Assert.Equal(sentAt, loaded.LastReminderAt);
    }

    [Fact]
    public void Legacy_tasks_are_imported_only_once_and_can_be_saved_afterward()
    {
        var store = new TodoTaskStore(DatabasePath);
        var legacyTask = new TodoTaskEntry("legacy-1", "Tarefa antiga", "Descrição", DateTimeOffset.UtcNow);

        Assert.True(store.ImportLegacyOnce([legacyTask]));
        Assert.False(store.ImportLegacyOnce([legacyTask with { Title = "Duplicada" }]));

        Assert.Equal(legacyTask with { TaskNumber = 1 }, Assert.Single(store.GetAll()));
        store.SaveAll([]);
        Assert.Empty(new TodoTaskStore(DatabasePath).GetAll());
    }

    [Fact]
    public void Task_numbers_increment_per_local_day_and_are_not_reused_after_deletion()
    {
        var store = new TodoTaskStore(DatabasePath);
        var today = DateTimeOffset.Now.Date.AddHours(9);
        var first = store.Create("Primeira", "", today);
        var second = store.Create("Segunda", "", today.AddHours(1));
        var tomorrow = store.Create("Amanhã", "", today.AddDays(1));

        Assert.Equal(1, first.TaskNumber);
        Assert.Equal(2, second.TaskNumber);
        Assert.Equal(1, tomorrow.TaskNumber);
        store.SaveAll([second, tomorrow]);

        var next = store.Create("Outra", "", today.AddHours(2));
        Assert.Equal(3, next.TaskNumber);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
