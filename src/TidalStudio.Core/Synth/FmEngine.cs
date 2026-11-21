namespace TidalStudio.Core.Synth;

/// <summary>
/// Offline renderer for <see cref="FmPatch"/>: six sine operators in phase-modulation FM, wired by an algorithm,
/// with feedback on operator 6, a pitch envelope, an LFO, and the same effects chain as Tidepool. Rendered at twice
/// the sample rate and filtered down, so bright, high-index sounds don't alias. Deterministic.
/// </summary>
public static class FmEngine
{
    /// <summary>Phase deviation (radians) of a modulator at level 1 and brightness 1.</summary>
    public const double MaxIndex = 4 * Math.PI;

    public static RenderedNote Render(FmPatch patch, int midi)
    {
        patch.Repair();
        int sr = Math.Clamp(patch.SampleRate, 8000, 192000);
        double hold = Math.Clamp(patch.Hold, 0.01, 30);
        double rate = RateFactor(patch, midi);
        var fx = patch.Fx;

        var algorithm = patch.CurrentAlgorithm;
        double release = 0.05;
        foreach (int c in algorithm.Carriers)
        {
            var op = patch.Operators[c - 1];
            if (op.Enabled && op.Level > 0) release = Math.Max(release, op.Envelope.Release / rate);
        }
        double tail = release * 1.5 + 0.05;
        if (fx.DelayMix > 0) tail += Math.Min(4, fx.DelayTime * Math.Log(0.001) / Math.Log(Math.Clamp(fx.DelayFeedback, 0.05, 0.95)));
        if (fx.ReverbMix > 0) tail += 0.5 + fx.ReverbSize * 3;
        double seconds = Math.Min(Math.Max(patch.MaxLength, hold + 0.05), hold + tail);
        int n = Math.Max(1, (int)(seconds * sr));

        var left = new float[n];
        var right = new float[n];
        RenderOperators(patch, midi, sr, hold, rate, left, right);
        SynthEngine.ApplyFx(fx, sr, left, right);

        int length = SynthEngine.TrimLength(left, right, (int)(hold * sr));
        Array.Resize(ref left, length);
        Array.Resize(ref right, length);
        SynthEngine.FadeOut(left, right, (int)(0.005 * sr));
        // Normalising sets the peak, so Volume has to set the level here too.
        if (patch.Normalize) SynthEngine.Normalize(left, right, 0.89 * Math.Clamp(patch.Volume, 0, 1));
        else SynthEngine.Scale(left, right, 1);
        return new RenderedNote(midi, left, right, sr);
    }

    /// <summary>How much faster the envelopes run for this note (KeyRate): 1 at c5.</summary>
    public static double RateFactor(FmPatch patch, int midi) =>
        Math.Clamp(Math.Pow(2, Math.Clamp(patch.KeyRate, 0, 1) * (midi - 60) / 12.0), 0.25, 4);

    /// <summary>Level multiplier from an operator's key scaling: 1 at c5, less for higher notes.</summary>
    public static double KeyScaleFactor(FmOperator op, int midi) =>
        Math.Clamp(Math.Pow(2, -Math.Clamp(op.KeyScale, 0, 1) * (midi - 60) / 24.0), 0.1, 2.5);

    private static void RenderOperators(FmPatch patch, int midi, int sr, double hold, double rate, float[] left, float[] right)
    {
        const int over = 2;
        int sr2 = sr * over;
        var algorithm = patch.CurrentAlgorithm;
        var ops = patch.Operators;

        // Per operator: is it on, its scale (carrier: loudness; modulator: phase deviation), and who it modulates.
        var on = new bool[7];
        var scale = new double[7];
        var targets = new List<int>[7];
        var carriers = new List<int>();
        for (int k = 1; k <= 6; k++)
        {
            var op = ops[k - 1];
            on[k] = op.Enabled && op.Level > 0;
            targets[k] = algorithm.Links.Where(l => l.From == k).Select(l => l.To).ToList();
            double ks = KeyScaleFactor(op, midi);
            if (algorithm.IsCarrier(k))
            {
                scale[k] = Math.Clamp(op.Level, 0, 1) * ks;
                if (on[k]) carriers.Add(k);
            }
            else scale[k] = Math.Pow(Math.Clamp(op.Level, 0, 1), 2) * MaxIndex * Math.Clamp(patch.Brightness, 0, 2) * ks;
        }
        if (carriers.Count == 0) return;
        double carrierNorm = 1 / Math.Sqrt(carriers.Count);
        // Several carriers are spread a little across the stereo field.
        var panL = new double[7];
        var panR = new double[7];
        for (int c = 0; c < carriers.Count; c++)
        {
            double pan = carriers.Count == 1 ? 0 : (c / (double)(carriers.Count - 1) * 2 - 1) * 0.3;
            double angle = (pan + 1) * Math.PI / 4;
            panL[carriers[c]] = Math.Cos(angle) * Math.Sqrt(2);
            panR[carriers[c]] = Math.Sin(angle) * Math.Sqrt(2);
        }

        var phase = new double[7];
        var output = new double[7];
        var modIn = new double[7];
        double fb1 = 0, fb2 = 0;
        double feedback = Math.Clamp(patch.Feedback, 0, 1) * Math.PI * 0.8;
        var lowL = new Decimator(sr2, sr);
        var lowR = new Decimator(sr2, sr);
        int total = left.Length * over;
        double gateOff = hold * rate;

        for (int i = 0; i < total; i++)
        {
            double t = i / (double)sr2;
            double te = t * rate;
            double lfoFade = patch.LfoDelay > 0 ? Math.Clamp(t / patch.LfoDelay, 0, 1) : 1;
            double lfo = Math.Sin(2 * Math.PI * patch.LfoRate * t) * lfoFade;
            double pitch = midi + patch.Vibrato / 100 * lfo;
            if (patch.PitchEnvAmount != 0)
                pitch += patch.PitchEnvAmount * Math.Exp(-4.6 * t / Math.Max(0.001, patch.PitchEnvDecay));
            double noteHz = SynthEngine.MidiToHz(pitch);

            Array.Clear(modIn);
            double l = 0, r = 0;
            for (int k = 6; k >= 1; k--)
            {
                if (!on[k]) { output[k] = 0; continue; }
                var op = ops[k - 1];
                double freq = op.Fixed ? Math.Clamp(op.FixedHz, 1, 20000) : noteHz * op.Ratio * Math.Pow(2, op.Detune / 1200);
                phase[k] += freq / sr2;
                if (phase[k] >= 1) phase[k] -= Math.Floor(phase[k]);

                double pm = modIn[k];
                if (k == 6 && feedback > 0) pm += feedback * (fb1 + fb2) * 0.5;
                double env = SynthEngine.Envelope(op.Envelope, te, gateOff);
                double y = env * Math.Sin(2 * Math.PI * phase[k] + pm);
                output[k] = y;
                if (k == 6) { fb2 = fb1; fb1 = y; }

                if (targets[k].Count == 0)
                {
                    double s = y * scale[k] * carrierNorm;
                    l += s * panL[k];
                    r += s * panR[k];
                }
                else foreach (int to in targets[k]) modIn[to] += y * scale[k];
            }

            double amp = patch.Volume * (1 - Math.Clamp(patch.Tremolo, 0, 1) * 0.5 * (1 - lfo));
            l = lowL.Process(l * amp);
            r = lowR.Process(r * amp);
            if (i % over == over - 1)
            {
                left[i / over] = (float)l;
                right[i / over] = (float)r;
            }
        }
    }

    /// <summary>4-pole Butterworth low-pass just below the output's Nyquist, run at the oversampled rate.</summary>
    private sealed class Decimator
    {
        private readonly Biquad _a, _b;

        public Decimator(int rate, int outputRate)
        {
            double fc = 0.45 * outputRate;
            _a = new Biquad(rate, fc, 0.5412);
            _b = new Biquad(rate, fc, 1.3066);
        }

        public double Process(double x) => _b.Process(_a.Process(x));
    }

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public Biquad(int rate, double fc, double q)
        {
            double w = 2 * Math.PI * fc / rate;
            double alpha = Math.Sin(w) / (2 * q);
            double cos = Math.Cos(w);
            double a0 = 1 + alpha;
            _b0 = (1 - cos) / 2 / a0;
            _b1 = (1 - cos) / a0;
            _b2 = _b0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
