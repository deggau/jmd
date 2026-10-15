namespace JMD.Core;

public static class VoiceModelOptions
{
    public const string Vosk = "vosk";
    public const string WhisperTiny = "whisperTiny";
    public const string WhisperBase = "whisperBase";
    public const string Gemma4E2B = "gemma4E2B";
    public static readonly TimeSpan GemmaMaximumAudioDuration = TimeSpan.FromSeconds(30);
    public const int GemmaPcmBytesPerSecond = 32000;
    public const int GemmaMaximumPcmBytes = GemmaPcmBytesPerSecond * 30;

    public static bool IsSupported(string? modelId)
        => modelId is Vosk or WhisperTiny or WhisperBase or Gemma4E2B;

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
