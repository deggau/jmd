using System.IO;
using System.Text;
using System.Text.Json;
using JMD.Core;
using NAudio.Wave;
using Vosk;

namespace JMD.App;

internal sealed record VoiceCaptureDebugInfo(int DurationMilliseconds, int? CutMilliseconds, byte[]? TranscriptionAudioWav);
internal sealed record VoiceTranscriptionCandidate(string Text, double? Confidence);
internal sealed record WakeWordRecognitionDebugInfo(string Transcript, bool IsFinal, bool? WakeWordDetected);

internal sealed class VoiceWakeWordService : IDisposable
{
    private readonly Model _model;
    private readonly VoskRecognizer _recognizer;
    private readonly Model _portugueseModel;
    private readonly bool _useWhisperPortuguese;
    private readonly bool _useWhisperEnglish;
    private readonly WhisperCppTranscriber _whisper = new();
    private readonly WaveInEvent _microphone;
    private readonly AudioFrameQueue _audioFrames = new(capacity: 1800);
    private readonly Task _audioProcessor;
    private readonly MemoryStream _utteranceAudio = new();
    private readonly MemoryStream _followupAudio = new();
    private readonly MemoryStream _followupSegmentAudio = new();
    private long _recognizerAudioBytesFed;
    private double _utteranceStartSeconds;
    private byte[]? _wakeWordAudioPrefix;
    private DateTimeOffset _followupDeadline;
    private bool _awaitingPostWakeSpeech;
    private bool _started;
    private int _disposeRequested;
    private int _resourcesDisposed;

    private volatile bool _debugEnabled;
    private volatile bool _wakeWordDebugEnabled;
    private VoiceCaptureDebugInfo? _lastDebugInfo;
    private string _lastWakeWordDebugPartial = string.Empty;

    public bool DebugEnabled { get => _debugEnabled; set => _debugEnabled = value; }
    public bool WakeWordRecognitionDebugEnabled { get => _wakeWordDebugEnabled; set => _wakeWordDebugEnabled = value; }
    public event Action<string?, byte[], VoiceCaptureDebugInfo?>? WakeWordRecognized;
    public event Action<string?, byte[], VoiceCaptureDebugInfo?>? FollowupSpeechCaptured;
    public event Action<Exception>? AudioProcessingFailed;
    public event Action<WakeWordRecognitionDebugInfo>? WakeWordRecognitionDebugUpdated;

    public VoiceWakeWordService(bool useWhisperPortuguese = false, bool useWhisperEnglish = false)
    {
        _useWhisperPortuguese = useWhisperPortuguese;
        _useWhisperEnglish = useWhisperEnglish;
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
        _audioProcessor = Task.Run(ProcessAudioFramesAsync);
    }

    public void Start()
    {
        _microphone.StartRecording();
        _started = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposeRequested) != 0 || eventArgs.BytesRecorded <= 0) return;
        _audioFrames.TryWrite(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
    }

    private async Task ProcessAudioFramesAsync()
    {
        await foreach (var frame in _audioFrames.ReadAllAsync())
        {
            if (Volatile.Read(ref _disposeRequested) != 0) break;
            try
            {
                ProcessAudioFrame(frame);
            }
            catch (Exception exception)
            {
                try { AudioProcessingFailed?.Invoke(exception); }
                catch { }
            }
        }
    }

    private void ProcessAudioFrame(byte[] buffer)
    {
        _recognizerAudioBytesFed += buffer.Length;
        if (_awaitingPostWakeSpeech)
        {
            CapturePostWakeSpeech(buffer);
            return;
        }

        _utteranceAudio.Write(buffer, 0, buffer.Length);
        var wakeWordFinalized = _recognizer.AcceptWaveform(buffer, buffer.Length);
        if (!wakeWordFinalized)
        {
            if (_wakeWordDebugEnabled) ReportWakeWordRecognition(_recognizer.PartialResult(), isFinal: false, wakeWordDetected: null);
            return;
        }

        try
        {
            var wakeWordResult = _recognizer.Result();
            var wakeWordDetected = IsWakeWordResult(wakeWordResult);
            if (_wakeWordDebugEnabled) ReportWakeWordRecognition(wakeWordResult, isFinal: true, wakeWordDetected);
            _lastWakeWordDebugPartial = string.Empty;
            if (!wakeWordDetected) return;
            var pcmAudio = _utteranceAudio.ToArray();
            string? transcript;
            _lastDebugInfo = null;
            try { transcript = TranscribeSpeechAfterWakeWord(wakeWordResult, pcmAudio, _utteranceStartSeconds); }
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
            _utteranceStartSeconds = _recognizerAudioBytesFed / 32000d;
        }
    }

    private string TranscribePortugueseAudio(byte[] audio, int length)
    {
        return TranscribeAudio(_portugueseModel, audio, length);
    }

    private string TranscribeEnglishAudio(byte[] audio, int length)
    {
        return TranscribeAudio(_model, audio, length);
    }

    private static string TranscribeAudio(Model model, byte[] audio, int length)
    {
        using var recognizer = new VoskRecognizer(model, 16000.0f);
        recognizer.SetWords(true);
        var isFinalized = recognizer.AcceptWaveform(audio, length);
        return isFinalized ? recognizer.Result() : recognizer.FinalResult();
    }

    private string? TranscribeBestAudio(byte[] audio, int length)
    {
        VoiceTranscriptionCandidate? portuguese = null;
        VoiceTranscriptionCandidate? english = null;
        try { portuguese = _useWhisperPortuguese
            ? _whisper.Transcribe(audio, length, "pt")
            : CreateTranscriptionCandidate(TranscribePortugueseAudio(audio, length)); }
        catch { }
        try { english = _useWhisperEnglish
            ? _whisper.Transcribe(audio, length, "en")
            : CreateTranscriptionCandidate(TranscribeEnglishAudio(audio, length)); }
        catch { }
        var best = ChooseBestTranscription(portuguese, english);
        return best is null ? null : RemoveActivationWord(best.Text);
    }

    private void BeginPostWakeCapture(byte[] wakeWordPcm)
    {
        _wakeWordAudioPrefix = wakeWordPcm;
        _followupAudio.SetLength(0);
        _followupSegmentAudio.SetLength(0);
        _followupDeadline = DateTimeOffset.UtcNow.AddSeconds(6);
        _awaitingPostWakeSpeech = true;
    }

    private void CapturePostWakeSpeech(byte[] buffer)
    {
        _followupAudio.Write(buffer, 0, buffer.Length);
        _followupSegmentAudio.Write(buffer, 0, buffer.Length);
        var finalized = _recognizer.AcceptWaveform(buffer, buffer.Length);
        if (finalized)
        {
            var wakeRecognizerResult = _recognizer.Result();
            var transcript = TranscribeBestAudio(
                _followupSegmentAudio.GetBuffer(), checked((int)_followupSegmentAudio.Length));
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
            var transcript = TranscribeBestAudio(
                _followupSegmentAudio.GetBuffer(), checked((int)_followupSegmentAudio.Length));
            FollowupSpeechCaptured?.Invoke(transcript, CreateCombinedWaveAudio(), GetFollowupDebugInfo());
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
        _utteranceStartSeconds = _recognizerAudioBytesFed / 32000d;
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

    private static VoiceTranscriptionCandidate? CreateTranscriptionCandidate(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        if (!result.RootElement.TryGetProperty("text", out var textElement)) return null;
        var text = textElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        double? confidence = null;
        if (result.RootElement.TryGetProperty("result", out var words) &&
            words.ValueKind == JsonValueKind.Array)
        {
            var confidences = new List<double>();
            foreach (var word in words.EnumerateArray())
            {
                if (word.TryGetProperty("conf", out var value) && value.TryGetDouble(out var wordConfidence))
                    confidences.Add(wordConfidence);
            }
            if (confidences.Count > 0) confidence = confidences.Average();
        }

        return new VoiceTranscriptionCandidate(text, confidence);
    }

    private string? TranscribeSpeechAfterWakeWord(string wakeWordJson, byte[] pcmAudio, double segmentStartSeconds)
    {
        using var wakeWord = JsonDocument.Parse(wakeWordJson);
        if (!TryGetWakeWordEnd(wakeWord.RootElement, out var wakeWordEnd)) return null;
        wakeWordEnd = VoiceAudioSegmenter.ToSegmentOffset(wakeWordEnd, segmentStartSeconds);
        if (!double.IsFinite(wakeWordEnd) || wakeWordEnd > pcmAudio.Length / 32000d)
        {
            if (_debugEnabled)
                _lastDebugInfo = new VoiceCaptureDebugInfo(GetDurationMilliseconds(pcmAudio.Length), null, null);
            return null;
        }
        var cutMilliseconds = (int)Math.Round(wakeWordEnd * 1000d);
        var speechAudio = VoiceAudioSegmenter.AfterBoundary(pcmAudio, wakeWordEnd);
        if (_debugEnabled)
            _lastDebugInfo = new VoiceCaptureDebugInfo(GetDurationMilliseconds(pcmAudio.Length), cutMilliseconds,
                speechAudio.Length == 0 ? null : CreateWaveAudio(speechAudio));
        if (speechAudio.Length == 0) return null;
        return TranscribeBestAudio(speechAudio, speechAudio.Length);
    }

    private static VoiceTranscriptionCandidate? ChooseBestTranscription(
        VoiceTranscriptionCandidate? portuguese,
        VoiceTranscriptionCandidate? english)
    {
        if (portuguese is null) return english;
        if (english is null) return portuguese;
        if (portuguese.Confidence is null || english.Confidence is null) return portuguese;

        // Keep Portuguese on close scores to preserve current behavior unless English is clearly stronger.
        return english.Confidence.Value > portuguese.Confidence.Value + 0.03
            ? english
            : portuguese;
    }

    private static string RemoveActivationWord(string transcript)
    {
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && IsActivationWord(words[0]))
            return string.Join(' ', words.Skip(1));
        return transcript;
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

    private void ReportWakeWordRecognition(string resultJson, bool isFinal, bool? wakeWordDetected)
    {
        try
        {
            using var result = JsonDocument.Parse(resultJson);
            var property = isFinal ? "text" : "partial";
            var transcript = result.RootElement.TryGetProperty(property, out var text)
                ? text.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (!isFinal && string.Equals(transcript, _lastWakeWordDebugPartial, StringComparison.Ordinal)) return;
            _lastWakeWordDebugPartial = isFinal ? string.Empty : transcript;
            WakeWordRecognitionDebugUpdated?.Invoke(new WakeWordRecognitionDebugInfo(transcript, isFinal, wakeWordDetected));
        }
        catch (JsonException) { }
        catch (Exception) { }
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
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        _microphone.DataAvailable -= OnDataAvailable;
        if (_started)
        {
            try { _microphone.StopRecording(); }
            catch (InvalidOperationException) { }
        }
        _microphone.Dispose();
        _audioFrames.Complete();
        if (_audioProcessor.IsCompleted)
        {
            DisposeAudioResources();
        }
        else
        {
            _ = _audioProcessor.ContinueWith(_ => DisposeAudioResources(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void DisposeAudioResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _recognizer.Dispose();
        _portugueseModel.Dispose();
        _model.Dispose();
        _utteranceAudio.Dispose();
        _followupAudio.Dispose();
        _followupSegmentAudio.Dispose();
    }
}
