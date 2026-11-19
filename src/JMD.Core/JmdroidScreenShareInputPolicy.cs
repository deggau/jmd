namespace JMD.Core;

public static class JmdroidScreenShareInputPolicy
{
    public static TimeSpan MinimumInputInterval { get; } = TimeSpan.FromMilliseconds(20);
    public const int MaximumTextLength = 4000;
    public const int MinimumDragDurationMilliseconds = 50;
    public const int MaximumDragDurationMilliseconds = 5000;

    public static bool IsValid(string action, double x, double y, int deltaX, int deltaY,
        double? endX = null, double? endY = null, int durationMilliseconds = 0)
    {
        if (action is not ("tap" or "scroll" or "drag") || !double.IsFinite(x) || !double.IsFinite(y) ||
            x is < 0 or > 1 || y is < 0 or > 1) return false;

        return action switch
        {
            "tap" => deltaX == 0 && deltaY == 0,
            "scroll" => deltaX is >= -1200 and <= 1200 && deltaY is >= -1200 and <= 1200,
            "drag" => IsValidDrag(x, y, deltaX, deltaY, endX, endY, durationMilliseconds),
            _ => false
        };
    }

    public static bool IsValidText(string? text)
        => !string.IsNullOrEmpty(text) && text.Length <= MaximumTextLength;

    private static bool IsValidDrag(double x, double y, int deltaX, int deltaY,
        double? endX, double? endY, int durationMilliseconds)
        => deltaX == 0 && deltaY == 0 && endX is { } targetX && endY is { } targetY &&
           double.IsFinite(targetX) && double.IsFinite(targetY) && targetX is >= 0 and <= 1 && targetY is >= 0 and <= 1 &&
           durationMilliseconds is >= MinimumDragDurationMilliseconds and <= MaximumDragDurationMilliseconds;
}
