using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class VoiceAudioSegmenterTests
{
    [Fact]
    public void After_boundary_starts_on_the_corresponding_pcm_sample()
    {
        var pcmAudio = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();

        var speech = VoiceAudioSegmenter.AfterBoundary(pcmAudio, 0.001);

        Assert.Equal(pcmAudio[32..], speech);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(1)]
    public void After_boundary_returns_empty_for_invalid_or_end_of_audio_offsets(double offset)
    {
        var pcmAudio = new byte[320];

        Assert.Empty(VoiceAudioSegmenter.AfterBoundary(pcmAudio, offset));
    }
}
