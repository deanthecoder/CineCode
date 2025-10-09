using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CineCode.Views;

public partial class RecentMediaDialog : Window
{
    private readonly List<RecentMediaItem> m_items;

    public RecentMediaDialog()
        : this(Enumerable.Empty<string>())
    {
    }

    public RecentMediaDialog(IEnumerable<string> recentEntries)
    {
        InitializeComponent();

        m_items = recentEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(RecentMediaItem.TryCreate)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        ApplyFilter();
        Opened += (_, _) => FocusFilterTextBox();
    }

    private void PlayButton_Click(object? sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void RecentMediaList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void RecentMediaList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            ConfirmSelection();
        }
    }

    private void RecentMediaList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdatePlayButtonState();
    }

    private void FilterTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void FilterTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when RecentMediaList is { } list && list.ItemCount > 0:
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
        if (RecentMediaList is null)
        {
            return;
        }

        var filter = (FilterTextBox?.Text ?? string.Empty).Trim();
        var comparison = StringComparison.OrdinalIgnoreCase;

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? m_items
            : m_items
                .Where(item =>
                    item.DisplayName.Contains(filter, comparison) ||
                    item.MediaId.Contains(filter, comparison))
                .ToList();

        var previousSelection = RecentMediaList.SelectedItem as RecentMediaItem;
        RecentMediaList.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            RecentMediaList.SelectedIndex = -1;
        }
        else if (previousSelection is not null)
        {
            var index = filtered.FindIndex(item => item == previousSelection);
            RecentMediaList.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            RecentMediaList.SelectedIndex = 0;
        }

        UpdatePlayButtonState();
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
        if (RecentMediaList.SelectedItem is RecentMediaItem item)
        {
            Close(item);
        }
    }

    private void UpdatePlayButtonState()
    {
        if (PlayButton is null)
        {
            return;
        }

        PlayButton.IsEnabled = RecentMediaList?.SelectedItem is RecentMediaItem;
    }
}

internal sealed record RecentMediaItem(string MediaId, string DisplayName)
{
    public static RecentMediaItem? TryCreate(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        var separator = entry.IndexOf('|');
        string mediaId;
        string displayName;

        if (separator < 0)
        {
            mediaId = entry.Trim();
            displayName = mediaId;
        }
        else
        {
            mediaId = entry[..separator].Trim();
            displayName = entry[(separator + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = mediaId;
        }

        return new RecentMediaItem(mediaId, displayName);
    }
}
