namespace JMD.Core;

public static class ConversionCountSummary
{
    public static string Append(string message, int? identified, int? output)
    {
        if (identified is null || output is null) return message;
        var duplicatesRemoved = identified.Value - output.Value;
        var summary = $"{message} Itens identificados: {identified.Value}.";
        return duplicatesRemoved > 0
            ? $"{summary} Removidos por duplicidade: {duplicatesRemoved}."
            : summary;
    }
}
