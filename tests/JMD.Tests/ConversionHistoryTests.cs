using JMD.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JMD.Tests;

public sealed class ConversionHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "JMD.Tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "history.db");

    [Fact]
    public void Recorder_persists_successful_clipboard_conversion_and_searches_both_values()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        var recorder = new ConversionHistoryRecorder(store);
        var result = TransformationResult.Ok("'alpha','beta'");

        Assert.True(recorder.RecordIfSuccessful("Montar SQL IN", "alpha,beta", result));

        var byOriginal = store.Search("alpha,beta");
        var byConverted = store.Search("'alpha','beta'");
        var entry = Assert.Single(byOriginal);
        Assert.Equal(entry, Assert.Single(byConverted));
        Assert.Equal("Montar SQL IN", entry.CommandName);
        Assert.Equal("alpha,beta", entry.BeforeValue);
        Assert.Equal("'alpha','beta'", entry.AfterValue);
        Assert.InRange(entry.OccurredAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Recorder_skips_failed_transformations_and_records_successful_selection_replacements()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        var recorder = new ConversionHistoryRecorder(store);

        Assert.False(recorder.RecordIfSuccessful("Montar SQL IN", "a,,b", TransformationResult.Fail("Entrada inválida.")));
        Assert.False(recorder.RecordIfSuccessful("Substituir seleção", new SelectionTransformResult(false, "Falhou.", "original", null)));
        Assert.True(recorder.RecordIfSuccessful("Substituir seleção (SQL IN)", new SelectionTransformResult(true, "Concluído.", "a,b", "'a','b'")));

        var entry = Assert.Single(store.Search("a,b"));
        Assert.Equal("Substituir seleção (SQL IN)", entry.CommandName);
        Assert.Equal("a,b", entry.BeforeValue);
        Assert.Equal("'a','b'", entry.AfterValue);
    }

    [Fact]
    public void History_is_persistent_and_ordered_newest_first()
    {
        var instant = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var store = new ConversionHistoryStore(DatabasePath, () => instant);
        store.Add("Mais antigo", "old", "old-result", instant.AddMinutes(-1));
        store.Add("Mais recente", "new", "new-result", instant);

        var reopenedStore = new ConversionHistoryStore(DatabasePath, () => instant);
        var entries = reopenedStore.Search(string.Empty);

        Assert.Equal(new[] { "Mais recente", "Mais antigo" }, entries.Select(entry => entry.CommandName));
        Assert.Equal(instant, entries[0].OccurredAt);
        Assert.Equal(entries[0], reopenedStore.GetLatest());
    }

    [Fact]
    public void Voice_history_keeps_recorded_audio_available_for_playback()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        byte[] recordedAudio = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4];
        store.Add("Ativação por voz (Jey)", string.Empty, "olá", source: null,
            isVoiceTriggered: true, voiceTranscript: "olá", voiceAudioWav: recordedAudio);

        var entry = Assert.Single(store.Search(string.Empty));

        Assert.True(entry.HasVoiceAudio);
        Assert.Equal(recordedAudio, store.GetVoiceAudio(entry.Id));
    }

    [Fact]
    public void Voice_history_keeps_debug_timing_and_transcription_audio()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        byte[] originalAudio = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4];
        byte[] transcriptionAudio = [0x52, 0x49, 0x46, 0x46, 5, 6, 7, 8];
        var id = store.Add("Ativação por voz (Jey)", string.Empty, string.Empty,
            isVoiceTriggered: true, voiceAudioWav: originalAudio,
            voiceDurationMilliseconds: 1800, voiceCutMilliseconds: 640,
            voiceTranscriptionAudioWav: transcriptionAudio);

        store.UpdateVoiceCapture(id, "formatar", originalAudio,
            voiceDurationMilliseconds: 2400, voiceCutMilliseconds: 640,
            voiceTranscriptionAudioWav: transcriptionAudio);

        var entry = Assert.Single(store.Search(string.Empty));
        Assert.Equal(2400, entry.VoiceDurationMilliseconds);
        Assert.Equal(640, entry.VoiceCutMilliseconds);
        Assert.True(entry.HasVoiceTranscriptionAudio);
        Assert.Equal(transcriptionAudio, store.GetVoiceTranscriptionAudio(id));
    }

    [Fact]
    public void Retention_removes_entries_older_than_the_configured_days()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var store = new ConversionHistoryStore(DatabasePath, () => now);
        store.Add("Vencido", "old", "old-result", now.AddDays(-16));
        store.Add("Limite", "boundary", "boundary-result", now.AddDays(-15));
        store.Add("Recente", "recent", "recent-result", now.AddDays(-3));

        store.DeleteOlderThan(15);

        Assert.Equal(new[] { "Recente", "Limite" }, store.Search(string.Empty).Select(entry => entry.CommandName));
    }

    [Fact]
    public void Search_treats_sql_wildcards_as_literal_characters()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        store.Add("Conversão", "valor 100%", "resultado", DateTimeOffset.UtcNow);
        store.Add("Conversão", "valor 100x", "outro resultado", DateTimeOffset.UtcNow);

        var entries = store.Search("100%");

        Assert.Equal("valor 100%", Assert.Single(entries).BeforeValue);
    }

    [Fact]
    public void History_search_supports_fifteen_item_pages_and_direct_page_lookup()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        long oldestId = 0;
        for (var index = 0; index < 20; index++)
        {
            var id = store.Add("Comando", index.ToString(), index.ToString(), DateTimeOffset.UtcNow.AddMinutes(index));
            if (index == 0) oldestId = id;
        }

        Assert.Equal(20, store.CountSearch(string.Empty));
        Assert.Equal(15, store.SearchPage(string.Empty, 15, 0).Count);
        Assert.Equal(5, store.SearchPage(string.Empty, 15, 1).Count);
        Assert.Equal(1, store.GetPageIndexContaining(oldestId, string.Empty, 15));
        Assert.Empty(store.SearchPage("missing", 15, 0));
        Assert.Equal(0, store.GetPageIndexContaining(long.MaxValue, string.Empty, 15));
    }

    [Fact]
    public void Draft_history_migrates_existing_events_and_indexes_new_events_separately()
    {
        Directory.CreateDirectory(_directory);
        var timestamp = "2026-10-06T12:00:00.0000000+00:00";
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ConversionHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OccurredAtUtc TEXT NOT NULL,
                    CommandName TEXT NOT NULL,
                    BeforeValue TEXT NOT NULL,
                    AfterValue TEXT NOT NULL
                );
                INSERT INTO ConversionHistory (OccurredAtUtc, CommandName, BeforeValue, AfterValue)
                VALUES
                    ($timestamp, 'Salvar rascunho', '', 'draft antigo'),
                    ($timestamp, 'Formatar JSON', '{}', '{ }'),
                    ($timestamp, 'Ativação por voz (Jey) - Restaurar rascunho', '', 'draft antigo');
                """;
            command.Parameters.AddWithValue("$timestamp", timestamp);
            command.ExecuteNonQuery();
        }

        var store = new ConversionHistoryStore(DatabasePath);
        store.Add("Salvar rascunho", string.Empty, "draft novo", DateTimeOffset.Parse(timestamp));
        var drafts = store.GetDraftHistory();

        Assert.Equal(3, drafts.Count);
        Assert.Equal(new[] { "draft antigo", "draft antigo", "draft novo" }, drafts.Select(entry => entry.Text));
        Assert.Equal(4, store.Search(string.Empty).Count);
    }

    [Fact]
    public void Draft_history_obeys_the_same_retention_window_as_conversion_history()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var store = new ConversionHistoryStore(DatabasePath, () => now);
        store.Add(DraftStack.SavedCommandName, string.Empty, "expired", now.AddDays(-16));
        store.Add(DraftStack.SavedCommandName, string.Empty, "recent", now.AddDays(-2));

        store.DeleteOlderThan(15);

        Assert.Equal("recent", Assert.Single(store.GetDraftHistory()).Text);
    }

    [Fact]
    public void History_persists_and_searches_the_source_application_and_window_title()
    {
        var store = new ConversionHistoryStore(DatabasePath);
        var source = new ConversionSource("ssms", "Consulta.sql - SQL Server Management Studio");
        store.Add("Formatar JSON", "{}", "{\r\n}", DateTimeOffset.UtcNow, source);

        var byApplication = Assert.Single(store.Search("ssms"));
        var byWindowTitle = Assert.Single(store.Search("Consulta.sql"));
        var latest = store.GetLatest();

        Assert.Equal("ssms", byApplication.ApplicationName);
        Assert.Equal(source.WindowTitle, byWindowTitle.WindowTitle);
        Assert.Equal(byApplication, latest);
    }

    [Fact]
    public void Opening_existing_history_database_adds_source_columns_without_losing_entries()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ConversionHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OccurredAtUtc TEXT NOT NULL,
                    CommandName TEXT NOT NULL,
                    BeforeValue TEXT NOT NULL,
                    AfterValue TEXT NOT NULL
                );
                INSERT INTO ConversionHistory (OccurredAtUtc, CommandName, BeforeValue, AfterValue)
                VALUES ('2026-10-06T12:00:00.0000000+00:00', 'Antigo', 'antes', 'depois');
                """;
            command.ExecuteNonQuery();
        }

        var store = new ConversionHistoryStore(DatabasePath);
        var entry = Assert.Single(store.Search(string.Empty));

        Assert.Equal("Antigo", entry.CommandName);
        Assert.Equal("antes", entry.BeforeValue);
        Assert.Equal("depois", entry.AfterValue);
        Assert.Null(entry.ApplicationName);
        Assert.Null(entry.WindowTitle);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
