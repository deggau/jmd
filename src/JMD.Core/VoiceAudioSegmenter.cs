namespace JMD.Core;

/// <summary>Extracts the speech after a word boundary from 16 kHz, 16-bit mono PCM audio.</summary>
public static class VoiceAudioSegmenter
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;

    public static double ToSegmentOffset(double absoluteBoundarySeconds, double segmentStartSeconds)
    {
        if (!double.IsFinite(absoluteBoundarySeconds) || !double.IsFinite(segmentStartSeconds) ||
            absoluteBoundarySeconds < segmentStartSeconds)
            return double.NaN;
        return absoluteBoundarySeconds - segmentStartSeconds;
    }

    public static byte[] AfterBoundary(byte[] pcmAudio, double boundarySeconds)
    {
        ArgumentNullException.ThrowIfNull(pcmAudio);
        if (!double.IsFinite(boundarySeconds) || boundarySeconds <= 0) return [];
        if (boundarySeconds >= pcmAudio.Length / (double)(SampleRate * BytesPerSample)) return [];

        var boundarySamples = (long)Math.Round(boundarySeconds * SampleRate, MidpointRounding.AwayFromZero);
        var byteOffset = Math.Clamp(boundarySamples * BytesPerSample, 0L, (long)pcmAudio.Length);
        byteOffset -= byteOffset % BytesPerSample;
        return pcmAudio[(int)byteOffset..];
    }
}
