namespace JMD.Core;

public sealed record SherpaModelFile(string FileName, string Sha256, long SizeBytes);

public static class SherpaModelDownloads
{
    public const long EstimatedDownloadBytes = 670_576_792;
    public static IReadOnlyList<SherpaModelFile> Files { get; } =
    [
        new("encoder.int8.onnx", "260aff7bf778b016b42385e186d88a57224bd6958001993dbb6070a9a75b6690", 652_282_302),
        new("decoder.int8.onnx", "6b77e877b1dc29ebead07c0f461a7385d1ac5f8eff5be6422fdddb390d4a9967", 11_845_274),
        new("joiner.int8.onnx", "eb365a33945103cb787aa849b344cbf2abc093b43211e3b969ff97be3fdfc0e4", 6_355_277),
        new("tokens.txt", "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93_939)
    ];

    public static IReadOnlyList<SherpaModelFile> EnglishFiles { get; } =
    [
        new("encoder-epoch-99-avg-1.int8.onnx", "3a6ac78a31cc2c60ca8c1e2e2f43c878fbbcaf051ada4900e0a42ef8ba53d375", 0),
        new("decoder-epoch-99-avg-1.onnx", "1621a13eaa7f6fc52380f025ee2a6a05b56a87754ab5a0f4f580a3928f3467a2", 0),
        new("joiner-epoch-99-avg-1.int8.onnx", "6b183b6ec656e4d3ca6b86d0aeca992dac14df80aae6b416d7c068d0ff2bd4d7", 0),
        new("tokens.txt", "", 0)
    ];

    public const long EnglishEstimatedDownloadBytes = 30_500_000;

    public static IReadOnlyList<SherpaModelFile> GetFiles(string modelId) => modelId switch
    {
        VoiceModelOptions.SherpaParakeetPtBr => Files,
        VoiceModelOptions.SherpaZipformerEn => EnglishFiles,
        _ => throw new ArgumentOutOfRangeException(nameof(modelId))
    };

    public static string GetRepository(string modelId) => modelId switch
    {
        VoiceModelOptions.SherpaParakeetPtBr => "joaorura/parakeet-tdt-0.6b-v3-ptBR-sherpa-onnx-int8",
        VoiceModelOptions.SherpaZipformerEn => "csukuangfj/sherpa-onnx-zipformer-small-en-2023-06-26",
        _ => throw new ArgumentOutOfRangeException(nameof(modelId))
    };

    public static string GetDownloadUrl(string fileName)
    {
        return GetDownloadUrl(VoiceModelOptions.SherpaParakeetPtBr, fileName);
    }

    public static string GetDownloadUrl(string modelId, string fileName)
    {
        if (!GetFiles(modelId).Any(file => file.FileName == fileName)) throw new ArgumentOutOfRangeException(nameof(fileName));
        return $"https://huggingface.co/{GetRepository(modelId)}/resolve/main/{fileName}";
    }
}
