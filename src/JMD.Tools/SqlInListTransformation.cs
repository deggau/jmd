using JMD.Core;

namespace JMD.Tools;

public sealed class SqlInListTransformation : ITextTransformation
{
    private static readonly string[] Delimiters = ["\r\n", "\n", "\r", ",", "|", ";"];

    public string Id => "sql-in-list";
    public string Name => "Formatar lista para SQL IN";
    public string Description => "Converte valores separados em literais SQL entre apóstrofos.";

    public TransformationResult Transform(string input) => Transform(input, null);

    public TransformationResult Transform(string input, string? preferredDelimiter)
    {
        if (string.IsNullOrWhiteSpace(input))
            return TransformationResult.Fail("O clipboard não contém texto para converter.");

        var delimiterResult = FindDelimiter(input, preferredDelimiter);
        if (!delimiterResult.Success)
            return TransformationResult.Fail(delimiterResult.Error!);

        var normalizedInput = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        List<string> items = delimiterResult.Delimiter is null
            ? [input.Trim()]
            : normalizedInput.Split(delimiterResult.Delimiter, StringSplitOptions.None)
                .Select(item => item.Trim())
                .ToList();

        while (items.Count > 0 && items[0].Length == 0) items.RemoveAt(0);
        while (items.Count > 0 && items[^1].Length == 0) items.RemoveAt(items.Count - 1);

        if (items.Count == 0)
            return TransformationResult.Fail("A lista não contém valores.");
        if (items.Any(item => item.Length == 0))
            return TransformationResult.Fail("A lista contém um valor vazio entre separadores.");

        var quotedItems = items.Select(item => $"'{item.Replace("'", "''", StringComparison.Ordinal)}'");
        return TransformationResult.Ok(string.Join(",", quotedItems));
    }

    private static (bool Success, string? Delimiter, string? Error) FindDelimiter(string input, string? preferredDelimiter)
    {
        if (preferredDelimiter is not null)
        {
            if (!Delimiters.Contains(preferredDelimiter, StringComparer.Ordinal))
                return (false, null, "O delimitador selecionado não é suportado.");

            var normalizedInput = input.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var normalizedDelimiter = preferredDelimiter is "\r\n" or "\r" or "\n" ? "\n" : preferredDelimiter;
            return normalizedInput.Contains(normalizedDelimiter, StringComparison.Ordinal)
                ? (true, normalizedDelimiter, null)
                : (true, null, null);
        }

        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var candidates = new[] { "\n", ";", "|", "," }
            .Select(delimiter => (Delimiter: delimiter, Count: Count(normalized, delimiter)))
            .Where(candidate => candidate.Count > 0)
            .OrderByDescending(candidate => candidate.Count)
            .ToList();

        if (candidates.Count == 0) return (true, null, null);
        if (candidates.Count > 1 && candidates[0].Count == candidates[1].Count)
            return (false, null, "Não consegui identificar um delimitador único. Escolha vírgula, pipe, ponto e vírgula ou quebra de linha.");

        return (true, candidates[0].Delimiter, null);
    }

    private static int Count(string input, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = input.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
