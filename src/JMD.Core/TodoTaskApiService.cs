namespace JMD.Core;

public sealed record TodoTaskApiTaskInput(
    string Title,
    string Description = "",
    int? ReminderIntervalMinutes = null,
    string CategoryColor = "Verde",
    string Urls = "",
    string Attachments = "",
    IReadOnlyList<TodoTaskChecklistEntry>? Checklist = null);

public sealed record TodoTaskApiChecklistCompletion(bool IsCompleted);
public sealed record TodoTaskApiPauseInput(string Reason = "", string Responsible = "");
public sealed record TodoTaskApiPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public sealed class TodoTaskApiService
{
    private readonly TodoTaskStore _tasks;
    private readonly ConversionHistoryStore _history;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();

    public event Action? TasksChanged;

    public TodoTaskApiService(TodoTaskStore tasks, ConversionHistoryStore history, Func<DateTimeOffset>? now = null)
    {
        _tasks = tasks;
        _history = history;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public IReadOnlyList<TodoTaskEntry> GetTasks(string status = "open")
    {
        var tasks = _tasks.GetAll();
        return status.ToLowerInvariant() switch
        {
            "open" => tasks.Where(task => task.CompletedAt is null).OrderBy(task => task.CreatedAt).ToArray(),
            "completed" => tasks.Where(task => task.CompletedAt is not null).OrderByDescending(task => task.CompletedAt).ToArray(),
            "all" => tasks.OrderBy(task => task.CreatedAt).ToArray(),
            _ => throw new ArgumentException("Status deve ser open, completed ou all.", nameof(status))
        };
    }

    public TodoTaskEntry? GetTask(string taskId) => _tasks.GetAll().FirstOrDefault(task => task.Id == taskId);

    public TodoTaskEntry Create(TodoTaskApiTaskInput input, string source = "APP", string? deviceName = null)
    {
        Validate(input);
        lock (_sync)
        {
            var created = _tasks.Create(input.Title.Trim(), input.Description, _now(), input.ReminderIntervalMinutes,
                input.CategoryColor, input.Urls, input.Attachments);
            var task = created with { Checklist = input.Checklist?.ToList() ?? [] };
            _tasks.SaveAll(_tasks.GetAll().Select(existing => existing.Id == task.Id ? task : existing));
            Record("criada", task, string.Empty, TaskSummary("Tarefa criada", task), source, deviceName);
            TasksChanged?.Invoke();
            return task;
        }
    }

    public TodoTaskEntry? Update(string taskId, TodoTaskApiTaskInput input, string source = "APP", string? deviceName = null)
    {
        Validate(input);
        lock (_sync)
        {
            var tasks = _tasks.GetAll().ToList();
            var index = tasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return null;
            var previous = tasks[index];
            var updated = previous with
            {
                Title = input.Title.Trim(), Description = input.Description,
                ReminderIntervalMinutes = input.ReminderIntervalMinutes,
                LastReminderAt = input.ReminderIntervalMinutes is null ? null
                    : previous.ReminderIntervalMinutes != input.ReminderIntervalMinutes ? _now() : previous.LastReminderAt,
                CategoryColor = input.CategoryColor, Urls = input.Urls, Attachments = input.Attachments,
                Checklist = input.Checklist?.ToList() ?? []
            };
            if (EditableFieldsEqual(previous, updated)) return previous;
            tasks[index] = updated;
            _tasks.SaveAll(tasks);
            Record("editada", updated, TaskSummary("Antes", previous), DescribeTaskChanges(previous, updated), source, deviceName);
            TasksChanged?.Invoke();
            return updated;
        }
    }

    public TodoTaskEntry? SetChecklistStep(string taskId, int index, bool isCompleted, string source = "APP", string? deviceName = null)
    {
        lock (_sync)
        {
            var tasks = _tasks.GetAll().ToList();
            var taskIndex = tasks.FindIndex(task => task.Id == taskId);
            if (taskIndex < 0) return null;
            var task = tasks[taskIndex];
            var checklist = task.Checklist?.ToList() ?? [];
            if (index < 0 || index >= checklist.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (checklist[index].IsCompleted == isCompleted) return task;
            checklist[index] = checklist[index] with { IsCompleted = isCompleted };
            var updated = task with { Checklist = checklist };
            tasks[taskIndex] = updated;
            var percent = TodoTaskChecklistWeightPolicy.CompletedPercent(checklist);
            var historyEvents = new List<(string Action, TodoTaskEntry Task, string After)>
            {
                ("checklist", updated, $"Etapa \"{checklist[index].Title}\" {(isCompleted ? "concluída" : "reaberta")} na tarefa {task.Title} — {percent:0.#}% concluído")
            };
            if (updated.CompletedAt is null && TodoTaskChecklistWeightPolicy.IsComplete(checklist))
            {
                if (updated.PausedAt is not null)
                {
                    historyEvents.Add(("destravada", updated, $"Tarefa destravada: {updated.Title}"));
                    updated = updated with { PausedAt = null, PauseReason = string.Empty, PausedBy = string.Empty };
                    tasks[taskIndex] = updated;
                }
                updated = updated with { CompletedAt = _now() };
                tasks[taskIndex] = updated;
                historyEvents.Add(("concluída", updated, TaskSummary("Tarefa concluída", updated)));
            }
            _tasks.SaveAll(tasks);
            foreach (var entry in historyEvents) Record(entry.Action, entry.Task, string.Empty, entry.After, source, deviceName);
            TasksChanged?.Invoke();
            return updated;
        }
    }

    public TodoTaskEntry? SetCompleted(string taskId, bool completed, string source = "APP", string? deviceName = null)
    {
        lock (_sync)
        {
            var tasks = _tasks.GetAll().ToList();
            var index = tasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return null;
            var task = tasks[index];
            if ((task.CompletedAt is not null) == completed) return task;
            if (completed && task.PausedAt is not null)
            {
                Record("destravada", task, string.Empty, $"Tarefa destravada: {task.Title}", source, deviceName);
                task = task with { PausedAt = null, PauseReason = string.Empty, PausedBy = string.Empty };
            }
            task = task with { CompletedAt = completed ? _now() : null };
            tasks[index] = task;
            _tasks.SaveAll(tasks);
            Record(completed ? "concluída" : "reaberta", task, string.Empty,
                TaskSummary(completed ? "Tarefa concluída" : "Tarefa reaberta", task), source, deviceName);
            TasksChanged?.Invoke();
            return task;
        }
    }

    public TodoTaskEntry? Pause(string taskId, string reason, string responsible, string source = "APP", string? deviceName = null)
    {
        if (reason is null || responsible is null || reason.Length > 1000 || responsible.Length > 200)
            throw new ArgumentException("Motivo ou responsável excede o tamanho permitido.");
        lock (_sync)
        {
            var tasks = _tasks.GetAll().ToList();
            var index = tasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return null;
            var task = tasks[index];
            if (task.CompletedAt is not null) throw new InvalidOperationException("Uma tarefa concluída não pode ser pausada.");
            if (task.PausedAt is not null) return task;
            task = task with { PausedAt = _now(), PauseReason = reason.Trim(), PausedBy = responsible.Trim() };
            tasks[index] = task;
            _tasks.SaveAll(tasks);
            var details = string.Join(" - ", new[]
            {
                string.IsNullOrWhiteSpace(task.PausedBy) ? null : $"Responsável: {task.PausedBy}",
                string.IsNullOrWhiteSpace(task.PauseReason) ? null : $"Motivo: {task.PauseReason}"
            }.Where(value => value is not null));
            Record("pausada", task, string.Empty, TaskSummary("Tarefa pausada", task) + (details.Length == 0 ? string.Empty : $" - {details}"), source, deviceName);
            TasksChanged?.Invoke();
            return task;
        }
    }

    public TodoTaskEntry? Unpause(string taskId, string source = "APP", string? deviceName = null)
    {
        lock (_sync)
        {
            var tasks = _tasks.GetAll().ToList();
            var index = tasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return null;
            var task = tasks[index];
            if (task.PausedAt is null) return task;
            var pausedBy = task.PausedBy;
            task = task with { PausedAt = null, PauseReason = string.Empty, PausedBy = string.Empty, LastReminderAt = _now() };
            tasks[index] = task;
            _tasks.SaveAll(tasks);
            Record("destravada", task, string.Empty, $"Tarefa destravada por {pausedBy}: {task.Title}", source, deviceName);
            TasksChanged?.Invoke();
            return task;
        }
    }

    public TodoTaskEntry? Delete(string taskId, string source = "APP", string? deviceName = null)
    {
        lock (_sync)
        {
            var tasks = _tasks.GetAll().ToList();
            var index = tasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return null;
            var task = tasks[index];
            tasks.RemoveAt(index);
            _tasks.SaveAll(tasks);
            Record("excluída", task, TaskSummary("Antes da exclusão", task), string.Empty, source, deviceName);
            TasksChanged?.Invoke();
            return task;
        }
    }

    public TodoTaskApiPage<ConversionHistoryEntry> GetTaskHistory(string query, int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var total = _history.CountTaskSearch(query);
        return new TodoTaskApiPage<ConversionHistoryEntry>(_history.SearchTaskPage(query, pageSize, page), total, page, pageSize);
    }

    private void Record(string action, TodoTaskEntry task, string before, string after, string source, string? deviceName)
    {
        var normalizedSource = string.Equals(source, "JMDroid", StringComparison.OrdinalIgnoreCase) ? "JMDroid" : "APP";
        _history.Add($"Tarefa {action} — {task.Title}", before, after, origin: normalizedSource, sourceDeviceName: normalizedSource == "JMDroid" ? deviceName : null);
    }

    private static string TaskSummary(string prefix, TodoTaskEntry task)
        => $"{prefix}: {task.Title}" + (string.IsNullOrWhiteSpace(task.Description) ? string.Empty : $" — {task.Description}");

    private static bool EditableFieldsEqual(TodoTaskEntry left, TodoTaskEntry right)
        => left.Title == right.Title && left.Description == right.Description &&
           left.ReminderIntervalMinutes == right.ReminderIntervalMinutes && left.CategoryColor == right.CategoryColor &&
           left.Urls == right.Urls && left.Attachments == right.Attachments &&
           (left.Checklist ?? []).SequenceEqual(right.Checklist ?? []);

    private static string DescribeTaskChanges(TodoTaskEntry before, TodoTaskEntry after)
    {
        var changes = new List<string>();
        if (before.Title != after.Title) changes.Add($"Título alterado para: {after.Title}");
        if (before.Description != after.Description) changes.Add("Descrição alterada");
        if (before.CategoryColor != after.CategoryColor) changes.Add($"Prioridade alterada para {after.CategoryColor}");
        if (before.ReminderIntervalMinutes != after.ReminderIntervalMinutes)
            changes.Add(after.ReminderIntervalMinutes is { } interval ? $"Lembrete alterado para cada {interval} minutos" : "Lembrete desativado");
        changes.AddRange(DescribeLineChanges(before.Attachments, after.Attachments, "anexo"));
        changes.AddRange(DescribeLineChanges(before.Urls, after.Urls, "URL"));

        var beforeByTitle = (before.Checklist ?? []).GroupBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.CurrentCultureIgnoreCase);
        var afterByTitle = (after.Checklist ?? []).GroupBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.CurrentCultureIgnoreCase);
        changes.AddRange((after.Checklist ?? []).Where(item => !beforeByTitle.ContainsKey(item.Title)).Select(item => $"Etapa adicionada: {item.Title}"));
        changes.AddRange((before.Checklist ?? []).Where(item => !afterByTitle.ContainsKey(item.Title)).Select(item => $"Etapa removida: {item.Title}"));
        foreach (var item in after.Checklist ?? [])
        {
            if (!beforeByTitle.TryGetValue(item.Title, out var previous)) continue;
            if (previous.Weight != item.Weight) changes.Add($"Peso da etapa \"{item.Title}\" alterado para {item.Weight:0.#}%");
            if (previous.IsCompleted != item.IsCompleted) changes.Add($"Etapa \"{item.Title}\" {(item.IsCompleted ? "concluída" : "reaberta")}");
        }
        return changes.Count == 0 ? "Nenhuma alteração registrada" : string.Join(Environment.NewLine, changes);
    }

    private static IEnumerable<string> DescribeLineChanges(string before, string after, string itemType)
    {
        var oldItems = before.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = after.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return newItems.Except(oldItems).Select(item => $"Adicionado {itemType} {item}")
            .Concat(oldItems.Except(newItems).Select(item => $"Removido {itemType} {item}"));
    }

    private static void Validate(TodoTaskApiTaskInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 300)
            throw new ArgumentException("O título é obrigatório e deve ter até 300 caracteres.", nameof(input));
        if (input.Description is null || input.CategoryColor is null || input.Urls is null || input.Attachments is null ||
            input.Description.Length > 100_000 || input.Urls.Length > 20_000 || input.Attachments.Length > 20_000)
            throw new ArgumentException("Um dos campos excede o tamanho permitido.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.CategoryColor) || input.CategoryColor.Length > 40)
            throw new ArgumentException("Informe uma prioridade válida.", nameof(input));
        if (input.ReminderIntervalMinutes is <= 0 or > 10080)
            throw new ArgumentException("O intervalo do lembrete deve estar entre 1 e 10080 minutos.", nameof(input));
        var checklist = input.Checklist ?? [];
        if (checklist.Count > 100 || checklist.Any(item => item is null || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 300 || item.Weight is < 0 or > 100) || checklist.Sum(item => item.Weight) > 100)
            throw new ArgumentException("A lista de etapas deve conter até 100 itens, com título e pesos cuja soma não ultrapasse 100%.", nameof(input));
    }
}
