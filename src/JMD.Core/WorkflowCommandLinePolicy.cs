using System.Text;
using System.Text.RegularExpressions;

namespace JMD.Core;

public static class WorkflowCommandLinePolicy
{
    private static readonly Regex ExecutableExtension = new(@"\.(?:exe|com|bat|cmd|lnk)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string? commandLine, out string executable, out IReadOnlyList<string> arguments,
        Func<string, bool>? fileExists = null)
    {
        executable = string.Empty;
        arguments = [];
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        var input = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        fileExists ??= File.Exists;

        // Prefer the longest existing file prefix. This handles the common Windows
        // shortcut case where a Start Menu .lnk path was pasted without quotes.
        if (input[0] != '"')
        {
            for (var index = input.Length - 1; index > 0; index--)
            {
                if (!char.IsWhiteSpace(input[index])) continue;
                var candidate = input[..index].TrimEnd();
                if (!fileExists(candidate)) continue;
                if (!TryParseArguments(input[(index + 1)..], out var parsedArguments)) return false;
                executable = candidate;
                arguments = parsedArguments;
                return true;
            }

            // Even if the file is currently unavailable, retain the complete path
            // through a recognized executable suffix so Process.Start reports the
            // actual missing target rather than a fragment such as "...\\Start".
            var suffix = ExecutableExtension.Match(input);
            if (suffix.Success)
            {
                var end = suffix.Index + suffix.Length;
                var candidate = input[..end];
                IReadOnlyList<string> parsedArguments = [];
                if (end == input.Length || TryParseArguments(input[end..], out parsedArguments))
                {
                    executable = candidate;
                    arguments = end == input.Length ? [] : parsedArguments;
                    return true;
                }
            }
        }

        if (!TryParseArguments(input, out var parts) || parts.Count == 0 || string.IsNullOrWhiteSpace(parts[0])) return false;
        executable = parts[0];
        arguments = parts.Skip(1).ToArray();
        return true;
    }

    public static bool TryParseArguments(string? commandLine, out IReadOnlyList<string> arguments)
    {
        arguments = [];
        if (commandLine is null) return false;

        var parsed = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        var slashes = 0;

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            if (character == '"')
            {
                current.Append('\\', slashes / 2);
                if (slashes % 2 == 1)
                {
                    current.Append('"');
                    started = true;
                }
                else
                {
                    quoted = !quoted;
                    started = true;
                }
                slashes = 0;
                continue;
            }

            current.Append('\\', slashes);
            slashes = 0;
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (started)
                {
                    parsed.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
                continue;
            }
            current.Append(character);
            started = true;
        }

        current.Append('\\', slashes);
        if (quoted) return false;
        if (started) parsed.Add(current.ToString());
        arguments = parsed;
        return true;
    }

    public static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashes);
                result.Append(character);
            }
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}

public static class WorkflowDialogClosePolicy
{
    /// <summary>Store the outcome separately and close, avoiding Window.DialogResult's modal-only restriction.</summary>
    public static void Close(bool accepted, Action<bool> storeResult, Action close)
    {
        ArgumentNullException.ThrowIfNull(storeResult);
        ArgumentNullException.ThrowIfNull(close);
        storeResult(accepted);
        close();
    }
}
