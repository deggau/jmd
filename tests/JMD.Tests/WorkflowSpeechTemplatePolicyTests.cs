using System.Globalization;
using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WorkflowSpeechTemplatePolicyTests
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    [Fact]
    public void Resolves_current_time_to_speech_friendly_text()
    {
        var now = new DateTimeOffset(2026, 11, 19, 15, 4, 0, TimeSpan.FromHours(-3));

        Assert.True(WorkflowSpeechTemplatePolicy.TryResolve("hora", now, Portuguese, out var value));

        Assert.Equal("15 horas e 4 minutos", value);
    }

    [Fact]
    public void Resolves_current_date_to_long_localized_date()
    {
        var now = new DateTimeOffset(2026, 11, 19, 15, 4, 0, TimeSpan.FromHours(-3));

        Assert.True(WorkflowSpeechTemplatePolicy.TryResolve("data", now, Portuguese, out var value));

        Assert.Equal("quinta-feira, 19 de novembro de 2026", value);
    }

    [Theory]
    [InlineData("meia-noite", 0)]
    [InlineData("meio-dia", 12)]
    public void Resolves_midnight_and_noon_naturally(string expected, int hour)
    {
        var now = new DateTimeOffset(2026, 11, 19, hour, 0, 0, TimeSpan.FromHours(-3));

        Assert.True(WorkflowSpeechTemplatePolicy.TryResolve("hora", now, Portuguese, out var value));

        Assert.Equal(expected, value);
    }

    [Fact]
    public void Rejects_unknown_system_variables()
        => Assert.False(WorkflowSpeechTemplatePolicy.TryResolve("unknown", DateTimeOffset.Now, Portuguese, out _));
}
