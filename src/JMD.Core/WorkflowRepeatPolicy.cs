namespace JMD.Core;

public static class WorkflowRepeatPolicy
{
    public const int MinimumCount = 1;
    public const int MaximumCount = 100;

    public static bool IsValidCount(int count) => count is >= MinimumCount and <= MaximumCount;
}
