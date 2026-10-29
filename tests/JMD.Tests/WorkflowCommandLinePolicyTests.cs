using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WorkflowCommandLinePolicyTests
{
    [Fact]
    public void Unquoted_existing_shortcut_path_with_spaces_is_kept_whole()
    {
        const string shortcut = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\TeamViewer.lnk";
        var input = shortcut + " --minimize";

        var parsed = WorkflowCommandLinePolicy.TryParse(input, out var executable, out var arguments,
            candidate => candidate.Equals(shortcut, StringComparison.OrdinalIgnoreCase));

        Assert.True(parsed);
        Assert.Equal(shortcut, executable);
        Assert.Equal(["--minimize"], arguments);
    }

    [Fact]
    public void Missing_unquoted_lnk_path_reports_the_complete_target_instead_of_first_space_segment()
    {
        const string shortcut = @"C:\Users\user\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Chat App.lnk";

        Assert.True(WorkflowCommandLinePolicy.TryParse(shortcut, out var executable, out var arguments,
            _ => false));

        Assert.Equal(shortcut, executable);
        Assert.Empty(arguments);
    }

    [Fact]
    public void Existing_unquoted_executable_and_arguments_parse_at_the_file_boundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jmd workflow {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, "Program.exe");
        File.WriteAllText(executablePath, string.Empty);
        try
        {
            Assert.True(WorkflowCommandLinePolicy.TryParse($"{executablePath} --profile \"User Profile\"",
                out var executable, out var arguments));

            Assert.Equal(executablePath, executable);
            Assert.Equal(["--profile", "User Profile"], arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Quoted_executable_and_windows_arguments_round_trip()
    {
        const string executable = @"C:\Program Files\Vendor\Program.exe";
        string[] arguments = [@"C:\Profiles\User Folder\prefs.json", "text with \"quotes\"", "trailing\\"];
        var commandLine = $"\"{executable}\" {WorkflowCommandLinePolicy.JoinArguments(arguments)}";

        Assert.True(WorkflowCommandLinePolicy.TryParse(commandLine, out var parsedExecutable, out var parsedArguments));

        Assert.Equal(executable, parsedExecutable);
        Assert.Equal(arguments, parsedArguments);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Program.exe")]
    [InlineData("C:\\Program Files\\Program.exe \"unterminated argument")]
    public void Malformed_quoting_is_rejected_without_throwing(string commandLine)
    {
        Assert.False(WorkflowCommandLinePolicy.TryParse(commandLine, out _, out _));
    }

    [Fact]
    public void Editor_close_stores_result_and_closes_without_setting_wpf_dialog_result()
    {
        bool? accepted = null;
        var closed = false;

        WorkflowDialogClosePolicy.Close(accepted: false, result => accepted = result, () => closed = true);

        Assert.False(accepted);
        Assert.True(closed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Editor_completion_preserves_accept_and_cancel_outcomes(bool accepted)
    {
        bool? storedResult = null;
        var closed = false;

        WorkflowDialogClosePolicy.Close(accepted, result => storedResult = result, () => closed = true);

        Assert.Equal(accepted, storedResult);
        Assert.True(closed);
    }
}
