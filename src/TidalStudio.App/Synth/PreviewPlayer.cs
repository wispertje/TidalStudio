using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TidalStudio.Core.Synth;

// WasapiOut may be marked obsolete in NAudio 3 in favour of a builder API; it is the simple, tested path here.
#pragma warning disable CS0618
#pragma warning disable CA1416 // only used on Windows (see Start)

namespace TidalStudio.App.Synth;

/// <summary>
/// Plays rendered audio through the default output device. <see cref="Play"/> plays a one-shot (a new note interrupts the
/// previous one); <see cref="PlayLoop"/> loops a sample until stopped and can swap in a new render without losing its place,
/// so sound changes are heard while the loop keeps playing.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    /// <summary>Roughly how far the device plays behind what it has read (the WASAPI latency below).</summary>
    public const double LatencySeconds = 0.06;

    private WasapiOut? _output;
    private Source? _source;
    private int _sampleRate;

    public string? Error { get; private set; }

    /// <summary>Plays <paramref name="note"/> once from the start.</summary>
    public void Play(RenderedNote note)
    {
        if (Start(note.SampleRate)) _source!.Set(note, loop: false, keepPosition: false);
    }

    /// <summary>
    /// Loops <paramref name="loop"/> until <see cref="Stop"/>. With <paramref name="keepPosition"/> and a loop already playing,
    /// the new sample continues from the same place (crossfaded), so tweaks are heard in time.
    /// </summary>
    public void PlayLoop(RenderedNote loop, bool keepPosition)
    {
        bool sameDevice = _output is not null && _sampleRate == loop.SampleRate;
        if (Start(loop.SampleRate)) _source!.Set(loop, loop: true, keepPosition: keepPosition && sameDevice);
    }

    /// <summary>Seconds into the playing loop (or one-shot) that are being heard now, or null when nothing plays.</summary>
    public double? PositionSeconds => _source?.Position(LatencySeconds);

    public void Stop() => _source?.Clear();

    private bool Start(int sampleRate)
    {
        if (!OperatingSystem.IsWindows())
        {
            Error = "Preview playback is only available on Windows; saved samples still work everywhere.";
            return false;
        }
        try
        {
            if (_output is null || _sampleRate != sampleRate)
            {
                Dispose();
                _sampleRate = sampleRate;
                _source = new Source(sampleRate);
                _output = new WasapiOut(AudioClientShareMode.Shared, 60);
                _output.Init(_source);
                _output.Play();
            }
            Error = null;
            return true;
        }
        catch (Exception e)
        {
            Error = $"Could not play the preview: {e.Message}";
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        try { _output?.Stop(); } catch (Exception) { }
        _output?.Dispose();
        _output = null;
        _source = null;
    }

    /// <summary>Stereo float source that plays one sample (once or looped) and crossfades when the sample is replaced.</summary>
    private sealed class Source : IWaveProvider
    {
        private const int FadeFrames = 512;

        private readonly object _lock = new();
        private readonly int _sampleRate;
        private float[]? _l, _r;
        private long _pos;
        private bool _loop;
        // The sample being faded out after a swap.
        private float[]? _oldL, _oldR;
        private long _oldPos;
        private int _fade;

        public Source(int sampleRate)
        {
            _sampleRate = sampleRate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public void Set(RenderedNote note, bool loop, bool keepPosition)
        {
            lock (_lock)
            {
                bool continuing = keepPosition && _loop && _l is { Length: > 0 };
                if (continuing)
                {
                    _oldL = _l; _oldR = _r; _oldPos = _pos; _fade = FadeFrames;
                    _pos = note.Left.Length > 0 ? _pos % note.Left.Length : 0;
                }
                else
                {
                    _oldL = _oldR = null; _fade = 0;
                    _pos = 0;
                }
                _l = note.Left;
                _r = note.Right;
                _loop = loop;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _l = _r = _oldL = _oldR = null;
                _fade = 0;
                _pos = 0;
                _loop = false;
            }
        }

        public double? Position(double latency)
        {
            lock (_lock)
            {
                if (_l is not { Length: > 0 } l) return null;
                double t = _pos / (double)_sampleRate - latency;
                double length = l.Length / (double)_sampleRate;
                if (_loop) return ((t % length) + length) % length;
                return _pos >= l.Length ? null : Math.Max(0, t);
            }
        }

        public int Read(Span<byte> buffer)
        {
            int count = buffer.Length;
            var output = MemoryMarshal.Cast<byte, float>(buffer[..(count - count % 4)]);
            int frames = output.Length / 2;
            lock (_lock)
            {
                for (int i = 0; i < frames; i++)
                {
                    float left = 0, right = 0;
                    if (_l is { Length: > 0 } l && _r is { } r)
                    {
                        if (_pos >= l.Length && _loop) _pos = 0;
                        if (_pos < l.Length)
                        {
                            left = l[_pos];
                            right = r[_pos];
                            _pos++;
                        }
                        else if (!_loop) _pos = l.Length; // finished one-shot: silence
                    }
                    if (_fade > 0 && _oldL is { Length: > 0 } oldL && _oldR is { } oldR)
                    {
                        if (_oldPos >= oldL.Length) _oldPos = 0;
                        float g = _fade / (float)FadeFrames;
                        left = left * (1 - g) + oldL[_oldPos] * g;
                        right = right * (1 - g) + oldR[_oldPos] * g;
                        _oldPos++;
                        if (--_fade == 0) _oldL = _oldR = null;
                    }
                    output[i * 2] = left;
                    output[i * 2 + 1] = right;
                }
            }
            for (int i = frames * 2; i < output.Length; i++) output[i] = 0;
            buffer[(output.Length * 4)..].Clear();
            return count; // always a full buffer, so the device keeps running
        }
    }
}
