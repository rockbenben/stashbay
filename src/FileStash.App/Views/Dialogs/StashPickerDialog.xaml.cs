using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.App.Services;

namespace FileStash.App.Views.Dialogs;

public partial class StashPickerDialog : Window
{
    private sealed record Entry(string Path, string Name, bool IsDirectory)
    {
        public ImageSource? Icon => ShellIconService.GetIcon(Path, IsDirectory);
        public override string ToString() => Name;
    }

    private CancellationTokenSource? _navigation;

    public StashPickerDialog(string? initialDirectory)
    {
        InitializeComponent();
        WindowEffects.FitToWorkArea(this);
        WindowEffects.EnableAntiFlash(this);
        Loaded += async (_, _) =>
        {
            await NavigateAsync(initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (Entries.ItemsSource == null && IsVisible) await NavigateAsync(null);
        };
        Closed += (_, _) => _navigation?.Cancel();
    }

    public string? CurrentDirectory { get; private set; }
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    // null represents the drive list. Enumerate off the dispatcher so a slow share
    // can be left or cancelled without blocking the rest of the dialog.
    private async Task NavigateAsync(string? address)
    {
        _navigation?.Cancel();
        using var request = new CancellationTokenSource();
        _navigation = request;
        Entries.UnselectAll();
        Entries.IsEnabled = false;
        AddSelection.IsEnabled = false;
        Hint.Text = Strings.StatusBusy;
        try
        {
            var path = string.IsNullOrWhiteSpace(address) ? null :
                Path.GetFullPath(address, CurrentDirectory ?? Environment.CurrentDirectory);
            var result = await Task.Run(() =>
            {
                var file = File.Exists(path) ? path : null;
                var folder = file == null ? path : Path.GetDirectoryName(file);
                var entries = ReadEntries(folder, request.Token);
                return (folder, entries, file);
            }, request.Token);
            if (request.IsCancellationRequested) return;

            CurrentDirectory = result.folder;
            Address.Text = CurrentDirectory ?? string.Empty;
            Entries.ItemsSource = result.entries;
            if (result.file is not null)
            {
                Entries.SelectedItem = result.entries.FirstOrDefault(item =>
                    StringComparer.OrdinalIgnoreCase.Equals(item.Path, result.file));
                if (Entries.SelectedItem != null) Entries.ScrollIntoView(Entries.SelectedItem);
            }
            Hint.Text = Strings.PickItemsHint;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!request.IsCancellationRequested)
            {
                Address.Text = CurrentDirectory ?? string.Empty;
                Hint.Text = ex.Message;
            }
        }
        finally
        {
            if (!request.IsCancellationRequested)
            {
                _navigation = null;
                Entries.IsEnabled = true;
                UpButton.IsEnabled = CurrentDirectory != null;
                UpdateAddButton();
            }
        }
    }

    private static Entry[] ReadEntries(string? folder, CancellationToken token)
    {
        if (folder == null)
            return Directory.GetLogicalDrives().Select(drive => new Entry(drive, drive, true)).ToArray();

        var entries = new List<Entry>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            entries.Add(new Entry(info.FullName, info.Name, info is DirectoryInfo));
        }
        return entries.OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async void OnUp(object sender, RoutedEventArgs e) =>
        await NavigateAsync(CurrentDirectory == null ? null : Directory.GetParent(CurrentDirectory)?.FullName);

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Address.Focus();
            Address.SelectAll();
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None && Entries.IsKeyboardFocusWithin &&
                 Entries.SelectedItems.Count == 1 && Entries.SelectedItem is Entry { IsDirectory: true } directory)
        {
            e.Handled = true;
            await NavigateAsync(directory.Path);
            Entries.Focus();
        }
    }

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateAsync(Address.Text.Trim().Trim('"'));
        Entries.Focus();
    }

    private async void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(Entries, source) is not ListBoxItem { Content: Entry entry }) return;
        if (entry.IsDirectory)
            await NavigateAsync(entry.Path);
        else
            ConfirmSelection();
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e) => UpdateAddButton();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAddButton();

    private void UpdateAddButton()
    {
        if (AddSelection != null)
            AddSelection.IsEnabled = Entries.IsEnabled && Entries.SelectedItems.Count > 0 &&
                Address.Text == (CurrentDirectory ?? string.Empty);
    }

    private void OnAdd(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (!AddSelection.IsEnabled) return;
        SelectedPaths = Entries.SelectedItems.Cast<Entry>().Select(item => item.Path).ToArray();
        DialogResult = true;
    }
}
