using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using JMD.Core;

namespace JMD.App;

internal sealed class Gemma4Transcriber
{
    private static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "models", "gemma");

    public static string ModelPath => Path.Combine(ModelDirectory, "gemma-4-E2B-it-Q4_0.gguf");
    public static string AudioProjectorPath => Path.Combine(ModelDirectory, "mmproj-gemma-4-E2B-it-Q8_0.gguf");
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "llama", "llama-mtmd-cli.exe");
    public static bool IsModelDownloaded => File.Exists(ModelPath) && File.Exists(AudioProjectorPath);
    public static long EstimatedDownloadBytes => 2_840_000_000L + 557_000_000L;

    public static async Task DownloadModelAsync(
        IProgress<WhisperDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelDirectory);
        var files = new[]
        {
            (Path: ModelPath,
                Url: "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q4_0.gguf",
                Sha256: "8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52",
                Label: "Gemma E2B · modelo · 2,84 GiB"),
            (Path: AudioProjectorPath,
                Url: "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/mmproj-gemma-4-E2B-it-Q8_0.gguf",
                Sha256: "9406f99c16d68cda4f1f0552192dcc99021ea1fc6d2fd50b1dc3ccf30d04b292",
                Label: "Gemma E2B · codificador de áudio · 557 MiB")
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        foreach (var file in files)
        {
            var temporaryPath = file.Path + ".download";
            try
            {
                progress?.Report(new WhisperDownloadProgress(null, $"Conectando para baixar {file.Label}…"));
                using var response = await client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                            progress?.Report(new WhisperDownloadProgress(
                                Math.Min(0.99, (double)readTotal / totalBytes.Value * 0.99), $"Baixando {file.Label}"));
                    }
                }

                progress?.Report(new WhisperDownloadProgress(null, $"Verificando {file.Label}…"));
                await VerifiedFileInstaller.InstallSha256VerifiedFileAsync(temporaryPath, file.Path, file.Sha256, cancellationToken);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public VoiceTranscriptionCandidate? Transcribe(byte[] pcmAudio, int length, string language)
    {
        if (!File.Exists(ExecutablePath))
            throw new FileNotFoundException("O runtime Gemma não está incluído nesta instalação do JMD.", ExecutablePath);
        if (!IsModelDownloaded)
            throw new FileNotFoundException("Baixe o Gemma E2B e seu codificador de áudio nas configurações do Jey.", ModelPath);
        if (length > VoiceModelOptions.GemmaMaximumPcmBytes)
            throw new InvalidDataException("O Gemma E2B aceita trechos de áudio de até 30 segundos.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "JMD-Gemma", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var inputPath = Path.Combine(tempDirectory, "audio.wav");
        try
        {
            WriteWave(inputPath, pcmAudio, length);
            return Run(inputPath, language);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public VoiceTranscriptionCandidate? TranscribeFile(string wavPath, string language)
    {
        if (!File.Exists(ExecutablePath))
            throw new FileNotFoundException("O runtime Gemma não está incluído nesta instalação do JMD.", ExecutablePath);
        if (!IsModelDownloaded)
            throw new FileNotFoundException("Baixe o Gemma E2B e seu codificador de áudio nas configurações do Jey.", ModelPath);
        return Run(wavPath, language);
    }

    private static VoiceTranscriptionCandidate? Run(string audioPath, string language)
    {
        var languageName = language == "pt" ? "Portuguese" : "English";
        var prompt = $"Transcribe the following speech segment in {languageName} into {languageName} text. Only output the transcription. Do not add explanations or newlines.";
        var start = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-m"); start.ArgumentList.Add(ModelPath);
        start.ArgumentList.Add("--mmproj"); start.ArgumentList.Add(AudioProjectorPath);
        start.ArgumentList.Add("--audio"); start.ArgumentList.Add(audioPath);
        start.ArgumentList.Add("-p"); start.ArgumentList.Add(prompt);
        start.ArgumentList.Add("--jinja");
        start.ArgumentList.Add("--no-warmup");
        start.ArgumentList.Add("--temp"); start.ArgumentList.Add("0");
        start.ArgumentList.Add("-n"); start.ArgumentList.Add("160");

        using var activity = TranscriptionMemoryStatus.BeginExternalModel();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o runtime Gemma.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timer = Stopwatch.StartNew();
        long peakWorkingSet = 0;
        while (!process.WaitForExit(200))
        {
            try
            {
                var workingSet = process.WorkingSet64;
                peakWorkingSet = Math.Max(peakWorkingSet, workingSet);
                TranscriptionMemoryStatus.ReportExternalModelWorkingSet(workingSet);
            }
            catch (InvalidOperationException) { }
            if (timer.Elapsed > TimeSpan.FromSeconds(120))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new TimeoutException("A transcrição Gemma excedeu 120 segundos.");
            }
        }
        try
        {
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            TranscriptionMemoryStatus.ReportExternalModelWorkingSet(peakWorkingSet);
        }
        catch (InvalidOperationException) { }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Gemma terminou com erro: {stderr.GetAwaiter().GetResult().Trim()}");

        var result = stdout.GetAwaiter().GetResult().Trim();
        result = result.Replace("\u001b[0m", string.Empty, StringComparison.Ordinal)
            .Replace("\u001b[1m", string.Empty, StringComparison.Ordinal)
            .Replace("\u001b[0;32m", string.Empty, StringComparison.Ordinal);
        foreach (var marker in new[] { "<|channel>analysis", "<|channel>thought", "<|channel>final", "<|end_of_turn>", "<|im_end|>" })
            result = result.Replace(marker, string.Empty, StringComparison.OrdinalIgnoreCase);
        result = string.Join(' ', result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(result) ? null : new VoiceTranscriptionCandidate(result, null);
    }

    private static void WriteWave(string path, byte[] pcmAudio, int length)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(length + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(length);
        writer.Write(pcmAudio, 0, length);
    }
}
