using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class AcidTests
{
    private static AcidBox SmallBox()
    {
        var box = AcidLines.Default();
        box.SampleRate = 22050;
        return box;
    }

    [Fact]
    public void Parse_and_ToString_round_trip()
    {
        const string text = "C! C ^C C~ D# - C _A# C' Eb";
        var p = AcidPattern.Parse(text);
        Assert.Equal(10, p.Length);
        Assert.True(p.Steps[0].Accent);
        Assert.Equal(1, p.Steps[2].Octave);
        Assert.True(p.Steps[3].Slide);
        Assert.False(p.Steps[5].Gate);
        Assert.Equal(-1, p.Steps[7].Octave);
        Assert.Equal(10, p.Steps[7].Note);
        Assert.Equal(12, p.Steps[8].Note);
        Assert.Equal(3, p.Steps[9].Note); // Eb = D#
        Assert.Equal("C! C ^C C~ D# - C _A# C' D#", p.ToString());
        Assert.Throws<FormatException>(() => AcidPattern.Parse("C H"));
    }

    [Fact]
    public void Demo_patterns_parse_and_fill_seven_slots()
    {
        var box = AcidLines.Default();
        Assert.Equal(AcidBox.Slots, box.Patterns.Count);
        Assert.Equal(7, box.SavedSlotCount);
        Assert.Equal(32, box.Patterns[6].Length);
        Assert.True(box.Patterns[7].IsEmpty);
        Assert.Empty(box.Validate());
    }

    [Fact]
    public void Loop_has_the_exact_length_of_the_pattern_and_is_clean()
    {
        var box = SmallBox();
        box.Bpm = 120; // 16 steps = 2 s
        var loop = AcidEngine.Render(box, 0);
        Assert.Equal(2 * 22050, loop.Left.Length);
        Assert.All(loop.Left, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1));
        float peak = loop.Left.Max(x => Math.Abs(x));
        Assert.InRange(peak, 0.5f, 0.95f);

        var twice = AcidEngine.Render(box, 0, repeats: 2);
        Assert.Equal(2 * loop.Left.Length, twice.Left.Length);
    }

    [Fact]
    public void Loop_is_seamless()
    {
        // Rendering two passes: the second must continue the first without a jump.
        var box = SmallBox();
        box.Fx.DelayMix = 0.4;
        box.Fx.ReverbMix = 0.3;
        box.Normalize = false;
        var one = AcidEngine.Render(box, 1);
        var two = AcidEngine.Render(box, 1, repeats: 2);
        // The loop's start and the start of its second repeat are the same signal.
        for (int i = 0; i < 2000; i++)
            Assert.True(Math.Abs(one.Left[i] - two.Left[one.Left.Length + i]) < 0.02, $"sample {i} differs");
    }

    [Fact]
    public void Loop_is_seamless_with_chorus_tape_sub_and_detune()
    {
        var box = SmallBox();
        box.Normalize = false;
        box.Voice.Sub = 0.5; box.Voice.Detune = 0.6; box.Voice.KeyTrack = 0.5;
        box.Fx.ChorusMix = 0.6; box.Fx.ChorusRate = 0.7; box.Fx.Tape = 0.7; box.Fx.ReverbMix = 0.3;
        var one = AcidEngine.Render(box, 1);
        var two = AcidEngine.Render(box, 1, repeats: 2);
        for (int i = 0; i < 2000; i++)
            Assert.True(Math.Abs(one.Left[i] - two.Left[one.Left.Length + i]) < 0.02, $"sample {i} differs");
    }

    [Fact]
    public void Every_new_control_changes_the_sound()
    {
        var box = SmallBox();
        box.Normalize = false;
        var plain = AcidEngine.Render(box, 0).Left;
        var changes = new (string Name, Action<AcidBox> Set)[]
        {
            ("pulse width", b => { b.Voice.Wave = AcidWave.Square; b.Voice.PulseWidth = 0.2; }),
            ("sub", b => b.Voice.Sub = 0.6),
            ("detune", b => b.Voice.Detune = 0.5),
            ("key tracking", b => b.Voice.KeyTrack = 0.8),
            ("chorus", b => b.Fx.ChorusMix = 0.5),
            ("tape", b => b.Fx.Tape = 0.6),
        };
        var square = SmallBox();
        square.Normalize = false;
        square.Voice.Wave = AcidWave.Square;
        var plainSquare = AcidEngine.Render(square, 0).Left;
        foreach (var (name, set) in changes)
        {
            var b = SmallBox();
            b.Normalize = false;
            set(b);
            var reference = b.Voice.Wave == AcidWave.Square ? plainSquare : plain;
            var changed = AcidEngine.Render(b, 0).Left;
            Assert.True(changed.All(float.IsFinite), name);
            double difference = changed.Zip(reference, (x, y) => Math.Abs(x - y)).Max();
            Assert.True(difference > 0.01, $"{name} made no difference");
        }
    }

    [Fact]
    public void Boxes_saved_before_the_new_controls_sound_the_same()
    {
        var box = SmallBox();
        AcidLines.Sounds[2].Apply(box);
        box.SampleRate = 22050;
        // An older box: the JSON has none of the new properties.
        string json = box.ToJson();
        string old = string.Join('\n', json.Split('\n').Where(l =>
            !new[] { "\"PulseWidth\"", "\"Sub\"", "\"Detune\"", "\"KeyTrack\"", "\"ChorusMix\"", "\"ChorusRate\"", "\"Tape\"" }.Any(l.Contains)));
        old = System.Text.RegularExpressions.Regex.Replace(old, ",(\\s*})", "$1"); // drop trailing commas left behind
        Assert.DoesNotContain("PulseWidth", old);
        var loaded = AcidBox.FromJson(old);
        Assert.Equal(0.5, loaded.Voice.PulseWidth);
        Assert.Equal(0, loaded.Fx.Tape);
        Assert.Equal(AcidEngine.Render(box, 0).Left, AcidEngine.Render(loaded, 0).Left);
    }

    [Fact]
    public void Slide_glides_instead_of_retriggering()
    {
        var box = SmallBox();
        box.Normalize = false;
        box.Voice.Gate = 0.5;
        box.Patterns[0] = AcidPattern.Parse("C - - - C - - - - - - - - - - -");
        var plain = AcidEngine.Render(box, 0);
        box.Patterns[0] = AcidPattern.Parse("C~ ^C - - - - - - - - - - - - - -");
        var slid = AcidEngine.Render(box, 0);
        int step = (int)(box.StepSeconds * box.SampleRate);
        // Without slide the first note stops after half a step; with slide it keeps sounding into step 2.
        double Energy(float[] x, int from, int to) => x.Skip(from).Take(to - from).Sum(v => (double)v * v);
        Assert.True(Energy(plain.Left, (int)(step * 0.7), step) < Energy(slid.Left, (int)(step * 0.7), step) * 0.2);
    }

    [Fact]
    public void Accent_is_louder()
    {
        var box = SmallBox();
        box.Normalize = false;
        box.Voice.Drive = 0;
        box.Patterns[0] = AcidPattern.Parse("C - - - - - - - - - - - - - - -");
        var normal = AcidEngine.Render(box, 0);
        box.Patterns[0] = AcidPattern.Parse("C! - - - - - - - - - - - - - - -");
        var accented = AcidEngine.Render(box, 0);
        Assert.True(accented.Left.Max(x => Math.Abs(x)) > normal.Left.Max(x => Math.Abs(x)) * 1.2);
    }

    [Fact]
    public void Every_sound_preset_renders()
    {
        foreach (var (name, apply) in AcidLines.AllSounds)
        {
            var box = SmallBox();
            apply(box);
            var loop = AcidEngine.Render(box, 2);
            Assert.True(loop.Left.Max(x => Math.Abs(x)) > 0.3, name);
            Assert.All(loop.Right, s => Assert.True(float.IsFinite(s), name));
        }
    }

    [Fact]
    public void Synth_pop_patterns_parse()
    {
        var box = AcidLines.FromPatterns(AcidLines.PopPatterns);
        Assert.Equal(AcidLines.PopPatterns.Length, box.SavedSlotCount);
        Assert.Equal(32, box.Patterns[2].Length);
        Assert.All(box.Patterns.Take(box.SavedSlotCount), p => Assert.False(p.IsEmpty));
        Assert.Equal(AcidLines.AllSounds.Count(), AcidLines.AllSounds.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void Randomise_mutate_rotate_and_transpose()
    {
        var p = new AcidPattern();
        AcidLines.Randomise(p, AcidScale.Phrygian, new Random(3));
        Assert.True(p.Steps[0].Gate);
        Assert.All(p.Steps.Where(s => s.Gate), s => Assert.Contains(s.Note % 12, AcidLines.Intervals(AcidScale.Phrygian)));
        string before = p.ToString();
        AcidLines.Mutate(p, AcidScale.Phrygian, new Random(4));
        Assert.NotEqual(before, p.ToString());

        var q = AcidPattern.Parse("C D - E");
        AcidLines.Rotate(q, 1);
        Assert.Equal("E C D -", q.ToString());
        AcidLines.Rotate(q, -1);
        Assert.Equal("C D - E", q.ToString());

        AcidLines.Transpose(q, 12);
        Assert.Equal("C' ^D - ^E", q.ToString());
        AcidLines.Transpose(q, -24);
        Assert.Equal("_C _D - _E", q.ToString());
    }

    [Fact]
    public void Json_round_trip_and_repair()
    {
        var box = AcidLines.Default();
        box.Name = "mybass";
        box.Voice.Cutoff = 512;
        var copy = AcidBox.FromJson(box.ToJson());
        Assert.Equal("mybass", copy.Name);
        Assert.Equal(512, copy.Voice.Cutoff);
        Assert.Equal(box.Patterns[3].ToString(), copy.Patterns[3].ToString());

        var broken = AcidBox.FromJson("{\"Name\":\"x\",\"Patterns\":[{\"Length\":99,\"Steps\":[{\"Gate\":true,\"Note\":40,\"Octave\":5}]}]}");
        Assert.Equal(AcidBox.Slots, broken.Patterns.Count);
        Assert.Equal(AcidPattern.MaxSteps, broken.Patterns[0].Length);
        Assert.Equal(12, broken.Patterns[0].Steps[0].Note);
        Assert.Equal(1, broken.Patterns[0].Steps[0].Octave);
    }

    [Fact]
    public void Validate_and_write()
    {
        var box = SmallBox();
        box.Name = "Bad Name";
        Assert.NotEmpty(box.Validate());
        box.Name = "testacid";
        box.Patterns[2] = new AcidPattern(); // an empty slot in between stays (silent), so n keeps matching the letter
        string root = Path.Combine(Path.GetTempPath(), "tidalstudio-acid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AcidLines.Write(root, box);
            Assert.Equal(7, paths.Count);
            Assert.Equal("testacid_00_A.wav", Path.GetFileName(paths[0]));
            Assert.Equal("testacid_06_G.wav", Path.GetFileName(paths[6]));
            Assert.All(paths, p => Assert.True(new FileInfo(p).Length > 1000));
        }
        finally { Directory.Delete(root, true); }

        var empty = new AcidBox { Name = "ok" };
        empty.Repair();
        Assert.Contains(empty.Validate(), p => p.Contains("empty"));
    }

    [Fact]
    public void Example_code_uses_loopAt_and_chop()
    {
        var box = AcidLines.Default();
        Assert.Contains("d1 $ loopAt 1 $ chop 16 $ s \"acidloop\" # n 0", AcidLines.Example(box, 0));
        Assert.Contains("loopAt 2 $ chop 32", AcidLines.Example(box, 6));
        Assert.Contains("setcps (130/60/4)", AcidLines.Example(box, 0));
    }
}
