using System.Collections.Concurrent;
using System.Numerics;
using TidalStudio.Core.Audio;

namespace TidalStudio.Core.Synth;

/// <summary>
/// A wavetable: <see cref="FrameCount"/> single-cycle waveforms that the oscillator's Position morphs between.
/// Each frame is stored at several band limits (one per octave) so high notes don't alias.
/// </summary>
public sealed class Wavetable
{
    public const int Size = 2048;
    public const int Levels = 11; // level L keeps harmonics up to 1024 >> L

    // [frame][level][sample]
    private readonly float[][][] _data;

    public Wavetable(string name, string description, float[][][] data)
    {
        Name = name;
        Description = description;
        _data = data;
    }

    public string Name { get; }
    public string Description { get; }
    public int FrameCount => _data.Length;

    /// <summary>The full-bandwidth waveform of a frame (for drawing).</summary>
    public float[] Frame(int frame) => _data[Math.Clamp(frame, 0, _data.Length - 1)][0];

    /// <summary>Band-limit level for a fundamental: few enough harmonics to stay below Nyquist.</summary>
    public static int LevelFor(double frequency, double sampleRate)
    {
        double maxHarmonic = 0.45 * sampleRate / Math.Max(frequency, 1);
        if (maxHarmonic >= Size / 2) return 0;
        int level = (int)Math.Ceiling(Math.Log2(Size / 2 / Math.Max(maxHarmonic, 1)));
        return Math.Clamp(level, 0, Levels - 1);
    }

    /// <summary>Reads the table: <paramref name="phase"/> 0..1, <paramref name="position"/> 0..1 across the frames.</summary>
    public float Read(double phase, double position, int level)
    {
        double f = Math.Clamp(position, 0, 1) * (_data.Length - 1);
        int f0 = (int)f;
        int f1 = Math.Min(f0 + 1, _data.Length - 1);
        double ft = f - f0;
        double p = (phase - Math.Floor(phase)) * Size;
        int i0 = (int)p & (Size - 1);
        int i1 = (i0 + 1) & (Size - 1);
        double pt = p - Math.Floor(p);
        var a = _data[f0][level];
        var b = _data[f1][level];
        double va = a[i0] + (a[i1] - a[i0]) * pt;
        double vb = b[i0] + (b[i1] - b[i0]) * pt;
        return (float)(va + (vb - va) * ft);
    }

    /// <summary>
    /// Builds a table from a function (frame 0..1, phase 0..1) → sample. Each frame is analysed with an FFT and
    /// resynthesised per octave with only the harmonics that fit, then normalised to a peak of 1.
    /// </summary>
    public static Wavetable Build(string name, string description, int frames, Func<double, double, double> shape)
    {
        var data = new float[frames][][];
        var buffer = new Complex[Size];
        for (int fr = 0; fr < frames; fr++)
        {
            double framePos = frames == 1 ? 0 : fr / (double)(frames - 1);
            for (int i = 0; i < Size; i++) buffer[i] = new Complex(shape(framePos, i / (double)Size), 0);
            Fft.Transform(buffer);
            var spectrum = (Complex[])buffer.Clone();
            spectrum[0] = Complex.Zero; // no DC offset

            data[fr] = new float[Levels][];
            for (int level = 0; level < Levels; level++)
            {
                int keep = (Size / 2) >> level;
                var bins = new Complex[Size];
                for (int k = 1; k < Size / 2 && k <= keep; k++)
                {
                    bins[k] = spectrum[k];
                    bins[Size - k] = spectrum[Size - k];
                }
                data[fr][level] = Inverse(bins);
            }
            // Same gain for every level so the sound doesn't jump in volume between octaves.
            float peak = data[fr][0].Max(Math.Abs);
            if (peak > 1e-6f)
                foreach (var level in data[fr])
                    for (int i = 0; i < Size; i++) level[i] /= peak;
        }
        return new Wavetable(name, description, data);
    }

    private static float[] Inverse(Complex[] bins)
    {
        // ifft(x) = conj(fft(conj(x))) / N
        for (int i = 0; i < bins.Length; i++) bins[i] = Complex.Conjugate(bins[i]);
        Fft.Transform(bins);
        var result = new float[bins.Length];
        for (int i = 0; i < bins.Length; i++) result[i] = (float)(bins[i].Real / bins.Length);
        return result;
    }
}

/// <summary>The built-in wavetables. Each is generated once, on first use.</summary>
public static class Wavetables
{
    private static readonly ConcurrentDictionary<string, Lazy<Wavetable>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<(string Name, string Description)> Available = new[]
    {
        ("Basic", "sine → triangle → saw → square"),
        ("PWM", "pulse, wide to narrow"),
        ("Harmonics", "organ-like, adding harmonics"),
        ("Formant", "vowels a → e → i → o → u"),
        ("Sync", "hard-sync sweep"),
        ("Digital", "bright, metallic spectra"),
        ("Fold", "sine through a wavefolder"),
        ("Noise", "white noise; position makes it darker"),
    };

    public static Wavetable Get(string name)
    {
        if (!Available.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = "Basic";
        return Cache.GetOrAdd(name, n => new Lazy<Wavetable>(() => Create(n))).Value;
    }

    private const int Frames = 32;

    private static Wavetable Create(string name)
    {
        string description = Available.First(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Description;
        return name.ToLowerInvariant() switch
        {
            "pwm" => Wavetable.Build("PWM", description, Frames, (f, p) => p < 0.5 - 0.45 * f ? 1 : -1),
            "harmonics" => Wavetable.Build("Harmonics", description, Frames, (f, p) =>
            {
                // Drawbar-like: each frame fades in the next harmonic.
                double sum = 0, count = 1 + f * 15;
                for (int h = 1; h <= 16; h++)
                {
                    double w = Math.Clamp(count - (h - 1), 0, 1) / Math.Sqrt(h);
                    if (w > 0) sum += w * Math.Sin(2 * Math.PI * h * p);
                }
                return sum;
            }),
            "formant" => Wavetable.Build("Formant", description, Frames, Formant),
            "sync" => Wavetable.Build("Sync", description, Frames, (f, p) =>
            {
                // A saw restarted every cycle of the fundamental, running 1x..8x as fast.
                double ratio = 1 + f * 7;
                double slave = p * ratio % 1;
                return (2 * slave - 1) * (1 - 0.3 * p);
            }),
            "digital" => Wavetable.Build("Digital", description, Frames, Digital),
            "fold" => Wavetable.Build("Fold", description, Frames, (f, p) =>
            {
                double x = Math.Sin(2 * Math.PI * p) * (1 + f * 6);
                return Math.Sin(x * Math.PI / 2);
            }),
            "noise" => Wavetable.Build("Noise", description, Frames, NoiseShape()),
            _ => Wavetable.Build("Basic", description, Frames, Basic),
        };
    }

    private static double Basic(double f, double p)
    {
        double sine = Math.Sin(2 * Math.PI * p);
        double tri = p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4;
        double saw = p < 0.5 ? 2 * p : 2 * p - 2;
        double square = p < 0.5 ? 1 : -1;
        double[] shapes = { sine, tri, saw, square };
        double x = f * 3;
        int i = Math.Min((int)x, 2);
        double t = x - i;
        return shapes[i] * (1 - t) + shapes[i + 1] * t;
    }

    // Formant frequencies (Hz) for a, e, i, o, u; the table assumes a ~110 Hz fundamental.
    private static readonly double[][] Vowels =
    {
        new[] { 730.0, 1090, 2440 }, new[] { 530.0, 1840, 2480 }, new[] { 270.0, 2290, 3010 },
        new[] { 570.0, 840, 2410 }, new[] { 300.0, 870, 2240 },
    };

    private static double Formant(double f, double p)
    {
        double x = f * (Vowels.Length - 1);
        int i = Math.Min((int)x, Vowels.Length - 2);
        double t = x - i;
        double sum = 0;
        for (int h = 1; h <= 40; h++)
        {
            double freq = h * 110;
            double amp = 0;
            for (int k = 0; k < 3; k++)
            {
                double centre = Vowels[i][k] * (1 - t) + Vowels[i + 1][k] * t;
                double width = 90 + 40 * k;
                amp += Math.Exp(-Math.Pow((freq - centre) / width, 2)) / (k + 1);
            }
            sum += (amp + 0.02 / h) * Math.Sin(2 * Math.PI * h * p);
        }
        return sum;
    }

    private static double Digital(double f, double p)
    {
        // Sparse, bright harmonic sets that shift as the position moves: bell- and FM-like.
        double sum = Math.Sin(2 * Math.PI * p);
        int[] partials = { 3, 5, 7, 9, 11, 14, 17, 21, 26, 31 };
        for (int k = 0; k < partials.Length; k++)
        {
            double shift = Math.Sin((f * 4 + k * 0.7) * Math.PI);
            double amp = Math.Max(0, shift) * 0.6 / (1 + k * 0.25);
            int h = partials[k] + (int)Math.Round(f * k);
            sum += amp * Math.Sin(2 * Math.PI * h * p + k);
        }
        return sum;
    }

    private static Func<double, double, double> NoiseShape()
    {
        // Fixed random cycle, smoothed more and more across the frames (white-ish to soft).
        var random = new Random(7);
        var raw = Enumerable.Range(0, Wavetable.Size).Select(_ => random.NextDouble() * 2 - 1).ToArray();
        return (f, p) =>
        {
            int width = 1 + (int)(f * f * 40);
            int centre = (int)(p * Wavetable.Size);
            double sum = 0;
            for (int i = -width; i <= width; i++) sum += raw[(centre + i + Wavetable.Size) % Wavetable.Size];
            return sum / (2 * width + 1);
        };
    }
}
