namespace JMD.Core;

public static class HistoryEntryDisplayPolicy
{
    private const string VoiceCommandPrefix = "Ativação por voz (Jey) - ";

    public static bool IsApplicationLaunch(string commandName, string beforeValue, string afterValue)
    {
        if (string.IsNullOrWhiteSpace(beforeValue) || !string.IsNullOrEmpty(afterValue)) return false;

        var actionName = commandName.StartsWith(VoiceCommandPrefix, StringComparison.Ordinal)
            ? commandName[VoiceCommandPrefix.Length..]
            : commandName;
        return actionName.StartsWith("Abrir ", StringComparison.OrdinalIgnoreCase) ||
               actionName.StartsWith("Focar ", StringComparison.OrdinalIgnoreCase);
    }
}
