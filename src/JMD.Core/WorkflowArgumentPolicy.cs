namespace JMD.Core;

public static class WorkflowArgumentPolicy
{
    /// <summary>Preserves shortcut arguments and appends any arguments supplied by the workflow.</summary>
    public static IReadOnlyList<string> Combine(IEnumerable<string> shortcutArguments, IEnumerable<string> workflowArguments)
        => shortcutArguments.Concat(workflowArguments).ToArray();
}
