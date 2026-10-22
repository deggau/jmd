using JMD.Core;
using Microsoft.Data.Sqlite;
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
    public void Paused_task_details_are_persisted_and_reminders_are_not_marked_sent()
    {
        var store = new TodoTaskStore(DatabasePath);
        var created = DateTimeOffset.Now.AddHours(-1);
        var lastReminder = created.AddMinutes(20);
        var pausedAt = DateTimeOffset.Now;
        var task = new TodoTaskEntry("paused-1", "Aguardar aprovação", "", created,
            TaskNumber: 1, ReminderIntervalMinutes: 30, LastReminderAt: lastReminder,
            PausedAt: pausedAt, PauseReason: "Aguardando retorno", PausedBy: "Marina");

        store.SaveAll([task]);
        store.MarkReminderSent(task.Id, DateTimeOffset.Now.AddMinutes(30));

        var loaded = Assert.Single(new TodoTaskStore(DatabasePath).GetAll());
        Assert.Equal(task, loaded);
    }

    [Fact]
    public void Existing_task_database_is_migrated_with_pause_fields_empty()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TodoTasks (
                    Id TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    CompletedAtUtc TEXT NULL,
                    CreatedLocalDate TEXT NOT NULL DEFAULT '',
                    TaskNumber INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO TodoTasks (Id, Title, Description, CreatedAtUtc, TaskNumber)
                VALUES ('old-task', 'Tarefa antiga', 'Descrição preservada', '2026-10-21T12:00:00.0000000+00:00', 1);
                """;
            command.ExecuteNonQuery();
        }

        var loaded = Assert.Single(new TodoTaskStore(DatabasePath).GetAll());

        Assert.Equal("Tarefa antiga", loaded.Title);
        Assert.Null(loaded.PausedAt);
        Assert.Equal(string.Empty, loaded.PauseReason);
        Assert.Equal(string.Empty, loaded.PausedBy);
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
