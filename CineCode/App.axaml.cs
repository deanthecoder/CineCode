using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using CineCode.Views;
using DTC.Core.Commands;

namespace CineCode;

public class App : Application
{
    public ICommand AboutCommand { get; }

    private bool m_aboutDialogOpen;

    public App()
    {
        AboutCommand = new RelayCommand(ShowAboutDialog);
        DataContext = this;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AvaloniaWebViewBuilder.Initialize(null);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowAboutDialog(object o)
    {
        if (m_aboutDialogOpen)
            return;

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
            return;

        var dialog = new AboutDialog();
        dialog.Opened += (_, _) => m_aboutDialogOpen = true;
        dialog.Closed += (_, _) => m_aboutDialogOpen = false;

        _ = dialog.ShowDialog(desktop.MainWindow);
    }
}
