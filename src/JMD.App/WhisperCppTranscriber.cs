using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace JMD.App;

internal sealed class WhisperCppTranscriber
{
    public static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "models", "whisper", "ggml-base.bin");

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "whisper", "whisper-cli.exe");

    public static bool IsModelDownloaded => File.Exists(ModelPath);

    public static async Task DownloadBaseModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        const string url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
        const string expectedSha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var temporaryPath = ModelPath + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                    if (totalBytes is > 0) progress?.Report((double)readTotal / totalBytes.Value);
                }
            }

            await using var model = File.OpenRead(temporaryPath);
            var actualSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(model, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("O checksum do modelo Whisper não confere.");
            File.Move(temporaryPath, ModelPath, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public VoiceTranscriptionCandidate? Transcribe(byte[] pcmAudio, int length, string language)
    {
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("O mecanismo Whisper não está instalado nesta versão do Jey.", ExecutablePath);
        if (!IsModelDownloaded) throw new FileNotFoundException("Baixe o modelo Whisper nas configurações do Jey.", ModelPath);
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
            start.ArgumentList.Add("-m"); start.ArgumentList.Add(ModelPath);
            start.ArgumentList.Add("-l"); start.ArgumentList.Add(language);
            start.ArgumentList.Add("-nt"); start.ArgumentList.Add("-np"); start.ArgumentList.Add("-ojf");
            start.ArgumentList.Add("-of"); start.ArgumentList.Add(outputBasePath);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(inputPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o Whisper.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("A transcrição Whisper excedeu 45 segundos.");
            }
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
