using System.IO;
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
    private bool _started;

    public event Action<string?>? WakeWordRecognized;

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
        _utteranceAudio.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        var wakeWordFinalized = _recognizer.AcceptWaveform(eventArgs.Buffer, eventArgs.BytesRecorded);
        if (!wakeWordFinalized) return;

        try
        {
            var wakeWordResult = _recognizer.Result();
            if (!IsWakeWordResult(wakeWordResult)) return;
            var portugueseResult = TranscribePortugueseUtterance();
            var transcript = GetTranscriptAfterWakeWord(wakeWordResult, portugueseResult);
            WakeWordRecognized?.Invoke(transcript);
        }
        finally
        {
            _utteranceAudio.SetLength(0);
        }
    }

    private string TranscribePortugueseUtterance()
    {
        using var recognizer = new VoskRecognizer(_portugueseModel, 16000.0f);
        recognizer.SetWords(true);
        var audio = _utteranceAudio.GetBuffer();
        var isFinalized = recognizer.AcceptWaveform(audio, checked((int)_utteranceAudio.Length));
        return isFinalized ? recognizer.Result() : recognizer.FinalResult();
    }

    private static string? GetTranscriptAfterWakeWord(string wakeWordJson, string portugueseJson)
    {
        using var wakeWord = JsonDocument.Parse(wakeWordJson);
        if (!TryGetWakeWordEnd(wakeWord.RootElement, out var wakeWordEnd)) return null;
        using var portuguese = JsonDocument.Parse(portugueseJson);
        if (!portuguese.RootElement.TryGetProperty("result", out var recognizedWords) ||
            recognizedWords.ValueKind != JsonValueKind.Array)
            return null;

        var transcriptWords = recognizedWords.EnumerateArray()
            .Where(word => word.TryGetProperty("start", out var start) && start.GetDouble() >= wakeWordEnd)
            .Select(word => word.TryGetProperty("word", out var recognizedWord) ? recognizedWord.GetString() : null)
            .Where(word => !string.IsNullOrWhiteSpace(word));
        var transcript = string.Join(' ', transcriptWords);
        return string.IsNullOrWhiteSpace(transcript) ? null : transcript;
    }

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
    }
}
