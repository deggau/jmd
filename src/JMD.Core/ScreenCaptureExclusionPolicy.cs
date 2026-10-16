namespace JMD.Core;

public static class ScreenCaptureExclusionPolicy
{
    private static readonly Version MinimumSupportedVersion = new(10, 0, 19041);

    public static bool IsSupported(Version operatingSystemVersion)
        => operatingSystemVersion >= MinimumSupportedVersion;
}
