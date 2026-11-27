using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using FileStash.App.Resources;
using FileStash.Core.Utils;

namespace FileStash.App.Views;

/// <summary>关于页：版本号、运行时、git 状态、数据目录、GitHub 入口。</summary>
public partial class AboutWindow : Window
{
    private readonly string _fullVersion;

    public AboutWindow()
    {
        InitializeComponent();

        var assembly = Assembly.GetExecutingAssembly();
        _fullVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? Strings.Unknown;

        var shortVersion = _fullVersion.Split('+')[0];
        VersionText.Text = Strings.Format(Strings.VersionLabel, shortVersion);
        VersionText.ToolTip = _fullVersion;

        RuntimeText.Text = Strings.Format(Strings.RuntimeLabel, Environment.Version);

        try
        {
            var process = Environment.ProcessPath;
            var gitAvailable = !string.IsNullOrEmpty(process)
                && File.Exists(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Git",
                    "cmd",
                    "git.exe"));

            GitText.Text = gitAvailable ? Strings.GitFound : Strings.GitMissing;
        }
        catch (IOException)
        {
            GitText.Text = Strings.GitUnknown;
        }

        var paths = new AppPaths();
        DataText.Text = Strings.Format(Strings.DataDirectoryLabel, paths.RootDirectory);
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e)
    {
        if (App.InSmoke) return;
        try
        {
            Clipboard.SetText(_fullVersion);
        }
        catch (Exception)
        {
            // Clipboard may be locked transiently by another app.
        }
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e)
    {
        if (App.InSmoke) return;
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/rockbenben/stashbay") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Ignore shell launch failures when no browser is registered.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
