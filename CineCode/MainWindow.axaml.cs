using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CineCode.Views;
using Material.Icons;
using WebViewCore.Events;

namespace CineCode;

public partial class MainWindow : Window
{
    private const int MaxMruEntries = 50;
    private const int MaxRecentMediaEntries = 25;
    private const string DefaultLoadVideoTooltip = "Load Video";
    private const string InvalidVideoTooltip = "Enter a valid YouTube URL or ID.";
    private const string LoadingVideoTooltip = "Loading video...";
    private const double ToolbarActiveOpacity = 1.0;
    private const double ToolbarDimmedOpacity = 0.35;
    private static readonly TimeSpan HostActivityThrottle = TimeSpan.FromMilliseconds(200);

    private string m_currentFilePath = string.Empty;
    private bool m_isEditorReady;
    private double? m_pendingOpacity;
    private readonly bool m_suppressOpacityUpdate;
    private TaskCompletionSource<string?>? m_pendingContentRequest;
    private (string content, string extension)? m_pendingFile;
    private bool m_isPlaybackPaused;
    private string m_currentMediaId;
    private double m_currentVolume;
    private double? m_pendingVolume;
    private bool m_suppressVolumeChange;
    private readonly Dictionary<string, Func<string, Task>> m_commandHandlers;
    private readonly List<string> m_commandNames;
    private readonly Dictionary<string, IReadOnlyList<string>> m_commandArgumentOptions;
    private bool m_suppressCommandSuggestionUpdate;
    private bool m_isToolbarDimmed;
    private bool m_isEditorDimmed;
    private DateTime m_lastHostActivitySent = DateTime.MinValue;
    
    public MainWindow()
    {
        m_commandHandlers = CreateCommandHandlers();
        m_commandNames = m_commandHandlers.Keys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        m_commandArgumentOptions = CreateCommandArgumentOptions();
        InitializeComponent();
        InitializeWebView();
        SetupEventHandlers();
        TrimMruList();
        TrimRecentMediaList();
        m_suppressOpacityUpdate = true;
        var savedOpacity = Math.Clamp(Settings.Instance.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        OpacitySlider.Value = savedOpacity;
        m_suppressOpacityUpdate = false;
        var savedMediaId = NormalizeMediaId(Settings.Instance.YouTubeVideoId);
        if (string.IsNullOrWhiteSpace(savedMediaId))
            savedMediaId = NormalizeMediaId(YouTubeIdTextBox.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(savedMediaId))
            savedMediaId = NormalizeMediaId(Settings.DefaultYouTubeVideoId);
        
        m_currentMediaId = savedMediaId;
        YouTubeIdTextBox.Text = m_currentMediaId;
        Settings.Instance.YouTubeVideoId = m_currentMediaId;
        var savedVolume = Math.Clamp(Settings.Instance.Volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
        m_currentVolume = savedVolume;
        m_suppressVolumeChange = true;
        VolumeSlider.Value = m_currentVolume;
        m_suppressVolumeChange = false;
        UpdateVolumeIcon();
        UpdatePlayPauseIcon();
        SetLoadVideoButtonTooltip(DefaultLoadVideoTooltip);
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    private List<string> GetSuggestionMatches(CommandSuggestionContext context)
    {
        if (!context.IsArgument)
        {
            var query = context.Query;

            if (query.IndexOf('?', StringComparison.Ordinal) >= 0)
            {
                return m_commandNames.ToList();
            }

            var matches = m_commandNames
                .Where(name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0 && context.IsExplicit && string.IsNullOrEmpty(query))
            {
                matches = m_commandNames.ToList();
            }

            return matches;
        }

        if (!m_commandArgumentOptions.TryGetValue(context.CommandName, out var options))
        {
            return new List<string>();
        }

        var argumentMatches = options
            .Where(option => option.StartsWith(context.Query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (argumentMatches.Count == 0 && string.IsNullOrEmpty(context.Query))
        {
            argumentMatches = options.ToList();
        }

        return argumentMatches;
    }

    private Dictionary<string, Func<string, Task>> CreateCommandHandlers()
    {
        return new Dictionary<string, Func<string, Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["open"] = HandleOpenCommandAsync,
            ["open recent"] = _ => HandleOpenCommandAsync("recent"),
            ["play recent"] = _ => HandlePlayRecentCommandAsync(),
            ["save"] = _ => SaveFileAsync(),
            ["exit"] = _ =>
            {
                RequestApplicationQuit();
                return Task.CompletedTask;
            }
        };
    }

    private static Dictionary<string, IReadOnlyList<string>> CreateCommandArgumentOptions()
    {
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["open"] = new List<string> { "recent" }
        };
    }

    private void InitializeWebView()
    {
        WebViewControl.WebMessageReceived += OnWebViewMessageReceived;
        WebViewControl.WebViewCreated += OnWebViewCreated;
        LoadEditorHtml();
    }

    private void LoadEditorHtml()
    {
        try
        {
            m_isEditorReady = false;
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "CineCode.Assets.editor.html";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var html = reader.ReadToEnd();
                WebViewControl.HtmlContent = html;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading editor HTML: {ex.Message}");
        }
    }

    private void SetupEventHandlers()
    {
        OpacitySlider.PropertyChanged += async (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                var opacity = OpacitySlider.Value;
                if (!m_suppressOpacityUpdate)
                {
                    Settings.Instance.Opacity = opacity;
                }

                await ApplyEditorOpacityAsync(opacity);
            }
        };
    }

    private void OnWebViewCreated(object? sender, WebViewCreatedEventArgs e)
    {
        if (!e.IsSucceed)
        {
            if (!string.IsNullOrEmpty(e.Message))
            {
                Console.WriteLine($"WebView creation failed: {e.Message}");
            }
            return;
        }

        TryEnableThirdPartyCookies();
    }

    private async void OnWebViewMessageReceived(object? sender, WebViewMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Message))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.Message);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "editor-ready":
                    await OnEditorReadyAsync();
                    break;
                case "editor-content":
                    var content = document.RootElement.TryGetProperty("content", out var contentElement)
                        ? contentElement.GetString()
                        : null;
                    m_pendingContentRequest?.TrySetResult(content);
                    m_pendingContentRequest = null;
                    break;
                case "focus-command-palette":
                    FocusCommandPalette();
                    break;
                case "editor-focus":
                    HandleEditorFocus();
                    break;
                case "log":
                    if (document.RootElement.TryGetProperty("message", out var messageElement))
                    {
                        Console.WriteLine($"[WebView] {messageElement.GetString()}");
                    }
                    break;
                case "playback-changed":
                    if (document.RootElement.TryGetProperty("state", out var stateElement))
                    {
                        var state = stateElement.GetString();
                        var paused = string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase);
                        m_isPlaybackPaused = paused;
                        UpdatePlayPauseIcon();
                    }
                    break;
                case "request-quit":
                    Dispatcher.UIThread.Post(RequestApplicationQuit);
                    break;
                case "request-open":
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            _ = OpenFileAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error opening file: {ex.Message}");
                        }
                    });
                    break;
                case "request-paste":
                    Dispatcher.UIThread.Post(HandlePasteRequestedAsync);
                    break;
                case "player-error":
                    if (document.RootElement.TryGetProperty("code", out var errorCodeElement))
                    {
                        var errorCode = errorCodeElement.GetInt32();
                        Console.WriteLine($"[YouTube] Player error {errorCode}");
                        SetLoadVideoButtonTooltip(GetPlayerErrorTooltip(errorCode));
                    }
                    break;
                case "video-metadata":
                    HandleVideoMetadata(document.RootElement);
                    break;
                case "editor-dimmed":
                    if (document.RootElement.TryGetProperty("dimmed", out var dimmedElement))
                    {
                        var dimmed = dimmedElement.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => bool.TryParse(dimmedElement.GetString(), out var parsed) && parsed,
                            JsonValueKind.Number => Math.Abs(dimmedElement.GetDouble()) > double.Epsilon,
                            _ => false
                        };
                        HandleEditorDimmedChange(dimmed);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing web message: {ex.Message}");
        }
    }

    private async Task OnEditorReadyAsync()
    {
        m_isEditorReady = true;

        if (m_pendingFile is { } pendingFile)
        {
            SendWebViewMessage(new
            {
                type = "load-code",
                pendingFile.content,
                pendingFile.extension
            });
            m_pendingFile = null;
        }

        var opacity = m_pendingOpacity ?? OpacitySlider.Value;
        m_pendingOpacity = null;

        await ApplyEditorOpacityAsync(opacity);

        SendWebViewMessage(new
        {
            type = "load-video",
            videoId = m_currentMediaId,
            autoplay = true
        });
        m_isPlaybackPaused = false;
        UpdatePlayPauseIcon();
        SetLoadVideoButtonTooltip(LoadingVideoTooltip);

        if (m_pendingVolume.HasValue)
        {
            m_currentVolume = Math.Clamp(m_pendingVolume.Value, 0, 1);
            m_suppressVolumeChange = true;
            VolumeSlider.Value = m_currentVolume;
            m_suppressVolumeChange = false;
            m_pendingVolume = null;
            UpdateVolumeIcon();
        }

        ApplyVolumeToWebView();
        TryEnableThirdPartyCookies();
    }

    private Task ApplyEditorOpacityAsync(double opacity)
    {
        if (!m_isEditorReady)
        {
            m_pendingOpacity = opacity;
            return Task.CompletedTask;
        }

        try
        {
            SendWebViewMessage(new { type = "set-opacity", value = opacity });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting editor opacity: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void SendWebViewMessage(object payload)
    {
        try
        {
            var message = JsonSerializer.Serialize(payload);
            var sent = WebViewControl.PostWebMessageAsString(message, null);
            if (!sent)
            {
                Console.WriteLine($"WebView rejected message: {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending message to WebView: {ex.Message}");
        }
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        await OpenFileAsync();
    }

    private async Task OpenFileAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Code File",
            AllowMultiple = false
        });

        if (files.Count >= 1)
        {
            var file = files[0];
            var path = file.Path.LocalPath;

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            ApplyLoadedFile(content, path);
        }
    }

    private Task HandleOpenCommandAsync(string argument)
    {
        if (string.Equals(argument, "recent", StringComparison.OrdinalIgnoreCase))
        {
            return OpenRecentAsync();
        }

        return OpenFileAsync();
    }

    private async Task OpenRecentAsync()
    {
        TrimMruList();
        var recentFiles = Settings.Instance.MruFiles;

        if (recentFiles.Count == 0)
        {
            SetLoadVideoButtonTooltip("No recent files found.");
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        var dialog = new OpenRecentDialog(recentFiles);
        var selectedPath = await dialog.ShowDialog<string?>(this);

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        await LoadFileFromPathAsync(selectedPath);
    }

    private async Task HandlePlayRecentCommandAsync()
    {
        TrimRecentMediaList();
        var recentMedia = Settings.Instance.RecentYouTubeItems;

        if (recentMedia.Count == 0)
        {
            SetLoadVideoButtonTooltip("No recent videos found.");
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        var dialog = new RecentMediaDialog(recentMedia);
        var selection = await dialog.ShowDialog<RecentMediaItem?>(this);

        if (selection is null || string.IsNullOrWhiteSpace(selection.MediaId))
        {
            return;
        }

        LoadMedia(selection.MediaId, selection.DisplayName);
    }

    private async Task LoadFileFromPathAsync(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath))
            {
                SetLoadVideoButtonTooltip($"File not found: {Path.GetFileName(fullPath)}");
                return;
            }

            var content = await File.ReadAllTextAsync(fullPath);
            ApplyLoadedFile(content, fullPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening recent file '{path}': {ex.Message}");
            SetLoadVideoButtonTooltip("Failed to open recent file.");
        }
    }

    private void ApplyLoadedFile(string content, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        m_currentFilePath = filePath;
        var extension = Path.GetExtension(m_currentFilePath).TrimStart('.');

        if (m_isEditorReady)
        {
            SendWebViewMessage(new { type = "load-code", content, extension });
        }
        else
        {
            m_pendingFile = (content, extension);
        }

        UpdateMruList(m_currentFilePath);
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!m_isEditorReady)
        {
            return;
        }

        SendWebViewMessage(new { type = "toggle-playback" });
    }

    private void RewindButton_Click(object? sender, RoutedEventArgs e)
    {
        SeekVideo(-10);
    }

    private void ForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        SeekVideo(10);
    }

    private void SeekVideo(double offsetSeconds)
    {
        if (!m_isEditorReady)
        {
            return;
        }

        SendWebViewMessage(new
        {
            type = "seek-video",
            offset = offsetSeconds
        });
    }

    private void UpdatePlayPauseIcon()
    {
        if (PlayPauseIcon is { })
        {
            PlayPauseIcon.Kind = m_isPlaybackPaused ? MaterialIconKind.PlayCircleOutline : MaterialIconKind.PauseCircleOutline;
        }
    }

    private void UpdateVolumeIcon()
    {
        if (VolumeIcon is null)
            return;

        var isMuted = m_currentVolume <= 0.0001;
        var isQuiet = m_currentVolume <= 0.5;
        if (isMuted)
            VolumeIcon.Kind = MaterialIconKind.VolumeMute;
        else if (isQuiet)
            VolumeIcon.Kind = MaterialIconKind.VolumeMedium;
        else
            VolumeIcon.Kind = MaterialIconKind.VolumeHigh;
    }

    private void LoadVideoButton_Click(object? sender, RoutedEventArgs e)
    {
        TryLoadVideoFromInput();
    }

    private async void YouTubeIdTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (CommandSuggestionsPopup?.IsOpen == true && CommandSuggestionsListBox is { } suggestionList)
        {
            var normalizedKey = e.Key == Key.Return ? Key.Enter : e.Key;

            switch (normalizedKey)
            {
                case Key.Down:
                    e.Handled = true;
                    MoveCommandSuggestionSelection(1);
                    return;
                case Key.Up:
                    e.Handled = true;
                    MoveCommandSuggestionSelection(-1);
                    return;
                case Key.Tab:
                    if (suggestionList.SelectedItem is string tabSelection)
                    {
                        e.Handled = true;
                        AcceptCommandSuggestion(tabSelection);
                    }
                    return;
                case Key.Enter:
                    if (suggestionList.SelectedItem is string enterSelection
                        && !IsCurrentCommandSelectionComplete(enterSelection))
                    {
                        e.Handled = true;
                        AcceptCommandSuggestion(enterSelection);
                        return;
                    }
                    break;
                case Key.Escape:
                    e.Handled = true;
                    CloseCommandSuggestions();
                    return;
            }
        }

        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            CloseCommandSuggestions();
            var input = YouTubeIdTextBox.Text ?? string.Empty;

            var commandResult = await TryExecuteCommandAsync(input);
            if (commandResult == CommandExecutionResult.Executed)
            {
                YouTubeIdTextBox.Text = string.Empty;
                SetLoadVideoButtonTooltip(DefaultLoadVideoTooltip);
                return;
            }

            if (commandResult == CommandExecutionResult.UnknownCommand)
            {
                return;
            }

            TryLoadVideoFromInput();
        }
    }

    private void YouTubeIdTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCommandSuggestions();
    }

    private void YouTubeIdTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        CloseCommandSuggestions();
    }

    private void CommandSuggestionsListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (CommandSuggestionsListBox?.SelectedItem is string selected)
        {
            AcceptCommandSuggestion(selected);
            YouTubeIdTextBox?.Focus();
            e.Handled = true;
        }
    }

    private void TryLoadVideoFromInput()
    {
        var rawId = YouTubeIdTextBox.Text ?? string.Empty;
        LoadMedia(rawId);
    }

    private void LoadMedia(string mediaId, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            SetLoadVideoButtonTooltip(InvalidVideoTooltip);
            return;
        }

        var normalizedId = NormalizeMediaId(mediaId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            SetLoadVideoButtonTooltip(InvalidVideoTooltip);
            return;
        }

        m_currentMediaId = normalizedId;

        if (YouTubeIdTextBox is { } textBox)
        {
            textBox.Text = m_currentMediaId;
        }

        Settings.Instance.YouTubeVideoId = m_currentMediaId;

        UpdateRecentMediaList(m_currentMediaId, displayName);

        m_isPlaybackPaused = false;
        UpdatePlayPauseIcon();
        SetLoadVideoButtonTooltip(LoadingVideoTooltip);

        if (m_isEditorReady)
        {
            SendWebViewMessage(new
            {
                type = "load-video",
                videoId = m_currentMediaId,
                autoplay = true
            });
            TryEnableThirdPartyCookies();
        }

        ApplyVolumeToWebView();
    }

    private static bool IsPlaylistId(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        // Common playlist/feed prefixes: PL (playlist), UU (uploads), OL (mixes/other lists)
        return s.StartsWith("PL", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("UU", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("OL", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMediaId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                var path = uri.AbsolutePath.Trim('/');
                return SanitizeId(path);
            }

            if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer playlist if present
                var list = TryGetQueryParameter(uri.Query, "list");
                if (!string.IsNullOrWhiteSpace(list))
                {
                    return SanitizeId(list);
                }

                var vid = TryGetQueryParameter(uri.Query, "v");
                if (!string.IsNullOrWhiteSpace(vid))
                {
                    return SanitizeId(vid);
                }

                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    if (string.Equals(segments[0], "embed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(segments[0], "shorts", StringComparison.OrdinalIgnoreCase))
                    {
                        return SanitizeId(segments[1]);
                    }
                }
            }
        }

        return SanitizeId(trimmed);
    }

    private static string SanitizeId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // Keep only ID-safe chars
        return new string(input.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
    }

    private void HandleVideoMetadata(JsonElement element)
    {
        var incomingId = element.TryGetProperty("videoId", out var videoIdElement)
            ? NormalizeMediaId(videoIdElement.GetString() ?? string.Empty)
            : string.Empty;

        var currentId = NormalizeMediaId(m_currentMediaId);
        var currentIsPlaylist = IsPlaylistId(currentId);

        // If we're on a playlist, accept the metadata of the currently playing item.
        // If we're on a single video, ensure IDs match.
        if (!currentIsPlaylist && !string.IsNullOrWhiteSpace(incomingId)
            && !string.Equals(incomingId, currentId, StringComparison.Ordinal))
        {
            return;
        }

        var title = element.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? string.Empty
            : string.Empty;

        var targetId = currentIsPlaylist ? currentId : incomingId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            targetId = currentId;
        }

        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var displayName = !string.IsNullOrWhiteSpace(title)
                ? currentIsPlaylist ? $"{title} (playlist)" : title
                : null;

            UpdateRecentMediaList(targetId, displayName);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            SetLoadVideoButtonTooltip($"Video: {title}");
        }
    }

    private void SetLoadVideoButtonTooltip(string? message)
    {
        var tooltipText = string.IsNullOrWhiteSpace(message) ? DefaultLoadVideoTooltip : message;

        void ApplyTooltip()
        {
            ToolTip.SetTip(LoadVideoButton, tooltipText);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyTooltip();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyTooltip);
        }
    }

    private static string GetPlayerErrorTooltip(int errorCode)
    {
        return errorCode switch
        {
            2 => "Invalid YouTube ID or URL.",
            5 => "Cannot play this video right now.",
            100 => "Video not found or removed.",
            101 => "Network is unreachable.",
            150 => "Playback is restricted by the owner.",
            _ => "Unable to load video."
        };
    }

    private static string? TryGetQueryParameter(string query, string key)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(parts[0]);
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            return value;
        }

        return null;
    }

    private void VolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (m_suppressVolumeChange)
        {
            return;
        }

        m_currentVolume = Math.Clamp(e.NewValue, 0, 1);
        UpdateVolumeIcon();
        Settings.Instance.Volume = m_currentVolume;
        ApplyVolumeToWebView();
    }

    private void ApplyVolumeToWebView()
    {
        if (!m_isEditorReady)
        {
            m_pendingVolume = m_currentVolume;
            return;
        }

        SendWebViewMessage(new
        {
            type = "set-volume",
            value = m_currentVolume
        });
    }

    private bool m_cookiesEnabled;

    private void TryEnableThirdPartyCookies()
    {
        if (m_cookiesEnabled)
        {
            return;
        }

        var platformWebView = WebViewControl.PlatformWebView;
        if (platformWebView is null)
        {
            return;
        }

        try
        {
            var platformType = platformWebView.GetType();
            var coreWebView2Property = platformType.GetProperty("CoreWebView2");
            if (coreWebView2Property == null)
            {
                return;
            }

            var coreWebView2 = coreWebView2Property.GetValue(platformWebView);
            if (coreWebView2 == null)
            {
                return;
            }

            var settingsProperty = coreWebView2.GetType().GetProperty("Settings");
            var settings = settingsProperty?.GetValue(coreWebView2);
            if (settings == null)
            {
                return;
            }

            var cookiesProperty = settings.GetType().GetProperty("IsThirdPartyCookiesEnabled");
            if (cookiesProperty == null || !cookiesProperty.CanWrite)
            {
                return;
            }

            var currentValue = cookiesProperty.GetValue(settings) as bool?;
            if (currentValue != true)
            {
                cookiesProperty.SetValue(settings, true);
                Console.WriteLine("[WebView] Enabled third-party cookies for playback.");
            }

            m_cookiesEnabled = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebView] Unable to enable third-party cookies: {ex.Message}");
        }
    }


    private static void TrimRecentMediaList()
    {
        var entries = Settings.Instance.RecentYouTubeItems;
        if (entries.Count == 0)
        {
            return;
        }

        var trimmed = new List<string>(MaxRecentMediaEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!TryParseRecentMediaEntry(entry, out var mediaId, out var displayName))
            {
                continue;
            }

            var normalizedId = NormalizeMediaId(mediaId);
            if (string.IsNullOrWhiteSpace(normalizedId) || !seen.Add(normalizedId))
            {
                continue;
            }

            var friendlyName = SanitizeDisplayName(displayName, normalizedId);
            trimmed.Add($"{normalizedId}|{friendlyName}");

            if (trimmed.Count >= MaxRecentMediaEntries)
            {
                break;
            }
        }

        if (!entries.SequenceEqual(trimmed, StringComparer.Ordinal))
        {
            Settings.Instance.RecentYouTubeItems = trimmed;
        }
    }

    private static void UpdateRecentMediaList(string mediaId, string? displayName)
    {
        var normalizedId = NormalizeMediaId(mediaId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return;
        }

        var list = new List<string>(Settings.Instance.RecentYouTubeItems);

        list.RemoveAll(entry =>
        {
            if (!TryParseRecentMediaEntry(entry, out var entryId, out _))
            {
                return false;
            }

            var normalizedEntryId = NormalizeMediaId(entryId);
            return !string.IsNullOrWhiteSpace(normalizedEntryId)
                && string.Equals(normalizedEntryId, normalizedId, StringComparison.OrdinalIgnoreCase);
        });

        var friendlyName = SanitizeDisplayName(displayName, normalizedId);
        list.Insert(0, $"{normalizedId}|{friendlyName}");

        if (list.Count > MaxRecentMediaEntries)
        {
            list = list.Take(MaxRecentMediaEntries).ToList();
        }

        Settings.Instance.RecentYouTubeItems = list;
    }

    private static bool TryParseRecentMediaEntry(string? entry, out string mediaId, out string displayName)
    {
        mediaId = string.Empty;
        displayName = string.Empty;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var separator = entry.IndexOf('|');
        if (separator < 0)
        {
            mediaId = entry.Trim();
            displayName = mediaId;
            return !string.IsNullOrWhiteSpace(mediaId);
        }

        mediaId = entry[..separator].Trim();
        displayName = entry[(separator + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(mediaId);
    }

    private static string SanitizeDisplayName(string? displayName, string fallbackId)
    {
        var sanitized = (displayName ?? string.Empty)
            .ReplaceLineEndings(" ")
            .Replace('|', ' ')
            .Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return GetDefaultDisplayName(fallbackId);
        }

        return sanitized;
    }

    private static string GetDefaultDisplayName(string mediaId)
    {
        return IsPlaylistId(mediaId)
            ? $"{mediaId} (playlist)"
            : mediaId;
    }

    private static void TrimMruList()
    {
        var list = Settings.Instance.MruFiles;
        var trimmed = new List<string>(MaxMruEntries);

        foreach (var entry in list.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(entry);
            }
            catch
            {
                continue;
            }

            if (!IsFileAccessible(fullPath))
                continue;
            trimmed.Add(fullPath);
            if (trimmed.Count >= MaxMruEntries)
                break;
        }

        if (!list.SequenceEqual(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            Settings.Instance.MruFiles = trimmed;
        }
    }

    private static void UpdateMruList(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var list = Settings.Instance.MruFiles;

            list.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, fullPath);

            if (list.Count > MaxMruEntries)
            {
                list = list.Take(MaxMruEntries).ToList();
            }

            Settings.Instance.MruFiles = list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating MRU list: {ex.Message}");
        }
    }

    private static bool IsFileAccessible(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void HandlePasteRequestedAsync()
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                return;
            }

            var text = await clipboard.TryGetTextAsync() ?? string.Empty;
            SendWebViewMessage(new
            {
                type = "paste-content",
                content = text
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling paste request: {ex.Message}");
        }
    }

    private async void SaveFile_Click(object? sender, RoutedEventArgs e)
    {
        await SaveFileAsync();
    }

    private async Task SaveFileAsync()
    {
        if (string.IsNullOrEmpty(m_currentFilePath))
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Code File"
            });

            if (file == null)
            {
                return;
            }
            m_currentFilePath = file.Path.LocalPath;
            UpdateMruList(m_currentFilePath);
        }

        if (!m_isEditorReady) return;

        m_pendingContentRequest?.TrySetCanceled();
        var completionSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        m_pendingContentRequest = completionSource;

        SendWebViewMessage(new { type = "request-content" });

        var completedTask = await Task.WhenAny(completionSource.Task, Task.Delay(3000));

        if (completedTask == completionSource.Task)
        {
            try
            {
                var content = await completionSource.Task;
                m_pendingContentRequest = null;

                if (content != null)
                {
                    await File.WriteAllTextAsync(m_currentFilePath, content);
                    UpdateMruList(m_currentFilePath);
                }
            }
            catch (Exception ex)
            {
                m_pendingContentRequest = null;
                Console.WriteLine($"Error retrieving editor content: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Timed out waiting for editor content.");
            m_pendingContentRequest = null;
        }
    }

    private async Task<CommandExecutionResult> TryExecuteCommandAsync(string rawInput)
    {
        if (!TryParseCommand(rawInput, out var commandName, out var argument))
        {
            return CommandExecutionResult.NotHandled;
        }

        if (!m_commandHandlers.TryGetValue(commandName, out var handler))
        {
            SetLoadVideoButtonTooltip($"Unknown command: {commandName}");
            return CommandExecutionResult.UnknownCommand;
        }

        await handler(argument);
        return CommandExecutionResult.Executed;
    }

    private readonly struct CommandSuggestionContext
    {
        public string Query { get; init; }
        public string CommandName { get; init; }
        public int StartIndex { get; init; }
        public int Length { get; init; }
        public bool IsArgument { get; init; }
        public bool IsExplicit { get; init; }
    }

    private void UpdateCommandSuggestions()
    {
        if (m_suppressCommandSuggestionUpdate)
        {
            return;
        }

        if (CommandSuggestionsPopup is not { } popup || CommandSuggestionsListBox is not { } listBox)
        {
            return;
        }

        if (!TryGetSuggestionContext(out var context))
        {
            CloseCommandSuggestions();
            return;
        }

        var matches = GetSuggestionMatches(context);

        if (matches.Count == 0)
        {
            CloseCommandSuggestions();
            return;
        }

        var previousSelection = listBox.SelectedItem as string;
        listBox.ItemsSource = matches;

        var selectionIndex = previousSelection is null
            ? -1
            : matches.FindIndex(o => string.Equals(o, previousSelection, StringComparison.OrdinalIgnoreCase));

        if (selectionIndex < 0)
        {
            selectionIndex = 0;
        }

        listBox.SelectedIndex = selectionIndex;

        if (listBox.SelectedItem is { } item)
        {
            listBox.ScrollIntoView(item);
        }

        popup.IsOpen = true;
    }

    private void CloseCommandSuggestions()
    {
        if (CommandSuggestionsPopup is { } popup)
        {
            popup.IsOpen = false;
        }

        if (CommandSuggestionsListBox is { } listBox)
        {
            listBox.ItemsSource = null;
            listBox.SelectedIndex = -1;
        }
    }

    private void MoveCommandSuggestionSelection(int offset)
    {
        if (CommandSuggestionsListBox is not { } listBox || listBox.ItemCount == 0)
        {
            return;
        }

        var currentIndex = listBox.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = offset > 0 ? 0 : listBox.ItemCount - 1;
        }
        else
        {
            currentIndex = Math.Clamp(currentIndex + offset, 0, listBox.ItemCount - 1);
        }

        listBox.SelectedIndex = currentIndex;

        if (listBox.SelectedItem is { } item)
        {
            listBox.ScrollIntoView(item);
        }
    }

    private void AcceptCommandSuggestion(string suggestion)
    {
        if (YouTubeIdTextBox is null)
        {
            return;
        }

        if (!TryGetSuggestionContext(out var context))
        {
            return;
        }

        var text = YouTubeIdTextBox.Text ?? string.Empty;
        var prefix = context.StartIndex > 0 ? text.Substring(0, context.StartIndex) : string.Empty;
        var suffixStart = Math.Min(context.StartIndex + context.Length, text.Length);
        var suffix = suffixStart < text.Length ? text.Substring(suffixStart) : string.Empty;

        var caret = prefix.Length + suggestion.Length;

        m_suppressCommandSuggestionUpdate = true;
        YouTubeIdTextBox.Text = prefix + suggestion + suffix;
        YouTubeIdTextBox.CaretIndex = caret;
        YouTubeIdTextBox.SelectionStart = caret;
        YouTubeIdTextBox.SelectionEnd = caret;
        m_suppressCommandSuggestionUpdate = false;

        CloseCommandSuggestions();
    }

    private bool IsCurrentCommandSelectionComplete(string suggestion) =>
        !TryGetSuggestionContext(out var context) || string.Equals(context.Query, suggestion, StringComparison.OrdinalIgnoreCase);

    private bool TryGetSuggestionContext(out CommandSuggestionContext context)
    {
        context = default;

        if (YouTubeIdTextBox is null)
        {
            return false;
        }

        var text = YouTubeIdTextBox.Text ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        var caretIndex = YouTubeIdTextBox.CaretIndex;
        if (caretIndex != text.Length)
        {
            return false;
        }

        var span = text.AsSpan(0, caretIndex);
        var index = 0;

        while (index < span.Length && char.IsWhiteSpace(span[index]))
        {
            index++;
        }

        var isExplicit = false;
        if (index < span.Length && span[index] == '>')
        {
            isExplicit = true;
            index++;
            while (index < span.Length && span[index] == ' ')
            {
                index++;
            }
        }

        if (index > span.Length)
        {
            return false;
        }

        if (index == span.Length)
        {
            if (!isExplicit)
            {
                return false;
            }

            context = new CommandSuggestionContext
            {
                Query = string.Empty,
                CommandName = string.Empty,
                StartIndex = text.Length,
                Length = 0,
                IsArgument = false,
                IsExplicit = true
            };
            return true;
        }

        var working = text.Substring(index, span.Length - index);
        var firstSpace = working.IndexOf(' ');
        var hasSpace = firstSpace >= 0;
        var commandToken = hasSpace ? working[..firstSpace] : working;

        if (!hasSpace)
        {
            if (!isExplicit && string.IsNullOrEmpty(commandToken))
            {
                return false;
            }

            context = new CommandSuggestionContext
            {
                Query = commandToken,
                CommandName = commandToken,
                StartIndex = index,
                Length = commandToken.Length,
                IsArgument = false,
                IsExplicit = isExplicit
            };
            return true;
        }

        commandToken = commandToken.Trim();
        if (commandToken.Length == 0)
        {
            return false;
        }

        var trailingSpace = working.EndsWith(' ');
        int tokenStartRelative;
        int tokenLength;
        string query;

        if (trailingSpace)
        {
            tokenStartRelative = working.Length;
            tokenLength = 0;
            query = string.Empty;
        }
        else
        {
            var lastSpace = working.LastIndexOf(' ');
            tokenStartRelative = lastSpace + 1;
            tokenLength = working.Length - tokenStartRelative;
            query = working.Substring(tokenStartRelative, tokenLength);
        }

        if (!isExplicit && !m_commandHandlers.ContainsKey(commandToken))
        {
            return false;
        }

        context = new CommandSuggestionContext
        {
            Query = query,
            CommandName = commandToken,
            StartIndex = index + tokenStartRelative,
            Length = tokenLength,
            IsArgument = true,
            IsExplicit = isExplicit
        };
        return true;
    }

    private bool TryParseCommand(string rawInput, out string commandName, out string argument)
    {
        commandName = string.Empty;
        argument = string.Empty;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var trimmed = rawInput.Trim();
        var explicitCommand = trimmed.StartsWith(">", StringComparison.Ordinal);

        if (explicitCommand)
        {
            trimmed = trimmed[1..].TrimStart();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }
        }

        if (m_commandHandlers.ContainsKey(trimmed))
        {
            commandName = trimmed;
            argument = string.Empty;
            return true;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        commandName = parts[0];
        argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        if (explicitCommand)
        {
            return true;
        }

        return m_commandHandlers.ContainsKey(commandName);
    }

    private enum CommandExecutionResult
    {
        NotHandled,
        Executed,
        UnknownCommand
    }

    private void FocusCommandPalette()
    {
        if (YouTubeIdTextBox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (YouTubeIdTextBox.IsVisible)
            {
                YouTubeIdTextBox.Focus();
                YouTubeIdTextBox.SelectAll();
                if (m_isEditorReady)
                {
                    SendWebViewMessage(new { type = "blur-editor" });
                }
            }
        }, DispatcherPriority.Input);
    }

    private void HandleEditorFocus()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (YouTubeIdTextBox is { } textBox)
            {
                var collapseIndex = textBox.Text?.Length ?? 0;
                textBox.SelectionStart = collapseIndex;
                textBox.SelectionEnd = collapseIndex;
                textBox.CaretIndex = collapseIndex;

                if (textBox.IsFocused)
                {
                    textBox.IsEnabled = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        textBox.IsEnabled = true;
                    }, DispatcherPriority.Background);
                }
            }

            WebViewControl.Focus();
        }, DispatcherPriority.Input);
    }

    private void YouTubeIdTextBox_GotFocus(object? sender, GotFocusEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            YouTubeIdTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }


    private void HandleEditorDimmedChange(bool isDimmed)
    {
        m_isEditorDimmed = isDimmed;
        Dispatcher.UIThread.Post(() => SetToolbarDimmed(isDimmed));
    }

    private void SetToolbarDimmed(bool dimmed)
    {
        if (ToolbarGrid is null)
        {
            return;
        }

        if (m_isToolbarDimmed == dimmed)
        {
            return;
        }

        m_isToolbarDimmed = dimmed;
        ToolbarGrid.Opacity = dimmed ? ToolbarDimmedOpacity : ToolbarActiveOpacity;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) => RegisterPointerActivity();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => RegisterPointerActivity();

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e) => RegisterPointerActivity();

    private void RegisterPointerActivity()
    {
        if (m_isToolbarDimmed)
        {
            SetToolbarDimmed(false);
        }

        if (!m_isEditorReady)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (!m_isEditorDimmed && now - m_lastHostActivitySent < HostActivityThrottle)
        {
            return;
        }

        m_lastHostActivitySent = now;
        SendWebViewMessage(new { type = "host-activity" });
    }

    private void RequestApplicationQuit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            desktopLifetime.Shutdown();
        }
        else
        {
            Close();
        }
    }
}
