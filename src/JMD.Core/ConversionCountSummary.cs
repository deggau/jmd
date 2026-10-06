namespace JMD.Core;

public static class ConversionCountSummary
{
    public static string Append(string message, int? identified, int? output)
    {
        if (identified is null || output is null) return message;
        var duplicatesRemoved = identified.Value - output.Value;
        return $"{message} Itens identificados: {identified.Value}. Removidos por duplicidade: {duplicatesRemoved}.";
    }
}
