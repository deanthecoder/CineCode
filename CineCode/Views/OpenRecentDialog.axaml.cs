using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CineCode.Views;

public partial class OpenRecentDialog : Window
{
    private readonly IReadOnlyList<RecentFileItem> m_items;

    public OpenRecentDialog()
        : this(Enumerable.Empty<string>())
    {
    }

    public OpenRecentDialog(IEnumerable<string> recentFiles)
    {
        InitializeComponent();

        m_items = recentFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(RecentFileItem.TryCreate)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        RecentFilesList.ItemsSource = m_items;
        if (m_items.Count > 0)
        {
            RecentFilesList.SelectedIndex = 0;
        }

        UpdateOpenButtonState();
    }

    private void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void RecentFilesList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void RecentFilesList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            ConfirmSelection();
        }
    }

    private void RecentFilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateOpenButtonState();
    }

    private void ConfirmSelection()
    {
        if (RecentFilesList.SelectedItem is RecentFileItem item)
        {
            Close(item.FullPath);
        }
    }

    private void UpdateOpenButtonState()
    {
        if (OpenButton is null)
        {
            return;
        }

        OpenButton.IsEnabled = RecentFilesList?.SelectedItem is RecentFileItem;
    }

}

internal sealed record RecentFileItem(string FullPath, string FileName, string Directory)
{
    public static RecentFileItem? TryCreate(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            return new RecentFileItem(fullPath, fileName, directory);
        }
        catch
        {
            return null;
        }
    }
}
