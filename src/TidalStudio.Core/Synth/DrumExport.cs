namespace TidalStudio.Core.Synth;

/// <summary>One WAV file a drum kit saves.</summary>
public sealed record DrumFile(string Folder, string FileName, DrumVoice Voice, int Variation);

/// <summary>
/// How a drum kit is laid out on disk, so that SuperDirt and Tidal see it as proper drum samples:
/// <list type="bullet">
/// <item>every drum is its own sound named kit + drum, like the tidal-drum-machines kits:
/// <c>mykitbd/mykitbd_00.wav</c>, <c>mykitbd_01.wav</c>, … → <c>s "mykitbd"</c>, versions with <c># n 1</c>;</item>
/// <item>optionally the whole kit as one sound, one drum per index, like Dirt-Samples' "drum" folder:
/// <c>mykit/mykit_00_bd.wav</c>, <c>mykit_01_sd.wav</c>, … → <c>s "mykit:0 mykit:1"</c>.</item>
/// </list>
/// </summary>
public static class DrumExport
{
    public static List<DrumFile> Plan(DrumKit kit, bool combined)
    {
        var files = new List<DrumFile>();
        var voices = kit.ActiveVoices.ToList();
        foreach (var voice in voices)
        {
            string sound = kit.SoundName(voice);
            int count = Math.Clamp(voice.Variations, 1, 16);
            for (int i = 0; i < count; i++)
                files.Add(new DrumFile(sound, $"{sound}_{i:00}.wav", voice, i));
        }
        if (combined)
            for (int i = 0; i < voices.Count; i++)
                files.Add(new DrumFile(kit.Name, $"{kit.Name}_{i:00}_{voices[i].Abbreviation}.wav", voices[i], 0));
        return files;
    }

    /// <summary>The sound folders a kit saves (each one a Tidal sound name).</summary>
    public static List<string> Folders(DrumKit kit, bool combined) =>
        Plan(kit, combined).Select(f => f.Folder).Distinct().ToList();

    /// <summary>
    /// Renders and writes every file of the kit under <paramref name="samplesRoot"/>, replacing the WAV files already
    /// in those folders. Returns the folders written.
    /// </summary>
    public static List<string> Write(string samplesRoot, DrumKit kit, bool combined)
    {
        var plan = Plan(kit, combined);
        var rendered = plan.AsParallel().AsOrdered()
            .Select(f => (File: f, Note: DrumEngine.Render(f.Voice, f.Variation, kit.SampleRate, kit.Normalize)))
            .ToList();
        var folders = new List<string>();
        foreach (var group in rendered.GroupBy(r => r.File.Folder))
        {
            string folder = Path.Combine(samplesRoot, group.Key);
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder, "*.wav")) File.Delete(old);
            foreach (var (file, note) in group) SampleExport.WriteWav(Path.Combine(folder, file.FileName), note);
            folders.Add(folder);
        }
        return folders;
    }

    /// <summary>A few lines of Tidal that play the kit, for the status line and the "copy example" button.</summary>
    public static string Example(DrumKit kit, bool combined)
    {
        var voices = kit.ActiveVoices.Select(v => v.Abbreviation).ToList();
        string S(string abbr) => voices.Contains(abbr) ? kit.Name + abbr : kit.Name + voices.FirstOrDefault();
        var lines = new List<string>
        {
            $"d1 $ s \"{S("bd")}*4\"",
            $"d2 $ s \"~ {S("cp")} ~ {S("cp")}\"",
            $"d3 $ s \"{S("hh")}*8\" # n \"<0 1>\" # gain 0.9",
        };
        if (combined && voices.Count > 1)
            lines.Add($"d4 $ s \"{kit.Name}*8\" # n \"0 .. {voices.Count - 1}\"   -- the whole kit: n 0 = {voices[0]}, n 1 = {voices[1]} …");
        return string.Join("\n", lines);
    }
}
