using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using JMD.Core;

namespace JMD.App;

internal static class DesktopScreenCaptureStreamer
{
    private const int SourceCopy = 0x00CC0020;
    private const int CaptureLayeredWindows = 0x40000000;

    public static async Task StreamAsync(
        int targetWidth,
        int targetHeight,
        int framesPerSecond,
        Func<byte[], CancellationToken, Task> sendFrame,
        CancellationToken cancellationToken)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("Não foi possível localizar a tela principal do computador.");
        var target = JmdroidScreenSharePolicy.FitScreenToViewport(
            bounds.Width, bounds.Height, targetWidth, targetHeight, JmdroidScreenSharePolicy.DesktopToAndroidMaxDimension);
        var fps = JmdroidScreenSharePolicy.NormalizeFramesPerSecond(framesPerSecond);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / fps));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await Task.Run(() => CaptureJpeg(bounds, target.Width, target.Height), cancellationToken);
            await sendFrame(frame, cancellationToken);
        }
    }

    private static byte[] CaptureJpeg(Rectangle bounds, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("Não foi possível acessar a tela principal.");

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var targetDc = graphics.GetHdc();
            try
            {
                if (!StretchBlt(targetDc, 0, 0, width, height, screenDc,
                        bounds.Left, bounds.Top, bounds.Width, bounds.Height, SourceCopy | CaptureLayeredWindows))
                    throw new InvalidOperationException("Não foi possível capturar a tela principal do computador.");
            }
            finally { graphics.ReleaseHdc(targetDc); }
        }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }

        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(item => item.MimeType == "image/jpeg")
            ?? throw new InvalidOperationException("O codificador JPEG não está disponível.");
        var quality = JmdroidScreenSharePolicy.DesktopToAndroidJpegQuality;
        var current = bitmap;
        Bitmap? resized = null;
        try
        {
            while (true)
            {
                foreach (var qualityOption in new[] { quality, 40L, 25L, 15L })
                {
                    using var stream = new MemoryStream();
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, qualityOption);
                    current.Save(stream, codec, parameters);
                    if (stream.Length <= JmdroidScreenSharePolicy.DesktopToAndroidMaxFrameBytes)
                        return stream.ToArray();
                }

                if (current.Width <= 160 && current.Height <= 160)
                    throw new InvalidOperationException("Não foi possível reduzir o quadro para menos de 512 KiB.");
                var nextWidth = Math.Max(1, (int)(current.Width * 0.8));
                var nextHeight = Math.Max(1, (int)(current.Height * 0.8));
                var next = new Bitmap(nextWidth, nextHeight, PixelFormat.Format24bppRgb);
                using (var resizeGraphics = Graphics.FromImage(next))
                {
                    resizeGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    resizeGraphics.DrawImage(current, 0, 0, nextWidth, nextHeight);
                }
                resized?.Dispose();
                resized = next;
                current = resized;
            }
        }
        finally { resized?.Dispose(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr destination, int destinationX, int destinationY, int destinationWidth, int destinationHeight,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);
}
