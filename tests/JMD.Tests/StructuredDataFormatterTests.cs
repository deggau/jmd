using System.Text.Json;
using System.Xml.Linq;
using JMD.Core;
using JMD.Tools;
using Xunit;

namespace JMD.Tests;

public sealed class StructuredDataFormatterTests
{
    private readonly StructuredDataFormatter _formatter = new();

    [Theory]
    [InlineData("{\"name\":\"JMD\",\"enabled\":true}")]
    [InlineData("[1,2,{\"value\":3}]")]
    [InlineData("42")]
    public void Pretty_formats_valid_json_and_preserves_its_value(string input)
    {
        var result = _formatter.Transform(input, StructuredDataLayout.Pretty);

        Assert.True(result.Transformation.Success, result.Transformation.Error);
        Assert.Equal(StructuredDataFormat.Json, result.Format);
        if (input.TrimStart()[0] is '{' or '[')
            Assert.Contains(Environment.NewLine, result.Transformation.Value);
        using var expected = JsonDocument.Parse(input);
        using var actual = JsonDocument.Parse(result.Transformation.Value!);
        Assert.Equal(JsonSerializer.Serialize(expected.RootElement), JsonSerializer.Serialize(actual.RootElement));
    }

    [Theory]
    [InlineData("{ \"name\": \"JMD\", \"values\": [ 1, 2 ] }", "{\"name\":\"JMD\",\"values\":[1,2]}")]
    [InlineData("[ 1, { \"ok\": true } ]", "[1,{\"ok\":true}]")]
    public void Compact_json_removes_formatting_without_changing_its_value(string input, string expectedCompact)
    {
        var result = _formatter.Transform(input, StructuredDataLayout.Compact);

        Assert.True(result.Transformation.Success, result.Transformation.Error);
        Assert.Equal(StructuredDataFormat.Json, result.Format);
        Assert.Equal(expectedCompact, result.Transformation.Value);
        using var expected = JsonDocument.Parse(input);
        using var actual = JsonDocument.Parse(result.Transformation.Value!);
        Assert.Equal(JsonSerializer.Serialize(expected.RootElement), JsonSerializer.Serialize(actual.RootElement));
    }

    [Theory]
    [InlineData("<root><item id=\"1\">valor</item><item id=\"2\" /></root>")]
    [InlineData("<?xml version=\"1.0\"?><root><child /></root>")]
    public void Pretty_formats_xml_and_preserves_its_document(string input)
    {
        var result = _formatter.Transform(input, StructuredDataLayout.Pretty);

        Assert.True(result.Transformation.Success, result.Transformation.Error);
        Assert.Equal(StructuredDataFormat.Xml, result.Format);
        Assert.Contains(Environment.NewLine, result.Transformation.Value);
        Assert.True(XNode.DeepEquals(XDocument.Parse(input), XDocument.Parse(result.Transformation.Value!)));
    }

    [Theory]
    [InlineData("<root>\n  <item>valor</item>\n</root>", "<root><item>valor</item></root>")]
    [InlineData("<root><item id=\"1\" /><item id=\"2\" /></root>", "<root><item id=\"1\" /><item id=\"2\" /></root>")]
    public void Compact_xml_removes_indentation_and_line_breaks(string input, string expected)
    {
        var result = _formatter.Transform(input, StructuredDataLayout.Compact);

        Assert.True(result.Transformation.Success, result.Transformation.Error);
        Assert.Equal(StructuredDataFormat.Xml, result.Format);
        Assert.Equal(expected, result.Transformation.Value);
        Assert.True(XNode.DeepEquals(XDocument.Parse(input), XDocument.Parse(result.Transformation.Value!)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("não é JSON nem XML")]
    [InlineData("{broken}")]
    [InlineData("<root>")]
    public void Invalid_or_empty_input_returns_failure_without_format(string input)
    {
        var result = _formatter.Transform(input, StructuredDataLayout.Pretty);

        Assert.False(result.Transformation.Success);
        Assert.Null(result.Transformation.Value);
        Assert.Null(result.Format);
        Assert.False(string.IsNullOrWhiteSpace(result.Transformation.Error));
    }
}
