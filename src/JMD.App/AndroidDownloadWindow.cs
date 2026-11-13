using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using UserControl = System.Windows.Controls.UserControl;

namespace JMD.App;

internal sealed class AndroidInstallPanel : UserControl
{
    private const string DownloadUrl = "https://github.com/deggau/jmdDroid/releases/latest/download/jmdroid.apk";
    private static readonly Color MutedColor = Color.FromRgb(160, 166, 178);

    public AndroidInstallPanel()
    {
        var content = new StackPanel { Margin = new Thickness(10, 12, 10, 18), HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = "JMD para Android",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Leia o QR code com o celular para baixar o APK do JMDroid.",
            FontSize = 13,
            Foreground = new SolidColorBrush(MutedColor),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(4, 0, 4, 16)
        });

        var qrUrl = "https://api.qrserver.com/v1/create-qr-code/?size=280x280&format=png&data=" + Uri.EscapeDataString(DownloadUrl);
        var qr = new Image
        {
            Width = 280,
            Height = 280,
            Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri(qrUrl, UriKind.Absolute)),
            ToolTip = "QR code para baixar o JMDroid"
        };
        content.Children.Add(new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(12),
            Child = qr
        });

        var link = new TextBlock
        {
            Text = DownloadUrl,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 18, 0, 14),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        link.MouseLeftButtonUp += (_, _) => OpenDownload();
        content.Children.Add(link);

        var openButton = new Button
        {
            Content = "Abrir página de download",
            Padding = new Thickness(16, 9, 16, 9),
            Background = new SolidColorBrush(Color.FromRgb(42, 45, 53)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        openButton.Click += (_, _) => OpenDownload();
        content.Children.Add(openButton);
        Content = content;
    }

    private static void OpenDownload()
    {
        Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
    }
}
