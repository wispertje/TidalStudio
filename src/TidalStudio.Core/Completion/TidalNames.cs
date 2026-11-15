using System.Text;
using System.Text.RegularExpressions;

namespace TidalStudio.Core.Completion;

/// <summary>Names for code completion: Tidal's functions (from ghci, with types) plus short descriptions.</summary>
public static partial class TidalNames
{
    [GeneratedRegex(@"^(?:[A-Z][\w']*\.)*([a-z_][\w']*)\s+::\s*(.*)$")]
    private static partial Regex Declaration();

    /// <summary>
    /// Parses the output of <c>:browse Sound.Tidal.Context</c> into name → type signature. Signatures that ghci
    /// wraps over several lines are joined. Operators, data types and classes are skipped.
    /// </summary>
    public static Dictionary<string, string> ParseBrowse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var signature = new StringBuilder();

        void Flush()
        {
            if (name is not null) result.TryAdd(name, Regex.Replace(signature.ToString().Trim(), @"\s+", " "));
            name = null;
            signature.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                if (name is not null) signature.Append(' ').Append(line.Trim());
                continue;
            }
            Flush();
            var m = Declaration().Match(line);
            if (!m.Success) continue;
            name = m.Groups[1].Value;
            signature.Append(m.Groups[2].Value);
        }
        Flush();
        return result;
    }

    /// <summary>Short descriptions of the functions and effects people use most.</summary>
    public static readonly IReadOnlyDictionary<string, string> Docs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Playing
        ["d1"] = "Play a pattern on channel 1 (d1 … d16)",
        ["p"] = "Play a pattern on a named or numbered channel: p \"drums\" $ …",
        ["hush"] = "Stop all patterns",
        ["silence"] = "An empty pattern",
        ["once"] = "Play a pattern once, right now",
        ["setcps"] = "Set the tempo in cycles per second, e.g. setcps (120/60/4)",
        ["solo"] = "Solo a channel: solo 1",
        ["unsolo"] = "Undo solo",
        ["mute"] = "Mute a channel: mute 1",
        ["unmute"] = "Unmute a channel",
        ["xfade"] = "Crossfade a channel to a new pattern",
        ["xfadeIn"] = "Crossfade a channel to a new pattern over n cycles",

        // Time
        ["fast"] = "Speed a pattern up by a factor",
        ["slow"] = "Slow a pattern down by a factor",
        ["hurry"] = "Speed up and raise the pitch (fast and speed together)",
        ["rev"] = "Reverse each cycle",
        ["iter"] = "Shift the pattern by 1/n of a cycle each cycle",
        ["palindrome"] = "Play forwards, then backwards, alternating cycles",
        ["ply"] = "Repeat each event n times within its slot",
        ["linger"] = "Repeat the first part of each cycle (linger 0.25)",
        ["trunc"] = "Play only the first part of each cycle (trunc 0.75)",
        ["swingBy"] = "swingBy x n: delay events in the second half of each 1/n slice by x",
        ["swing"] = "swing n: swingBy (1/3) n",
        ["inside"] = "inside n f: apply f as if the pattern were n times faster",
        ["outside"] = "outside n f: apply f as if the pattern were n times slower",
        ["rot"] = "Shift values to later events, keeping the rhythm",
        ["shuffle"] = "Play n parts of each cycle in random order (no repeats)",
        ["scramble"] = "Play n parts of each cycle picked at random (may repeat)",
        ["segment"] = "Sample a pattern n times per cycle (turns continuous patterns into events)",
        ["chunk"] = "chunk n f: apply f to one of n parts, a different part each cycle",
        ["loopFirst"] = "Repeat the first cycle forever",

        // Conditions and randomness
        ["every"] = "every n f: apply f every n cycles",
        ["whenmod"] = "whenmod a b f: apply f when the cycle number mod a is at least b",
        ["sometimes"] = "Apply a function to about 50% of events",
        ["often"] = "Apply a function to about 75% of events",
        ["rarely"] = "Apply a function to about 25% of events",
        ["almostNever"] = "Apply a function to about 10% of events",
        ["almostAlways"] = "Apply a function to about 90% of events",
        ["always"] = "Always apply the function",
        ["never"] = "Never apply the function",
        ["sometimesBy"] = "sometimesBy p f: apply f to events with probability p",
        ["someCycles"] = "Apply a function to about 50% of whole cycles",
        ["someCyclesBy"] = "someCyclesBy p f: apply f to whole cycles with probability p",
        ["degrade"] = "Randomly drop about 50% of events",
        ["degradeBy"] = "Randomly drop events with the given probability",
        ["rand"] = "Continuous random values between 0 and 1",
        ["irand"] = "Random integers from 0 to n-1",
        ["perlin"] = "Smooth random values between 0 and 1",
        ["choose"] = "Pick randomly from a list",
        ["wchoose"] = "Pick randomly from a list of (value, weight) pairs",
        ["randcat"] = "Concatenate patterns in random order, one cycle each",
        ["wrandcat"] = "Like randcat, with weights",

        // Combining
        ["stack"] = "Play several patterns at the same time",
        ["cat"] = "Play patterns one after another, one cycle each",
        ["fastcat"] = "Squash patterns into one cycle, one after another",
        ["overlay"] = "Play two patterns at the same time",
        ["superimpose"] = "Play a transformed copy on top of the pattern",
        ["off"] = "off t f: add a copy shifted by t cycles and transformed by f",
        ["layer"] = "Apply several functions and stack the results",
        ["jux"] = "Apply a function to the right channel only",
        ["juxBy"] = "Like jux, with an adjustable stereo width",
        ["echo"] = "echo count time feedback: repeat each event with decaying gain",
        ["stut"] = "Old name for echo-style repeats; prefer echo",
        ["euclid"] = "euclid k n: k events spread over n steps",
        ["euclidOff"] = "euclidOff k n r: euclid rotated by r steps",
        ["euclidInv"] = "The steps euclid leaves empty",
        ["struct"] = "Impose a rhythm given as a boolean pattern (\"t f t t\")",
        ["mask"] = "Remove events where a boolean pattern is false",
        ["fix"] = "Apply a function only to events with the given controls",
        ["while"] = "Apply a function where a boolean pattern is true",
        ["sew"] = "Switch between two patterns with a boolean pattern",
        ["stitch"] = "Use a boolean pattern to pick values from two patterns",

        // Values
        ["range"] = "Scale a 0..1 pattern to a range: range 200 2000 sine",
        ["rangex"] = "Like range, exponentially",
        ["sine"] = "Continuous sine wave, 0..1 over a cycle",
        ["cosine"] = "Continuous cosine wave, 0..1 over a cycle",
        ["saw"] = "Continuous ramp up, 0..1 over a cycle",
        ["isaw"] = "Continuous ramp down, 1..0 over a cycle",
        ["tri"] = "Continuous triangle wave, 0..1",
        ["square"] = "Continuous square wave, 0 or 1",
        ["envL"] = "Linear ramp from 0 to 1 over the first cycle, then 1",
        ["scale"] = "Map numbers to a named scale: n (scale \"major\" \"0 .. 7\")",
        ["toScale"] = "Map numbers to the given list of semitones",
        ["arp"] = "Arpeggiate chords: arp \"<up down>\"",
        ["arpeggiate"] = "Play the notes of each chord one after another",
        ["chop"] = "Cut each sample into n parts",
        ["striate"] = "Interlace n parts of each sample across the cycle",
        ["slice"] = "slice n i: play part i of a sample cut into n parts",
        ["splice"] = "Like slice, with speed adjusted to fit the event",
        ["loopAt"] = "Fit a sample to n cycles (adjusts speed)",
        ["fit"] = "Step through a list of values in steps",
        ["run"] = "run n: the pattern 0 .. n-1",

        // Sound parameters (use after #)
        ["s"] = "Sample folder or synth name: s \"bd sn\"",
        ["sound"] = "Sample folder or synth name (same as s)",
        ["n"] = "Sample number within the folder, or note for synths",
        ["note"] = "Pitch in semitones",
        ["up"] = "Pitch in semitones (old name for note)",
        ["gain"] = "Volume; 1 is normal, above 1 is louder",
        ["amp"] = "Linear amplitude (default 0.4)",
        ["pan"] = "Stereo position: 0 left, 0.5 centre, 1 right",
        ["speed"] = "Playback speed; 2 is an octave up, negative plays backwards",
        ["accelerate"] = "Change of speed over the sound",
        ["legato"] = "Note length relative to its slot (1 = until the next event)",
        ["sustain"] = "Note length in seconds",
        ["begin"] = "Start position in the sample, 0..1",
        ["end"] = "End position in the sample, 0..1",
        ["cut"] = "Cut group: a new sound in the group stops the previous one",
        ["unit"] = "Unit for speed: \"r\" rate, \"c\" cycles, \"s\" seconds",
        ["lpf"] = "Low-pass filter cutoff in Hz",
        ["cutoff"] = "Low-pass filter cutoff in Hz (same as lpf)",
        ["hpf"] = "High-pass filter cutoff in Hz",
        ["hcutoff"] = "High-pass filter cutoff in Hz (same as hpf)",
        ["bpf"] = "Band-pass filter centre frequency in Hz",
        ["resonance"] = "Low-pass filter resonance, 0..1",
        ["lpq"] = "Low-pass filter resonance (same as resonance)",
        ["hpq"] = "High-pass filter resonance",
        ["bpq"] = "Band-pass filter resonance",
        ["room"] = "Reverb amount, 0..1",
        ["size"] = "Reverb room size, 0..1",
        ["dry"] = "Dry/wet balance for the reverb",
        ["delay"] = "Delay amount, 0..1",
        ["delaytime"] = "Delay time",
        ["delayfeedback"] = "Delay feedback, 0..1 (keep below 1)",
        ["lock"] = "lock 1: delay time in cycles instead of seconds",
        ["crush"] = "Bit crusher; lower is harsher (1..16)",
        ["coarse"] = "Sample-rate reduction (whole numbers, higher is rougher)",
        ["shape"] = "Wave-shaping distortion, 0..1",
        ["distort"] = "Distortion amount",
        ["triode"] = "Tube-like distortion",
        ["squiz"] = "Pitch-shift distortion (try 2 and up)",
        ["vowel"] = "Formant filter: \"a e i o u\"",
        ["orbit"] = "Effect bus; patterns on different orbits get separate reverb and delay",
        ["octave"] = "Octave for synths",
        ["attack"] = "Attack time in seconds",
        ["release"] = "Release time in seconds",
        ["hold"] = "Hold time in seconds",
        ["djf"] = "DJ filter: below 0.5 low-pass, above 0.5 high-pass",
        ["leslie"] = "Rotating-speaker effect",
        ["phaserrate"] = "Phaser speed",
        ["phaserdepth"] = "Phaser depth",
        ["tremolorate"] = "Tremolo speed",
        ["tremolodepth"] = "Tremolo depth",
        ["color"] = "TidalStudio: highlight colour for this pattern, e.g. # color \"orange\" or \"ff8800\"",
        ["colour"] = "TidalStudio: highlight colour for this pattern (same as color)",
    };

    /// <summary>Names that are only in the boot file (not in Sound.Tidal.Context).</summary>
    public static readonly IReadOnlyList<string> BootNames = new[]
    {
        "d1", "d2", "d3", "d4", "d5", "d6", "d7", "d8", "d9", "d10", "d11", "d12", "d13", "d14", "d15", "d16",
        "p", "hush", "once", "setcps", "solo", "unsolo", "mute", "unmute", "xfade", "xfadeIn", "color", "colour",
    };
}
