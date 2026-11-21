using SkiaSharp;
using TidalStudio.Core.Vj;

namespace TidalStudio.App.Vj;

/// <summary>A falling block in the "Pattern rain" scene: one Tidal event.</summary>
internal readonly record struct RainDrop(float Age, float Duration, float X, uint Color, float Gain, string Label);

/// <summary>Everything one frame needs, captured on the UI thread and drawn on the render thread.</summary>
internal sealed class VjFrame
{
    public float Time, Cycle, Travel;
    public float Bass, Mid, High, Level, Beat, Intensity;
    public float[] ColA = { 0, 0, 0 }, ColB = { 0, 0, 0 }, ColC = { 1, 1, 1 };
    public int Scene, Next = -1, TransitionKind;
    public float Progress;
    public VjPost Post;
    public int Quality;
    public Particle[] Particles = Array.Empty<Particle>();
    public float[] Spectrum = Array.Empty<float>();
    public float[] Wave = Array.Empty<float>();
    public List<RainDrop> Rain = new();
    public List<CodeLine> Code = new();
    public VjCodeMode CodeMode;
    public Dictionary<string, uint> PatternColors = new();
    public string? Hud;
    public float HudAlpha;
}

/// <summary>
/// Draws VJ frames with Skia: scene(s) into offscreen surfaces at a reduced resolution, a transition between them,
/// the hits from Tidal events, a feedback pass (trails, kaleidoscope, RGB split), then the finished image scaled to the
/// screen with the code on top at full resolution. Uses the GPU when Avalonia renders with one.
/// </summary>
internal sealed class VjRenderer
{
    /// <summary>Width cap when there is no GPU (software rendering, screenshots).</summary>
    public static int RasterMaxWidth { get; set; } = 640;

    /// <summary>Shader compile errors (empty when all compiled), for the self-test.</summary>
    public static string CompileErrors { get; private set; } = "";

    private static readonly object CompileLock = new();
    private static SKRuntimeEffect?[]? _scenes;
    private static SKRuntimeEffect? _transition, _feedback, _finish;

    private readonly object _lock = new();
    private GRContext? _gr;
    private bool _gpu;
    private int _w, _h;
    private SKSurface? _a, _b, _mix, _fb0, _fb1;

    /// <summary>
    /// GPU and software frames each keep their own surfaces: a software frame (a screenshot) must never dispose the
    /// GPU surfaces, which belong to the render thread's context.
    /// </summary>
    private sealed class SurfaceSet
    {
        public GRContext? Gr;
        public int W, H;
        public SKSurface? A, B, Mix, Fb0, Fb1;
    }
    private readonly SurfaceSet _gpuSet = new(), _rasterSet = new();
    private SurfaceSet? _current;

    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);
    private static readonly SKTypeface Mono = Pick("Cascadia Mono", "Consolas", "Courier New");
    private static readonly SKTypeface Sans = Pick("Segoe UI", "Arial");

    private static SKTypeface Pick(params string[] families)
    {
        foreach (var f in families)
        {
            var t = SKTypeface.FromFamilyName(f);
            if (t is not null && t.FamilyName.Equals(f, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return SKTypeface.Default;
    }

    public static void EnsureCompiled()
    {
        lock (CompileLock)
        {
            if (_scenes is not null) return;
            var errors = new List<string>();
            SKRuntimeEffect? Compile(string name, string? source)
            {
                if (source is null) return null;
                var effect = SKRuntimeEffect.CreateShader(source, out var error);
                if (effect is null || !string.IsNullOrEmpty(error)) errors.Add($"{name}: {error}");
                return effect;
            }
            _scenes = VjShaders.Scenes.Select((s, i) => Compile(VjDirector.Scenes[i], s)).ToArray();
            _transition = Compile("transition", VjShaders.Transition);
            _feedback = Compile("feedback", VjShaders.Feedback);
            _finish = Compile("finish", VjShaders.Finish);
            CompileErrors = string.Join("\n", errors);
        }
    }

    public void Render(SKCanvas canvas, GRContext? gr, VjFrame f, float width, float height)
    {
        if (width < 8 || height < 8) return;
        EnsureCompiled();
        lock (_lock)
        {
            int maxW = f.Quality switch { 0 => 640, 1 => 960, 2 => 1280, _ => 4096 };
            if (gr is null) maxW = Math.Min(maxW, RasterMaxWidth);
            int w = (int)Math.Min(maxW, width);
            int h = Math.Max(8, (int)(w * height / width));
            EnsureSurfaces(gr, w, h);
            if (_a is null || _mix is null || _fb0 is null || _fb1 is null) return;

            // 1. Scene(s), and the transition between them.
            DrawScene(f.Scene, _a, f);
            if (f.Next >= 0 && _b is not null && _transition is not null)
            {
                DrawScene(f.Next, _b, f);
                using var ia = _a.Snapshot();
                using var ib = _b.Snapshot();
                using var sa = ia.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Linear);
                using var sb = ib.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Linear);
                var u = new SKRuntimeEffectUniforms(_transition);
                Set(u, "p", Ease(f.Progress));
                Set(u, "kind", (float)f.TransitionKind);
                Set(u, "iRes", new[] { (float)_w, _h });
                Set(u, "iTime", f.Time);
                var c = new SKRuntimeEffectChildren(_transition) { ["a"] = sa, ["b"] = sb };
                using var shader = _transition.ToShader(u, c);
                using var paint = new SKPaint { Shader = shader };
                _mix.Canvas.DrawRect(0, 0, _w, _h, paint);
            }
            else
            {
                using var ia = _a.Snapshot();
                _mix.Canvas.DrawImage(ia, 0, 0);
            }

            // 2. Hits from Tidal events.
            DrawParticles(_mix.Canvas, f);

            // 3. Feedback: the new image over the zoomed, fading previous one.
            var post = f.Post;
            if (_feedback is not null)
            {
                using var im = _mix.Snapshot();
                using var ip = _fb0.Snapshot();
                using var sm = im.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Linear);
                using var sp = ip.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Linear);
                var u = new SKRuntimeEffectUniforms(_feedback);
                Set(u, "iRes", new[] { (float)_w, _h });
                Set(u, "uTrails", (float)post.Trails);
                Set(u, "uZoom", (float)post.Zoom);
                Set(u, "uRot", (float)post.Rotate);
                Set(u, "uRgb", (float)post.RgbShift);
                Set(u, "uKaleido", (float)post.Kaleido);
                Set(u, "uMirror", post.Mirror ? 1f : 0f);
                Set(u, "uPixel", (float)(post.Pixel * _w / 960.0));
                var c = new SKRuntimeEffectChildren(_feedback) { ["src"] = sm, ["prev"] = sp };
                using var shader = _feedback.ToShader(u, c);
                using var paint = new SKPaint { Shader = shader };
                _fb1.Canvas.DrawRect(0, 0, _w, _h, paint);
                (_fb0, _fb1) = (_fb1, _fb0);
                if (_current is not null) (_current.Fb0, _current.Fb1) = (_fb0, _fb1);
            }

            // 4. Onto the screen.
            using (var img = _fb0.Snapshot())
            using (var si = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Linear))
            {
                canvas.Save();
                canvas.Scale(width / _w, height / _h);
                if (_finish is not null)
                {
                    var u = new SKRuntimeEffectUniforms(_finish);
                    Set(u, "iRes", new[] { (float)_w, _h });
                    Set(u, "uInvert", (float)post.Invert);
                    Set(u, "uFlash", (float)post.Flash);
                    Set(u, "uScan", post.Scanlines ? 1f : 0f);
                    Set(u, "uVignette", (float)post.Vignette);
                    var c = new SKRuntimeEffectChildren(_finish) { ["img"] = si };
                    using var shader = _finish.ToShader(u, c);
                    using var paint = new SKPaint { Shader = shader };
                    canvas.DrawRect(0, 0, _w, _h, paint);
                }
                else canvas.DrawImage(img, 0, 0);
                canvas.Restore();
            }

            // 5. Code and names at full resolution.
            DrawCode(canvas, f, width, height);
            DrawHud(canvas, f, width, height);
        }
    }

    private static float Ease(float t) => t * t * (3 - 2 * t);

    private static void Set(SKRuntimeEffectUniforms u, string name, float value)
    {
        if (u.Contains(name)) u[name] = value;
    }

    private static void Set(SKRuntimeEffectUniforms u, string name, float[] value)
    {
        if (u.Contains(name)) u[name] = value;
    }

    private void EnsureSurfaces(GRContext? gr, int w, int h)
    {
        var set = gr is null ? _rasterSet : _gpuSet;
        if (set.A is null || w != set.W || h != set.H || !ReferenceEquals(gr, set.Gr))
        {
            foreach (var s in new[] { set.A, set.B, set.Mix, set.Fb0, set.Fb1 }) s?.Dispose();
            set.Gr = gr;
            set.W = w;
            set.H = h;
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            SKSurface? Make() => gr is not null ? SKSurface.Create(gr, false, info) ?? SKSurface.Create(info) : SKSurface.Create(info);
            set.A = Make();
            set.B = Make();
            set.Mix = Make();
            set.Fb0 = Make();
            set.Fb1 = Make();
            set.Fb0?.Canvas.Clear(SKColors.Black);
            set.Fb1?.Canvas.Clear(SKColors.Black);
        }
        _current = set;
        (_a, _b, _mix, _fb0, _fb1) = (set.A, set.B, set.Mix, set.Fb0, set.Fb1);
        (_gr, _gpu, _w, _h) = (gr, gr is not null, w, h);
    }

    // ------------------------------------------------------------------ scenes

    private void DrawScene(int scene, SKSurface surface, VjFrame f)
    {
        var canvas = surface.Canvas;
        var effect = _scenes is not null && scene >= 0 && scene < _scenes.Length ? _scenes[scene] : null;
        if (effect is not null)
        {
            var u = new SKRuntimeEffectUniforms(effect);
            Set(u, "iRes", new[] { (float)_w, _h });
            Set(u, "iTime", f.Time);
            Set(u, "uBass", f.Bass);
            Set(u, "uMid", f.Mid);
            Set(u, "uHigh", f.High);
            Set(u, "uLevel", f.Level);
            Set(u, "uBeat", f.Beat);
            Set(u, "uCycle", f.Cycle);
            Set(u, "uTravel", f.Travel);
            Set(u, "uIntensity", f.Intensity);
            Set(u, "uColA", f.ColA);
            Set(u, "uColB", f.ColB);
            Set(u, "uColC", f.ColC);
            using var shader = effect.ToShader(u);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, _w, _h, paint);
            return;
        }
        switch (VjDirector.Scenes[Math.Clamp(scene, 0, VjDirector.Scenes.Count - 1)])
        {
            case "Spectrum ring": SpectrumRing(canvas, f); break;
            case "Pattern rain": PatternRain(canvas, f); break;
            default: canvas.Clear(SKColors.Black); break;
        }
    }

    private static SKColor Col(float[] c, float alpha = 1, float scale = 1) =>
        new((byte)Math.Clamp(c[0] * scale * 255, 0, 255), (byte)Math.Clamp(c[1] * scale * 255, 0, 255), (byte)Math.Clamp(c[2] * scale * 255, 0, 255), (byte)Math.Clamp(alpha * 255, 0, 255));

    private static SKColor Pal(VjFrame f, float t, float alpha = 1)
    {
        t = t - MathF.Floor(t);
        float[] a = t < 0.5f ? f.ColA : f.ColB, b = t < 0.5f ? f.ColB : f.ColC;
        float k = t < 0.5f ? t * 2 : (t - 0.5f) * 2;
        return Col(new[] { a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k }, alpha);
    }

    private void Background(SKCanvas canvas, VjFrame f)
    {
        using var shader = SKShader.CreateRadialGradient(new SKPoint(_w / 2f, _h / 2f), Math.Max(_w, _h) * 0.7f,
            new[] { Col(f.ColA, 1, 0.35f + 0.3f * f.Beat), SKColors.Black }, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, _w, _h, paint);
    }

    private void SpectrumRing(SKCanvas canvas, VjFrame f)
    {
        Background(canvas, f);
        float cx = _w / 2f, cy = _h / 2f, unit = _h;
        float r0 = unit * (0.17f + 0.05f * f.Bass * f.Intensity);
        int n = f.Spectrum.Length;
        if (n == 0) return;
        using var bar = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = Math.Max(2, unit * 0.008f) };
        float spin = f.Time * 0.1f;
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < n; i++)
            {
                float a = spin + (side == 0 ? 1 : -1) * (i + 0.5f) / n * MathF.PI - MathF.PI / 2;
                float len = unit * (0.01f + 0.28f * f.Spectrum[i] * f.Intensity);
                bar.Color = Pal(f, i / (float)n * 0.9f + f.Cycle * 0.05f, 0.9f);
                canvas.DrawLine(cx + MathF.Cos(a) * r0, cy + MathF.Sin(a) * r0, cx + MathF.Cos(a) * (r0 + len), cy + MathF.Sin(a) * (r0 + len), bar);
            }
        if (f.Wave.Length > 1)
        {
            using var path = new SKPath();
            for (int i = 0; i <= f.Wave.Length; i++)
            {
                float a = i / (float)f.Wave.Length * MathF.PI * 2;
                float r = r0 * 0.8f + f.Wave[i % f.Wave.Length] * unit * 0.12f * f.Intensity;
                var p = new SKPoint(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, unit * 0.004f), Color = Col(f.ColC, 0.9f) };
            canvas.DrawPath(path, line);
        }
        using var core = new SKPaint { IsAntialias = true, Color = Col(f.ColC, 0.25f + 0.6f * f.Beat) };
        canvas.DrawCircle(cx, cy, r0 * 0.35f * (1 + f.Beat * 0.5f), core);
    }

    private void PatternRain(SKCanvas canvas, VjFrame f)
    {
        canvas.Clear(Col(f.ColA, 1, 0.12f));
        float speed = _h * 0.32f;
        using var block = new SKPaint { IsAntialias = true };
        using var font = new SKFont(Mono, Math.Max(9, _h * 0.022f));
        using var text = new SKPaint { IsAntialias = true };
        float colW = _w / 24f;
        // Faint column guides.
        using var guide = new SKPaint { Color = Col(f.ColB, 0.06f) };
        for (int i = 0; i < 24; i += 2) canvas.DrawRect(i * colW, 0, colW, _h, guide);
        foreach (var d in f.Rain)
        {
            float x = d.X * (_w - colW);
            float y = d.Age * speed;
            float hgt = Math.Max(_h * 0.012f, d.Duration * speed);
            var c = new SKColor(d.Color);
            float alpha = Math.Clamp(1 - y / _h, 0, 1);
            block.Color = c.WithAlpha((byte)(alpha * 230));
            canvas.DrawRoundRect(x + 2, y - hgt, colW - 4, hgt, 3, 3, block);
            if (d.Age < 0.12f)
            {
                block.Color = SKColors.White.WithAlpha((byte)((1 - d.Age / 0.12f) * 200));
                canvas.DrawRoundRect(x, y - hgt - 2, colW, hgt + 4, 3, 3, block);
            }
            if (d.Age < 0.6f)
            {
                text.Color = c.WithAlpha((byte)((1 - d.Age / 0.6f) * 255));
                canvas.DrawText(d.Label, x + 2, y + font.Size * 1.2f, font, text);
            }
        }
        using var now = new SKPaint { Color = Col(f.ColC, 0.3f + 0.7f * f.Beat), StrokeWidth = 2 + 4 * f.Beat };
        canvas.DrawLine(0, 1, _w, 1, now);
    }

    // ------------------------------------------------------------------ hits

    private void DrawParticles(SKCanvas canvas, VjFrame f)
    {
        if (f.Particles.Length == 0) return;
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var fill = new SKPaint { IsAntialias = true };
        using var font = new SKFont(Sans) { Embolden = true };
        float unit = _h;
        foreach (var p in f.Particles)
        {
            float t = (float)(p.Age / p.Life);
            float alpha = 1 - t;
            var color = new SKColor(p.Color);
            float x = (float)p.X * _w, y = (float)p.Y * _h;
            switch (p.Kind)
            {
                case ParticleKind.Ring:
                    float r = (float)p.Size * unit * (0.15f + 0.85f * (1 - (1 - t) * (1 - t)));
                    stroke.StrokeWidth = Math.Max(1.5f, unit * 0.012f * (1 - t));
                    stroke.Color = color.WithAlpha((byte)(alpha * 230));
                    canvas.DrawCircle(x, y, r, stroke);
                    break;
                case ParticleKind.Spark:
                    fill.Color = color.WithAlpha((byte)(alpha * 255));
                    canvas.DrawCircle(x, y, Math.Max(1.2f, (float)p.Size * unit), fill);
                    break;
                case ParticleKind.Label when p.Text is { } label:
                    font.Size = Math.Max(10, (float)p.Size * unit);
                    fill.Color = color.WithAlpha((byte)(Math.Min(1, alpha * 1.5f) * 255));
                    canvas.DrawText(label, x, y, SKTextAlign.Center, font, fill);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ code

    private static void DrawCode(SKCanvas canvas, VjFrame f, float width, float height)
    {
        if (f.CodeMode == VjCodeMode.Off || f.Code.Count == 0) return;
        bool panel = f.CodeMode == VjCodeMode.Panel;
        float areaX = panel ? width * 0.03f : width * 0.08f;
        float areaY = height * 0.06f;
        float areaW = panel ? width * 0.5f : width * 0.84f;
        float areaH = height * 0.86f;
        int maxLen = Math.Max(20, f.Code.Max(l => l.Text.Length));
        using var font = new SKFont(Mono, 20);
        float charW = font.MeasureText("M");
        float size = Math.Min(areaH / (f.Code.Count + 1) / 1.45f, areaW / (maxLen + 2) / (charW / 20f));
        size = Math.Clamp(size, 11, panel ? 30 : 60);
        font.Size = size;
        charW = font.MeasureText("M");
        float lineH = size * 1.45f;
        float blockH = lineH * f.Code.Count;
        float y0 = panel ? areaY + lineH : areaY + Math.Max(lineH, (areaH - blockH) / 2 + lineH);
        float textW = maxLen * charW;
        float x0 = panel ? areaX + size : areaX + Math.Max(0, (areaW - textW) / 2);

        if (panel)
        {
            using var back = new SKPaint { Color = new SKColor(0, 0, 0, 140), IsAntialias = true };
            float panelH = Math.Min(areaH, blockH + lineH * 1.2f);
            canvas.DrawRoundRect(areaX, areaY, Math.Min(areaW, textW + size * 2), panelH, 10, 10, back);
        }

        using var text = new SKPaint { IsAntialias = true };
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 170) };
        using var glow = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.35f) };
        using var box = new SKPaint { IsAntialias = true };
        using var bar = new SKPaint { IsAntialias = true };
        float y = y0;
        foreach (var line in f.Code)
        {
            var pattern = line.PatternId is { } id && f.PatternColors.TryGetValue(id, out var pc) ? new SKColor(pc) : new SKColor(0xFF8A93A6);
            if (line.IsError) pattern = new SKColor(0xFFFF5A5A);
            // New code types itself in; drafts (being typed in the editor) are shown as they are.
            int typed = line.IsDraft ? line.Text.Length : (int)(line.Age * 80) - line.CharOffset;
            if (typed <= 0) { y += lineH; continue; }
            string shown = typed >= line.Text.Length ? line.Text : line.Text[..typed];
            float shake = line.IsError ? MathF.Sin(f.Time * 70) * size * 0.25f * (float)Math.Max(0, 1 - line.Age / 1.5) : 0;
            float x = x0 + shake;
            float fade = line.IsTransient ? (float)Math.Clamp((CodeView.TransientSeconds - line.Age) / 1.5, 0, 1)
                : line.IsError ? (float)Math.Clamp((CodeView.ErrorSeconds - line.Age) / 1.5, 0, 1)
                : line.IsDraft ? 0.6f * (float)Math.Clamp((CodeView.DraftSeconds - line.Age) / 2, 0, 1)
                : 1;
            bool lastDraftLine = line.IsDraft && ReferenceEquals(line, f.Code[^1]);

            // Pattern colour bar at the left edge (panel) and the sounding tokens.
            if (panel)
            {
                bar.Color = pattern.WithAlpha((byte)(200 * fade));
                canvas.DrawRect(x0 - size * 0.6f, y - size * 0.95f, size * 0.18f, lineH * 0.95f, bar);
            }
            foreach (var hl in line.Highlights)
            {
                if (hl.Start >= shown.Length) continue;
                var hc = hl.PatternId is { } hid && f.PatternColors.TryGetValue(hid, out var hcol) ? new SKColor(hcol) : pattern;
                float hx = x + hl.Start * charW, hw = Math.Min(hl.Length, shown.Length - hl.Start) * charW;
                var rect = new SKRect(hx - 2, y - size * 0.98f, hx + hw + 2, y + size * 0.32f);
                glow.Color = hc.WithAlpha((byte)(180 * hl.Strength * fade));
                canvas.DrawRoundRect(rect, 4, 4, glow);
                box.Color = hc.WithAlpha((byte)((60 + 150 * hl.Strength) * fade));
                canvas.DrawRoundRect(rect, 4, 4, box);
            }
            if (!panel) canvas.DrawText(shown, x + size * 0.06f, y + size * 0.06f, font, shadow);
            var baseColor = line.IsError ? new SKColor(0xFFFF8080) : line.IsTransient ? new SKColor(0xFFB8C0D0) : new SKColor(0xFFF2F4F8);
            text.Color = Blend(baseColor, pattern, 0.25f).WithAlpha((byte)(255 * fade * (panel ? 1 : 0.92f)));
            canvas.DrawText(shown, x, y, font, text);
            // The bright tokens drawn again on top so they read on the glow.
            foreach (var hl in line.Highlights)
            {
                if (hl.Start >= shown.Length || hl.Strength < 0.3) continue;
                text.Color = SKColors.White.WithAlpha((byte)(255 * fade));
                string token = shown.Substring(hl.Start, Math.Min(hl.Length, shown.Length - hl.Start));
                canvas.DrawText(token, x + hl.Start * charW, y, font, text);
            }
            // A block cursor: while a line types itself in, and blinking at the end of a draft.
            bool typing = typed < line.Text.Length;
            bool blink = lastDraftLine && (int)(f.Time * 2.5) % 2 == 0;
            if (typing || blink)
            {
                box.Color = (line.IsDraft ? SKColors.White : pattern).WithAlpha((byte)(220 * Math.Max(fade, 0.5f)));
                canvas.DrawRect(x + shown.Length * charW + 2, y - size * 0.9f, charW * 0.6f, size * 1.1f, box);
            }
            y += lineH;
        }
    }

    private static SKColor Blend(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), a.Alpha);

    private static void DrawHud(SKCanvas canvas, VjFrame f, float width, float height)
    {
        if (f.Hud is null || f.HudAlpha <= 0) return;
        using var font = new SKFont(Sans, Math.Max(14, height * 0.03f)) { Embolden = true };
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(f.HudAlpha * 220)) };
        using var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha((byte)(f.HudAlpha * 160)) };
        float x = width - height * 0.04f, y = height * 0.94f;
        canvas.DrawText(f.Hud.ToUpperInvariant(), x + 2, y + 2, SKTextAlign.Right, font, shadow);
        canvas.DrawText(f.Hud.ToUpperInvariant(), x, y, SKTextAlign.Right, font, paint);
    }
}
