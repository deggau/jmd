namespace JMD.Core;

/// <summary>In-memory LIFO stack for interrupted text drafts.</summary>
public sealed class DraftStack
{
    public const string SavedCommandName = "Salvar rascunho";
    public const string RestoredCommandName = "Restaurar rascunho";

    private readonly List<string> _items = [];

    public int Count => _items.Count;
    public IReadOnlyList<string> Items => _items.AsReadOnly();

    public bool Push(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        _items.Add(text);
        return true;
    }

    public bool TryPop(out string? text)
    {
        if (_items.Count == 0)
        {
            text = null;
            return false;
        }

        var lastIndex = _items.Count - 1;
        text = _items[lastIndex];
        _items.RemoveAt(lastIndex);
        return true;
    }

    /// <summary>Replays draft save and restore events in chronological order.</summary>
    public void RestoreFromHistory(IEnumerable<ConversionHistoryEntry> history)
    {
        _items.Clear();
        foreach (var entry in history.OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id))
        {
            if (entry.CommandName == SavedCommandName) Push(entry.AfterValue);
            else if (entry.CommandName == RestoredCommandName)
            {
                var restoredIndex = _items.FindLastIndex(text => text == entry.AfterValue);
                if (restoredIndex >= 0) _items.RemoveAt(restoredIndex);
            }
        }
    }
}
