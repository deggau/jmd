using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Xml.Linq;
using JMD.Core;

namespace JMD.Tools;

public enum StructuredDataFormat
{
    Json,
    Xml
}

public enum StructuredDataLayout
{
    Pretty,
    Compact
}

public sealed record StructuredDataFormatResult(TransformationResult Transformation, StructuredDataFormat? Format);

public sealed class StructuredDataFormatter
{
    public StructuredDataFormatResult Transform(string input, StructuredDataLayout layout)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new(TransformationResult.Fail("Não há texto para formatar."), null);

        try
        {
            using var document = JsonDocument.Parse(input);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = layout == StructuredDataLayout.Pretty,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
                document.RootElement.WriteTo(writer);
            return new(TransformationResult.Ok(Encoding.UTF8.GetString(stream.ToArray())), StructuredDataFormat.Json);
        }
        catch (JsonException)
        {
            // A entrada pode ser XML; tente o outro formato antes de retornar erro.
        }

        try
        {
            var document = XDocument.Parse(input);
            var options = layout == StructuredDataLayout.Compact ? SaveOptions.DisableFormatting : SaveOptions.None;
            return new(TransformationResult.Ok(document.ToString(options)), StructuredDataFormat.Xml);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            return new(TransformationResult.Fail("O texto não é um JSON ou XML válido."), null);
        }
    }
}
