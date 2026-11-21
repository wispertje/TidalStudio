using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class SequenceTests
{
    private static MelodySequence Melody(params (int Step, int Length, int Midi)[] notes)
    {
        var s = new MelodySequence { Bpm = 120, Length = 16 };
        foreach (var (step, length, midi) in notes) s.Notes.Add(new SeqNote { Step = step, Length = length, Midi = midi });
        return s;
    }

    /// <summary>A plain sine "sample" rendered at <paramref name="midi"/>.</summary>
    private static RenderedNote Sine(int midi, int sr = 8000, double seconds = 1)
    {
        int n = (int)(sr * seconds);
        var l = new float[n];
        double hz = SynthEngine.MidiToHz(midi);
        for (int i = 0; i < n; i++) l[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / sr));
        return new RenderedNote(midi, l, (float[])l.Clone(), sr);
    }

    private static double Energy(float[] x, int from, int to)
    {
        double e = 0;
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++) e += x[i] * x[i];
        return e;
    }

    [Fact]
    public void Melody_code_with_one_sample_uses_note_names_rests_and_lengths()
    {
        var s = Melody((0, 2, 60), (4, 1, 64), (8, 4, 55));
        string code = SequenceCode.Melody(s, "lead", new[] { 60 });
        Assert.Contains("do\n  setcps (120/60/4)\n", code);
        // Grouped by beat with Tidal's ".": a note filling a beat is just its name.
        Assert.Contains("  d1 $ note \"c@2 ~ ~ . e ~ ~ ~ . g4 . ~\" # s \"lead\" # legato 1", code);
        Assert.DoesNotContain("# n", code);
    }

    [Fact]
    public void Notes_crossing_beats_are_grouped_by_half_bar_or_written_flat_with_rest_runs()
    {
        var half = Melody((0, 3, 60), (3, 3, 64), (14, 2, 67));
        Assert.Contains("note \"c@3 e@3 ~ ~ . ~@6 g@2\"", SequenceCode.Melody(half, "lead", new[] { 60 }));
        var flat = Melody((0, 3, 60), (6, 4, 64), (14, 2, 67)); // crosses the half bar too
        Assert.Contains("note \"c@3 ~@3 e@4 ~@4 g@2\"", SequenceCode.Melody(flat, "lead", new[] { 60 }));
    }

    [Fact]
    public void Melody_code_stacks_notes_played_together_and_writes_whole_bars_per_cycle()
    {
        var s = Melody((0, 4, 60), (0, 4, 64), (0, 4, 67), (16, 4, 62));
        s.Length = 32;
        s.End = NoteEnd.Cut;
        s.Orbit = 3;
        string code = SequenceCode.Melody(s, "pad", new[] { 60 });
        // No note crosses a bar line: one bar per cycle with <>, no slow.
        Assert.Contains("d3 $ note \"<[[c,e,g] . ~ . ~ . ~] [d . ~ . ~ . ~]>\" # s \"pad\" # cut 1", code);
    }

    [Fact]
    public void Identical_bars_are_written_once_and_notes_across_bar_lines_slow_down_one_sequence()
    {
        var same = Melody((0, 3, 64), (3, 3, 67), (16, 3, 64), (19, 3, 67));
        same.Length = 32;
        Assert.Contains("d1 $ note \"e@3 g@3 ~ ~ . ~\"", SequenceCode.Melody(same, "x", new[] { 60 }));

        var across = Melody((14, 4, 60), (20, 1, 62));
        across.Length = 32;
        Assert.Contains("d1 $ slow 2 $ note \"~@14 c@4 ~ ~ d ~@11\"", SequenceCode.Melody(across, "x", new[] { 60 }));
    }

    [Fact]
    public void Melody_code_picks_the_closest_rendered_sample_for_each_note()
    {
        var s = Melody((0, 1, 50), (4, 1, 64));
        string code = SequenceCode.Melody(s, "bass", new[] { 48, 60 });
        Assert.Contains("stack", code);
        Assert.Contains("note \"d ~ ~ ~ . ~ . ~ . ~\" # n 0", code); // d3 = c3 sample + 2
        Assert.Contains("note \"~ . e ~ ~ ~ . ~ . ~\" # n 1", code); // e5 = c5 sample + 4
        Assert.Contains("] # s \"bass\" # legato 1", code);
    }

    [Fact]
    public void A_chord_uses_one_sample_the_closest_to_its_lowest_note()
    {
        // F#4 minor (66 69 73): 66 is closest to the c5 sample (60), 73 alone would pick c6 (72).
        var s = Melody((0, 4, 66), (0, 4, 69), (0, 4, 73));
        var units = SequenceCode.Units(s, new[] { 48, 60, 72 });
        Assert.Single(units);
        Assert.Equal(1, units[0].Sample);
        Assert.Contains("note \"[fs,a,cs6] . ~ . ~ . ~\" # s \"x\" # n 1", SequenceCode.Melody(s, "x", new[] { 48, 60, 72 }));
    }

    [Fact]
    public void Swing_and_short_lengths_become_swingBy_and_fast()
    {
        var s = Melody((0, 1, 60));
        s.Swing = 0.25;
        s.Length = 8;
        string code = SequenceCode.Melody(s, "x", new[] { 60 });
        Assert.Contains("d1 $ swingBy 0.125 8 $ fast 2 $ note", code);
        Assert.Equal("slow (12/16) $ ", SequenceCode.Speed(12));
    }

    [Fact]
    public void Note_tokens_follow_tidal_names()
    {
        Assert.Equal("c", SequenceCode.NoteToken(0));
        Assert.Equal("cs", SequenceCode.NoteToken(1));
        Assert.Equal("a4", SequenceCode.NoteToken(-3));
        Assert.Equal("c6", SequenceCode.NoteToken(12));
        Assert.Equal("c3", SequenceCode.NoteToken(-24));
    }

    [Fact]
    public void Beat_code_compresses_rows_and_adds_gain_for_soft_and_accent()
    {
        var s = new BeatSequence { Bpm = 126 };
        for (int i = 0; i < 16; i += 4) s.Row("bd")[i] = BeatSequence.Normal;
        for (int i = 0; i < 16; i++) s.Row("hh")[i] = i % 2 == 0 ? BeatSequence.Accent : BeatSequence.Soft;
        s.Row("sd")[4] = s.Row("sd")[12] = BeatSequence.Normal;
        s.Row("cp")[2] = BeatSequence.Normal;
        s.Muted.Add("cp");
        var drums = new[] { ("bd", "kbd"), ("sd", "ksd"), ("hh", "khh"), ("cp", "kcp") };
        string code = SequenceCode.Beat(s, drums);
        Assert.Contains("  setcps (126/60/4)", code);
        Assert.Contains("[ s \"[kbd ~ ~ ~]*4\"", code);
        Assert.Contains(", s \"[~ ~ ~ ~ ksd ~ ~ ~]*2\"", code);
        Assert.Contains(", s \"khh*16\" # gain \"[1.1 0.85]*8\"", code);
        Assert.DoesNotContain("kcp", code);
    }

    [Fact]
    public void Melody_render_loops_cuts_legato_notes_and_wraps_tails()
    {
        int sr = 8000;
        var s = Melody((0, 2, 60), (12, 1, 67)); // 120 bpm: a step is 0.125 s = 1000 samples
        var loop = SequenceRenderer.Melody(s, new[] { Sine(60, sr) }, sr);
        Assert.Equal(16000, loop.Left.Length);
        Assert.True(Energy(loop.Left, 0, 2000) > 10);
        Assert.True(Energy(loop.Left, 2100, 11900) < 1e-6);   // legato: silent between the notes
        Assert.True(Energy(loop.Left, 12000, 13000) > 10);

        s.End = NoteEnd.Full;                                  // whole sample (1 s = 8 steps): the 2nd note's tail wraps to the start
        var full = SequenceRenderer.Melody(s, new[] { Sine(60, sr) }, sr);
        Assert.True(Energy(full.Left, 2100, 7900) > 10);
        Assert.True(Energy(full.Left, 16000 - 100, 16000) > 0);
        Assert.True(Energy(full.Left, 0, 3900) > Energy(loop.Left, 0, 3900));
    }

    [Fact]
    public void Melody_render_pitches_the_sample_by_resampling()
    {
        int sr = 8000;
        var s = Melody((0, 8, 72)); // an octave above the c5 sample
        var loop = SequenceRenderer.Melody(s, new[] { Sine(60, sr) }, sr);
        // Count zero crossings over 0.5 s: c6 ≈ 523 Hz → about 523 crossings.
        int crossings = 0;
        for (int i = 1; i < 4000; i++) if (loop.Left[i - 1] < 0 != loop.Left[i] < 0) crossings++;
        Assert.InRange(crossings, 500, 545);
    }

    [Fact]
    public void Beat_render_places_hits_with_swing_and_skips_muted_drums()
    {
        int sr = 8000;
        var click = new RenderedNote(60, new float[] { 1, 0, 0, 0 }, new float[] { 1, 0, 0, 0 }, sr);
        var s = new BeatSequence { Bpm = 120, Swing = 0.5 };
        s.Row("bd")[0] = BeatSequence.Normal;
        s.Row("bd")[1] = BeatSequence.Accent;
        s.Row("sd")[4] = BeatSequence.Normal;
        s.Muted.Add("sd");
        var loop = SequenceRenderer.Beat(s, new Dictionary<string, RenderedNote> { ["bd"] = click, ["sd"] = click }, sr);
        Assert.True(loop.Left[0] > 0);
        Assert.True(loop.Left[1500] > loop.Left[0]);  // step 1 swung by half a step, accented (1.1⁴ > 1)
        Assert.Equal(0, loop.Left[4000]);             // muted
    }

    [Fact]
    public void Ideas_stay_in_the_scale_and_round_trip_as_json()
    {
        var r = new Random(4);
        var s = new MelodySequence { Root = 57, Scale = AcidScale.Minor, Length = 32 };
        SequenceIdeas.RandomMelody(s, r);
        for (int i = 0; i < 10; i++) SequenceIdeas.Mutate(s, r);
        var scale = AcidLines.Intervals(AcidScale.Minor).ToHashSet();
        Assert.NotEmpty(s.Notes);
        Assert.All(s.Notes, n => Assert.Contains(((n.Midi - 57) % 12 + 12) % 12, scale));
        Assert.Equal(s.ToJson(), MelodySequence.FromJson(s.ToJson()).ToJson());

        var beat = new BeatSequence { Length = 32 };
        var drums = new[] { ("bd", DrumModel.Kick), ("sd", DrumModel.Snare), ("hh", DrumModel.ClosedHat), ("oh", DrumModel.OpenHat) };
        SequenceIdeas.RandomBeat(beat, drums, r);
        Assert.Equal(BeatSequence.Normal, beat.Row("bd")[0]);
        Assert.Equal(BeatSequence.Normal, beat.Row("sd")[4]);
        for (int i = 0; i < SequenceBase.MaxSteps; i++) Assert.False(beat.Row("oh")[i] > 0 && beat.Row("hh")[i] > 0);
        var back = BeatSequence.FromJson(beat.ToJson());
        Assert.Equal(beat.Row("hh"), back.Row("hh"));
    }
}
