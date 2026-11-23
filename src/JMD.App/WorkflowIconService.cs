using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using JMD.Core;

namespace JMD.App;

internal sealed record WorkflowIconData(string Target, string? DataUrl);

internal static class WorkflowIconService
{
    private const int IconSize = 64;
    private const int MaximumDownloadBytes = 2 * 1024 * 1024;
    private static readonly Regex VariablePattern = new(@"\{\{[A-Za-z][A-Za-z0-9_]{0,39}\}\}", RegexOptions.Compiled);
    private static readonly Regex LinkTagPattern = new("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AttributePattern = new("([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static async Task<WorkflowIconData?> ResolveAsync(
        IEnumerable<CustomWorkflowStep> steps,
        string? previousTarget = null,
        string? previousDataUrl = null,
        CancellationToken cancellationToken = default)
    {
        var openTarget = steps.FirstOrDefault(step => step.Action is "Open" or "OpenProgram" or "OpenUrl")?.Value;
        var target = GetIconTarget(openTarget);
        if (target is null) return null;
        if (string.Equals(target, previousTarget, StringComparison.OrdinalIgnoreCase) &&
            IsValidDataUrl(previousDataUrl))
            return new WorkflowIconData(target, previousDataUrl);

        try
        {
            var executable = GetExecutablePath(openTarget);
            if (executable is not null)
            {
                var icon = ExtractExecutableIcon(executable);
                if (icon is not null) return new WorkflowIconData(target, icon);
            }

            if (Uri.TryCreate(target, UriKind.Absolute, out var pageUri) &&
                pageUri.Scheme is "http" or "https")
            {
                var favicon = await DownloadFaviconAsync(pageUri, cancellationToken);
                return new WorkflowIconData(target, favicon);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or InvalidOperationException or
                                           System.Runtime.InteropServices.ExternalException or
                                           HttpRequestException or TaskCanceledException or
                                           System.Security.SecurityException)
        {
            CriticalErrorLog.Write("Obter ícone do workflow", exception);
        }

        return new WorkflowIconData(target, null);
    }

    public static BitmapImage? ToImageSource(string? dataUrl)
    {
        if (dataUrl is null || !IsValidDataUrl(dataUrl)) return null;
        try
        {
            var base64 = dataUrl[(dataUrl.IndexOf(',') + 1)..];
            var bytes = Convert.FromBase64String(base64);
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes, writable: false);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = IconSize;
            image.DecodePixelHeight = IconSize;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is FormatException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetIconTarget(string? targetText)
    {
        if (string.IsNullOrWhiteSpace(targetText)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(VariablePattern.Replace(targetText.Trim(), "icon-value"));
        if (!WorkflowCommandLinePolicy.TryParse(expanded, out var executable, out _)) return null;
        if (Uri.TryCreate(executable, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.GetLeftPart(UriPartial.Authority);
        return executable.Trim('"');
    }

    private static string? GetExecutablePath(string? targetText)
    {
        if (string.IsNullOrWhiteSpace(targetText)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(VariablePattern.Replace(targetText.Trim(), "icon-value"));
        if (!WorkflowCommandLinePolicy.TryParse(expanded, out var executable, out _)) return null;
        if (Uri.TryCreate(executable, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return null;
        return ResolveExecutable(executable);
    }

    private static string? ResolveExecutable(string executable)
    {
        executable = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        if (File.Exists(executable)) return executable;
        var fileName = Path.GetFileName(executable);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                var appPath = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(appPath) && File.Exists(appPath)) return appPath;
            }
            catch (System.Security.SecurityException) { }
        }

        if (Path.IsPathRooted(executable)) return null;
        var pathExtensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = Path.HasExtension(fileName)
            ? [fileName]
            : pathExtensions.Select(extension => fileName + extension).ToArray();
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var candidate in candidates)
        {
            var fullPath = Path.Combine(directory.Trim('"'), candidate);
            if (File.Exists(fullPath)) return fullPath;
        }
        return null;
    }

    private static string? ExtractExecutableIcon(string executable)
    {
        using var icon = Icon.ExtractAssociatedIcon(executable);
        if (icon is null) return null;
        using var source = icon.ToBitmap();
        return EncodeThumbnail(source);
    }

    private static async Task<string?> DownloadFaviconAsync(Uri pageUri, CancellationToken cancellationToken)
    {
        var iconCandidates = new List<Uri> { new(pageUri, "/favicon.ico") };
        try
        {
            using var response = await Http.GetAsync(pageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode && IsAllowedContentLength(response))
            {
                var bytes = await ReadLimitedAsync(response, cancellationToken);
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                    TryEncodeThumbnail(bytes) is { } directImage)
                    return directImage;
                if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var html = System.Text.Encoding.UTF8.GetString(bytes);
                    iconCandidates.AddRange(FindDeclaredIcons(html, response.RequestMessage?.RequestUri ?? pageUri));
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // Try the conventional /favicon.ico location below.
        }

        foreach (var candidate in iconCandidates.DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await Http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode || !IsAllowedContentLength(response)) continue;
                var bytes = await ReadLimitedAsync(response, cancellationToken);
                if (TryEncodeThumbnail(bytes) is { } icon) return icon;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                // The next favicon candidate may still work.
            }
        }
        return null;
    }

    private static IEnumerable<Uri> FindDeclaredIcons(string html, Uri baseUri)
    {
        foreach (Match tag in LinkTagPattern.Matches(html))
        {
            var attributes = AttributePattern.Matches(tag.Value).Cast<Match>()
                .ToDictionary(match => match.Groups[1].Value, match =>
                    match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value,
                    StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("rel", out var rel) || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                !attributes.TryGetValue("href", out var href) || string.IsNullOrWhiteSpace(href)) continue;
            if (Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href), out var iconUri) && iconUri.Scheme is "http" or "https")
                yield return iconUri;
        }
    }

    private static bool IsAllowedContentLength(HttpResponseMessage response)
        => response.Content.Headers.ContentLength is not { } length || length is > 0 and <= MaximumDownloadBytes;

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (destination.Length + count > MaximumDownloadBytes) throw new IOException("O arquivo de ícone excede o limite permitido.");
            destination.Write(buffer, 0, count);
        }
        return destination.ToArray();
    }

    private static string? TryEncodeThumbnail(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width is <= 0 or > 4096 || image.Height is <= 0 or > 4096) return null;
            return EncodeThumbnail(image);
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException or ExternalException)
        {
            return null;
        }
    }

    private static string EncodeThumbnail(Image source)
    {
        using var thumbnail = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(thumbnail))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            var scale = Math.Min((double)IconSize / source.Width, (double)IconSize / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            graphics.DrawImage(source, (IconSize - width) / 2, (IconSize - height) / 2, width, height);
        }
        using var output = new MemoryStream();
        thumbnail.Save(output, ImageFormat.Png);
        return $"data:image/png;base64,{Convert.ToBase64String(output.ToArray())}";
    }

    private static bool IsValidDataUrl(string? dataUrl)
        => !string.IsNullOrWhiteSpace(dataUrl) && dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal) &&
           dataUrl.Length <= 100_000;
}
