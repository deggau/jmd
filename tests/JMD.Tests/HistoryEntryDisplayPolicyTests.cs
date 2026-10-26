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

    [Theory]
    [InlineData("Executar Conectar na VPN", true)]
    [InlineData("Testar Conectar na VPN", true)]
    [InlineData("Ativação por voz (Jey) - Testar Conectar na VPN", true)]
    [InlineData("Ativação por voz (Jey) - Executar Conectar na VPN", true)]
    [InlineData("Formatar SQL", false)]
    public void IsWorkflowExecution_Recognizes_custom_workflow_history_rows(string commandName, bool expected)
    {
        Assert.Equal(expected, HistoryEntryDisplayPolicy.IsWorkflowExecution(commandName));
    }

    [Theory]
    [InlineData("Atualização do JMD", true)]
    [InlineData("Ativação por voz (Jey) - Atualização do JMD", false)]
    [InlineData("Verificar atualizações JMD", false)]
    public void IsVersionUpdate_Recognizes_version_update_history_rows(string commandName, bool expected)
    {
        Assert.Equal(expected, HistoryEntryDisplayPolicy.IsVersionUpdate(commandName));
    }
}
