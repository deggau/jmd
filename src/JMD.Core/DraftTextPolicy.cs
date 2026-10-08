namespace JMD.Core;

public static class DraftTextPolicy
{
    public const int DirectInputCharacterLimit = 512;

    public static bool ShouldUseClipboard(string text)
        => text.Length > DirectInputCharacterLimit;
}
