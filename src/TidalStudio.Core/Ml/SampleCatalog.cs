using System.Text.Json;
using System.Text.RegularExpressions;
using TidalStudio.Core.Synth;

namespace TidalStudio.Core.Ml;

public enum SoundKind { Pitched, Drum, Loop, Kit }

/// <summary>One sound (sample folder) the suggestions may use.</summary>
public sealed record SoundInfo
{
    public required string Name { get; init; }
    public required SoundKind Kind { get; init; }
    /// <summary>Role for pitched sounds and loops; Drums for drum sounds.</summary>
    public PartRole Role { get; init; }
    /// <summary>Drum type (bd, sd, …) for drum sounds.</summary>
    public string? Drum { get; init; }
    /// <summary>The kit a drum sound belongs to ("gated" for gatedbd).</summary>
    public string? Kit { get; init; }
    /// <summary>The pitch of every sample (index = n), Tidal MIDI (c5 = 60). Empty for drums and loops.</summary>
    public IReadOnlyList<int> Roots { get; init; } = Array.Empty<int>();
    public int Variants { get; init; } = 1;
    /// <summary>Loops: tempo they were made at, and their length in bars (index = n).</summary>
    public double? LoopBpm { get; init; }
    public IReadOnlyList<double> LoopBars { get; init; } = Array.Empty<double>();

    /// <summary>The sample (n) whose pitch is nearest below-or-at <paramref name="pitch"/>, and its root.</summary>
    public (int N, int Root) NearestRoot(int pitch)
    {
        if (Roots.Count == 0) return (0, 60);
        int best = 0;
        for (int i = 0; i < Roots.Count; i++)
            if (Score(Roots[i]) < Score(Roots[best])) best = i;
        return (best, Roots[best]);
        // Prefer a sample a little below the note: pitching up a few semitones sounds better than down.
        double Score(int root) => pitch >= root ? pitch - root : (root - pitch) * 1.6;
    }
}

/// <summary>
/// The sounds in the samples folder that suggestions may use, with their role and pitch. Built for the samples the
/// Tidepool synths save: pitched notes <c>name/name_00_c4.wav</c>, drums <c>kitbd/kitbd_00.wav</c>, loops
/// <c>name/name_00_A.wav</c> and combined kits <c>kit/kit_00_bd.wav</c>.
/// </summary>
public sealed partial class SampleCatalog
{
    public string Root { get; }
    public IReadOnlyList<SoundInfo> Sounds { get; }
    private readonly Dictionary<string, SoundInfo> _byName;

    public SampleCatalog(string root, IEnumerable<SoundInfo> sounds)
    {
        Root = root;
        Sounds = sounds.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        _byName = Sounds.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public SoundInfo? Find(string name) => _byName.TryGetValue(name, out var s) ? s : null;

    public IEnumerable<SoundInfo> ForRole(PartRole role) => Sounds.Where(s => s.Kind == SoundKind.Pitched && s.Role == role);

    /// <summary>Drum kits: kit name → its drums.</summary>
    public IReadOnlyDictionary<string, List<SoundInfo>> Kits =>
        Sounds.Where(s => s.Kind == SoundKind.Drum && s.Kit is not null).GroupBy(s => s.Kit!)
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>The drum sound for a drum type in a kit, or its nearest substitute in that kit.</summary>
    public SoundInfo? DrumIn(string kit, string drum)
    {
        if (Find(kit + drum) is { Kind: SoundKind.Drum } s) return s;
        foreach (var alt in Drums.Substitutes(drum))
            if (Find(kit + alt) is { Kind: SoundKind.Drum } a) return a;
        return null;
    }

    [GeneratedRegex(@"^(?<name>[a-z][a-z0-9_-]*?)_(?<n>\d{2})_(?<tag>[a-z]+-?\d+|[A-Z]|[a-z]+)\.wav$")]
    private static partial Regex TaggedFile();
    [GeneratedRegex(@"^(?<name>[a-z][a-z0-9_-]*?)_(?<n>\d{2})\.wav$")]
    private static partial Regex PlainFile();

    /// <summary>Scans <paramref name="root"/>. <paramref name="roleOverrides"/>: sound name → role chosen by the user.</summary>
    public static SampleCatalog Scan(string root, IReadOnlyDictionary<string, PartRole>? roleOverrides = null)
    {
        var sounds = new List<SoundInfo>();
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Describe(dir) is { } s)
                        sounds.Add(roleOverrides is not null && roleOverrides.TryGetValue(s.Name, out var r) && s.Kind == SoundKind.Pitched ? s with { Role = r } : s);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return new SampleCatalog(root, sounds);
    }

    public static SoundInfo? Describe(string dir)
    {
        string name = Path.GetFileName(dir);
        var files = Directory.EnumerateFiles(dir, "*.wav").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0 || !SampleExport.IsValidName(name)) return null;

        var tagged = files.Select(f => TaggedFile().Match(f)).ToList();
        if (tagged.All(m => m.Success))
        {
            var tags = tagged.Select(m => m.Groups["tag"].Value).ToList();
            // Pitched: name_00_c4.wav
            var roots = tags.Select(t => SynthPatch.TryParseNoteName(t, out int midi) ? midi : -1).ToList();
            if (roots.All(r => r >= 0) && tags.All(t => char.IsLower(t[0])))
            {
                var drum = PitchedDrum(name);
                if (drum is not null)
                    return new SoundInfo { Name = name, Kind = SoundKind.Drum, Role = PartRole.Drums, Drum = drum, Variants = files.Count };
                return new SoundInfo { Name = name, Kind = SoundKind.Pitched, Role = GuessRole(name, roots), Roots = roots, Variants = files.Count };
            }
            // Loops: name_00_A.wav
            if (tags.All(t => t.Length == 1 && char.IsUpper(t[0])))
            {
                var lengths = files.Select(f => WavSeconds(Path.Combine(dir, f))).ToList();
                double bar = lengths.Where(l => l > 0).DefaultIfEmpty(2).Min();
                double bpm = Math.Round(240 / bar, 1);
                return new SoundInfo
                {
                    Name = name, Kind = SoundKind.Loop, Role = GuessLoopRole(name), LoopBpm = bpm,
                    LoopBars = lengths.Select(l => Math.Max(1, Math.Round(l / bar))).ToList(), Variants = files.Count,
                };
            }
            // Combined kit: kit_00_bd.wav
            if (tags.All(t => DrumKits.Abbreviations.ContainsKey(t)))
                return new SoundInfo { Name = name, Kind = SoundKind.Kit, Role = PartRole.Drums, Variants = files.Count };
        }
        if (files.All(f => PlainFile().IsMatch(f)))
        {
            // A drum of a kit: kitbd/kitbd_00.wav
            foreach (var abbr in DrumKits.Abbreviations.Keys.OrderByDescending(a => a.Length))
                if (name.EndsWith(abbr, StringComparison.Ordinal) && name.Length > abbr.Length)
                    return new SoundInfo
                    {
                        Name = name, Kind = SoundKind.Drum, Role = PartRole.Drums, Drum = abbr == "rb" ? "bd" : abbr,
                        Kit = name[..^abbr.Length], Variants = files.Count,
                    };
        }
        return null;
    }

    /// <summary>Tidepool presets that are drums although saved as notes (Kick, Hat, FM kick …).</summary>
    private static string? PitchedDrum(string name) =>
        name.Contains("kick") ? "bd" :
        name.Contains("hat") ? "hh" :
        name.Contains("snare") ? "sd" :
        name.Contains("clap") ? "cp" :
        name is "fmhit" or "fmwood" or "fmmetal" ? "perc" : null;

    private static readonly (string Word, PartRole Role)[] RoleWords =
    {
        ("bass", PartRole.Bass), ("sub", PartRole.Bass), ("reese", PartRole.Bass), ("wobble", PartRole.Bass),
        ("slap", PartRole.Bass), ("solid", PartRole.Bass), ("rubber", PartRole.Bass), ("acid", PartRole.Bass),
        ("pad", PartRole.Chords), ("string", PartRole.Chords), ("choir", PartRole.Chords), ("chord", PartRole.Chords),
        ("organ", PartRole.Chords), ("piano", PartRole.Chords), ("keys", PartRole.Chords), ("ep", PartRole.Chords),
        ("clav", PartRole.Chords), ("brass", PartRole.Chords), ("stab", PartRole.Chords), ("hoover", PartRole.Chords),
        ("poly", PartRole.Chords), ("warm", PartRole.Chords), ("vowel", PartRole.Chords),
    };

    public static PartRole GuessRole(string name, IReadOnlyList<int> roots)
    {
        // "acidlead" is a lead, "fmpopbell" a bell: specific words first.
        if (name.Contains("lead") || name.Contains("bell") || name.Contains("arp") || name.Contains("pluck") || name.Contains("mallet"))
            return PartRole.Melody;
        foreach (var (word, role) in RoleWords)
            if (name.Contains(word)) return role;
        return roots.Count > 0 && roots.Max() <= 36 ? PartRole.Bass : PartRole.Melody;
    }

    private static PartRole GuessLoopRole(string name) =>
        name.Contains("arp") || name.Contains("pluck") || name.Contains("lead") ? PartRole.Melody : PartRole.Bass;

    /// <summary>Length of a PCM WAV file in seconds (0 when it can't be read).</summary>
    public static double WavSeconds(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            using var r = new BinaryReader(s);
            if (new string(r.ReadChars(4)) != "RIFF") return 0;
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") return 0;
            int byteRate = 0;
            while (s.Position + 8 <= s.Length)
            {
                string id = new(r.ReadChars(4));
                int size = r.ReadInt32();
                if (id == "fmt ")
                {
                    r.ReadInt16(); r.ReadInt16(); r.ReadInt32();
                    byteRate = r.ReadInt32();
                    s.Position += size - 12;
                }
                else if (id == "data") return byteRate > 0 ? (double)size / byteRate : 0;
                else s.Position += size + (size & 1);
            }
        }
        catch (IOException) { }
        return 0;
    }

    /// <summary>Role overrides saved by the user (sound name → role).</summary>
    public static Dictionary<string, PartRole> LoadOverrides(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, PartRole>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new();
    }

    public static void SaveOverrides(string path, IReadOnlyDictionary<string, PartRole> overrides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(overrides, new JsonSerializerOptions { WriteIndented = true }));
    }
}
