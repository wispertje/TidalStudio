using System.Text.RegularExpressions;
using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class ExampleScriptTests
{
    // Names the scripts use for sounds the user makes and names themselves.
    private static readonly HashSet<string> UserNames = new() { "acidloop", "popline", "mykit" };

    [Fact]
    public void Every_script_is_built_in()
    {
        foreach (var (title, file) in ExampleScripts.All)
        {
            string text = ExampleScripts.Read(file);
            Assert.True(text.Length > 500, $"{title} ({file}) is missing or empty");
            Assert.Contains("d1 $", text);
        }
    }

    [Fact]
    public void Every_sound_a_script_plays_is_made_by_a_preset_kit_or_the_user()
    {
        var known = new HashSet<string>(UserNames);
        foreach (var (_, create) in SynthPresets.All) known.Add(create().Name);
        foreach (var (_, create) in FmPresets.All) known.Add(create().Name);
        foreach (var (_, create) in DrumKits.All)
        {
            var kit = create();
            known.Add(kit.Name);
            foreach (var v in kit.Voices) known.Add(kit.SoundName(v));
        }
        foreach (var abbr in DrumKits.Abbreviations.Keys) known.Add("mykit" + abbr);

        var sounds = new Regex("\\bs \\(?\"([^\"]*)\"");
        var missing = new List<string>();
        foreach (var (_, file) in ExampleScripts.All)
            foreach (Match m in sounds.Matches(ExampleScripts.Read(file)))
                foreach (var word in Regex.Matches(m.Groups[1].Value, "[a-z][a-z0-9]*").Select(w => w.Value))
                    if (!known.Contains(word)) missing.Add($"{file}: {word}");
        Assert.Empty(missing);
    }
}
