using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GitManager.Views;

public sealed class FormField(string label, string value = "", string picker = "", bool required = true, bool secret = false) : GitManager.ViewModels.ObservableObject
{
    private string _value = value;
    public string Label { get; } = label;
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Picker { get; } = picker;
    public bool HasPicker => Picker.Length > 0;
    public bool Required { get; } = required;
    public bool IsSecret { get; } = secret;
}
public sealed record ThemeChoice(string Label, string Value);

public partial class FormWindow : Window
{
    public IReadOnlyList<FormField> Fields { get; }
    public List<ThemeChoice> Themes { get; } = [new("跟随系统", "System"), new("白天模式", "Light"), new("黑夜模式", "Dark")];
    public string Theme => ThemeBox.SelectedValue as string ?? "System";
    public Func<string?>? Validate { get; set; }
    public FormWindow(string title, params FormField[] fields)
    {
        Fields = fields;
        InitializeComponent();
        GitManager.Services.ThemeService.Attach(this);
        Title = title;
        DataContext = this;
        Loaded += (_, _) => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }
    public void ShowTheme(string theme) { ThemePanel.Visibility = Visibility.Visible; ThemeBox.SelectedValue = theme; }
    public bool OptionValue => OptionBox.IsChecked == true;
    public void ShowOption(string label, bool value) { OptionBox.Content = label; OptionBox.IsChecked = value; OptionBox.Visibility = Visibility.Visible; }
    public void ShowAction(string label, Action action)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("QuietButton") };
        button.Click += (_, _) => action();
        ActionPanel.Children.Add(button);
    }
    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: FormField field } password) field.Value = password.Password;
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        string? error = Fields.FirstOrDefault(field => field.Required && string.IsNullOrWhiteSpace(field.Value)) is { } missing ? "请填写" + missing.Label + "。" : Validate?.Invoke();
        if (error is not null) { ErrorText.Text = error; return; }
        DialogResult = true;
    }
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FormField field }) return;
        if (field.Picker == "folder")
        {
            var dialog = new OpenFolderDialog { Title = field.Label };
            if (dialog.ShowDialog(this) == true) field.Value = dialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = field.Label, Filter = "Git 程序|git.exe|可执行文件|*.exe" };
            if (dialog.ShowDialog(this) == true) field.Value = dialog.FileName;
        }
    }
}
