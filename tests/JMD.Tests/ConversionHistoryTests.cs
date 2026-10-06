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
