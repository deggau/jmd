using System.IO;
using System.Text;
using System.Text.Json;
using JMD.Core;
using NAudio.Wave;
using Vosk;

namespace JMD.App;

internal sealed record VoiceCaptureDebugInfo(int DurationMilliseconds, int? CutMilliseconds, byte[]? TranscriptionAudioWav,
    int? PortugueseTranscriptionTimeMilliseconds = null, int? EnglishTranscriptionTimeMilliseconds = null,
    int? TotalTranscriptionTimeMilliseconds = null);
internal sealed record VoiceTranscriptionCandidate(string Text, double? Confidence);
internal sealed record VoiceTranscriptionAttemptDebugInfo(
    string Language, string Engine, string? Transcript, double? Confidence, string? Error, bool Selected,
    int? ElapsedMilliseconds);
internal sealed record VoiceTranscriptionDebugInfo(
    DateTimeOffset AudioCapturedAt, int AudioDurationMilliseconds, int? CutMilliseconds,
    IReadOnlyList<VoiceTranscriptionAttemptDebugInfo> Attempts, int? TotalElapsedMilliseconds = null);
internal sealed record WakeWordRecognitionDebugInfo(
    DateTimeOffset AudioCapturedAt, string Transcript, bool IsFinal, bool? WakeWordDetected, double? Confidence,
    bool ConfidenceAccepted, int MinimumConfidencePercent, bool ClosedGrammarEnabled);

internal sealed class VoiceWakeWordService : IDisposable
{
    private readonly Model _model;
    private readonly VoskRecognizer _recognizer;
    private readonly VoskRecognizer? _followupRecognizer;
    private readonly Model _portugueseModel;
    private readonly string _portugueseModelId;
    private readonly string _englishModelId;
    private readonly bool _closedWakeWordGrammarEnabled;
    private readonly bool _closedCommandGrammarEnabled;
    private readonly WhisperCppTranscriber _whisper = new();
    private readonly Gemma4Transcriber _gemma = new();
    private readonly SherpaParakeetTranscriber _sherpaPortuguese = new();
    private readonly SherpaParakeetTranscriber _sherpaEnglish = new();
    private readonly WaveInEvent _microphone;
    private readonly AudioFrameQueue _audioFrames = new(capacity: 1800);
    private readonly Task _audioProcessor;
    private readonly MemoryStream _utteranceAudio = new();
    private readonly MemoryStream _followupAudio = new();
    private readonly MemoryStream _followupSegmentAudio = new();
    private long _recognizerAudioBytesFed;
    private double _utteranceStartSeconds;
    private DateTimeOffset? _utteranceCapturedAt;
    private byte[]? _wakeWordAudioPrefix;
    private DateTimeOffset? _followupSegmentCapturedAt;
    private DateTimeOffset _followupDeadline;
    private bool _awaitingPostWakeSpeech;
    private bool _started;
    private int _disposeRequested;
    private int _resourcesDisposed;

    private volatile bool _debugEnabled;
    private volatile bool _wakeWordDebugEnabled;
    private volatile int _minimumWakeWordConfidencePercent;
    private volatile int _minimumCommandConfidencePercent;
    private string[] _voiceCommandPhrases = [];
    private VoiceCaptureDebugInfo? _lastDebugInfo;
    private string _lastWakeWordDebugPartial = string.Empty;

    public bool DebugEnabled { get => _debugEnabled; set => _debugEnabled = value; }
    public bool WakeWordRecognitionDebugEnabled { get => _wakeWordDebugEnabled; set => _wakeWordDebugEnabled = value; }
    public int MinimumWakeWordConfidencePercent
    {
        get => _minimumWakeWordConfidencePercent;
        set => _minimumWakeWordConfidencePercent = Math.Clamp(value, 0, 100);
    }
    public int MinimumCommandConfidencePercent
    {
        get => _minimumCommandConfidencePercent;
        set => _minimumCommandConfidencePercent = Math.Clamp(value, 0, 100);
    }
    public void SetVoiceCommandPhrases(string[] phrases)
        => Volatile.Write(ref _voiceCommandPhrases, phrases);
    public event Action<string?, byte[], VoiceCaptureDebugInfo?>? WakeWordRecognized;
    public event Action<string?, byte[], VoiceCaptureDebugInfo?>? FollowupSpeechCaptured;
    public event Action<Exception>? AudioProcessingFailed;
    public event Action<WakeWordRecognitionDebugInfo>? WakeWordRecognitionDebugUpdated;
    public event Action<VoiceTranscriptionDebugInfo>? WakeWordTranscriptionDebugUpdated;

    public VoiceWakeWordService(
        string portugueseModelId = "vosk",
        string englishModelId = "vosk",
        bool closedWakeWordGrammarEnabled = false,
        int minimumWakeWordConfidencePercent = 0,
        bool closedCommandGrammarEnabled = false,
        int minimumCommandConfidencePercent = 0)
    {
        _portugueseModelId = portugueseModelId;
        _englishModelId = englishModelId;
        _closedWakeWordGrammarEnabled = closedWakeWordGrammarEnabled;
        _closedCommandGrammarEnabled = closedCommandGrammarEnabled;
        MinimumWakeWordConfidencePercent = minimumWakeWordConfidencePercent;
        MinimumCommandConfidencePercent = minimumCommandConfidencePercent;
        var wakeWordModelPath = Path.Combine(AppContext.BaseDirectory, "vosk-model-small-en-us-0.15");
        var portugueseModelPath = Path.Combine(AppContext.BaseDirectory, "vosk-model-small-pt-0.3");
        if (!Directory.Exists(wakeWordModelPath))
            throw new DirectoryNotFoundException("O modelo local de reconhecimento de voz não foi encontrado.");
        if (!Directory.Exists(portugueseModelPath))
            throw new DirectoryNotFoundException("O modelo local de português não foi encontrado.");

        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(wakeWordModelPath);
        if (_closedWakeWordGrammarEnabled)
        {
            _recognizer = new VoskRecognizer(_model, 16000.0f, "[\"jay\"]");
            _followupRecognizer = new VoskRecognizer(_model, 16000.0f);
        }
        else
        {
            _recognizer = new VoskRecognizer(_model, 16000.0f);
        }
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
        _audioFrames.TryWrite(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded), DateTimeOffset.Now);
    }

    private async Task ProcessAudioFramesAsync()
    {
        await foreach (var frame in _audioFrames.ReadTimestampedFramesAsync())
        {
            if (Volatile.Read(ref _disposeRequested) != 0) break;
            try
            {
                ProcessAudioFrame(frame.Audio, frame.CapturedAt);
            }
            catch (Exception exception)
            {
                try { AudioProcessingFailed?.Invoke(exception); }
                catch { }
            }
        }
    }

    private void ProcessAudioFrame(byte[] buffer, DateTimeOffset capturedAt)
    {
        _recognizerAudioBytesFed += buffer.Length;
        if (_awaitingPostWakeSpeech)
        {
            CapturePostWakeSpeech(buffer, capturedAt);
            return;
        }

        _utteranceCapturedAt ??= capturedAt;
        _utteranceAudio.Write(buffer, 0, buffer.Length);
        var wakeWordFinalized = _recognizer.AcceptWaveform(buffer, buffer.Length);
        if (!wakeWordFinalized)
        {
            if (_wakeWordDebugEnabled)
                ReportWakeWordRecognition(_recognizer.PartialResult(), isFinal: false, wakeWordDetected: null,
                    confidence: null, confidenceAccepted: false,
                    minimumConfidencePercent: MinimumWakeWordConfidencePercent, _utteranceCapturedAt ?? capturedAt);
            return;
        }

        try
        {
            var wakeWordResult = _recognizer.Result();
            var wakeWordCandidateDetected = IsWakeWordResult(wakeWordResult);
            var wakeWordConfidence = GetFirstWordConfidence(wakeWordResult);
            var minimumConfidence = MinimumWakeWordConfidencePercent;
            var confidenceAccepted = wakeWordCandidateDetected &&
                (minimumConfidence == 0 || wakeWordConfidence is { } confidence && confidence * 100 >= minimumConfidence);
            if (_wakeWordDebugEnabled)
                ReportWakeWordRecognition(wakeWordResult, isFinal: true, wakeWordCandidateDetected,
                    wakeWordConfidence, confidenceAccepted, minimumConfidence, _utteranceCapturedAt ?? capturedAt);
            _lastWakeWordDebugPartial = string.Empty;
            if (!confidenceAccepted) return;
            var pcmAudio = _utteranceAudio.ToArray();
            string? transcript;
            _lastDebugInfo = null;
            var audioCapturedAt = _utteranceCapturedAt ?? capturedAt;
            try { transcript = TranscribeSpeechAfterWakeWord(wakeWordResult, pcmAudio, _utteranceStartSeconds, audioCapturedAt); }
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
            _utteranceCapturedAt = null;
            _utteranceStartSeconds = _recognizerAudioBytesFed / 32000d;
        }
    }

    private string TranscribePortugueseAudio(byte[] audio, int length, string? grammar)
    {
        return TranscribeAudio(_portugueseModel, audio, length, grammar);
    }

    private string TranscribeEnglishAudio(byte[] audio, int length, string? grammar)
    {
        return TranscribeAudio(_model, audio, length, grammar);
    }

    private static string TranscribeAudio(Model model, byte[] audio, int length, string? grammar)
    {
        using var recognizer = grammar is null
            ? new VoskRecognizer(model, 16000.0f)
            : new VoskRecognizer(model, 16000.0f, grammar);
        recognizer.SetWords(true);
        var isFinalized = recognizer.AcceptWaveform(audio, length);
        return isFinalized ? recognizer.Result() : recognizer.FinalResult();
    }

    private string? TranscribeBestAudio(
        byte[] audio, int length, int? cutMilliseconds = null, DateTimeOffset? audioCapturedAt = null)
    {
        var measure = _debugEnabled || _wakeWordDebugEnabled;
        var totalTimer = measure ? System.Diagnostics.Stopwatch.StartNew() : null;
        VoiceTranscriptionCandidate? portuguese = null;
        VoiceTranscriptionCandidate? english = null;
        string? portugueseError = null;
        string? englishError = null;
        var attempts = new List<VoiceTranscriptionAttemptDebugInfo>();
        int? portugueseElapsedMilliseconds;
        int? englishElapsedMilliseconds;
        if (VoiceTranscriptionParallelism.CanRunConcurrently(_portugueseModelId, _englishModelId))
        {
            var portugueseTask = Task.Run(() => RunTranscriptionAttempt(audio, length, "pt", _portugueseModelId, measure));
            var englishTask = Task.Run(() => RunTranscriptionAttempt(audio, length, "en", _englishModelId, measure));
            Task.WaitAll(portugueseTask, englishTask);
            (portuguese, portugueseError, portugueseElapsedMilliseconds) = portugueseTask.Result;
            (english, englishError, englishElapsedMilliseconds) = englishTask.Result;
        }
        else
        {
            (portuguese, portugueseError, portugueseElapsedMilliseconds) = RunTranscriptionAttempt(audio, length, "pt", _portugueseModelId, measure);
            (english, englishError, englishElapsedMilliseconds) = RunTranscriptionAttempt(audio, length, "en", _englishModelId, measure);
        }
        var best = ChooseBestTranscription(portuguese, english);
        totalTimer?.Stop();
        attempts.Add(CreateAttemptDebugInfo("Português", GetModelDisplayName(_portugueseModelId), portuguese, portugueseError, best, portugueseElapsedMilliseconds));
        attempts.Add(CreateAttemptDebugInfo("Inglês", GetModelDisplayName(_englishModelId), english, englishError, best, englishElapsedMilliseconds));
        if (_debugEnabled)
        {
            var audioWav = length > 0 ? CreateWaveAudio(audio.AsSpan(0, length).ToArray()) : null;
            _lastDebugInfo = _lastDebugInfo is { } prior
                ? prior with { CutMilliseconds = cutMilliseconds ?? prior.CutMilliseconds, TranscriptionAudioWav = audioWav,
                    PortugueseTranscriptionTimeMilliseconds = portugueseElapsedMilliseconds,
                    EnglishTranscriptionTimeMilliseconds = englishElapsedMilliseconds,
                    TotalTranscriptionTimeMilliseconds = totalTimer is null ? null : (int?)totalTimer.ElapsedMilliseconds }
                : new VoiceCaptureDebugInfo(GetDurationMilliseconds(length), cutMilliseconds, audioWav,
                    portugueseElapsedMilliseconds, englishElapsedMilliseconds,
                    totalTimer is null ? null : (int?)totalTimer.ElapsedMilliseconds);
        }
        if (_wakeWordDebugEnabled)
        {
            try
            {
                WakeWordTranscriptionDebugUpdated?.Invoke(new VoiceTranscriptionDebugInfo(
                    audioCapturedAt ?? DateTimeOffset.Now, GetDurationMilliseconds(length), cutMilliseconds,
                    attempts, totalTimer is null ? null : (int?)totalTimer.ElapsedMilliseconds));
            }
            catch { }
        }
        return best is null ? null : RemoveActivationWord(best.Text);
    }

    private VoiceTranscriptionCandidate? TranscribeConfiguredModel(byte[] audio, int length, string language, string modelId)
    {
        var candidate = modelId switch
        {
            "vosk" => CreateTranscriptionCandidate(language == "pt"
                ? TranscribePortugueseAudio(audio, length, CreateVoskCommandGrammar(modelId))
                : TranscribeEnglishAudio(audio, length, CreateVoskCommandGrammar(modelId))),
            "whisperTiny" or "whisperBase" => _whisper.Transcribe(audio, length, language, modelId),
            "gemma4E2B" => _gemma.Transcribe(audio, length, language),
            "sherpaParakeetPtBr" => _sherpaPortuguese.Transcribe(audio, length, modelId,
                VoiceCommandGrammar.CreateSherpaHotwords(_closedCommandGrammarEnabled, modelId, Volatile.Read(ref _voiceCommandPhrases))),
            "sherpaZipformerEn" => _sherpaEnglish.Transcribe(audio, length, modelId,
                VoiceCommandGrammar.CreateSherpaHotwords(_closedCommandGrammarEnabled, modelId, Volatile.Read(ref _voiceCommandPhrases))),
            _ => throw new InvalidOperationException($"Modelo de transcrição desconhecido: {modelId}")
        };

        if (_closedCommandGrammarEnabled && modelId == "vosk" &&
            !VoiceCommandGrammar.IsConfidenceAccepted(MinimumCommandConfidencePercent, candidate?.Confidence))
            return null;
        return candidate;
    }

    private string? CreateVoskCommandGrammar(string modelId)
        => VoiceCommandGrammar.CreateVoskGrammar(
            _closedCommandGrammarEnabled, modelId, Volatile.Read(ref _voiceCommandPhrases));

    private static bool IsVosk(string modelId) => modelId == "vosk";

    private static string GetModelDisplayName(string modelId) => modelId switch
    {
        "vosk" => "Vosk",
        "whisperTiny" => "Whisper Tiny",
        "whisperBase" => "Whisper Base",
        "gemma4E2B" => "Gemma E2B experimental",
        "sherpaParakeetPtBr" => "Sherpa Parakeet pt-BR",
        "sherpaZipformerEn" => "Sherpa Zipformer inglês",
        _ => modelId
    };

    private (VoiceTranscriptionCandidate? Candidate, string? Error, int? ElapsedMilliseconds) RunTranscriptionAttempt(
        byte[] audio, int length, string language, string modelId, bool measure)
    {
        var timer = measure ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            var candidate = TranscribeConfiguredModel(audio, length, language, modelId);
            timer?.Stop();
            return (candidate, null, timer is null ? null : (int?)timer.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            timer?.Stop();
            return (null, exception.Message, timer is null ? null : (int?)timer.ElapsedMilliseconds);
        }
    }

    private static VoiceTranscriptionAttemptDebugInfo CreateAttemptDebugInfo(
        string language, string engine, VoiceTranscriptionCandidate? candidate, string? error,
        VoiceTranscriptionCandidate? selected, int? elapsedMilliseconds)
        => new(language, engine, candidate?.Text, candidate?.Confidence, error, ReferenceEquals(candidate, selected), elapsedMilliseconds);

    private void BeginPostWakeCapture(byte[] wakeWordPcm)
    {
        _followupRecognizer?.Reset();
        _wakeWordAudioPrefix = wakeWordPcm;
        _followupAudio.SetLength(0);
        _followupSegmentAudio.SetLength(0);
        _followupSegmentCapturedAt = null;
        _followupDeadline = DateTimeOffset.UtcNow.AddSeconds(6);
        _awaitingPostWakeSpeech = true;
    }

    private void CapturePostWakeSpeech(byte[] buffer, DateTimeOffset capturedAt)
    {
        _followupAudio.Write(buffer, 0, buffer.Length);
        _followupSegmentCapturedAt ??= capturedAt;
        _followupSegmentAudio.Write(buffer, 0, buffer.Length);
        var followupRecognizer = _followupRecognizer ?? _recognizer;
        var finalized = followupRecognizer.AcceptWaveform(buffer, buffer.Length);
        if (finalized)
        {
            var wakeRecognizerResult = followupRecognizer.Result();
            var transcript = TranscribeBestAudio(
                _followupSegmentAudio.GetBuffer(), checked((int)_followupSegmentAudio.Length), audioCapturedAt: _followupSegmentCapturedAt ?? capturedAt);
            if (HasRecognizedText(wakeRecognizerResult) || transcript is not null)
            {
                FollowupSpeechCaptured?.Invoke(transcript, CreateCombinedWaveAudio(), GetFollowupDebugInfo());
                EndPostWakeCapture();
                return;
            }
            _followupAudio.SetLength(0);
            _followupSegmentAudio.SetLength(0);
            _followupSegmentCapturedAt = null;
        }

        if (DateTimeOffset.UtcNow < _followupDeadline) return;
        var partialResult = followupRecognizer.PartialResult();
        if (HasRecognizedPartialText(partialResult) && _followupSegmentAudio.Length > 0)
        {
            var transcript = TranscribeBestAudio(
                _followupSegmentAudio.GetBuffer(), checked((int)_followupSegmentAudio.Length), audioCapturedAt: _followupSegmentCapturedAt ?? capturedAt);
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
            prior?.TranscriptionAudioWav, prior?.PortugueseTranscriptionTimeMilliseconds,
            prior?.EnglishTranscriptionTimeMilliseconds);
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

    private string? TranscribeSpeechAfterWakeWord(
        string wakeWordJson, byte[] pcmAudio, double segmentStartSeconds, DateTimeOffset capturedAt)
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
        return TranscribeBestAudio(speechAudio, speechAudio.Length, cutMilliseconds, capturedAt);
    }

    private VoiceTranscriptionCandidate? ChooseBestTranscription(
        VoiceTranscriptionCandidate? portuguese,
        VoiceTranscriptionCandidate? english)
    {
        var portugueseMatchesCommand = portuguese is not null && MatchesConfiguredVoiceCommand(portuguese);
        var englishMatchesCommand = english is not null && MatchesConfiguredVoiceCommand(english);
        if (portugueseMatchesCommand != englishMatchesCommand)
            return portugueseMatchesCommand ? portuguese : english;

        if (IsVosk(_portugueseModelId) != IsVosk(_englishModelId))
        {
            var nonVosk = IsVosk(_portugueseModelId) ? english : portuguese;
            var vosk = IsVosk(_portugueseModelId) ? portuguese : english;
            if (vosk is null) return nonVosk;
            var minimum = MinimumWakeWordConfidencePercent;
            if (minimum == 0 || vosk.Confidence is { } confidence && confidence * 100 >= minimum)
                return vosk;
            return nonVosk;
        }

        if (portuguese is null) return english;
        if (english is null) return portuguese;
        if (portuguese.Confidence is null) return english.Confidence is null ? portuguese : english;
        if (english.Confidence is null) return portuguese;
        return english.Confidence > portuguese.Confidence ? english : portuguese;
    }

    private bool MatchesConfiguredVoiceCommand(VoiceTranscriptionCandidate candidate)
    {
        var transcript = RemoveActivationWord(candidate.Text);
        var phrases = Volatile.Read(ref _voiceCommandPhrases);
        return phrases.Any(phrase => VoicePhrasePolicy.Matches(phrase, transcript));
    }

    private static string RemoveActivationWord(string transcript)
    {
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && IsActivationWord(VoicePhrasePolicy.Normalize(words[0])))
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

    private void ReportWakeWordRecognition(
        string resultJson, bool isFinal, bool? wakeWordDetected, double? confidence,
        bool confidenceAccepted, int minimumConfidencePercent, DateTimeOffset capturedAt)
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
            WakeWordRecognitionDebugUpdated?.Invoke(new WakeWordRecognitionDebugInfo(
                capturedAt, transcript, isFinal, wakeWordDetected, confidence, confidenceAccepted,
                minimumConfidencePercent, _closedWakeWordGrammarEnabled));
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
        var firstWord = result.RootElement.TryGetProperty("text", out var text)
            ? text.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        return firstWord is not null && IsWakeWordToken(firstWord);
    }

    private static bool IsWakeWordToken(string word)
        => word.Equals("jay", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("jey", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("jei", StringComparison.OrdinalIgnoreCase);

    private static double? GetFirstWordConfidence(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        if (!result.RootElement.TryGetProperty("result", out var words) ||
            words.ValueKind != JsonValueKind.Array || words.GetArrayLength() == 0)
            return null;
        var firstWord = words[0];
        return firstWord.TryGetProperty("conf", out var confidence) &&
            confidence.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : null;
    }

    private static bool TryGetWakeWordEnd(JsonElement result, out double end)
    {
        end = 0;
        if (!result.TryGetProperty("result", out var words) ||
            words.ValueKind != JsonValueKind.Array || words.GetArrayLength() == 0)
            return false;
        var firstWord = words[0];
        return firstWord.TryGetProperty("word", out var word) &&
            word.GetString() is { } recognizedWord && IsWakeWordToken(recognizedWord) &&
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
        _followupRecognizer?.Dispose();
        _recognizer.Dispose();
        _portugueseModel.Dispose();
        _model.Dispose();
        _sherpaPortuguese.Dispose();
        _sherpaEnglish.Dispose();
        _utteranceAudio.Dispose();
        _followupAudio.Dispose();
        _followupSegmentAudio.Dispose();
    }
}
