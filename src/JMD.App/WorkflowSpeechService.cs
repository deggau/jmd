using System.Globalization;
using System.Speech.Synthesis;

namespace JMD.App;

internal static class WorkflowSpeechService
{
    private static readonly SemaphoreSlim SpeechLock = new(1, 1);
    private static readonly CultureInfo PortugueseBrazil = CultureInfo.GetCultureInfo("pt-BR");

    public static async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Informe o texto que o Jey deve falar.");
        if (text.Length > 2000)
            throw new InvalidOperationException("O texto falado não pode passar de 2000 caracteres.");

        await SpeechLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using var synthesizer = new SpeechSynthesizer();
                var portugueseVoice = synthesizer.GetInstalledVoices(PortugueseBrazil)
                    .FirstOrDefault(voice => voice.Enabled);
                if (portugueseVoice is not null)
                    synthesizer.SelectVoice(portugueseVoice.VoiceInfo.Name);
                synthesizer.Speak(text);
            });
        }
        finally { SpeechLock.Release(); }
    }
}
