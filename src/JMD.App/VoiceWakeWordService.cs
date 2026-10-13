using System.IO;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace JMD.App;

internal sealed class VoiceWakeWordService : IDisposable
{
    private readonly Model _model;
    private readonly VoskRecognizer _recognizer;
    private readonly Model _portugueseModel;
    private readonly WaveInEvent _microphone;
    private readonly MemoryStream _utteranceAudio = new();
    private readonly MemoryStream _followupAudio = new();
    private readonly MemoryStream _followupSegmentAudio = new();
    private byte[]? _wakeWordAudioPrefix;
    private DateTimeOffset _followupDeadline;
    private bool _awaitingPostWakeSpeech;
    private bool _started;

    public event Action<string?, byte[]>? WakeWordRecognized;
    public event Action<string?, byte[]>? FollowupSpeechCaptured;

    public VoiceWakeWordService()
    {
        var wakeWordModelPath = Path.Combine(AppContext.BaseDirectory, "vosk-model-small-en-us-0.15");
        var portugueseModelPath = Path.Combine(AppContext.BaseDirectory, "vosk-model-small-pt-0.3");
        if (!Directory.Exists(wakeWordModelPath))
            throw new DirectoryNotFoundException("O modelo local de reconhecimento de voz não foi encontrado.");
        if (!Directory.Exists(portugueseModelPath))
            throw new DirectoryNotFoundException("O modelo local de português não foi encontrado.");

        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(wakeWordModelPath);
        _recognizer = new VoskRecognizer(_model, 16000.0f);
        _recognizer.SetWords(true);
        _portugueseModel = new Model(portugueseModelPath);
        _microphone = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _microphone.DataAvailable += OnDataAvailable;
    }

    public void Start()
    {
        _microphone.StartRecording();
        _started = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (_awaitingPostWakeSpeech)
        {
            CapturePostWakeSpeech(eventArgs);
            return;
        }

        _utteranceAudio.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        var wakeWordFinalized = _recognizer.AcceptWaveform(eventArgs.Buffer, eventArgs.BytesRecorded);
        if (!wakeWordFinalized) return;

        try
        {
            var wakeWordResult = _recognizer.Result();
            if (!IsWakeWordResult(wakeWordResult)) return;
            string? transcript;
            try
            {
                var portugueseResult = TranscribePortugueseUtterance();
                transcript = GetTranscriptAfterWakeWord(wakeWordResult, portugueseResult);
            }
            catch
            {
                transcript = null;
            }
            var pcmAudio = _utteranceAudio.ToArray();
            if (string.IsNullOrWhiteSpace(transcript)) BeginPostWakeCapture(pcmAudio);
            WakeWordRecognized?.Invoke(transcript, CreateWaveAudio(pcmAudio));
        }
        finally
        {
            _utteranceAudio.SetLength(0);
        }
    }

    private string TranscribePortugueseUtterance()
        => TranscribePortugueseAudio(_utteranceAudio.GetBuffer(), checked((int)_utteranceAudio.Length));

    private string TranscribePortugueseAudio(byte[] audio, int length)
    {
        using var recognizer = new VoskRecognizer(_portugueseModel, 16000.0f);
        recognizer.SetWords(true);
        var isFinalized = recognizer.AcceptWaveform(audio, length);
        return isFinalized ? recognizer.Result() : recognizer.FinalResult();
    }

    private void BeginPostWakeCapture(byte[] wakeWordPcm)
    {
        _wakeWordAudioPrefix = wakeWordPcm;
        _followupAudio.SetLength(0);
        _followupSegmentAudio.SetLength(0);
        _followupDeadline = DateTimeOffset.UtcNow.AddSeconds(6);
        _awaitingPostWakeSpeech = true;
    }

    private void CapturePostWakeSpeech(WaveInEventArgs eventArgs)
    {
        _followupAudio.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        _followupSegmentAudio.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        var finalized = _recognizer.AcceptWaveform(eventArgs.Buffer, eventArgs.BytesRecorded);
        if (finalized)
        {
            var wakeRecognizerResult = _recognizer.Result();
            var portugueseResult = TranscribePortugueseAudio(
                _followupSegmentAudio.GetBuffer(), checked((int)_followupSegmentAudio.Length));
            var transcript = GetPortugueseText(portugueseResult);
            if (HasRecognizedText(wakeRecognizerResult) || transcript is not null)
            {
                FollowupSpeechCaptured?.Invoke(transcript, CreateCombinedWaveAudio());
                EndPostWakeCapture();
                return;
            }
            _followupAudio.SetLength(0);
            _followupSegmentAudio.SetLength(0);
        }

        if (DateTimeOffset.UtcNow < _followupDeadline) return;
        var partialResult = _recognizer.PartialResult();
        if (HasRecognizedPartialText(partialResult) && _followupSegmentAudio.Length > 0)
        {
            var portugueseResult = TranscribePortugueseAudio(
                _followupSegmentAudio.GetBuffer(), checked((int)_followupSegmentAudio.Length));
            FollowupSpeechCaptured?.Invoke(GetPortugueseText(portugueseResult), CreateCombinedWaveAudio());
        }
        EndPostWakeCapture();
    }

    private byte[] CreateCombinedWaveAudio()
    {
        var prefix = _wakeWordAudioPrefix ?? [];
        var followupLength = checked((int)_followupAudio.Length);
        var audio = new byte[checked(prefix.Length + followupLength)];
        Buffer.BlockCopy(prefix, 0, audio, 0, prefix.Length);
        Buffer.BlockCopy(_followupAudio.GetBuffer(), 0, audio, prefix.Length, checked((int)_followupAudio.Length));
        return CreateWaveAudio(audio);
    }

    private void EndPostWakeCapture()
    {
        _awaitingPostWakeSpeech = false;
        _wakeWordAudioPrefix = null;
        _followupAudio.SetLength(0);
        _followupSegmentAudio.SetLength(0);
    }

    private byte[] CreateWaveAudio(byte[] pcmAudio)
    {
        var sampleBytes = pcmAudio.Length;
        using var wave = new MemoryStream(44 + sampleBytes);
        using (var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(sampleBytes + 36);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16000);
            writer.Write(32000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(sampleBytes);
            writer.Write(pcmAudio, 0, sampleBytes);
        }
        return wave.ToArray();
    }

    private static string? GetPortugueseText(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        if (!result.RootElement.TryGetProperty("text", out var text)) return null;
        var transcript = text.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(transcript) ? null : transcript;
    }

    private static bool HasRecognizedText(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        return result.RootElement.TryGetProperty("text", out var text) &&
            !string.IsNullOrWhiteSpace(text.GetString());
    }

    private static bool HasRecognizedPartialText(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        return result.RootElement.TryGetProperty("partial", out var text) &&
            !string.IsNullOrWhiteSpace(text.GetString());
    }

    private static string? GetTranscriptAfterWakeWord(string wakeWordJson, string portugueseJson)
    {
        using var wakeWord = JsonDocument.Parse(wakeWordJson);
        if (!TryGetWakeWordEnd(wakeWord.RootElement, out var wakeWordEnd)) return null;
        using var portuguese = JsonDocument.Parse(portugueseJson);
        if (!portuguese.RootElement.TryGetProperty("result", out var recognizedWords) ||
            recognizedWords.ValueKind != JsonValueKind.Array)
            return null;

        var transcriptWords = new List<string>();
        foreach (var word in recognizedWords.EnumerateArray())
        {
            if (!word.TryGetProperty("start", out var startElement) || !word.TryGetProperty("end", out var endElement) ||
                !word.TryGetProperty("word", out var textElement))
                continue;
            var start = startElement.GetDouble();
            var end = endElement.GetDouble();
            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text) || end < wakeWordEnd - 0.35) continue;

            if (IsActivationWord(text) && start < wakeWordEnd + 0.05) continue;
            transcriptWords.Add(text);
        }
        var transcript = string.Join(' ', transcriptWords);
        if (!string.IsNullOrWhiteSpace(transcript)) return transcript;

        if (!portuguese.RootElement.TryGetProperty("text", out var fullText)) return null;
        var fallbackWords = fullText.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (fallbackWords.Length > 0 && IsActivationWord(fallbackWords[0]))
            fallbackWords = fallbackWords.Skip(1).ToArray();
        var fallbackTranscript = string.Join(' ', fallbackWords);
        return string.IsNullOrWhiteSpace(fallbackTranscript) ? null : fallbackTranscript;
    }

    private static bool IsActivationWord(string word)
        => word.Equals("jay", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("jey", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("jei", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("já", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("ja", StringComparison.OrdinalIgnoreCase);

    private static bool IsWakeWordResult(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        return result.RootElement.TryGetProperty("text", out var text) &&
            string.Equals(text.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                "jay", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetWakeWordEnd(JsonElement result, out double end)
    {
        end = 0;
        if (!result.TryGetProperty("result", out var words) ||
            words.ValueKind != JsonValueKind.Array || words.GetArrayLength() == 0)
            return false;
        var firstWord = words[0];
        return firstWord.TryGetProperty("word", out var word) &&
            string.Equals(word.GetString(), "jay", StringComparison.OrdinalIgnoreCase) &&
            firstWord.TryGetProperty("end", out var endElement) &&
            (end = endElement.GetDouble()) >= 0;
    }

    public void Dispose()
    {
        _microphone.DataAvailable -= OnDataAvailable;
        if (_started)
        {
            try { _microphone.StopRecording(); }
            catch (InvalidOperationException) { }
        }
        _microphone.Dispose();
        _recognizer.Dispose();
        _portugueseModel.Dispose();
        _model.Dispose();
        _utteranceAudio.Dispose();
        _followupAudio.Dispose();
        _followupSegmentAudio.Dispose();
    }
}
