using System.Net;
using System.Text.RegularExpressions;

namespace JMD.Core;

public sealed record TodoTaskClipboardCapture(string Description, string Urls, string Attachments);

public static partial class TodoTaskClipboardCapturePolicy
{
    private static readonly Regex AnchorRegex = new("<a\\b[^>]*\\bhref\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')[^>]*>(.*?)</a\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static TodoTaskClipboardCapture Resolve(string? text, string? html, IReadOnlyList<string>? filePaths)
    {
        var files = (filePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 0)
            return new TodoTaskClipboardCapture(string.Empty, string.Empty, string.Join(Environment.NewLine, files));

        var selectedText = text?.Trim() ?? string.Empty;
        if (TryNormalizeUrls(selectedText, out var urls))
            return new TodoTaskClipboardCapture(string.Empty, urls, string.Empty);

        if (selectedText.Length > 0 && !string.IsNullOrWhiteSpace(html))
        {
            var anchors = AnchorRegex.Matches(html);
            if (anchors.Count == 1)
            {
                var anchor = anchors[0];
                var href = WebUtility.HtmlDecode(anchor.Groups[1].Success ? anchor.Groups[1].Value : anchor.Groups[2].Value).Trim();
                var anchorText = WebUtility.HtmlDecode(Regex.Replace(anchor.Groups[3].Value, "<[^>]+>", string.Empty)).Trim();
                if (string.Equals(anchorText, selectedText, StringComparison.Ordinal) && TryNormalizeUrls(href, out urls))
                    return new TodoTaskClipboardCapture(string.Empty, urls, string.Empty);
            }
        }

        return new TodoTaskClipboardCapture(selectedText, string.Empty, string.Empty);
    }

    private static bool TryNormalizeUrls(string text, out string urls)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Any(line => !Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            urls = string.Empty;
            return false;
        }
        urls = string.Join(Environment.NewLine, lines);
        return true;
    }
}

public sealed record TodoTaskAttachmentOrganizationResult(IReadOnlyList<string> Paths, IReadOnlyList<string> Errors);

public static class TodoTaskAttachmentOrganizer
{
    public static TodoTaskAttachmentOrganizationResult Organize(string attachments, string rootFolder, DateTimeOffset createdAt, int taskNumber)
    {
        var paths = attachments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(rootFolder) || paths.Length == 0)
            return new TodoTaskAttachmentOrganizationResult(paths, []);

        var organized = new List<string>(paths.Length);
        var errors = new List<string>();
        string taskFolder;
        try
        {
            taskFolder = Path.Combine(Path.GetFullPath(rootFolder), createdAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                $"Task-{Math.Max(0, taskNumber):D6}");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new TodoTaskAttachmentOrganizationResult(paths, [$"Pasta de anexos inválida: {exception.Message}"]);
        }

        foreach (var path in paths)
        {
            if (IsHttpUrl(path) || !File.Exists(path))
            {
                organized.Add(path);
                continue;
            }

            try
            {
                var source = Path.GetFullPath(path);
                if (source.StartsWith(Path.GetFullPath(taskFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    organized.Add(source);
                    continue;
                }

                Directory.CreateDirectory(taskFolder);
                var destination = GetAvailableDestination(taskFolder, Path.GetFileName(source));
                File.Copy(source, destination, overwrite: false);
                try
                {
                    File.Delete(source);
                    organized.Add(destination);
                }
                catch
                {
                    organized.Add(source);
                    errors.Add($"O arquivo foi copiado para ‘{destination}’, mas não foi possível remover a origem ‘{source}’.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                organized.Add(path);
                errors.Add($"Não foi possível organizar ‘{path}’: {exception.Message}");
            }
        }

        return new TodoTaskAttachmentOrganizationResult(organized, errors);
    }

    private static string GetAvailableDestination(string taskFolder, string fileName)
    {
        var candidate = Path.Combine(taskFolder, fileName);
        if (!File.Exists(candidate)) return candidate;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(taskFolder, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
