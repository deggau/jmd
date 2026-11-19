using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using JMD.Core;

namespace JMD.App;

internal sealed class AndroidScreenShareWindow : Window
{
    private readonly System.Windows.Controls.Image _image;
    private readonly TextBlock _status;
    private readonly Border _statusBorder;
    private readonly System.Windows.Controls.TextBox _textInput;
    private readonly DispatcherTimer _statusTimer;
    private System.Windows.Point? _dragStart;
    private DateTimeOffset _dragStartedAt;
    private bool _hasReceivedFrame;

    public string DeviceId { get; }
    public string ShareId { get; private set; }
    public event Action? ClosedByUser;
    public event Action<string, ScreenShareInputRequest>? InputRequested;
    public event Action<string, string>? TextRequested;

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
        _image.Focusable = true;
        _status = new TextBlock
        {
            Text = "Aguardando a autorização de captura no Android…",
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 28, 31, 38)),
            Padding = new Thickness(12),
            TextWrapping = TextWrapping.Wrap
        };
        var hideStatus = new System.Windows.Controls.Button
        {
            Content = "×",
            Width = 28,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
            FontSize = 18,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(53, 57, 66)),
            BorderThickness = new Thickness(0),
            ToolTip = "Fechar mensagem"
        };
        hideStatus.Click += (_, _) => HideStatus();
        var statusContent = new DockPanel();
        DockPanel.SetDock(hideStatus, Dock.Right);
        statusContent.Children.Add(hideStatus);
        statusContent.Children.Add(_status);
        _statusBorder = new Border
        {
            Child = statusContent,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10),
            CornerRadius = new CornerRadius(6)
        };
        _textInput = new System.Windows.Controls.TextBox
        {
            MinHeight = 40,
            MaxLength = JmdroidScreenShareInputPolicy.MaximumTextLength,
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 14,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 39, 48)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 62, 72)),
            CaretBrush = System.Windows.Media.Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Digite o texto para inserir no campo focado do Android e pressione Enter"
        };
        _textInput.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            SendTextInput();
            args.Handled = true;
        };
        var sendText = new System.Windows.Controls.Button
        {
            Content = "Enviar",
            MinWidth = 72,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(53, 57, 66)),
            BorderThickness = new Thickness(0)
        };
        sendText.Click += (_, _) => SendTextInput();
        var textPanel = new DockPanel { Margin = new Thickness(10, 0, 10, 10) };
        DockPanel.SetDock(sendText, Dock.Right);
        textPanel.Children.Add(sendText);
        textPanel.Children.Add(_textInput);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_image);
        root.Children.Add(_statusBorder);
        Grid.SetRow(textPanel, 1);
        root.Children.Add(textPanel);
        Content = root;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusTimer.Tick += (_, _) => HideStatus();
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
                SetStatus("Ao vivo · clique para tocar · arraste para mover · role para percorrer a tela.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or FormatException)
        {
            SetStatus("Não foi possível decodificar um quadro recebido do Android.");
        }
    }

    public void SetStatus(string message)
    {
        _statusTimer.Stop();
        _status.Text = message;
        _statusBorder.Visibility = Visibility.Visible;
    }

    public void SetTransientStatus(string message)
    {
        _status.Text = message;
        _statusBorder.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        _statusBorder.Visibility = Visibility.Collapsed;
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
            _dragStart = e.GetPosition(_image);
            _dragStartedAt = DateTimeOffset.UtcNow;
            _image.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_image.IsMouseCaptured) return;
        _image.ReleaseMouseCapture();
        var start = _dragStart;
        _dragStart = null;
        if (start is { } startPoint && TryGetNormalizedPoint(startPoint, out var x, out var y) &&
            TryGetNormalizedPoint(e.GetPosition(_image), out var endX, out var endY) && _image.Source is BitmapSource bitmap)
        {
            var endPoint = e.GetPosition(_image);
            var distance = (endPoint - startPoint).Length;
            if (distance >= 6)
            {
                var duration = (int)Math.Clamp((DateTimeOffset.UtcNow - _dragStartedAt).TotalMilliseconds,
                    JmdroidScreenShareInputPolicy.MinimumDragDurationMilliseconds,
                    JmdroidScreenShareInputPolicy.MaximumDragDurationMilliseconds);
                InputRequested?.Invoke(ShareId, new ScreenShareInputRequest("drag", x, y, 0, 0,
                    bitmap.PixelWidth, bitmap.PixelHeight, endX, endY, duration));
            }
            else
            {
                InputRequested?.Invoke(ShareId, new ScreenShareInputRequest("tap", endX, endY, 0, 0,
                    bitmap.PixelWidth, bitmap.PixelHeight));
            }
        }
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryGetNormalizedPoint(e.GetPosition(_image), out var x, out var y)) return;
        if (_image.Source is not BitmapSource bitmap) return;
        InputRequested?.Invoke(ShareId, new ScreenShareInputRequest("scroll", x, y, 0, e.Delta,
            bitmap.PixelWidth, bitmap.PixelHeight));
        e.Handled = true;
    }

    private void SendTextInput()
    {
        var text = _textInput.Text;
        if (string.IsNullOrEmpty(text)) return;
        TextRequested?.Invoke(ShareId, text);
        _textInput.Clear();
        _textInput.Focus();
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
        Height = Math.Clamp(pixelHeight * scale + 110, MinHeight, MaxHeight);
    }

    private double? _lastAspectRatio;
}

internal sealed record ScreenShareInputRequest(string Action, double X, double Y, int DeltaX, int DeltaY,
    int FrameWidth, int FrameHeight, double? EndX = null, double? EndY = null, int DurationMilliseconds = 0);
