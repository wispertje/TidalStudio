namespace TidalStudio.Core.Synth;

/// <summary>
/// Offline drum synthesis from layers: two wavetable oscillators with pitch drops (oscillator 2 can FM oscillator 1),
/// filtered noise with optional repeated bursts, a metallic square-wave cluster and a click; then drive, a filter
/// with its own envelope, and a room. Rendering is deterministic: the same voice and variation give the same samples.
/// </summary>
public static class DrumEngine
{
    /// <summary>The classic metallic partials (Hz) of analog hats and cymbals.</summary>
    public static readonly double[] MetalPartials = { 205.3, 304.4, 369.6, 522.7, 540.0, 800.0 };

    /// <summary>The settings of version <paramref name="variation"/> (0 = the voice itself).</summary>
    public static DrumVoice Vary(DrumVoice voice, int variation)
    {
        if (variation <= 0) return voice;
        var v = voice.Clone();
        var r = new Random(variation * 104729 + (int)voice.Model * 31);
        double spread = Math.Clamp(voice.Spread, 0, 1);
        double J() => r.NextDouble() * 2 - 1;
        v.Tune += J() * spread * 3;
        double decay = 1 + J() * spread * 0.4;
        foreach (var env in new[] { v.Osc1.Envelope, v.Osc2.Envelope, v.Noise.Envelope, v.Metal.Envelope }) env.Decay *= decay;
        v.Osc1.PitchDrop = Math.Max(0, v.Osc1.PitchDrop * (1 + J() * spread * 0.3));
        v.Noise.Cutoff *= Math.Pow(2, J() * spread * 0.5);
        v.Metal.Cutoff *= Math.Pow(2, J() * spread * 0.3);
        v.Filter.Cutoff *= Math.Pow(2, J() * spread * 0.4);
        v.Click.Level = Math.Clamp(v.Click.Level * (1 + J() * spread * 0.4), 0, 1);
        return v;
    }

    /// <summary>How long the voice sounds before effects, seconds.</summary>
    public static double SoundingLength(DrumVoice v)
    {
        double length = 0.005;
        if (v.Osc1.Enabled) length = Math.Max(length, v.Osc1.Envelope.Length);
        if (v.Osc2.Enabled && v.Osc2.Level > 0) length = Math.Max(length, v.Osc2.Envelope.Length);
        if (v.Noise.Enabled) length = Math.Max(length, NoiseStart(v.Noise, Math.Max(1, v.Noise.Bursts) - 1) + v.Noise.Envelope.Length);
        if (v.Metal.Enabled) length = Math.Max(length, v.Metal.Envelope.Length);
        if (v.Click.Enabled) length = Math.Max(length, v.Click.Length);
        return length;
    }

    public static RenderedNote Render(DrumVoice voice, int variation, int sampleRate, bool normalize = true)
    {
        var v = Vary(voice, variation);
        int sr = Math.Clamp(sampleRate, 8000, 192000);
        double seconds = SoundingLength(v) + 0.01;
        if (v.Room > 0) seconds += 0.3 + v.Room * (0.5 + v.RoomSize * 2);
        seconds = Math.Min(seconds, Math.Clamp(v.MaxLength, 0.05, 10));
        int n = Math.Max(1, (int)(seconds * sr));

        var mono = new float[n];
        Synthesize(v, sr, mono, new Random(variation * 7919 + 13));

        double angle = (Math.Clamp(v.Pan, -1, 1) + 1) * Math.PI / 4;
        var left = new float[n];
        var right = new float[n];
        for (int i = 0; i < n; i++)
        {
            left[i] = (float)(mono[i] * Math.Cos(angle) * Math.Sqrt(2));
            right[i] = (float)(mono[i] * Math.Sin(angle) * Math.Sqrt(2));
        }
        if (v.Room > 0)
            SynthEngine.ApplyFx(new FxSettings { ReverbMix = Math.Clamp(v.Room, 0, 1) * 0.6, ReverbSize = Math.Clamp(v.RoomSize, 0, 1) }, sr, left, right);

        int length = SynthEngine.TrimLength(left, right, (int)(0.01 * sr));
        Array.Resize(ref left, length);
        Array.Resize(ref right, length);
        SynthEngine.FadeOut(left, right, (int)(0.003 * sr));
        double level = Math.Clamp(v.Level, 0, 1);
        if (normalize) SynthEngine.Normalize(left, right, 0.89 * level);
        else SynthEngine.Scale(left, right, level);
        return new RenderedNote(60, left, right, sr);
    }

    /// <summary>Pitch-drop shape: 1 at the start, 0 after <paramref name="time"/>.</summary>
    public static double PitchShape(double t, double time, double curve)
    {
        if (time <= 0 || t >= time) return 0;
        double x = t / time;
        double c = Math.Clamp(curve, 0, 1);
        double exp = (Math.Exp(-6 * x) - Math.Exp(-6)) / (1 - Math.Exp(-6));
        return (1 - c) * (1 - x) + c * exp;
    }

    /// <summary>Current pitch of oscillator 1, Hz.</summary>
    public static double Osc1Frequency(DrumVoice v, double t) =>
        v.Osc1.Frequency * Math.Pow(2, (v.Tune + v.Osc1.PitchDrop * PitchShape(t, v.Osc1.PitchTime, v.Osc1.PitchCurve)) / 12);

    private static double NoiseStart(DrumNoise noise, int burst) => burst * Math.Max(0.001, noise.BurstSpacing);

    /// <summary>Noise level at <paramref name="t"/>: quick hits for all but the last burst, which gets the full envelope.</summary>
    public static double NoiseEnvelope(DrumNoise noise, double t)
    {
        int bursts = Math.Clamp(noise.Bursts, 1, 8);
        double env = 0;
        for (int b = 0; b < bursts - 1; b++)
        {
            double tb = t - NoiseStart(noise, b);
            if (tb >= 0) env = Math.Max(env, Math.Exp(-tb / 0.0045) * (tb < 0.0005 ? tb / 0.0005 : 1));
        }
        return Math.Max(env, noise.Envelope.At(t - NoiseStart(noise, bursts - 1)));
    }

    private static void Synthesize(DrumVoice v, int sr, float[] output, Random rng)
    {
        var o1 = v.Osc1;
        var o2 = v.Osc2;
        var noise = v.Noise;
        var metal = v.Metal;
        var click = v.Click;
        var filter = v.Filter;
        var table1 = Wavetables.Get(o1.Table);
        var table2 = Wavetables.Get(o2.Table);
        double metalRatio = Math.Pow(2, (v.Tune + metal.Tune) / 12);
        int metalVoices = Math.Clamp(metal.Voices, 1, 6);

        var noiseFilter = new Svf();
        var metalFilter = new Svf();
        var metalHighPass = new Svf();
        var clickFilter = new Svf();
        var filterL = new Svf();
        double phase1 = 0, phase2 = 0, osc2Out = 0;
        var metalPhases = new double[6];
        double pinkB0 = 0, pinkB1 = 0, pinkB2 = 0, brown = 0;
        double drive = Math.Clamp(v.Drive, 0, 1);

        for (int i = 0; i < output.Length; i++)
        {
            double t = i / (double)sr;
            double s = 0;

            // Oscillators. Osc 2 runs at a ratio of osc 1's current pitch, so it follows the pitch drop.
            double f1 = Osc1Frequency(v, t);
            if (o2.Enabled)
            {
                double f2 = f1 * Math.Max(0.05, o2.Frequency) * Math.Pow(2, o2.PitchDrop * PitchShape(t, o2.PitchTime, o2.PitchCurve) / 12);
                phase2 += f2 / sr;
                phase2 -= Math.Floor(phase2);
                osc2Out = table2.Read(phase2, Math.Clamp(o2.Position, 0, 1), Wavetable.LevelFor(f2, sr)) * o2.Envelope.At(t);
                s += osc2Out * o2.Level;
            }
            if (o1.Enabled)
            {
                double fm = o2.Enabled ? 1 + Math.Clamp(o2.Fm, 0, 1) * 4 * osc2Out : 1;
                phase1 += f1 * fm / sr;
                phase1 -= Math.Floor(phase1);
                s += table1.Read(phase1, Math.Clamp(o1.Position, 0, 1), Wavetable.LevelFor(f1, sr)) * o1.Envelope.At(t) * o1.Level;
            }

            // Noise.
            if (noise.Enabled && noise.Level > 0)
            {
                double white = rng.NextDouble() * 2 - 1;
                double colored = white;
                if (noise.Color == NoiseColor.Pink)
                {
                    pinkB0 = 0.99765 * pinkB0 + white * 0.0990460;
                    pinkB1 = 0.96300 * pinkB1 + white * 0.2965164;
                    pinkB2 = 0.57000 * pinkB2 + white * 1.0526913;
                    colored = (pinkB0 + pinkB1 + pinkB2 + white * 0.1848) * 0.25;
                }
                else if (noise.Color == NoiseColor.Brown)
                {
                    brown = (brown + white * 0.04) * 0.998;
                    colored = brown * 3;
                }
                double filtered = noise.Filter == FilterMode.Off ? colored : noiseFilter.Process(colored, noise.Cutoff, noise.Resonance, sr, noise.Filter);
                s += filtered * noise.Level * NoiseEnvelope(noise, t) * 1.4;
            }

            // Metal: square waves at inharmonic ratios, band-passed and high-passed.
            if (metal.Enabled && metal.Level > 0)
            {
                double sum = 0;
                for (int k = 0; k < metalVoices; k++)
                {
                    int partial = metalVoices == 2 ? (k == 0 ? 4 : 5) : k; // two voices: the cowbell pair (540 / 800 Hz)
                    metalPhases[k] += MetalPartials[partial] * metalRatio / sr;
                    metalPhases[k] -= Math.Floor(metalPhases[k]);
                    sum += metalPhases[k] < 0.5 ? 1 : -1;
                }
                sum /= metalVoices;
                double x = metalFilter.Process(sum, metal.Cutoff, metal.Resonance, sr, FilterMode.BandPass);
                if (metalVoices > 2) x = metalHighPass.Process(x, metal.Cutoff * 0.6, 0.1, sr, FilterMode.HighPass);
                s += x * metal.Level * 3 * metal.Envelope.At(t);
            }

            // Click.
            if (click.Enabled && click.Level > 0 && t < click.Length * 3)
            {
                double burst = clickFilter.Process(rng.NextDouble() * 2 - 1, click.Tone, 0.2, sr, FilterMode.HighPass);
                double c = burst * Math.Exp(-t / Math.Max(0.0003, click.Length / 3)) + (i == 0 ? 1 : i == 1 ? -0.5 : 0);
                s += c * click.Level * 1.3;
            }

            // Drive.
            if (drive > 0)
            {
                double d = 1 + drive * 9;
                s = v.DriveMode switch
                {
                    DriveMode.Hard => Math.Clamp(s * d, -1, 1),
                    DriveMode.Fold => Math.Sin(s * d * Math.PI / 2),
                    DriveMode.Crush => Crush(s * (1 + drive * 2), drive),
                    _ => Math.Tanh(s * d) / Math.Tanh(d),
                };
            }

            // Filter with its own envelope.
            if (filter.Mode != FilterMode.Off)
            {
                double env = filter.EnvDecay > 0 ? Math.Exp(-6.9 * t / filter.EnvDecay) : 0;
                double cutoff = filter.Cutoff * Math.Pow(2, filter.EnvAmount * env);
                s = filterL.Process(s, cutoff, filter.Resonance, sr, filter.Mode);
            }
            output[i] = (float)s;
        }
    }

    private static double Crush(double s, double amount)
    {
        double steps = Math.Pow(2, 12 - amount * 10);
        return Math.Clamp(Math.Round(s * steps) / steps, -1, 1);
    }

    /// <summary>State-variable filter; the coefficients are computed per sample (cheap enough for drums).</summary>
    private sealed class Svf
    {
        private double _ic1, _ic2;

        public double Process(double x, double cutoff, double resonance, int sr, FilterMode mode)
        {
            double g = Math.Tan(Math.PI * Math.Clamp(cutoff, 20, 0.45 * sr) / sr);
            double k = 2 - 1.9 * Math.Clamp(resonance, 0, 1);
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
}
