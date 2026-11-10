using Avalonia;
using Gf2ExiliumTranslationLauncher.Diagnostics;

namespace Gf2ExiliumTranslationLauncher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppLog.Initialize();
        AppLog.Info($"Process starting. Args={args.Length}, BaseDirectory={AppContext.BaseDirectory}");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                AppLog.Error("Unhandled application exception.", exception);
            }
            else
            {
                AppLog.Error($"Unhandled application exception object: {eventArgs.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLog.Info($"Process exiting normally. ExitCode={exitCode}");
            return exitCode;
        }
        catch (Exception exception)
        {
            AppLog.Error("Fatal application failure.", exception);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect();
}
