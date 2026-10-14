using System.IO;
using System.Text;
using System.Text.Json;
using JMD.Core;
using NAudio.Wave;
using Vosk;

namespace JMD.App;

internal sealed record VoiceCaptureDebugInfo(int DurationMilliseconds, int? CutMilliseconds, byte[]? TranscriptionAudioWav);

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

    private volatile bool _debugEnabled;
    private VoiceCaptureDebugInfo? _lastDebugInfo;

    public bool DebugEnabled { get => _debugEnabled; set => _debugEnabled = value; }
    public event Action<string?, byte[], VoiceCaptureDebugInfo?>? WakeWordRecognized;
    public event Action<string?, byte[], VoiceCaptureDebugInfo?>? FollowupSpeechCaptured;

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
            var pcmAudio = _utteranceAudio.ToArray();
            string? transcript;
            _lastDebugInfo = null;
            try { transcript = TranscribeSpeechAfterWakeWord(wakeWordResult, pcmAudio); }
            catch { transcript = null; }
            var debugInfo = _debugEnabled
                ? _lastDebugInfo ?? new VoiceCaptureDebugInfo(GetDurationMilliseconds(pcmAudio.Length), null, null)
                : null;
            if (string.IsNullOrWhiteSpace(transcript)) BeginPostWakeCapture(pcmAudio);
            WakeWordRecognized?.Invoke(transcript, CreateWaveAudio(pcmAudio), debugInfo);
        }
        finally
        {
            _utteranceAudio.SetLength(0);
        }
    }

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
                FollowupSpeechCaptured?.Invoke(transcript, CreateCombinedWaveAudio(), GetFollowupDebugInfo());
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
            FollowupSpeechCaptured?.Invoke(GetPortugueseText(portugueseResult), CreateCombinedWaveAudio(), GetFollowupDebugInfo());
        }
        EndPostWakeCapture();
    }

    private VoiceCaptureDebugInfo? GetFollowupDebugInfo()
    {
        if (!_debugEnabled) return null;
        var totalLength = (_wakeWordAudioPrefix?.Length ?? 0) + checked((int)_followupAudio.Length);
        var prior = _lastDebugInfo;
        return new VoiceCaptureDebugInfo(GetDurationMilliseconds(totalLength), prior?.CutMilliseconds,
            prior?.TranscriptionAudioWav);
    }

    private static int GetDurationMilliseconds(int pcmByteLength) => (int)Math.Round(pcmByteLength * 1000d / 32000d);

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

    private string? TranscribeSpeechAfterWakeWord(string wakeWordJson, byte[] pcmAudio)
    {
        using var wakeWord = JsonDocument.Parse(wakeWordJson);
        if (!TryGetWakeWordEnd(wakeWord.RootElement, out var wakeWordEnd)) return null;
        var cutMilliseconds = (int)Math.Round(wakeWordEnd * 1000d);
        var speechAudio = VoiceAudioSegmenter.AfterBoundary(pcmAudio, wakeWordEnd);
        if (_debugEnabled)
            _lastDebugInfo = new VoiceCaptureDebugInfo(GetDurationMilliseconds(pcmAudio.Length), cutMilliseconds,
                speechAudio.Length == 0 ? null : CreateWaveAudio(speechAudio));
        if (speechAudio.Length == 0) return null;
        var portugueseResult = TranscribePortugueseAudio(speechAudio, speechAudio.Length);
        var transcript = GetPortugueseText(portugueseResult);
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && IsActivationWord(words[0]))
            transcript = string.Join(' ', words.Skip(1));
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
