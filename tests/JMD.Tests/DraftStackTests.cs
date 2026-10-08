using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class DraftStackTests
{
    [Fact]
    public void Push_and_pop_use_last_in_first_out_order()
    {
        var stack = new DraftStack();

        Assert.True(stack.Push("primeiro"));
        Assert.True(stack.Push("segundo"));
        Assert.Equal(2, stack.Count);
        Assert.True(stack.TryPop(out var latest));
        Assert.Equal("segundo", latest);
        Assert.True(stack.TryPop(out var earlier));
        Assert.Equal("primeiro", earlier);
        Assert.False(stack.TryPop(out var empty));
        Assert.Null(empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void Push_ignores_empty_text(string? text)
    {
        var stack = new DraftStack();

        Assert.False(stack.Push(text));
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void Restore_from_history_replays_saves_and_restores_in_order()
    {
        var at = DateTimeOffset.UtcNow;
        var stack = new DraftStack();
        stack.Push("stale state");

        stack.RestoreFromHistory([
            Entry(3, at.AddMinutes(2), DraftStack.RestoredCommandName, "draft B"),
            Entry(1, at, DraftStack.SavedCommandName, "draft A"),
            Entry(2, at.AddMinutes(1), DraftStack.SavedCommandName, "draft B")
        ]);

        Assert.Equal(1, stack.Count);
        Assert.True(stack.TryPop(out var remaining));
        Assert.Equal("draft A", remaining);
    }

    [Fact]
    public void Restore_from_history_uses_id_to_order_events_with_same_timestamp()
    {
        var at = DateTimeOffset.UtcNow;
        var stack = new DraftStack();

        stack.RestoreFromHistory([
            Entry(2, at, DraftStack.RestoredCommandName, "draft A"),
            Entry(1, at, DraftStack.SavedCommandName, "draft A"),
            Entry(3, at, DraftStack.SavedCommandName, "draft B")
        ]);

        Assert.True(stack.TryPop(out var remaining));
        Assert.Equal("draft B", remaining);
    }

    [Fact]
    public void Restore_from_history_does_not_pop_an_unrelated_draft_when_older_save_was_expired()
    {
        var at = DateTimeOffset.UtcNow;
        var stack = new DraftStack();

        stack.RestoreFromHistory([
            Entry(1, at, DraftStack.SavedCommandName, "draft B"),
            Entry(2, at.AddMinutes(1), DraftStack.RestoredCommandName, "expired draft A")
        ]);

        Assert.True(stack.TryPop(out var remaining));
        Assert.Equal("draft B", remaining);
    }

    [Fact]
    public void Restored_draft_can_be_replaced_by_the_text_saved_from_the_active_field()
    {
        var at = DateTimeOffset.UtcNow;
        var stack = new DraftStack();

        stack.RestoreFromHistory([
            Entry(1, at, DraftStack.SavedCommandName, "interrupted text"),
            Entry(2, at.AddSeconds(1), DraftStack.RestoredCommandName, "interrupted text"),
            Entry(3, at.AddSeconds(2), DraftStack.SavedCommandName, "quick reply")
        ]);

        Assert.True(stack.TryPop(out var latest));
        Assert.Equal("quick reply", latest);
        Assert.Equal(0, stack.Count);
    }

    private static ConversionHistoryEntry Entry(long id, DateTimeOffset at, string command, string text)
        => new(id, at, command, string.Empty, text);
}
