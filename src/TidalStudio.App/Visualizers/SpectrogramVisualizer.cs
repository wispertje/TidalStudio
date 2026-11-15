using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TidalStudio.App.Services;
using TidalStudio.Core.Audio;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Scrolling spectrogram (waterfall) of the captured audio: time runs left to right, frequency bottom to top,
/// loudness is colour. Kicks show as low vertical smears, hats as high dots, chords as horizontal lines.
/// </summary>
public sealed class SpectrogramVisualizer : VisualizerBase
{
    private float[] _samples = Array.Empty<float>();
    private float[] _db = Array.Empty<float>();
    private byte[][] _columns = Array.Empty<byte[]>(); // ring buffer: one column of intensities per pixel
    private int _head;                                  // index of the next column to write
    private int _rows;
    private string _layoutKey = "";
    private double _lastTime;
    private double _pending;
    private int[] _pixels = Array.Empty<int>();
    private WriteableBitmap? _bitmap;
    private uint[] _palette = Array.Empty<uint>();
    private string _paletteKey = "";

    public SpectrogramVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "spectrogram";
    public override string Title => "Spectrogram";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Number("speed", "Speed (pixels per second)", 60, 10, 300, 5),
        VisOption.Choice("fft", "FFT size", "2048", "1024", "2048", "4096"),
        VisOption.Number("minDb", "Min dB", -90, -140, -30, 5),
        VisOption.Number("maxDb", "Max dB", -10, -60, 20, 5),
        VisOption.Bool("log", "Log frequency axis", true),
        VisOption.Number("minFreq", "Min frequency (Hz)", 30, 1, 1000, 1),
        VisOption.Number("maxFreq", "Max frequency (Hz)", 16000, 1000, 24000, 100),
        VisOption.Choice("palette", "Colours", "inferno", "inferno", "ocean", "mono"),
        VisOption.Bool("labels", "Frequency labels", true),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var o = Options;
        if (!Services.AudioCapture.IsRunning)
        {
            DrawIdleHint(dc, bounds, Services.AudioCapture.Error is { } err ? $"Audio capture failed: {err}" : "Audio capture is off (View ▸ Audio capture)");
            return;
        }

        var audio = Services.Audio;
        int width = Math.Max(2, (int)bounds.Width), height = Math.Max(2, (int)bounds.Height);
        int size = int.TryParse(o.Choice("fft"), out int s) ? s : 2048;
        double minF = o.Number("minFreq"), maxF = Math.Min(Math.Max(o.Number("maxFreq"), minF * 2), audio.SampleRate / 2.0);
        bool log = o.Bool("log");

        // A new size or frequency layout starts a fresh history.
        string key = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}/{size}/{minF}/{maxF}/{log}");
        if (key != _layoutKey)
        {
            _layoutKey = key;
            _rows = height;
            _columns = new byte[width][];
            for (int i = 0; i < width; i++) _columns[i] = new byte[height];
            _head = 0;
            _pixels = new int[width * height];
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _lastTime = now;
        }
        if (_samples.Length != size)
        {
            _samples = new float[size];
            _db = new float[size / 2];
        }

        // Advance by as many columns as time has passed.
        _pending += Math.Clamp(now - _lastTime, 0, 1) * o.Number("speed");
        _lastTime = now;
        int add = Math.Min((int)_pending, width);
        _pending -= (int)_pending;
        if (add > 0)
        {
            audio.ReadLatest(_samples);
            Fft.MagnitudeDb(_samples, _db, -140f);
            double minDb = o.Number("minDb"), maxDb = Math.Max(o.Number("maxDb"), minDb + 10);
            var column = new byte[_rows];
            for (int row = 0; row < _rows; row++)
            {
                // Row 0 is the top (highest frequency).
                double f0 = FreqAt(_rows - row - 1, _rows, minF, maxF, log), f1 = FreqAt(_rows - row, _rows, minF, maxF, log);
                int b0 = Math.Clamp((int)Math.Floor(f0 * size / audio.SampleRate), 1, _db.Length - 1);
                int b1 = Math.Clamp((int)Math.Ceiling(f1 * size / audio.SampleRate), b0 + 1, _db.Length);
                float best = -140;
                for (int b = b0; b < b1; b++) best = Math.Max(best, _db[b]);
                column[row] = (byte)(Math.Clamp((best - minDb) / (maxDb - minDb), 0, 1) * 255);
            }
            for (int i = 0; i < add; i++)
            {
                Array.Copy(column, _columns[_head], _rows);
                _head = (_head + 1) % width;
            }
        }

        // Paint the history: oldest column on the left.
        var palette = Palette(o.Choice("palette"));
        for (int x = 0; x < width; x++)
        {
            var col = _columns[(_head + x) % width];
            for (int y = 0; y < _rows; y++) _pixels[y * width + x] = (int)palette[col[y]];
        }
        using (var fb = _bitmap!.Lock())
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(_pixels, y * width, fb.Address + y * fb.RowBytes, width);
        }
        dc.DrawImage(_bitmap, new Rect(0, 0, width, height), new Rect(0, 0, width, height));

        if (o.Bool("labels"))
        {
            foreach (double f in new double[] { 100, 1000, 10000 })
            {
                if (f < minF || f > maxF) continue;
                double y = height - (log ? Math.Log(f / minF) / Math.Log(maxF / minF) : (f - minF) / (maxF - minF)) * height;
                dc.DrawLine(Pen(Color.FromArgb(40, 255, 255, 255), 1), new Point(0, y), new Point(14, y));
                DrawText(dc, f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", new Point(16, y - 7), 9, LabelBrush);
            }
        }
    }

    private static double FreqAt(int row, int rows, double minF, double maxF, bool log) =>
        log ? minF * Math.Pow(maxF / minF, row / (double)rows) : minF + (maxF - minF) * row / rows;

    /// <summary>256 premultiplied BGRA colours for intensities 0..255.</summary>
    private uint[] Palette(string name)
    {
        string key = name + (Transparent ? "/t" : "");
        if (key == _paletteKey) return _palette;
        Color[] stops = name switch
        {
            "ocean" => new[] { Color.Parse("#12141A"), Color.Parse("#0B3954"), Color.Parse("#087E8B"), Color.Parse("#4ECDC4"), Color.Parse("#E6FFFB") },
            "mono" => new[] { Color.Parse("#12141A"), Color.Parse("#5C6475"), Color.Parse("#FFFFFF") },
            _ => new[] { Color.Parse("#12141A"), Color.Parse("#3B0F70"), Color.Parse("#8C2981"), Color.Parse("#DE4968"), Color.Parse("#FE9F6D"), Color.Parse("#FCFDBF") },
        };
        var palette = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0 * (stops.Length - 1);
            int k = Math.Min((int)t, stops.Length - 2);
            var c = Mix(stops[k], stops[k + 1], t - k);
            // Behind code: quiet parts are transparent so the text stays readable.
            double a = Transparent ? Math.Clamp(i / 255.0 * 1.4, 0, 1) : 1;
            palette[i] = ((uint)(a * 255) << 24) | ((uint)(c.R * a) << 16) | ((uint)(c.G * a) << 8) | (uint)(c.B * a);
        }
        _palette = palette;
        _paletteKey = key;
        return palette;
    }
}
