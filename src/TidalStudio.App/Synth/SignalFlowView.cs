using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TidalStudio.Core.Synth;

namespace TidalStudio.App.Synth;

/// <summary>
/// Schematic of the patch: the audio path (oscillators → mixer → filter → amp → effects → output) on top and the
/// modulation sources underneath, with a wire to everything they modulate. Bypassed parts are drawn faded and
/// dashed; click a block to jump to its controls.
/// </summary>
public sealed class SignalFlowView : Control
{
    public static readonly Color[] OscColors = { Color.Parse("#4ECDC4"), Color.Parse("#A78BFA"), Color.Parse("#FFD166") };
    public static readonly Color FilterColor = Color.Parse("#FF6B6B");
    public static readonly Color EnvColor = Color.Parse("#60A5FA");
    public static readonly Color ModColor = Color.Parse("#F472B6");
    public static readonly Color FxColor = Color.Parse("#34D399");
    internal static readonly Color AudioWire = Color.Parse("#C9CFDA");
    internal static readonly IBrush Back = new SolidColorBrush(Color.Parse("#12141A"));
    internal static readonly IBrush BoxFill = new SolidColorBrush(Color.Parse("#1F232C"));
    internal static readonly Typeface Font = new("Inter");
    internal static readonly Typeface FontBold = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    private readonly Func<SynthPatch> _patch;
    private readonly List<(Rect Rect, string Key)> _hits = new();
    private string? _hover;

    public SignalFlowView(Func<SynthPatch> patch)
    {
        _patch = patch;
        Height = 196;
        ClipToBounds = true;
    }

    /// <summary>A block was clicked: "osc0".."osc2", "filter", "amp", "fx", "out", "lfo1", "lfo2", "menv", "fenv", "aenv".</summary>
    public event Action<string>? BlockClicked;

    // ------------------------------------------------------------------ input

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        string? hit = _hits.LastOrDefault(h => h.Rect.Contains(p)).Key;
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
        var p = e.GetPosition(this);
        if (_hits.LastOrDefault(h => h.Rect.Contains(p)).Key is { } key) BlockClicked?.Invoke(key);
    }

    // ------------------------------------------------------------------ drawing

    private sealed record Block(string Key, Rect Rect, string Title, string Detail, Color Color, bool Active);

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(Back, b, 4);
        _hits.Clear();
        var patch = _patch();
        if (b.Width < 200) return;

        // ---- layout: oscillator column, then the chain of processing blocks
        const double pad = 10, audioTop = 10, oscH = 30, oscGap = 6;
        double oscW = Math.Clamp(b.Width * 0.13, 96, 170);
        var chain = Chain(patch);
        double mixR = 11;
        double mixX = pad + oscW + 28;
        double audioMid = audioTop + (oscH * 3 + oscGap * 2) / 2;
        double chainLeft = mixX + mixR + 22;
        double gap = 18;
        double blockW = (b.Width - pad - chainLeft - gap * (chain.Count - 1)) / chain.Count;
        const double blockH = 44;

        var oscBlocks = new List<Block>();
        for (int i = 0; i < 3; i++)
        {
            var o = patch.Oscillators[i];
            var r = new Rect(pad, audioTop + i * (oscH + oscGap), oscW, oscH);
            oscBlocks.Add(new Block($"osc{i}", r, $"OSC {i + 1}", OscDetail(o), OscColors[i], o.Enabled && o.Level > 0));
        }
        var blocks = new List<Block>();
        for (int i = 0; i < chain.Count; i++)
        {
            var (key, title, detail, color, active) = chain[i];
            var r = new Rect(chainLeft + i * (blockW + gap), audioMid - blockH / 2, blockW, blockH);
            blocks.Add(new Block(key, r, title, detail, color, active));
        }

        // ---- audio wires
        var mix = new Point(mixX, audioMid);
        foreach (var ob in oscBlocks)
        {
            var from = new Point(ob.Rect.Right, ob.Rect.Center.Y);
            Wire(dc, from, new Point(mix.X - mixR, mix.Y), ob.Active ? ob.Color : Faded(ob.Color), ob.Active ? 1.6 : 1, !ob.Active, curve: true);
        }
        bool anyOsc = oscBlocks.Any(o => o.Active);
        dc.DrawEllipse(BoxFill, new Pen(new SolidColorBrush(anyOsc ? AudioWire : Faded(AudioWire)), 1.4), mix, mixR, mixR);
        DrawCentered(dc, "+", mix, 13, FontBold, anyOsc ? AudioWire : Faded(AudioWire));

        var previous = new Point(mix.X + mixR, mix.Y);
        foreach (var block in blocks)
        {
            Arrow(dc, previous, new Point(block.Rect.Left, audioMid), anyOsc ? AudioWire : Faded(AudioWire));
            previous = new Point(block.Rect.Right, audioMid);
        }

        // ---- modulation sources and their wires
        double modTop = b.Height - pad - 40;
        var sources = Sources(patch);
        double srcGap = 12;
        double srcRight = blocks.FirstOrDefault(x => x.Key == "amp")?.Rect.Right ?? b.Width * 0.6;
        double srcW = Math.Min(150, (srcRight - pad - srcGap * (sources.Count - 1)) / sources.Count);
        var sourceBlocks = new List<(Block Block, List<(string Target, double Amount, string Label)> Routes)>();
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var r = new Rect(pad + i * (srcW + srcGap), modTop, srcW, 40);
            sourceBlocks.Add((new Block(s.Key, r, s.Title, s.Detail, s.Color, s.Routes.Count > 0), s.Routes));
        }

        // Spread the wires that arrive at the same block along its bottom edge.
        var allBlocks = oscBlocks.Concat(blocks).ToDictionary(x => x.Key);
        var arrivals = sourceBlocks.SelectMany(sb => sb.Routes.Select(r => r.Target)).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var arrived = new Dictionary<string, int>();
        foreach (var (src, routes) in sourceBlocks)
        {
            for (int i = 0; i < routes.Count; i++)
            {
                var (target, amount, label) = routes[i];
                if (!allBlocks.TryGetValue(target, out var tb)) continue;
                int n = arrivals[target], k = arrived.GetValueOrDefault(target);
                arrived[target] = k + 1;
                // Oscillators are stacked: wires to an oscillator arrive at its left edge, the others at the bottom.
                Point end = target.StartsWith("osc", StringComparison.Ordinal)
                    ? new Point(tb.Rect.Left + 6 + k * 7, tb.Rect.Bottom)
                    : new Point(tb.Rect.Left + tb.Rect.Width * (k + 1) / (n + 1), tb.Rect.Bottom);
                var start = new Point(src.Rect.Left + src.Rect.Width * (i + 1) / (routes.Count + 1), src.Rect.Top);
                ModWire(dc, start, end, src.Color, amount);
                DrawText(dc, label, new Point(end.X + 4, end.Y + 2), 9, Font, Color.FromArgb(200, src.Color.R, src.Color.G, src.Color.B));
            }
        }

        // ---- boxes on top of the wires
        foreach (var ob in oscBlocks) DrawBlock(dc, ob);
        foreach (var block in blocks) DrawBlock(dc, block);
        foreach (var (src, _) in sourceBlocks) DrawBlock(dc, src);

        DrawText(dc, "MODULATION", new Point(pad, modTop - 14), 9, FontBold, Color.FromArgb(110, 200, 208, 224));
    }

    // ------------------------------------------------------------------ content

    private static List<(string Key, string Title, string Detail, Color Color, bool Active)> Chain(SynthPatch p)
    {
        var f = p.Filter;
        var fx = p.Fx;
        string filterDetail = f.Mode == FilterMode.Off
            ? "bypassed"
            : $"{ModeShort(f.Mode)} {Hz(f.Cutoff)} · res {Pct(f.Resonance)}" + (f.Drive > 0 ? $" · drive {Pct(f.Drive)}" : "");
        return new()
        {
            ("filter", "FILTER", filterDetail, FilterColor, f.Mode != FilterMode.Off),
            ("amp", "AMP", $"vol {Pct(p.Volume)}", EnvColor, p.Volume > 0),
            ("fx-drive", "DRIVE", fx.Drive > 0 ? Pct(fx.Drive) : "off", FxColor, fx.Drive > 0),
            ("fx-chorus", "CHORUS", fx.ChorusMix > 0 ? Pct(fx.ChorusMix) : "off", FxColor, fx.ChorusMix > 0),
            ("fx-delay", "DELAY", fx.DelayMix > 0 ? $"{Pct(fx.DelayMix)} · {fx.DelayTime * 1000:0} ms" : "off", FxColor, fx.DelayMix > 0),
            ("fx-reverb", "REVERB", fx.ReverbMix > 0 ? $"{Pct(fx.ReverbMix)} · size {Pct(fx.ReverbSize)}" : "off", FxColor, fx.ReverbMix > 0),
            ("out", "OUT", $"{p.SampleRate / 1000.0:0.#} kHz{(p.Normalize ? " · norm" : "")}", EnvColor, true),
        };
    }

    private static string OscDetail(OscSettings o)
    {
        if (!o.Enabled) return "off";
        string pitch = (o.Octave != 0 ? $" {o.Octave:+0;-0}oct" : "") + (o.Semi != 0 ? $" {o.Semi:+0;-0}st" : "");
        string unison = o.Unison > 1 ? $" ×{o.Unison}" : "";
        string warp = o.Warp != WarpMode.None && o.WarpAmount > 0 ? $" {o.Warp.ToString().ToLowerInvariant()}" : "";
        return o.Table + unison + pitch + warp;
    }

    private sealed record Source(string Key, string Title, string Detail, Color Color, List<(string Target, double Amount, string Label)> Routes);

    private static List<Source> Sources(SynthPatch p)
    {
        var result = new List<Source>();
        foreach (var (key, title, lfo) in new[] { ("lfo1", "LFO 1", p.Lfo1), ("lfo2", "LFO 2", p.Lfo2) })
        {
            var routes = new List<(string, double, string)>();
            if (lfo.Target != ModTarget.None && lfo.Amount > 0) routes.AddRange(Routes(p, lfo.Target, lfo.Amount));
            string detail = routes.Count > 0 ? $"{lfo.Shape.ToString().ToLowerInvariant()} {lfo.Rate:0.##} Hz" : "not routed";
            result.Add(new Source(key, title, detail, ModColor, routes));
        }

        var m = p.ModEnvelope;
        var modRoutes = m.Target != ModTarget.None && m.Amount != 0 ? Routes(p, m.Target, m.Amount) : new();
        result.Add(new Source("menv", "MOD ENV", modRoutes.Count > 0 ? Adsr(m.Envelope) : "not routed", ModColor, modRoutes));

        var filterRoutes = p.Filter.Mode != FilterMode.Off && p.Filter.EnvAmount != 0
            ? new List<(string, double, string)> { ("filter", p.Filter.EnvAmount / 8, $"cutoff {p.Filter.EnvAmount:+0.#;-0.#} oct") }
            : new();
        result.Add(new Source("fenv", "FILTER ENV", filterRoutes.Count > 0 ? Adsr(p.FilterEnvelope) : "not routed", FilterColor, filterRoutes));

        result.Add(new Source("aenv", "AMP ENV", Adsr(p.AmpEnvelope), EnvColor, new() { ("amp", 1, "level") }));
        return result;
    }

    /// <summary>Which blocks a modulation target reaches.</summary>
    private static List<(string Target, double Amount, string Label)> Routes(SynthPatch p, ModTarget target, double amount)
    {
        string label = $"{target.ToString().ToLowerInvariant()} {amount * 100:+0;-0}%";
        return target switch
        {
            // Pitch, position and warp act on every oscillator; drawn as one wire into the oscillator column.
            ModTarget.Pitch or ModTarget.Position or ModTarget.Warp => p.Oscillators.Any(o => o.Enabled)
                ? new() { ("osc2", amount, label) }
                : new(),
            ModTarget.Cutoff => p.Filter.Mode != FilterMode.Off ? new() { ("filter", amount, label) } : new(),
            ModTarget.Amp or ModTarget.Pan => new() { ("amp", amount, label) },
            _ => new(),
        };
    }

    private static string Adsr(EnvelopeSettings e) => $"{Sec(e.Attack)} {Sec(e.Decay)} {Pct(e.Sustain)} {Sec(e.Release)}";
    private static string Sec(double s) => s < 1 ? $"{s * 1000:0}ms" : $"{s:0.#}s";
    private static string Pct(double v) => $"{v * 100:0}%";
    private static string Hz(double v) => v >= 1000 ? $"{v / 1000:0.#}k" : $"{v:0}Hz";
    private static string ModeShort(FilterMode m) => m switch
    {
        FilterMode.LowPass => "LP", FilterMode.HighPass => "HP", FilterMode.BandPass => "BP", FilterMode.Notch => "notch", _ => "",
    };

    // ------------------------------------------------------------------ primitives

    private void DrawBlock(DrawingContext dc, Block block)
    {
        _hits.Add((block.Rect, block.Key));
        DrawBox(dc, block.Rect, block.Title, block.Detail, block.Color, block.Active, _hover == block.Key);
    }

    /// <summary>A block of the diagram: title and detail, accent bar when active, dashed and faded when not.</summary>
    internal static void DrawBox(DrawingContext dc, Rect rect, string titleText, string detailText, Color blockColor, bool active, bool hover)
    {
        var block = (Rect: rect, Title: titleText, Detail: detailText, Color: blockColor, Active: active);
        var color = block.Active ? block.Color : Faded(block.Color);
        var pen = new Pen(new SolidColorBrush(color), hover ? 2 : 1.2, block.Active ? null : new DashStyle(new double[] { 3, 3 }, 0));
        dc.DrawRectangle(hover ? new SolidColorBrush(Color.FromArgb(40, block.Color.R, block.Color.G, block.Color.B)) : BoxFill, pen, block.Rect, 5, 5);
        // Accent bar on the left.
        if (block.Active)
            dc.FillRectangle(new SolidColorBrush(block.Color), new Rect(block.Rect.X + 1, block.Rect.Y + 4, 3, block.Rect.Height - 8), 1.5f);

        double x = block.Rect.X + 9, w = block.Rect.Width - 13;
        bool tall = block.Rect.Height >= 38;
        var title = Text(block.Title, 10, FontBold, block.Active ? color : Faded(Color.Parse("#C9CFDA")), w);
        var detail = Text(block.Detail, 9.5, Font, block.Active ? Color.FromArgb(220, 200, 208, 224) : Color.FromArgb(110, 200, 208, 224), w);
        if (tall)
        {
            double top = block.Rect.Center.Y - (title.Height + detail.Height) / 2;
            dc.DrawText(title, new Point(x, top));
            dc.DrawText(detail, new Point(x, top + title.Height));
        }
        else
        {
            dc.DrawText(title, new Point(x, block.Rect.Center.Y - title.Height / 2));
            var short_ = Text(block.Detail, 9.5, Font, block.Active ? Color.FromArgb(200, 200, 208, 224) : Color.FromArgb(110, 200, 208, 224), Math.Max(10, w - title.Width - 6));
            dc.DrawText(short_, new Point(x + title.Width + 6, block.Rect.Center.Y - short_.Height / 2));
        }
    }

    internal static void Arrow(DrawingContext dc, Point from, Point to, Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1.4);
        dc.DrawLine(pen, from, new Point(to.X - 1, to.Y));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(to.X, to.Y), true);
            ctx.LineTo(new Point(to.X - 6, to.Y - 3.5));
            ctx.LineTo(new Point(to.X - 6, to.Y + 3.5));
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(new SolidColorBrush(color), null, head);
    }

    internal static void Wire(DrawingContext dc, Point from, Point to, Color color, double thickness, bool dashed, bool curve)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(from, false);
            if (curve)
            {
                double mid = (from.X + to.X) / 2;
                ctx.CubicBezierTo(new Point(mid, from.Y), new Point(mid, to.Y), to);
            }
            else ctx.LineTo(to);
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness, dashed ? new DashStyle(new double[] { 3, 3 }, 0) : null), geo);
    }

    /// <summary>A modulation wire: dotted, thicker for larger amounts, with a dot where it lands.</summary>
    internal static void ModWire(DrawingContext dc, Point from, Point to, Color color, double amount)
    {
        double thickness = 1 + Math.Min(1, Math.Abs(amount)) * 1.6;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(from, false);
            double dy = Math.Max(20, (from.Y - to.Y) * 0.55);
            ctx.CubicBezierTo(new Point(from.X, from.Y - dy), new Point(to.X, to.Y + dy), to);
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B)), thickness,
            new DashStyle(new double[] { 1, 2.2 }, 0), PenLineCap.Round), geo);
        dc.DrawEllipse(new SolidColorBrush(color), null, to, 2.6, 2.6);
    }

    internal static Color Faded(Color c) => Color.FromArgb(90, c.R, c.G, c.B);

    internal static FormattedText Text(string text, double size, Typeface face, Color color, double maxWidth)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(color))
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return ft;
    }

    internal static void DrawText(DrawingContext dc, string text, Point at, double size, Typeface face, Color color) =>
        dc.DrawText(Text(text, size, face, color, 400), at);

    internal static void DrawCentered(DrawingContext dc, string text, Point centre, double size, Typeface face, Color color)
    {
        var ft = Text(text, size, face, color, 100);
        dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2));
    }
}
