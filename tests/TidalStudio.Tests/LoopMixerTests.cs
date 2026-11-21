using TidalStudio.Core.Synth;
using Xunit;

namespace TidalStudio.Tests;

public class LoopMixerTests
{
    private const int Sr = 8000;

    /// <summary>A loop of <paramref name="steps"/> sixteenths at <paramref name="bpm"/> with a click at the start of every step in <paramref name="clicks"/>.</summary>
    private static RenderedNote Clicks(int steps, double bpm, int sampleRate, params int[] clicks)
    {
        double stepSeconds = 60 / bpm / 4;
        int n = (int)Math.Round(steps * stepSeconds * sampleRate);
        var l = new float[n];
        // A few samples wide, so resampling from another rate can't step over it.
        int width = Math.Max(1, sampleRate / Sr * 3);
        foreach (int c in clicks)
            for (int i = 0; i < width; i++) l[(int)Math.Round(c * stepSeconds * sampleRate) + i] = 0.5f;
        return new RenderedNote(60, l, (float[])l.Clone(), sampleRate);
    }

    /// <summary>Frames (of <paramref name="seconds"/> mixed in blocks like a sound card asks for) where a click starts.</summary>
    private static List<int> Hits(LoopMixer mixer, double seconds, Action<int>? atFrame = null)
    {
        var hits = new List<int>();
        var block = new float[2 * 480];
        int frame = 0;
        bool high = false;
        while (frame < seconds * Sr)
        {
            atFrame?.Invoke(frame);
            mixer.Mix(block);
            for (int i = 0; i < block.Length / 2; i++)
            {
                bool now = block[i * 2] > 0.2f;
                if (now && !high) hits.Add(frame + i);
                high = now;
            }
            frame += block.Length / 2;
        }
        return hits;
    }

    [Fact]
    public void Loops_of_different_lengths_stay_on_the_same_grid_without_drift()
    {
        var mixer = new LoopMixer(Sr) { Bpm = 118 }; // a step is 1017.0 frames: lengths don't round to whole frames
        mixer.Play("a", Clicks(16, 118, Sr, 0), 16);
        mixer.Play("b", Clicks(12, 118, Sr, 0), 12);
        var hits = Hits(mixer, 60);
        double step = 60.0 / 118 / 4 * Sr;
        // Every click lands on a step of the shared grid, a minute in as well as at the start.
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.InRange(Math.Abs(h / step - Math.Round(h / step)) * step, 0, 1.5));
        Assert.Contains(hits, h => h > 59 * Sr);
    }

    [Fact]
    public void A_loop_started_later_joins_in_time()
    {
        var mixer = new LoopMixer(Sr) { Bpm = 120 }; // a step is 1000 frames, a bar 16000
        mixer.Play("drums", Clicks(16, 120, Sr), 16);   // silent loop: just keeps the clock running
        var hits = Hits(mixer, 4, frame => { if (frame == 5280) mixer.Play("bass", Clicks(16, 120, Sr, 0, 8), 16); });
        // Started 5.28 steps into the bar: the first click is the one on step 8, then the next bar's step 0.
        Assert.Equal(new[] { 8000, 16000, 24000 }, hits.Take(3));
    }

    [Fact]
    public void Tempo_changes_apply_to_every_loop_at_once_and_keep_their_place()
    {
        var mixer = new LoopMixer(Sr) { Bpm = 120 };
        mixer.Play("a", Clicks(4, 120, Sr, 0), 4);
        mixer.Play("b", Clicks(8, 120, Sr, 0, 4), 8);
        // Speed up to 240 bpm (a step = 500 frames) at frame 1920 = step 1.92.
        var hits = Hits(mixer, 2, frame => { if (frame == 1920) mixer.Bpm = 240; });
        // The remaining 2.08 steps to step 4 take 2.08 × 500 frames: step 4 is at 1920 + 1040 = 2960.
        Assert.InRange(hits.Skip(1).First(), 2959, 2961);
        // Both loops click together on every 4th step from then on (one onset each time: they line up).
        for (int i = 2; i < hits.Count; i++) Assert.InRange(hits[i] - hits[i - 1], 1999, 2001);
    }

    [Fact]
    public void Loops_rendered_at_another_sample_rate_keep_time()
    {
        var mixer = new LoopMixer(Sr) { Bpm = 120 };
        mixer.Play("a", Clicks(16, 120, 11025, 0, 4, 8, 12), 16);
        var hits = Hits(mixer, 4); // two bars of four clicks, and the last block reaches the next bar's first
        Assert.Equal(9, hits.Count);
        for (int i = 0; i < hits.Count; i++) Assert.InRange(hits[i], i * 4000 - 1, i * 4000 + 1);
    }

    [Fact]
    public void Stop_fades_out_and_the_clock_restarts_when_nothing_plays()
    {
        var mixer = new LoopMixer(Sr) { Bpm = 120 };
        mixer.Play("a", Clicks(16, 120, Sr, 0), 16);
        Hits(mixer, 1);
        Assert.True(mixer.Step > 7);
        Assert.NotNull(mixer.StepIn("a"));
        mixer.Stop("a");
        Assert.False(mixer.IsPlaying("a"));
        Assert.Null(mixer.StepIn("a"));
        Hits(mixer, 0.2);
        Assert.False(mixer.IsRunning);
        Assert.Equal(0, mixer.Step);
        // Playing again starts at the top of the bar.
        mixer.Play("a", Clicks(16, 120, Sr, 0), 16);
        Assert.Equal(0, Hits(mixer, 0.5)[0]);
    }

    [Fact]
    public void Replacing_a_loop_keeps_its_position()
    {
        var mixer = new LoopMixer(Sr) { Bpm = 120 };
        mixer.Play("a", Clicks(16, 120, Sr), 16);
        var hits = Hits(mixer, 3.1, frame => { if (frame == 6240) mixer.Play("a", Clicks(16, 120, Sr, 8), 16); });
        Assert.Equal(new[] { 8000, 24000 }, hits);
    }
}
