using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class FmTests
{
    private static double Peak(RenderedNote n) => n.Left.Concat(n.Right).Max(x => Math.Abs(x));

    /// <summary>Strength of one frequency in the first part of a channel (single-bin DFT).</summary>
    private static double Magnitude(float[] x, int sr, double hz, int length)
    {
        double re = 0, im = 0;
        for (int i = 0; i < length; i++)
        {
            double a = 2 * Math.PI * hz * i / sr;
            re += x[i] * Math.Cos(a);
            im += x[i] * Math.Sin(a);
        }
        return Math.Sqrt(re * re + im * im) / length;
    }

    [Fact]
    public void Every_algorithm_links_downwards_and_has_a_carrier()
    {
        foreach (var a in FmPatch.Algorithms)
        {
            Assert.All(a.Links, l => Assert.True(l.From > l.To && l.To >= 1 && l.From <= 6, $"{a.Name}: {l.From}>{l.To}"));
            Assert.NotEmpty(a.Carriers);
        }
        Assert.Equal(new[] { 1 }, FmPatch.Algorithms[0].Carriers);
        Assert.Equal(new[] { 1, 3, 5 }, FmPatch.Algorithms[2].Carriers);
        Assert.Equal(6, FmPatch.Algorithms[8].Carriers.Count);
    }

    [Fact]
    public void Ratios_round_trip()
    {
        foreach (double r in new[] { 0.5, 0.75, 1, 1.41, 2, 3.5, 7, 14 })
            Assert.Equal(r, new FmOperator().WithRatio(r).Ratio, 3);
    }

    [Fact]
    public void A_single_carrier_is_a_sine_at_the_note()
    {
        var p = new FmPatch { Algorithm = 8, Normalize = false, Hold = 0.5 };
        for (int k = 1; k < 6; k++) p.Operators[k].Enabled = false;
        p.Operators[0] = new FmOperator { Level = 1, Envelope = new() { Attack = 0, Decay = 0, Sustain = 1, Release = 0.05 } };
        var note = FmEngine.Render(p, 69); // a5 = 440 Hz
        int n = note.SampleRate / 4;
        double at440 = Magnitude(note.Left, note.SampleRate, 440, n);
        double at880 = Magnitude(note.Left, note.SampleRate, 880, n);
        Assert.True(at440 > 0.1, $"440 Hz: {at440}");
        Assert.True(at880 < at440 * 0.01, $"880 Hz: {at880} vs {at440}");
    }

    [Fact]
    public void Modulation_adds_sidebands_and_depth_controls_them()
    {
        RenderedNote Render(double depth)
        {
            var p = new FmPatch { Algorithm = 2, Normalize = false, Hold = 0.5 };
            for (int k = 2; k < 6; k++) p.Operators[k].Enabled = false;
            p.Operators[0] = new FmOperator { Level = 1, Envelope = new() { Attack = 0, Decay = 0, Sustain = 1, Release = 0.05 } };
            p.Operators[1] = new FmOperator { Level = depth, Envelope = new() { Attack = 0, Decay = 0, Sustain = 1, Release = 0.05 } };
            return FmEngine.Render(p, 69);
        }
        // Small indices only: deeper, the harmonics follow Bessel functions and rise and fall again.
        var soft = Render(0.1);
        var hard = Render(0.3);
        int n = soft.SampleRate / 4;
        // 1:1 modulation puts energy on the harmonics of 440 Hz.
        double softH2 = Magnitude(soft.Left, soft.SampleRate, 880, n), hardH2 = Magnitude(hard.Left, hard.SampleRate, 880, n);
        Assert.True(softH2 > 0.005, $"soft 2nd harmonic {softH2}");
        Assert.True(hardH2 > softH2 * 2, $"hard {hardH2} vs soft {softH2}");
    }

    [Fact]
    public void Every_preset_renders_clean_audio()
    {
        var names = new HashSet<string>();
        foreach (var (name, create) in FmPresets.All)
        {
            var p = create();
            Assert.True(SampleExport.IsValidName(p.Name), $"{name}: {p.Name}");
            if (name != "Init") Assert.True(names.Add(p.Name), $"duplicate sound name {p.Name}");
            p.MaxLength = Math.Min(p.MaxLength, 2);
            var notes = SynthPatch.ParseNotes(p.Notes);
            Assert.NotEmpty(notes);
            var note = FmEngine.Render(p, notes[0]);
            Assert.True(note.Left.Length > note.SampleRate / 20, $"{name} too short");
            Assert.All(note.Left, x => Assert.True(float.IsFinite(x)));
            double peak = Peak(note);
            Assert.True(peak is > 0.5 and <= 1, $"{name}: peak {peak}");
        }
    }

    [Fact]
    public void Preset_names_do_not_clash_with_the_other_synths()
    {
        var others = SynthPresets.All.Select(p => p.Create().Name).ToHashSet();
        foreach (var (_, create) in DrumKits.All) others.Add(create().Name);
        foreach (var (name, create) in FmPresets.All)
            Assert.DoesNotContain(create().Name, others);
    }

    [Fact]
    public void Rendering_is_deterministic_and_json_round_trips()
    {
        var p = FmPresets.Create("Tine piano");
        p.MaxLength = 1;
        var copy = FmPatch.FromJson(p.ToJson());
        Assert.Equal(p.ToJson(), copy.ToJson());
        var a = FmEngine.Render(p, 60);
        var b = FmEngine.Render(copy, 60);
        Assert.Equal(a.Left, b.Left);
    }

    [Fact]
    public void Key_rate_shortens_high_notes()
    {
        var p = FmPresets.Create("Marimba");
        p.Normalize = false;
        Assert.True(FmEngine.Render(p, 84).Left.Length < FmEngine.Render(p, 48).Left.Length);
    }

    [Fact]
    public void Repair_keeps_six_operators()
    {
        var p = FmPatch.FromJson("{\"Algorithm\": 99, \"Operators\": [{}]}");
        Assert.Equal(6, p.Operators.Count);
        Assert.Equal(FmPatch.Algorithms.Count - 1, p.Algorithm);
    }
}
