using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace JMD.App;

internal sealed class WorkflowVariablesWindow : Window
{
    private readonly List<WorkflowVariableEntry> _variables;
    private readonly StackPanel _items = new();
    public List<WorkflowVariableEntry> Variables => _variables.Select(Clone).ToList();

    public WorkflowVariablesWindow(IEnumerable<WorkflowVariableEntry> variables)
    {
        _variables = variables.Select(Clone).ToList();
        Title = "Variáveis dos workflows";
        Width = 560;
        Height = 600;
        MinWidth = 460;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31));
        Topmost = true;

        var outer = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(18) };
        Content = outer;
        var layout = new DockPanel();
        outer.Child = layout;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MakeButton("Salvar variáveis", true);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => { DialogResult = true; Close(); };
        footer.Children.Add(cancel);
        footer.Children.Add(save);

        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Variáveis globais", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Use {{nome}} no texto de uma etapa. Segredos são protegidos pelo Windows para esta conta.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        var add = MakeButton("+ Nova variável", false);
        add.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 0, 0, 8);
        add.Click += (_, _) => EditVariable(null);
        header.Children.Add(add);

        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _items });
        RenderItems();
    }

    private void RenderItems()
    {
        _items.Children.Clear();
        foreach (var variable in _variables.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock { Text = variable.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            details.Children.Add(new TextBlock { Text = variable.IsSecret ? "Segredo · protegido" : "Texto", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10 });
            row.Children.Add(details);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = MakeButton("Editar", false);
            edit.Margin = new Thickness(4, 0, 0, 0);
            edit.Click += (_, _) => EditVariable(variable);
            var remove = MakeButton("Remover", false);
            remove.Margin = new Thickness(4, 0, 0, 0);
            remove.Click += (_, _) => { _variables.Remove(variable); RenderItems(); };
            actions.Children.Add(edit);
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            _items.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = row });
        }
        if (_variables.Count == 0)
            _items.Children.Add(new TextBlock { Text = "Nenhuma variável cadastrada.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), Margin = new Thickness(2, 8, 2, 0) });
    }

    private void EditVariable(WorkflowVariableEntry? existing)
    {
        string initialValue;
        try { initialValue = existing is null ? string.Empty : existing.IsSecret ? WorkflowVariableProtection.Unprotect(existing.ProtectedValue) : existing.Value; }
        catch (Exception exception) when (exception is CryptographicException or FormatException or DllNotFoundException)
        {
            MessageBox.Show(this, $"Não foi possível descriptografar essa variável: {exception.Message}", "Falha ao abrir segredo", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var dialog = new WorkflowVariableEditWindow(existing?.Name ?? string.Empty, initialValue, existing?.IsSecret == true);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        var name = dialog.VariableName.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,39}$"))
        {
            MessageBox.Show(this, "Use um nome começando por letra e contendo apenas letras, números ou sublinhado.", "Nome inválido", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_variables.Any(item => !ReferenceEquals(item, existing) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Já existe uma variável com esse nome.", "Nome duplicado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var value = dialog.VariableValue;
        if (value.Length == 0)
        {
            MessageBox.Show(this, "Informe um valor para a variável.", "Valor vazio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var isSecret = dialog.IsSecret;
        var protectedValue = string.Empty;
        try
        {
            if (isSecret) protectedValue = WorkflowVariableProtection.Protect(value);
        }
        catch (Exception exception) when (exception is CryptographicException or DllNotFoundException)
        {
            MessageBox.Show(this, $"Não foi possível proteger essa variável: {exception.Message}", "Falha ao proteger segredo", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var entry = existing ?? new WorkflowVariableEntry();
        entry.Name = name;
        entry.IsSecret = isSecret;
        entry.Value = isSecret ? string.Empty : value;
        entry.ProtectedValue = protectedValue;
        if (existing is null) _variables.Add(entry);
        RenderItems();
    }

    private static WorkflowVariableEntry Clone(WorkflowVariableEntry entry) => new() { Name = entry.Name, Value = entry.Value, ProtectedValue = entry.ProtectedValue, IsSecret = entry.IsSecret };

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(11, 6, 11, 6),
        Background = primary ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(Color.FromRgb(42, 45, 53)),
        Foreground = primary ? new SolidColorBrush(Color.FromRgb(23, 25, 31)) : Brushes.White,
        BorderThickness = new Thickness(0)
    };
}

internal sealed class WorkflowVariableEditWindow : Window
{
    private readonly TextBox _name = new() { Height = 36, Padding = new Thickness(9, 6, 9, 6), FontSize = 12 };
    private readonly TextBox _plainValue = new() { Height = 40, Padding = new Thickness(9, 6, 9, 6), FontSize = 12 };
    private readonly PasswordBox _secretValue = new() { Height = 40, Padding = new Thickness(9, 6, 9, 6), FontSize = 12 };
    private readonly CheckBox _secret = new() { Content = "Tratar como segredo (ex.: senha)", Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
    public string VariableName => _name.Text;
    public string VariableValue => _secret.IsChecked == true ? _secretValue.Password : _plainValue.Text;
    public bool IsSecret => _secret.IsChecked == true;

    public WorkflowVariableEditWindow(string name, string value, bool isSecret)
    {
        Title = "Editar variável";
        Width = 420;
        Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31));
        Topmost = true;
        _name.Text = name;
        _plainValue.Text = value;
        _secretValue.Password = value;
        _secretValue.Foreground = Brushes.White;
        _secretValue.Background = new SolidColorBrush(Color.FromRgb(24, 26, 32));
        _secretValue.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81));
        _secret.IsChecked = isSecret;
        var outer = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(18) };
        Content = outer;
        var layout = new DockPanel();
        outer.Child = layout;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MakeButton("Salvar", true);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => { DialogResult = true; Close(); };
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        var form = new StackPanel();
        layout.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "Variável global", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        AddField(form, "Nome", _name);
        _secret.Checked += (_, _) => UpdateVisibility();
        _secret.Unchecked += (_, _) => UpdateVisibility();
        form.Children.Add(_secret);
        form.Children.Add(new TextBlock { Text = "Valor", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 11, Margin = new Thickness(2, 10, 2, 3) });
        form.Children.Add(_plainValue);
        form.Children.Add(_secretValue);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        _plainValue.Visibility = _secret.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        _secretValue.Visibility = _secret.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AddField(Panel parent, string label, TextBox box)
    {
        box.Foreground = Brushes.White;
        box.Background = new SolidColorBrush(Color.FromRgb(24, 26, 32));
        box.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81));
        var field = new StackPanel();
        field.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 11, Margin = new Thickness(2, 0, 2, 3) });
        field.Children.Add(box);
        parent.Children.Add(field);
    }

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(12, 7, 12, 7),
        Background = primary ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(Color.FromRgb(42, 45, 53)),
        Foreground = primary ? new SolidColorBrush(Color.FromRgb(23, 25, 31)) : Brushes.White,
        BorderThickness = new Thickness(0)
    };
}
