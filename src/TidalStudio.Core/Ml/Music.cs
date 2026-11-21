using System.Globalization;

namespace TidalStudio.Core.Ml;

/// <summary>What a part does in a track.</summary>
public enum PartRole { Drums, Bass, Melody, Chords }

/// <summary>
/// One note (or drum hit) of a part. Times are in cycles (1 cycle = 1 bar). <see cref="Pitch"/> is a MIDI number in
/// Tidal's convention (c5 = 60 = note 0); for drums it is the index into <see cref="Drums.Names"/>.
/// </summary>
public readonly record struct MNote(double Start, double Dur, int Pitch, double Vel = 0.8);

/// <summary>A musical part: notes over a number of bars, starting at cycle 0.</summary>
public sealed class MPart
{
    public PartRole Role { get; set; }
    public int Bars { get; set; } = 1;
    public List<MNote> Notes { get; set; } = new();
    /// <summary>The sound it was played with (when known).</summary>
    public string? Sound { get; set; }

    public MPart Clone() => new() { Role = Role, Bars = Bars, Notes = Notes.ToList(), Sound = Sound };

    public override string ToString() =>
        $"{Role} {Bars} bar(s): " + string.Join(" ", Notes.Take(12).Select(n => $"{n.Start.ToString("0.###", CultureInfo.InvariantCulture)}:{n.Pitch}"));
}

/// <summary>The drum types the models know (the Tidepool Drums abbreviations).</summary>
public static class Drums
{
    public static readonly string[] Names = { "bd", "sd", "cp", "rim", "hh", "oh", "cr", "rd", "lt", "mt", "ht", "cb", "sh", "perc" };

    public static int Index(string abbr)
    {
        if (abbr == "rb") return 0;
        int i = Array.IndexOf(Names, abbr);
        return i < 0 ? Names.Length - 1 : i;
    }

    /// <summary>Drums to fall back to when a kit lacks one, nearest first.</summary>
    public static IEnumerable<string> Substitutes(string abbr) => abbr switch
    {
        "oh" => new[] { "hh", "sh" },
        "hh" => new[] { "sh", "oh", "rd" },
        "cr" => new[] { "rd", "oh" },
        "rd" => new[] { "cr", "oh", "hh" },
        "rim" => new[] { "cp", "sd", "perc" },
        "cp" => new[] { "sd", "rim" },
        "sd" => new[] { "cp", "rim" },
        "sh" => new[] { "hh", "perc" },
        "cb" => new[] { "perc", "rim" },
        "perc" => new[] { "cb", "rim", "sh" },
        "lt" => new[] { "mt", "ht", "perc" },
        "mt" => new[] { "lt", "ht", "perc" },
        "ht" => new[] { "mt", "lt", "perc" },
        _ => Array.Empty<string>(),
    };

    /// <summary>General MIDI percussion key to a drum type.</summary>
    public static int FromGeneralMidi(int key) => key switch
    {
        35 or 36 => 0,
        38 or 40 => 1,
        39 => 2,
        37 => 3,
        42 or 44 => 4,
        46 => 5,
        49 or 52 or 55 or 57 => 6,
        51 or 53 or 59 => 7,
        41 or 43 or 45 => 8,
        47 or 48 => 9,
        50 => 10,
        56 => 11,
        54 or 69 or 70 or 82 => 12,
        _ => 13,
    };
}

public enum ChordQuality { None, Major, Minor, Diminished }

public readonly record struct MChord(int Root, ChordQuality Quality)
{
    public static readonly MChord None = new(0, ChordQuality.None);

    public int[] PitchClasses => Quality switch
    {
        ChordQuality.Major => new[] { Root, (Root + 4) % 12, (Root + 7) % 12 },
        ChordQuality.Minor => new[] { Root, (Root + 3) % 12, (Root + 7) % 12 },
        ChordQuality.Diminished => new[] { Root, (Root + 3) % 12, (Root + 6) % 12 },
        _ => Array.Empty<int>(),
    };

    public override string ToString() => Quality == ChordQuality.None ? "-" :
        Theory.DisplayName(Root) + Quality switch { ChordQuality.Minor => "m", ChordQuality.Diminished => "dim", _ => "" };
}

public readonly record struct MKey(int Tonic, bool Minor)
{
    public static readonly MKey CMinor = new(0, true);

    public int[] Scale
    {
        get
        {
            int tonic = Tonic;
            return (Minor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 }).Select(i => (i + tonic) % 12).ToArray();
        }
    }

    public bool Contains(int pitch) => Scale.Contains(((pitch % 12) + 12) % 12);

    public override string ToString() => Theory.DisplayName(Tonic) + (Minor ? " minor" : " major");
}

/// <summary>Key and chord detection, scales.</summary>
public static class Theory
{
    private static readonly string[] PcNames = { "c", "cs", "d", "ef", "e", "f", "fs", "g", "af", "a", "bf", "b" };

    public static string PitchClassName(int pc) => PcNames[((pc % 12) + 12) % 12];

    private static readonly string[] Display = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

    /// <summary>For people: C, C#, Eb …</summary>
    public static string DisplayName(int pc) => Display[((pc % 12) + 12) % 12];

    // Krumhansl–Kessler key profiles.
    private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    public static double[] PitchClassHistogram(IEnumerable<MNote> notes)
    {
        var h = new double[12];
        foreach (var n in notes) h[((n.Pitch % 12) + 12) % 12] += Math.Max(0.05, n.Dur) * Math.Max(0.1, n.Vel);
        return h;
    }

    /// <summary>The most likely key of pitched notes (C minor when there are none).</summary>
    public static MKey DetectKey(IEnumerable<MNote> notes)
    {
        var h = PitchClassHistogram(notes);
        if (h.Sum() <= 0) return MKey.CMinor;
        double best = double.NegativeInfinity;
        var key = MKey.CMinor;
        for (int t = 0; t < 12; t++)
            foreach (bool minor in new[] { false, true })
            {
                var profile = minor ? MinorProfile : MajorProfile;
                double r = Correlation(h, Enumerable.Range(0, 12).Select(i => profile[((i - t) % 12 + 12) % 12]).ToArray());
                if (r > best) { best = r; key = new MKey(t, minor); }
            }
        // A major key and its relative minor share their notes, so the profiles can't tell them apart well: when
        // the other one is about as likely, the first chord (where a progression usually starts) decides.
        var relative = key.Minor ? new MKey((key.Tonic + 3) % 12, false) : new MKey((key.Tonic + 9) % 12, true);
        var relativeProfile = relative.Minor ? MinorProfile : MajorProfile;
        double rr = Correlation(h, Enumerable.Range(0, 12).Select(i => relativeProfile[((i - relative.Tonic) % 12 + 12) % 12]).ToArray());
        var list = notes as IReadOnlyList<MNote> ?? notes.ToList();
        if (best - rr < 0.15 && list.Count > 0)
        {
            double start = list.Min(n => n.Start);
            var first = DetectChord(list.Where(n => n.Start < start + 1).ToList());
            bool Fits(MKey k) => first.Root == k.Tonic && (first.Quality == ChordQuality.Minor) == k.Minor;
            if (Fits(relative) && !Fits(key)) key = relative;
        }
        return key;
    }

    private static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), num = 0, da = 0, db = 0;
        for (int i = 0; i < a.Length; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }
        return da <= 0 || db <= 0 ? 0 : num / Math.Sqrt(da * db);
    }

    /// <summary>The best matching triad for some notes (the lowest notes count double, as the bass).</summary>
    public static MChord DetectChord(IReadOnlyList<MNote> notes)
    {
        if (notes.Count == 0) return MChord.None;
        var h = PitchClassHistogram(notes);
        int lowest = notes.Min(n => n.Pitch);
        foreach (var n in notes.Where(n => n.Pitch < lowest + 7)) h[((n.Pitch % 12) + 12) % 12] += Math.Max(0.05, n.Dur);
        double total = h.Sum();
        if (total <= 0) return MChord.None;
        double best = 0;
        var chord = MChord.None;
        for (int r = 0; r < 12; r++)
            foreach (var q in new[] { ChordQuality.Major, ChordQuality.Minor, ChordQuality.Diminished })
            {
                var c = new MChord(r, q);
                double inside = c.PitchClasses.Sum(pc => h[pc]);
                double score = inside / total + 0.25 * h[r] / total - (q == ChordQuality.Diminished ? 0.08 : 0);
                if (score > best) { best = score; chord = c; }
            }
        return best < 0.45 ? MChord.None : chord;
    }

    /// <summary>One chord per bar from all pitched notes (times in cycles from 0).</summary>
    public static List<MChord> DetectChords(IEnumerable<MNote> notes, int bars)
    {
        var all = notes.ToList();
        var result = new List<MChord>();
        for (int b = 0; b < bars; b++)
        {
            var inBar = all.Where(n => n.Start < b + 1 && n.Start + n.Dur > b)
                .Select(n => n with { Dur = Math.Min(n.Start + n.Dur, b + 1) - Math.Max(n.Start, b) }).ToList();
            result.Add(DetectChord(inBar));
        }
        return result;
    }
}
