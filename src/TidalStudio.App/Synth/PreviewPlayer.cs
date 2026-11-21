using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TidalStudio.Core.Synth;

// WasapiOut may be marked obsolete in NAudio 3 in favour of a builder API; it is the simple, tested path here.
#pragma warning disable CS0618
#pragma warning disable CA1416 // only used on Windows (see Start)

namespace TidalStudio.App.Synth;

/// <summary>
/// Plays rendered notes once through the default output device; a new note interrupts the previous one (crossfaded).
/// Looping sequences play on the shared <see cref="SequencerTransport"/> instead, so they stay in time with each other.
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
        if (!OperatingSystem.IsWindows())
        {
            Error = "Preview playback is only available on Windows; saved samples still work everywhere.";
            return;
        }
        try
        {
            if (_output is null || _sampleRate != note.SampleRate)
            {
                Dispose();
                _sampleRate = note.SampleRate;
                _source = new Source(note.SampleRate);
                _output = new WasapiOut(AudioClientShareMode.Shared, 60);
                _output.Init(_source);
                _output.Play();
            }
            _source!.Set(note);
            Error = null;
        }
        catch (Exception e)
        {
            Error = $"Could not play the preview: {e.Message}";
            Dispose();
        }
    }

    public void Stop() => _source?.Set(null);

    public void Dispose()
    {
        try { _output?.Stop(); } catch (Exception) { }
        _output?.Dispose();
        _output = null;
        _source = null;
    }

    /// <summary>Stereo float source that plays one sample once, crossfading from the previous one when it is replaced.</summary>
    private sealed class Source : IWaveProvider
    {
        private const int FadeFrames = 512;

        private readonly object _lock = new();
        private RenderedNote? _note, _old;
        private int _pos, _oldPos, _fade;

        public Source(int sampleRate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

        public WaveFormat WaveFormat { get; }

        public void Set(RenderedNote? note)
        {
            lock (_lock)
            {
                if (_note is not null && _pos < _note.Left.Length) { _old = _note; _oldPos = _pos; _fade = FadeFrames; }
                _note = note;
                _pos = 0;
            }
        }

        public int Read(Span<byte> buffer)
        {
            int usable = buffer.Length - buffer.Length % 8;
            var output = MemoryMarshal.Cast<byte, float>(buffer[..usable]);
            lock (_lock)
            {
                for (int i = 0; i < output.Length / 2; i++)
                {
                    float l = 0, r = 0;
                    if (_note is { } n && _pos < n.Left.Length) { l = n.Left[_pos]; r = n.Right[_pos]; _pos++; }
                    if (_fade > 0 && _old is { } o)
                    {
                        float g = _fade / (float)FadeFrames;
                        if (_oldPos < o.Left.Length) { l += o.Left[_oldPos] * g; r += o.Right[_oldPos] * g; _oldPos++; }
                        if (--_fade == 0) _old = null;
                    }
                    output[i * 2] = l;
                    output[i * 2 + 1] = r;
                }
            }
            buffer[usable..].Clear();
            return buffer.Length;
        }
    }
}
