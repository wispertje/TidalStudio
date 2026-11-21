using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class PresetSamplesTests
{
    [Fact]
    public void Every_preset_has_its_own_valid_sound_name()
    {
        var plan = PresetSamples.Plan();
        var folders = plan.SelectMany(PresetSamples.FoldersOf).ToList();
        Assert.All(folders, f => Assert.True(SampleExport.IsValidName(f), f));
        Assert.Empty(folders.GroupBy(f => f).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.DoesNotContain(plan, p => p.Preset == "Init");
    }

    [Fact]
    public void Existing_sounds_are_left_alone()
    {
        string root = Path.Combine(Path.GetTempPath(), "tidal-presets-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Only the existence check is tested: put a wav in every folder so nothing renders.
            foreach (var f in PresetSamples.Plan().SelectMany(PresetSamples.FoldersOf))
            {
                Directory.CreateDirectory(Path.Combine(root, f));
                File.WriteAllText(Path.Combine(root, f, "mine.wav"), "x");
            }
            var r = PresetSamples.RenderAll(root, replace: false);
            Assert.Empty(r.Written);
            Assert.Equal(PresetSamples.Plan().Count, r.Skipped.Count);
            Assert.All(Directory.EnumerateDirectories(root), d => Assert.Equal("x", File.ReadAllText(Path.Combine(d, "mine.wav"))));
        }
        finally { Directory.Delete(root, true); }
    }
}
