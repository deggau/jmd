using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class VoiceModelOptionsTests
{
    [Theory]
    [InlineData("vosk")]
    [InlineData("whisperTiny")]
    [InlineData("whisperBase")]
    [InlineData("gemma4E2B")]
    public void All_transcription_models_are_recognized(string modelId)
    {
        Assert.True(VoiceModelOptions.IsSupported(modelId));
        Assert.Equal(modelId, VoiceModelOptions.NormalizePersisted(modelId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-model")]
    public void Unknown_or_missing_model_settings_fall_back_to_vosk(string? modelId)
        => Assert.Equal(VoiceModelOptions.Vosk, VoiceModelOptions.NormalizePersisted(modelId));

    [Fact]
    public void Legacy_whisper_setting_migrates_to_whisper_base()
        => Assert.Equal(VoiceModelOptions.WhisperBase, VoiceModelOptions.NormalizePersisted("whisper"));

    [Fact]
    public void Gemma_audio_limit_is_thirty_seconds_of_16khz_mono_pcm16()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), VoiceModelOptions.GemmaMaximumAudioDuration);
        Assert.Equal(960_000, VoiceModelOptions.GemmaMaximumPcmBytes);
    }

    [Theory]
    [InlineData(0, VoiceBenchmarkSpeedRating.Good, "Bom")]
    [InlineData(1500, VoiceBenchmarkSpeedRating.Good, "Bom")]
    [InlineData(1501, VoiceBenchmarkSpeedRating.Satisfactory, "Satisfatório")]
    [InlineData(4000, VoiceBenchmarkSpeedRating.Satisfactory, "Satisfatório")]
    [InlineData(4001, VoiceBenchmarkSpeedRating.SlowNotRecommended, "Lento (não indicado)")]
    [InlineData(30000, VoiceBenchmarkSpeedRating.SlowNotRecommended, "Lento (não indicado)")]
    public void Benchmark_speed_rating_obeys_the_documented_boundaries(
        long elapsedMilliseconds,
        VoiceBenchmarkSpeedRating expectedRating,
        string expectedLabel)
    {
        Assert.Equal(expectedRating, VoiceBenchmarkSpeed.Rate(elapsedMilliseconds));
        Assert.Equal(expectedLabel, VoiceBenchmarkSpeed.GetPortugueseLabel(elapsedMilliseconds));
    }
}
