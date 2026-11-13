using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UserControl = System.Windows.Controls.UserControl;

namespace JMD.App;

internal sealed class AndroidSettingsPanel : UserControl
{
    private static readonly Color CardColor = Color.FromRgb(34, 37, 45);
    private static readonly Color BorderColor = Color.FromRgb(58, 62, 72);
    private static readonly Color AccentColor = Color.FromRgb(180, 243, 106);
    private readonly AndroidInstallPanel _installPanel = new();
    private readonly ScrollViewer _installScroll;
    private readonly AndroidPairingPanel _pairingPanel;
    private readonly ContentControl _content = new();
    private readonly Button _installButton;
    private readonly Button _connectButton;
    private bool _connectSelected;

    public AndroidSettingsPanel(JmdroidApiServer server)
    {
        _pairingPanel = new AndroidPairingPanel(server);
        _installScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _installPanel
        };
        Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
        var heading = new TextBlock
        {
            Text = "Instale o JMDroid ou conecte um aparelho ao JMD.",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            FontSize = 12,
            Margin = new Thickness(2, 0, 2, 12)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var choices = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _installButton = CreateChoiceButton("Instalar app", "Baixar o APK no celular");
        _installButton.Margin = new Thickness(0, 0, 5, 0);
        _installButton.Click += (_, _) => _ = SelectInstallAsync();
        _connectButton = CreateChoiceButton("Conectar JMDroid", "Parear e gerenciar aparelhos");
        _connectButton.Margin = new Thickness(5, 0, 0, 0);
        _connectButton.Click += async (_, _) => await SelectConnectAsync();
        Grid.SetColumn(_installButton, 0);
        Grid.SetColumn(_connectButton, 1);
        choices.Children.Add(_installButton);
        choices.Children.Add(_connectButton);
        DockPanel.SetDock(choices, Dock.Top);
        root.Children.Add(choices);

        _content.Content = _installScroll;
        root.Children.Add(_content);
        Content = root;
        UpdateChoiceStyles();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _ = _pairingPanel.DeactivateAsync();
            else if (_connectSelected) _ = _pairingPanel.ActivateAsync();
        };
    }

    private async Task SelectInstallAsync()
    {
        _connectSelected = false;
        _content.Content = _installScroll;
        UpdateChoiceStyles();
        await _pairingPanel.DeactivateAsync();
    }

    private async Task SelectConnectAsync()
    {
        _connectSelected = true;
        _content.Content = _pairingPanel;
        UpdateChoiceStyles();
        await _pairingPanel.ActivateAsync();
    }

    private void UpdateChoiceStyles()
    {
        StyleChoiceButton(_installButton, !_connectSelected);
        StyleChoiceButton(_connectButton, _connectSelected);
    }

    private static Button CreateChoiceButton(string title, string description)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            Margin = new Thickness(0, 3, 0, 0)
        });
        return new Button
        {
            Content = content,
            Padding = new Thickness(12, 9, 12, 9),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(CardColor),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    private void StyleChoiceButton(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected ? Color.FromRgb(48, 61, 43) : CardColor);
        button.BorderBrush = new SolidColorBrush(selected ? AccentColor : BorderColor);
    }
}
