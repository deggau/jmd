using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class VoiceAudioSegmenterTests
{
    [Fact]
    public void To_segment_offset_subtracts_the_start_of_the_current_recognizer_segment()
    {
        Assert.Equal(0.72, VoiceAudioSegmenter.ToSegmentOffset(101.82, 101.10), precision: 6);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(1, 2)]
    public void To_segment_offset_returns_nan_for_invalid_timestamps(double absoluteBoundarySeconds, double segmentStartSeconds)
    {
        Assert.True(double.IsNaN(VoiceAudioSegmenter.ToSegmentOffset(absoluteBoundarySeconds, segmentStartSeconds)));
    }

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
