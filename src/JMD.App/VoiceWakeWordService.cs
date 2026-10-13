using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace JMD.App;

internal sealed class VoiceWakeWordService : IDisposable
{
    private readonly Model _model;
    private readonly VoskRecognizer _recognizer;
    private readonly WaveInEvent _microphone;
    private bool _started;

    public event Action<string?>? WakeWordRecognized;

    public VoiceWakeWordService()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "vosk-model-small-en-us-0.15");
        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException("O modelo local de reconhecimento de voz não foi encontrado.");

        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);
        _recognizer = new VoskRecognizer(_model, 16000.0f);
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
        if (!_recognizer.AcceptWaveform(eventArgs.Buffer, eventArgs.BytesRecorded)) return;
        using var result = JsonDocument.Parse(_recognizer.Result());
        if (!result.RootElement.TryGetProperty("text", out var textElement)) return;
        var text = textElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(words[0], "jay", StringComparison.OrdinalIgnoreCase)) return;
        var transcript = words.Length > 1 ? string.Join(' ', words.Skip(1)) : null;
        WakeWordRecognized?.Invoke(transcript);
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
        _model.Dispose();
    }
}
