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
    [InlineData("sherpaParakeetPtBr")]
    [InlineData("sherpaZipformerEn")]
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

    [Theory]
    [InlineData(0, 9000, 1)]
    [InlineData(4500, 9000, 6)]
    [InlineData(9000, 9000, 12)]
    public void Benchmark_bar_length_scales_with_elapsed_time(long elapsed, long slowest, int expected)
        => Assert.Equal(expected, VoiceBenchmarkSpeed.GetBarLength(elapsed, slowest));

    [Theory]
    [InlineData("vosk", "vosk", true)]
    [InlineData("sherpaParakeetPtBr", "sherpaZipformerEn", true)]
    [InlineData("whisperTiny", "vosk", false)]
    [InlineData("gemma4E2B", "vosk", false)]
    public void Parallel_dual_language_transcription_skips_heavy_process_models(string pt, string en, bool expected)
        => Assert.Equal(expected, VoiceTranscriptionParallelism.CanRunConcurrently(pt, en));

    [Fact]
    public void Sherpa_model_download_metadata_pins_portuguese_files_and_models_are_language_specific()
    {
        Assert.Equal(4, SherpaModelDownloads.Files.Count);
        Assert.All(SherpaModelDownloads.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.All(SherpaModelDownloads.EnglishFiles.Where(file => file.FileName != "tokens.txt"),
            file => Assert.Equal(64, file.Sha256.Length));
        Assert.Contains("ptBR", SherpaModelDownloads.GetRepository(VoiceModelOptions.SherpaParakeetPtBr));
        Assert.Contains("small-en", SherpaModelDownloads.GetRepository(VoiceModelOptions.SherpaZipformerEn));
        Assert.Equal(VoiceModelOptions.SherpaParakeetPtBr, VoiceModelOptions.NormalizePersisted(VoiceModelOptions.SherpaParakeetPtBr));
    }
}
