namespace JMD.Core;

public sealed record WhisperModelDownload(string FileName, string Url, string Sha256, string Label);

public static class WhisperModelDownloads
{
    public static WhisperModelDownload Get(string model) => model switch
    {
        "whisperTiny" => new(
            "ggml-tiny-q5_1.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/fb5ed9369c26eaf08392504d8446da431e0fffb9/ggml-tiny-q5_1.bin",
            "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7",
            "Whisper Tiny Q5 · 32 MiB"),
        "whisperBase" => new(
            "ggml-base.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            "Whisper Base · 142 MiB"),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Modelo Whisper inválido.")
    };
}

public static class VoiceModelDownloadFeedback
{
    public static string AfterStatusRefresh(string? errorMessage, string refreshedStatus)
        => errorMessage ?? refreshedStatus;
}
