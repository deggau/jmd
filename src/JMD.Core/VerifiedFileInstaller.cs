using System.Security.Cryptography;

namespace JMD.Core;

/// <summary>Verifies a staged file before moving it into its final location.</summary>
public static class VerifiedFileInstaller
{
    public static async Task InstallSha256VerifiedFileAsync(
        string stagedPath,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string actualSha256;
        await using (var stream = File.OpenRead(stagedPath))
        {
            actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"O checksum do arquivo não confere (SHA-256 recebido: {actualSha256}).");

        File.Move(stagedPath, destinationPath, overwrite: true);
    }
}
