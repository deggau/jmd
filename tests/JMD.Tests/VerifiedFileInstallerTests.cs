using System.Security.Cryptography;
using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class VerifiedFileInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "JMD.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Verified_model_is_moved_into_place_after_hashing()
    {
        Directory.CreateDirectory(_directory);
        var stagedPath = Path.Combine(_directory, "model.download");
        var destinationPath = Path.Combine(_directory, "model.bin");
        var model = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(stagedPath, model);
        var expectedHash = Convert.ToHexString(SHA256.HashData(model));

        await VerifiedFileInstaller.InstallSha256VerifiedFileAsync(stagedPath, destinationPath, expectedHash);

        Assert.False(File.Exists(stagedPath));
        Assert.Equal(model, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task Invalid_hash_keeps_the_staged_file_and_does_not_install_it()
    {
        Directory.CreateDirectory(_directory);
        var stagedPath = Path.Combine(_directory, "model.download");
        var destinationPath = Path.Combine(_directory, "model.bin");
        var model = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(stagedPath, model);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VerifiedFileInstaller.InstallSha256VerifiedFileAsync(stagedPath, destinationPath, "invalid-hash"));

        Assert.True(File.Exists(stagedPath));
        Assert.False(File.Exists(destinationPath));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
