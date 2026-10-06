using JMD.Core;
using JMD.Tools;
using Xunit;

namespace JMD.Tests;

public sealed class SqlInDistinctNotificationTests
{
    private readonly SqlInListTransformation _transformation = new();

    [Theory]
    [InlineData("red,blue,red", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData("red|blue|red", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData("red;blue;red", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData("red\r\nblue\r\nred", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData("red\nblue\nred", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData("red\rblue\rred", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData(" red , blue , red ", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData(",red,blue,red,", null, "'red',\r\n'blue'", 3, 2)]
    [InlineData("same;same;same;same", null, "'same'", 4, 1)]
    [InlineData("red,blue,green", null, "'red',\r\n'blue',\r\n'green'", 3, 3)]
    [InlineData("A,a,A", null, "'A',\r\n'a'", 3, 2)]
    [InlineData("red,blue;red,blue", ";", "'red,blue'", 2, 1)]
    public void Transformation_and_notification_report_distinct_counts_for_each_supported_delimiter(
        string input,
        string? preferredDelimiter,
        string expectedOutput,
        int expectedIdentified,
        int expectedOutputCount)
    {
        var result = _transformation.Transform(input, preferredDelimiter);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedOutput, result.Value);
        Assert.Equal(expectedIdentified, result.InputItemCount);
        Assert.Equal(expectedOutputCount, result.OutputItemCount);

        var duplicatesRemoved = expectedIdentified - expectedOutputCount;
        var notification = ConversionCountSummary.Append(
            "Lista formatada para SQL IN e pronta para colar.",
            result.InputItemCount,
            result.OutputItemCount);
        Assert.Equal(
            $"Lista formatada para SQL IN e pronta para colar. Itens identificados: {expectedIdentified}. Removidos por duplicidade: {duplicatesRemoved}.",
            notification);
    }

    [Fact]
    public void Large_list_reports_all_input_items_and_duplicates_removed()
    {
        const int itemCount = 10_001;
        var input = string.Join(',', Enumerable.Repeat("duplicate", itemCount));

        var result = _transformation.Transform(input);

        Assert.True(result.Success, result.Error);
        Assert.Equal("'duplicate'", result.Value);
        Assert.Equal(itemCount, result.InputItemCount);
        Assert.Equal(1, result.OutputItemCount);
        Assert.Equal(
            "Conversão concluída. Itens identificados: 10001. Removidos por duplicidade: 10000.",
            ConversionCountSummary.Append("Conversão concluída.", result.InputItemCount, result.OutputItemCount));
    }
}
