using System.Globalization;
using TidalStudio.Core.Vj;

namespace TidalStudio.Core.Ml;

/// <summary>One event printed by the tsDump helper.</summary>
public sealed record DumpEvent(string Pattern, double Start, double Dur, string? Sound, double? N, double? Note, double? Gain, double? Legato);

/// <summary>
/// What is playing, as music: parts per pattern and role, the key, the chords per bar. Built from the events the
/// <c>tsDump</c> helper prints (Tidal itself works out which notes the code plays).
/// </summary>
public sealed class TidalContext
{
    /// <summary>The Haskell helpers, each defined with one <c>QueryAsync</c> (which adds the :{ :} itself).</summary>
    public static readonly string[] HelperDefinitions =
    {
        """
        tsDump :: Time -> Time -> IO ()
        tsDump a b = do
          pm <- Control.Concurrent.MVar.readMVar (sPMapMV tidal)
          let states = TSMap.toList pm
              anySolo = any (psSolo . snd) states
              live = [ (k, psPattern ps) | (k, ps) <- states, not (psMute ps), not anySolo || psSolo ps ]
              num v = case v of { VF x -> show x; VN x -> show (unNote x); VI x -> show x; VR x -> show (fromRational x :: Double); _ -> "" }
              str v = case v of { VS x -> x; _ -> "" }
              field m k f = maybe "" f (TSMap.lookup k m)
              d x = show (fromRational x :: Double)
              line k e = case whole e of
                Just w | start w == start (part e) -> Just $ intercalate "|"
                  ["#EV", k, d (start w), d (stop w - start w), field (value e) "s" str, field (value e) "n" num, field (value e) "note" num, field (value e) "gain" num, field (value e) "legato" num]
                _ -> Nothing
          mapM_ putStrLn [ l | (k, p) <- live, e <- queryArc p (Arc a b), Just l <- [line k e] ]
          putStrLn "#EV-END"
        """,
        """
        tsDumpNow :: Time -> IO ()
        tsDumpNow n = do
          now <- streamGetNow tidal
          let a = fromIntegral (floor now :: Integer)
          putStrLn ("#EV-NOW|" ++ show (fromRational now :: Double))
          tsDump a (a + n)
        """,
    };

    public double From { get; init; }
    public int Bars { get; init; }
    /// <summary>Parts by pattern id (several when a pattern plays more than one role).</summary>
    public List<(string Pattern, MPart Part)> Parts { get; init; } = new();
    public MKey Key { get; init; } = MKey.CMinor;
    public List<MChord> Chords { get; init; } = new();
    /// <summary>The cycle the dump was taken at (live), when known.</summary>
    public double? Now { get; init; }

    public bool Has(PartRole role) => Parts.Any(p => p.Part.Role == role);

    public static List<DumpEvent> Parse(IEnumerable<string> lines, out double? now)
    {
        now = null;
        var list = new List<DumpEvent>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EV-NOW|", StringComparison.Ordinal))
            {
                if (Num(line[8..]) is { } n) now = n;
                continue;
            }
            if (!line.StartsWith("#EV|", StringComparison.Ordinal)) continue;
            var f = line.Split('|');
            if (f.Length < 9 || Num(f[2]) is not { } start || Num(f[3]) is not { } dur) continue;
            list.Add(new DumpEvent(f[1], start, dur, f[4].Length > 0 ? f[4] : null, Num(f[5]), Num(f[6]), Num(f[7]), Num(f[8])));
        }
        return list;
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

    /// <summary>Turns dumped events (cycles <paramref name="from"/> … from + bars) into parts, key and chords.</summary>
    public static TidalContext Build(IEnumerable<DumpEvent> events, SampleCatalog catalog, double from, int bars, double? now = null)
    {
        var groups = new Dictionary<(string, PartRole), MPart>();
        foreach (var e in events)
        {
            if (e.Start < from - 1e-9 || e.Start >= from + bars - 1e-9) continue;
            if (ToNote(e, catalog, from) is not { } item) continue;
            var key = (e.Pattern, item.Role);
            if (!groups.TryGetValue(key, out var part))
                groups[key] = part = new MPart { Role = item.Role, Bars = bars, Sound = SoundName(e.Sound) };
            part.Notes.Add(item.Note);
        }
        foreach (var p in groups.Values) p.Notes.Sort((a, b) => a.Start.CompareTo(b.Start));
        var pitched = groups.Values.Where(p => p.Role != PartRole.Drums).SelectMany(p => p.Notes).ToList();
        return new TidalContext
        {
            From = from, Bars = bars, Now = now,
            Parts = groups.OrderBy(g => g.Key.Item1, StringComparer.Ordinal).Select(g => (g.Key.Item1, g.Value)).ToList(),
            Key = Theory.DetectKey(pitched),
            Chords = Theory.DetectChords(pitched, bars),
        };
    }

    private static string? SoundName(string? s) => s is null ? null : s.Split(':')[0];

    /// <summary>One event as a note of a role, or null when it can't be placed (unknown, effects …).</summary>
    public static (PartRole Role, MNote Note)? ToNote(DumpEvent e, SampleCatalog catalog, double from)
    {
        string? name = SoundName(e.Sound);
        int nIndex = (int)Math.Round(e.N ?? 0);
        if (e.Sound is { } full && full.Contains(':') && int.TryParse(full.Split(':')[1], out int colonN)) nIndex += colonN;
        double vel = Math.Clamp(e.Gain ?? 1, 0, 1.5) / 1.5 + 0.2;
        vel = Math.Clamp(vel, 0, 1);
        double start = e.Start - from, dur = Math.Max(1.0 / 64, e.Dur);
        var info = name is null ? null : catalog.Find(name);
        if (info is not null)
            switch (info.Kind)
            {
                case SoundKind.Drum:
                    return (PartRole.Drums, new MNote(start, dur, Drums.Index(info.Drum ?? "perc"), vel));
                case SoundKind.Kit:
                    return null;
                case SoundKind.Loop:
                    return null; // a sequenced loop: its notes aren't known here
                case SoundKind.Pitched:
                    int root = info.Roots.Count == 0 ? 60 : info.Roots[Math.Clamp(nIndex, 0, info.Roots.Count - 1) % info.Roots.Count];
                    int pitch = root + (int)Math.Round(e.Note ?? 0);
                    return (info.Role, new MNote(start, dur, pitch, vel));
            }
        // Not one of ours: Dirt-Samples drums by name, synths (superpiano …) by note.
        if (name is not null)
        {
            var kind = VjSignals.Classify(name, null);
            bool synth = name.StartsWith("super", StringComparison.Ordinal);
            if (!synth && e.Note is null)
            {
                return kind switch
                {
                    HitKind.Kick => (PartRole.Drums, new MNote(start, dur, 0, vel)),
                    HitKind.Snare => (PartRole.Drums, new MNote(start, dur, name.Contains("cp") || name.Contains("clap") ? 2 : 1, vel)),
                    HitKind.Hat => (PartRole.Drums, new MNote(start, dur, name.StartsWith("oh") ? 5 : 4, vel)),
                    _ => null,
                };
            }
        }
        if (e.Note is { } note)
        {
            int pitch = 60 + (int)Math.Round(note);
            return (pitch < 48 ? PartRole.Bass : PartRole.Melody, new MNote(start, dur, pitch, vel));
        }
        return null;
    }

    /// <summary>A one-line summary for the UI: "C minor · Cm Ab Eb Bb · drums, bass, chords".</summary>
    public string Describe()
    {
        var roles = Parts.Select(p => p.Part.Role).Distinct().Select(r => r.ToString().ToLowerInvariant());
        string chords = string.Join(" ", Chords.Take(8).Select(c => c.ToString()));
        return $"{Key} · {chords} · playing: {(Parts.Count == 0 ? "nothing" : string.Join(", ", roles))}";
    }
}
