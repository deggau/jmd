using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace JMD.App;

internal static class JmdroidFirewallRule
{
    public static async Task ConfigurePrivateNetworkAccessAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A regra do Firewall do Windows só pode ser configurada no Windows.");

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Não foi possível localizar o executável do JMD.");

        var escapedPath = executablePath.Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference = 'Stop'; try { " +
                     "$ruleName = 'JMD JMDroid API (Private)'; " +
                     "Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop; " +
                     $"New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Program '{escapedPath}' -Action Allow -Profile Private -Protocol TCP | Out-Null; " +
                     "exit 0 } catch { exit 1 }";
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedScript}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar a configuração do Firewall do Windows.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("O Windows não conseguiu criar a regra do Firewall. Verifique se sua conta pode administrar o computador.");
    }
}
