using System.Security.Cryptography;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace JMD.App;

internal sealed class BusinessmapIntegrationSettings
{
    public bool Enabled { get; set; }
    public string AccountUrl { get; set; } = string.Empty;
    public string ProtectedApiKey { get; set; } = string.Empty;
    public List<BusinessmapBoardSettings> Boards { get; set; } = [];

    public BusinessmapIntegrationSettings Clone() => new()
    {
        Enabled = Enabled,
        AccountUrl = AccountUrl,
        ProtectedApiKey = ProtectedApiKey,
        Boards = Boards.Select(board => board.Clone()).ToList()
    };
}

internal sealed class BusinessmapBoardSettings
{
    public string BoardId { get; set; } = string.Empty;
    public string BoardName { get; set; } = string.Empty;
    public bool UseLaneFilter { get; set; }
    public List<string> IncludedLaneIds { get; set; } = [];
    public List<string> HiddenLaneIds { get; set; } = [];
    public bool UseOwnerFilter { get; set; }
    public List<string> OwnerIds { get; set; } = [];

    public BusinessmapBoardSettings Clone() => new()
    {
        BoardId = BoardId,
        BoardName = BoardName,
        UseLaneFilter = UseLaneFilter,
        IncludedLaneIds = [.. IncludedLaneIds ?? []],
        HiddenLaneIds = [.. HiddenLaneIds ?? []],
        UseOwnerFilter = UseOwnerFilter,
        OwnerIds = [.. OwnerIds ?? []]
    };
}

internal sealed class BusinessmapSettingsWindow : Window
{
    private readonly BusinessmapIntegrationSettings _settings;
    private readonly TextBox _accountUrl = new();
    private readonly PasswordBox _apiKey = new();
    private readonly CheckBox _enabled = new() { Content = "Sincronizar tarefas do Businessmap no painel", Foreground = Brushes.White, FontSize = 13 };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _boardsPanel = new();
    private IReadOnlyList<BusinessmapBoard> _availableBoards = [];
    private IReadOnlyList<BusinessmapLane> _availableLanes = [];
    private readonly Dictionary<string, IReadOnlyList<BusinessmapLane>> _lanesByBoard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<BusinessmapOwner>> _ownersByBoard = new(StringComparer.Ordinal);
    private bool _loading;

    public BusinessmapIntegrationSettings Configuration => _settings;

    public BusinessmapSettingsWindow(BusinessmapIntegrationSettings settings)
    {
        _settings = settings.Clone();
        _settings.Boards ??= [];
        foreach (var board in _settings.Boards)
        {
            board.IncludedLaneIds ??= [];
            board.HiddenLaneIds ??= [];
            board.OwnerIds ??= [];
        }

        Title = "Configurações de tarefas";
        Width = 680;
        Height = 720;
        MinWidth = 540;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31));
        Topmost = true;

        var outer = new Border
        {
            Background = Background,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18)
        };
        Content = outer;
        var layout = new DockPanel();
        outer.Child = layout;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MakeButton("Salvar", true);
        save.Click += (_, _) => SaveSettings();
        footer.Children.Add(cancel);
        footer.Children.Add(save);

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        var titleBar = new Border { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll, Child = new TextBlock { Text = "Configurações de tarefas", Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.SemiBold } };
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(titleBar);
        header.Children.Add(new TextBlock { Text = "As tarefas locais do JMD continuam separadas dos cartões do Businessmap. Nesta versão, os cartões são exibidos em modo somente leitura.", Foreground = MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new StackPanel();
        scroll.Content = body;
        layout.Children.Add(scroll);
        _enabled.IsChecked = _settings.Enabled;
        body.Children.Add(_enabled);
        body.Children.Add(Label("URL da conta Businessmap"));
        StyleBox(_accountUrl);
        _accountUrl.Text = _settings.AccountUrl;
        _accountUrl.ToolTip = "Use a URL do seu ambiente, por exemplo https://empresa.kanbanize.com";
        _accountUrl.Margin = new Thickness(0, 4, 0, 8);
        body.Children.Add(_accountUrl);
        body.Children.Add(Label("Chave de API do seu usuário"));
        _apiKey.Password = UnprotectApiKey(_settings.ProtectedApiKey);
        _apiKey.Padding = new Thickness(9);
        _apiKey.FontSize = 13;
        _apiKey.Foreground = Brushes.White;
        _apiKey.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48));
        _apiKey.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72));
        _apiKey.Margin = new Thickness(0, 4, 0, 8);
        body.Children.Add(_apiKey);

        var connect = MakeButton("Conectar e carregar quadros", false);
        connect.HorizontalAlignment = HorizontalAlignment.Left;
        connect.Click += async (_, _) => await ConnectAsync();
        body.Children.Add(connect);
        body.Children.Add(_status);
        body.Children.Add(new TextBlock { Text = "Quadros e filtros", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
        body.Children.Add(new TextBlock { Text = "Marque os quadros que quer exibir. Abra cada quadro para configurar lanes e responsáveis. Se não ativar um filtro, todas as opções daquele tipo serão mostradas.", Foreground = MutedBrush, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(_boardsPanel);

        if (!string.IsNullOrWhiteSpace(_settings.ProtectedApiKey))
            _status.Text = "Chave protegida salva para esta conta do Windows. Conecte para atualizar a lista de quadros e filtros.";
        RenderBoards();
    }

    private async Task ConnectAsync()
    {
        if (_loading) return;
        _loading = true;
        _status.Foreground = MutedBrush;
        _status.Text = "Conectando e carregando quadros…";
        try
        {
            using var client = CreateClient();
            _availableBoards = await client.GetBoardsAsync();
            _settings.AccountUrl = _accountUrl.Text.Trim().TrimEnd('/');
            _settings.ProtectedApiKey = WorkflowVariableProtection.Protect(_apiKey.Password);
            foreach (var selected in _settings.Boards)
                if (_availableBoards.FirstOrDefault(board => board.Id == selected.BoardId) is { } board)
                    selected.BoardName = board.Name;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106));
            _status.Text = $"Conectado. {_availableBoards.Count} quadro(s) disponível(is). Abra os quadros selecionados para carregar as lanes e responsáveis.";
            RenderBoards();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or JsonException)
        {
            _status.Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 135));
            _status.Text = exception.Message;
        }
        finally { _loading = false; }
    }

    private void RenderBoards()
    {
        _boardsPanel.Children.Clear();
        var boards = _availableBoards.Count > 0
            ? _availableBoards
            : _settings.Boards.Select(board => new BusinessmapBoard(board.BoardId, board.BoardName)).ToList();
        if (boards.Count == 0)
        {
            _boardsPanel.Children.Add(new TextBlock { Text = "Conecte-se para escolher os quadros.", Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(2, 4, 2, 10) });
            return;
        }

        foreach (var board in boards)
        {
            var saved = _settings.Boards.FirstOrDefault(item => item.BoardId == board.Id);
            var selected = new CheckBox { Content = board.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsChecked = saved is not null };
            var expander = new Expander { Header = selected, Margin = new Thickness(0, 2, 0, 4), Foreground = Brushes.White };
            var filters = new StackPanel { Margin = new Thickness(18, 5, 4, 8) };
            filters.Children.Add(new TextBlock { Text = "Abra para carregar e editar filtros deste quadro.", Foreground = MutedBrush, FontSize = 10 });
            expander.Content = filters;
            selected.Checked += (_, _) => EnsureBoardSelection(board);
            selected.Unchecked += (_, _) => RemoveBoardSelection(board.Id);
            expander.Expanded += async (_, _) =>
            {
                if (selected.IsChecked != true) { selected.IsChecked = true; }
                await LoadBoardFiltersAsync(board, filters);
            };
            _boardsPanel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5), Child = expander });
        }
    }

    private async Task LoadBoardFiltersAsync(BusinessmapBoard board, StackPanel target)
    {
        if (_lanesByBoard.ContainsKey(board.Id) && _ownersByBoard.ContainsKey(board.Id))
        {
            RenderBoardFilters(board, target);
            return;
        }
        try
        {
            target.Children.Clear();
            target.Children.Add(new TextBlock { Text = "Carregando lanes e responsáveis…", Foreground = MutedBrush, FontSize = 10 });
            using var client = CreateClient();
            var lanesTask = client.GetLanesAsync(board.Id);
            var cardsTask = client.GetCardsAsync(board.Id);
            await Task.WhenAll(lanesTask, cardsTask);
            _lanesByBoard[board.Id] = lanesTask.Result;
            _ownersByBoard[board.Id] = cardsTask.Result
                .Where(card => card.OwnerId.Length > 0)
                .GroupBy(card => card.OwnerId, StringComparer.Ordinal)
                .Select(group => new BusinessmapOwner(group.Key, group.Select(card => card.OwnerName).FirstOrDefault(name => name.Length > 0) ?? group.Key))
                .OrderBy(owner => owner.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _settings.Boards.FirstOrDefault(item => item.BoardId == board.Id)!.BoardName = board.Name;
            RenderBoardFilters(board, target);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or JsonException)
        {
            target.Children.Clear();
            target.Children.Add(new TextBlock { Text = exception.Message, Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 135)), FontSize = 10, TextWrapping = TextWrapping.Wrap });
        }
    }

    private void RenderBoardFilters(BusinessmapBoard board, StackPanel target)
    {
        target.Children.Clear();
        var settings = _settings.Boards.FirstOrDefault(item => item.BoardId == board.Id);
        if (settings is null) return;
        var lanes = _lanesByBoard.GetValueOrDefault(board.Id, []);
        var owners = _ownersByBoard.GetValueOrDefault(board.Id, []);
        var laneFilter = new CheckBox { Content = "Mostrar somente as lanes marcadas como incluídas", Foreground = Brushes.White, IsChecked = settings.UseLaneFilter, Margin = new Thickness(0, 5, 0, 5) };
        target.Children.Add(laneFilter);
        laneFilter.Checked += (_, _) =>
        {
            settings.UseLaneFilter = true;
            if (settings.IncludedLaneIds.Count == 0) settings.IncludedLaneIds = lanes.Select(lane => lane.Id).ToList();
        };
        laneFilter.Unchecked += (_, _) => settings.UseLaneFilter = false;
        target.Children.Add(new TextBlock { Text = "Lane · Marque incluir para usar com o filtro; ocultar sempre exclui a lane.", Foreground = MutedBrush, FontSize = 10, Margin = new Thickness(0, 2, 0, 4) });
        foreach (var lane in lanes)
        {
            var include = new CheckBox { Content = "Incluir", IsChecked = !settings.UseLaneFilter || settings.IncludedLaneIds.Count == 0 || settings.IncludedLaneIds.Contains(lane.Id), Foreground = MutedBrush, FontSize = 10, Margin = new Thickness(8, 0, 6, 0) };
            var hide = new CheckBox { Content = "Ocultar", IsChecked = settings.HiddenLaneIds.Contains(lane.Id), Foreground = MutedBrush, FontSize = 10, Margin = new Thickness(0, 0, 6, 0) };
            include.Checked += (_, _) => settings.IncludedLaneIds.AddUnique(lane.Id);
            include.Unchecked += (_, _) => settings.IncludedLaneIds.Remove(lane.Id);
            hide.Checked += (_, _) => settings.HiddenLaneIds.AddUnique(lane.Id);
            hide.Unchecked += (_, _) => settings.HiddenLaneIds.Remove(lane.Id);
            var label = string.IsNullOrWhiteSpace(lane.WorkflowName) ? lane.Name : $"{lane.WorkflowName} · {lane.Name}";
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(hide, Dock.Right); row.Children.Add(hide);
            DockPanel.SetDock(include, Dock.Right); row.Children.Add(include);
            row.Children.Insert(0, new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            target.Children.Add(row);
        }

        var ownerFilter = new CheckBox { Content = "Filtrar por responsável", Foreground = Brushes.White, IsChecked = settings.UseOwnerFilter, Margin = new Thickness(0, 10, 0, 5) };
        target.Children.Add(ownerFilter);
        ownerFilter.Checked += (_, _) =>
        {
            settings.UseOwnerFilter = true;
            if (settings.OwnerIds.Count == 0) settings.OwnerIds = owners.Select(owner => owner.Id).ToList();
        };
        ownerFilter.Unchecked += (_, _) => settings.UseOwnerFilter = false;
        if (owners.Count == 0)
            target.Children.Add(new TextBlock { Text = "Nenhum responsável aparece nos cartões deste quadro.", Foreground = MutedBrush, FontSize = 10 });
        foreach (var owner in owners)
        {
            var check = new CheckBox { Content = owner.Name, IsChecked = !settings.UseOwnerFilter || settings.OwnerIds.Count == 0 || settings.OwnerIds.Contains(owner.Id), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(8, 2, 0, 2) };
            check.Checked += (_, _) => settings.OwnerIds.AddUnique(owner.Id);
            check.Unchecked += (_, _) => settings.OwnerIds.Remove(owner.Id);
            target.Children.Add(check);
        }
    }

    private BusinessmapApiClient CreateClient()
        => new(_accountUrl.Text, _apiKey.Password);

    private void EnsureBoardSelection(BusinessmapBoard board)
    {
        if (_settings.Boards.Any(item => item.BoardId == board.Id)) return;
        _settings.Boards.Add(new BusinessmapBoardSettings { BoardId = board.Id, BoardName = board.Name });
    }

    private void RemoveBoardSelection(string boardId)
        => _settings.Boards.RemoveAll(item => item.BoardId == boardId);

    private void SaveSettings()
    {
        _settings.Enabled = _enabled.IsChecked == true;
        _settings.AccountUrl = _accountUrl.Text.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(_apiKey.Password))
        {
            try { _settings.ProtectedApiKey = WorkflowVariableProtection.Protect(_apiKey.Password); }
            catch (Exception exception) when (exception is CryptographicException or DllNotFoundException)
            {
                _status.Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 135));
                _status.Text = $"Não foi possível proteger a chave para este usuário do Windows: {exception.Message}";
                return;
            }
        }
        if (_settings.Enabled && (_settings.AccountUrl.Length == 0 || _settings.ProtectedApiKey.Length == 0 || _settings.Boards.Count == 0))
        {
            _status.Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 135));
            _status.Text = "Para sincronizar, informe a URL e a chave, conecte-se e selecione pelo menos um quadro.";
            return;
        }
        DialogResult = true;
        Close();
    }

    private static string UnprotectApiKey(string protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey)) return string.Empty;
        try { return WorkflowVariableProtection.Unprotect(protectedKey); }
        catch (Exception exception) when (exception is CryptographicException or FormatException or DllNotFoundException) { return string.Empty; }
    }

    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(150, 155, 167));
    private static TextBlock Label(string text) => new() { Text = text, Foreground = MutedBrush, FontSize = 11 };
    private static void StyleBox(TextBox box) { box.Padding = new Thickness(9); box.FontSize = 13; box.Foreground = Brushes.White; box.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48)); box.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)); }
    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Margin = new Thickness(8, 0, 0, 0),
        Background = primary ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(Color.FromRgb(49, 53, 63)),
        Foreground = primary ? new SolidColorBrush(Color.FromRgb(23, 25, 31)) : Brushes.White,
        BorderThickness = new Thickness(0)
    };
}

internal static class BusinessmapCollectionExtensions
{
    public static void AddUnique(this List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
    }
}
