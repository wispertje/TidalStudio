using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TidalStudio.App.Services;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Visualizers;

/// <summary>Base class for all visualizers: shared services, options, per-frame redraw and colour helpers.</summary>
public abstract class VisualizerBase : Control
{
    private IDisposable? _tick;
    private OptionValues? _options;

    protected VisualizerBase(AppServices services)
    {
        Services = services;
        ClipToBounds = true;
    }

    protected AppServices Services { get; }

    /// <summary>Identifier used in settings and in inline directives (e.g. "punchcard").</summary>
    public abstract string TypeName { get; }

    public abstract string Title { get; }

    public abstract IReadOnlyList<VisOption> DefineOptions();

    /// <summary>Draw without an opaque background (behind code / inline).</summary>
    public bool Transparent { get; set; }

    /// <summary>Optional filter (inline visuals only show the events of their own block).</summary>
    public Func<TidalEvent, bool>? EventFilter { get; set; }

    public OptionValues Options
    {
        get => _options ??= new OptionValues(new Dictionary<string, string>(), DefineOptions());
        set
        {
            _options = value;
            _options.Changed += InvalidateVisual;
            InvalidateVisual();
        }
    }

    public void BindOptions(Dictionary<string, string> store) => Options = new OptionValues(store, DefineOptions());

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tick = Services.Ticker.Subscribe(_ => InvalidateVisual());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tick?.Dispose();
        _tick = null;
    }

    public sealed override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4) return;
        if (!Transparent) context.FillRectangle(Background, bounds);
        double now = Services.Time.Now;
        using (context.PushClip(bounds))
        {
            RenderVisual(context, bounds, now);
        }
    }

    protected abstract void RenderVisual(DrawingContext dc, Rect bounds, double now);

    // ---------------- helpers ----------------

    protected static readonly IBrush Background = new SolidColorBrush(Color.Parse("#12141A"));
    protected static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    protected static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(150, 200, 208, 224));
    protected static readonly IBrush DimLabelBrush = new SolidColorBrush(Color.FromArgb(90, 200, 208, 224));
    protected static readonly Typeface LabelFace = new("Inter");
    protected static readonly Typeface MonoFace = new("Cascadia Mono, Consolas, monospace");

    protected IEnumerable<TidalEvent> Filter(IEnumerable<TidalEvent> events) =>
        EventFilter is null ? events : events.Where(EventFilter);

    protected Color EventColor(TidalEvent e)
    {
        if (e.Color is { Length: > 0 } custom && TryParseColor(custom, out var c)) return c;
        return Color.Parse(Services.Settings.PatternColor(e.PatternId));
    }

    private static readonly Dictionary<string, Color?> ColorCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryParseColor(string text, out Color color)
    {
        if (!ColorCache.TryGetValue(text, out var cached))
        {
            // Names ("orange") and hex with or without '#' ("ff8800") — '#' can't be used inside Tidal strings.
            string t = text.Trim();
            cached = Color.TryParse(t, out var parsed) ? parsed
                : t.Length is 3 or 6 or 8 && t.All(Uri.IsHexDigit) && Color.TryParse("#" + t, out var hex) ? hex
                : null;
            ColorCache[text] = cached;
        }
        color = cached ?? default;
        return cached.HasValue;
    }

    protected static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    protected static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>A stable colour for a label (sound name), used by the "sound" colour mode.</summary>
    protected static Color HashColor(string label)
    {
        int h = 17;
        foreach (char ch in label) h = h * 31 + ch;
        double hue = (Math.Abs(h) % 360) / 360.0;
        return HsvToColor(hue, 0.6, 0.95);
    }

    protected static Color HsvToColor(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)Math.Floor(h);
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (double r, double g, double b) = i switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    protected static FormattedText Text(string text, double size, IBrush brush, Typeface? face = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? LabelFace, size, brush);

    protected static void DrawText(DrawingContext dc, string text, Point at, double size, IBrush brush, Typeface? face = null)
        => dc.DrawText(Text(text, size, brush, face), at);

    protected static void DrawCentered(DrawingContext dc, string text, Point center, double size, IBrush brush, Typeface? face = null)
    {
        var ft = Text(text, size, brush, face);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    protected void DrawIdleHint(DrawingContext dc, Rect bounds, string message)
    {
        if (Transparent) return;
        DrawCentered(dc, message, bounds.Center, 12, DimLabelBrush);
    }

    private static readonly Dictionary<uint, IBrush> BrushCache = new();

    protected static IBrush Brush(Color c)
    {
        uint key = c.ToUInt32();
        if (!BrushCache.TryGetValue(key, out var b))
        {
            if (BrushCache.Count > 4096) BrushCache.Clear();
            b = new SolidColorBrush(c).ToImmutable();
            BrushCache[key] = b;
        }
        return b;
    }

    protected static Pen Pen(Color c, double thickness) => new(Brush(c), thickness);
}
