namespace TidalStudio.Core.Synth;

/// <summary>One sound folder that rendering every factory preset writes.</summary>
public sealed record PresetSound(string Synth, string Preset, string Sound, Func<string, List<string>> Write);

/// <summary>What rendering every factory preset did.</summary>
public sealed record PresetRenderResult(List<PresetSound> Written, List<PresetSound> Skipped, List<(PresetSound Sound, string Error)> Failed, int Files);

/// <summary>
/// Renders the factory presets of every Tidepool synth (the sample synth, Drums, Acid and FM) into a samples folder,
/// so all of them can be played from Tidal without opening the synth windows first.
/// </summary>
public static class PresetSamples
{
    /// <summary>Sound names for the Acid sounds (the box presets have no name of their own).</summary>
    private static readonly IReadOnlyDictionary<string, string> AcidNames = new Dictionary<string, string>
    {
        ["Classic squelch"] = "acidclassic",
        ["Rubber (slow filter)"] = "acidslow",
        ["Screamer (distorted)"] = "acidscreamer",
        ["Deep square"] = "aciddeep",
        ["Hollow square bite"] = "acidhollow",
        ["Dub acid (delay)"] = "aciddub",
        ["Ringing (self-oscillating)"] = "acidringing",
        ["Thin & clicky"] = "acidthin",
        ["Warehouse (drive + room)"] = "acidwarehouse",
        ["Pop octave bass"] = "seqoctbass",
        ["Driving eighths bass"] = "seqeighths",
        ["Warm sequencer bass"] = "seqwarmbass",
        ["Bright arp"] = "seqbrightarp",
        ["Plucky sequence (echo)"] = "seqpluck",
        ["Night drive bass"] = "seqnight",
        ["Detuned saw bass"] = "seqdetune",
        ["Pulse bass (narrow)"] = "seqpulse",
        ["Neon arp (echo)"] = "seqneonarp",
        ["Tape arp"] = "seqtapearp",
    };

    /// <summary>The tempo of the synth pop sequences (the Acid window and the synth pop example use it too).</summary>
    public const double PopBpm = 118;

    public static string AcidSoundName(string preset) =>
        AcidNames.TryGetValue(preset, out var n) ? n : "acid" + new string(preset.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>Every sound folder the factory presets make, in menu order. The "Init" presets are left out.</summary>
    public static List<PresetSound> Plan()
    {
        var list = new List<PresetSound>();
        foreach (var (name, create) in SynthPresets.All)
        {
            if (name == "Init") continue;
            var patch = create();
            list.Add(new PresetSound("Tidepool", name, patch.Name, root =>
            {
                var notes = SynthPatch.ParseNotes(patch.Notes);
                var rendered = notes.AsParallel().AsOrdered().Select(m => SynthEngine.Render(patch, m)).ToList();
                return SampleExport.Write(Path.Combine(root, patch.Name), patch.Name, rendered);
            }));
        }
        foreach (var (name, create) in FmPresets.All)
        {
            if (name == "Init") continue;
            var patch = create();
            list.Add(new PresetSound("Tidepool FM", name, patch.Name, root =>
            {
                var notes = SynthPatch.ParseNotes(patch.Notes);
                var rendered = notes.AsParallel().AsOrdered().Select(m => FmEngine.Render(patch.Clone(), m)).ToList();
                return SampleExport.Write(Path.Combine(root, patch.Name), patch.Name, rendered);
            }));
        }
        foreach (var (name, create) in DrumKits.All)
        {
            var kit = create();
            // Every drum is its own sound (kit + drum), plus the whole kit as one sound.
            var folders = DrumExport.Folders(kit, combined: true);
            list.Add(new PresetSound("Tidepool Drums", name, string.Join(" ", folders), root =>
            {
                DrumExport.Write(root, kit, combined: true);
                return DrumExport.Plan(kit, combined: true).Select(f => Path.Combine(root, f.Folder, f.FileName)).ToList();
            }));
        }
        foreach (var (name, apply) in AcidLines.AllSounds)
        {
            bool pop = AcidLines.PopSounds.Concat(AcidLines.SynthwaveSounds).Any(s => s.Name == name);
            var box = AcidLines.FromPatterns(pop ? AcidLines.PopPatterns : AcidLines.DemoPatterns);
            apply(box);
            box.Name = AcidSoundName(name);
            if (pop) box.Bpm = PopBpm;
            list.Add(new PresetSound("Tidepool Acid", name, box.Name, root => AcidLines.Write(root, box)));
        }

        // The sounds the example scripts use, which are otherwise made by saving from the synth windows.
        var mykit = DrumKits.Create("Punchy machine");
        mykit.Name = "mykit";
        list.Add(new PresetSound("Example scripts", "Drums: Punchy machine as \"mykit\"", string.Join(" ", DrumExport.Folders(mykit, combined: true)), root =>
        {
            DrumExport.Write(root, mykit, combined: true);
            return DrumExport.Plan(mykit, combined: true).Select(f => Path.Combine(root, f.Folder, f.FileName)).ToList();
        }));
        var acidloop = AcidLines.Default();
        list.Add(new PresetSound("Example scripts", "Acid: demo patterns as \"acidloop\"", acidloop.Name, root => AcidLines.Write(root, acidloop)));
        var popline = AcidLines.FromPatterns(AcidLines.PopPatterns);
        AcidLines.PopSounds[0].Apply(popline);
        popline.Name = "popline";
        popline.Bpm = PopBpm;
        list.Add(new PresetSound("Example scripts", "Acid: synth pop patterns as \"popline\"", popline.Name, root => AcidLines.Write(root, popline)));
        return list;
    }

    /// <summary>The sound folders of a planned item (a drum kit makes several).</summary>
    public static IEnumerable<string> FoldersOf(PresetSound sound) => sound.Sound.Split(' ');

    /// <summary>
    /// Renders every preset into <paramref name="samplesRoot"/>. A preset whose sound folder already has WAV files is
    /// skipped unless <paramref name="replace"/> is set, so samples saved by hand are never overwritten.
    /// </summary>
    public static PresetRenderResult RenderAll(string samplesRoot, bool replace, Action<int, int, PresetSound>? progress = null)
    {
        var plan = Plan();
        var written = new List<PresetSound>();
        var skipped = new List<PresetSound>();
        var failed = new List<(PresetSound, string)>();
        int files = 0;
        for (int i = 0; i < plan.Count; i++)
        {
            var item = plan[i];
            progress?.Invoke(i, plan.Count, item);
            bool exists = FoldersOf(item).Any(f =>
            {
                string dir = Path.Combine(samplesRoot, f);
                return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.wav").Any();
            });
            if (exists && !replace) { skipped.Add(item); continue; }
            try
            {
                files += item.Write(samplesRoot).Count;
                written.Add(item);
            }
            catch (Exception e)
            {
                failed.Add((item, e.Message));
            }
        }
        progress?.Invoke(plan.Count, plan.Count, plan[^1]);
        return new PresetRenderResult(written, skipped, failed, files);
    }
}
