using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class ConversionCountSummaryTests
{
    [Fact]
    public void Summary_reports_identified_items_and_removed_duplicates()
    {
        var summary = ConversionCountSummary.Append("Conversão concluída.", 8, 5);

        Assert.Equal("Conversão concluída. Itens identificados: 8. Removidos por duplicidade: 3.", summary);
    }

    [Fact]
    public void Summary_reports_zero_duplicates_when_all_items_are_unique()
    {
        Assert.Equal(
            "Conversão concluída. Itens identificados: 4. Removidos por duplicidade: 0.",
            ConversionCountSummary.Append("Conversão concluída.", 4, 4));
    }

    [Fact]
    public void Summary_leaves_message_unchanged_when_counts_are_not_available()
    {
        Assert.Equal("Conversão concluída.", ConversionCountSummary.Append("Conversão concluída.", null, null));
    }
}
