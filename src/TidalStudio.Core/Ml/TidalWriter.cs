using System.Globalization;
using System.Text;

namespace TidalStudio.Core.Ml;

/// <summary>Turns a part into readable Tidal code that plays it with a sound from the catalog.</summary>
public static class TidalWriter
{
    public const int StepsPerBar = 16;

    /// <summary>Code for a pitched part: <c>d5 $ note "…" # s "popbass" # n 1 # legato 1</c>.</summary>
    public static string Pitched(MPart part, SoundInfo sound, int channel, string? comment = null)
    {
        var notes = part.Notes;
        int median = notes.Count == 0 ? 60 : notes.Select(n => n.Pitch).OrderBy(p => p).ElementAt(notes.Count / 2);
        var (n, root) = sound.NearestRoot(median);
        bool names = ((root % 12) + 12) % 12 == 0; // samples made on a C: write note names
        string NoteText(int pitch) => names ? NoteName(pitch - root) : (pitch - root).ToString(CultureInfo.InvariantCulture);

        bool poly = part.Role == PartRole.Chords;
        string pattern = Bars(part, poly, NoteText);
        var sb = new StringBuilder();
        if (comment is not null) sb.Append("-- ").Append(comment).Append('\n');
        sb.Append('d').Append(channel).Append(" $ note \"").Append(pattern).Append("\" # s \"").Append(sound.Name).Append('"');
        if (sound.Variants > 1 || n > 0) sb.Append(" # n ").Append(n);
        sb.Append(" # legato 1");
        if (part.Role == PartRole.Bass) sb.Append(" # cut ").Append(channel);
        double gain = part.Role switch { PartRole.Chords => 0.75, PartRole.Melody => 0.85, _ => 0.9 };
        sb.Append(" # gain ").Append(gain.ToString("0.##", CultureInfo.InvariantCulture));
        if (part.Role is PartRole.Chords or PartRole.Melody) sb.Append(" # room 0.25 # orbit 1");
        return sb.ToString();
    }

    /// <summary>Code for a drum part with a kit: <c>d1 $ s "[gatedbd*4, ~ gatedsd ~ gatedsd, gatedhh*8]"</c>.</summary>
    public static string DrumPart(MPart part, SampleCatalog catalog, string kit, int channel, string? comment = null)
    {
        var rows = new List<string>();
        foreach (var group in part.Notes.GroupBy(x => x.Pitch).OrderBy(g => g.Key))
        {
            string drum = Drums.Names[Math.Clamp(group.Key, 0, Drums.Names.Length - 1)];
            var sound = catalog.DrumIn(kit, drum);
            if (sound is null) continue;
            var single = new MPart { Role = PartRole.Drums, Bars = part.Bars, Notes = group.ToList() };
            rows.Add(DrumRow(single, sound.Name));
        }
        var sb = new StringBuilder();
        if (comment is not null) sb.Append("-- ").Append(comment).Append('\n');
        string body = rows.Count switch { 0 => "~", 1 => rows[0], _ => "[" + string.Join(", ", rows) + "]" };
        sb.Append('d').Append(channel).Append(" $ s \"").Append(body).Append('"');
        return sb.ToString();
    }

    /// <summary>One drum's hits over the bars, as mini-notation (bars that repeat collapse).</summary>
    private static string DrumRow(MPart part, string sound)
    {
        var bars = new List<string>();
        for (int b = 0; b < part.Bars; b++)
        {
            var hits = new bool[StepsPerBar];
            foreach (var x in part.Notes)
            {
                int step = (int)Math.Round((x.Start - b) * StepsPerBar);
                if (step >= 0 && step < StepsPerBar && x.Start < b + 1 - 1e-9 && x.Start >= b - 1e-9) hits[step] = true;
            }
            bars.Add(DrumBar(hits, sound));
        }
        return Alternate(bars);
    }

    private static string DrumBar(bool[] hits, string sound)
    {
        if (!hits.Any(h => h)) return "~";
        // The shortest repeating period: [x ~ ~ ~]*4 → x*4
        foreach (int p in new[] { 1, 2, 4, 8 })
        {
            bool repeats = true;
            for (int i = p; i < hits.Length && repeats; i++) repeats = hits[i] == hits[i % p];
            if (!repeats) continue;
            int times = hits.Length / p;
            if (hits[0] && hits.Take(p).Count(h => h) == 1) return times == 1 ? sound : $"{sound}*{times}";
            string cell = Steps(hits.Take(p).Select(h => h ? sound : null).ToList());
            return times == 1 ? cell : $"[{cell}]*{times}";
        }
        return Steps(hits.Select(h => h ? sound : null).ToList());
    }

    /// <summary>Drum steps (a sound or null for a rest) as mini-notation: a hit lasts until the next one.</summary>
    private static string Steps(IReadOnlyList<string?> steps)
    {
        var items = new List<(string Text, int Weight)>();
        foreach (var s in steps)
        {
            if (s is null && items.Count > 0) items[^1] = (items[^1].Text, items[^1].Weight + 1);
            else items.Add((s ?? "~", 1));
        }
        return Join(items);
    }

    private static string Join(List<(string Text, int Weight)> items)
    {
        int g = items.Select(i => i.Weight).Aggregate(0, Gcd);
        if (g > 1) items = items.Select(i => (i.Text, i.Weight / g)).ToList();
        // A run of rests reads better spelled out when short.
        var parts = new List<string>();
        foreach (var (text, w) in items)
        {
            if (w == 1) parts.Add(text);
            else if (text == "~" && w <= 2) parts.AddRange(Enumerable.Repeat("~", w));
            else parts.Add($"{text}@{w}");
        }
        return string.Join(" ", parts);
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>Pitched bars; each note lasts until the next onset (monophonic) unless <paramref name="poly"/>.</summary>
    private static string Bars(MPart part, bool poly, Func<int, string> noteText)
    {
        var bars = new List<string>();
        for (int b = 0; b < part.Bars; b++)
        {
            var onsets = part.Notes.Where(x => x.Start >= b - 1e-9 && x.Start < b + 1 - 1e-9)
                .GroupBy(x => Math.Clamp((int)Math.Round((x.Start - b) * StepsPerBar), 0, StepsPerBar - 1))
                .OrderBy(g => g.Key).ToList();
            var items = new List<(string Text, int Weight)>();
            int pos = 0;
            for (int i = 0; i < onsets.Count; i++)
            {
                int step = onsets[i].Key;
                if (step > pos) items.Add(("~", step - pos));
                int next = i + 1 < onsets.Count ? onsets[i + 1].Key : StepsPerBar;
                double durSteps = onsets[i].Max(x => x.Dur) * StepsPerBar;
                int len = Math.Clamp((int)Math.Round(durSteps), 1, next - step);
                var pitches = onsets[i].Select(x => x.Pitch).Distinct().OrderBy(p => p).ToList();
                if (!poly) pitches = pitches.Take(1).ToList();
                items.Add((ChordText(pitches, noteText), len));
                if (len < next - step) items.Add(("~", next - step - len));
                pos = next;
            }
            if (items.Count == 0) items.Add(("~", StepsPerBar));
            // Merge neighbouring rests.
            var merged = new List<(string Text, int Weight)>();
            foreach (var it in items)
                if (it.Text == "~" && merged.Count > 0 && merged[^1].Text == "~") merged[^1] = ("~", merged[^1].Weight + it.Weight);
                else merged.Add(it);
            bars.Add(Join(merged));
        }
        return Alternate(bars);
    }

    private static string ChordText(List<int> pitches, Func<int, string> noteText)
    {
        if (pitches.Count == 1) return noteText(pitches[0]);
        return "[" + string.Join(",", pitches.Select(noteText)) + "]";
    }

    /// <summary>Bars as one pattern: "x" when all bars are the same, "&lt;[a] [b]&gt;" otherwise.</summary>
    private static string Alternate(List<string> bars)
    {
        if (bars.Count == 0) return "~";
        if (bars.All(b => b == bars[0])) return bars[0];
        // Repeat a shorter cycle when the bars repeat (a b a b → <[a] [b]>).
        foreach (int p in new[] { 1, 2, 4 })
            if (p < bars.Count && bars.Count % p == 0 && bars.Select((b, i) => b == bars[i % p]).All(x => x))
                return "<" + string.Join(" ", bars.Take(p).Select(Wrap)) + ">";
        return "<" + string.Join(" ", bars.Select(Wrap)) + ">";
        static string Wrap(string bar) => bar.Contains(' ') ? $"[{bar}]" : bar;
    }

    /// <summary>A note offset as a Tidal note name relative to c5: 0 → c, 3 → ef, -4 → af4, 12 → c6.</summary>
    public static string NoteName(int offset)
    {
        int pc = ((offset % 12) + 12) % 12;
        int octave = 5 + (int)Math.Floor(offset / 12.0);
        string name = Theory.PitchClassName(pc);
        return octave == 5 ? name : name + octave.ToString(CultureInfo.InvariantCulture);
    }
}
