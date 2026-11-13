using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace JMD.App;

internal static class JmdroidFirewallRule
{
    public static async Task ConfigurePrivateNetworkAccessAsync(IPAddress listeningAddress, int listeningPort)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A regra do Firewall do Windows só pode ser configurada no Windows.");

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Não foi possível localizar o executável do JMD.");

        var escapedPath = executablePath.Replace("'", "''", StringComparison.Ordinal);
        var escapedAddress = listeningAddress.ToString().Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference = 'Stop'; try { " +
                     $"$interfaceIndex = (Get-NetIPAddress -AddressFamily IPv4 -IPAddress '{escapedAddress}' -ErrorAction Stop | Select-Object -First 1 -ExpandProperty InterfaceIndex); " +
                     "$networkProfile = Get-NetConnectionProfile -InterfaceIndex $interfaceIndex -ErrorAction Stop | Select-Object -First 1; " +
                     "if ($networkProfile.NetworkCategory -eq 'Public') { exit 2 }; " +
                     "$profile = if ($networkProfile.NetworkCategory -eq 'DomainAuthenticated') { 'Domain' } else { 'Private' }; " +
                     "$ruleName = 'JMD JMDroid API (Private)'; " +
                     "Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop; " +
                     $"New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Program '{escapedPath}' -Action Allow -Profile $profile -Protocol TCP -LocalPort {listeningPort} -LocalAddress '{escapedAddress}' -RemoteAddress LocalSubnet | Out-Null; " +
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
        if (process.ExitCode == 2)
            throw new InvalidOperationException("A rede atual do Windows está classificada como Pública. Altere o perfil da rede para Privada nas configurações do Windows e tente novamente.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException("O Windows não conseguiu criar a regra do Firewall para o endereço e a porta ativos. Verifique se sua conta pode administrar o computador.");
    }
}
