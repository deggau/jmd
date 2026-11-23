using System.Drawing;
using System.Globalization;

namespace JMD.Core;

public enum WorkflowControlIdentifierKind
{
    NameOrAutomationId,
    Name,
    AutomationId
}

public sealed record WorkflowControlIdentifier(WorkflowControlIdentifierKind Kind, string Value);

public static class WorkflowControlIdentifierPolicy
{
    public static bool TryParse(string? text, out WorkflowControlIdentifier identifier)
    {
        identifier = new WorkflowControlIdentifier(WorkflowControlIdentifierKind.NameOrAutomationId, string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        var separator = value.IndexOf(':');
        if (separator > 0)
        {
            var prefix = value[..separator].Trim();
            var remainder = value[(separator + 1)..].Trim();
            if (remainder.Length == 0) return false;
            if (prefix.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("automationid", StringComparison.OrdinalIgnoreCase))
            {
                identifier = new WorkflowControlIdentifier(WorkflowControlIdentifierKind.AutomationId, remainder);
                return true;
            }
            if (prefix.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                identifier = new WorkflowControlIdentifier(WorkflowControlIdentifierKind.Name, remainder);
                return true;
            }
        }
        identifier = new WorkflowControlIdentifier(WorkflowControlIdentifierKind.NameOrAutomationId, value);
        return true;
    }
}

public enum WorkflowControlCondition
{
    Exists,
    Missing,
    Enabled,
    Disabled,
    Visible,
    Checked,
    Unchecked
}

public static class WorkflowStepActionPolicy
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.Ordinal)
    {
        "Open", "OpenProgram", "OpenUrl", "Key", "Type", "Wait", "Focus", "WaitWindow", "WaitControl", "IfControl", "Click", "PositionWindow", "Speak"
    };

    public static bool IsSupported(string? action) => action is not null && SupportedActions.Contains(action);
}

public static class WorkflowLaunchTargetPolicy
{
    private static readonly System.Text.RegularExpressions.Regex VariablePattern =
        new(@"\{\{[A-Za-z][A-Za-z0-9_]{0,39}\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsUrlTarget(string? value)
    {
        var target = value?.Trim() ?? string.Empty;
        return target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static string ClassifyLegacyTarget(string? value) => IsUrlTarget(value) ? "OpenUrl" : "OpenProgram";

    public static bool IsValidUrlTarget(string? value)
    {
        if (!IsUrlTarget(value)) return false;
        var expanded = VariablePattern.Replace(value!.Trim(), "jmd-template-value");
        return Uri.TryCreate(expanded, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }

    public static bool IsValidProgramTarget(string? value)
        => !IsUrlTarget(value) && WorkflowCommandLinePolicy.IsValidTarget(value);
}

public static class WorkflowSpeechTemplatePolicy
{
    public static bool IsBuiltInVariable(string? variableName)
        => variableName is not null && (variableName.Equals("hora", StringComparison.OrdinalIgnoreCase) ||
                                        variableName.Equals("data", StringComparison.OrdinalIgnoreCase));

    public static bool TryResolve(string? variableName, DateTimeOffset now, CultureInfo culture, out string value)
    {
        value = variableName?.ToLowerInvariant() switch
        {
            "hora" => FormatTime(now),
            "data" => now.ToString("dddd, d 'de' MMMM 'de' yyyy", culture),
            _ => string.Empty
        };
        return IsBuiltInVariable(variableName);
    }

    private static string FormatTime(DateTimeOffset now)
    {
        if (now.Hour == 0 && now.Minute == 0) return "meia-noite";
        if (now.Hour == 12 && now.Minute == 0) return "meio-dia";
        var hour = now.Hour switch
        {
            0 => "meia-noite",
            12 => "meio-dia",
            _ => $"{now.Hour} {(now.Hour == 1 ? "hora" : "horas")}"
        };
        return now.Minute == 0 ? hour : $"{hour} e {now.Minute} {(now.Minute == 1 ? "minuto" : "minutos")}";
    }
}

public static class WorkflowTimeoutPolicy
{
    public const int MinimumMilliseconds = 100;
    public const int MaximumMilliseconds = 120000;

    public static bool IsValid(int timeoutMilliseconds)
        => timeoutMilliseconds is >= MinimumMilliseconds and <= MaximumMilliseconds;
}

public static class WorkflowConditionalPolicy
{
    public static int NextStepIndexWhenFalse(int currentStepIndex, int skipSteps, int totalSteps)
        => Math.Clamp(currentStepIndex + Math.Max(0, skipSteps) + 1, currentStepIndex + 1, totalSteps);
}

public static class WorkflowControlConditionPolicy
{
    public static bool Matches(WorkflowControlCondition condition, bool exists, bool enabled,
        bool offscreen, bool? isChecked)
        => condition switch
        {
            WorkflowControlCondition.Exists => exists,
            WorkflowControlCondition.Missing => !exists,
            WorkflowControlCondition.Enabled => exists && enabled,
            WorkflowControlCondition.Disabled => exists && !enabled,
            WorkflowControlCondition.Visible => exists && !offscreen,
            WorkflowControlCondition.Checked => exists && isChecked == true,
            WorkflowControlCondition.Unchecked => exists && isChecked == false,
            _ => false
        };
}

public readonly record struct WorkflowRelativePoint(double X, double Y)
{
    public static WorkflowRelativePoint FromScreenPoint(Point point, Rectangle windowBounds)
        => windowBounds.Width <= 1 || windowBounds.Height <= 1
            ? default
            : new WorkflowRelativePoint(
                Math.Clamp((double)(point.X - windowBounds.Left) / (windowBounds.Width - 1), 0, 1),
                Math.Clamp((double)(point.Y - windowBounds.Top) / (windowBounds.Height - 1), 0, 1));

    public static bool TryParse(string? value, out WorkflowRelativePoint point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            x is < 0 or > 1 || y is < 0 or > 1)
            return false;
        point = new WorkflowRelativePoint(x, y);
        return true;
    }

    public Point ToScreenPoint(Rectangle windowBounds)
        => new(windowBounds.Left + (int)Math.Round(Math.Clamp(X, 0, 1) * Math.Max(0, windowBounds.Width - 1)),
            windowBounds.Top + (int)Math.Round(Math.Clamp(Y, 0, 1) * Math.Max(0, windowBounds.Height - 1)));

    public override string ToString()
        => $"{X.ToString("0.####", CultureInfo.InvariantCulture)},{Y.ToString("0.####", CultureInfo.InvariantCulture)}";
}

public enum WorkflowMouseAction
{
    LeftClick,
    DoubleClick,
    RightClick,
    ScrollUp,
    ScrollDown
}

public static class WorkflowMouseActionPolicy
{
    public static bool TryParse(string? value, out WorkflowMouseAction action)
        => Enum.TryParse(value, true, out action) && Enum.IsDefined(action);
}

public static class WorkflowWindowTargetPolicy
{
    public static (bool IsProcess, string Value) Parse(string target)
    {
        var value = target.Trim();
        if (value.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
        {
            var process = value[8..].Trim();
            if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];
            return (true, process);
        }
        return (false, value);
    }
}

public static class WorkflowMonitorPlacementPolicy
{
    public static Rectangle CenterAndClamp(Rectangle monitorBounds, Size windowSize)
    {
        var width = Math.Min(Math.Max(1, windowSize.Width), Math.Max(1, monitorBounds.Width));
        var height = Math.Min(Math.Max(1, windowSize.Height), Math.Max(1, monitorBounds.Height));
        return new Rectangle(monitorBounds.Left + (monitorBounds.Width - width) / 2,
            monitorBounds.Top + (monitorBounds.Height - height) / 2, width, height);
    }
}
