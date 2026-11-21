using TidalStudio.Core.Ml;
using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class MuseTests
{
    private static string FakeSamples()
    {
        string root = Path.Combine(Path.GetTempPath(), "muse-samples-" + Guid.NewGuid().ToString("N"));
        void Wav(string folder, string file)
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            SampleExport.WriteWav(Path.Combine(root, folder, file), new RenderedNote(60, new float[100], new float[100], 48000));
        }
        Wav("popbass", "popbass_00_c2.wav"); Wav("popbass", "popbass_01_c3.wav");
        Wav("poplead", "poplead_00_c5.wav");
        Wav("poppad", "poppad_00_c4.wav");
        foreach (var d in new[] { "bd", "sd", "hh", "cp", "oh" }) Wav("gated" + d, $"gated{d}_00.wav");
        return root;
    }

    private static TidalContext Context(SampleCatalog c)
    {
        var drums = new MPart { Role = PartRole.Drums, Bars = 4, Sound = "gatedbd" };
        for (int b = 0; b < 4; b++) for (int i = 0; i < 4; i++) drums.Notes.Add(new MNote(b + i / 4.0, 0.0625, 0));
        var pad = new MPart { Role = PartRole.Chords, Bars = 4, Sound = "poppad" };
        int[][] chords = { new[] { 48, 51, 55 }, new[] { 44, 48, 51 }, new[] { 51, 55, 58 }, new[] { 46, 50, 53 } };
        for (int b = 0; b < 4; b++) foreach (int p in chords[b]) pad.Notes.Add(new MNote(b, 1, p));
        return new TidalContext
        {
            From = 0, Bars = 4, Parts = { ("1", drums), ("3", pad) }, Key = Theory.DetectKey(pad.Notes), Chords = Theory.DetectChords(pad.Notes, 4),
        };
    }

    [Theory]
    [InlineData(SuggestKind.Melody)]
    [InlineData(SuggestKind.Bass)]
    [InlineData(SuggestKind.Drums)]
    [InlineData(SuggestKind.Chords)]
    [InlineData(SuggestKind.NewPart)]
    public void Suggestions_fit_the_context(SuggestKind kind)
    {
        string root = FakeSamples();
        try
        {
            var catalog = SampleCatalog.Scan(root);
            var s = new Suggester(catalog, new TasteModel());
            var ctx = Context(catalog);
            Assert.Equal("C minor", ctx.Key.ToString());
            var items = s.Suggest(ctx, new SuggestOptions { Kind = kind, Bars = 4, Count = 4 }, new Random(3));
            Assert.Equal(4, items.Count);
            foreach (var item in items)
            {
                Assert.Equal("rules", item.Source);
                Assert.StartsWith("-- suggestion:", item.Code);
                Assert.NotEmpty(item.Layers);
                // A free channel: d1 and d3 are playing.
                if (kind != SuggestKind.NewPart) Assert.Contains("d2 $", item.Code);
                foreach (var l in item.Layers.Where(l => l.Part.Role != PartRole.Drums))
                    Assert.All(l.Part.Notes, n => Assert.True(ctx.Key.Contains(n.Pitch)));
                // The kick that plays reaches the request of pitched parts.
                foreach (var l in item.Layers.Where(l => l.Part.Role != PartRole.Drums))
                    Assert.Equal(new[] { 0, 4, 8, 12 }, l.Request.KickSteps.OrderBy(k => k));
            }
            if (kind == SuggestKind.NewPart)
                Assert.All(items, i => Assert.Contains("\ndo\n", i.Code));
            if (kind == SuggestKind.Drums)
                Assert.All(items, i => Assert.Contains("gatedbd", i.Code)); // the kit that plays
            var listen = items[0].CodeOnChannels(new[] { 14, 15, 16 });
            Assert.Contains("d14 $", listen);
            Assert.DoesNotContain("--", listen);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Taste_learns_a_preference()
    {
        var taste = new TasteModel();
        var request = new PartRequest { Role = PartRole.Melody, Key = MKey.CMinor, Bars = 2, Density = 2 };
        var busy = RuleGenerator.Generate(request with { Density = 4 }, new Random(1));
        var sparse = RuleGenerator.Generate(request with { Density = 0 }, new Random(1));
        var fb = PartFeatures.Of(busy, request, false);
        var fs = PartFeatures.Of(sparse, request, false);
        for (int i = 0; i < 30; i++) taste.Prefer(PartRole.Melody, fs, fb);
        Assert.True(taste.Score(PartRole.Melody, fs) > taste.Score(PartRole.Melody, fb));
        Assert.Equal(30, taste.RatingCount(PartRole.Melody));
        Assert.Contains(taste.Strongest(PartRole.Melody), x => x.Feature == "density" && x.Weight < 0);
        string path = Path.Combine(Path.GetTempPath(), "taste-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            taste.Save(path);
            var back = TasteModel.Load(path);
            Assert.Equal(taste.Score(PartRole.Melody, fs), back.Score(PartRole.Melody, fs), 9);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ratings_are_stored_for_the_trainer()
    {
        string path = Path.Combine(Path.GetTempPath(), "ratings-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            RatingStore.Add(new RatingRecord { Kind = "pair", Role = "Bass", Prompt = new() { 1, 6, 3 }, Chosen = new() { 4, 60 }, Rejected = new() { 4, 61 } }, path);
            RatingStore.Add(new RatingRecord { Kind = "like", Role = "Bass", Prompt = new() { 1 }, Chosen = new() { 4 } }, path);
            Assert.Equal(2, RatingStore.All(path).Count);
            Assert.Contains("\"rejected\":[4,61]", File.ReadAllLines(path)[0]);
            RatingStore.RemoveLast(path);
            Assert.Single(RatingStore.All(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Sampling_only_picks_allowed_tokens()
    {
        var logits = new float[Tokenizer.VocabSize];
        logits[10] = 50; // very likely, but not allowed
        var allowed = new bool[Tokenizer.VocabSize];
        allowed[Tokenizer.Bar] = allowed[Tokenizer.Eos] = true;
        var random = new Random(1);
        for (int i = 0; i < 200; i++)
            Assert.Contains(MuseModel.Sample(logits, allowed, new SamplingOptions(), random), new[] { Tokenizer.Bar, Tokenizer.Eos });
    }

    [Fact]
    public void Monotone_melodies_rank_lower()
    {
        var mono = new MPart { Role = PartRole.Melody, Bars = 1 };
        var varied = new MPart { Role = PartRole.Melody, Bars = 1 };
        for (int i = 0; i < 8; i++)
        {
            mono.Notes.Add(new MNote(i / 8.0, 0.125, 67));
            varied.Notes.Add(new MNote(i / 8.0, 0.125, new[] { 60, 63, 67, 65, 63, 62, 60, 67 }[i]));
        }
        Assert.True(Suggester.Variety(varied) > Suggester.Variety(mono) + 0.3);
    }
}
