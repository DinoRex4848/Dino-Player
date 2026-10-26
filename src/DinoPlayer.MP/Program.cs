using Avalonia;

namespace DinoPlayer.MP;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            AppLog.Error($"Unhandled exception: {eventArgs.ExceptionObject}");
            // The process may end right after this handler returns.
            AppLog.Flush();
        };

        try
        {
            AppLog.Initialize();
            Settings.SettingsStore.Initialize();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Fatal startup error: {exception}");
            throw;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .With(new X11PlatformOptions { WmClass = "DinoPlayer.MP" })
            .UsePlatformDetect()
            .LogToTrace();
}
