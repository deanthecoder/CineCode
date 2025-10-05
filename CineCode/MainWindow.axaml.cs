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
using Material.Icons;
using WebViewCore.Events;

namespace CineCode;

public partial class MainWindow : Window
{
    private const int MaxMruEntries = 50;
    private const string DefaultLoadVideoTooltip = "Load Video";
    private const string InvalidVideoTooltip = "Enter a valid YouTube URL or ID.";
    private const string LoadingVideoTooltip = "Loading video...";

    private string m_currentFilePath = string.Empty;
    private bool m_isEditorReady;
    private double? m_pendingOpacity;
    private readonly bool m_suppressOpacityUpdate;
    private TaskCompletionSource<string?>? m_pendingContentRequest;
    private (string content, string extension)? m_pendingFile;
    private bool m_isPlaybackPaused;
    private string m_currentVideoId = string.Empty;
    private string m_currentVideoTitle = string.Empty;
    private double m_currentVolume = 0.5;
    private double? m_pendingVolume;
    private bool m_suppressVolumeChange;
    
    public MainWindow()
    {
        InitializeComponent();
        InitializeWebView();
        SetupEventHandlers();
        TrimMruList();
        m_suppressOpacityUpdate = true;
        var savedOpacity = Math.Clamp(Settings.Instance.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        OpacitySlider.Value = savedOpacity;
        m_suppressOpacityUpdate = false;
        var savedVideoId = NormalizeVideoId(Settings.Instance.YouTubeVideoId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(savedVideoId))
        {
            savedVideoId = NormalizeVideoId(YouTubeIdTextBox.Text ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(savedVideoId))
        {
            savedVideoId = NormalizeVideoId("eYhP50P31h4");
        }

        m_currentVideoId = savedVideoId;
        YouTubeIdTextBox.Text = m_currentVideoId;
        Settings.Instance.YouTubeVideoId = m_currentVideoId;
        m_suppressVolumeChange = true;
        VolumeSlider.Value = m_currentVolume;
        m_suppressVolumeChange = false;
        UpdatePlayPauseIcon();
        SetLoadVideoButtonTooltip(DefaultLoadVideoTooltip);
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
        OpacitySlider.PropertyChanged += async (s, e) =>
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
                    Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            await OpenFileAsync();
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
                        m_currentVideoTitle = string.Empty;
                        SetLoadVideoButtonTooltip(GetPlayerErrorTooltip(errorCode));
                    }
                    break;
                case "video-metadata":
                    HandleVideoMetadata(document.RootElement);
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
            videoId = m_currentVideoId,
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
            m_currentFilePath = file.Path.LocalPath;
            
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            
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

    private void LoadVideoButton_Click(object? sender, RoutedEventArgs e)
    {
        TryLoadVideoFromInput();
    }

    private void YouTubeIdTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            TryLoadVideoFromInput();
        }
    }

    private bool TryLoadVideoFromInput()
    {
        var rawId = YouTubeIdTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawId))
        {
            SetLoadVideoButtonTooltip(InvalidVideoTooltip);
            return false;
        }

        var normalized = NormalizeVideoId(rawId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            SetLoadVideoButtonTooltip(InvalidVideoTooltip);
            return false;
        }

        m_currentVideoId = normalized;
        m_currentVideoTitle = string.Empty;
        YouTubeIdTextBox.Text = m_currentVideoId;
        Settings.Instance.YouTubeVideoId = m_currentVideoId;

        m_isPlaybackPaused = false;
        UpdatePlayPauseIcon();
        SetLoadVideoButtonTooltip(LoadingVideoTooltip);

        if (m_isEditorReady)
        {
            SendWebViewMessage(new
            {
                type = "load-video",
                videoId = m_currentVideoId,
                autoplay = true
            });
            TryEnableThirdPartyCookies();
        }

        ApplyVolumeToWebView();
        return true;
    }

    private static string NormalizeVideoId(string input)
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
                return SanitizeVideoId(path);
            }

            if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                var queryId = TryGetQueryParameter(uri.Query, "v");
                if (!string.IsNullOrWhiteSpace(queryId))
                {
                    return SanitizeVideoId(queryId);
                }

                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    if (string.Equals(segments[0], "embed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(segments[0], "shorts", StringComparison.OrdinalIgnoreCase))
                    {
                        return SanitizeVideoId(segments[1]);
                    }
                }
            }
        }

        return SanitizeVideoId(trimmed);
    }

    private static string SanitizeVideoId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return new string(input.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
    }

    private void HandleVideoMetadata(JsonElement element)
    {
        var incomingId = element.TryGetProperty("videoId", out var videoIdElement)
            ? NormalizeVideoId(videoIdElement.GetString() ?? string.Empty)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(incomingId) &&
            !string.Equals(incomingId, m_currentVideoId, StringComparison.Ordinal))
        {
            return;
        }

        var title = element.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(title))
        {
            m_currentVideoTitle = title;
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


    private static void TrimMruList()
    {
        var list = Settings.Instance.MruFiles ?? [];
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
        if (string.IsNullOrEmpty(m_currentFilePath))
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Code File"
            });

            if (file != null)
            {
                m_currentFilePath = file.Path.LocalPath;
                UpdateMruList(m_currentFilePath);
            }
            else
            {
                return;
            }
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
