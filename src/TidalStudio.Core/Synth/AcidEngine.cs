namespace TidalStudio.Core.Synth;

/// <summary>
/// Renders an acid pattern: a polyBLEP saw or square with glide into a driven, resonant 4-pole low-pass
/// (2× oversampled), an exponential filter envelope, accents that build up over consecutive accented notes,
/// then drive, tempo-synced delay and reverb. The result is one seamless loop.
/// </summary>
public static class AcidEngine
{
    /// <summary>
    /// The pattern in <paramref name="slot"/> as a loop of exactly its length (× <paramref name="repeats"/>).
    /// It is rendered after one or more "pre-roll" passes of the same pattern, so slides, delay and reverb from the end of the loop
    /// already sound at its start: the sample loops without a click or a gap.
    /// </summary>
    public static RenderedNote Render(AcidBox box, int slot, int repeats = 1)
    {
        var pattern = box.Patterns[Math.Clamp(slot, 0, box.Patterns.Count - 1)];
        int sr = box.SampleRate;
        int loop = LoopSamples(box, pattern);
        double loopSeconds = loop / (double)sr;
        // Enough pre-roll for tails to settle (at least one pass, about 4 s for short loops).
        int preroll = Math.Clamp((int)Math.Ceiling(4 / loopSeconds), 1, 8);
        repeats = Math.Max(1, repeats);
        int passes = preroll + repeats;

        var mono = Synthesise(box, pattern, passes, loop);
        var left = mono;
        var right = (float[])mono.Clone();
        var fx = new FxSettings
        {
            DelayMix = box.Fx.DelayMix,
            DelayTime = Math.Clamp(box.Fx.DelaySteps, 0.25, 16) * box.StepSeconds,
            DelayFeedback = box.Fx.DelayFeedback,
            ReverbMix = box.Fx.ReverbMix,
            ReverbSize = box.Fx.ReverbSize,
        };
        SynthEngine.ApplyFx(fx, sr, left, right);

        int start = preroll * loop;
        var l = left.AsSpan(start, repeats * loop).ToArray();
        var r = right.AsSpan(start, repeats * loop).ToArray();
        if (box.Normalize) SynthEngine.Normalize(l, r, 0.89 * Math.Clamp(box.Voice.Level, 0, 1));
        else SynthEngine.Scale(l, r, Math.Clamp(box.Voice.Level, 0, 1));
        return new RenderedNote(box.Root, l, r, sr);
    }

    /// <summary>Length of one pass of the pattern in samples (exactly its steps at the box's tempo).</summary>
    public static int LoopSamples(AcidBox box, AcidPattern pattern) =>
        Math.Max(1, (int)Math.Round(Math.Clamp(pattern.Length, 1, AcidPattern.MaxSteps) * box.StepSeconds * box.SampleRate));

    /// <summary>Where step <paramref name="index"/> starts within the loop, in samples (swing delays the odd steps).</summary>
    public static int StepStart(AcidBox box, int index, int sampleRate)
    {
        double step = box.StepSeconds;
        double t = index * step + (index % 2 == 1 ? Math.Clamp(box.Swing, 0, 0.5) * step : 0);
        return (int)Math.Round(t * sampleRate);
    }

    private static float[] Synthesise(AcidBox box, AcidPattern pattern, int passes, int loop)
    {
        var v = box.Voice;
        int sr = box.SampleRate;
        int len = Math.Clamp(pattern.Length, 1, AcidPattern.MaxSteps);
        int total = passes * loop;
        var output = new float[total];
        double stepSamples = box.StepSeconds * sr;

        // Per-step start times within one pass.
        var starts = new int[len + 1];
        for (int i = 0; i < len; i++) starts[i] = StepStart(box, i, sr);
        starts[len] = loop;

        // Voice state.
        double phase = 0, midi = box.Root, targetMidi = box.Root;
        bool gate = false;
        int gateOff = -1;
        double amp = 0, filterEnv = 0, accentEnv = 0, accentCv = 0;
        bool noteAccent = false;
        double y1 = 0, y2 = 0, y3 = 0, y4 = 0;   // ladder
        double dcIn = 0, dcOut = 0;              // DC blocker

        double ampAttack = 1 - Math.Exp(-1 / (0.002 * sr));
        double ampRelease = 1 - Math.Exp(-1 / (0.006 * sr));
        double glide = 1 - Math.Exp(-1 / (Math.Max(0.002, v.SlideTime) / 3 * sr));
        double normalDecay = Math.Exp(-4.6 / (Math.Clamp(v.Decay, 0.02, 3) * sr));
        double accentDecay = Math.Exp(-4.6 / (Math.Min(Math.Clamp(v.Decay, 0.02, 3), 0.2) * sr));
        double accentCharge = 1 - Math.Exp(-1 / (0.03 * sr));
        double decay = normalDecay;
        double k = Math.Clamp(v.Resonance, 0, 1) * 4.05;
        double tune = Math.Clamp(v.Tune, -24, 24);
        double logCutoff = Math.Log2(Math.Clamp(v.Cutoff, 20, 12000));
        double envOctaves = Math.Clamp(v.EnvMod, 0, 1) * 5.5;
        double accentAmount = Math.Clamp(v.Accent, 0, 1);
        double gateLength = Math.Clamp(v.Gate, 0.1, 1);
        double drive = Math.Clamp(v.Drive, 0, 1);
        double driveGain = 1 + drive * 9, driveNorm = 1 / Math.Tanh(driveGain);
        double dcCoef = 1 - 2 * Math.PI * 20 / sr;
        int os = sr * 2;

        int nextStep = 0;      // step index (absolute over all passes) that starts next
        int nextStart = 0;
        int totalSteps = passes * len;

        for (int n = 0; n < total; n++)
        {
            // --- step boundary
            while (nextStep < totalSteps && n >= nextStart)
            {
                int i = nextStep % len;
                var s = pattern.Steps[i];
                var prev = pattern.Steps[(i - 1 + len) % len];
                var next = pattern.Steps[(i + 1) % len];
                bool slidIn = nextStep > 0 && prev.Gate && prev.Slide && gate;
                if (s.Gate)
                {
                    targetMidi = box.Root + s.Semitones + tune;
                    if (!slidIn)
                    {
                        // A note on the loop's first step starts the oscillator from zero, so every pass (and the
                        // saved loop played over and over) begins identically.
                        if (i == 0) phase = 0;
                        midi = targetMidi;
                        gate = true;
                        filterEnv = 1;
                        noteAccent = s.Accent;
                        decay = s.Accent ? accentDecay : normalDecay;
                        if (s.Accent) accentEnv = 1;
                    }
                    else if (s.Accent && !noteAccent)
                    {
                        // An accent on a slid-into note still lifts it (without re-triggering the filter).
                        noteAccent = true;
                        accentEnv = 1;
                    }
                    int stepStart = (nextStep / len) * loop + starts[i];
                    gateOff = s.Slide && next.Gate
                        ? -1 // held into the next step
                        : stepStart + (int)(gateLength * stepSamples);
                }
                nextStep++;
                nextStart = nextStep < totalSteps ? (nextStep / len) * loop + starts[nextStep % len] : int.MaxValue;
            }
            if (gateOff >= 0 && n >= gateOff) { gate = false; gateOff = -1; }

            // --- pitch and oscillator
            midi += (targetMidi - midi) * glide;
            double freq = 440 * Math.Pow(2, (midi - 69) / 12);
            double dt = freq / sr;
            phase += dt;
            if (phase >= 1) phase -= 1;
            double osc = v.Wave == AcidWave.Saw
                ? 2 * phase - 1 - PolyBlep(phase, dt)
                : (phase < 0.5 ? 1 : -1) + PolyBlep(phase, dt) - PolyBlep((phase + 0.5) % 1, dt);

            // --- envelopes
            amp += ((gate ? 1 : 0) - amp) * (gate ? ampAttack : ampRelease);
            filterEnv *= decay;
            accentEnv *= accentDecay;
            accentCv += (accentEnv - accentCv) * accentCharge * (accentEnv > accentCv ? 1 : 0.35);

            double accentNow = noteAccent ? accentAmount : 0;
            double octaves = logCutoff + envOctaves * filterEnv * (1 + accentNow * 0.5) + accentAmount * 2.6 * accentCv;
            double fc = Math.Min(Math.Pow(2, octaves), os * 0.2);
            double g = 1 - Math.Exp(-2 * Math.PI * fc / os);

            // --- ladder, 2× oversampled
            double x = osc * 0.75;
            for (int o = 0; o < 2; o++)
            {
                double u = Math.Tanh(x - k * y4);
                y1 += g * (u - y1);
                y2 += g * (y1 - y2);
                y3 += g * (y2 - y3);
                y4 += g * (Math.Tanh(y3) - y4);
            }
            double y = y4 * (1 + k * 0.3);

            double gain = amp * (1 + accentNow * 0.9 * (0.4 + 0.6 * accentCv));
            double sample = y * gain;
            if (drive > 0) sample = Math.Tanh(sample * driveGain) * driveNorm;

            // --- DC blocker (the folded, driven signal can drift)
            double dc = sample - dcIn + dcCoef * dcOut;
            dcIn = sample;
            dcOut = dc;
            output[n] = double.IsFinite(dc) ? (float)dc : 0;
        }
        return output;
    }

    private static double PolyBlep(double t, double dt)
    {
        if (t < dt) { t /= dt; return t + t - t * t - 1; }
        if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
        return 0;
    }
}
