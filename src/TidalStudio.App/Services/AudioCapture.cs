using NAudio.Wave;
using TidalStudio.Core.Audio;

// WasapiLoopbackCapture is marked obsolete in NAudio 3 in favour of WasapiRecorderBuilder; it still works and is
// the tested path here. Switch when the builder API is needed (e.g. capturing only scsynth's process).
#pragma warning disable CS0618
// The capture object is only created when OperatingSystem.IsWindows() (see Start), so the other calls are safe.
#pragma warning disable CA1416

namespace TidalStudio.App.Services;

/// <summary>
/// Captures what Windows is playing (WASAPI loopback), which includes SuperCollider's output,
/// and writes a mono mix into the ring buffer used by the scope and spectrum.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly AudioRingBuffer _buffer;
    private WasapiLoopbackCapture? _capture;
    private float[] _mono = new float[4096];

    public AudioCapture(AudioRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public bool IsRunning => _capture is not null;
    public string? Error { get; private set; }
    public string Description { get; private set; } = "off";

    public void Start()
    {
        if (_capture is not null || !OperatingSystem.IsWindows()) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Error = e.Exception.Message;
            };
            _buffer.SampleRate = capture.WaveFormat.SampleRate;
            capture.StartRecording();
            _capture = capture;
            Error = null;
            Description = $"{capture.WaveFormat.SampleRate / 1000.0:0.#} kHz loopback";
        }
        catch (Exception e)
        {
            Error = e.Message;
            Description = "unavailable";
            _capture = null;
        }
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.StopRecording();
        }
        catch (Exception) { }
        capture.Dispose();
        Description = "off";
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || e.BytesRecorded == 0) return;
        var format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        if (_mono.Length < frames) _mono = new float[frames];

        var data = e.Buffer.AsSpan(0, e.BytesRecorded);
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32 && IsFloatExtensible(format));

        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int offset = (f * channels + c) * bytesPerSample;
                sum += ReadSample(data, offset, bytesPerSample, isFloat);
            }
            _mono[f] = sum / channels;
        }
        _buffer.Write(_mono.AsSpan(0, frames));
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private static bool IsFloatExtensible(WaveFormat format) =>
        format is WaveFormatExtensible ext
            ? ext.SubFormat == IeeeFloatSubFormat
            : true; // shared-mode loopback is almost always 32-bit float

    private static float ReadSample(Span<byte> data, int offset, int bytes, bool isFloat)
    {
        switch (bytes)
        {
            case 4 when isFloat: return BitConverter.ToSingle(data.Slice(offset, 4));
            case 4: return BitConverter.ToInt32(data.Slice(offset, 4)) / 2147483648f;
            case 3: return ((data[offset] << 8) | (data[offset + 1] << 16) | (data[offset + 2] << 24)) / 2147483648f;
            case 2: return BitConverter.ToInt16(data.Slice(offset, 2)) / 32768f;
            default: return 0;
        }
    }

    public void Dispose() => Stop();
}
