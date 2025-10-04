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

namespace CineCode;

public partial class MainWindow : Window
{
    private string _currentFilePath = string.Empty;
    private bool _isEditorReady;
    private double? _pendingOpacity;
    private TaskCompletionSource<string?>? _pendingContentRequest;
    private (string content, string extension)? _pendingFile;
    
    public MainWindow()
    {
        InitializeComponent();
        InitializeWebView();
        SetupEventHandlers();
    }

    private void InitializeWebView()
    {
        WebViewControl.WebMessageReceived += OnWebViewMessageReceived;
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
                        Console.WriteLine($"[Playback] {stateElement.GetString()}");
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
