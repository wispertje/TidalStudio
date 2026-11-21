namespace TidalStudio.Core.Synth;

/// <summary>Sounds, demo patterns, the pattern generator and saving for Tidepool Acid.</summary>
public static class AcidLines
{
    // ------------------------------------------------------------------ sounds

    /// <summary>Sound presets: they change the voice and effects, never the patterns.</summary>
    public static readonly IReadOnlyList<(string Name, Action<AcidBox> Apply)> Sounds = new (string, Action<AcidBox>)[]
    {
        ("Classic squelch", b => Set(b, new AcidVoice(), new AcidFx())),
        ("Rubber (slow filter)", b => Set(b,
            new AcidVoice { Cutoff = 260, Resonance = 0.72, EnvMod = 0.7, Decay = 0.9, Accent = 0.5, SlideTime = 0.09, Gate = 0.7, Drive = 0.2 },
            new AcidFx())),
        ("Screamer (distorted)", b => Set(b,
            new AcidVoice { Cutoff = 520, Resonance = 0.88, EnvMod = 0.65, Decay = 0.3, Accent = 0.95, Gate = 0.5, Drive = 0.75 },
            new AcidFx { ReverbMix = 0.1, ReverbSize = 0.4 })),
        ("Deep square", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, Cutoff = 220, Resonance = 0.62, EnvMod = 0.45, Decay = 0.5, Accent = 0.45, Gate = 0.6, Drive = 0.25 },
            new AcidFx())),
        ("Hollow square bite", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, Cutoff = 420, Resonance = 0.84, EnvMod = 0.6, Decay = 0.25, Accent = 0.75, Gate = 0.45, Drive = 0.4 },
            new AcidFx())),
        ("Dub acid (delay)", b => Set(b,
            new AcidVoice { Cutoff = 330, Resonance = 0.8, EnvMod = 0.6, Decay = 0.45, Accent = 0.6, Gate = 0.5, Drive = 0.2 },
            new AcidFx { DelayMix = 0.38, DelaySteps = 3, DelayFeedback = 0.5, ReverbMix = 0.2, ReverbSize = 0.7 })),
        ("Ringing (self-oscillating)", b => Set(b,
            new AcidVoice { Cutoff = 300, Resonance = 0.97, EnvMod = 0.45, Decay = 0.55, Accent = 0.55, Gate = 0.55, Drive = 0.3 },
            new AcidFx { DelayMix = 0.15, DelaySteps = 2, DelayFeedback = 0.3 })),
        ("Thin & clicky", b => Set(b,
            new AcidVoice { Cutoff = 700, Resonance = 0.7, EnvMod = 0.35, Decay = 0.12, Accent = 0.7, Gate = 0.3, Drive = 0.1 },
            new AcidFx { ReverbMix = 0.12, ReverbSize = 0.35 })),
        ("Warehouse (drive + room)", b => Set(b,
            new AcidVoice { Cutoff = 400, Resonance = 0.83, EnvMod = 0.6, Decay = 0.38, Accent = 0.8, Gate = 0.55, Drive = 0.55 },
            new AcidFx { DelayMix = 0.12, DelaySteps = 6, DelayFeedback = 0.35, ReverbMix = 0.25, ReverbSize = 0.8 })),
    };

    /// <summary>
    /// Synth pop sounds for the same box: rounder, less resonant, longer gates, more echo. Use them with the synth pop
    /// patterns (octave basses and arpeggios), and Key higher (c3/c4) for arpeggios.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, Action<AcidBox> Apply)> PopSounds = new (string, Action<AcidBox>)[]
    {
        ("Pop octave bass", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, Cutoff = 900, Resonance = 0.2, EnvMod = 0.35, Decay = 0.25, Accent = 0.3, Gate = 0.45, Drive = 0.1 },
            new AcidFx())),
        ("Driving eighths bass", b => Set(b,
            new AcidVoice { Cutoff = 700, Resonance = 0.3, EnvMod = 0.4, Decay = 0.2, Accent = 0.35, Gate = 0.7, Drive = 0.15 },
            new AcidFx())),
        ("Warm sequencer bass", b => Set(b,
            new AcidVoice { Cutoff = 480, Resonance = 0.35, EnvMod = 0.3, Decay = 0.35, Accent = 0.3, SlideTime = 0.04, Gate = 0.85, Drive = 0.05 },
            new AcidFx { ReverbMix = 0.08, ReverbSize = 0.4 })),
        ("Bright arp", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, Cutoff = 1800, Resonance = 0.25, EnvMod = 0.4, Decay = 0.15, Accent = 0.4, Gate = 0.35, Drive = 0 },
            new AcidFx { DelayMix = 0.3, DelaySteps = 3, DelayFeedback = 0.35, ReverbMix = 0.2, ReverbSize = 0.6 })),
        ("Plucky sequence (echo)", b => Set(b,
            new AcidVoice { Cutoff = 1100, Resonance = 0.4, EnvMod = 0.5, Decay = 0.12, Accent = 0.45, Gate = 0.3, Drive = 0.05 },
            new AcidFx { DelayMix = 0.35, DelaySteps = 6, DelayFeedback = 0.45, ReverbMix = 0.25, ReverbSize = 0.7 })),
    };

    /// <summary>
    /// Synthwave sounds: chorused and detuned basses with a sub, pulse basses and echoing arps (they use the pulse width,
    /// sub, detune, key tracking, chorus and tape controls). Use them with the synth pop patterns.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, Action<AcidBox> Apply)> SynthwaveSounds = new (string, Action<AcidBox>)[]
    {
        ("Night drive bass", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, PulseWidth = 0.4, Sub = 0.6, Cutoff = 650, Resonance = 0.25, EnvMod = 0.3, Decay = 0.3, Accent = 0.25, Gate = 0.8, Drive = 0.1, KeyTrack = 0.3 },
            new AcidFx { ChorusMix = 0.3, ChorusRate = 0.7 })),
        ("Detuned saw bass", b => Set(b,
            new AcidVoice { Detune = 0.5, Sub = 0.3, Cutoff = 800, Resonance = 0.3, EnvMod = 0.4, Decay = 0.2, Accent = 0.3, Gate = 0.6, Drive = 0.12 },
            new AcidFx { ChorusMix = 0.25, Tape = 0.1 })),
        ("Pulse bass (narrow)", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, PulseWidth = 0.22, Sub = 0.45, Cutoff = 1000, Resonance = 0.15, EnvMod = 0.35, Decay = 0.22, Accent = 0.3, Gate = 0.55, Drive = 0.05 },
            new AcidFx())),
        ("Neon arp (echo)", b => Set(b,
            new AcidVoice { Wave = AcidWave.Square, PulseWidth = 0.35, Detune = 0.3, KeyTrack = 0.6, Cutoff = 1400, Resonance = 0.3, EnvMod = 0.45, Decay = 0.15, Accent = 0.35, Gate = 0.4, Drive = 0 },
            new AcidFx { DelayMix = 0.35, DelaySteps = 3, DelayFeedback = 0.45, ReverbMix = 0.3, ReverbSize = 0.8, ChorusMix = 0.35 })),
        ("Tape arp", b => Set(b,
            new AcidVoice { Detune = 0.2, KeyTrack = 0.5, Cutoff = 1200, Resonance = 0.25, EnvMod = 0.4, Decay = 0.18, Accent = 0.3, Gate = 0.45, Drive = 0.05 },
            new AcidFx { DelayMix = 0.3, DelaySteps = 6, DelayFeedback = 0.4, ReverbMix = 0.3, ReverbSize = 0.85, ChorusMix = 0.4, ChorusRate = 0.5, Tape = 0.5 })),
    };

    /// <summary>Every sound: acid, synth pop and synthwave.</summary>
    public static IEnumerable<(string Name, Action<AcidBox> Apply)> AllSounds => Sounds.Concat(PopSounds).Concat(SynthwaveSounds);

    private static void Set(AcidBox box, AcidVoice voice, AcidFx fx)
    {
        voice.Level = box.Voice.Level;
        box.Voice = voice;
        box.Fx = fx;
    }

    // ------------------------------------------------------------------ patterns

    /// <summary>Demo lines for the slots of a new box (root C; the last slot is left empty).</summary>
    public static readonly string[] DemoPatterns =
    {
        "C! C ^C C~ D# - C ^D# C - G~ G# C! ^C C _C",
        "C~ C ^C! - D#~ F C - ^C~ ^C# C! G - C ^G# C",
        "C C ^C C D# C ^C C F C ^C! C G~ G# C _C",
        "C~ ^C~ C - - D#! - C~ C' - G~ F D# - C! -",
        "C C# C ^C C! C# - C ^C# C C - _A# C~ C# C!",
        "- C - ^C! - C - D#~ - C - ^G - C! - ^D#",
        "C! C ^C C~ D# - C ^D# C - G~ G# C! ^C C _C C! C ^C C~ F - C ^F C - A#~ ^C ^D#! ^C G~ G",
    };

    /// <summary>
    /// Synth pop lines (root C): octave pulses, driving sixteenths, a four-chord octave bass over two bars
    /// (C, A♭, B♭, G), arpeggios and syncopated lines.
    /// </summary>
    public static readonly string[] PopPatterns =
    {
        "C - ^C - C - ^C - C - ^C - C - ^C -",
        "C C ^C C C C ^C C C C ^C C C C ^C C",
        "C - ^C - C - ^C - _G# - G# - _G# - G# - _A# - A# - _A# - A# - _G - G - _G - G -",
        "C D# G ^C G D# C D# G ^C ^D# ^C G D# G ^C",
        "C! E G ^C G E C! E G ^C ^E ^C G E C G",
        "- C - ^C - C - ^C - _A# - A# - _G# - G#",
        "C - - C - - ^C - C - - C - - G A#",
    };

    public static AcidBox Default() => FromPatterns(DemoPatterns);

    /// <summary>A box with these patterns in slots A.. (the rest empty).</summary>
    public static AcidBox FromPatterns(IEnumerable<string> patterns)
    {
        var box = new AcidBox();
        foreach (var p in patterns) box.Patterns.Add(AcidPattern.Parse(p));
        box.Repair();
        return box;
    }

    public static int[] Intervals(AcidScale scale) => scale switch
    {
        AcidScale.Phrygian => new[] { 0, 1, 3, 5, 7, 8, 10 },
        AcidScale.Dorian => new[] { 0, 2, 3, 5, 7, 9, 10 },
        AcidScale.MinorPentatonic => new[] { 0, 3, 5, 7, 10 },
        AcidScale.Major => new[] { 0, 2, 4, 5, 7, 9, 11 },
        AcidScale.Chromatic => Enumerable.Range(0, 12).ToArray(),
        _ => new[] { 0, 2, 3, 5, 7, 8, 10 },
    };

    /// <summary>A new random acid line: mostly the root, notes from the scale, octave jumps, accents and slides.</summary>
    public static void Randomise(AcidPattern pattern, AcidScale scale, Random random, double density = 0.75)
    {
        var notes = Intervals(scale);
        for (int i = 0; i < AcidPattern.MaxSteps; i++)
            pattern.Steps[i] = RandomStep(notes, random, i == 0 ? 1 : density);
    }

    /// <summary>Changes a few steps (a quarter of them at most), keeping the character of the line.</summary>
    public static void Mutate(AcidPattern pattern, AcidScale scale, Random random)
    {
        var notes = Intervals(scale);
        int changes = Math.Max(1, pattern.Length / 6);
        for (int c = 0; c < changes; c++)
        {
            int i = random.Next(pattern.Length);
            var s = pattern.Steps[i];
            switch (random.Next(5))
            {
                case 0: pattern.Steps[i] = RandomStep(notes, random, 0.8); break;
                case 1: s.Accent = !s.Accent; break;
                case 2: s.Slide = !s.Slide; break;
                case 3: s.Octave = s.Octave == 0 ? (random.Next(3) == 0 ? -1 : 1) : 0; break;
                default: if (s.Gate) s.Note = notes[random.Next(notes.Length)]; else pattern.Steps[i] = RandomStep(notes, random, 1); break;
            }
        }
    }

    private static AcidStep RandomStep(int[] notes, Random r, double density)
    {
        var s = new AcidStep { Gate = r.NextDouble() < density };
        if (!s.Gate) return s;
        double pick = r.NextDouble();
        s.Note = pick < 0.45 ? 0 : pick < 0.52 ? 12 : notes[r.Next(notes.Length)];
        double oct = r.NextDouble();
        s.Octave = s.Note == 12 ? 0 : oct < 0.18 ? 1 : oct < 0.27 ? -1 : 0;
        s.Accent = r.NextDouble() < 0.22;
        s.Slide = r.NextDouble() < 0.18;
        return s;
    }

    /// <summary>Moves the pattern one step left (-1) or right (+1), wrapping around within its length.</summary>
    public static void Rotate(AcidPattern pattern, int by)
    {
        int len = pattern.Length;
        var steps = pattern.Steps.Take(len).ToList();
        for (int i = 0; i < len; i++) pattern.Steps[((i + by) % len + len) % len] = steps[i];
    }

    /// <summary>Transposes every note by semitones, keeping notes within the box's three octaves.</summary>
    public static void Transpose(AcidPattern pattern, int semitones)
    {
        foreach (var s in pattern.Steps.Where(s => s.Gate))
        {
            int total = Math.Clamp(s.Semitones + semitones, -12, 24);
            int octave = total >= 12 ? 1 : total < 0 ? -1 : 0;
            int note = total - 12 * octave;
            if (octave == 1 && note == 0) { octave = 0; note = 12; }
            s.Octave = octave;
            s.Note = note;
        }
    }

    // ------------------------------------------------------------------ saving

    /// <summary>File name of a slot's loop: sound_00_A.wav (sorted by name, so n 0 = A).</summary>
    public static string FileName(string sound, int slot) => $"{sound}_{slot:00}_{AcidBox.SlotNames[slot]}.wav";

    /// <summary>
    /// Writes slots A.. up to the last pattern with notes into &lt;root&gt;/&lt;name&gt; (empty slots in between become
    /// silent loops so n keeps matching the slot letter), replacing the folder's .wav files. Returns the paths.
    /// </summary>
    public static List<string> Write(string samplesRoot, AcidBox box)
    {
        string folder = Path.Combine(samplesRoot, box.Name);
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "*.wav")) File.Delete(old);
        var paths = new List<string>();
        for (int slot = 0; slot < box.SavedSlotCount; slot++)
        {
            var loop = AcidEngine.Render(box, slot);
            string path = Path.Combine(folder, FileName(box.Name, slot));
            SampleExport.WriteWav(path, loop);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Tidal code that plays the saved loops in time.</summary>
    public static string Example(AcidBox box, int slot)
    {
        var p = box.Patterns[Math.Clamp(slot, 0, box.Patterns.Count - 1)];
        string cycles = AcidBox.Cycles(p);
        string loopAt = cycles == "1" ? "loopAt 1" : $"loopAt {cycles}";
        int chops = p.Length;
        return $"-- {box.Bpm:0.##} bpm; pattern {AcidBox.SlotNames[slot]} lasts {cycles} cycle(s)\n" +
               $"{box.CpsExpression}\n" +
               $"d1 $ {loopAt} $ chop {chops} $ s \"{box.Name}\" # n {slot}\n" +
               $"-- switch patterns each bar: d1 $ {loopAt} $ chop {chops} $ s \"{box.Name}\" # n \"<0 1 0 2>\"";
    }
}
