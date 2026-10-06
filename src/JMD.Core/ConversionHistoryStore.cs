using Microsoft.Data.Sqlite;

namespace JMD.Core;

public sealed record ConversionHistoryEntry(
    long Id,
    DateTimeOffset OccurredAt,
    string CommandName,
    string BeforeValue,
    string AfterValue);

public sealed class ConversionHistoryStore
{
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _utcNow;

    public ConversionHistoryStore(string databasePath, Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ConversionHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OccurredAtUtc TEXT NOT NULL,
                CommandName TEXT NOT NULL,
                BeforeValue TEXT NOT NULL,
                AfterValue TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ConversionHistory_OccurredAtUtc
                ON ConversionHistory (OccurredAtUtc DESC, Id DESC);
            """;
        command.ExecuteNonQuery();
    }

    public void Add(string commandName, string beforeValue, string afterValue, DateTimeOffset? occurredAtUtc = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ConversionHistory (OccurredAtUtc, CommandName, BeforeValue, AfterValue)
            VALUES ($occurredAt, $commandName, $beforeValue, $afterValue);
            """;
        command.Parameters.AddWithValue("$occurredAt", (occurredAtUtc ?? _utcNow()).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$commandName", commandName);
        command.Parameters.AddWithValue("$beforeValue", beforeValue);
        command.Parameters.AddWithValue("$afterValue", afterValue);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ConversionHistoryEntry> Search(string query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, CommandName, BeforeValue, AfterValue
            FROM ConversionHistory
            WHERE $query = '' OR CommandName LIKE $pattern ESCAPE '\'
                OR BeforeValue LIKE $pattern ESCAPE '\' OR AfterValue LIKE $pattern ESCAPE '\'
            ORDER BY OccurredAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$query", query);
        var escapedQuery = query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$pattern", $"%{escapedQuery}%");

        var entries = new List<ConversionHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            entries.Add(ReadEntry(reader));
        return entries;
    }

    public ConversionHistoryEntry? GetLatest()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, CommandName, BeforeValue, AfterValue
            FROM ConversionHistory
            ORDER BY OccurredAtUtc DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static ConversionHistoryEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4));

    public void DeleteOlderThan(int retentionDays)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ConversionHistory WHERE OccurredAtUtc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", _utcNow().ToUniversalTime().AddDays(-retentionDays).ToString("O"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

public sealed class ConversionHistoryRecorder(ConversionHistoryStore historyStore)
{
    public bool RecordIfSuccessful(string commandName, string beforeValue, TransformationResult result)
    {
        if (!result.Success || result.Value is null) return false;
        historyStore.Add(commandName, beforeValue, result.Value);
        return true;
    }

    public bool RecordIfSuccessful(string commandName, SelectionTransformResult result)
    {
        if (!result.Success || result.BeforeValue is null || result.AfterValue is null) return false;
        historyStore.Add(commandName, result.BeforeValue, result.AfterValue);
        return true;
    }
}
