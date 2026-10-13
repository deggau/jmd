using System.Globalization;
using System.Speech.Recognition;

namespace JMD.App;

internal sealed class VoiceWakeWordService : IDisposable
{
    private readonly SpeechRecognitionEngine _recognizer;
    private bool _started;

    public event Action? WakeWordRecognized;

    public VoiceWakeWordService()
    {
        _recognizer = new SpeechRecognitionEngine(new CultureInfo("en-US"));
        _recognizer.LoadGrammar(new Grammar(new GrammarBuilder("Jey")));
        _recognizer.SpeechRecognized += OnSpeechRecognized;
    }

    public void Start()
    {
        _recognizer.SetInputToDefaultAudioDevice();
        _recognizer.RecognizeAsync(RecognizeMode.Multiple);
        _started = true;
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        if (eventArgs.Result.Confidence >= 0.65)
            WakeWordRecognized?.Invoke();
    }

    public void Dispose()
    {
        _recognizer.SpeechRecognized -= OnSpeechRecognized;
        if (_started)
        {
            try { _recognizer.RecognizeAsyncCancel(); }
            catch (InvalidOperationException) { }
        }
        _recognizer.Dispose();
    }
}
