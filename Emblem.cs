using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

internal enum EmblemShape
{
    Match,
    Roundel,
    Shield,
    Pennant,
    Arrowhead,
    Wings,
    Crescent,
}

/// <summary>A squadron emblem: one texture, or a frame sequence played at a fixed rate.</summary>
internal sealed class Emblem
{
    public Texture2D[] Frames;
    public float Fps = 12f;
    public bool Generated;

    public Texture2D FrameAt(float seconds)
    {
        if (Frames == null || Frames.Length == 0) return null;
        if (Frames.Length == 1) return Frames[0];
        int i = Mathf.FloorToInt(Mathf.Max(0f, seconds) * Fps) % Frames.Length;
        return Frames[i];
    }

    /// <summary>Loads the squadron's emblem file or frame folder; null if it has none or it fails.</summary>
    internal static Emblem Load(SquadronDef def)
    {
        if (string.IsNullOrWhiteSpace(def.Emblem) || string.IsNullOrEmpty(def.Folder)) return null;
        string path = Path.Combine(def.Folder, def.EmblemFile);
        try
        {
            if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                var frames = files.Select(LoadPng).Where(t => t != null).ToArray();
                if (frames.Length == 0)
                {
                    Plugin.Log.LogWarning($"Squadron '{def.Squadron}': no PNG frames in {path}.");
                    return null;
                }
                return new Emblem { Frames = frames, Fps = Mathf.Clamp(def.EmblemFps ?? 12f, 1f, 60f) };
            }
            if (File.Exists(path))
            {
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.LogWarning(path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        ? $"Squadron '{def.Squadron}': emblem '{def.Emblem}' is WebP, which the game cannot decode; save it as PNG instead."
                        : $"Squadron '{def.Squadron}': emblem '{def.Emblem}' is not a PNG/JPG or frame folder (video emblems are not supported yet).");
                    return null;
                }
                var tex = LoadPng(path);
                return tex == null ? null : new Emblem { Frames = new[] { tex } };
            }
            Plugin.Log.LogWarning($"Squadron '{def.Squadron}': emblem '{path}' not found.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Squadron '{def.Squadron}': emblem failed to load: {ex.Message}");
        }
        return null;
    }

    private static Texture2D LoadPng(string file)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true) { name = Path.GetFileName(file) };
        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(file), markNonReadable: true))
        {
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        UnityEngine.Object.DontDestroyOnLoad(tex);
        return tex;
    }

    private static readonly Dictionary<string, Emblem> GeneratedCache = new();

    /// <summary>
    /// A procedural emblem for squadrons without artwork, in the squadron colour. Deterministic per squadron name, so
    /// the same squadron always looks the same.
    /// - Roundel: ring with a chevron or star, optional diamonds.
    /// - Shield: heraldic unit patch with a double gold border, header band and a crescent, sword or chevrons.
    /// - Pennant: a dark square flag with a compass star, swept stripes and an angular blade in the colour.
    /// - Arrowhead: a spearhead with a centre stripe and stacked chevrons.
    /// - Wings: a disc with a star between swept, feathered wings.
    /// - Crescent: a crescent moon cradling a star inside a thin ring.
    /// </summary>
    internal static Emblem Generate(string squadronName, Color colour, EmblemShape shape = EmblemShape.Roundel, Side side = Side.Unknown)
    {
        if (shape == EmblemShape.Match) shape = EmblemShape.Roundel;
        string key = squadronName + "|" + ColorUtility.ToHtmlStringRGB(colour) + "|" + shape + "|" + side;
        if (GeneratedCache.TryGetValue(key, out var cached)) return cached;

        const int size = 256;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true) { name = "emblem:" + squadronName };
        var px = new Color32[size * size];
        var painter = new Painter(Seed(squadronName), colour, 2f / size * 1.2f, side);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Centred coordinates in -1..1, y up.
            var uv = new Vector2((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f);
            px[y * size + x] = shape switch
            {
                EmblemShape.Shield => painter.Shield(uv),
                EmblemShape.Pennant => painter.Pennant(uv),
                EmblemShape.Arrowhead => painter.Arrowhead(uv),
                EmblemShape.Wings => painter.Wings(uv),
                EmblemShape.Crescent => painter.Crescent(uv),
                _ => painter.Roundel(uv),
            };
        }
        tex.SetPixels32(px);
        tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        UnityEngine.Object.DontDestroyOnLoad(tex);

        var emblem = new Emblem { Frames = new[] { tex }, Generated = true };
        GeneratedCache[key] = emblem;
        return emblem;
    }

    internal static int Seed(string text) => (text ?? "").Aggregate(17, (h, c) => unchecked(h * 31 + c)) & 0x7fffffff;

    /// <summary>Signed-distance shapes composited back to front; each method shades one pixel of one design.</summary>
    private sealed class Painter
    {
        private readonly int _seed;
        private readonly Color _main, _dark, _gold, _navy;
        private readonly float _edge;
        private static readonly Color White = new(0.96f, 0.96f, 0.96f);
        private static readonly Color Ink = new(0.08f, 0.08f, 0.09f);

        // Built once per emblem.
        private readonly Vector2[] _shield, _shieldInner, _blade, _arrow, _arrowInner, _star5, _star8, _wing;

        public Painter(int seed, Color main, float edge, Side side)
        {
            _seed = seed;
            _main = main;
            _dark = Color.Lerp(main, Color.black, 0.7f);
            _gold = new Color(0.86f, 0.74f, 0.38f);
            // Pennant flag field: BDF navy, PALA dark olive, neutral navy.
            _navy = side == Side.PALA ? new Color(0.20f, 0.22f, 0.12f) : new Color(0.13f, 0.18f, 0.38f);
            _edge = edge;

            _shield = new Vector2[]
            {
                new(-0.74f, 0.88f), new(0.74f, 0.88f), new(0.74f, 0.10f), new(0.64f, -0.36f), new(0.40f, -0.70f),
                new(0f, -0.96f), new(-0.40f, -0.70f), new(-0.64f, -0.36f), new(-0.74f, 0.10f),
            };
            _shieldInner = _shield.Select(v => v * 0.84f + new Vector2(0f, 0.02f)).ToArray();
            _blade = new Vector2[] { new(-0.10f, -0.95f), new(0.92f, 0.38f), new(0.64f, 0.96f), new(0.44f, 0.30f), new(0.12f, -0.02f), new(-0.30f, -0.48f) };
            _arrow = new Vector2[] { new(0f, 0.96f), new(0.62f, -0.44f), new(0f, -0.14f), new(-0.62f, -0.44f) };
            _arrowInner = _arrow.Select(v => v * 0.78f + new Vector2(0f, 0.06f)).ToArray();
            _star5 = Star(5, 1f, 0.42f);
            _star8 = Star(8, 1f, 0.38f);
            _wing = new Vector2[] { new(-0.22f, 0.12f), new(-0.98f, 0.52f), new(-0.90f, 0.30f), new(-0.22f, -0.12f) };
        }

        private bool Bit(int n) => ((_seed >> n) & 1) == 1;

        // ---- designs ----

        public Color Roundel(Vector2 p)
        {
            float r = p.magnitude;
            Color c = Color.clear;
            c = Over(c, _dark, Cover(r - 0.96f), 0.88f);
            c = Over(c, _main, Cover(Mathf.Max(r - 0.96f, 0.84f - r)));
            if (Bit(0))
            {
                c = Over(c, _main, Cover(Poly(p / 0.5f, _star5) * 0.5f));
            }
            else
            {
                float depth = 0.22f + ((_seed >> 2) & 7) * 0.02f;
                float chev = Mathf.Abs(p.x) * 0.9f + p.y;
                float band = Mathf.Max(Mathf.Max((0.05f - depth) - chev, chev - 0.05f), Mathf.Abs(p.x) - 0.62f);
                c = Over(c, _main, Cover(band));
            }
            if (Bit(1))
            {
                for (int k = 0; k < 4; k++)
                {
                    float a = Mathf.PI * 0.25f + k * Mathf.PI * 0.5f;
                    var q = p - new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.68f;
                    c = Over(c, White, Cover(Mathf.Abs(q.x) + Mathf.Abs(q.y) - 0.08f));
                }
            }
            return c;
        }

        public Color Shield(Vector2 p)
        {
            float outer = Poly(p, _shield);
            float inner = Poly(p, _shieldInner);
            Color c = Color.clear;
            c = Over(c, _gold, Cover(outer));
            c = Over(c, Ink, Cover(outer + 0.06f));
            c = Over(c, _gold, Cover(Mathf.Abs(inner) - 0.012f));
            // Header band with five studs.
            c = Over(c, _dark, Cover(Mathf.Max(inner, 0.56f - p.y)));
            c = Over(c, _gold, Cover(Mathf.Max(inner, Mathf.Abs(p.y - 0.56f) - 0.01f)));
            for (int k = -2; k <= 2; k++)
                c = Over(c, _gold, Cover((p - new Vector2(k * 0.2f, 0.69f)).magnitude - 0.035f));

            var m = p - new Vector2(0f, -0.06f);
            switch (_seed % 3)
            {
                case 0: // crescent and star
                    float moon = Mathf.Max(m.magnitude - 0.42f, 0.36f - (m - new Vector2(0.16f, 0.08f)).magnitude);
                    c = Over(c, _gold, Cover(Mathf.Max(moon, inner)));
                    c = Over(c, _main, Cover(Poly((m - new Vector2(0.14f, 0.06f)) / 0.15f, _star5) * 0.15f));
                    break;
                case 1: // sword over a disc
                    c = Over(c, _main, Cover(Mathf.Max(m.magnitude - 0.38f, inner)));
                    c = Over(c, _gold, Cover(Box(m - new Vector2(0f, -0.02f), new Vector2(0.035f, 0.5f))));
                    c = Over(c, _gold, Cover(Box(m - new Vector2(0f, -0.3f), new Vector2(0.16f, 0.03f))));
                    c = Over(c, _gold, Cover(Poly((m - new Vector2(0f, 0.48f)) / 0.07f, _arrow) * 0.07f));
                    break;
                default: // stacked chevrons
                    for (int k = 0; k < 3; k++)
                    {
                        var q = m - new Vector2(0f, 0.22f - k * 0.2f);
                        float chev = Mathf.Abs(Mathf.Abs(q.x) * 0.8f + q.y) - 0.045f;
                        c = Over(c, k == 0 ? _gold : _main, Cover(Mathf.Max(chev, Mathf.Abs(q.x) - 0.42f)));
                    }
                    break;
            }
            return c;
        }

        public Color Pennant(Vector2 p)
        {
            Color c = Color.clear;
            var flag = Rot(p - new Vector2(-0.22f, 0.24f), 4f);
            float sq = Box(flag, new Vector2(0.56f, 0.56f));
            c = Over(c, _navy, Cover(sq), 0.92f);
            c = Over(c, White, Cover(Mathf.Abs(sq) - 0.012f), 0.7f);
            var sc = flag - new Vector2(-0.28f, 0.26f);
            c = Over(c, White, Cover(Poly(sc / 0.2f, _star8) * 0.2f));
            c = Over(c, _main, Cover(sc.magnitude - 0.04f));
            // Swept stripes from lower left to upper right.
            for (int k = 0; k < 4; k++)
            {
                float off = -0.18f + k * 0.14f;
                float d = Seg(p, new Vector2(-0.70f, -0.20f + off), new Vector2(0.20f, 0.52f + off)) - 0.022f;
                c = Over(c, White, Cover(d), 0.9f);
            }
            float blade = Poly(p, _blade);
            c = Over(c, White, Cover(blade - 0.02f), 0.9f);
            c = Over(c, _main, Cover(blade));
            // A darker inner facet gives the blade an edge.
            float facet = Mathf.Max(blade + 0.07f, Vector2.Dot(p - new Vector2(0.4f, 0.2f), new Vector2(-0.79f, 0.61f)));
            c = Over(c, _dark, Cover(facet), 0.45f);
            return c;
        }

        public Color Arrowhead(Vector2 p)
        {
            Color c = Color.clear;
            float a = Poly(p, _arrow);
            float ai = Poly(p, _arrowInner);
            c = Over(c, White, Cover(a));
            c = Over(c, _main, Cover(a + 0.05f));
            c = Over(c, _dark, Cover(ai));
            c = Over(c, _main, Cover(Mathf.Max(Box(p - new Vector2(0f, 0.12f), new Vector2(0.04f, 0.62f)), ai)));
            int chevrons = Bit(3) ? 3 : 2;
            for (int k = 0; k < chevrons; k++)
            {
                var q = p - new Vector2(0f, -0.56f - k * 0.13f);
                float chev = Mathf.Abs(Mathf.Abs(q.x) * 0.5f + q.y) - 0.03f;
                c = Over(c, White, Cover(Mathf.Max(chev, Mathf.Abs(q.x) - 0.3f)));
            }
            return c;
        }

        public Color Wings(Vector2 p)
        {
            Color c = Color.clear;
            var mirrored = new Vector2(-Mathf.Abs(p.x), p.y);
            for (int k = 0; k < 3; k++)
            {
                var q = mirrored + new Vector2(0f, k * 0.17f);
                float scale = 1f - k * 0.14f;
                float w = Poly(new Vector2(q.x / scale, q.y), _wing);
                c = Over(c, Color.Lerp(White, _main, 0.35f * k), Cover(w));
                c = Over(c, _dark, Cover(Mathf.Abs(w) - 0.01f), 0.8f);
            }
            float disc = p.magnitude - 0.3f;
            c = Over(c, _gold, Cover(disc));
            c = Over(c, _main, Cover(disc + 0.045f));
            c = Over(c, White, Cover(Poly(p / 0.19f, Bit(2) ? _star5 : _star8) * 0.19f));
            return c;
        }

        public Color Crescent(Vector2 p)
        {
            Color c = Color.clear;
            float r = p.magnitude;
            c = Over(c, _dark, Cover(r - 0.95f), 0.85f);
            c = Over(c, _gold, Cover(Mathf.Abs(r - 0.9f) - 0.02f));
            var m = Rot(p, Bit(4) ? 20f : -20f);
            float moon = Mathf.Max(m.magnitude - 0.7f, 0.58f - (m - new Vector2(0.3f, 0.12f)).magnitude);
            c = Over(c, _main, Cover(moon));
            c = Over(c, White, Cover(Poly((m - new Vector2(0.28f, 0.1f)) / 0.22f, Bit(5) ? _star5 : _star8) * 0.22f));
            return c;
        }

        // ---- helpers ----

        private float Cover(float sdf) => Mathf.Clamp01(0.5f - sdf / _edge);

        private static Color Over(Color under, Color over, float alpha, float opacity = 1f)
        {
            alpha *= opacity;
            if (alpha <= 0f) return under;
            float a = alpha + under.a * (1f - alpha);
            if (a <= 0f) return Color.clear;
            Color rgb = (over * alpha + under * under.a * (1f - alpha)) / a;
            rgb.a = a;
            return rgb;
        }

        private static float Box(Vector2 p, Vector2 half)
        {
            var d = new Vector2(Mathf.Abs(p.x) - half.x, Mathf.Abs(p.y) - half.y);
            return new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude + Mathf.Min(Mathf.Max(d.x, d.y), 0f);
        }

        private static float Seg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude;
        }

        private static Vector2 Rot(Vector2 p, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
            return new Vector2(cs * p.x + sn * p.y, -sn * p.x + cs * p.y);
        }

        /// <summary>Signed distance to a closed polygon (negative inside).</summary>
        private static float Poly(Vector2 p, Vector2[] v)
        {
            float d = Vector2.Dot(p - v[0], p - v[0]);
            float s = 1f;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
            {
                Vector2 e = v[j] - v[i], w = p - v[i];
                Vector2 b = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Vector2.Dot(e, e));
                d = Mathf.Min(d, Vector2.Dot(b, b));
                bool c1 = p.y >= v[i].y, c2 = p.y < v[j].y, c3 = e.x * w.y > e.y * w.x;
                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) s = -s;
            }
            return s * Mathf.Sqrt(d);
        }

        private static Vector2[] Star(int points, float outer, float inner)
        {
            var v = new Vector2[points * 2];
            for (int k = 0; k < v.Length; k++)
            {
                float a = Mathf.PI * 0.5f + k * Mathf.PI / points;
                float r = k % 2 == 0 ? outer : inner;
                v[k] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            return v;
        }
    }
}
