namespace TidalStudio.Core.Audio;

/// <summary>Fixed-size mono sample history. Written by the capture thread, read by visualizers.</summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private long _written;

    public AudioRingBuffer(int capacity = 1 << 15)
    {
        _buffer = new float[capacity];
    }

    public int Capacity => _buffer.Length;

    public int SampleRate { get; set; } = 48000;

    /// <summary>Total samples written since creation.</summary>
    public long TotalWritten
    {
        get { lock (_lock) return _written; }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (float s in samples)
            {
                _buffer[(int)(_written % _buffer.Length)] = s;
                _written++;
            }
        }
    }

    /// <summary>Copies the most recent <c>destination.Length</c> samples (oldest first). Missing history is zero.</summary>
    public void ReadLatest(Span<float> destination)
    {
        lock (_lock)
        {
            int n = destination.Length;
            long start = _written - n;
            for (int i = 0; i < n; i++)
            {
                long idx = start + i;
                destination[i] = idx < 0 || idx < _written - _buffer.Length ? 0 : _buffer[(int)(idx % _buffer.Length)];
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _written = 0;
        }
    }
}
