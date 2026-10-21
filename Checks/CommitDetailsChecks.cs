using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.Models;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunCommitDetailsChecks(string repository)
    {
        var commits = Git.ReadCommitsAsync(repository, "HEAD", 30).GetAwaiter().GetResult();
        Check(commits.Count > 1, "真实历史可解析多条提交");
        foreach (var commit in commits.Take(5))
        {
            var actual = Runner.RunAsync(repository, ["show", "-s", "--format=%B%x00%P", commit.Hash]).GetAwaiter().GetResult().Output.Split('\0');
            Check(commit.FullMessage == actual[0].TrimEnd('\r', '\n') && commit.Parents == actual[1].TrimEnd('\r', '\n'), "完整说明和父提交与 Git 一致");
        }
        var detailed = commits.First(item => item.FullMessage.Contains('\n'));
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/GitManager;component/Themes/VersionTemplates.xaml", UriKind.Relative) });
        var template = (DataTemplate)app.Resources["CommitRowTemplate"];
        var list = new ListBox { Width = 300, Height = 240, ItemTemplate = template, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.Items.Add(detailed);
        list.Items.Add(commits.First(item => item.Hash != detailed.Hash));
        var window = new Window { Content = list, Width = 320, Height = 280, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        window.Show();
        list.Measure(new Size(300, 240));
        list.Arrange(new Rect(0, 0, 300, 240));
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        TextBlock text = FindCommitText(row, detailed.Subject) ?? throw new InvalidOperationException("未显示提交标题。");
        list.SelectedIndex = 0;
        list.UpdateLayout();
        Check(text.Text == detailed.FullMessage && text.TextWrapping == TextWrapping.Wrap, "选中提交后展开完整多行说明");
        list.SelectedIndex = 1;
        list.UpdateLayout();
        Check(text.Text == detailed.Subject, "切换提交后收起上一条说明");
        window.Close();
        app.Shutdown();
        Console.WriteLine("提交说明检查通过。");
    }

    private static TextBlock? FindCommitText(DependencyObject parent, string subject)
    {
        if (parent is TextBlock text && text.Text == subject) return text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindCommitText(VisualTreeHelper.GetChild(parent, index), subject) is { } found) return found;
        return null;
    }
}
