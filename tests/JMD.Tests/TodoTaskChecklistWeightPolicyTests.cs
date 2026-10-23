using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class TodoTaskChecklistWeightPolicyTests
{
    [Fact]
    public void Editing_an_item_preserves_earlier_weights_and_redistributes_only_later_items()
    {
        var result = TodoTaskChecklistWeightPolicy.RebalanceAfterEdit([25m, 25m, 25m, 25m], 1, 40m);

        Assert.Equal([25m, 40m, 17.5m, 17.5m], result);
        Assert.Equal(100m, result.Sum());
    }

    [Fact]
    public void Editing_the_last_item_redistributes_the_previous_items_to_fit()
    {
        var result = TodoTaskChecklistWeightPolicy.RebalanceAfterEdit([25m, 25m, 25m, 25m], 3, 20m);

        Assert.Equal([26.66m, 26.66m, 26.68m, 20m], result);
        Assert.Equal(100m, result.Sum());
    }

    [Fact]
    public void A_single_item_always_represents_the_whole_task()
    {
        var result = TodoTaskChecklistWeightPolicy.RebalanceAfterEdit([100m], 0, 25m);

        Assert.Equal([100m], result);
    }

    [Fact]
    public void An_edited_weight_is_limited_by_the_fixed_weights_before_it()
    {
        var result = TodoTaskChecklistWeightPolicy.RebalanceAfterEdit([30m, 30m, 40m], 1, 80m);

        Assert.Equal([30m, 70m, 0m], result);
        Assert.Equal(100m, result.Sum());
    }
}
