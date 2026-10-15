using System.IO;
using NAudio.Wave;

namespace JMD.App;

internal static class ActivationSoundService
{
    private const int SampleRate = 22050;
    private const double Volume = 0.22;

    public static void PlayShortcutActivated() => Play([740, 988]);

    public static void PlayVoiceCommandFound() => Play([587, 740, 988]);

    private static void Play(int[] notes)
    {
        RawSourceWaveStream? stream = null;
        WaveOutEvent? output = null;
        try
        {
            stream = new RawSourceWaveStream(new MemoryStream(CreatePcm(notes)), new WaveFormat(SampleRate, 16, 1));
            output = new WaveOutEvent { Volume = (float)Volume };
            output.PlaybackStopped += (_, _) =>
            {
                output.Dispose();
                stream.Dispose();
            };
            output.Init(stream);
            output.Play();
        }
        catch
        {
            output?.Dispose();
            stream?.Dispose();
        }
    }

    private static byte[] CreatePcm(int[] notes)
    {
        const int noteMilliseconds = 72;
        const int gapMilliseconds = 18;
        var samplesPerNote = SampleRate * noteMilliseconds / 1000;
        var gapSamples = SampleRate * gapMilliseconds / 1000;
        var pcm = new byte[(samplesPerNote * notes.Length + gapSamples * (notes.Length - 1)) * sizeof(short)];
        var offset = 0;
        foreach (var frequency in notes)
        {
            for (var sample = 0; sample < samplesPerNote; sample++)
            {
                var progress = (double)sample / samplesPerNote;
                var envelope = Math.Min(1, progress * 18) * Math.Pow(1 - progress, 1.7);
                var value = (short)(Math.Sin(2 * Math.PI * frequency * sample / SampleRate) * envelope * short.MaxValue);
                pcm[offset++] = (byte)(value & 0xff);
                pcm[offset++] = (byte)((value >> 8) & 0xff);
            }
            offset += gapSamples * sizeof(short);
        }
        return pcm;
    }
}
