using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class HistoryEntryDisplayPolicyTests
{
    [Theory]
    [InlineData("Abrir Meu programa", "C:\\Apps\\Meu programa.exe", "", true)]
    [InlineData("Ativação por voz (Jey) - Abrir Documentação", "https://example.com/docs", "", true)]
    [InlineData("Ativação por voz (Jey) - Focar Editor", "editor.exe --project teste", "", true)]
    [InlineData("Ativação por voz (Jey) - Abrir Sobre", "", "", false)]
    [InlineData("Ativação por voz (Jey) - Abrir link", "https://example.com", "resultado", false)]
    [InlineData("Formatar", "texto original", "texto formatado", false)]
    public void IsApplicationLaunch_RecognizesLaunchHistoryRows(
        string commandName, string beforeValue, string afterValue, bool expected)
    {
        Assert.Equal(expected, HistoryEntryDisplayPolicy.IsApplicationLaunch(commandName, beforeValue, afterValue));
    }
}
