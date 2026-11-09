using System.IO;
using System.Reflection;
using System.Windows;
using FileStash.App.Resources;
using FileStash.Core.Utils;

namespace FileStash.App.Views;

/// <summary>关于页：版本号、运行时、git 状态、数据目录。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? Strings.Unknown;

        VersionText.Text = Strings.Format(Strings.VersionLabel, informational);

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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
