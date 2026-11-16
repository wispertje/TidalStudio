namespace TidalStudio.App.Vj;

/// <summary>
/// SkSL sources for the VJ screen: one shader per scene (null for scenes drawn with the canvas), a transition
/// shader that mixes two scenes, a feedback pass (trails, kaleidoscope, RGB split) and a finishing pass.
/// Every scene gets the same uniforms: resolution, time, the music signals and a three-colour palette.
/// </summary>
internal static class VjShaders
{
    private const string Header = """
        uniform float2 iRes;
        uniform float iTime;
        uniform float uBass;
        uniform float uMid;
        uniform float uHigh;
        uniform float uLevel;
        uniform float uBeat;
        uniform float uCycle;
        uniform float uTravel;
        uniform float uIntensity;
        uniform float3 uColA;
        uniform float3 uColB;
        uniform float3 uColC;

        float hash(float2 p) {
            p = fract(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }
        float noise(float2 p) {
            float2 i = floor(p);
            float2 f = fract(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + float2(1.0, 0.0)), u.x),
                       mix(hash(i + float2(0.0, 1.0)), hash(i + float2(1.0, 1.0)), u.x), u.y);
        }
        float fbm(float2 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 5; i++) {
                v += a * noise(p);
                p = p * 2.03 + float2(1.7, 9.2);
                a *= 0.5;
            }
            return v;
        }
        float3 pal(float t) {
            t = fract(t);
            return t < 0.5 ? mix(uColA, uColB, t * 2.0) : mix(uColB, uColC, (t - 0.5) * 2.0);
        }
        float2 rot(float2 p, float a) {
            float c = cos(a);
            float s = sin(a);
            return float2(p.x * c - p.y * s, p.x * s + p.y * c);
        }

        """;

    private const string Liquid = """
        half4 main(float2 fc) {
            float2 uv = (fc - 0.5 * iRes) / iRes.y;
            float t = iTime * 0.12;
            float2 q = float2(fbm(uv * 2.0 + t), fbm(uv * 2.0 - t + 3.1));
            float2 r = float2(fbm(uv * 2.0 + 3.0 * q + float2(1.7, 9.2) + 0.3 * t),
                              fbm(uv * 2.0 + 3.0 * q + float2(8.3, 2.8) - 0.25 * t));
            float f = fbm(uv * 2.0 + (2.5 + uBass * 2.0 * uIntensity) * r);
            float3 col = pal(f * 1.4 + iTime * 0.015 + uMid * 0.25);
            col *= 0.3 + 1.25 * f + uBeat * 0.35 * uIntensity;
            col += uColC * uHigh * 0.3 * smoothstep(0.6, 1.0, f);
            return half4(half3(col), 1.0);
        }
        """;

    private const string Tunnel = """
        half4 main(float2 fc) {
            float2 uv = (fc - 0.5 * iRes) / iRes.y;
            uv = rot(uv, iTime * 0.1 + uMid * 0.2);
            float r = length(uv) + 0.001;
            float a = atan(uv.y, uv.x);
            float z = 0.32 / r + uTravel * 1.5;
            float seg = 6.0 + floor(uMid * 4.0) * 2.0;
            float stripes = 0.5 + 0.5 * cos(a * seg + z * 2.0 + iTime * 0.4);
            float ring = pow(abs(sin(z * 3.14159)), 6.0 - 4.0 * uBass * uIntensity);
            float3 col = pal(z * 0.08 + 0.2 * sin(a)) * (stripes * 0.6 + 0.4) * (0.25 + ring * 1.3);
            col *= smoothstep(0.0, 0.5, r) * (1.0 + uBeat * 0.8 * uIntensity);
            col += uColC * uBeat * 0.6 * smoothstep(0.2, 0.0, r);
            return half4(half3(col), 1.0);
        }
        """;

    private const string RetroGrid = """
        half4 main(float2 fc) {
            float2 p = (fc - 0.5 * iRes) / iRes.y;
            p.y = -p.y;
            float horizon = -0.05;
            float3 col;
            if (p.y > horizon) {
                float h = p.y - horizon;
                col = mix(uColA * 0.55, uColA * 0.06, clamp(h * 2.2, 0.0, 1.0));
                float2 sp = p - float2(0.0, 0.17);
                float r = 0.24 + 0.025 * uBass * uIntensity;
                float d = length(sp);
                float sun = smoothstep(r, r - 0.006, d);
                float gap = sp.y < 0.0 ? step(fract(-sp.y * 20.0 + iTime * 0.5), clamp(-sp.y * 2.6, 0.0, 0.7)) : 0.0;
                sun *= 1.0 - gap;
                float3 sunCol = mix(uColC, uColB, clamp(0.5 - sp.y * 2.5, 0.0, 1.0));
                col = mix(col, sunCol, sun);
                col += sunCol * 0.35 * exp(-max(d - r, 0.0) * 9.0) * (1.0 + uBeat * uIntensity);
                float star = step(0.996, hash(floor(fc / 3.0))) * smoothstep(0.0, 0.3, h);
                col += star * (0.4 + 0.6 * uHigh);
            } else {
                float depth = horizon - p.y;
                float z = 0.25 / depth;
                float x = p.x * z;
                float wz = 0.5 - abs(fract(z + uTravel * 2.0) - 0.5);
                float lz = smoothstep(0.02 * z + 0.012, 0.0, wz);
                float wx = 0.5 - abs(fract(x * 1.5) - 0.5);
                float lx = smoothstep(0.012 * z + 0.012, 0.0, wx);
                float grid = max(lz, lx);
                float fade = exp(-z * 0.07);
                col = uColA * 0.12 + uColB * grid * fade * (1.1 + uBeat * 1.6 * uIntensity);
                col += uColB * 0.35 * exp(-depth * 14.0);
            }
            return half4(half3(col), 1.0);
        }
        """;

    private const string KaleidoBloom = """
        half4 main(float2 fc) {
            float2 p = (fc - 0.5 * iRes) / iRes.y;
            float r = length(p);
            float a = atan(p.y, p.x) + iTime * 0.08;
            float n = 6.0 + floor(uMid * 3.0) * 2.0;
            float seg = 6.2831 / n;
            a = abs(mod(a, seg) - seg * 0.5);
            float2 q = float2(cos(a), sin(a)) * r * (2.6 - uBass * uIntensity);
            float v = 0.0;
            for (int i = 0; i < 5; i++) {
                q = abs(q) - 0.45 - 0.1 * sin(iTime * 0.3 + float(i));
                q = rot(q * 1.32, iTime * 0.05 + float(i) * 0.7);
                v += exp(-abs(q.y) * 3.5) * 0.3;
            }
            float3 col = pal(v * 0.6 + r * 0.6 + iTime * 0.03) * v * (0.7 + uBeat * uIntensity);
            col += uColC * 0.2 * uHigh * exp(-r * 4.0);
            return half4(half3(col), 1.0);
        }
        """;

    private const string Plasma = """
        half4 main(float2 fc) {
            float2 p = fc / iRes.y * 4.0;
            float t = iTime * 0.7;
            float v = sin(p.x + t) + sin((p.y + t) * 0.7) + sin((p.x + p.y + t) * 0.6);
            v += sin(length(p - float2(2.0 + sin(t * 0.5) * 2.0, 2.0 + cos(t * 0.3) * 2.0)) * 2.0 + t);
            v += uBass * uIntensity * 2.0 * sin(length(p - float2(2.0 * iRes.x / iRes.y, 2.0)) * 3.0 - iTime * 4.0);
            float3 col = pal(v * 0.15 + uCycle * 0.125) * (0.55 + 0.45 * sin(v * 3.14159));
            col *= 0.6 + uLevel * 0.6 + uBeat * 0.3;
            return half4(half3(col), 1.0);
        }
        """;

    private const string WarpStars = """
        half4 main(float2 fc) {
            float2 p = (fc - 0.5 * iRes) / iRes.y;
            float3 col = uColA * 0.05;
            for (int i = 0; i < 4; i++) {
                float fi = float(i);
                float depth = fract(uTravel * 0.25 + fi / 4.0);
                float scale = mix(18.0, 0.6, depth);
                float fade = smoothstep(0.0, 0.25, depth) * smoothstep(1.0, 0.85, depth);
                float2 g = p * scale + float2(fi * 13.1, fi * 7.7);
                float2 id = floor(g);
                float2 f = fract(g) - 0.5;
                float h = hash(id);
                float2 off = float2(hash(id + 1.3), hash(id + 2.7)) - 0.5;
                float2 d2 = f - off * 0.7;
                float streak = 1.0 + uLevel * 6.0 * uIntensity;
                float2 dir = normalize(p + 0.0001);
                float along = dot(d2, dir);
                float across = length(d2 - dir * along);
                float star = smoothstep(0.06, 0.0, across) * smoothstep(0.06 * streak, 0.0, abs(along)) * step(0.72, h);
                col += star * fade * pal(h + fi * 0.2) * 2.2;
            }
            col += pal(0.25) * 0.12 * exp(-length(p) * 3.0) * (1.0 + uBeat * 3.0 * uIntensity);
            return half4(half3(col), 1.0);
        }
        """;

    private const string Cells = """
        half4 main(float2 fc) {
            float2 p = (fc - 0.5 * iRes) / iRes.y * (3.5 - uBass * uIntensity);
            p += float2(iTime * 0.1, 0.0);
            float2 i = floor(p);
            float2 f = fract(p);
            float md = 8.0;
            float md2 = 8.0;
            float2 mid = float2(0.0);
            for (int y = -1; y <= 1; y++) {
                for (int x = -1; x <= 1; x++) {
                    float2 g = float2(float(x), float(y));
                    float2 o = float2(hash(i + g), hash(i + g + 5.3));
                    o = 0.5 + 0.5 * sin(iTime * 0.8 + 6.2831 * o);
                    float d = length(g + o - f);
                    if (d < md) { md2 = md; md = d; mid = i + g; }
                    else if (d < md2) { md2 = d; }
                }
            }
            float h = hash(mid);
            float edge = smoothstep(0.0, 0.06, md2 - md);
            float lit = step(1.0 - 0.35 * uBeat * uIntensity, fract(h * 7.0 + floor(uCycle * 4.0) * 0.37));
            float3 col = pal(h + uCycle * 0.05) * (0.15 + 0.55 * (1.0 - md) + lit * 1.2);
            col *= edge;
            col += (1.0 - edge) * uColC * (0.4 + uHigh);
            return half4(half3(col), 1.0);
        }
        """;

    /// <summary>One entry per scene in VjDirector.Scenes; null = drawn on the canvas.</summary>
    public static readonly string?[] Scenes =
    {
        Header + Liquid, Header + Tunnel, Header + RetroGrid, Header + KaleidoBloom, Header + Plasma, Header + WarpStars, Header + Cells,
        null, null,
    };

    public const string Transition = """
        uniform shader a;
        uniform shader b;
        uniform float p;
        uniform float kind;
        uniform float2 iRes;
        uniform float iTime;

        float hash(float2 q) {
            q = fract(q * float2(123.34, 456.21));
            q += dot(q, q + 45.32);
            return fract(q.x * q.y);
        }

        half4 main(float2 fc) {
            float2 uv = fc / iRes;
            half4 ca = a.eval(fc);
            half4 cb = b.eval(fc);
            float m = p;
            if (kind < 0.5) {
                m = p;
            } else if (kind < 1.5) {
                float edge = uv.x * 0.85 + uv.y * 0.15;
                m = smoothstep(edge - 0.04, edge + 0.04, p * 1.1 - 0.05);
            } else if (kind < 2.5) {
                float d = length((fc - 0.5 * iRes) / iRes.y);
                m = smoothstep(d - 0.02, d + 0.02, p * 1.05);
            } else if (kind < 3.5) {
                m = step(hash(floor(fc / 14.0)), p);
            } else if (kind < 4.5) {
                float band = floor(uv.y * 26.0);
                float o = (hash(float2(band, floor(iTime * 24.0))) - 0.5) * 0.35 * sin(p * 3.14159);
                ca = a.eval(fc + float2(o * iRes.x, 0.0));
                cb = b.eval(fc - float2(o * iRes.x, 0.0));
                m = step(hash(float2(band, 3.0)), p);
            } else if (kind < 5.5) {
                float2 c = 0.5 * iRes;
                ca = a.eval(c + (fc - c) * (1.0 - p * 0.7));
                cb = b.eval(c + (fc - c) * (1.0 + (1.0 - p) * 0.7));
                m = smoothstep(0.3, 0.7, p);
            } else if (kind < 6.5) {
                float lum = dot(float3(ca.rgb), float3(0.3, 0.59, 0.11));
                m = smoothstep(lum - 0.1, lum + 0.1, p * 1.25 - 0.1);
            } else {
                m = step(0.5, p);
                float w = 1.0 - abs(p - 0.5) * 2.0;
                half4 mixed = mix(ca, cb, half(m));
                return half4(mixed.rgb + half3(w * w * w), 1.0);
            }
            return mix(ca, cb, half(m));
        }
        """;

    /// <summary>Kaleidoscope / mirror / pixelate / RGB split on the scene, mixed with the zoomed previous frame.</summary>
    public const string Feedback = """
        uniform shader src;
        uniform shader prev;
        uniform float2 iRes;
        uniform float uTrails;
        uniform float uZoom;
        uniform float uRot;
        uniform float uRgb;
        uniform float uKaleido;
        uniform float uMirror;
        uniform float uPixel;

        half4 main(float2 fc) {
            float2 c = 0.5 * iRes;
            float2 p = fc - c;
            if (uKaleido > 0.5) {
                float r = length(p);
                float ang = atan(p.y, p.x);
                float seg = 6.2831 / uKaleido;
                ang = abs(mod(ang, seg) - seg * 0.5);
                p = float2(cos(ang), sin(ang)) * r;
            }
            if (uMirror > 0.5) { p.x = abs(p.x); }
            if (uPixel > 1.0) { p = floor(p / uPixel) * uPixel + uPixel * 0.5; }
            float2 sc = c + p;
            float2 dir = (fc - c) / iRes.y;
            float off = uRgb * iRes.y;
            float r = float(src.eval(sc + dir * off).r);
            float g = float(src.eval(sc).g);
            float b = float(src.eval(sc - dir * off).b);
            float3 col = float3(r, g, b);
            float2 q = fc - c;
            float cs = cos(uRot);
            float sn = sin(uRot);
            q = float2(q.x * cs - q.y * sn, q.x * sn + q.y * cs) / uZoom;
            float3 fb = float3(prev.eval(c + q).rgb) * uTrails;
            col = max(col, fb);
            return half4(half3(col), 1.0);
        }
        """;

    /// <summary>The last pass onto the screen: flash, invert, scanlines and vignette.</summary>
    public const string Finish = """
        uniform shader img;
        uniform float2 iRes;
        uniform float uInvert;
        uniform float uFlash;
        uniform float uScan;
        uniform float uVignette;

        half4 main(float2 fc) {
            float3 col = float3(img.eval(fc).rgb);
            col = mix(col, 1.0 - col, uInvert);
            col += uFlash;
            col *= 1.0 - uScan * 0.35 * (0.5 + 0.5 * sin(fc.y * 3.14159));
            float d = length((fc - 0.5 * iRes) / iRes.y);
            col *= 1.0 - uVignette * smoothstep(0.45, 1.25, d);
            return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
        }
        """;
}
