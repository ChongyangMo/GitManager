using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GitManager.Views;

public sealed class FormField(string label, string value = "", string picker = "") : GitManager.ViewModels.ObservableObject
{
    private string _value = value;
    public string Label { get; } = label;
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Picker { get; } = picker;
    public bool HasPicker => Picker.Length > 0;
}
public sealed record ThemeChoice(string Label, string Value);

public partial class FormWindow : Window
{
    public IReadOnlyList<FormField> Fields { get; }
    public List<ThemeChoice> Themes { get; } = [new("跟随系统", "System"), new("浅色", "Light"), new("深色", "Dark")];
    public string Theme => ThemeBox.SelectedValue as string ?? "System";
    public Func<string?>? Validate { get; set; }
    public FormWindow(string title, params FormField[] fields)
    {
        Fields = fields;
        InitializeComponent();
        Title = title;
        DataContext = this;
        Loaded += (_, _) => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }
    public void ShowTheme(string theme) { ThemePanel.Visibility = Visibility.Visible; ThemeBox.SelectedValue = theme; }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        string? error = Fields.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Value)) is { } missing ? "请填写" + missing.Label + "。" : Validate?.Invoke();
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
