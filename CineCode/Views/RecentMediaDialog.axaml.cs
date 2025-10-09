using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CineCode.Views;

public partial class RecentMediaDialog : Window
{
    private readonly IReadOnlyList<RecentMediaItem> m_items;

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

        RecentMediaList.ItemsSource = m_items;
        if (m_items.Count > 0)
        {
            RecentMediaList.SelectedIndex = 0;
        }

        UpdatePlayButtonState();
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
