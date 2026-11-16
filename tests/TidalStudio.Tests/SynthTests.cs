using System.Numerics;
using TidalStudio.Core.Audio;
using TidalStudio.Core.Synth;

namespace TidalStudio.Tests;

public class SynthTests
{
    private static SynthPatch Sine()
    {
        var p = new SynthPatch { Normalize = true, Hold = 1 };
        p.Oscillators[0].Position = 0; // pure sine
        foreach (var o in p.Oscillators.Skip(1)) o.Enabled = false;
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new EnvelopeSettings { Attack = 0.01, Decay = 0.1, Sustain = 1, Release = 0.1 };
        return p;
    }

    private static double PeakFrequency(float[] samples, int start, int sampleRate)
    {
        const int n = 16384;
        var buffer = new Complex[n];
        for (int i = 0; i < n; i++) buffer[i] = new Complex(samples[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)), 0);
        Fft.Transform(buffer);
        int best = 1;
        for (int k = 1; k < n / 2; k++) if (buffer[k].Magnitude > buffer[best].Magnitude) best = k;
        return best * (double)sampleRate / n;
    }

    [Theory]
    [InlineData(69, 440.0)]   // a5 in Tidal terms = MIDI 69
    [InlineData(60, 261.63)]  // c5 = note 0
    [InlineData(48, 130.81)]
    public void Notes_render_at_the_right_pitch(int midi, double expectedHz)
    {
        var note = SynthEngine.Render(Sine(), midi);
        double hz = PeakFrequency(note.Left, note.SampleRate / 4, note.SampleRate);
        Assert.InRange(hz, expectedHz * 0.99, expectedHz * 1.01);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var patch = SynthPresets.Create("Supersaw lead");
        var a = SynthEngine.Render(patch, 60);
        var b = SynthEngine.Render(patch, 60);
        Assert.Equal(a.Left, b.Left);
        Assert.Equal(a.Right, b.Right);
    }

    [Fact]
    public void Every_preset_renders_clean_audio()
    {
        foreach (var (name, create) in SynthPresets.All)
        {
            var patch = create();
            foreach (int midi in SynthPatch.ParseNotes(patch.Notes))
            {
                var note = SynthEngine.Render(patch, midi);
                Assert.True(note.Left.Length >= (int)(patch.Hold * note.SampleRate * 0.99), $"{name}: shorter than the held part");
                Assert.True(note.Seconds <= Math.Max(patch.MaxLength, patch.Hold + 0.05) + 0.01, $"{name}: longer than MaxLength");
                float peak = 0;
                foreach (var s in note.Left.Concat(note.Right))
                {
                    Assert.True(float.IsFinite(s), $"{name}: NaN/infinity");
                    peak = Math.Max(peak, Math.Abs(s));
                }
                Assert.InRange(peak, 0.5f, 1f); // normalised to -1 dBFS, not silent, not clipping
            }
        }
    }

    [Fact]
    public void The_release_tail_rings_after_the_key_is_let_go()
    {
        var patch = Sine();
        patch.AmpEnvelope.Release = 0.5;
        var note = SynthEngine.Render(patch, 60);
        int afterRelease = (int)(1.2 * note.SampleRate); // 0.2 s into the 0.5 s release
        Assert.True(note.Left.Length > afterRelease);
        Assert.True(note.Left.Skip(afterRelease).Take(2000).Max(Math.Abs) > 0.01f);
    }

    [Theory]
    [InlineData("c5", 60)]
    [InlineData("a5", 69)]
    [InlineData("cs4", 49)]
    [InlineData("c#4", 49)]
    [InlineData("ef3", 39)]
    [InlineData("e", 64)]
    public void Tidal_note_names_parse(string name, int midi)
    {
        Assert.True(SynthPatch.TryParseNoteName(name, out int parsed));
        Assert.Equal(midi, parsed);
    }

    [Fact]
    public void Note_lists_accept_names_and_numbers()
    {
        Assert.Equal(new[] { 36, 48, 60 }, SynthPatch.ParseNotes("c3 c4 c5"));
        Assert.Equal(new[] { 60, 67, 72 }, SynthPatch.ParseNotes("0 7 12"));
        Assert.Equal("c5", SynthPatch.NoteName(60));
        Assert.Equal("cs5", SynthPatch.NoteName(61));
        Assert.Equal("b4", SynthPatch.NoteName(59));
    }

    [Theory]
    [InlineData("mysynth", true)]
    [InlineData("bass-2", true)]
    [InlineData("pad_soft", true)]
    [InlineData("My Synth", false)]
    [InlineData("2bass", false)]
    [InlineData("bd:3", false)]
    [InlineData("", false)]
    public void Sound_names_must_work_in_tidal(string name, bool valid) =>
        Assert.Equal(valid, SampleExport.IsValidName(name));

    [Fact]
    public void Startup_script_commented_samples_line_is_uncommented()
    {
        // The shape of the user's startup.scd.
        string script = string.Join("\r\n",
            "\t\t~dirt.loadSoundFiles;  // default Dirt-Samples",
            "\t\t~dirt.loadSoundFiles(\"C:/Users/me/Music/tidal-drum-machines/machines/*/*\");",
            "\t\t// your own sample folders (each subfolder = one sound name):",
            "\t\t// ~dirt.loadSoundFiles(\"C:/Users/me/Music/samples/*\");",
            "\t\ts.sync;");
        string? edited = TidalStudio.Core.SuperCollider.StartupScript.AddSamplesFolder(script, @"C:\Users\me\Music\samples");
        Assert.NotNull(edited);
        var lines = edited!.Split("\r\n");
        Assert.Equal(5, lines.Length);                                     // nothing added, one line changed
        Assert.Equal("\t\t~dirt.loadSoundFiles(\"C:/Users/me/Music/samples/*\");", lines[3]);
        Assert.Equal("\t\t// your own sample folders (each subfolder = one sound name):", lines[2]); // prose comment untouched
    }

    [Fact]
    public void Startup_script_gets_a_samples_line_after_the_last_load()
    {
        string script = "s.waitForBoot {\n    ~dirt.loadSoundFiles;\n    s.sync;\n};";
        string? edited = TidalStudio.Core.SuperCollider.StartupScript.AddSamplesFolder(script, "/home/me/samples/");
        var lines = edited!.Split('\n');
        Assert.StartsWith("    ~dirt.loadSoundFiles(\"/home/me/samples/*\");", lines[2]);
        Assert.Equal("    s.sync;", lines[3]);

        // A script that loads no samples at all has no sensible place for it.
        Assert.Null(TidalStudio.Core.SuperCollider.StartupScript.AddSamplesFolder("s.boot;", "/x"));
    }

    [Fact]
    public void Wav_files_are_24_bit_stereo_and_ordered_for_n()
    {
        var note = new RenderedNote(60, new[] { 0f, 1f, -1f }, new[] { 0.5f, 0f, 0f }, 48000);
        using var stream = new MemoryStream();
        SampleExport.WriteWav(stream, note);
        var bytes = stream.ToArray();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(2, BitConverter.ToInt16(bytes, 22));       // channels
        Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));   // sample rate
        Assert.Equal(24, BitConverter.ToInt16(bytes, 34));      // bits
        Assert.Equal(3 * 2 * 3, BitConverter.ToInt32(bytes, 40)); // data size
        Assert.Equal(44 + 18, bytes.Length);
        // Second frame, left channel = +1.0 → 0x7FFFFF.
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x7F }, bytes[50..53]);

        // SuperDirt sorts by file name: the index keeps n in note order (c2 before c10 etc.).
        Assert.True(string.CompareOrdinal(SampleExport.FileName("x", 2, 36), SampleExport.FileName("x", 10, 24)) < 0);
    }
}
