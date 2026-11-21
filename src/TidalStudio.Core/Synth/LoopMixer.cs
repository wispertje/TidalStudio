namespace TidalStudio.Core.Synth;

/// <summary>
/// Mixes looping sequences on one shared clock, so loops from different synths play in time with each other.
/// The clock counts sixteenth steps since the mixer started; every loop's position is that count modulo its own length
/// (like Tidal: a pattern that starts later joins in at the place it would be if it had always been playing).
/// Positions are derived from the shared count, never kept per loop, so loops can't drift apart; changing the tempo
/// changes how fast the count advances, so every loop follows at once.
/// </summary>
public sealed class LoopMixer
{
    private const int FadeFrames = 512;

    private sealed class Track
    {
        public required RenderedNote Loop;
        public required int Steps;
        // The loop being replaced, faded out over FadeFrames (old loop and new loop read at the same position).
        public RenderedNote? Old;
        public int OldSteps;
        public int Fade;
        // > 0 while the track fades out after Stop; removed when it reaches 0.
        public int FadeOut;
    }

    private readonly object _lock = new();
    private readonly Dictionary<object, Track> _tracks = new();
    private double _step;
    private double _bpm = 120;

    public LoopMixer(int sampleRate) => SampleRate = sampleRate;

    public int SampleRate { get; }

    /// <summary>Tempo of the shared clock (40..300).</summary>
    public double Bpm
    {
        get { lock (_lock) return _bpm; }
        set { lock (_lock) _bpm = Math.Clamp(value, 40, 300); }
    }

    /// <summary>True while at least one loop plays (or is still fading out).</summary>
    public bool IsRunning { get { lock (_lock) return _tracks.Count > 0; } }

    /// <summary>Sixteenth steps since the clock started.</summary>
    public double Step { get { lock (_lock) return _step; } }

    /// <summary>
    /// Plays <paramref name="loop"/> (lasting <paramref name="steps"/> sixteenths) for <paramref name="owner"/>, replacing
    /// its previous loop at the same position. The first loop to play starts the clock at step 0.
    /// </summary>
    public void Play(object owner, RenderedNote loop, int steps)
    {
        steps = Math.Max(1, steps);
        lock (_lock)
        {
            if (_tracks.Count == 0) _step = 0;
            if (_tracks.TryGetValue(owner, out var t) && t.FadeOut == 0)
            {
                t.Old = t.Loop;
                t.OldSteps = t.Steps;
                t.Fade = FadeFrames;
                t.Loop = loop;
                t.Steps = steps;
            }
            else _tracks[owner] = new Track { Loop = loop, Steps = steps };
        }
    }

    /// <summary>Stops the loop of <paramref name="owner"/> (with a short fade).</summary>
    public void Stop(object owner)
    {
        lock (_lock)
            if (_tracks.TryGetValue(owner, out var t) && t.FadeOut == 0) t.FadeOut = FadeFrames;
    }

    public bool IsPlaying(object owner)
    {
        lock (_lock) return _tracks.TryGetValue(owner, out var t) && t.FadeOut == 0;
    }

    /// <summary>
    /// Where <paramref name="owner"/>'s loop is, in steps (0..its length), <paramref name="latencySeconds"/> ago — what is
    /// being heard now. Null when it doesn't play.
    /// </summary>
    public double? StepIn(object owner, double latencySeconds = 0)
    {
        lock (_lock)
        {
            if (!_tracks.TryGetValue(owner, out var t) || t.FadeOut > 0) return null;
            double step = _step - latencySeconds * _bpm * 4 / 60;
            return ((step % t.Steps) + t.Steps) % t.Steps;
        }
    }

    /// <summary>Fills interleaved stereo <paramref name="output"/> and advances the clock.</summary>
    public void Mix(Span<float> output)
    {
        output.Clear();
        int frames = output.Length / 2;
        lock (_lock)
        {
            if (_tracks.Count == 0) return;
            double stepsPerFrame = _bpm * 4 / 60 / SampleRate;
            List<object>? finished = null;
            foreach (var (owner, t) in _tracks)
            {
                for (int i = 0; i < frames; i++)
                {
                    double step = _step + i * stepsPerFrame;
                    var (l, r) = Read(t.Loop, t.Steps, step);
                    if (t.Fade > 0 && t.Old is { } old)
                    {
                        float g = t.Fade / (float)FadeFrames;
                        var (oldL, oldR) = Read(old, t.OldSteps, step);
                        l = l * (1 - g) + oldL * g;
                        r = r * (1 - g) + oldR * g;
                        if (--t.Fade == 0) t.Old = null;
                    }
                    if (t.FadeOut > 0)
                    {
                        float g = t.FadeOut / (float)FadeFrames;
                        l *= g;
                        r *= g;
                        if (--t.FadeOut == 0)
                        {
                            (finished ??= new()).Add(owner);
                            break;
                        }
                    }
                    output[i * 2] += l;
                    output[i * 2 + 1] += r;
                }
            }
            if (finished is not null)
                foreach (var owner in finished) _tracks.Remove(owner);
            _step = _tracks.Count == 0 ? 0 : _step + frames * stepsPerFrame;
        }
        // Several loops add up: bend peaks above 0.8 smoothly towards 1 instead of clipping.
        for (int i = 0; i < output.Length; i++)
        {
            float x = output[i], a = MathF.Abs(x);
            if (a > 0.8f) output[i] = MathF.CopySign(0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f), x);
        }
    }

    /// <summary>The loop at a step of the shared clock (linear interpolation, so loops at any sample rate fit).</summary>
    private static (float L, float R) Read(RenderedNote loop, int steps, double step)
    {
        int n = loop.Left.Length;
        if (n == 0) return (0, 0);
        double phase = ((step % steps) + steps) % steps / steps;
        double pos = phase * n;
        int i0 = Math.Min((int)pos, n - 1);
        int i1 = i0 + 1 == n ? 0 : i0 + 1;
        float f = (float)(pos - i0);
        return (loop.Left[i0] + (loop.Left[i1] - loop.Left[i0]) * f, loop.Right[i0] + (loop.Right[i1] - loop.Right[i0]) * f);
    }
}
