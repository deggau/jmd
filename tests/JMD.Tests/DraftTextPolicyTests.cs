using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class DraftTextPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(512, false)]
    [InlineData(513, true)]
    [InlineData(2048, true)]
    public void Clipboard_is_used_only_when_text_exceeds_512_characters(int length, bool expected)
    {
        Assert.Equal(expected, DraftTextPolicy.ShouldUseClipboard(new string('x', length)));
    }
}
