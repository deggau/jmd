namespace JMD.Core;

public static class VoiceModelOptions
{
    public const string Disabled = "disabled";
    public const string Vosk = "vosk";
    public const string WhisperTiny = "whisperTiny";
    public const string WhisperBase = "whisperBase";
    public const string Gemma4E2B = "gemma4E2B";
    public const string SherpaParakeetPtBr = "sherpaParakeetPtBr";
    public const string SherpaZipformerEn = "sherpaZipformerEn";
    public static readonly TimeSpan GemmaMaximumAudioDuration = TimeSpan.FromSeconds(30);
    public const int GemmaPcmBytesPerSecond = 32000;
    public const int GemmaMaximumPcmBytes = GemmaPcmBytesPerSecond * 30;

    public static bool IsSupported(string? modelId)
        => modelId is Disabled or Vosk or WhisperTiny or WhisperBase or Gemma4E2B or SherpaParakeetPtBr or SherpaZipformerEn;

    public static string NormalizePersisted(string? modelId)
    {
        if (modelId == "whisper") return WhisperBase;
        return IsSupported(modelId) ? modelId! : Vosk;
    }
}

public enum VoiceBenchmarkSpeedRating
{
    Good,
    Satisfactory,
    SlowNotRecommended
}

public static class VoiceBenchmarkSpeed
{
    public static int GetBarLength(long elapsedMilliseconds, long slowestMilliseconds, int maximumLength = 12)
    {
        if (maximumLength <= 0) return 0;
        var maximum = Math.Max(1, slowestMilliseconds);
        return Math.Clamp((int)Math.Ceiling(Math.Max(0, elapsedMilliseconds) / (double)maximum * maximumLength), 1, maximumLength);
    }

    public static VoiceBenchmarkSpeedRating Rate(long elapsedMilliseconds) => elapsedMilliseconds switch
    {
        <= 1500 => VoiceBenchmarkSpeedRating.Good,
        <= 4000 => VoiceBenchmarkSpeedRating.Satisfactory,
        _ => VoiceBenchmarkSpeedRating.SlowNotRecommended
    };

    public static string GetPortugueseLabel(long elapsedMilliseconds) => Rate(elapsedMilliseconds) switch
    {
        VoiceBenchmarkSpeedRating.Good => "Bom",
        VoiceBenchmarkSpeedRating.Satisfactory => "Satisfatório",
        _ => "Lento (não indicado)"
    };
}

public static class VoiceTranscriptionParallelism
{
    public static bool CanRunConcurrently(string portugueseModelId, string englishModelId)
        => portugueseModelId is not (VoiceModelOptions.Gemma4E2B or VoiceModelOptions.WhisperTiny or VoiceModelOptions.WhisperBase) &&
           englishModelId is not (VoiceModelOptions.Gemma4E2B or VoiceModelOptions.WhisperTiny or VoiceModelOptions.WhisperBase);
}
