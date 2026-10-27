using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunRemoteGroupChecks()
    {
        Check(RepositoryRemoteGroup.FromRemote("", "", "") == RepositoryRemoteGroup.LocalOnly, "没有远程归入仅本地");
        Check(RepositoryRemoteGroup.FromRemote("git@github.com:a/b.git", "", "") == RepositoryRemoteGroup.GitHub, "SSH 形式的 GitHub 地址归入 GitHub");
        Check(RepositoryRemoteGroup.FromRemote("https://SDServer.example.com/git/a/b.git", "", "https://sdserver.example.com/") == RepositoryRemoteGroup.ServiceCloud, "与服务云地址同主机的归入服务云");
        Check(RepositoryRemoteGroup.FromRemote("ssh://git@10.18.32.67/team/p.git", "", "") == "10.18.32.67", "其他主机按主机名分组");
        Check(RepositoryRemoteGroup.FromRemote(@"C:\repos\shared.git", "", "") == RepositoryRemoteGroup.LocalPath
            && RepositoryRemoteGroup.FromRemote("file:///C:/repos/shared.git", "", "") == RepositoryRemoteGroup.LocalPath, "本地路径远程归入本地路径");

        string root = CheckDirectories.Create("RemoteGroupChecks");
        string Repo(string name, params string[][] commands)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            Runner.RunAsync(path, ["init", "-q"]).GetAwaiter().GetResult();
            foreach (var command in commands) Runner.RunAsync(path, command).GetAwaiter().GetResult();
            return path;
        }
        string local = Repo("local-a");
        string githubB = Repo("github-b", ["remote", "add", "origin", "https://github.com/example/github-b.git"]);
        string gitlab = Repo("gitlab-d", ["remote", "add", "origin", "git@gitlab.example.com:team/gitlab-d.git"]);
        string cloud = Repo("cloud-c", ["remote", "add", "origin", "https://sdserver.example.com/git/team/cloud-c.git"],
            ["config", "gitmanager.serviceCloudUrl", "https://sdserver.example.com/git/team/cloud-c.git"]);
        string githubE = Repo("github-e", ["remote", "add", "origin", "https://github.com/example/github-e.git"]);
        string upstream = Repo("upstream-f", ["remote", "add", "upstream", "https://github.com/example/upstream-f.git"]);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(Path.Combine(CheckDirectories.Create("Checks"), "settings.json"));
            settings.Value.RepositoryDiscovery.Enabled = false;
            settings.Value.Repositories = [githubB, local, gitlab, cloud, githubE, upstream];
            using var viewModel = new MainViewModel(settings);
            var window = new MainWindow(viewModel) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await viewModel.InitializeAsync(local);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (viewModel.Repositories.Any(item => item.RemoteGroup.Length == 0) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var view = (ListCollectionView)CollectionViewSource.GetDefaultView(viewModel.Repositories);
                string actual = string.Join(" | ", view.Groups.Cast<CollectionViewGroup>().Select(group =>
                    group.Name + "：" + string.Join("、", group.Items.Cast<RepositoryBookmark>().Select(item => Path.GetFileName(item.Path)))));
                Check(actual == "服务云：cloud-c | GitHub：github-b、github-e、upstream-f | gitlab.example.com：gitlab-d | 仅本地：local-a",
                    "导航栏按远程服务分组，组内保持原有顺序：" + actual);
                var list = (ListBox)window.FindName("RepositoryList");
                Check(list.SelectedItem is RepositoryBookmark selected && selected.Path == viewModel.Repository?.Path && viewModel.Repository?.Path == local,
                    "移入分组后仍选中当前仓库");
                SaveScreenshot(window, Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "sidebar-remote-groups.png"));
                await viewModel.SetThemeAsync("Dark");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "sidebar-remote-groups.dark.png"));
                await viewModel.SetThemeAsync("Light");
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("导航栏分组检查失败。", failure);
    }
}
