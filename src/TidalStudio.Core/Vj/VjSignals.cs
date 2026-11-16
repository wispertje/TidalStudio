using TidalStudio.Core.Audio;
using TidalStudio.Core.Events;

namespace TidalStudio.Core.Vj;

/// <summary>What drives the visuals.</summary>
public enum VjSource
{
    /// <summary>Audio when something is heard, Dirt events always add hits (the default).</summary>
    Auto,
    /// <summary>Only the captured audio.</summary>
    Audio,
    /// <summary>Only Tidal's events (works without audio capture, e.g. when SuperCollider plays to another device).</summary>
    Events,
}

/// <summary>What kind of drum or sound an event is, from its sound name.</summary>
public enum HitKind { Kick, Snare, Hat, Bass, Tone }

/// <summary>
/// Turns audio (bands and onsets) and Tidal events (kick / snare / hat hits) into smooth 0..1 control values
/// for the visuals. Updated once per frame on the UI thread.
/// </summary>
public sealed class VjSignals
{
    public const int FftSize = 2048;
    public const int SpectrumBands = 64;

    // Outputs, 0..1
    public double Level { get; private set; }
    public double Bass { get; private set; }
    public double Mid { get; private set; }
    public double High { get; private set; }
    /// <summary>A pulse at every beat (kick or bass onset), decaying.</summary>
    public double Beat { get; private set; }
    public double Kick { get; private set; }
    public double Snare { get; private set; }
    public double Hat { get; private set; }
    /// <summary>Number of beats so far.</summary>
    public long Beats { get; private set; }
    /// <summary>True when the audio has signal (above about -54 dB).</summary>
    public bool AudioActive { get; private set; }
    /// <summary>Log-spaced spectrum, 0..1 per band (for the spectrum scenes).</summary>
    public float[] Spectrum { get; } = new float[SpectrumBands];
    /// <summary>The latest waveform, 256 points.</summary>
    public float[] Wave { get; } = new float[256];

    private readonly float[] _samples = new float[FftSize];
    private readonly float[] _db = new float[FftSize / 2];
    private double _peakBass = 1e-3, _peakMid = 1e-3, _peakHigh = 1e-3, _peakLevel = 1e-3;
    private double _bassAvg;
    private double _sinceBeat = 10;
    // Envelopes from events
    private double _eBass, _eMid, _eHigh, _eLevel;

    public static HitKind Classify(string? sound, double? midi)
    {
        // Drum kits name sounds kit + abbreviation ("mykitbd", "808sd"), Dirt-Samples abbreviation + number ("hh27").
        string s = (sound ?? "").ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        bool Ends(params string[] a) => a.Any(x => s.EndsWith(x, StringComparison.Ordinal) || s.StartsWith(x, StringComparison.Ordinal));
        bool Has(params string[] a) => a.Any(x => s.Contains(x, StringComparison.Ordinal));
        if (Has("kick", "kik") || Ends("bd")) return HitKind.Kick;
        if (Has("snare", "clap") || Ends("sd", "sn", "cp", "rim")) return HitKind.Snare;
        if (Has("hat", "shaker", "tamb", "ride", "crash", "cymbal") || Ends("hh", "oh", "cb", "perc") || s.EndsWith("sh", StringComparison.Ordinal))
            return HitKind.Hat;
        if (Has("bass", "sub") || (midi is { } m && m < 52)) return HitKind.Bass;
        return HitKind.Tone;
    }

    /// <summary>An event starts sounding.</summary>
    public void OnEvent(TidalEvent e, VjSource source)
    {
        if (source == VjSource.Audio) return;
        double g = Math.Clamp(e.Gain, 0, 1.5);
        switch (Classify(e.Sound, e.Midi()))
        {
            case HitKind.Kick:
                Kick = Math.Max(Kick, g);
                _eBass = Math.Max(_eBass, g);
                TriggerBeat();
                break;
            case HitKind.Snare:
                Snare = Math.Max(Snare, g);
                _eMid = Math.Max(_eMid, g * 0.9);
                break;
            case HitKind.Hat:
                Hat = Math.Max(Hat, g * 0.8);
                _eHigh = Math.Max(_eHigh, g * 0.8);
                break;
            case HitKind.Bass:
                _eBass = Math.Max(_eBass, g * 0.7);
                break;
            default:
                _eMid = Math.Max(_eMid, g * 0.6);
                _eHigh = Math.Max(_eHigh, g * 0.3);
                break;
        }
        _eLevel = Math.Max(_eLevel, g * 0.8);
    }

    private void TriggerBeat()
    {
        if (_sinceBeat < 0.09) return; // the same hit from audio and from the event
        Beat = 1;
        Beats++;
        _sinceBeat = 0;
    }

    /// <summary>Advance by <paramref name="dt"/> seconds, analysing the latest audio when there is some.</summary>
    public void Update(double dt, AudioRingBuffer? audio, VjSource source)
    {
        dt = Math.Clamp(dt, 0.001, 0.25);
        _sinceBeat += dt;
        double fast = Math.Exp(-dt / 0.12), pulse = Math.Exp(-dt / 0.2), slow = Math.Exp(-dt / 0.35);
        Beat *= pulse;
        Kick *= pulse;
        Snare *= pulse;
        Hat *= Math.Exp(-dt / 0.1);
        _eBass *= slow;
        _eMid *= slow;
        _eHigh *= fast;
        _eLevel *= slow;

        double aBass = 0, aMid = 0, aHigh = 0, aLevel = 0;
        AudioActive = false;
        if (audio is not null && source != VjSource.Events)
        {
            audio.ReadLatest(_samples);
            Analyse(audio.SampleRate, dt, out aBass, out aMid, out aHigh, out aLevel);
        }
        else Array.Clear(Wave);

        double eWeight = source == VjSource.Audio ? 0 : AudioActive ? 0.5 : 1;
        Bass = Smooth(Bass, Math.Max(aBass, _eBass * eWeight), dt);
        Mid = Smooth(Mid, Math.Max(aMid, _eMid * eWeight), dt);
        High = Smooth(High, Math.Max(aHigh, _eHigh * eWeight), dt);
        Level = Smooth(Level, Math.Max(aLevel, _eLevel * eWeight), dt);
        if (!AudioActive && source != VjSource.Audio)
            for (int i = 0; i < Spectrum.Length; i++)
            {
                // Without audio, a spectrum shaped by the event envelopes keeps the spectrum scenes alive.
                double x = i / (double)(Spectrum.Length - 1);
                double target = _eBass * Math.Exp(-x * 8) + _eMid * Math.Exp(-Math.Pow((x - 0.45) * 5, 2)) + _eHigh * Math.Exp(-Math.Pow((x - 0.8) * 5, 2));
                Spectrum[i] = (float)Math.Max(target, Spectrum[i] * Math.Exp(-dt / 0.15));
            }
    }

    private static double Smooth(double current, double target, double dt) =>
        target > current ? current + (target - current) * (1 - Math.Exp(-dt / 0.025)) : current + (target - current) * (1 - Math.Exp(-dt / 0.18));

    private void Analyse(int sampleRate, double dt, out double bass, out double mid, out double high, out double level)
    {
        double sum = 0;
        for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
        double rms = Math.Sqrt(sum / _samples.Length);
        int step = _samples.Length / Wave.Length;
        for (int i = 0; i < Wave.Length; i++) Wave[i] = _samples[_samples.Length - Wave.Length * step + i * step];

        bass = mid = high = level = 0;
        if (rms < 0.002)
        {
            for (int i = 0; i < Spectrum.Length; i++) Spectrum[i] *= (float)Math.Exp(-dt / 0.15);
            return;
        }
        AudioActive = true;
        Fft.MagnitudeDb(_samples, _db);
        double binHz = sampleRate / (double)FftSize;
        double Band(double lo, double hi)
        {
            int a = Math.Max(1, (int)(lo / binHz)), b = Math.Min(_db.Length - 1, (int)(hi / binHz));
            double s = 0;
            for (int k = a; k <= b; k++) { double m = Math.Pow(10, _db[k] / 20); s += m * m; }
            return Math.Sqrt(s / Math.Max(1, b - a + 1));
        }
        double rawBass = Band(30, 150), rawMid = Band(150, 2000), rawHigh = Band(2000, 12000);

        // Adaptive scaling: each band relative to its recent peak (slowly forgetting), so quiet and loud mixes both move.
        double forget = Math.Exp(-dt / 6);
        _peakBass = Math.Max(rawBass, Math.Max(_peakBass * forget, 1e-4));
        _peakMid = Math.Max(rawMid, Math.Max(_peakMid * forget, 1e-4));
        _peakHigh = Math.Max(rawHigh, Math.Max(_peakHigh * forget, 1e-4));
        _peakLevel = Math.Max(rms, Math.Max(_peakLevel * forget, 1e-3));
        bass = Math.Pow(rawBass / _peakBass, 1.5);
        mid = rawMid / _peakMid;
        high = rawHigh / _peakHigh;
        level = rms / _peakLevel;

        // Onset: bass well above its recent average.
        if (rawBass > _bassAvg * 1.45 && bass > 0.55 && _sinceBeat > 0.18) TriggerBeat();
        _bassAvg += (rawBass - _bassAvg) * (1 - Math.Exp(-dt / 0.25));

        // Log-spaced spectrum 30 Hz .. 16 kHz in dB, -70..-10 dB mapped to 0..1.
        for (int i = 0; i < Spectrum.Length; i++)
        {
            double lo = 30 * Math.Pow(16000 / 30.0, i / (double)Spectrum.Length);
            double hi = 30 * Math.Pow(16000 / 30.0, (i + 1) / (double)Spectrum.Length);
            int a = Math.Max(1, (int)(lo / binHz)), b = Math.Max(a, Math.Min(_db.Length - 1, (int)(hi / binHz)));
            float max = -120;
            for (int k = a; k <= b; k++) max = Math.Max(max, _db[k]);
            float v = (float)Math.Clamp((max + 70) / 60, 0, 1);
            Spectrum[i] = v > Spectrum[i] ? v : (float)(Spectrum[i] + (v - Spectrum[i]) * (1 - Math.Exp(-dt / 0.12)));
        }
    }
}
