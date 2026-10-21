using System.IO;
using NAudio.Wave;

namespace JMD.App;

internal static class ActivationSoundService
{
    private const int SampleRate = 22050;
    private const double Volume = 0.22;

    public const string Classic = "classic";
    public const string Soft = "soft";
    public const string Chime = "chime";
    public const string Double = "double";
    public const string None = "none";

    public static string Normalize(string? sound, string fallback)
        => sound is Classic or Soft or Chime or Double or None ? sound : fallback;

    public static void PlayShortcutActivated(string? sound = null) => Play(sound, Signal.Shortcut);

    public static void PlayVoiceCommandFound(string? sound = null) => Play(sound, Signal.VoiceCommand);

    public static void PlayJeyRecognized(string? sound = null) => Play(sound, Signal.JeyActivated);

    public static void PlayTaskReminder(string? sound = null) => Play(sound ?? Soft, Signal.TaskReminder);

    private static void Play(string? sound, Signal signal)
    {
        sound = Normalize(sound, signal == Signal.TaskReminder ? Soft : Classic);
        if (sound == None) return;

        var pattern = sound switch
        {
            Soft => new SoundPattern([440], 0.075, 115, 0),
            Chime => new SoundPattern([784, 1046, 1318], 0.14, 78, 24),
            Double => new SoundPattern([659, 659], 0.13, 72, 42),
            _ => signal switch
            {
                Signal.Shortcut => new SoundPattern([740, 988], Volume, 72, 18),
                Signal.VoiceCommand => new SoundPattern([587, 740, 988], Volume, 72, 18),
                Signal.JeyActivated => new SoundPattern([494], 0.10, 72, 18),
                Signal.TaskReminder => new SoundPattern([440], 0.075, 115, 0),
                _ => new SoundPattern([], 0, 72, 18)
            }
        };
        Play(pattern);
    }

    private static void Play(SoundPattern pattern)
    {
        RawSourceWaveStream? stream = null;
        WaveOutEvent? output = null;
        try
        {
            stream = new RawSourceWaveStream(new MemoryStream(CreatePcm(pattern)), new WaveFormat(SampleRate, 16, 1));
            output = new WaveOutEvent { Volume = (float)pattern.Volume };
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

    private static byte[] CreatePcm(SoundPattern pattern)
    {
        var samplesPerNote = SampleRate * pattern.NoteMilliseconds / 1000;
        var gapSamples = SampleRate * pattern.GapMilliseconds / 1000;
        var pcm = new byte[(samplesPerNote * pattern.Notes.Length + gapSamples * (pattern.Notes.Length - 1)) * sizeof(short)];
        var offset = 0;
        for (var noteIndex = 0; noteIndex < pattern.Notes.Length; noteIndex++)
        {
            var frequency = pattern.Notes[noteIndex];
            for (var sample = 0; sample < samplesPerNote; sample++)
            {
                var progress = (double)sample / samplesPerNote;
                var envelope = Math.Min(1, progress * 18) * Math.Pow(1 - progress, 1.7);
                var value = (short)(Math.Sin(2 * Math.PI * frequency * sample / SampleRate) * envelope * short.MaxValue);
                pcm[offset++] = (byte)(value & 0xff);
                pcm[offset++] = (byte)((value >> 8) & 0xff);
            }
            if (noteIndex < pattern.Notes.Length - 1) offset += gapSamples * sizeof(short);
        }
        return pcm;
    }

    private sealed record SoundPattern(int[] Notes, double Volume, int NoteMilliseconds, int GapMilliseconds);

    private enum Signal { Shortcut, VoiceCommand, JeyActivated, TaskReminder }
}
