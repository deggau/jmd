using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JMD.Core;
using NAudio.Wave;
using Vosk;

namespace JMD.App;

internal sealed record VoiceModelBenchmarkResult(string ModelId, string Name, long ElapsedMilliseconds, string Transcript);

internal sealed class VoiceModelBenchmark
{
    public async Task<string> RunAsync(
        string audioPath,
        string language,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pcm = await Task.Run(() => ReadPcm16KhzMono(audioPath), cancellationToken);
        var models = new List<string> { "vosk" };
        if (WhisperCppTranscriber.IsModelDownloaded("whisperTiny")) models.Add("whisperTiny");
        if (WhisperCppTranscriber.IsModelDownloaded("whisperBase")) models.Add("whisperBase");
        if (Gemma4Transcriber.IsModelDownloaded && File.Exists(Gemma4Transcriber.ExecutablePath)) models.Add("gemma4E2B");

        var results = new List<VoiceModelBenchmarkResult>();
        foreach (var modelId in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = GetModelName(modelId);
            progress?.Report($"Testando {name}…");
            try
            {
                var result = await Task.Run(() => BenchmarkOne(pcm, audioPath, language, modelId), cancellationToken);
                results.Add(result with { Name = name });
                progress?.Report(FormatResult(result with { Name = name }));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report($"{name}: falhou · {exception.Message}");
            }
        }

        if (results.Count == 0)
            return "Nenhum modelo concluiu o benchmark.";

        var fastest = results.MinBy(result => result.ElapsedMilliseconds)!;
        var recommendation = GetSpeedRecommendation(fastest.ElapsedMilliseconds);
        var output = new StringBuilder();
        output.AppendLine($"Áudio: {Path.GetFileName(audioPath)} · idioma: {(language == "pt" ? "português" : "inglês")}");
        output.AppendLine("Tempo inclui a inicialização do processo de transcrição quando aplicável.");
        foreach (var result in results.OrderBy(result => result.ElapsedMilliseconds))
        {
            output.AppendLine(FormatResult(result));
            if (!string.IsNullOrWhiteSpace(result.Transcript))
                output.AppendLine($"  «{TrimTranscript(result.Transcript)}»");
        }
        output.Append($"Mais rápido: {fastest.Name} · Recomendação de velocidade: {recommendation}.");
        return output.ToString();
    }

    private static VoiceModelBenchmarkResult BenchmarkOne(byte[] pcm, string originalPath, string language, string modelId)
    {
        VoiceTranscriptionCandidate? candidate;
        if (modelId == "vosk")
        {
            // Vosk models are already resident in the normal Jey flow; exclude model loading from response time.
            using var model = new Model(Path.Combine(AppContext.BaseDirectory,
                language == "pt" ? "vosk-model-small-pt-0.3" : "vosk-model-small-en-us-0.15"));
            var timer = Stopwatch.StartNew();
            using var recognizer = new VoskRecognizer(model, 16000.0f);
            recognizer.SetWords(true);
            recognizer.AcceptWaveform(pcm, pcm.Length);
            candidate = ParseVoskResult(recognizer.FinalResult());
            timer.Stop();
            return new VoiceModelBenchmarkResult(modelId, GetModelName(modelId), timer.ElapsedMilliseconds, candidate?.Text ?? "(sem transcrição)");
        }

        var modelTimer = Stopwatch.StartNew();
        if (modelId is "whisperTiny" or "whisperBase")
        {
            candidate = new WhisperCppTranscriber().Transcribe(pcm, pcm.Length, language, modelId);
        }
        else
        {
            var wavPath = WriteTemporaryWave(pcm);
            try { candidate = new Gemma4Transcriber().TranscribeFile(wavPath, language); }
            finally
            {
                try { File.Delete(wavPath); } catch (IOException) { }
                try { Directory.Delete(Path.GetDirectoryName(wavPath)!, true); } catch (IOException) { }
            }
        }
        modelTimer.Stop();
        return new VoiceModelBenchmarkResult(modelId, GetModelName(modelId), modelTimer.ElapsedMilliseconds, candidate?.Text ?? "(sem transcrição)");
    }

    private static VoiceTranscriptionCandidate? ParseVoskResult(string jsonText)
    {
        using var document = JsonDocument.Parse(jsonText);
        if (!document.RootElement.TryGetProperty("text", out var textValue)) return null;
        var text = textValue.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new VoiceTranscriptionCandidate(text, null);
    }

    private static byte[] ReadPcm16KhzMono(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime > TimeSpan.FromSeconds(30))
            throw new InvalidDataException("Escolha um áudio de até 30 segundos para comparar todos os modelos.");

        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var pcm = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = resampler.Read(buffer, 0, buffer.Length)) > 0)
            pcm.Write(buffer, 0, count);
        if (pcm.Length == 0) throw new InvalidDataException("O arquivo não contém áudio decodificável.");
        return pcm.ToArray();
    }

    private static string WriteTemporaryWave(byte[] pcm)
    {
        var directory = Path.Combine(Path.GetTempPath(), "JMD-Benchmark", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "benchmark.wav");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(pcm.Length + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return path;
    }

    private static string GetModelName(string modelId) => modelId switch
    {
        "vosk" => "Vosk",
        "whisperTiny" => "Whisper Tiny",
        "whisperBase" => "Whisper Base",
        "gemma4E2B" => "Gemma E2B",
        _ => modelId
    };

    private static string FormatResult(VoiceModelBenchmarkResult result)
    {
        var recommendation = GetSpeedRecommendation(result.ElapsedMilliseconds);
        return $"{result.Name}: {result.ElapsedMilliseconds.ToString("N0", CultureInfo.CurrentCulture)} ms · {recommendation}";
    }

    private static string GetSpeedRecommendation(long milliseconds) => VoiceBenchmarkSpeed.GetPortugueseLabel(milliseconds);

    private static string TrimTranscript(string transcript)
        => transcript.Length <= 140 ? transcript : transcript[..137] + "…";
}
