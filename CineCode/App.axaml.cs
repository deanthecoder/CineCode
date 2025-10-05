using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using CineCode.Views;

namespace CineCode;

public class App : Application
{
    public ICommand AboutCommand { get; }

    private bool m_aboutDialogOpen;

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
        if (m_aboutDialogOpen)
        {
            return;
        }

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
        {
            return;
        }

        var dialog = new AboutDialog();
        dialog.Opened += (_, _) => m_aboutDialogOpen = true;
        dialog.Closed += (_, _) => m_aboutDialogOpen = false;

        _ = dialog.ShowDialog(desktop.MainWindow);
    }

    private sealed class ActionCommand : ICommand
    {
        private readonly Action? m_execute;
        private readonly Func<bool>? m_canExecute;

        public ActionCommand(Action? execute, Func<bool>? canExecute = null)
        {
            m_execute = execute;
            m_canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => m_canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => m_execute?.Invoke();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
