using Avalonia;
using TidalStudio.Core.Settings;

namespace TidalStudio.App;

internal static class Program
{
    public static string CrashLogPath => Path.Combine(AppSettings.DefaultDirectory, "crash.log");

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(e.Exception);
            e.SetObserved();
        };
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            WriteCrash(e);
            throw;
        }
    }

    public static void WriteCrash(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DefaultDirectory);
            File.AppendAllText(CrashLogPath, $"--- {DateTime.Now:O}\n{e}\n\n");
        }
        catch (Exception) { }
    }

    // Avalonia configuration, also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
