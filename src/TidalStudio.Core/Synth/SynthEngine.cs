namespace TidalStudio.Core.Synth;

/// <summary>A rendered note: stereo float samples (-1..1).</summary>
public sealed record RenderedNote(int Midi, float[] Left, float[] Right, int SampleRate)
{
    public double Seconds => Left.Length / (double)SampleRate;
}

/// <summary>
/// Offline renderer for <see cref="SynthPatch"/>: three wavetable oscillators with unison and warp, a resonant
/// state-variable filter, three envelopes, two LFOs and an effects chain (drive, chorus, delay, reverb).
/// Rendering is deterministic: the same patch and note always give the same samples.
/// </summary>
public static class SynthEngine
{
    public static double MidiToHz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

    public static RenderedNote Render(SynthPatch patch, int midi)
    {
        int sr = Math.Clamp(patch.SampleRate, 8000, 192000);
        double hold = Math.Clamp(patch.Hold, 0.01, 30);
        var fx = patch.Fx;
        double tail = patch.AmpEnvelope.Release * 1.5 + 0.05;
        if (fx.DelayMix > 0) tail += Math.Min(4, fx.DelayTime * Math.Log(0.001) / Math.Log(Math.Clamp(fx.DelayFeedback, 0.05, 0.95)));
        if (fx.ReverbMix > 0) tail += 0.5 + fx.ReverbSize * 3;
        double seconds = Math.Min(Math.Max(patch.MaxLength, hold + 0.05), hold + tail);
        int n = Math.Max(1, (int)(seconds * sr));

        var left = new float[n];
        var right = new float[n];
        RenderVoices(patch, midi, sr, hold, left, right);
        ApplyFx(fx, sr, left, right);

        int length = TrimLength(left, right, (int)(hold * sr));
        Array.Resize(ref left, length);
        Array.Resize(ref right, length);
        FadeOut(left, right, (int)(0.005 * sr));
        // Normalising sets the peak, so Volume has to set the level here too (it also feeds the voices, before drive).
        if (patch.Normalize) Normalize(left, right, 0.89 * Math.Clamp(patch.Volume, 0, 1)); // -1 dBFS at full volume
        else Scale(left, right, 1);
        return new RenderedNote(midi, left, right, sr);
    }

    // ------------------------------------------------------------------ voices

    private sealed class Voice
    {
        public required OscSettings Osc { get; init; }
        public required Wavetable Table { get; init; }
        public required double DetuneCents { get; init; }
        public required double Pan { get; init; }
        public required double Gain { get; init; }
        public double Phase;
        // The "Noise" table is real white noise (a looped cycle would sound like a chord), smoothed by Position.
        public bool IsNoise => Table.Name == "Noise";
        public Random? Rng;
        public double NoiseState;
    }

    private static void RenderVoices(SynthPatch patch, int midi, int sr, double hold, float[] left, float[] right)
    {
        var random = new Random(midi * 7919 + 17);
        var voices = new List<Voice>();
        foreach (var osc in patch.Oscillators.Where(o => o.Enabled && o.Level > 0))
        {
            int count = Math.Clamp(osc.Unison, 1, 9);
            var table = Wavetables.Get(osc.Table);
            for (int u = 0; u < count; u++)
            {
                double spreadPos = count == 1 ? 0 : u / (double)(count - 1) * 2 - 1; // -1..1
                voices.Add(new Voice
                {
                    Osc = osc,
                    Table = table,
                    DetuneCents = spreadPos * osc.Detune / 2,
                    Pan = Math.Clamp(osc.Pan + spreadPos * osc.Spread, -1, 1),
                    Gain = osc.Level / Math.Sqrt(count),
                    Phase = count == 1 ? 0 : random.NextDouble(),
                    Rng = new Random(random.Next()),
                });
            }
        }
        if (voices.Count == 0) return;

        var filter = patch.Filter;
        var filterL = new Svf();
        var filterR = new Svf();
        double keyOctaves = filter.KeyTrack * (midi - 60) / 12.0;
        double drive = Math.Clamp(filter.Drive, 0, 1);
        double g = 0, k = 2;
        var lfoRandom1 = new SampleAndHold(11);
        var lfoRandom2 = new SampleAndHold(23);

        for (int i = 0; i < left.Length; i++)
        {
            double t = i / (double)sr;
            double amp = Envelope(patch.AmpEnvelope, t, hold);
            double fenv = Envelope(patch.FilterEnvelope, t, hold);
            var mods = new Mods();
            var modEnv = patch.ModEnvelope;
            if (modEnv.Target != ModTarget.None && modEnv.Amount != 0)
                mods.AddEnvelope(modEnv.Target, modEnv.Amount, Envelope(modEnv.Envelope, t, hold));
            mods.AddLfo(patch.Lfo1, Lfo(patch.Lfo1, t, lfoRandom1));
            mods.AddLfo(patch.Lfo2, Lfo(patch.Lfo2, t, lfoRandom2));

            double l = 0, r = 0;
            foreach (var v in voices)
            {
                var osc = v.Osc;
                double pitch = midi + osc.Octave * 12 + osc.Semi + (osc.Fine + v.DetuneCents) / 100 + mods.Pitch;
                double freq = MidiToHz(pitch);
                v.Phase += freq / sr;
                if (v.Phase >= 1) v.Phase -= Math.Floor(v.Phase);

                double position = Math.Clamp(osc.Position + mods.Position, 0, 1);
                double warp = Math.Clamp(osc.WarpAmount + mods.Warp, 0, 1);
                double s;
                if (v.IsNoise)
                {
                    // One-pole low-pass: position 0 = white, 1 = dark rumble.
                    double white = v.Rng!.NextDouble() * 2 - 1;
                    double coeff = Math.Pow(position, 2) * 0.995;
                    v.NoiseState = white * (1 - coeff) + v.NoiseState * coeff;
                    s = v.NoiseState * (1 + position * 4);
                }
                else s = Oscillate(v.Table, v.Phase, position, freq, sr, osc.Warp, warp);
                s *= v.Gain;

                double angle = (Math.Clamp(v.Pan + mods.Pan, -1, 1) + 1) * Math.PI / 4;
                l += s * Math.Cos(angle);
                r += s * Math.Sin(angle);
            }

            if (drive > 0)
            {
                double d = 1 + drive * 8;
                l = Math.Tanh(l * d) / Math.Sqrt(d);
                r = Math.Tanh(r * d) / Math.Sqrt(d);
            }

            if (filter.Mode != FilterMode.Off)
            {
                if ((i & 15) == 0)
                {
                    double cutoff = filter.Cutoff * Math.Pow(2, filter.EnvAmount * fenv + keyOctaves + mods.Cutoff);
                    cutoff = Math.Clamp(cutoff, 20, 0.45 * sr);
                    g = Math.Tan(Math.PI * cutoff / sr);
                    k = 2 - 1.95 * Math.Clamp(filter.Resonance, 0, 1);
                }
                l = filterL.Process(l, g, k, filter.Mode);
                r = filterR.Process(r, g, k, filter.Mode);
            }

            double gain = amp * patch.Volume * mods.Amp;
            left[i] = (float)(l * gain);
            right[i] = (float)(r * gain);
        }
    }

    private static double Oscillate(Wavetable table, double phase, double position, double freq, int sr, WarpMode mode, double warp)
    {
        if (mode == WarpMode.None || warp <= 0) return table.Read(phase, position, Wavetable.LevelFor(freq, sr));
        switch (mode)
        {
            case WarpMode.Bend:
            {
                // Phase distortion: squeeze the first half of the cycle, stretch the second.
                double knee = 0.5 - 0.45 * warp;
                double p = phase < knee ? 0.5 * phase / knee : 0.5 + 0.5 * (phase - knee) / (1 - knee);
                return table.Read(p, position, Wavetable.LevelFor(freq * (1 + warp * 3), sr));
            }
            case WarpMode.Sync:
            {
                double ratio = 1 + warp * 7;
                double p = phase * ratio;
                return table.Read(p - Math.Floor(p), position, Wavetable.LevelFor(freq * ratio, sr));
            }
            case WarpMode.Fold:
            {
                double s = table.Read(phase, position, Wavetable.LevelFor(freq, sr));
                return Math.Sin(s * (1 + warp * 5) * Math.PI / 2);
            }
            case WarpMode.Crush:
            {
                double s = table.Read(phase, position, Wavetable.LevelFor(freq, sr));
                double steps = Math.Pow(2, 15 - warp * 13);
                return Math.Round(s * steps) / steps;
            }
            default:
                return table.Read(phase, position, Wavetable.LevelFor(freq, sr));
        }
    }

    /// <summary>Sum of all modulation for one sample, already scaled to its target's units.</summary>
    private struct Mods
    {
        public double Pitch;    // semitones
        public double Position; // table position
        public double Cutoff;   // octaves
        public double Pan;
        public double Warp;
        private double _amp;
        private bool _ampSet;
        public double Amp => _ampSet ? Math.Max(0, _amp) : 1;

        public void AddEnvelope(ModTarget target, double amount, double env)
        {
            switch (target)
            {
                case ModTarget.Pitch: Pitch += amount * 24 * env; break;
                case ModTarget.Position: Position += amount * env; break;
                case ModTarget.Cutoff: Cutoff += amount * 6 * env; break;
                case ModTarget.Pan: Pan += amount * env; break;
                case ModTarget.Warp: Warp += amount * env; break;
                case ModTarget.Amp: MulAmp(1 + amount * (env - 1)); break;
            }
        }

        public void AddLfo(LfoSettings lfo, double value)
        {
            if (lfo.Target == ModTarget.None || lfo.Amount <= 0) return;
            double a = lfo.Amount * value; // value is -1..1 (already faded in)
            switch (lfo.Target)
            {
                case ModTarget.Pitch: Pitch += a * 12; break;
                case ModTarget.Position: Position += a * 0.5; break;
                case ModTarget.Cutoff: Cutoff += a * 4; break;
                case ModTarget.Pan: Pan += a; break;
                case ModTarget.Warp: Warp += a * 0.5; break;
                case ModTarget.Amp: MulAmp(1 - lfo.Amount * (0.5 - 0.5 * value)); break;
            }
        }

        private void MulAmp(double factor)
        {
            _amp = _ampSet ? _amp * factor : factor;
            _ampSet = true;
        }
    }

    /// <summary>ADSR level at time <paramref name="t"/> for a key released at <paramref name="gateOff"/>.</summary>
    public static double Envelope(EnvelopeSettings e, double t, double gateOff)
    {
        double Held(double time)
        {
            if (time < e.Attack) return e.Attack <= 0 ? 1 : time / e.Attack;
            double d = time - Math.Max(e.Attack, 0);
            double s = Math.Clamp(e.Sustain, 0, 1);
            return e.Decay <= 0 ? s : s + (1 - s) * Math.Exp(-5 * d / e.Decay);
        }
        if (t < gateOff) return Held(t);
        double start = Held(gateOff);
        double r = t - gateOff;
        return e.Release <= 0 ? 0 : start * Math.Exp(-6.9 * r / e.Release); // -60 dB after Release
    }

    private sealed class SampleAndHold(int seed)
    {
        private readonly Random _random = new(seed);
        private long _cycle = -1;
        private double _value;

        public double At(double phase)
        {
            long cycle = (long)Math.Floor(phase);
            while (_cycle < cycle) { _cycle++; _value = _random.NextDouble() * 2 - 1; }
            return _value;
        }
    }

    private static double Lfo(LfoSettings lfo, double t, SampleAndHold sh)
    {
        if (lfo.Target == ModTarget.None || lfo.Amount <= 0) return 0;
        double phase = t * lfo.Rate;
        double p = phase - Math.Floor(phase);
        double v = lfo.Shape switch
        {
            LfoShape.Triangle => p < 0.5 ? 4 * p - 1 : 3 - 4 * p,
            LfoShape.Saw => 1 - 2 * p,
            LfoShape.Square => p < 0.5 ? 1 : -1,
            LfoShape.SampleHold => sh.At(phase),
            _ => Math.Sin(2 * Math.PI * p),
        };
        double fade = lfo.FadeIn > 0 ? Math.Clamp(t / lfo.FadeIn, 0, 1) : 1;
        return v * fade;
    }

    /// <summary>Topology-preserving state-variable filter (Zavalishin / Simper).</summary>
    private sealed class Svf
    {
        private double _ic1, _ic2;

        public double Process(double x, double g, double k, FilterMode mode)
        {
            double a1 = 1 / (1 + g * (g + k));
            double a2 = g * a1;
            double a3 = g * a2;
            double v3 = x - _ic2;
            double v1 = a1 * _ic1 + a2 * v3;
            double v2 = _ic2 + a2 * _ic1 + a3 * v3;
            _ic1 = 2 * v1 - _ic1;
            _ic2 = 2 * v2 - _ic2;
            return mode switch
            {
                FilterMode.HighPass => x - k * v1 - v2,
                FilterMode.BandPass => v1,
                FilterMode.Notch => x - k * v1,
                _ => v2,
            };
        }
    }

    // ------------------------------------------------------------------ effects

    /// <param name="loopSeconds">
    /// For loops (Tidepool Acid): the length of one pass. The chorus and tape LFOs are then tuned to whole cycles per pass,
    /// so the end of a saved loop flows into its start without a click.
    /// </param>
    internal static void ApplyFx(FxSettings fx, int sr, float[] left, float[] right, double? loopSeconds = null)
    {
        if (fx.Drive > 0)
        {
            double d = 1 + fx.Drive * 10;
            double norm = 1 / Math.Tanh(d);
            for (int i = 0; i < left.Length; i++)
            {
                left[i] = (float)(Math.Tanh(left[i] * d) * norm);
                right[i] = (float)(Math.Tanh(right[i] * d) * norm);
            }
        }
        if (fx.ChorusMix > 0) Chorus(fx, sr, left, right, loopSeconds);
        if (fx.DelayMix > 0) Delay(fx, sr, left, right);
        if (fx.ReverbMix > 0) new Reverb(sr, fx.ReverbSize).Process(left, right, fx.ReverbMix);
        if (fx.Tape > 0) Tape(Math.Clamp(fx.Tape, 0, 1), sr, left, right, loopSeconds);
    }

    /// <summary>An LFO rate rounded to whole cycles per loop pass (at least one), or unchanged when not rendering a loop.</summary>
    private static double LoopRate(double rate, double? loopSeconds) =>
        loopSeconds is { } s && s > 0 ? Math.Max(1, Math.Round(rate * s)) / s : rate;

    private static void Chorus(FxSettings fx, int sr, float[] left, float[] right, double? loopSeconds)
    {
        double mix = fx.ChorusMix;
        double rate = LoopRate(Math.Clamp(fx.ChorusRate, 0.05, 8), loopSeconds);
        double depth = Math.Clamp(fx.ChorusDepth, 0, 10) / 1000;
        int max = (int)(0.03 * sr) + 2;
        var bufL = new float[max];
        var bufR = new float[max];
        for (int i = 0; i < left.Length; i++)
        {
            int w = i % max;
            bufL[w] = left[i];
            bufR[w] = right[i];
            double t = i / (double)sr;
            double dl = (0.012 + depth * Math.Sin(2 * Math.PI * rate * t)) * sr;
            double dr = (0.012 + depth * Math.Sin(2 * Math.PI * rate * t + Math.PI)) * sr;
            double wl = Tap(bufL, w, dl, max), wr = Tap(bufR, w, dr, max);
            left[i] = (float)(left[i] * (1 - mix * 0.5) + wl * mix);
            right[i] = (float)(right[i] * (1 - mix * 0.5) + wr * mix);
        }
    }

    /// <summary>
    /// Tape: wow (a slow pitch drift) and flutter (a fast, small one) from a modulated delay, gentle saturation, and a top
    /// end that rolls off as <paramref name="amount"/> rises — the worn, nostalgic texture of a cassette or a tape master.
    /// </summary>
    private static void Tape(double amount, int sr, float[] left, float[] right, double? loopSeconds)
    {
        double wowRate = LoopRate(0.45, loopSeconds), flutterRate = LoopRate(6.5, loopSeconds);
        double wow = amount * 0.0012 * sr, flutter = amount * 0.00006 * sr;     // depth in samples
        double centre = wow + flutter;                                           // delay never goes below 0
        int max = (int)Math.Ceiling(2 * centre) + 3;
        var bufL = new float[max];
        var bufR = new float[max];
        double drive = 1 + amount * 1.5;
        double cutoff = 16000 * Math.Pow(6000 / 16000.0, amount);
        double a = 1 - Math.Exp(-2 * Math.PI * Math.Min(cutoff, sr * 0.45) / sr);
        double lpL = 0, lpR = 0;
        for (int i = 0; i < left.Length; i++)
        {
            int w = i % max;
            bufL[w] = left[i];
            bufR[w] = right[i];
            double t = i / (double)sr;
            double delay = centre + wow * Math.Sin(2 * Math.PI * wowRate * t) + flutter * Math.Sin(2 * Math.PI * flutterRate * t);
            double l = Math.Tanh(Tap(bufL, w, delay, max) * drive) / drive;
            double r = Math.Tanh(Tap(bufR, w, delay, max) * drive) / drive;
            lpL += a * (l - lpL);
            lpR += a * (r - lpR);
            left[i] = (float)lpL;
            right[i] = (float)lpR;
        }
    }

    private static double Tap(float[] buffer, int write, double delay, int size)
    {
        double pos = write - delay;
        while (pos < 0) pos += size;
        int i0 = (int)pos % size, i1 = (i0 + 1) % size;
        double f = pos - Math.Floor(pos);
        return buffer[i0] + (buffer[i1] - buffer[i0]) * f;
    }

    private static void Delay(FxSettings fx, int sr, float[] left, float[] right)
    {
        int d = Math.Max(1, (int)(Math.Clamp(fx.DelayTime, 0.01, 2) * sr));
        double fb = Math.Clamp(fx.DelayFeedback, 0, 0.95);
        var bufL = new float[d];
        var bufR = new float[d];
        for (int i = 0; i < left.Length; i++)
        {
            int p = i % d;
            float echoL = bufL[p], echoR = bufR[p];
            // Ping-pong: each channel's echo feeds the other side.
            bufL[p] = (float)(left[i] + echoR * fb);
            bufR[p] = (float)(right[i] + echoL * fb);
            left[i] += (float)(echoL * fx.DelayMix);
            right[i] += (float)(echoR * fx.DelayMix);
        }
    }

    /// <summary>A small Freeverb-style reverb: 8 parallel combs and 4 allpasses per channel.</summary>
    private sealed class Reverb
    {
        private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllpassTunings = { 556, 441, 341, 225 };
        private const int StereoSpread = 23;

        private readonly Comb[] _combsL, _combsR;
        private readonly Allpass[] _allL, _allR;

        public Reverb(int sr, double size)
        {
            double scale = sr / 44100.0;
            double feedback = 0.7 + 0.28 * Math.Clamp(size, 0, 1);
            _combsL = CombTunings.Select(t => new Comb((int)(t * scale), feedback, 0.25)).ToArray();
            _combsR = CombTunings.Select(t => new Comb((int)((t + StereoSpread) * scale), feedback, 0.25)).ToArray();
            _allL = AllpassTunings.Select(t => new Allpass((int)(t * scale))).ToArray();
            _allR = AllpassTunings.Select(t => new Allpass((int)((t + StereoSpread) * scale))).ToArray();
        }

        public void Process(float[] left, float[] right, double mix)
        {
            for (int i = 0; i < left.Length; i++)
            {
                double input = (left[i] + right[i]) * 0.015;
                double l = 0, r = 0;
                foreach (var c in _combsL) l += c.Process(input);
                foreach (var c in _combsR) r += c.Process(input);
                foreach (var a in _allL) l = a.Process(l);
                foreach (var a in _allR) r = a.Process(r);
                left[i] = (float)(left[i] * (1 - mix * 0.5) + l * mix * 3);
                right[i] = (float)(right[i] * (1 - mix * 0.5) + r * mix * 3);
            }
        }

        private sealed class Comb(int size, double feedback, double damp)
        {
            private readonly double[] _buffer = new double[Math.Max(1, size)];
            private int _index;
            private double _store;

            public double Process(double input)
            {
                double output = _buffer[_index];
                _store = output * (1 - damp) + _store * damp;
                _buffer[_index] = input + _store * feedback;
                if (++_index >= _buffer.Length) _index = 0;
                return output;
            }
        }

        private sealed class Allpass(int size)
        {
            private readonly double[] _buffer = new double[Math.Max(1, size)];
            private int _index;

            public double Process(double input)
            {
                double buffered = _buffer[_index];
                double output = buffered - input;
                _buffer[_index] = input + buffered * 0.5;
                if (++_index >= _buffer.Length) _index = 0;
                return output;
            }
        }
    }

    // ------------------------------------------------------------------ finishing

    /// <summary>Drops trailing silence (below -70 dB), keeping at least the held part.</summary>
    internal static int TrimLength(float[] left, float[] right, int minLength)
    {
        const float threshold = 0.0003f;
        float peak = 0;
        for (int i = 0; i < left.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        float limit = Math.Max(threshold * peak, 1e-7f);
        int last = left.Length - 1;
        while (last > minLength && Math.Abs(left[last]) < limit && Math.Abs(right[last]) < limit) last--;
        return Math.Min(left.Length, last + 1);
    }

    internal static void FadeOut(float[] left, float[] right, int samples)
    {
        samples = Math.Min(samples, left.Length);
        for (int i = 0; i < samples; i++)
        {
            float g = i / (float)samples;
            left[^(i + 1)] *= g;
            right[^(i + 1)] *= g;
        }
    }

    internal static void Normalize(float[] left, float[] right, double target)
    {
        float peak = 0;
        for (int i = 0; i < left.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        if (peak < 1e-6f) return;
        Scale(left, right, target / peak);
    }

    /// <summary>Scales and hard-limits to -1..1 (never writes NaN or clipped values).</summary>
    internal static void Scale(float[] left, float[] right, double factor)
    {
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = Clean(left[i] * factor);
            right[i] = Clean(right[i] * factor);
        }
    }

    private static float Clean(double v) => double.IsFinite(v) ? (float)Math.Clamp(v, -1, 1) : 0;
}
