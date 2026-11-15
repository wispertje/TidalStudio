#:package SkiaSharp@3.119.4
// Generates TidalStudio's logo: an SVG (for docs), a 512 px PNG (shown in the app) and a multi-size .ico (exe and
// window icon). Run from the repository root:  dotnet run tools/logo/MakeLogo.cs
//
// The logo: a cycle (ring) with a pattern playing round it — the playhead (yellow dot) — and a tide inside.
// Small icon sizes drop the second wave and use thicker strokes so the shape stays readable at 16 px.

using System.Globalization;
using System.Text;
using SkiaSharp;

const float S = 256, C = 128, R = 84;
const string Bg1 = "#1F2533", Bg2 = "#0E1016", Track = "#2A3142";
const string Teal = "#4ECDC4", Blue = "#60A5FA", Violet = "#A78BFA", Yellow = "#FFD166";
const float ArcSweep = 300; // degrees, clockwise from the top
const string Wave1 = "M 76 134 C 92 108 110 108 128 134 S 164 160 180 134";
const string Wave2 = "M 94 172 C 105 158 117 158 128 172 S 151 186 162 172";

static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
static (double X, double Y) OnRing(double degFromTop) =>
    (C + R * Math.Sin(degFromTop * Math.PI / 180), C - R * Math.Cos(degFromTop * Math.PI / 180));

var end = OnRing(ArcSweep);
string arc = $"M {F(C)} {F(C - R)} A {F(R)} {F(R)} 0 {(ArcSweep > 180 ? 1 : 0)} 1 {F(end.X)} {F(end.Y)}";

string root = args.Length > 0 ? args[0] : ".";
string appAssets = Path.Combine(root, "src", "TidalStudio.App", "Assets");
Directory.CreateDirectory(appAssets);

// ---------------------------------------------------------------- SVG
var svg = $"""
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{Bg1}"/><stop offset="1" stop-color="{Bg2}"/></linearGradient>
    <linearGradient id="arc" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{Teal}"/><stop offset="1" stop-color="{Blue}"/></linearGradient>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="54" fill="url(#bg)"/>
  <circle cx="{F(C)}" cy="{F(C)}" r="{F(R)}" fill="none" stroke="{Track}" stroke-width="12"/>
  <path d="{arc}" fill="none" stroke="url(#arc)" stroke-width="12" stroke-linecap="round"/>
  <path d="{Wave1}" fill="none" stroke="{Teal}" stroke-width="16" stroke-linecap="round"/>
  <path d="{Wave2}" fill="none" stroke="{Violet}" stroke-width="10" stroke-linecap="round"/>
  <circle cx="{F(end.X)}" cy="{F(end.Y)}" r="15" fill="{Yellow}"/>
</svg>
""";
File.WriteAllText(Path.Combine(root, "tools", "logo", "logo.svg"), svg.Replace("\r\n", "\n") + "\n");

// ---------------------------------------------------------------- raster
byte[] Render(int px)
{
    bool small = px <= 32;
    using var surface = SKSurface.Create(new SKImageInfo(px, px, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(px / S);
    float k = small ? 1.45f : 1f; // stroke boost for tiny sizes

    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new(0, 8), new(0, 248),
            new[] { SKColor.Parse(Bg1), SKColor.Parse(Bg2) }, SKShaderTileMode.Clamp);
        float inset = small ? 0 : 8; // fill the whole tile at small sizes
        canvas.DrawRoundRect(new SKRect(inset, inset, S - inset, S - inset), 54, 54, p);
    }

    SKPaint Stroke(string color, float width) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width * k,
        StrokeCap = SKStrokeCap.Round, Color = SKColor.Parse(color),
    };

    using (var track = Stroke(Track, 12)) canvas.DrawCircle(C, C, R, track);
    using (var a = Stroke(Teal, 12))
    {
        a.Shader = SKShader.CreateLinearGradient(new(C - R, C - R), new(C + R, C + R),
            new[] { SKColor.Parse(Teal), SKColor.Parse(Blue) }, SKShaderTileMode.Clamp);
        using var path = SKPath.ParseSvgPathData(arc);
        canvas.DrawPath(path, a);
    }
    using (var w = Stroke(Teal, 16)) using (var path = SKPath.ParseSvgPathData(Wave1)) canvas.DrawPath(path, w);
    if (!small)
        using (var w = Stroke(Violet, 10)) using (var path = SKPath.ParseSvgPathData(Wave2)) canvas.DrawPath(path, w);
    using (var dot = new SKPaint { IsAntialias = true, Color = SKColor.Parse(Yellow) })
        canvas.DrawCircle((float)end.X, (float)end.Y, 15 * (small ? 1.3f : 1f), dot);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

File.WriteAllBytes(Path.Combine(appAssets, "logo.png"), Render(512));

// .ico with PNG-compressed entries (supported since Windows Vista).
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var images = sizes.Select(Render).ToArray();
using (var ico = new BinaryWriter(File.Create(Path.Combine(appAssets, "TidalStudio.ico")), Encoding.ASCII))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0); ico.Write((byte)0);
        ico.Write((short)1); ico.Write((short)32);
        ico.Write(images[i].Length); ico.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) ico.Write(img);
}

Console.WriteLine($"Wrote logo.svg, Assets/logo.png and Assets/TidalStudio.ico ({sizes.Length} sizes).");
