using TidalStudio.Core.Audio;
using TidalStudio.Core.Events;
using TidalStudio.Core.Tidal;
using TidalStudio.Core.Vj;
using Xunit;

namespace TidalStudio.Tests;

public class VjTests
{
    private static int _id = 1000;

    private static (CodeBlock Block, IReadOnlyList<LiteralRef> Literals) Run(CodeLog log, string code, double time, bool success = true)
    {
        var pre = Preprocessor.Wrap(code, () => ++_id);
        return (log.Add(code, success, pre.Literals, time), pre.Literals);
    }

    [Fact]
    public void Pattern_ids_are_found()
    {
        Assert.Equal(new[] { "1", "2" }, CodeLog.PatternIdsIn("do\n  d1 $ s \"bd\"\n  d2 $ s \"hh*8\""));
        Assert.Equal(new[] { "drums" }, CodeLog.PatternIdsIn("p \"drums\" $ s \"bd\""));
        Assert.Empty(CodeLog.PatternIdsIn("setcps (120/60/4)"));
        Assert.Empty(CodeLog.PatternIdsIn("-- d1 $ s \"bd\""));
    }

    [Fact]
    public void The_program_keeps_the_latest_block_per_pattern()
    {
        var log = new CodeLog();
        Run(log, "d1 $ s \"bd*4\"", 1);
        Run(log, "d2 $ s \"hh*8\"", 2);
        Run(log, "d1 $ s \"bd sd\"", 3);
        Assert.Equal(new[] { "d2 $ s \"hh*8\"", "d1 $ s \"bd sd\"" }, log.Program.Select(b => b.Text));

        Run(log, "d2 $ silence", 4);
        Assert.Single(log.Program);

        Run(log, "setcps 0.5", 5);
        Assert.Single(log.Program);
        Assert.Equal("setcps 0.5", log.LastOther?.Text);

        Run(log, "d3 $ s \"oops", 6, success: false);
        Assert.Single(log.Program);
        Assert.NotNull(log.LastError);

        Run(log, "hush", 7);
        Assert.Empty(log.Program);
    }

    [Fact]
    public void Sounding_events_light_up_their_characters()
    {
        var log = new CodeLog();
        string code = "-- drums\nd1 $ s \"bd sd\"\n  # gain 1";
        var (_, literals) = Run(log, code, 10);
        var lit = Assert.Single(literals);
        // "sd" is characters 3..5 inside the literal.
        var e = new TidalEvent { Time = 10.5, Duration = 0.5, PatternId = "1", Sound = "sd", Spans = new[] { new SourceSpan(lit.Id, 3, 5) } };
        var lines = CodeView.Layout(log, new[] { e }, 10.6);
        Assert.Equal(2, lines.Count); // the comment line is left out
        Assert.Equal("d1 $ s \"bd sd\"", lines[0].Text);
        Assert.Equal("1", lines[0].PatternId);
        var h = Assert.Single(lines[0].Highlights);
        Assert.Equal("sd", lines[0].Text.Substring(h.Start, h.Length));
        Assert.True(h.Strength is > 0.5 and < 1);
        Assert.Empty(lines[1].Highlights);
        Assert.Equal(lines[0].Text.Length + 1, lines[1].CharOffset);
    }

    [Fact]
    public void Errors_and_setcps_show_for_a_while()
    {
        var log = new CodeLog();
        Run(log, "setcps 0.5", 0);
        Run(log, "d1 $ s \"bd", 0, success: false);
        var lines = CodeView.Layout(log, Array.Empty<TidalEvent>(), 1);
        Assert.Contains(lines, l => l.IsTransient);
        Assert.Contains(lines, l => l.IsError);
        Assert.Empty(CodeView.Layout(log, Array.Empty<TidalEvent>(), 20));
    }

    [Fact]
    public void The_draft_shows_until_it_is_run()
    {
        var log = new CodeLog();
        log.SetDraft("-- next\nd5 $ s \"arpy*4\"", 1);
        var lines = CodeView.Layout(log, Array.Empty<TidalEvent>(), 2);
        var draft = Assert.Single(lines);
        Assert.True(draft.IsDraft);
        Assert.Equal("5", draft.PatternId);
        Run(log, "-- next\nd5 $ s \"arpy*4\"", 3);
        Assert.Null(log.Draft);
        Assert.DoesNotContain(CodeView.Layout(log, Array.Empty<TidalEvent>(), 3.5), l => l.IsDraft);
        log.SetDraft("d6 $ s \"x\"", 4);
        Assert.Empty(CodeView.Layout(log, Array.Empty<TidalEvent>(), 4 + CodeView.DraftSeconds + 1).Where(l => l.IsDraft));
    }

    [Theory]
    [InlineData("bd", HitKind.Kick)]
    [InlineData("808bd", HitKind.Kick)]
    [InlineData("mykitbd", HitKind.Kick)]
    [InlineData("fmkick", HitKind.Kick)]
    [InlineData("sn", HitKind.Snare)]
    [InlineData("gatedsd", HitKind.Snare)]
    [InlineData("cp", HitKind.Snare)]
    [InlineData("hh27", HitKind.Hat)]
    [InlineData("acidwhoh", HitKind.Hat)]
    [InlineData("epopsh", HitKind.Hat)]
    [InlineData("popchord", HitKind.Tone)]
    [InlineData("fmepiano", HitKind.Tone)]
    [InlineData("popbass", HitKind.Bass)]
    public void Sounds_are_classified(string sound, HitKind kind) => Assert.Equal(kind, VjSignals.Classify(sound, null));

    [Fact]
    public void Events_alone_drive_the_signals()
    {
        var s = new VjSignals();
        s.OnEvent(new TidalEvent { Sound = "bd", Gain = 1 }, VjSource.Events);
        s.Update(1 / 60.0, null, VjSource.Events);
        Assert.Equal(1, s.Beats);
        Assert.True(s.Beat > 0.9 && s.Kick > 0.9);
        Assert.True(s.Bass > 0.2, $"bass {s.Bass}");
        for (int i = 0; i < 120; i++) s.Update(1 / 60.0, null, VjSource.Events);
        Assert.True(s.Beat < 0.01 && s.Bass < 0.05);
        // Audio-only ignores events.
        s.OnEvent(new TidalEvent { Sound = "bd", Gain = 1 }, VjSource.Audio);
        Assert.Equal(1, s.Beats);
    }

    [Fact]
    public void Kicks_in_the_audio_are_beats()
    {
        var audio = new AudioRingBuffer(1 << 16) { SampleRate = 48000 };
        var s = new VjSignals();
        int frame = 800; // 60 fps
        long n = 0;
        for (int f = 0; f < 60 * 4; f++) // 4 s at 120 bpm = 8 kicks
        {
            var block = new float[frame];
            for (int i = 0; i < frame; i++, n++)
            {
                double t = n / 48000.0, beat = t * 2 % 1;
                block[i] = (float)(Math.Sin(2 * Math.PI * 55 * t) * Math.Exp(-beat * 12) * 0.8 + Math.Sin(2 * Math.PI * 3000 * t) * 0.02);
            }
            audio.Write(block);
            s.Update(1 / 60.0, audio, VjSource.Audio);
        }
        Assert.True(s.AudioActive);
        Assert.InRange(s.Beats, 6, 9);
        Assert.Contains(s.Spectrum, v => v > 0.3f);
    }

    [Fact]
    public void Director_switches_every_few_cycles_and_finishes_transitions()
    {
        var o = new VjOptions { Auto = true, AutoCycles = 4, TransitionSeconds = 1, AutoFx = false };
        var d = new VjDirector(o, seed: 3);
        int start = d.Scene;
        d.Update(0, 0.5, playing: true);
        d.Update(1, 3.9, playing: true);
        Assert.Equal(-1, d.Next);
        d.Update(2, 4.1, playing: true);
        Assert.True(d.Next >= 0 && d.Next != start);
        int next = d.Next;
        d.Update(2.5, 4.5, playing: true);
        Assert.InRange(d.Progress, 0.4, 0.6);
        d.Update(3.1, 5, playing: true);
        Assert.Equal(next, d.Scene);
        Assert.Equal(-1, d.Next);
        Assert.Equal(next, o.Scene);

        d.Go(next, 10); // same scene: nothing
        Assert.Equal(-1, d.Next);
        o.Transition = 2;
        d.Go(next + 1, 10);
        Assert.Equal(2, d.TransitionKind);
    }

    [Fact]
    public void Particles_spawn_and_die()
    {
        var p = new VjParticles();
        p.Spawn(new TidalEvent { Sound = "sd", Gain = 1, Pan = 0.5 }, 0xFFFFFFFF, labels: true, intensity: 1);
        Assert.True(p.Items.Count >= 10);
        for (int i = 0; i < 120; i++) p.Step(1 / 60.0);
        Assert.Empty(p.Items);
    }

    [Fact]
    public void Options_repair_clamps()
    {
        var o = new VjOptions { Scene = 99, Transition = 50, Trails = 5, Quality = -3 };
        o.Repair();
        Assert.Equal(VjDirector.Scenes.Count - 1, o.Scene);
        Assert.Equal(VjDirector.Transitions.Count - 1, o.Transition);
        Assert.Equal(0.97, o.Trails);
        Assert.Equal(0, o.Quality);
    }
}
