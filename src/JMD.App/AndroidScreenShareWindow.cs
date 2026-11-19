using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;

namespace JMD.App;

internal sealed class AndroidScreenShareWindow : Window
{
    private readonly System.Windows.Controls.Image _image;
    private readonly TextBlock _status;
    private bool _hasReceivedFrame;

    public string DeviceId { get; }
    public string ShareId { get; private set; }
    public event Action? ClosedByUser;
    public event Action<string, string, double, double, int, int>? InputRequested;

    public AndroidScreenShareWindow(string deviceId, string deviceName, string shareId)
    {
        DeviceId = deviceId;
        ShareId = shareId;
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
        _image.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        _image.PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
        _image.PreviewMouseWheel += OnMouseWheel;
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
            if (!_hasReceivedFrame)
            {
                _hasReceivedFrame = true;
                _status.Text = "Ao vivo · clique para tocar · role para percorrer a tela.";
                _status.Visibility = Visibility.Visible;
            }
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
        _hasReceivedFrame = false;
        SetStatus("Solicitação enviada · autorize a captura de tela no Android…");
    }

    public void SetShareId(string shareId) => ShareId = shareId;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryGetNormalizedPoint(e.GetPosition(_image), out _, out _))
        {
            _image.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_image.IsMouseCaptured) return;
        _image.ReleaseMouseCapture();
        if (TryGetNormalizedPoint(e.GetPosition(_image), out var x, out var y))
            InputRequested?.Invoke(ShareId, "tap", x, y, 0, 0);
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryGetNormalizedPoint(e.GetPosition(_image), out var x, out var y)) return;
        InputRequested?.Invoke(ShareId, "scroll", x, y, 0, e.Delta);
        e.Handled = true;
    }

    private bool TryGetNormalizedPoint(System.Windows.Point point, out double x, out double y)
    {
        x = y = 0;
        if (_image.Source is not BitmapSource bitmap || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return false;
        var scale = Math.Min(_image.ActualWidth / bitmap.PixelWidth, _image.ActualHeight / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * scale;
        var height = bitmap.PixelHeight * scale;
        var left = (_image.ActualWidth - width) / 2;
        var top = (_image.ActualHeight - height) / 2;
        if (point.X < left || point.Y < top || point.X > left + width || point.Y > top + height) return false;
        x = Math.Clamp((point.X - left) / width, 0, 1);
        y = Math.Clamp((point.Y - top) / height, 0, 1);
        return true;
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
