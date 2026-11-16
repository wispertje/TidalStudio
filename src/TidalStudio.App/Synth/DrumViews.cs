using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TidalStudio.Core.Synth;
using static TidalStudio.App.Synth.SignalFlowView;

namespace TidalStudio.App.Synth;

/// <summary>Colours of the drum layers, shared by the diagram, the panels and the graphs.</summary>
public static class DrumColors
{
    public static readonly Color Osc1 = Color.Parse("#FF6B6B");
    public static readonly Color Osc2 = Color.Parse("#A78BFA");
    public static readonly Color Noise = Color.Parse("#FFD166");
    public static readonly Color Metal = Color.Parse("#4ECDC4");
    public static readonly Color Click = Color.Parse("#FB923C");
    public static readonly Color Shape = Color.Parse("#60A5FA");
    public static readonly Color Output = Color.Parse("#34D399");
}

/// <summary>
/// Schematic of one drum: the five layers (oscillators, noise, metal, click) mixed, then drive → filter → room →
/// output. FM from oscillator 2 into oscillator 1 is drawn as a wire; bypassed parts are dashed. Click a block to jump
/// to its controls.
/// </summary>
public sealed class DrumFlowView : Control
{
    private readonly Func<DrumVoice> _voice;
    private readonly List<(Rect Rect, string Key)> _hits = new();
    private string? _hover;

    public DrumFlowView(Func<DrumVoice> voice)
    {
        _voice = voice;
        Height = 156;
        ClipToBounds = true;
    }

    /// <summary>"osc1", "osc2", "noise", "metal", "click", "drive", "filter", "room" or "out".</summary>
    public event Action<string>? BlockClicked;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        string? hit = _hits.LastOrDefault(h => h.Rect.Contains(e.GetPosition(this))).Key;
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_hits.LastOrDefault(h => h.Rect.Contains(e.GetPosition(this))).Key is { } key) BlockClicked?.Invoke(key);
    }

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(Back, b, 4);
        _hits.Clear();
        if (b.Width < 200) return;
        var v = _voice();

        const double pad = 10, top = 8, layerH = 24, layerGap = 5.5;
        double layerW = Math.Clamp(b.Width * 0.3, 150, 230);
        double layerX = pad + 22; // room on the left for the FM wire
        var layers = new (string Key, string Title, string Detail, Color Color, bool Active)[]
        {
            ("osc1", "OSC 1", Osc1Detail(v), DrumColors.Osc1, v.Osc1.Enabled && v.Osc1.Level > 0),
            ("osc2", "OSC 2", Osc2Detail(v), DrumColors.Osc2, v.Osc2.Enabled && (v.Osc2.Level > 0 || v.Osc2.Fm > 0)),
            ("noise", "NOISE", NoiseDetail(v.Noise), DrumColors.Noise, v.Noise.Enabled && v.Noise.Level > 0),
            ("metal", "METAL", MetalDetail(v.Metal), DrumColors.Metal, v.Metal.Enabled && v.Metal.Level > 0),
            ("click", "CLICK", v.Click.Enabled ? $"{v.Click.Level * 100:0}% · {v.Click.Length * 1000:0.#} ms" : "off", DrumColors.Click, v.Click.Enabled && v.Click.Level > 0),
        };
        var layerRects = layers.Select((l, i) => new Rect(layerX, top + i * (layerH + layerGap), layerW, layerH)).ToList();
        double mid = top + (layers.Length * (layerH + layerGap) - layerGap) / 2;

        // Mixer.
        const double mixR = 11;
        var mix = new Point(layerX + layerW + 34, mid);
        bool any = layers.Any(l => l.Active && l.Key != "osc2") || (v.Osc2.Enabled && v.Osc2.Level > 0);
        for (int i = 0; i < layers.Length; i++)
        {
            var l = layers[i];
            // Osc 2 used only for FM doesn't reach the mixer.
            bool audible = l.Active && !(l.Key == "osc2" && v.Osc2.Level <= 0);
            Wire(dc, new Point(layerRects[i].Right, layerRects[i].Center.Y), new Point(mix.X - mixR, mix.Y),
                audible ? l.Color : Faded(l.Color), audible ? 1.6 : 1, !audible, curve: true);
        }
        dc.DrawEllipse(BoxFill, new Pen(new SolidColorBrush(any ? AudioWire : Faded(AudioWire)), 1.4), mix, mixR, mixR);
        DrawCentered(dc, "+", mix, 13, FontBold, any ? AudioWire : Faded(AudioWire));

        // FM: osc 2 → osc 1, along the left edge.
        if (v.Osc1.Enabled && v.Osc2.Enabled && v.Osc2.Fm > 0)
        {
            var from = new Point(layerRects[1].Left, layerRects[1].Center.Y);
            var to = new Point(layerRects[0].Left, layerRects[0].Center.Y);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(from, false);
                ctx.CubicBezierTo(new Point(from.X - 20, from.Y), new Point(to.X - 20, to.Y), to);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(DrumColors.Osc2), 1 + v.Osc2.Fm * 1.5, new DashStyle(new double[] { 1, 2.2 }, 0), PenLineCap.Round), geo);
            dc.DrawEllipse(new SolidColorBrush(DrumColors.Osc2), null, to, 2.6, 2.6);
            DrawText(dc, "FM", new Point(pad - 6, (from.Y + to.Y) / 2 - 6), 9, FontBold, DrumColors.Osc2);
        }

        // Chain.
        var f = v.Filter;
        var chain = new (string Key, string Title, string Detail, Color Color, bool Active)[]
        {
            ("drive", "DRIVE", v.Drive > 0 ? $"{v.DriveMode.ToString().ToLowerInvariant()} {v.Drive * 100:0}%" : "off", DrumColors.Shape, v.Drive > 0),
            ("filter", "FILTER", f.Mode == FilterMode.Off ? "off" : $"{Short(f.Mode)} {Hz(f.Cutoff)} · res {f.Resonance * 100:0}%" + (f.EnvAmount != 0 ? $" · env {f.EnvAmount:+0.#;-0.#} oct" : ""), DrumColors.Shape, f.Mode != FilterMode.Off),
            ("room", "ROOM", v.Room > 0 ? $"{v.Room * 100:0}% · size {v.RoomSize * 100:0}%" : "off", DrumColors.Shape, v.Room > 0),
            ("out", "OUT", $"tune {v.Tune:+0.#;-0.#;0} st · lvl {v.Level * 100:0}%" + (v.Variations > 1 ? $" · ×{v.Variations}" : ""), DrumColors.Output, true),
        };
        double chainLeft = mix.X + mixR + 22, gap = 18, blockH = 46;
        double blockW = (b.Width - pad - chainLeft - gap * (chain.Length - 1)) / chain.Length;
        var previous = new Point(mix.X + mixR, mid);
        var chainRects = new List<Rect>();
        foreach (var c in chain)
        {
            var r = new Rect(chainLeft + chainRects.Count * (blockW + gap), mid - blockH / 2, blockW, blockH);
            Arrow(dc, previous, new Point(r.Left, mid), any ? AudioWire : Faded(AudioWire));
            previous = new Point(r.Right, mid);
            chainRects.Add(r);
        }

        // Filter envelope: a small source under the filter.
        if (f.Mode != FilterMode.Off && f.EnvAmount != 0)
        {
            var fr = chainRects[1];
            var src = new Rect(fr.X, b.Height - 30, fr.Width, 22);
            ModWire(dc, new Point(src.Center.X, src.Top), new Point(fr.Center.X, fr.Bottom), DrumColors.Shape, Math.Abs(f.EnvAmount) / 8);
            DrawBox(dc, src, "ENV", $"{f.EnvDecay * 1000:0} ms", DrumColors.Shape, true, false);
        }

        for (int i = 0; i < layers.Length; i++)
        {
            _hits.Add((layerRects[i], layers[i].Key));
            DrawBox(dc, layerRects[i], layers[i].Title, layers[i].Detail, layers[i].Color, layers[i].Active, _hover == layers[i].Key);
        }
        for (int i = 0; i < chain.Length; i++)
        {
            _hits.Add((chainRects[i], chain[i].Key));
            DrawBox(dc, chainRects[i], chain[i].Title, chain[i].Detail, chain[i].Color, chain[i].Active, _hover == chain[i].Key);
        }
    }

    private static string Osc1Detail(DrumVoice v)
    {
        var o = v.Osc1;
        if (!o.Enabled) return "off";
        return $"{Hz(o.Frequency)}" + (o.PitchDrop > 0 ? $" · drop {o.PitchDrop:0} st" : "") + $" · {o.Envelope.Decay * 1000:0} ms";
    }

    private static string Osc2Detail(DrumVoice v)
    {
        var o = v.Osc2;
        if (!o.Enabled) return "off";
        return $"×{o.Frequency:0.##}" + (o.Level > 0 ? $" · {o.Level * 100:0}%" : " · silent") + (o.Fm > 0 ? $" · FM {o.Fm * 100:0}%" : "");
    }

    private static string NoiseDetail(DrumNoise n) => !n.Enabled ? "off"
        : $"{n.Color.ToString().ToLowerInvariant()} {(n.Filter == FilterMode.Off ? "" : Short(n.Filter) + " " + Hz(n.Cutoff))}" + (n.Bursts > 1 ? $" · ×{n.Bursts} hits" : "") + $" · {n.Envelope.Decay * 1000:0} ms";

    private static string MetalDetail(DrumMetal m) => !m.Enabled ? "off"
        : $"{m.Voices} squares · BP {Hz(m.Cutoff)} · {m.Envelope.Decay * 1000:0} ms";

    private static string Hz(double v) => v >= 1000 ? $"{v / 1000:0.#}k" : $"{v:0} Hz";
    private static string Short(FilterMode m) => m switch
    {
        FilterMode.LowPass => "LP", FilterMode.HighPass => "HP", FilterMode.BandPass => "BP", FilterMode.Notch => "notch", _ => "",
    };
}

/// <summary>
/// A layer's level over time (filled), optionally with its pitch drop (dashed). Every graph of a drum uses the same
/// time span, so the layers can be compared.
/// </summary>
public sealed class DrumCurveView : Control
{
    private readonly Func<double, double> _level;
    private readonly Func<double> _span;
    private readonly Color _accent;
    private readonly Func<double, double>? _pitch;
    private readonly Func<bool> _active;

    public DrumCurveView(Func<double, double> level, Func<double> span, Color accent, Func<bool> active, Func<double, double>? pitch = null)
    {
        _level = level;
        _span = span;
        _accent = accent;
        _active = active;
        _pitch = pitch;
        Height = 50;
    }

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(Back, b, 4);
        double span = Math.Max(0.01, _span());
        double alpha = _active() ? 1 : 0.35;
        int n = Math.Max(2, (int)b.Width - 8);
        double h = b.Height - 12;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(4, b.Bottom - 4), true);
            for (int i = 0; i <= n; i++)
                ctx.LineTo(new Point(4 + i, b.Bottom - 4 - Math.Clamp(_level(span * i / n), 0, 1) * h));
            ctx.LineTo(new Point(4 + n, b.Bottom - 4));
            ctx.EndFigure(true);
        }
        var stroke = Color.FromArgb((byte)(255 * alpha), _accent.R, _accent.G, _accent.B);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(50 * alpha), _accent.R, _accent.G, _accent.B)), new Pen(new SolidColorBrush(stroke), 1.5), geo);

        if (_pitch is not null)
        {
            var pg = new StreamGeometry();
            using (var ctx = pg.Open())
            {
                for (int i = 0; i <= n; i++)
                {
                    var p = new Point(4 + i, b.Bottom - 4 - Math.Clamp(_pitch(span * i / n), 0, 1) * h);
                    if (i == 0) ctx.BeginFigure(p, false); else ctx.LineTo(p);
                }
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * alpha), 230, 234, 242)), 1, new DashStyle(new double[] { 3, 2 }, 0)), pg);
            DrawText(dc, "- - pitch", new Point(b.Right - 48, b.Top + 2), 9, Font, Color.FromArgb(150, 200, 208, 224));
        }
        string label = span < 1 ? $"{span * 1000:0} ms" : $"{span:0.00} s";
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9, new SolidColorBrush(Color.FromArgb(140, 200, 208, 224)));
        dc.DrawText(ft, new Point(b.Right - ft.Width - 4, b.Bottom - ft.Height - 2));
    }
}
