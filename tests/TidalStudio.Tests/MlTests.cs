using TidalStudio.Core.Ml;
using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class MlTests
{
    private static string FakeSamples()
    {
        string root = Path.Combine(Path.GetTempPath(), "ml-samples-" + Guid.NewGuid().ToString("N"));
        void Wav(string folder, string file, double seconds = 0.5)
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            int frames = (int)(48000 * seconds);
            var note = new RenderedNote(60, new float[frames], new float[frames], 48000);
            SampleExport.WriteWav(Path.Combine(root, folder, file), note);
        }
        Wav("popbass", "popbass_00_c1.wav"); Wav("popbass", "popbass_01_c2.wav"); Wav("popbass", "popbass_02_c3.wav");
        Wav("poplead", "poplead_00_c4.wav"); Wav("poplead", "poplead_01_c5.wav");
        Wav("poppad", "poppad_00_c4.wav");
        Wav("gatedbd", "gatedbd_00.wav"); Wav("gatedsd", "gatedsd_00.wav"); Wav("gatedsd", "gatedsd_01.wav");
        Wav("gatedhh", "gatedhh_00.wav"); Wav("gatedcp", "gatedcp_00.wav");
        Wav("gated", "gated_00_bd.wav"); Wav("gated", "gated_01_sd.wav");
        Wav("seqarp", "seqarp_00_A.wav", 240 / 118.0); Wav("seqarp", "seqarp_01_B.wav", 480 / 118.0);
        Wav("fmkick", "fmkick_00_c5.wav");
        return root;
    }

    [Fact]
    public void The_catalog_knows_what_each_sound_is()
    {
        string root = FakeSamples();
        try
        {
            var c = SampleCatalog.Scan(root);
            var bass = c.Find("popbass")!;
            Assert.Equal(SoundKind.Pitched, bass.Kind);
            Assert.Equal(PartRole.Bass, bass.Role);
            Assert.Equal(new[] { 12, 24, 36 }, bass.Roots);
            Assert.Equal(PartRole.Melody, c.Find("poplead")!.Role);
            Assert.Equal(PartRole.Chords, c.Find("poppad")!.Role);
            Assert.Equal(("gated", "sd", 2), (c.Find("gatedsd")!.Kit, c.Find("gatedsd")!.Drum, c.Find("gatedsd")!.Variants));
            Assert.Equal(SoundKind.Kit, c.Find("gated")!.Kind);
            var loop = c.Find("seqarp")!;
            Assert.Equal(SoundKind.Loop, loop.Kind);
            Assert.Equal(118, loop.LoopBpm!.Value, 0);
            Assert.Equal(new[] { 1.0, 2.0 }, loop.LoopBars);
            Assert.Equal(("bd", SoundKind.Drum), (c.Find("fmkick")!.Drum, c.Find("fmkick")!.Kind));
            Assert.Equal("gatedhh", c.DrumIn("gated", "oh")!.Name); // substitute
            var overridden = SampleCatalog.Scan(root, new Dictionary<string, PartRole> { ["poplead"] = PartRole.Chords });
            Assert.Equal(PartRole.Chords, overridden.Find("poplead")!.Role);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Parts_become_readable_tidal()
    {
        string root = FakeSamples();
        try
        {
            var c = SampleCatalog.Scan(root);
            var drums = new MPart { Role = PartRole.Drums, Bars = 1 };
            for (int i = 0; i < 4; i++) drums.Notes.Add(new MNote(i / 4.0, 0.0625, 0));
            drums.Notes.Add(new MNote(0.25, 0.0625, 1));
            drums.Notes.Add(new MNote(0.75, 0.0625, 1));
            for (int i = 0; i < 8; i++) drums.Notes.Add(new MNote(i / 8.0, 0.0625, 4));
            Assert.Equal("d1 $ s \"[gatedbd*4, [~ gatedsd]*2, gatedhh*8]\"", TidalWriter.DrumPart(drums, c, "gated", 1));

            // Two bars of bass: c c6 on eighths, then af4 held.
            var bass = new MPart { Role = PartRole.Bass, Bars = 2 };
            bass.Notes.Add(new MNote(0, 0.25, 24)); bass.Notes.Add(new MNote(0.5, 0.25, 36));
            bass.Notes.Add(new MNote(1, 1, 20));
            string code = TidalWriter.Pitched(bass, c.Find("popbass")!, 5, "bass");
            Assert.Equal("-- bass\nd5 $ note \"<[c ~ c6 ~] af4>\" # s \"popbass\" # n 1 # legato 1 # cut 5 # gain 0.9", code);

            var pad = new MPart { Role = PartRole.Chords, Bars = 1 };
            foreach (int p in new[] { 48, 51, 55 }) pad.Notes.Add(new MNote(0, 1, p));
            Assert.Contains("note \"[c,ef,g]\"", TidalWriter.Pitched(pad, c.Find("poppad")!, 7));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Tokens_round_trip_and_follow_the_grammar()
    {
        var request = new PartRequest { Role = PartRole.Melody, Key = new MKey(9, true), Bars = 2, Chords = new[] { new MChord(9, ChordQuality.Minor), new MChord(5, ChordQuality.Major) }, KickSteps = new[] { 0, 4 } };
        var part = new MPart { Role = PartRole.Melody, Bars = 2, Notes = { new MNote(0, 0.25, 69), new MNote(0.5, 0.125, 72), new MNote(1.25, 0.5, 76) } };
        var tokens = Tokenizer.Encode(part, request);
        var back = Tokenizer.Decode(tokens, request.Key);
        Assert.Equal(part.Notes.Select(n => (n.Start, n.Dur, n.Pitch)), back.Notes.Select(n => (n.Start, n.Dur, n.Pitch)));
        AssertGrammar(tokens, PartRole.Melody, 2, minor: true);
    }

    private static void AssertGrammar(List<int> tokens, PartRole role, int bars, bool minor)
    {
        var grammar = new TokenGrammar(role, bars, minor, inKeyOnly: false);
        int sep = tokens.IndexOf(Tokenizer.Sep);
        for (int i = sep + 1; i < tokens.Count; i++)
            Assert.True(grammar.Allowed(tokens.Skip(sep + 1).Take(i - sep - 1).ToList())[tokens[i]], $"token {i} ({tokens[i]}) not allowed");
    }

    [Theory]
    [InlineData(PartRole.Drums)]
    [InlineData(PartRole.Bass)]
    [InlineData(PartRole.Melody)]
    [InlineData(PartRole.Chords)]
    public void Rule_parts_are_valid_and_in_key(PartRole role)
    {
        var key = new MKey(0, true);
        var chords = new[] { new MChord(0, ChordQuality.Minor), new MChord(8, ChordQuality.Major), new MChord(3, ChordQuality.Major), new MChord(10, ChordQuality.Major) };
        for (int seed = 0; seed < 20; seed++)
            for (int density = 0; density <= 4; density++)
            {
                var r = new PartRequest { Role = role, Key = key, Bars = 4, Density = density, Chords = chords };
                var part = RuleGenerator.Generate(r, new Random(seed));
                Assert.NotEmpty(part.Notes);
                Assert.All(part.Notes, n => Assert.InRange(n.Start, 0, 3.9999));
                if (role != PartRole.Drums)
                {
                    Assert.All(part.Notes, n => Assert.True(key.Contains(n.Pitch), $"{n.Pitch} not in C minor"));
                    var (lo, hi) = TokenGrammar.Range(role);
                    Assert.All(part.Notes, n => Assert.InRange(n.Pitch, lo, hi));
                }
                AssertGrammar(Tokenizer.Encode(part, r), role, 4, minor: true);
            }
    }

    [Fact]
    public void Key_and_chords_are_detected()
    {
        var notes = new List<MNote>();
        int[][] chords = { new[] { 60, 63, 67 }, new[] { 56, 60, 63 }, new[] { 63, 67, 70 }, new[] { 58, 62, 65 } };
        for (int b = 0; b < 4; b++)
            foreach (int p in chords[b]) notes.Add(new MNote(b, 1, p));
        notes.Add(new MNote(0, 1, 36));
        Assert.Equal(new MKey(0, true), Theory.DetectKey(notes));
        Assert.Equal("Cm Ab Eb Bb", string.Join(" ", Theory.DetectChords(notes, 4)));
    }

    [Fact]
    public void Dumped_events_become_context()
    {
        string root = FakeSamples();
        try
        {
            var c = SampleCatalog.Scan(root);
            var lines = new[]
            {
                "#EV-NOW|8.25",
                "#EV|1|8.0|0.25|gatedbd|||1.0|", "#EV|1|8.5|0.5|gatedsd|1.0|||",
                "#EV|2|8.0|1.0|poppad||0.0||", "#EV|2|8.0|1.0|poppad||3.0||", "#EV|2|8.0|1.0|poppad||7.0||",
                "#EV|3|8.0|0.5|popbass|1.0|0.0||",
                "#EV|4|8.0|1.0|superpiano||-14.0||",
                "garbage", "#EV-END",
            };
            var events = TidalContext.Parse(lines, out double? now);
            Assert.Equal(8.25, now);
            var ctx = TidalContext.Build(events, c, 8, 1, now);
            Assert.True(ctx.Has(PartRole.Drums) && ctx.Has(PartRole.Chords) && ctx.Has(PartRole.Bass));
            Assert.Equal(24, ctx.Parts.Single(p => p.Pattern == "3").Part.Notes[0].Pitch);
            Assert.Equal(PartRole.Bass, ctx.Parts.Single(p => p.Pattern == "4").Part.Role); // a synth note below 48
            Assert.Equal("Cm", ctx.Chords[0].ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Midi_files_are_read_into_parts()
    {
        // Format 0, 96 ticks per quarter, 120 bpm: a bass note per beat on channel 1, kicks on channel 10, for 2 bars.
        var events = new List<byte>();
        void Ev(int delta, params byte[] bytes) { events.AddRange(VarLen(delta)); events.AddRange(bytes); }
        Ev(0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20);
        Ev(0, 0xC0, 33);
        for (int i = 0; i < 8; i++)
        {
            Ev(0, 0x90, 36, 100); Ev(0, 0x99, 36, 110);
            Ev(48, 0x80, 36, 0); Ev(0, 0x89, 36, 0);
            Ev(48, 0x90, 0, 0); // a note-on with velocity 0 for an unused key (= off), as padding
        }
        Ev(0, 0xFF, 0x2F, 0);
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray()); file.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 });
        file.AddRange("MTrk"u8.ToArray());
        int len = events.Count;
        file.AddRange(new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len });
        file.AddRange(events);
        var song = MidiReader.Parse(file.ToArray(), "test.mid");
        Assert.NotNull(song);
        Assert.Equal(120, song!.Bpm);
        Assert.Equal(2, song.Bars);
        var bass = song.Parts.Single(p => p.Role == PartRole.Bass);
        Assert.Equal(8, bass.Notes.Count);
        Assert.Equal(0.25, bass.Notes[1].Start, 6);
        Assert.Equal(0.125, bass.Notes[0].Dur, 6);
        Assert.All(song.Parts.Single(p => p.Role == PartRole.Drums).Notes, n => Assert.Equal(0, n.Pitch));
        var sequences = Dataset.Sequences(song, new Random(1)).ToList();
        Assert.NotEmpty(sequences);
        Assert.All(sequences, s => Assert.Equal(Tokenizer.Bos, s[0]));

        static IEnumerable<byte> VarLen(int v)
        {
            var bytes = new List<byte> { (byte)(v & 0x7F) };
            while ((v >>= 7) > 0) bytes.Insert(0, (byte)((v & 0x7F) | 0x80));
            return bytes;
        }
    }
}
