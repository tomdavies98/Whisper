using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Client.Infrastructure;
using Whisper.Client.Services;
using Whisper.Client.ViewModels;

namespace Whisper.Client;

public partial class App : Application
{
    private ServiceProvider? _services;
    private bool _isReportingFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = ClientServices.Build();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashLog.Write(ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow
        {
            DataContext = _services.GetRequiredService<ShellViewModel>(),
        };

        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_services is { } services)
        {
            await services.GetRequiredService<IWhisperConnection>().DisposeAsync();
            await services.DisposeAsync();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        // A layout fault re-enters this handler on every render tick. Swallow repeats
        // before writing anything, or crash.log grows without bound and the window
        // never gets as far as the error dialog.
        if (_isReportingFailure)
        {
            return;
        }

        _isReportingFailure = true;
        CrashLog.Write(e.Exception);

        MessageBox.Show(
            $"{e.Exception.Message}\n\nDetails were written to:\n{CrashLog.FilePath}",
            "Whisper hit an unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Carrying on after an unhandled fault means running on state nobody has reasoned
        // about. Exiting is more honest than a window that half works.
        Shutdown(1);
    }
}
