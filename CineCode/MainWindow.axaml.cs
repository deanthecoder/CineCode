using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AvaloniaWebView;
using WebViewCore.Events;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using System.Linq;
using Material.Icons;
using Material.Icons.Avalonia;

namespace CineCode;

public partial class MainWindow : Window
{
    private string _currentFilePath = string.Empty;
    private bool _isEditorReady;
    private double? _pendingOpacity;
    private TaskCompletionSource<string?>? _pendingContentRequest;
    private (string content, string extension)? _pendingFile;
    private bool _isPlaybackPaused;
    private string _currentVideoId = "eYhP50P31h4";
    private double _currentVolume = 0.5;
    private double? _pendingVolume;
    private bool _suppressVolumeChange;
    
    public MainWindow()
    {
        InitializeComponent();
        InitializeWebView();
        SetupEventHandlers();
        _currentVideoId = NormalizeVideoId(YouTubeIdTextBox.Text ?? _currentVideoId);
        YouTubeIdTextBox.Text = _currentVideoId;
        _suppressVolumeChange = true;
        VolumeSlider.Value = _currentVolume;
        _suppressVolumeChange = false;
        UpdatePlayPauseIcon();
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
            _isEditorReady = false;
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "CineCode.Assets.editor.html";
            
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
                await ApplyEditorOpacityAsync(OpacitySlider.Value);
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
                    _pendingContentRequest?.TrySetResult(content);
                    _pendingContentRequest = null;
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
                        _isPlaybackPaused = paused;
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
                        Console.WriteLine($"[YouTube] Player error {errorCodeElement.GetInt32()}");
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
        _isEditorReady = true;

        if (_pendingFile is { } pendingFile)
        {
            SendWebViewMessage(new
            {
                type = "load-code",
                content = pendingFile.content,
                extension = pendingFile.extension
            });
            _pendingFile = null;
        }

        var opacity = _pendingOpacity ?? OpacitySlider.Value;
        _pendingOpacity = null;

        await ApplyEditorOpacityAsync(opacity);

        SendWebViewMessage(new
        {
            type = "load-video",
            videoId = _currentVideoId,
            autoplay = true
        });
        _isPlaybackPaused = false;
        UpdatePlayPauseIcon();

        if (_pendingVolume.HasValue)
        {
            _currentVolume = Math.Clamp(_pendingVolume.Value, 0, 1);
            _suppressVolumeChange = true;
            VolumeSlider.Value = _currentVolume;
            _suppressVolumeChange = false;
            _pendingVolume = null;
        }

        ApplyVolumeToWebView();
        TryEnableThirdPartyCookies();
    }

    private Task ApplyEditorOpacityAsync(double opacity)
    {
        if (!_isEditorReady)
        {
            _pendingOpacity = opacity;
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
            _currentFilePath = file.Path.LocalPath;
            
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            
            var extension = Path.GetExtension(_currentFilePath).TrimStart('.');
            
            if (_isEditorReady)
            {
                SendWebViewMessage(new { type = "load-code", content, extension });
            }
            else
            {
                _pendingFile = (content, extension);
            }
        }
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isEditorReady)
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
        if (!_isEditorReady)
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
            PlayPauseIcon.Kind = _isPlaybackPaused ? MaterialIconKind.PlayCircleOutline : MaterialIconKind.PauseCircleOutline;
        }
    }

    private void LoadVideoButton_Click(object? sender, RoutedEventArgs e)
    {
        var rawId = YouTubeIdTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return;
        }

        var normalized = NormalizeVideoId(rawId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _currentVideoId = normalized;
        YouTubeIdTextBox.Text = _currentVideoId;
        SendWebViewMessage(new
        {
            type = "load-video",
            videoId = _currentVideoId,
            autoplay = true
        });
        _isPlaybackPaused = false;
        UpdatePlayPauseIcon();
        ApplyVolumeToWebView();
        TryEnableThirdPartyCookies();
    }

    private static string NormalizeVideoId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var filtered = new string(input.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return filtered;
    }

    private void VolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeChange)
        {
            return;
        }

        _currentVolume = Math.Clamp(e.NewValue, 0, 1);
        ApplyVolumeToWebView();
    }

    private void ApplyVolumeToWebView()
    {
        if (!_isEditorReady)
        {
            _pendingVolume = _currentVolume;
            return;
        }

        SendWebViewMessage(new
        {
            type = "set-volume",
            value = _currentVolume
        });
    }

    private bool _cookiesEnabled;

    private void TryEnableThirdPartyCookies()
    {
        if (_cookiesEnabled)
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

            _cookiesEnabled = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebView] Unable to enable third-party cookies: {ex.Message}");
        }
    }

    private async void HandlePasteRequestedAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                return;
            }

            var text = await ClipboardExtensions.TryGetTextAsync(clipboard) ?? string.Empty;
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
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Code File"
            });

            if (file != null)
            {
                _currentFilePath = file.Path.LocalPath;
            }
            else
            {
                return;
            }
        }

        if (!_isEditorReady) return;

        _pendingContentRequest?.TrySetCanceled();
        var completionSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingContentRequest = completionSource;

        SendWebViewMessage(new { type = "request-content" });

        var completedTask = await Task.WhenAny(completionSource.Task, Task.Delay(3000));

        if (completedTask == completionSource.Task)
        {
            try
            {
                var content = await completionSource.Task;
                _pendingContentRequest = null;

                if (content != null)
                {
                    await File.WriteAllTextAsync(_currentFilePath, content);
                }
            }
            catch (Exception ex)
            {
                _pendingContentRequest = null;
                Console.WriteLine($"Error retrieving editor content: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Timed out waiting for editor content.");
            _pendingContentRequest = null;
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
