using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CineCode.Views;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;

namespace CineCode;

public partial class App : Application
{
    public ICommand AboutCommand { get; }

    private bool _aboutDialogOpen;

    public App()
    {
        AboutCommand = new ActionCommand(ShowAboutDialog);
        DataContext = this;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AvaloniaWebViewBuilder.Initialize(default);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowAboutDialog()
    {
        if (_aboutDialogOpen)
        {
            return;
        }

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
        {
            return;
        }

        var dialog = new AboutDialog();
        dialog.Opened += (_, _) => _aboutDialogOpen = true;
        dialog.Closed += (_, _) => _aboutDialogOpen = false;

        _ = dialog.ShowDialog(desktop.MainWindow);
    }

    private sealed class ActionCommand : ICommand
    {
        private readonly Action? _execute;
        private readonly Func<bool>? _canExecute;

        public ActionCommand(Action? execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute?.Invoke();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
