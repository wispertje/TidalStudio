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
        // --ml <command> …: tools for the suggestion model (no window), see MlCli.
        if (args.Contains("--ml"))
            Environment.Exit(TidalStudio.Core.Ml.MlCli.RunAsync(args).GetAwaiter().GetResult());
        // --render-presets <samples folder> [--replace]: render every factory preset to WAVs and exit (no window).
        int render = Array.IndexOf(args, "--render-presets");
        if (render >= 0 && render + 1 < args.Length)
        {
            string root = args[render + 1];
            Directory.CreateDirectory(root);
            var log = new List<string>();
            var result = TidalStudio.Core.Synth.PresetSamples.RenderAll(root, args.Contains("--replace"),
                (i, n, s) => { if (i < n) log.Add($"[{i + 1}/{n}] {s.Synth} · {s.Preset} → {s.Sound}"); });
            log.Add($"written {result.Written.Count} presets, {result.Files} files; skipped {result.Skipped.Count} (already there): {string.Join(", ", result.Skipped.Select(s => s.Sound))}");
            foreach (var (s, error) in result.Failed) log.Add($"FAILED {s.Synth} · {s.Preset}: {error}");
            Directory.CreateDirectory(AppSettings.DefaultDirectory);
            File.WriteAllLines(Path.Combine(AppSettings.DefaultDirectory, "render-presets.log"), log);
            Environment.Exit(result.Failed.Count == 0 ? 0 : 1);
        }
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
