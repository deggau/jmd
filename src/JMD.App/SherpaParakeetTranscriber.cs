using System.IO;
using System.Net.Http;
using JMD.Core;
using SherpaOnnx;

namespace JMD.App;

internal sealed class SherpaParakeetTranscriber : IDisposable
{
    private static string ModelDirectory(string modelId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "models", modelId);
    private readonly object _sync = new();
    private OfflineRecognizer? _recognizer;
    private string? _hotwordsPath;
    private string? _activeModelId;

    public static string ModelPath(string modelId, string fileName) => Path.Combine(ModelDirectory(modelId), fileName);
    public static bool IsModelDownloaded(string modelId) => SherpaModelDownloads.GetFiles(modelId).All(file =>
        File.Exists(ModelPath(modelId, file.FileName)) && new FileInfo(ModelPath(modelId, file.FileName)).Length > 0 &&
        (file.SizeBytes == 0 || new FileInfo(ModelPath(modelId, file.FileName)).Length == file.SizeBytes));
    public static bool HasAnyModelFiles(string modelId) => SherpaModelDownloads.GetFiles(modelId).Any(file => File.Exists(ModelPath(modelId, file.FileName)));
    public static string ModelDirectoryPath(string modelId) => ModelDirectory(modelId);

    public static async Task DownloadModelAsync(string modelId, IProgress<WhisperDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = SherpaModelDownloads.GetFiles(modelId);
        var totalBytes = files.Sum(file => file.SizeBytes);
        Directory.CreateDirectory(ModelDirectory(modelId));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        long completedBytes = 0;
        foreach (var file in files)
        {
            var destinationPath = ModelPath(modelId, file.FileName);
            if (File.Exists(destinationPath) && (file.SizeBytes == 0 || new FileInfo(destinationPath).Length == file.SizeBytes))
            {
                if (string.IsNullOrEmpty(file.Sha256)) { completedBytes += new FileInfo(destinationPath).Length; continue; }
                await using var existing = File.OpenRead(destinationPath);
                var existingHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(existing, cancellationToken)).ToLowerInvariant();
                if (existingHash == file.Sha256) { completedBytes += file.SizeBytes; continue; }
            }

            var stagedPath = destinationPath + ".download";
            try
            {
                using var response = await client.GetAsync(SherpaModelDownloads.GetDownloadUrl(modelId, file.FileName), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    long current = 0;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        current += read;
                        progress?.Report(new WhisperDownloadProgress(totalBytes == 0 ? null : Math.Min(0.99, (completedBytes + current) / (double)totalBytes * 0.99),
                            $"Baixando Sherpa pt-BR · {file.FileName}"));
                    }
                }

                if (new FileInfo(stagedPath).Length == 0 || (file.SizeBytes > 0 && new FileInfo(stagedPath).Length != file.SizeBytes))
                    throw new InvalidDataException($"O tamanho de {file.FileName} não confere com o modelo publicado.");
                progress?.Report(new WhisperDownloadProgress(null, $"Verificando {file.FileName}…"));
                if (string.IsNullOrEmpty(file.Sha256)) File.Move(stagedPath, destinationPath, overwrite: true);
                else await VerifiedFileInstaller.InstallSha256VerifiedFileAsync(stagedPath, destinationPath, file.Sha256, cancellationToken);
                completedBytes += file.SizeBytes > 0 ? file.SizeBytes : new FileInfo(destinationPath).Length;
            }
            finally
            {
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch (IOException) { }
            }
        }
    }

    public VoiceTranscriptionCandidate? Transcribe(byte[] pcmAudio, int length, string modelId, IReadOnlyCollection<string>? hotwords = null)
    {
        if (!IsModelDownloaded(modelId)) throw new FileNotFoundException("Baixe o modelo Sherpa nas configurações do Jey.", ModelDirectory(modelId));
        lock (_sync)
        {
            var normalizedHotwords = hotwords?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            var signature = string.Join('\n', normalizedHotwords);
            var currentHotwords = _hotwordsPath is null ? string.Empty : File.ReadAllText(_hotwordsPath);
            if (_recognizer is null || _activeModelId != modelId || !string.Equals(signature, currentHotwords, StringComparison.Ordinal))
                RecreateRecognizer(modelId, normalizedHotwords, signature);

            var samples = new float[length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(pcmAudio, i * 2) / 32768f;

            using var stream = _recognizer!.CreateStream();
            stream.AcceptWaveform(16000, samples);
            _recognizer.Decode(stream);
            var text = stream.Result.Text.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : new VoiceTranscriptionCandidate(text, null);
        }
    }

    private void RecreateRecognizer(string modelId, string[] hotwords, string signature)
    {
        _recognizer?.Dispose();
        if (hotwords.Length == 0)
        {
            if (_hotwordsPath is not null) { try { File.Delete(_hotwordsPath); } catch (IOException) { } }
            _hotwordsPath = null;
        }
        else
        {
            _hotwordsPath ??= Path.Combine(Path.GetTempPath(), $"JMD-sherpa-hotwords-{Guid.NewGuid():N}.txt");
            File.WriteAllText(_hotwordsPath, signature);
        }

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        var portugueseModel = modelId == VoiceModelOptions.SherpaParakeetPtBr;
        config.FeatConfig.FeatureDim = portugueseModel ? 128 : 80;
        config.ModelConfig.Tokens = ModelPath(modelId, "tokens.txt");
        config.ModelConfig.ModelType = portugueseModel ? "nemo_transducer" : string.Empty;
        config.ModelConfig.ModelingUnit = "bpe";
        config.ModelConfig.BpeVocab = hotwords.Length == 0 ? string.Empty : CreateBpeVocabulary(modelId);
        config.ModelConfig.NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        config.ModelConfig.Transducer.Encoder = ModelPath(modelId, portugueseModel ? "encoder.int8.onnx" : "encoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Decoder = ModelPath(modelId, portugueseModel ? "decoder.int8.onnx" : "decoder-epoch-99-avg-1.onnx");
        config.ModelConfig.Transducer.Joiner = ModelPath(modelId, portugueseModel ? "joiner.int8.onnx" : "joiner-epoch-99-avg-1.int8.onnx");
        config.HotwordsFile = _hotwordsPath ?? string.Empty;
        config.HotwordsScore = 1.5f;
        config.DecodingMethod = hotwords.Length == 0 ? "greedy_search" : "modified_beam_search";
        _recognizer = new OfflineRecognizer(config);
        _activeModelId = modelId;
    }

    private static string CreateBpeVocabulary(string modelId)
    {
        var path = ModelPath(modelId, "jmd-hotwords-bpe.vocab");
        if (File.Exists(path)) return path;
        using var writer = new StreamWriter(path);
        foreach (var line in File.ReadLines(ModelPath(modelId, "tokens.txt")))
        {
            var separator = line.LastIndexOf(' ');
            if (separator <= 0) continue;
            var token = line[..separator].Trim();
            if (token.Length == 0 || token.StartsWith('<')) continue;
            writer.WriteLine($"{token} -1.0");
        }
        return path;
    }

    public void Dispose()
    {
        lock (_sync) _recognizer?.Dispose();
        if (_hotwordsPath is not null) { try { File.Delete(_hotwordsPath); } catch (IOException) { } }
    }
}
