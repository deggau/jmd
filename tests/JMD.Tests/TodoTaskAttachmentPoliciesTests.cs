using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class TodoTaskAttachmentPoliciesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jmd-task-attachments-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Clipboard_file_selection_becomes_attachments()
    {
        var capture = TodoTaskClipboardCapturePolicy.Resolve("ignored", null, ["C:\\orders\\problem.pdf"]);

        Assert.Equal("C:\\orders\\problem.pdf", capture.Attachments);
        Assert.Empty(capture.Description);
        Assert.Empty(capture.Urls);
    }

    [Theory]
    [InlineData("https://example.test/orders/42")]
    [InlineData("https://example.test/orders/42\r\nhttps://example.test/orders/43")]
    public void Clipboard_urls_become_task_urls(string text)
    {
        var capture = TodoTaskClipboardCapturePolicy.Resolve(text, null, []);

        Assert.Equal(text.Replace("\r\n", Environment.NewLine), capture.Urls);
        Assert.Empty(capture.Description);
    }

    [Fact]
    public void Clipboard_html_link_is_captured_as_url_when_its_anchor_text_matches_selection()
    {
        var capture = TodoTaskClipboardCapturePolicy.Resolve("pedido 42", "<a href=\"https://example.test/orders/42\">pedido 42</a>", []);

        Assert.Equal("https://example.test/orders/42", capture.Urls);
        Assert.Empty(capture.Description);
    }

    [Fact]
    public void Ordinary_clipboard_selection_becomes_description()
    {
        var capture = TodoTaskClipboardCapturePolicy.Resolve("Pedido 42 com erro", null, []);

        Assert.Equal("Pedido 42 com erro", capture.Description);
        Assert.Empty(capture.Urls);
    }

    [Fact]
    public void Organizer_moves_files_into_date_and_task_folder_and_is_idempotent()
    {
        var source = Path.Combine(_directory, "source", "order.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "order");
        var createdAt = new DateTimeOffset(2026, 11, 18, 12, 30, 0, TimeSpan.Zero);
        var root = Path.Combine(_directory, "attachments");

        var moved = TodoTaskAttachmentOrganizer.Organize(source, root, createdAt, 7);
        var destination = Path.Combine(root, createdAt.ToLocalTime().ToString("yyyy-MM-dd"), "Task-000007", "order.pdf");

        Assert.Equal(destination, Assert.Single(moved.Paths));
        Assert.Empty(moved.Errors);
        Assert.False(File.Exists(source));
        Assert.Equal("order", File.ReadAllText(destination));

        var again = TodoTaskAttachmentOrganizer.Organize(destination, root, createdAt, 7);
        Assert.Equal(destination, Assert.Single(again.Paths));
        Assert.Empty(again.Errors);
    }

    [Fact]
    public void Organizer_keeps_urls_and_unavailable_files_unchanged()
    {
        var missing = Path.Combine(_directory, "missing.pdf");
        var inputs = string.Join(Environment.NewLine, "https://example.test/order", missing);

        var result = TodoTaskAttachmentOrganizer.Organize(inputs, Path.Combine(_directory, "attachments"), DateTimeOffset.Now, 3);

        Assert.Equal(["https://example.test/order", missing], result.Paths);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Organizer_uses_unique_name_when_destination_already_contains_same_file_name()
    {
        var root = Path.Combine(_directory, "attachments");
        var taskFolder = Path.Combine(root, DateTimeOffset.Now.ToLocalTime().ToString("yyyy-MM-dd"), "Task-000003");
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(Path.Combine(taskFolder, "order.pdf"), "existing");
        var source = Path.Combine(_directory, "order.pdf");
        File.WriteAllText(source, "incoming");

        var result = TodoTaskAttachmentOrganizer.Organize(source, root, DateTimeOffset.Now, 3);

        Assert.Equal(Path.Combine(taskFolder, "order (2).pdf"), Assert.Single(result.Paths));
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(source));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(taskFolder, "order.pdf")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(taskFolder, "order (2).pdf")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
