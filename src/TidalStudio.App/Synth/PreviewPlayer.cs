using NAudio.CoreAudioApi;
using NAudio.Wave;
using TidalStudio.Core.Synth;

// WasapiOut may be marked obsolete in NAudio 3 in favour of a builder API; it is the simple, tested path here.
#pragma warning disable CS0618
#pragma warning disable CA1416 // only used on Windows (see Play)

namespace TidalStudio.App.Synth;

/// <summary>Plays rendered notes through the default output device. A new note interrupts the previous one.</summary>
public sealed class PreviewPlayer : IDisposable
{
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private int _sampleRate;

    public string? Error { get; private set; }

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
                // Room for the longest sample.
                _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(note.SampleRate, 2), TimeSpan.FromSeconds(40))
                {
                    DiscardOnBufferOverflow = true,
                };
                _output = new WasapiOut(AudioClientShareMode.Shared, 60);
                _output.Init(_buffer);
                _output.Play();
            }
            _buffer!.ClearBuffer();
            var bytes = new byte[note.Left.Length * 8];
            for (int i = 0; i < note.Left.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8, 4), note.Left[i]);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4, 4), note.Right[i]);
            }
            _buffer.AddSamples(bytes, 0, bytes.Length);
            Error = null;
        }
        catch (Exception e)
        {
            Error = $"Could not play the preview: {e.Message}";
            Dispose();
        }
    }

    public void Stop() => _buffer?.ClearBuffer();

    public void Dispose()
    {
        try { _output?.Stop(); } catch (Exception) { }
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }
}
