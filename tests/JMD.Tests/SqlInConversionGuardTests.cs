using JMD.Core;
using JMD.Tools;
using Xunit;

namespace JMD.Tests;

public sealed class SqlInConversionGuardTests
{
    [Fact]
    public void Guard_skips_the_previous_output_but_allows_the_original_input_again()
    {
        var guard = new SqlInConversionGuard();
        const string original = "alpha,beta";
        const string converted = "'alpha','beta'";
        guard.Remember(original, converted);

        Assert.False(guard.TryGetOriginalForConvertedValue(original, out _));
        Assert.True(guard.TryGetOriginalForConvertedValue(converted, out var source));
        Assert.Equal(original, source);
    }

    [Fact]
    public void Guarded_transform_does_not_reformat_the_previous_output()
    {
        var guard = new SqlInConversionGuard();
        var transformation = new SqlInListTransformation();
        const string original = "alpha,beta";
        var converted = transformation.Transform(original);
        Assert.True(converted.Success, converted.Error);
        guard.Remember(original, converted.Value!);

        var duplicate = guard.TransformUnlessAlreadyConverted(converted.Value!, transformation.Transform);

        Assert.False(duplicate.Success);
        Assert.Contains(original, duplicate.Error);
        Assert.Equal(converted.Value, guard.TransformUnlessAlreadyConverted(original, transformation.Transform).Value);
    }
}
