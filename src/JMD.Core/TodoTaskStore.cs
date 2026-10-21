using Microsoft.Data.Sqlite;

namespace JMD.Core;

public sealed record TodoTaskEntry(
    string Id,
    string Title,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt = null,
    int TaskNumber = 0,
    int? ReminderIntervalMinutes = null,
    string CategoryColor = "Verde",
    DateTimeOffset? LastReminderAt = null);

public sealed class TodoTaskStore
{
    private readonly string _connectionString;

    public TodoTaskStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TodoTasks (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Description TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                CreatedLocalDate TEXT NOT NULL DEFAULT '',
                TaskNumber INTEGER NOT NULL DEFAULT 0,
                ReminderIntervalMinutes INTEGER NULL,
                CategoryColor TEXT NOT NULL DEFAULT 'Verde',
                LastReminderAtUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TodoTasks_Status_CreatedAtUtc
                ON TodoTasks (CompletedAtUtc, CreatedAtUtc, Id);
            CREATE TABLE IF NOT EXISTS TodoTaskMigrations (Version INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS TodoTaskDailySequence (
                LocalDate TEXT PRIMARY KEY,
                LastNumber INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "CreatedLocalDate", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TaskNumber", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "ReminderIntervalMinutes", "INTEGER NULL");
        EnsureColumn(connection, "CategoryColor", "TEXT NOT NULL DEFAULT 'Verde'");
        EnsureColumn(connection, "LastReminderAtUtc", "TEXT NULL");
        MigrateTaskNumbers(connection);
    }

    public IReadOnlyList<TodoTaskEntry> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Description, CreatedAtUtc, CompletedAtUtc, TaskNumber, ReminderIntervalMinutes, CategoryColor, LastReminderAtUtc
            FROM TodoTasks
            ORDER BY CreatedAtUtc, Id;
            """;
        using var reader = command.ExecuteReader();
        var tasks = new List<TodoTaskEntry>();
        while (reader.Read())
        {
            tasks.Add(new TodoTaskEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return tasks;
    }

    public void SaveAll(IEnumerable<TodoTaskEntry> tasks)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM TodoTasks;";
            delete.ExecuteNonQuery();
        }
        foreach (var task in tasks)
        {
            var localDate = task.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var taskNumber = task.TaskNumber > 0 ? task.TaskNumber : AllocateNumber(connection, transaction, localDate);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO TodoTasks (Id, Title, Description, CreatedAtUtc, CompletedAtUtc, CreatedLocalDate, TaskNumber, ReminderIntervalMinutes, CategoryColor, LastReminderAtUtc)
                VALUES ($id, $title, $description, $createdAt, $completedAt, $localDate, $taskNumber, $reminderInterval, $categoryColor, $lastReminderAt);
                """;
            insert.Parameters.AddWithValue("$id", task.Id);
            insert.Parameters.AddWithValue("$title", task.Title);
            insert.Parameters.AddWithValue("$description", task.Description);
            insert.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$completedAt", (object?)task.CompletedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$localDate", localDate);
            insert.Parameters.AddWithValue("$taskNumber", taskNumber);
            insert.Parameters.AddWithValue("$reminderInterval", (object?)task.ReminderIntervalMinutes ?? DBNull.Value);
            insert.Parameters.AddWithValue("$categoryColor", task.CategoryColor);
            insert.Parameters.AddWithValue("$lastReminderAt", (object?)task.LastReminderAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            insert.ExecuteNonQuery();
            UpdateSequence(connection, transaction, localDate, taskNumber);
        }
        transaction.Commit();
    }

    public TodoTaskEntry Create(string title, string description, DateTimeOffset createdAt, int? reminderIntervalMinutes = null, string categoryColor = "Verde")
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var localDate = createdAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var taskNumber = AllocateNumber(connection, transaction, localDate);
        var task = new TodoTaskEntry(Guid.NewGuid().ToString("N"), title, description, createdAt,
            TaskNumber: taskNumber, ReminderIntervalMinutes: reminderIntervalMinutes, CategoryColor: categoryColor);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TodoTasks (Id, Title, Description, CreatedAtUtc, CompletedAtUtc, CreatedLocalDate, TaskNumber, ReminderIntervalMinutes, CategoryColor, LastReminderAtUtc)
            VALUES ($id, $title, $description, $createdAt, NULL, $localDate, $taskNumber, $reminderInterval, $categoryColor, NULL);
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", task.Description);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$localDate", localDate);
        command.Parameters.AddWithValue("$taskNumber", taskNumber);
        command.Parameters.AddWithValue("$reminderInterval", (object?)reminderIntervalMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$categoryColor", categoryColor);
        command.ExecuteNonQuery();
        transaction.Commit();
        return task;
    }

    public bool ImportLegacyOnce(IEnumerable<TodoTaskEntry> tasks)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM TodoTaskMigrations WHERE Version = 1;";
            if (check.ExecuteScalar() is not null)
            {
                transaction.Rollback();
                return false;
            }
        }
        foreach (var task in tasks.OrderBy(task => task.CreatedAt.ToLocalTime().Date).ThenBy(task => task.CreatedAt))
        {
            var localDate = task.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var taskNumber = task.TaskNumber > 0 ? task.TaskNumber : AllocateNumber(connection, transaction, localDate);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO TodoTasks (Id, Title, Description, CreatedAtUtc, CompletedAtUtc, CreatedLocalDate, TaskNumber, ReminderIntervalMinutes, CategoryColor, LastReminderAtUtc)
                VALUES ($id, $title, $description, $createdAt, $completedAt, $localDate, $taskNumber, $reminderInterval, $categoryColor, $lastReminderAt);
                """;
            insert.Parameters.AddWithValue("$id", task.Id);
            insert.Parameters.AddWithValue("$title", task.Title);
            insert.Parameters.AddWithValue("$description", task.Description);
            insert.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$completedAt", (object?)task.CompletedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$localDate", localDate);
            insert.Parameters.AddWithValue("$taskNumber", taskNumber);
            insert.Parameters.AddWithValue("$reminderInterval", (object?)task.ReminderIntervalMinutes ?? DBNull.Value);
            insert.Parameters.AddWithValue("$categoryColor", task.CategoryColor);
            insert.Parameters.AddWithValue("$lastReminderAt", (object?)task.LastReminderAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            insert.ExecuteNonQuery();
            UpdateSequence(connection, transaction, localDate, taskNumber);
        }
        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO TodoTaskMigrations (Version) VALUES (1);";
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public void MarkReminderSent(string taskId, DateTimeOffset sentAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TodoTasks SET LastReminderAtUtc = $sentAt WHERE Id = $id AND CompletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$sentAt", sentAt.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    private static int AllocateNumber(SqliteConnection connection, SqliteTransaction transaction, string localDate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TodoTaskDailySequence (LocalDate, LastNumber) VALUES ($date, 1)
            ON CONFLICT(LocalDate) DO UPDATE SET LastNumber = LastNumber + 1;
            """;
        command.Parameters.AddWithValue("$date", localDate);
        command.ExecuteNonQuery();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT LastNumber FROM TodoTaskDailySequence WHERE LocalDate = $date;";
        read.Parameters.AddWithValue("$date", localDate);
        return Convert.ToInt32(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void UpdateSequence(SqliteConnection connection, SqliteTransaction transaction, string localDate, int taskNumber)
    {
        if (taskNumber <= 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TodoTaskDailySequence (LocalDate, LastNumber) VALUES ($date, $number)
            ON CONFLICT(LocalDate) DO UPDATE SET LastNumber = MAX(LastNumber, excluded.LastNumber);
            """;
        command.Parameters.AddWithValue("$date", localDate);
        command.Parameters.AddWithValue("$number", taskNumber);
        command.ExecuteNonQuery();
    }

    private static void MigrateTaskNumbers(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        var rows = new List<(string Id, DateTimeOffset CreatedAt, string LocalDate, int TaskNumber)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id, CreatedAtUtc, CreatedLocalDate, TaskNumber FROM TodoTasks ORDER BY CreatedAtUtc, Id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(2), reader.GetInt32(3)));
        }
        foreach (var row in rows)
        {
            var localDate = string.IsNullOrWhiteSpace(row.LocalDate)
                ? row.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : row.LocalDate;
            var taskNumber = row.TaskNumber > 0 ? row.TaskNumber : AllocateNumber(connection, transaction, localDate);
            UpdateSequence(connection, transaction, localDate, taskNumber);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE TodoTasks SET CreatedLocalDate = $date, TaskNumber = $number WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", row.Id);
            update.Parameters.AddWithValue("$date", localDate);
            update.Parameters.AddWithValue("$number", taskNumber);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, string columnName, string declaration)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(TodoTasks);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE TodoTasks ADD COLUMN {columnName} {declaration};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
