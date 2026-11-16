using TidalStudio.Core.Files;
using TidalStudio.Core.Synth;

namespace TidalStudio.Tests;

public class DrumTests
{
    [Fact]
    public void Every_kit_voice_and_variation_renders_clean_audio()
    {
        foreach (var (name, create) in DrumKits.All)
        {
            var kit = create();
            Assert.Empty(kit.Validate());
            foreach (var voice in kit.Voices)
                for (int variation = 0; variation < voice.Variations; variation++)
                {
                    var note = DrumEngine.Render(voice, variation, kit.SampleRate);
                    string what = $"{name}/{voice.Abbreviation}/{variation}";
                    Assert.InRange(note.Seconds, 0.01, 10);
                    float peak = 0;
                    foreach (var s in note.Left.Concat(note.Right))
                    {
                        Assert.True(float.IsFinite(s), $"{what}: NaN/infinity");
                        peak = Math.Max(peak, Math.Abs(s));
                    }
                    Assert.InRange(peak, 0.3f, 1f);
                }
        }
    }

    [Fact]
    public void Variations_differ_and_version_zero_is_the_voice_itself()
    {
        var voice = DrumKits.Default(DrumModel.Snare, "sd");
        voice.Variations = 3;
        voice.Spread = 0.5;
        Assert.Same(voice, DrumEngine.Vary(voice, 0));
        var a = DrumEngine.Render(voice, 1, 48000);
        var b = DrumEngine.Render(voice, 2, 48000);
        Assert.NotEqual(a.Left, b.Left);
        Assert.Equal(a.Left, DrumEngine.Render(voice, 1, 48000).Left); // deterministic
    }

    [Fact]
    public void Kick_is_short_and_hats_are_shorter_than_open_hats()
    {
        var kit = DrumKits.Create("Punchy machine");
        double Len(string abbr) => DrumEngine.Render(kit.Voices.First(v => v.Abbreviation == abbr), 0, 48000).Seconds;
        Assert.True(Len("hh") < Len("oh"));
        Assert.True(Len("bd") < 1.5);
    }

    [Fact]
    public void Plan_names_folders_like_drum_machine_kits_and_the_combined_kit_by_index()
    {
        var kit = new DrumKit
        {
            Name = "mykit",
            Voices =
            {
                DrumKits.Default(DrumModel.Kick, "bd"),
                new DrumVoice { Abbreviation = "hh", Model = DrumModel.ClosedHat, Variations = 2 },
                new DrumVoice { Abbreviation = "sd", Model = DrumModel.Snare, Enabled = false },
            },
        };
        var plan = DrumExport.Plan(kit, combined: true);
        Assert.Equal(new[] { "mykitbd/mykitbd_00.wav", "mykithh/mykithh_00.wav", "mykithh/mykithh_01.wav", "mykit/mykit_00_bd.wav", "mykit/mykit_01_hh.wav" },
            plan.Select(f => $"{f.Folder}/{f.FileName}"));
        Assert.Equal(new[] { "mykitbd", "mykithh" }, DrumExport.Folders(kit, combined: false));
    }

    [Fact]
    public void Kits_saved_by_the_first_drum_synth_are_converted_to_layers()
    {
        const string old = """
        { "Name": "old", "Normalize": true, "SampleRate": 48000, "Voices": [
          { "Enabled": true, "Abbreviation": "bd", "Model": "Kick", "Tune": -2, "Decay": 0.5, "Punch": 12, "Variations": 3, "Spread": 0.4 },
          { "Enabled": false, "Abbreviation": "hh", "Model": "ClosedHat", "Tune": 1 } ] }
        """;
        var kit = DrumKit.FromJson(old);
        Assert.Equal(2, kit.Version);
        Assert.Equal("old", kit.Name);
        var bd = kit.Voices[0];
        Assert.True(bd.Osc1.Enabled);
        Assert.True(bd.Osc1.PitchDrop > 0);
        Assert.Equal(-2, bd.Tune);
        Assert.Equal(3, bd.Variations);
        Assert.False(kit.Voices[1].Enabled);
        Assert.True(kit.Voices[1].Metal.Enabled);
        // And the new format round-trips.
        var again = DrumKit.FromJson(kit.ToJson());
        Assert.Equal(kit.ToJson(), again.ToJson());
    }

    [Fact]
    public void Envelope_rises_holds_and_falls_to_zero()
    {
        var env = new DrumEnvelope { Attack = 0.01, Hold = 0.02, Decay = 0.1, Curve = 1 };
        Assert.Equal(0.5, env.At(0.005), 3);
        Assert.Equal(1, env.At(0.02), 3);
        Assert.True(env.At(0.05) < 0.5);
        Assert.Equal(0, env.At(0.13 + 1e-9), 6);
        var linear = new DrumEnvelope { Attack = 0, Decay = 0.1, Curve = 0 };
        Assert.Equal(0.5, linear.At(0.05), 3);
    }

    [Fact]
    public void Each_layer_and_FM_changes_the_sound()
    {
        var kick = DrumKits.Default(DrumModel.Kick, "bd");
        var plain = DrumEngine.Render(kick, 0, 48000);
        var fm = kick.Clone();
        fm.Osc2 = new DrumOscillator { Enabled = true, Frequency = 1.5, Level = 0, Fm = 0.6 };
        Assert.NotEqual(plain.Left, DrumEngine.Render(fm, 0, 48000).Left);
        var withNoise = kick.Clone();
        withNoise.Noise.Enabled = true;
        Assert.NotEqual(plain.Left, DrumEngine.Render(withNoise, 0, 48000).Left);
        var longer = kick.Clone();
        longer.Osc1.Envelope.Decay = 1.5;
        Assert.True(DrumEngine.Render(longer, 0, 48000).Seconds > plain.Seconds + 0.5);
    }

    [Fact]
    public void Clap_bursts_are_separate_hits()
    {
        var noise = DrumKits.Default(DrumModel.Clap, "cp").Noise;
        // Between two hands the level dips, then comes back at the next hand.
        Assert.True(DrumEngine.NoiseEnvelope(noise, 0.009) < 0.3);
        Assert.True(DrumEngine.NoiseEnvelope(noise, noise.BurstSpacing + 0.0006) > 0.8);
    }

    [Fact]
    public void Randomise_keeps_every_kit_voice_renderable()
    {
        var r = new Random(1);
        foreach (var voice in DrumKits.Create("Hard techno").Voices)
        {
            DrumKits.Randomise(voice, r);
            var note = DrumEngine.Render(voice, 0, 44100);
            Assert.All(note.Left, s => Assert.True(float.IsFinite(s)));
        }
    }
    [Fact]
    public void Validate_rejects_bad_and_duplicate_drum_names()
    {
        var kit = new DrumKit { Name = "Kit", Voices = { new DrumVoice { Abbreviation = "bd" }, new DrumVoice { Abbreviation = "bd" }, new DrumVoice { Abbreviation = "B D" } } };
        var problems = kit.Validate();
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void Write_creates_the_planned_files_and_replaces_old_ones()
    {
        string root = Path.Combine(Path.GetTempPath(), "ts-drums-" + Guid.NewGuid().ToString("N"));
        try
        {
            var kit = new DrumKit { Name = "t", Voices = { DrumKits.Default(DrumModel.Kick, "bd"), DrumKits.Default(DrumModel.ClosedHat, "hh") } };
            Directory.CreateDirectory(Path.Combine(root, "tbd"));
            File.WriteAllText(Path.Combine(root, "tbd", "old.wav"), "x");
            var folders = DrumExport.Write(root, kit, combined: true);
            Assert.Equal(3, folders.Count);
            Assert.False(File.Exists(Path.Combine(root, "tbd", "old.wav")));
            Assert.True(File.Exists(Path.Combine(root, "tbd", "tbd_00.wav")));
            Assert.True(File.Exists(Path.Combine(root, "t", "t_01_hh.wav")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void New_presets_render_and_have_valid_unique_sound_names()
    {
        var names = SynthPresets.All.Select(p => p.Create().Name).ToList();
        Assert.All(names, n => Assert.True(SampleExport.IsValidName(n), n));
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains(SynthPresets.Groups, g => g.Category == "Acid techno" && g.Presets.Count >= 6);
        Assert.Contains(SynthPresets.Groups, g => g.Category == "Retro" && g.Presets.Count >= 6);
    }
}

public class FileOperationsTests
{
    [Theory]
    [InlineData("song.tidal", true)]
    [InlineData("my folder", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("a/b", false)]
    [InlineData("what?", false)]
    [InlineData("con", false)]
    [InlineData("con.tidal", false)]
    [InlineData("name.", false)]
    public void ValidateName(string name, bool ok) => Assert.Equal(ok, FileOperations.ValidateName(name) is null);

    [Fact]
    public void Create_unique_names_and_delete_permanently()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("untitled.tidal", FileOperations.UniqueName(dir, "untitled.tidal"));
            string file = FileOperations.CreateFile(dir, FileOperations.TidalFileName("untitled"));
            Assert.Equal(Path.Combine(dir, "untitled.tidal"), file);
            Assert.Equal("untitled 2.tidal", FileOperations.UniqueName(dir, "untitled.tidal"));
            Assert.Throws<IOException>(() => FileOperations.CreateFile(dir, "untitled.tidal"));
            Assert.Equal(0, new FileInfo(file).Length); // no BOM

            string folder = FileOperations.CreateFolder(dir, "sub");
            FileOperations.CreateFile(folder, "inner.tidal", "d1 $ s \"bd\"");
            Assert.True(FileOperations.IsSameOrInside(Path.Combine(folder, "inner.tidal"), folder));
            Assert.False(FileOperations.IsSameOrInside(dir, folder));

            Assert.False(FileOperations.Delete(folder, permanently: true));
            Assert.False(Directory.Exists(folder));
            FileOperations.Delete(file, permanently: true);
            Assert.False(File.Exists(file));
            Assert.Throws<FileNotFoundException>(() => FileOperations.Delete(file, permanently: true));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
