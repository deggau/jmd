using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JMD.App;

internal sealed class AndroidScreenShareWindow : Window
{
    private readonly System.Windows.Controls.Image _image;
    private readonly TextBlock _status;

    public string DeviceId { get; }
    public event Action? ClosedByUser;

    public AndroidScreenShareWindow(string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        Title = $"Tela do Android · {deviceName}";
        Width = 460;
        Height = 900;
        MinWidth = 300;
        MinHeight = 420;
        MaxWidth = 1100;
        MaxHeight = 1400;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = System.Windows.Media.Brushes.Black;

        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _status = new TextBlock
        {
            Text = "Aguardando a autorização de captura no Android…",
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 28, 31, 38)),
            Padding = new Thickness(12),
            TextWrapping = TextWrapping.Wrap
        };
        var root = new Grid();
        root.Children.Add(_image);
        var statusBorder = new Border
        {
            Child = _status,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10),
            CornerRadius = new CornerRadius(6)
        };
        root.Children.Add(statusBorder);
        Content = root;
        Closed += (_, _) => ClosedByUser?.Invoke();
    }

    public void ShowFrame(ReadOnlyMemory<byte> jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg.ToArray(), writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            ResizeToScreenAspect(bitmap.PixelWidth, bitmap.PixelHeight);
            _image.Source = bitmap;
            _status.Text = "Ao vivo · compartilhe novamente pelo Android para iniciar outra transmissão.";
            _status.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or FormatException)
        {
            _status.Text = "Não foi possível decodificar um quadro recebido do Android.";
            _status.Visibility = Visibility.Visible;
        }
    }

    public void SetStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = Visibility.Visible;
    }

    public void PrepareForShare()
    {
        _image.Source = null;
        _lastAspectRatio = null;
        SetStatus("Solicitação enviada · autorize a captura de tela no Android…");
    }

    private void ResizeToScreenAspect(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        var ratio = (double)pixelWidth / pixelHeight;
        if (_lastAspectRatio is { } previous && Math.Abs(previous - ratio) < 0.12) return;
        _lastAspectRatio = ratio;
        var scale = Math.Min(900d / pixelWidth, 1200d / pixelHeight);
        Width = Math.Clamp(pixelWidth * scale + 20, MinWidth, MaxWidth);
        Height = Math.Clamp(pixelHeight * scale + 60, MinHeight, MaxHeight);
    }

    private double? _lastAspectRatio;
}
