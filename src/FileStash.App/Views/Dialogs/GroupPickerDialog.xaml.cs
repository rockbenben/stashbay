using System.Windows;
using FileStash.Core.Models;

namespace FileStash.App.Views.Dialogs;

/// <summary>选择目标组。</summary>
public partial class GroupPickerDialog : Window
{
    public GroupPickerDialog()
    {
        InitializeComponent();
    }

    public void SetGroups(IReadOnlyList<StashGroup> groups, string currentGroupId)
    {
        GroupBox.ItemsSource = groups;
        GroupBox.SelectedValue = currentGroupId;
    }

    public string? SelectedGroupId => GroupBox.SelectedValue as string;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (SelectedGroupId is null)
        {
            return;
        }

        DialogResult = true;
    }
}
