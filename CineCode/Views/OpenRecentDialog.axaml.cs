using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CineCode.Views;

public partial class OpenRecentDialog : Window
{
    private readonly List<RecentFileItem> m_items;

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

        ApplyFilter();
        Opened += (_, _) => FocusFilterTextBox();
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

    private void FilterTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void FilterTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when RecentFilesList is { } list && list.ItemCount > 0:
                e.Handled = true;
                list.Focus();
                if (list.SelectedIndex < 0)
                {
                    list.SelectedIndex = 0;
                }
                break;
            case Key.Enter or Key.Return:
                e.Handled = true;
                ConfirmSelection();
                break;
            case Key.Escape when FilterTextBox is { Text.Length: > 0 } filter:
                e.Handled = true;
                filter.Text = string.Empty;
                break;
        }
    }

    private void ApplyFilter()
    {
        if (RecentFilesList is null)
        {
            return;
        }

        var filter = (FilterTextBox?.Text ?? string.Empty).Trim();
        var comparison = StringComparison.OrdinalIgnoreCase;

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? m_items
            : m_items
                .Where(item =>
                    item.FileName.Contains(filter, comparison) ||
                    item.Directory.Contains(filter, comparison))
                .ToList();

        var previousSelection = RecentFilesList.SelectedItem as RecentFileItem;
        RecentFilesList.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            RecentFilesList.SelectedIndex = -1;
        }
        else if (previousSelection is not null)
        {
            var index = filtered.FindIndex(item => item == previousSelection);
            RecentFilesList.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            RecentFilesList.SelectedIndex = 0;
        }

        UpdateOpenButtonState();
    }

    private void FocusFilterTextBox()
    {
        if (FilterTextBox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (FilterTextBox is null)
            {
                return;
            }

            FilterTextBox.Focus();
            FilterTextBox.SelectAll();
        }, DispatcherPriority.Input);
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
