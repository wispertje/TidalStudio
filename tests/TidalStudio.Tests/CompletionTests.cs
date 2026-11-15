using TidalStudio.Core.Completion;

namespace TidalStudio.Tests;

public class CompletionTests
{
    private static CompletionRequest At(string lineWithCaret)
    {
        int caret = lineWithCaret.IndexOf('|');
        return CompletionContext.Analyze(lineWithCaret.Remove(caret, 1), caret);
    }

    [Fact]
    public void Code_completes_the_identifier_before_the_caret()
    {
        var r = At("d1 $ every 4 (fas| 2) $ s \"bd\"");
        Assert.Equal(CompletionKind.Code, r.Kind);
        Assert.Equal("fas", r.Prefix);
        Assert.Equal(14, r.Start);
        Assert.False(r.AfterHash);
    }

    [Fact]
    public void Code_after_hash_is_flagged_so_effects_rank_first()
    {
        var r = At("d1 $ s \"bd\" # roo|");
        Assert.Equal(CompletionKind.Code, r.Kind);
        Assert.Equal("roo", r.Prefix);
        Assert.True(r.AfterHash);
    }

    [Fact]
    public void Sample_names_complete_inside_s_strings()
    {
        var r = At("d1 $ s \"bd*2 [sn cp|]\"");
        Assert.Equal(CompletionKind.Sample, r.Kind);
        Assert.Equal("cp", r.Prefix);

        Assert.Equal(CompletionKind.Sample, At("d1 $ sound \"|").Kind);
        Assert.Equal(CompletionKind.Sample, At("d1 $ n \"0 1\" # s \"super|").Kind);
    }

    [Fact]
    public void Other_strings_and_sample_numbers_get_nothing()
    {
        Assert.Equal(CompletionKind.None, At("d1 $ n \"0 1|\"").Kind);
        Assert.Equal(CompletionKind.None, At("d1 $ s \"bd:|").Kind);
        Assert.Equal(CompletionKind.None, At("d1 $ vowel \"a|\"").Kind);
    }

    [Fact]
    public void Visual_names_complete_after_comment_underscore()
    {
        var r = At("  -- _pian|");
        Assert.Equal(CompletionKind.VisualName, r.Kind);
        Assert.Equal("_pian", r.Prefix);
        Assert.Equal(5, r.Start);
        Assert.Equal(CompletionKind.VisualName, At("d1 $ s \"bd\" -- _|").Kind);
    }

    [Fact]
    public void Visual_option_keys_and_values()
    {
        var key = At("-- _pianoroll cycles=2 pla|");
        Assert.Equal(CompletionKind.VisualOptionKey, key.Kind);
        Assert.Equal("pianoroll", key.VisualType);
        Assert.Equal("pla", key.Prefix);
        Assert.Contains("cycles", key.UsedKeys!);

        var empty = At("-- _spiral |");
        Assert.Equal(CompletionKind.VisualOptionKey, empty.Kind);
        Assert.Equal("", empty.Prefix);

        var value = At("-- _pianoroll rows=pi|");
        Assert.Equal(CompletionKind.VisualOptionValue, value.Kind);
        Assert.Equal("rows", value.OptionKey);
        Assert.Equal("pi", value.Prefix);
    }

    [Fact]
    public void Prose_comments_and_double_dash_in_strings_are_not_completed()
    {
        Assert.Equal(CompletionKind.None, At("-- this is a no|").Kind);
        // "--" inside a string is not a comment.
        Assert.Equal(CompletionKind.Sample, At("d1 $ s \"bd -- |").Kind);
    }

    [Fact]
    public void Browse_output_is_parsed_with_wrapped_signatures()
    {
        var names = TidalNames.ParseBrowse(new[]
        {
            "fast :: Pattern Time -> Pattern a -> Pattern a",
            "every ::",
            "  Pattern Int",
            "  -> (Pattern a -> Pattern a) -> Pattern a -> Pattern a",
            "(#) :: Unionable b => Pattern b -> Pattern b -> Pattern b",
            "data Pattern a",
            "  = Pattern {query :: State -> [Event a]}",
            "Sound.Tidal.Context.n :: Pattern Note -> ControlPattern",
            "class Moddable a where",
            "  gmod :: a -> a -> a",
        });
        Assert.Equal("Pattern Time -> Pattern a -> Pattern a", names["fast"]);
        Assert.Equal("Pattern Int -> (Pattern a -> Pattern a) -> Pattern a -> Pattern a", names["every"]);
        Assert.Equal("Pattern Note -> ControlPattern", names["n"]);
        Assert.False(names.ContainsKey("query"));
        Assert.False(names.ContainsKey("gmod"));
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Startup_script_sound_folders_are_found_like_superdirt_does()
    {
        // Same layout as tidal-drum-machines: machines/<Machine>/<sound>/*.wav
        string root = Path.Combine(Path.GetTempPath(), "ts-machines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "machines", "RolandTR909", "rolandtr909-bd"));
        Directory.CreateDirectory(Path.Combine(root, "machines", "RolandTR909", "rolandtr909-hh"));
        Directory.CreateDirectory(Path.Combine(root, "machines", "Linn", "linn-sd"));
        Directory.CreateDirectory(Path.Combine(root, "other", "commented"));
        foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            File.WriteAllText(Path.Combine(d, "x.wav"), "");
        try
        {
            string unix = root.Replace('\\', '/');
            string script = $$"""
                ~dirt.loadSoundFiles;  // defaults
                ~dirt.loadSoundFiles("{{unix}}/machines/*/*");  // drum machines
                // ~dirt.loadSoundFiles("{{unix}}/other/*");
                """;
            var folders = SoundFoldersFromScript(script).Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "linn-sd", "rolandtr909-bd", "rolandtr909-hh" }, folders);

            var entries = SampleLibrary.Scan(Array.Empty<string>(), SoundFoldersFromScript(script));
            Assert.Contains(entries, e => e.Name == "rolandtr909-bd" && e.Count == 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static List<string> SoundFoldersFromScript(string s) => SampleLibrary.SoundFoldersFromScript(s);
    }

    [Fact]
    public void Startup_script_that_starts_superdirt_is_recognised()
    {
        Assert.True(TidalStudio.Core.SuperCollider.SuperDirtLauncher.StartsSuperDirt("~dirt = SuperDirt(2, s);\n~dirt.start(57120, 0 ! 12);"));
        Assert.False(TidalStudio.Core.SuperCollider.SuperDirtLauncher.StartsSuperDirt("// ~dirt = SuperDirt(2, s);\n// ~dirt.start(57120);"));
        Assert.False(TidalStudio.Core.SuperCollider.SuperDirtLauncher.StartsSuperDirt("s.boot;"));
    }

    [Fact]
    public void Sample_scan_counts_audio_files_and_adds_synths()
    {
        string root = Path.Combine(Path.GetTempPath(), "ts-samples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bd"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "bd", "a.wav"), "");
        File.WriteAllText(Path.Combine(root, "bd", "b.WAV"), "");
        File.WriteAllText(Path.Combine(root, "bd", "readme.txt"), "");
        try
        {
            var entries = SampleLibrary.Scan(new[] { root });
            var bd = Assert.Single(entries, e => e.Name == "bd");
            Assert.Equal(2, bd.Count);
            Assert.DoesNotContain(entries, e => e.Name == "empty");
            Assert.Contains(entries, e => e.Name == "superpiano" && e.IsSynth);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
