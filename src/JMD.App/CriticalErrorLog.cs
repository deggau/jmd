using System.IO;
using System.Text;

namespace JMD.App;

internal static class CriticalErrorLog
{
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "logs", "critical.log");

    public static void EnsureExists()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.WriteAllText(path, "JMD — registro de falhas críticas" + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public static void Write(string source, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                    File.WriteAllText(path, "JMD — registro de falhas críticas" + Environment.NewLine, Encoding.UTF8);
                File.AppendAllText(path,
                    $"{Environment.NewLine}[{DateTimeOffset.Now:O}] Origem: {source}{Environment.NewLine}{exception}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}
