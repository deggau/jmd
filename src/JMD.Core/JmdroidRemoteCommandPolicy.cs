namespace JMD.Core;

public static class JmdroidRemoteCommandPolicy
{
    private static readonly HashSet<string> RemoteCommands = new(StringComparer.Ordinal)
    {
        "clipboard", "upperCase", "lowerCase", "titleCase", "jsonPretty", "jsonCompact",
        "screenShare", "desktopScreenShare", "todoCreate", "todoPanel", "todoPanelClose", "about",
        "hibernate", "suspend", "lock"
    };

    private static readonly HashSet<string> EmptyInputCommands = new(StringComparer.Ordinal)
    {
        "screenShare", "desktopScreenShare", "todoCreate", "todoPanel", "todoPanelClose", "about",
        "hibernate", "suspend", "lock"
    };

    public static bool IsRemoteCommand(string commandId) => RemoteCommands.Contains(commandId);

    public static bool RequiresEmptyInput(string commandId) => EmptyInputCommands.Contains(commandId);
}
