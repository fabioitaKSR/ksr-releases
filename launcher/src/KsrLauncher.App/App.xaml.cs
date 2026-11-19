using System.Windows;

namespace KsrLauncher.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LauncherDiagnosticLog.Write($"Launcher {typeof(App).Assembly.GetName().Version?.ToString(3)} starting");
        DispatcherUnhandledException += (_, args) => LauncherDiagnosticLog.Write("Unhandled UI error", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LauncherDiagnosticLog.Write("Unhandled application error", args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LauncherDiagnosticLog.Write("Launcher exiting");
        base.OnExit(e);
    }
}
