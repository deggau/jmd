using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using JMD.Core;

namespace JMD.App;

internal sealed record WhisperDownloadProgress(double? Fraction, string Message);

internal sealed class WhisperCppTranscriber
{
    private static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "models", "whisper");

    public static string ModelPath(string model) => Path.Combine(ModelDirectory, model switch
    {
        "whisperTiny" => "ggml-tiny-q5_1.bin",
        "whisperBase" => "ggml-base.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Modelo Whisper inválido.")
    });

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "whisper", "whisper-cli.exe");

    public static bool IsModelDownloaded(string model) => File.Exists(ModelPath(model));

    public static async Task DownloadModelAsync(string model, IProgress<WhisperDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var download = WhisperModelDownloads.Get(model);
        var destinationPath = ModelPath(model);
        Directory.CreateDirectory(ModelDirectory);
        var temporaryPath = destinationPath + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    if (totalBytes is > 0)
                        progress?.Report(new WhisperDownloadProgress(Math.Min(0.99, (double)readTotal / totalBytes.Value * 0.99), $"Baixando {download.Label}"));
                }
            }

            progress?.Report(new WhisperDownloadProgress(null, "Verificando integridade do modelo…"));
            await VerifiedFileInstaller.InstallSha256VerifiedFileAsync(
                temporaryPath, destinationPath, download.Sha256, cancellationToken);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public VoiceTranscriptionCandidate? Transcribe(byte[] pcmAudio, int length, string language, string model)
    {
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("O mecanismo Whisper não está instalado nesta versão do Jey.", ExecutablePath);
        var modelPath = ModelPath(model);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Baixe o modelo Whisper nas configurações do Jey.", modelPath);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "JMD-Whisper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var inputPath = Path.Combine(tempDirectory, "audio.wav");
        var outputBasePath = Path.Combine(tempDirectory, "result");
        try
        {
            WriteWave(inputPath, pcmAudio, length);
            var start = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-m"); start.ArgumentList.Add(modelPath);
            start.ArgumentList.Add("-l"); start.ArgumentList.Add(language);
            start.ArgumentList.Add("-nt"); start.ArgumentList.Add("-np"); start.ArgumentList.Add("-ojf");
            start.ArgumentList.Add("-of"); start.ArgumentList.Add(outputBasePath);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(inputPath);
            using var activity = TranscriptionMemoryStatus.BeginExternalModel();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o Whisper.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var timer = Stopwatch.StartNew();
            while (!process.WaitForExit(200))
            {
                try { TranscriptionMemoryStatus.ReportExternalModelWorkingSet(process.WorkingSet64); }
                catch (InvalidOperationException) { }
                if (timer.Elapsed > TimeSpan.FromSeconds(45))
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException("A transcrição Whisper excedeu 45 segundos.");
                }
            }
            try { TranscriptionMemoryStatus.ReportExternalModelWorkingSet(process.WorkingSet64); }
            catch (InvalidOperationException) { }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Whisper terminou com erro: {stderr.Result}");
            var jsonPath = outputBasePath + ".json";
            if (!File.Exists(jsonPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!json.RootElement.TryGetProperty("transcription", out var transcription) || transcription.ValueKind != JsonValueKind.Array) return null;
            var segments = transcription.EnumerateArray().ToArray();
            var text = string.Join(" ", segments
                .Select(segment => segment.TryGetProperty("text", out var value) ? value.GetString()?.Trim() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var logProbabilities = segments
                .Where(segment => segment.TryGetProperty("avg_logprob", out _))
                .Select(segment => segment.GetProperty("avg_logprob").GetDouble())
                .Where(double.IsFinite)
                .ToArray();
            double? confidence = logProbabilities.Length == 0 ? null : logProbabilities.Average(Math.Exp);
            return new VoiceTranscriptionCandidate(text, confidence);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static void WriteWave(string path, byte[] pcm, int length)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(length + 36);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(length); writer.Write(pcm, 0, length);
    }
}
