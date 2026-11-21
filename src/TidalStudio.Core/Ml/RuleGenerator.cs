namespace TidalStudio.Core.Ml;

/// <summary>
/// Makes parts with simple music rules: chord tones on strong beats, scale steps in between, rhythm templates by
/// density, a motif that repeats with variation. The baseline the model has to beat, and what suggestions use
/// before a model is trained.
/// </summary>
public static class RuleGenerator
{
    public static MPart Generate(PartRequest r, Random random) => r.Role switch
    {
        PartRole.Drums => DrumPart(r, random),
        PartRole.Bass => Bass(r, random),
        PartRole.Chords => ChordPart(r, random),
        _ => Melody(r, random),
    };

    private static MChord ChordAt(PartRequest r, int bar)
    {
        if (r.Chords.Count == 0) return new MChord(r.Key.Tonic, r.Key.Minor ? ChordQuality.Minor : ChordQuality.Major);
        var c = r.Chords[bar % r.Chords.Count];
        return c.Quality == ChordQuality.None ? new MChord(r.Key.Tonic, r.Key.Minor ? ChordQuality.Minor : ChordQuality.Major) : c;
    }

    /// <summary>Rhythm templates (16 steps, 'x' = onset), sparse to busy.</summary>
    private static readonly string[][] BassRhythms =
    {
        new[] { "x...............", "x.......x.......", "x.........x....." },
        new[] { "x...x...x...x...", "x.....x...x.....", "x..x....x..x...." },
        new[] { "x.x.x.x.x.x.x.x.", "x..x..x...x.x...", "..x...x...x...x.", "x.xx..x.x.xx..x." },
        new[] { "xxx.x.xxx.x.x.xx", "x.xxx.x.x.xxx.x.", "xx.x.xx.xx.x.xx." },
        new[] { "xxxxxxxxxxxxxxxx", "xx.xxx.xxx.xxx.x" },
    };

    private static readonly string[][] MelodyRhythms =
    {
        new[] { "x.......x.......", "x...........x...", "x.....x........." },
        new[] { "x...x.....x.....", "x.....x...x...x.", "..x...x.x......." },
        new[] { "x.x...x.x.x...x.", "x..x..x.x..x....", "x.x.x...x...x.x.", "..x.x.x...x.x..." },
        new[] { "x.xxx.x.x.xxx.x.", "xx.x.xx.x.x.xx.x", "x.x.x.x.x.x.x.x." },
        new[] { "xxxxxxxxxxxxxxxx", "x.xxx.xxx.xxx.xx" },
    };

    private static List<int> Steps(string template) =>
        template.Select((c, i) => (c, i)).Where(t => t.c == 'x').Select(t => t.i).ToList();

    private static MPart Bass(PartRequest r, Random random)
    {
        var part = new MPart { Role = PartRole.Bass, Bars = r.Bars };
        var choices = BassRhythms[Math.Clamp(r.Density, 0, 4)];
        var rhythm = Steps(choices[random.Next(choices.Length)]);
        // Lock to the kick sometimes.
        if (r.KickSteps.Count > 0 && random.NextDouble() < 0.35) rhythm = rhythm.Union(r.KickSteps).OrderBy(s => s).ToList();
        bool octaves = random.NextDouble() < 0.4;
        for (int bar = 0; bar < r.Bars; bar++)
        {
            var chord = ChordAt(r, bar);
            int root = 36 + chord.Root;
            if (root > 43) root -= 12;
            for (int i = 0; i < rhythm.Count; i++)
            {
                int step = rhythm[i];
                int next = i + 1 < rhythm.Count ? rhythm[i + 1] : 16;
                int pitch = root;
                if (octaves && i % 2 == 1) pitch = root + 12;
                else if (!octaves && step >= 12 && random.NextDouble() < 0.4)
                    pitch = root + (random.NextDouble() < 0.5 ? 7 : chord.Quality == ChordQuality.Major ? 4 : 3);
                double len = Math.Max(1, (next - step) * (r.Density >= 3 ? 0.6 : 0.85));
                part.Notes.Add(new MNote(bar + step / 16.0, len / 16.0, pitch, step % 4 == 0 ? 0.9 : 0.7));
            }
        }
        return part;
    }

    private static MPart Melody(PartRequest r, Random random)
    {
        var part = new MPart { Role = PartRole.Melody, Bars = r.Bars };
        var scale = r.Key.Scale;
        var choices = MelodyRhythms[Math.Clamp(r.Density, 0, 4)];
        // A one-bar motif (rhythm + scale-degree steps), repeated with variations.
        var rhythm = Steps(choices[random.Next(choices.Length)]);
        var motifSteps = rhythm.Select(_ => random.Next(-2, 3)).ToList();
        var pitches = Enumerable.Range(55, 37).Where(p => scale.Contains(((p % 12) + 12) % 12)).ToList();
        int current = pitches.IndexOf(pitches.OrderBy(p => Math.Abs(p - 67)).First());
        for (int bar = 0; bar < r.Bars; bar++)
        {
            var chord = ChordAt(r, bar);
            bool vary = bar % 2 == 1 || random.NextDouble() < 0.3;
            var barRhythm = vary && random.NextDouble() < 0.5 ? Steps(choices[random.Next(choices.Length)]) : rhythm;
            for (int i = 0; i < barRhythm.Count; i++)
            {
                int step = barRhythm[i];
                int next = i + 1 < barRhythm.Count ? barRhythm[i + 1] : 16;
                int move = i < motifSteps.Count ? motifSteps[i] : random.Next(-1, 2);
                if (vary && random.NextDouble() < 0.4) move = random.Next(-2, 3);
                current = Math.Clamp(current + move, 0, pitches.Count - 1);
                int pitch = pitches[current];
                // Strong beats land on a chord tone.
                if (step % 8 == 0 && !chord.PitchClasses.Contains(((pitch % 12) + 12) % 12))
                {
                    int nearest = pitches.Where(p => chord.PitchClasses.Contains(((p % 12) + 12) % 12)).OrderBy(p => Math.Abs(p - pitch)).FirstOrDefault(pitch);
                    current = pitches.IndexOf(nearest);
                    pitch = nearest;
                }
                // Drift back towards the middle.
                if (pitch > 79 && random.NextDouble() < 0.5) current = Math.Max(0, current - 2);
                if (pitch < 60 && random.NextDouble() < 0.5) current = Math.Min(pitches.Count - 1, current + 2);
                double len = Math.Max(1, next - step - (random.NextDouble() < 0.3 ? 1 : 0));
                part.Notes.Add(new MNote(bar + step / 16.0, len / 16.0, pitch, step % 4 == 0 ? 0.85 : 0.7));
            }
        }
        return part;
    }

    private static readonly string[][] ChordRhythms =
    {
        new[] { "x...............", "x.......x.......", "x...........x..." },
        new[] { "x.......x.......", "x.........x....." },
        new[] { "..x...x...x...x.", "x..x..x...x.....", "x.....x.....x..." },
        new[] { "x.x.x.x.x.x.x.x.", "..xx..x...xx..x." },
        new[] { "xxxxxxxxxxxxxxxx", "x.xxx.xxx.xxx.xx" },
    };

    private static MPart ChordPart(PartRequest r, Random random)
    {
        var part = new MPart { Role = PartRole.Chords, Bars = r.Bars };
        var choices = ChordRhythms[Math.Clamp(r.Density, 0, 4)];
        var rhythm = Steps(choices[random.Next(choices.Length)]);
        int previousTop = 60 + random.Next(0, 14);
        for (int bar = 0; bar < r.Bars; bar++)
        {
            var chord = ChordAt(r, bar);
            // Voicing: one of the inversions closest to the last chord (keeps the voices moving smoothly).
            var voicings = Voicings(chord).OrderBy(v => Math.Abs(v.Max() - previousTop)).ToList();
            var voicing = voicings[Math.Min(voicings.Count - 1, random.NextDouble() < 0.7 ? 0 : 1)];
            previousTop = voicing.Max();
            for (int i = 0; i < rhythm.Count; i++)
            {
                int step = rhythm[i], next = i + 1 < rhythm.Count ? rhythm[i + 1] : 16;
                double len = r.Density <= 1 ? next - step : Math.Max(1, (next - step) * 0.6);
                foreach (int p in voicing) part.Notes.Add(new MNote(bar + step / 16.0, len / 16.0, p, 0.75));
            }
        }
        return part;
    }

    private static IEnumerable<int[]> Voicings(MChord chord)
    {
        var pcs = chord.PitchClasses;
        for (int inv = 0; inv < 3; inv++)
            foreach (int baseOctave in new[] { 48, 60 })
            {
                var notes = new List<int>();
                int last = -1;
                for (int k = 0; k < 3; k++)
                {
                    int pc = pcs[(k + inv) % 3];
                    int p = baseOctave + pc;
                    while (p <= last) p += 12;
                    notes.Add(p);
                    last = p;
                }
                if (notes.Min() >= 52 && notes.Max() <= 79) yield return notes.ToArray();
            }
    }

    private static readonly (string Kick, string Snare, string Hat, string? Extra)[][] Grooves =
    {
        new (string, string, string, string?)[] { ("x.......x.......", "........x.......", "x...x...x...x...", null) },
        new (string, string, string, string?)[] { ("x...x...x...x...", "....x.......x...", "..x...x...x...x.", null), ("x.....x...x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", null) },
        new (string, string, string, string?)[] { ("x...x...x...x...", "....x.......x...", "xxxxxxxxxxxxxxxx", "..x.....x..x...."), ("x..x..x...x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", "......x.......x.") },
        new (string, string, string, string?)[] { ("x...x...x...x...", "....x..x....x...", "xxxxxxxxxxxxxxxx", "x..x..x..x..x..."), ("x..x..x...x..x..", "....x.......x..x", "xxxxxxxxxxxxxxxx", "..x...x...x...x.") },
        new (string, string, string, string?)[] { ("x.x.x.x.x...x.x.", "....x..x.x..x..x", "xxxxxxxxxxxxxxxx", "x.xx.x.xx.x.x.x.") },
    };

    private static MPart DrumPart(PartRequest r, Random random)
    {
        var part = new MPart { Role = PartRole.Drums, Bars = r.Bars };
        var options = Grooves[Math.Clamp(r.Density, 0, 4)];
        var (kick, snare, hat, extra) = options[random.Next(options.Length)];
        int snareDrum = random.NextDouble() < 0.4 ? 2 : 1;     // clap or snare
        int hatDrum = random.NextDouble() < 0.2 ? 12 : 4;       // shaker or hat
        int extraDrum = new[] { 13, 3, 11, 5 }[random.Next(4)]; // perc, rim, cowbell, open hat
        for (int bar = 0; bar < r.Bars; bar++)
        {
            bool fill = r.Bars > 1 && bar == r.Bars - 1 && random.NextDouble() < 0.5;
            void Row(string template, int drum, double vel, double ghost = 0)
            {
                foreach (int s in Steps(template))
                    part.Notes.Add(new MNote(bar + s / 16.0, 1 / 16.0, drum, s % 4 == 0 ? vel : vel * (1 - ghost)));
            }
            Row(kick, 0, 0.95);
            Row(snare, snareDrum, 0.9);
            Row(hat, hatDrum, 0.7, 0.3);
            if (extra is not null) Row(extra, extraDrum, 0.6);
            if (fill)
                foreach (var (step, tom) in new[] { (12, 10), (13, 10), (14, 9), (15, 8) })
                    part.Notes.Add(new MNote(bar + step / 16.0, 1 / 16.0, tom, 0.85));
        }
        return part;
    }
}
