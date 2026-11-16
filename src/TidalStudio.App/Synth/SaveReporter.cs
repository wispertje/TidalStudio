using Avalonia.Controls;
using Avalonia.Input.Platform;
using TidalStudio.App.Services;
using TidalStudio.Core.Completion;
using TidalStudio.Core.SuperCollider;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Synth;

/// <summary>
/// After Tidepool or Tidepool Drums saves sound folders: get them into SuperDirt now (load into TidalStudio's own
/// SuperDirt, or restart one started elsewhere), and say whether SuperDirt loads them at every start.
/// </summary>
public sealed class SaveReporter(
    AppServices services,
    Func<string, Task<(bool Ok, string Message)>> restartSuperDirt,
    Action samplesChanged,
    Action<OutputKind, string> log)
{
    /// <summary>Returns the messages to show and buttons for follow-up actions.</summary>
    public async Task<(List<string> Messages, List<Control> Actions)> ReportAsync(
        Window owner, IReadOnlyList<string> folders, string samplesRoot, bool restart, Action<string> progress)
    {
        var messages = new List<string>();
        var actions = new List<Control>();
        var loadCodes = folders.Select(f => $"~dirt.loadSoundFiles(\"{f.Replace('\\', '/')}\");").ToList();

        // Now: TidalStudio's own SuperCollider can be told to load them directly (no interruption). One started
        // elsewhere can't be reached, so restart SuperCollider (if allowed), which also makes it TidalStudio's own.
        if (services.SuperDirt.IsRunning && loadCodes.All(services.SuperDirt.TrySendCode))
            messages.Add("Loaded into SuperDirt — you can play it now.");
        else if (restart)
        {
            progress("Restarting SuperCollider so SuperDirt loads it…");
            var (_, message) = await restartSuperDirt(folders[0]);
            // The restart loads every sound in the samples folder; make sure about the others too.
            if (services.SuperDirt.IsRunning) foreach (var code in loadCodes.Skip(1)) services.SuperDirt.TrySendCode(code);
            messages.Add(message);
        }
        else if (await SuperDirtProbe.IsRunningAsync())
        {
            string all = string.Join("\n", loadCodes);
            messages.Add("To use it right away, run this in SuperCollider (or restart SuperDirt):");
            var copy = SynthWindow.SmallButton("Copy load command", all);
            copy.Click += async (_, _) =>
            {
                if (owner.Clipboard is { } clipboard) await clipboard.SetTextAsync(all);
                copy.Content = "Copied ✓";
            };
            actions.Add(copy);
        }

        // Later: does startup.scd load these folders?
        string? startup = SampleLibrary.StartupScriptPath();
        string script = startup is not null && File.Exists(startup) ? await File.ReadAllTextAsync(startup) : "";
        if (startup is not null && folders.All(f => StartupScript.LoadsFolder(script, f)))
            messages.Add("SuperDirt also loads it at every start.");
        else if (startup is not null && StartupScript.AddSamplesFolder(script, samplesRoot) is { } edited)
        {
            messages.Add("Your SuperCollider startup file doesn't load your samples folder (TidalStudio loads it when it starts SuperDirt).");
            var add = SynthWindow.SmallButton("Load my samples at startup", $"Adds {StartupScript.LoadLine(samplesRoot)} to {startup} (a backup is kept as startup.scd.bak)");
            add.Click += async (_, _) =>
            {
                try
                {
                    File.Copy(startup, startup + ".bak", overwrite: true);
                    await File.WriteAllTextAsync(startup, edited);
                    add.Content = "Added to startup.scd ✓";
                    add.IsEnabled = false;
                    samplesChanged();
                    log(OutputKind.Info, $"Tidepool: {startup} now loads {samplesRoot} (backup: startup.scd.bak)");
                }
                catch (Exception e) { progress($"Could not edit {startup}: {e.Message}"); }
            };
            actions.Add(add);
        }
        else
            messages.Add($"To load it at every start, add this to your SuperCollider startup file: {StartupScript.LoadLine(samplesRoot)}");
        return (messages, actions);
    }
}
