using Microsoft.Data.Sqlite;

namespace JMD.Core;

public sealed record ConversionHistoryEntry(
    long Id,
    DateTimeOffset OccurredAt,
    string CommandName,
    string BeforeValue,
    string AfterValue,
    string? ApplicationName = null,
    string? WindowTitle = null,
    bool IsVoiceTriggered = false,
    string? VoiceTranscript = null,
    bool HasVoiceAudio = false,
    int? VoiceDurationMilliseconds = null,
    int? VoiceCutMilliseconds = null,
    bool HasVoiceTranscriptionAudio = false,
    int? PortugueseTranscriptionTimeMilliseconds = null,
    int? EnglishTranscriptionTimeMilliseconds = null,
    bool IsFailure = false,
    string? FailureDetails = null);

public sealed record DraftHistoryEntry(long HistoryId, DateTimeOffset OccurredAt, string CommandName, string Text);

public sealed record ConversionSource(string? ApplicationName, string? WindowTitle);

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
                AfterValue TEXT NOT NULL,
                ApplicationName TEXT NULL,
                WindowTitle TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ConversionHistory_OccurredAtUtc
                ON ConversionHistory (OccurredAtUtc DESC, Id DESC);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "ApplicationName");
        EnsureColumn(connection, "WindowTitle");
        EnsureColumn(connection, "IsVoiceTriggered", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "VoiceTranscript");
        EnsureColumn(connection, "VoiceAudioWav", "BLOB NULL");
        EnsureColumn(connection, "VoiceDurationMilliseconds", "INTEGER NULL");
        EnsureColumn(connection, "VoiceCutMilliseconds", "INTEGER NULL");
        EnsureColumn(connection, "VoiceTranscriptionAudioWav", "BLOB NULL");
        EnsureColumn(connection, "PortugueseTranscriptionTimeMilliseconds", "INTEGER NULL");
        EnsureColumn(connection, "EnglishTranscriptionTimeMilliseconds", "INTEGER NULL");
        EnsureColumn(connection, "IsFailure", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "FailureDetails");
        using var draftSchema = connection.CreateCommand();
        draftSchema.CommandText = """
            CREATE TABLE IF NOT EXISTS DraftHistory (
                HistoryId INTEGER PRIMARY KEY,
                OccurredAtUtc TEXT NOT NULL,
                CommandName TEXT NOT NULL,
                DraftText TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS JmdMigrations (Version INTEGER PRIMARY KEY);
            CREATE TRIGGER IF NOT EXISTS TR_ConversionHistory_DraftHistory_Insert
            AFTER INSERT ON ConversionHistory
            WHEN NEW.CommandName LIKE '%Salvar rascunho' OR NEW.CommandName LIKE '%Restaurar rascunho'
            BEGIN
                INSERT OR IGNORE INTO DraftHistory (HistoryId, OccurredAtUtc, CommandName, DraftText)
                VALUES (NEW.Id, NEW.OccurredAtUtc, NEW.CommandName, NEW.AfterValue);
            END;
            """;
        draftSchema.ExecuteNonQuery();
        using var migrationCheck = connection.CreateCommand();
        migrationCheck.CommandText = "SELECT 1 FROM JmdMigrations WHERE Version = 1;";
        if (migrationCheck.ExecuteScalar() is null)
        {
            using var migration = connection.BeginTransaction();
            using var migrateDrafts = connection.CreateCommand();
            migrateDrafts.Transaction = migration;
            migrateDrafts.CommandText = """
                INSERT OR IGNORE INTO DraftHistory (HistoryId, OccurredAtUtc, CommandName, DraftText)
                SELECT Id, OccurredAtUtc, CommandName, AfterValue
                FROM ConversionHistory
                WHERE CommandName LIKE '%Salvar rascunho' OR CommandName LIKE '%Restaurar rascunho';
                INSERT OR IGNORE INTO JmdMigrations (Version) VALUES (1);
                """;
            migrateDrafts.ExecuteNonQuery();
            migration.Commit();
        }
    }

    public long Add(
        string commandName,
        string beforeValue,
        string afterValue,
        DateTimeOffset? occurredAtUtc = null,
        ConversionSource? source = null,
        bool isVoiceTriggered = false,
        string? voiceTranscript = null,
        byte[]? voiceAudioWav = null,
        int? voiceDurationMilliseconds = null,
        int? voiceCutMilliseconds = null,
        byte[]? voiceTranscriptionAudioWav = null,
        int? portugueseTranscriptionTimeMilliseconds = null,
        int? englishTranscriptionTimeMilliseconds = null,
        bool isFailure = false,
        string? failureDetails = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ConversionHistory (OccurredAtUtc, CommandName, BeforeValue, AfterValue, ApplicationName, WindowTitle, IsVoiceTriggered, VoiceTranscript, VoiceAudioWav, VoiceDurationMilliseconds, VoiceCutMilliseconds, VoiceTranscriptionAudioWav, PortugueseTranscriptionTimeMilliseconds, EnglishTranscriptionTimeMilliseconds, IsFailure, FailureDetails)
            VALUES ($occurredAt, $commandName, $beforeValue, $afterValue, $applicationName, $windowTitle, $isVoiceTriggered, $voiceTranscript, $voiceAudioWav, $voiceDurationMilliseconds, $voiceCutMilliseconds, $voiceTranscriptionAudioWav, $portugueseTranscriptionTimeMilliseconds, $englishTranscriptionTimeMilliseconds, $isFailure, $failureDetails);
            """;
        command.Parameters.AddWithValue("$occurredAt", (occurredAtUtc ?? _utcNow()).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$commandName", commandName);
        command.Parameters.AddWithValue("$beforeValue", beforeValue);
        command.Parameters.AddWithValue("$afterValue", afterValue);
        command.Parameters.AddWithValue("$applicationName", (object?)source?.ApplicationName ?? DBNull.Value);
        command.Parameters.AddWithValue("$windowTitle", (object?)source?.WindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$isVoiceTriggered", isVoiceTriggered ? 1 : 0);
        command.Parameters.AddWithValue("$voiceTranscript", (object?)voiceTranscript ?? DBNull.Value);
        command.Parameters.AddWithValue("$voiceAudioWav", (object?)voiceAudioWav ?? DBNull.Value);
        command.Parameters.AddWithValue("$voiceDurationMilliseconds", (object?)voiceDurationMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$voiceCutMilliseconds", (object?)voiceCutMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$voiceTranscriptionAudioWav", (object?)voiceTranscriptionAudioWav ?? DBNull.Value);
        command.Parameters.AddWithValue("$portugueseTranscriptionTimeMilliseconds", (object?)portugueseTranscriptionTimeMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$englishTranscriptionTimeMilliseconds", (object?)englishTranscriptionTimeMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$isFailure", isFailure ? 1 : 0);
        command.Parameters.AddWithValue("$failureDetails", (object?)failureDetails ?? DBNull.Value);
        command.ExecuteNonQuery();
        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        return (long)idCommand.ExecuteScalar()!;
    }

    public void UpdateVoiceCapture(long id, string? transcript, byte[] audioWav,
        int? voiceDurationMilliseconds = null, int? voiceCutMilliseconds = null, byte[]? voiceTranscriptionAudioWav = null,
        int? portugueseTranscriptionTimeMilliseconds = null, int? englishTranscriptionTimeMilliseconds = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ConversionHistory SET AfterValue = COALESCE($transcript, AfterValue), VoiceTranscript = COALESCE($transcript, VoiceTranscript), VoiceAudioWav = $audio, VoiceDurationMilliseconds = COALESCE($duration, VoiceDurationMilliseconds), VoiceCutMilliseconds = COALESCE($cut, VoiceCutMilliseconds), VoiceTranscriptionAudioWav = COALESCE($transcriptionAudio, VoiceTranscriptionAudioWav), PortugueseTranscriptionTimeMilliseconds = COALESCE($portugueseTime, PortugueseTranscriptionTimeMilliseconds), EnglishTranscriptionTimeMilliseconds = COALESCE($englishTime, EnglishTranscriptionTimeMilliseconds) WHERE Id = $id AND IsVoiceTriggered = 1;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$transcript", (object?)transcript ?? DBNull.Value);
        command.Parameters.AddWithValue("$audio", audioWav);
        command.Parameters.AddWithValue("$duration", (object?)voiceDurationMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$cut", (object?)voiceCutMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$transcriptionAudio", (object?)voiceTranscriptionAudioWav ?? DBNull.Value);
        command.Parameters.AddWithValue("$portugueseTime", (object?)portugueseTranscriptionTimeMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$englishTime", (object?)englishTranscriptionTimeMilliseconds ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ConversionHistoryEntry> Search(string query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, CommandName, BeforeValue, AfterValue, ApplicationName, WindowTitle, IsVoiceTriggered, VoiceTranscript, VoiceAudioWav IS NOT NULL, VoiceDurationMilliseconds, VoiceCutMilliseconds, VoiceTranscriptionAudioWav IS NOT NULL, PortugueseTranscriptionTimeMilliseconds, EnglishTranscriptionTimeMilliseconds, IsFailure, FailureDetails
            FROM ConversionHistory
            WHERE $query = '' OR CommandName LIKE $pattern ESCAPE '\'
                OR BeforeValue LIKE $pattern ESCAPE '\' OR AfterValue LIKE $pattern ESCAPE '\'
                OR VoiceTranscript LIKE $pattern ESCAPE '\'
                OR ApplicationName LIKE $pattern ESCAPE '\' OR WindowTitle LIKE $pattern ESCAPE '\' OR FailureDetails LIKE $pattern ESCAPE '\'
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

    public IReadOnlyList<ConversionHistoryEntry> SearchPage(string query, int pageSize, int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, CommandName, BeforeValue, AfterValue, ApplicationName, WindowTitle, IsVoiceTriggered, VoiceTranscript, VoiceAudioWav IS NOT NULL, VoiceDurationMilliseconds, VoiceCutMilliseconds, VoiceTranscriptionAudioWav IS NOT NULL, PortugueseTranscriptionTimeMilliseconds, EnglishTranscriptionTimeMilliseconds, IsFailure, FailureDetails
            FROM ConversionHistory
            WHERE $query = '' OR CommandName LIKE $pattern ESCAPE '\'
                OR BeforeValue LIKE $pattern ESCAPE '\' OR AfterValue LIKE $pattern ESCAPE '\'
                OR VoiceTranscript LIKE $pattern ESCAPE '\'
                OR ApplicationName LIKE $pattern ESCAPE '\' OR WindowTitle LIKE $pattern ESCAPE '\' OR FailureDetails LIKE $pattern ESCAPE '\'
            ORDER BY OccurredAtUtc DESC, Id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddSearchParameters(command, query);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", checked(pageIndex * pageSize));
        var entries = new List<ConversionHistoryEntry>(pageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read()) entries.Add(ReadEntry(reader));
        return entries;
    }

    public int CountSearch(string query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ConversionHistory
            WHERE $query = '' OR CommandName LIKE $pattern ESCAPE '\'
                OR BeforeValue LIKE $pattern ESCAPE '\' OR AfterValue LIKE $pattern ESCAPE '\'
                OR VoiceTranscript LIKE $pattern ESCAPE '\'
                OR ApplicationName LIKE $pattern ESCAPE '\' OR WindowTitle LIKE $pattern ESCAPE '\' OR FailureDetails LIKE $pattern ESCAPE '\';
            """;
        AddSearchParameters(command, query);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public int GetPageIndexContaining(long historyId, string query, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        using var connection = OpenConnection();
        using var targetCommand = connection.CreateCommand();
        targetCommand.CommandText = "SELECT OccurredAtUtc FROM ConversionHistory WHERE Id = $id;";
        targetCommand.Parameters.AddWithValue("$id", historyId);
        var targetTime = targetCommand.ExecuteScalar() as string;
        if (targetTime is null) return 0;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ConversionHistory
            WHERE ($query = '' OR CommandName LIKE $pattern ESCAPE '\'
                OR BeforeValue LIKE $pattern ESCAPE '\' OR AfterValue LIKE $pattern ESCAPE '\'
                OR VoiceTranscript LIKE $pattern ESCAPE '\'
                OR ApplicationName LIKE $pattern ESCAPE '\' OR WindowTitle LIKE $pattern ESCAPE '\' OR FailureDetails LIKE $pattern ESCAPE '\')
                AND (OccurredAtUtc > $targetTime OR (OccurredAtUtc = $targetTime AND Id > $id));
            """;
        AddSearchParameters(command, query);
        command.Parameters.AddWithValue("$targetTime", targetTime);
        command.Parameters.AddWithValue("$id", historyId);
        var precedingEntries = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return precedingEntries / pageSize;
    }

    public IReadOnlyList<DraftHistoryEntry> GetDraftHistory()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT HistoryId, OccurredAtUtc, CommandName, DraftText FROM DraftHistory ORDER BY OccurredAtUtc, HistoryId;";
        var entries = new List<DraftHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            entries.Add(new DraftHistoryEntry(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(2), reader.GetString(3)));
        return entries;
    }

    public ConversionHistoryEntry? GetLatest()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, CommandName, BeforeValue, AfterValue, ApplicationName, WindowTitle, IsVoiceTriggered, VoiceTranscript, VoiceAudioWav IS NOT NULL, VoiceDurationMilliseconds, VoiceCutMilliseconds, VoiceTranscriptionAudioWav IS NOT NULL, PortugueseTranscriptionTimeMilliseconds, EnglishTranscriptionTimeMilliseconds, IsFailure, FailureDetails
            FROM ConversionHistory
            ORDER BY OccurredAtUtc DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public byte[]? GetVoiceAudio(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VoiceAudioWav FROM ConversionHistory WHERE Id = $id AND IsVoiceTriggered = 1;";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is byte[] bytes ? bytes : null;
    }

    public byte[]? GetVoiceTranscriptionAudio(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VoiceTranscriptionAudioWav FROM ConversionHistory WHERE Id = $id AND IsVoiceTriggered = 1;";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is byte[] bytes ? bytes : null;
    }

    private static ConversionHistoryEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7) != 0,
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt32(9) != 0,
        reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt32(11),
        reader.GetInt32(12) != 0,
        reader.IsDBNull(13) ? null : reader.GetInt32(13),
        reader.IsDBNull(14) ? null : reader.GetInt32(14),
        reader.GetInt32(15) != 0,
        reader.IsDBNull(16) ? null : reader.GetString(16));

    private static void EnsureColumn(SqliteConnection connection, string columnName, string declaration = "TEXT NULL")
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(ConversionHistory);";
        var exists = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
        }
        if (exists) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE ConversionHistory ADD COLUMN {columnName} {declaration};";
        alter.ExecuteNonQuery();
    }

    public void DeleteOlderThan(int retentionDays)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DraftHistory WHERE OccurredAtUtc < $cutoff; DELETE FROM ConversionHistory WHERE OccurredAtUtc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", _utcNow().ToUniversalTime().AddDays(-retentionDays).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void AddSearchParameters(SqliteCommand command, string query)
    {
        command.Parameters.AddWithValue("$query", query);
        var escapedQuery = query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$pattern", $"%{escapedQuery}%");
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
    public bool RecordIfSuccessful(string commandName, string beforeValue, TransformationResult result, ConversionSource? source = null)
    {
        if (!result.Success || result.Value is null) return false;
        historyStore.Add(commandName, beforeValue, result.Value, source: source);
        return true;
    }

    public bool RecordIfSuccessful(string commandName, SelectionTransformResult result, ConversionSource? source = null)
    {
        if (!result.Success || result.BeforeValue is null || result.AfterValue is null) return false;
        historyStore.Add(commandName, result.BeforeValue, result.AfterValue, source: source);
        return true;
    }
}
