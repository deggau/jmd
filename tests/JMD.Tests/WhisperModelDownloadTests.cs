using JMD.Core;
using Xunit;

namespace JMD.Tests;

public sealed class WhisperModelDownloadTests
{
    [Fact]
    public void Tiny_download_uses_the_pinned_file_and_matching_sha256()
    {
        var download = WhisperModelDownloads.Get("whisperTiny");

        Assert.Equal("ggml-tiny-q5_1.bin", download.FileName);
        Assert.Equal(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/fb5ed9369c26eaf08392504d8446da431e0fffb9/ggml-tiny-q5_1.bin",
            download.Url);
        Assert.Equal("818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7", download.Sha256);
    }

    [Fact]
    public void Download_error_survives_status_refresh()
    {
        const string error = "Não foi possível instalar Whisper Tiny: conexão encerrada.";

        var status = VoiceModelDownloadFeedback.AfterStatusRefresh(error, "Ainda não baixado.");

        Assert.Equal(error, status);
    }

    [Fact]
    public void Successful_download_uses_refreshed_status()
    {
        const string success = "Modelo verificado e instalado.";

        Assert.Equal(success, VoiceModelDownloadFeedback.AfterStatusRefresh(null, success));
    }
}
