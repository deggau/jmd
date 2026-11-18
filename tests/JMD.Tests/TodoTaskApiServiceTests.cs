using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class TodoTaskApiServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"jmd-todo-api-{Guid.NewGuid():N}");
    private readonly TodoTaskStore _tasks;
    private readonly ConversionHistoryStore _history;
    private readonly TodoTaskApiService _service;

    public TodoTaskApiServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _tasks = new TodoTaskStore(Path.Combine(_directory, "tasks.db"));
        _history = new ConversionHistoryStore(Path.Combine(_directory, "history.db"));
        _service = new TodoTaskApiService(_tasks, _history, () => DateTimeOffset.Parse("2026-11-16T12:00:00Z"));
    }

    [Fact]
    public void Create_and_list_tasks_preserve_panel_fields_and_filter_by_status()
    {
        var created = _service.Create(new TodoTaskApiTaskInput(
            "Preparar release", "Revisar build", 30, "Laranja", "https://example.com", "C:\\arquivo.txt",
            [new TodoTaskChecklistEntry("Compilar", 100)]), "JMDroid", "Celular da sala");

        Assert.Equal("Preparar release", created.Title);
        Assert.Equal(created.Id, Assert.Single(_service.GetTasks("open")).Id);
        Assert.Empty(_service.GetTasks("completed"));
        var history = Assert.Single(_service.GetTaskHistory("", 0, 20).Items);
        Assert.Equal("JMDroid", history.Source);
        Assert.Equal("Celular da sala", history.SourceDeviceName);
        Assert.Contains("criada", history.CommandName);
    }

    [Fact]
    public void Update_task_and_checklist_step_are_persisted_and_record_device_origin()
    {
        var task = _service.Create(new TodoTaskApiTaskInput("Título", Checklist:
            [new TodoTaskChecklistEntry("Etapa 1", 40), new TodoTaskChecklistEntry("Etapa 2", 60)]));
        var updated = _service.Update(task.Id, new TodoTaskApiTaskInput("Novo título", "Descrição", 15, "Vermelho", Checklist:
            [new TodoTaskChecklistEntry("Etapa 1", 40), new TodoTaskChecklistEntry("Etapa 2", 60)]), "JMDroid", "Pixel 9");

        Assert.Equal("Novo título", updated!.Title);
        Assert.Equal("Descrição", updated.Description);
        var marked = _service.SetChecklistStep(task.Id, 0, true, "Jmdroid", "Pixel 9");
        Assert.True(marked!.Checklist![0].IsCompleted);
        Assert.Null(marked.CompletedAt);
        var latest = _service.GetTaskHistory("", 0, 20).Items;
        Assert.Equal("JMDroid", latest[0].Source);
        Assert.Equal("Pixel 9", latest[0].SourceDeviceName);
        Assert.Contains("Etapa 1", latest[0].AfterValue);
    }

    [Fact]
    public void Add_attachments_to_existing_task_preserves_previous_files_and_records_device_origin()
    {
        var task = _service.Create(new TodoTaskApiTaskInput("Com comprovantes", Attachments: "C:\\old.pdf"));

        var updated = _service.AddAttachments(task.Id, ["C:\\new.pdf", "C:\\image.jpg"], "JMDroid", "Galaxy S24");

        Assert.Equal(["C:\\old.pdf", "C:\\new.pdf", "C:\\image.jpg"], updated!.Attachments.Split(Environment.NewLine));
        var history = _service.GetTaskHistory("", 0, 10).Items;
        Assert.Equal("JMDroid", history[0].Source);
        Assert.Equal("Galaxy S24", history[0].SourceDeviceName);
        Assert.Contains("C:\\new.pdf", history[0].AfterValue);
        Assert.Null(_service.AddAttachments("missing", ["C:\\missing.pdf"]));
    }

    [Fact]
    public void Completing_all_checklist_weight_completes_task_and_history_shows_app_origin_by_default()
    {
        var task = _service.Create(new TodoTaskApiTaskInput("Finalizar", Checklist:
            [new TodoTaskChecklistEntry("Tudo", 100)]));

        var completed = _service.SetChecklistStep(task.Id, 0, true);

        Assert.NotNull(completed!.CompletedAt);
        Assert.Empty(_service.GetTasks("open"));
        Assert.Single(_service.GetTasks("completed"));
        Assert.All(_service.GetTaskHistory("", 0, 20).Items, item => Assert.Equal("APP", item.Source));
    }

    [Fact]
    public void Task_lifecycle_supports_pause_reopen_and_delete()
    {
        var task = _service.Create(new TodoTaskApiTaskInput("Aguardando"));
        var paused = _service.Pause(task.Id, "Aprovação", "Ana", "JMDroid", "Galaxy");
        Assert.Equal("Aprovação", paused!.PauseReason);
        Assert.Equal("Ana", paused.PausedBy);
        Assert.Null(_service.Unpause(task.Id)!.PausedAt);
        Assert.NotNull(_service.SetCompleted(task.Id, true)!.CompletedAt);
        Assert.Null(_service.SetCompleted(task.Id, false)!.CompletedAt);
        Assert.NotNull(_service.Delete(task.Id));
        Assert.Empty(_service.GetTasks("all"));
    }

    [Fact]
    public void Missing_task_ids_and_invalid_checklist_positions_are_handled()
    {
        Assert.Null(_service.GetTask("missing"));
        Assert.Null(_service.Update("missing", new TodoTaskApiTaskInput("Valid")));
        Assert.Null(_service.SetCompleted("missing", true));
        Assert.Null(_service.Pause("missing", "", ""));
        Assert.Null(_service.Unpause("missing"));
        Assert.Null(_service.Delete("missing"));
        Assert.Throws<ArgumentException>(() => _service.GetTasks("paused"));

        var task = _service.Create(new TodoTaskApiTaskInput("Sem etapas"));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.SetChecklistStep(task.Id, 0, true));
    }

    [Fact]
    public void Task_history_search_and_paging_return_only_task_events_with_origin()
    {
        _service.Create(new TodoTaskApiTaskInput("Encontrar contrato"), "JMDroid", "Moto G");
        _history.Add("Executar comando", "entrada", "saída");
        _service.Create(new TodoTaskApiTaskInput("Outra tarefa"));

        var page = _service.GetTaskHistory("contrato", 0, 1);

        Assert.Equal(1, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("JMDroid", item.Source);
        Assert.Equal("Moto G", item.SourceDeviceName);
        Assert.Empty(_service.GetTaskHistory("inexistente", 0, 10).Items);
    }

    [Fact]
    public void Invalid_task_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => _service.Create(new TodoTaskApiTaskInput("  ")));
        Assert.Throws<ArgumentException>(() => _service.Create(new TodoTaskApiTaskInput("Invalid checklist", Checklist:
            [new TodoTaskChecklistEntry("One", 60), new TodoTaskChecklistEntry("Two", 60)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.GetTaskHistory("", 0, 0));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
