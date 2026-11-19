namespace JMD.Core;

public static class JmdroidScreenShareInputPolicy
{
    public static TimeSpan MinimumInputInterval { get; } = TimeSpan.FromMilliseconds(20);

    public static bool IsValid(string action, double x, double y, int deltaX, int deltaY)
    {
        if (action is not ("tap" or "scroll") || !double.IsFinite(x) || !double.IsFinite(y) ||
            x is < 0 or > 1 || y is < 0 or > 1) return false;

        return action switch
        {
            "tap" => deltaX == 0 && deltaY == 0,
            "scroll" => deltaX is >= -1200 and <= 1200 && deltaY is >= -1200 and <= 1200,
            _ => false
        };
    }
}
