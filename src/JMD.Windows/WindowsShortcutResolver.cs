using System.IO;
using System.Runtime.InteropServices;

namespace JMD.Windows;

public sealed record WindowsShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory);

/// <summary>Reads the launch settings stored in a Windows .lnk file.</summary>
public static class WindowsShortcutResolver
{
    public static WindowsShortcutTarget Resolve(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows shortcuts are supported only on Windows.");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("O Windows não disponibilizou o componente para ler atalhos .lnk.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            var fullShortcutPath = Path.GetFullPath(shortcutPath);
            var shortcutDirectory = Path.GetDirectoryName(fullShortcutPath)!;
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Não foi possível iniciar o leitor de atalhos do Windows.");
            dynamic shellObject = shell;
            shortcut = shellObject.CreateShortcut(fullShortcutPath);
            if (shortcut is null) throw new InvalidOperationException("Não foi possível ler o arquivo de atalho.");

            dynamic shortcutObject = shortcut;
            var targetPath = Environment.ExpandEnvironmentVariables(Convert.ToString(shortcutObject.TargetPath)?.Trim() ?? string.Empty);
            if (targetPath.Length == 0) throw new InvalidOperationException("O arquivo .lnk não possui um destino válido.");
            if (!Path.IsPathRooted(targetPath) && File.Exists(Path.Combine(shortcutDirectory, targetPath)))
                targetPath = Path.GetFullPath(targetPath, shortcutDirectory);
            var arguments = Environment.ExpandEnvironmentVariables(Convert.ToString(shortcutObject.Arguments) ?? string.Empty);
            var workingDirectory = Environment.ExpandEnvironmentVariables(Convert.ToString(shortcutObject.WorkingDirectory)?.Trim() ?? string.Empty);
            if (workingDirectory.Length > 0)
                workingDirectory = Path.GetFullPath(workingDirectory, shortcutDirectory);
            else if (Path.IsPathRooted(targetPath))
                workingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            return new WindowsShortcutTarget(targetPath, arguments, workingDirectory);
        }
        catch (Exception exception) when (exception is COMException or System.Reflection.TargetInvocationException)
        {
            throw new InvalidOperationException($"Não foi possível ler as configurações do atalho ‘{Path.GetFileName(shortcutPath)}’.", exception);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
