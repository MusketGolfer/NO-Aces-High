using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Experimental (GeneratedLiveries): a squadron livery made on the spot, and the squadron emblem on the tail.
///
/// A livery in this game is one texture (shader property "_Livery") that every part of the aircraft samples. For each
/// airframe a squadron flies, the base livery the game loaded for it is copied off the GPU (the textures are compressed
/// and not readable), recoloured toward the squadron colour while keeping its shading, panel lines and weathering, and
/// set on every part. The work is spread over frames, once per airframe and colour, and cached for the mission.
///
/// The emblem cannot be painted into the texture: most models' UV data cannot be read, so where the fin sits on the
/// texture is unknown. Instead it is a small decal (a textured quad) on each side of every fin-like part, placed from
/// the fin mesh's bounds (readable on any model) and parented to it, so it moves, and falls off, with the fin.
///
/// Every player builds the same result from the squadron's name, colour and emblem; nothing is sent over the network.
/// A squadron that names its own livery in squadron.json keeps it.
/// </summary>
internal static class LiveryForge
{
    private static readonly string[] FinWords = { "vstab", "vert", "fin", "rudder", "tail" };
    private static readonly int LiveryId = Shader.PropertyToID("_Livery");

    private static readonly Dictionary<string, Texture2D> Made = new();      // airframe|base texture|colour -> livery
    private static readonly HashSet<string> Making = new();
    private static readonly HashSet<Aircraft> Done = new();
    private static readonly Dictionary<Aircraft, Texture2D> Dressed = new(); // aces wearing a generated livery
    private static readonly List<UnityEngine.Object> Owned = new();
    private static Material _decalMaterialBase;
    private static float _nextScan;

    internal static void Reset()
    {
        Done.Clear();
        Dressed.Clear();
        Making.Clear();
        foreach (var o in Owned) if (o != null) UnityEngine.Object.Destroy(o);
        Owned.Clear();
        Made.Clear();
    }

    /// <summary>Once a second: aces of the live encounter (host) or its copy (client) that have not been dressed yet.</summary>
    internal static void Tick()
    {
        if (!Plugin.GeneratedLiveries.Value || Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 1f;
        var enc = Encounters.Current ?? NetSync.Mirror;
        if (enc is not { Ended: false }) return;
        if (enc.Def != null && enc.Def.Livery != null) return; // the squadron chose its own livery
        foreach (var m in enc.Members)
        {
            var a = m.Aircraft;
            if (a == null || a.disabled || Done.Contains(a)) continue;
            Done.Add(a);
            Plugin.Instance.StartCoroutine(Dress(a, enc));
        }
    }

    private static IEnumerator Dress(Aircraft a, ActiveEncounter enc)
    {
        // Wait for the game to finish putting its own livery on.
        float waited = 0f;
        var behaviour = a != null ? a.GetComponentInChildren<LiveryBehaviour>(true) : null;
        while (a != null && !a.disabled && waited < 15f && (behaviour != null && behaviour.IsLoading || BaseLivery(a) == null))
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        if (a == null || a.disabled) yield break;
        var baseTex = BaseLivery(a);
        if (baseTex == null) { Plugin.Log.LogWarning($"Generated livery: {a.definition?.unitName} has no livery texture to start from."); yield break; }

        string key = $"{a.definition?.unitName}|{baseTex.GetInstanceID()}|{ColorUtility.ToHtmlStringRGB(enc.Colour)}";
        while (Making.Contains(key)) yield return null; // another ace of the flight is making it
        if (!Made.TryGetValue(key, out var livery) || livery == null)
        {
            Making.Add(key);
            Texture2D result = null;
            yield return Recolour(baseTex, enc.Colour, t => result = t);
            Making.Remove(key);
            if (result == null) yield break;
            result.name = $"{enc.SquadronName} {a.definition?.unitName}";
            Made[key] = result;
            Owned.Add(result);
            livery = result;
            Plugin.Log.LogInfo($"Generated livery: {result.name} ({result.width}x{result.height}) in the squadron colour.");
        }
        if (a == null || a.disabled) yield break;
        Apply(a, livery);
        try { AddTailEmblem(a, enc); }
        catch (Exception ex) { Plugin.Log.LogWarning($"Generated livery: tail emblem on {a.definition?.unitName} failed: {ex.Message}"); }
    }

    private static Texture BaseLivery(Aircraft a)
    {
        foreach (var r in AllRenderers(a))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty(LiveryId) && m.GetTexture(LiveryId) is { } t) return t;
        return null;
    }

    /// <summary>
    /// The game paints each part through UnitPart.SetLivery, and can do it more than once on a fresh aircraft (a
    /// fallback livery, then the real one), so a dressed ace gets its generated livery back after every such call.
    /// </summary>
    internal static void ApplyPatch(HarmonyLib.Harmony harmony)
    {
        Patches.Patch(harmony, "generated liveries: keep them on",
            HarmonyLib.AccessTools.Method(typeof(UnitPart), nameof(UnitPart.SetLivery)),
            postfix: nameof(SetLiveryPostfix), owner: typeof(LiveryForge));
    }

    private static void SetLiveryPostfix(UnitPart __instance)
    {
        if (Dressed.Count == 0 || __instance == null) return;
        try
        {
            var a = __instance.parentUnit as Aircraft ?? __instance.GetComponentInParent<Aircraft>(); // parts are separate bodies in full physics
            if (a == null || !Dressed.TryGetValue(a, out var livery) || livery == null) return;
            foreach (var r in __instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                foreach (var m in r.materials)
                    if (m != null && m.HasProperty(LiveryId)) m.SetTexture(LiveryId, livery);
            }
        }
        catch { /* the part is going away */ }
    }

    /// <summary>
    /// Every renderer of the aircraft, found through its part list: in full (jointed) physics each part is its own
    /// body and is no longer a child of the aircraft object, so a search from the aircraft finds only the main body.
    /// </summary>
    private static IEnumerable<Renderer> AllRenderers(Aircraft a)
    {
        var seen = new HashSet<Renderer>();
        foreach (var r in a.GetComponentsInChildren<Renderer>(true)) if (seen.Add(r)) yield return r;
        if (a.partLookup == null) yield break;
        foreach (var part in a.partLookup)
            if (part != null)
                foreach (var r in part.GetComponentsInChildren<Renderer>(true))
                    if (seen.Add(r)) yield return r;
    }

    private static void Apply(Aircraft a, Texture2D livery)
    {
        Dressed[a] = livery;
        int n = 0;
        foreach (var r in AllRenderers(a))
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue; // not particles or trails
            foreach (var m in r.materials) // the game already instances these per part (UnitPart.SetLivery)
                if (m != null && m.HasProperty(LiveryId)) { m.SetTexture(LiveryId, livery); n++; }
        }
        Plugin.Log.LogInfo($"Generated livery on {a.definition?.unitName} ({n} materials).");
    }

    /// <summary>
    /// Copies the base livery off the GPU in bands and recolours it a slice per frame: the squadron colour scaled by the
    /// original's brightness (so shading, panel lines and weathering survive), blended 80% over the original; very dark
    /// and very bright detail (canopy frames, lights, markings) keeps more of its own colour.
    /// </summary>
    private static IEnumerator Recolour(Texture source, Color colour, Action<Texture2D> done)
    {
        int w = source.width, h = source.height;
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true);
        int band = Mathf.Max(64, 1 << 20 / w); // about a million pixels read back per frame
        for (int y = 0; y < h; y += band)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            int rows = Mathf.Min(band, h - y);
            tex.ReadPixels(new Rect(0, y, w, rows), 0, y, false);
            RenderTexture.active = prev;
            yield return null;
        }
        RenderTexture.ReleaseTemporary(rt);

        var px = tex.GetPixels32();
        // Diagnostics: -noahLiveryIdentity copies without recolouring, to check the copy itself is faithful.
        bool identity = Environment.GetCommandLineArgs().Any(x => x.Equals("-noahLiveryIdentity", StringComparison.OrdinalIgnoreCase));
        long sumLum = 0;
        for (int i = 0; i < px.Length; i += 61) sumLum += (px[i].r * 299 + px[i].g * 587 + px[i].b * 114) / 1000;
        Plugin.Log.LogInfo($"Generated livery: copied {w}x{h}, average brightness {sumLum / Math.Max(1, px.Length / 61)} of 255{(identity ? " (identity test: not recoloured)" : "")}.");

        Color32 c = colour;
        // The base livery's own brightness range (10th and 90th percentiles): a dark grey scheme and a light desert
        // one both map onto the full two-tone range, instead of a dark base turning almost black.
        var hist = new int[256];
        int samples = 0;
        for (int i = 0; i < px.Length; i += 17) { hist[(px[i].r * 299 + px[i].g * 587 + px[i].b * 114) / 1000]++; samples++; }
        int acc = 0, p10 = 0, p90 = 255;
        for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc < samples * 0.10f) p10 = v; if (acc < samples * 0.90f) p90 = v; }
        float lo = p10 / 255f, hi = Mathf.Max(p90 / 255f, lo + 0.08f);
        Plugin.Log.LogInfo($"Generated livery: base brightness range {lo:0.00}-{hi:0.00}.");
        int chunk = 1 << 19;
        for (int start = 0; !identity && start < px.Length; start += chunk)
        {
            int end = Mathf.Min(px.Length, start + chunk);
            for (int i = start; i < end; i++)
            {
                var p = px[i];
                float lum = (0.299f * p.r + 0.587f * p.g + 0.114f * p.b) / 255f;
                // Two-tone: lighter paint takes the squadron colour, darker paint a dark shade of it; the
                // original's light and shade rides on top, so panel lines and weathering stay.
                float rel = Mathf.Clamp01((lum - lo) / (hi - lo)); // 0 = the livery's darkest tenth, 1 = its brightest
                float t = Mathf.Clamp01(rel * 1.4f - 0.15f);       // most of the airframe takes the full colour
                t = t * t * (3f - 2f * t);
                float shade = 0.62f + 0.5f * rel;
                float r = Mathf.Lerp(c.r * 0.28f, c.r, t) * shade;
                float g = Mathf.Lerp(c.g * 0.28f, c.g, t) * shade;
                float bl = Mathf.Lerp(c.b * 0.28f, c.b, t) * shade;
                float keep = lum < lo * 0.5f ? 0.65f : lum > Mathf.Max(0.9f, hi * 1.15f) ? 0.55f : 0.1f; // panel lines and bright markings keep more of themselves
                p.r = (byte)Mathf.Clamp(Mathf.Lerp(r, p.r, keep), 0f, 255f);
                p.g = (byte)Mathf.Clamp(Mathf.Lerp(g, p.g, keep), 0f, 255f);
                p.b = (byte)Mathf.Clamp(Mathf.Lerp(bl, p.b, keep), 0f, 255f);
                px[i] = p;
            }
            yield return null;
        }
        tex.SetPixels32(px);
        string dumpDir = Environment.GetCommandLineArgs().SkipWhile(x => !x.Equals("-noahLiveryDump", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if (!string.IsNullOrEmpty(dumpDir))
            try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(dumpDir, $"livery-{w}-{DateTime.Now.Ticks}.png"), tex.EncodeToPNG()); } catch (Exception ex) { Plugin.Log.LogWarning("livery dump failed: " + ex.Message); }
        tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;
        done(tex);
    }

    // ---- tail emblem -----------------------------------------------------------------------------------

    private static Material DecalMaterial(Texture emblem)
    {
        if (_decalMaterialBase == null)
        {
            Shader shader = null;
            foreach (var name in new[] { "Sprites/Default", "Unlit/Transparent", "UI/Default", "Legacy Shaders/Transparent/Diffuse" })
                if ((shader = Shader.Find(name)) != null) break;
            if (shader == null) return null;
            _decalMaterialBase = new Material(shader);
            Owned.Add(_decalMaterialBase);
        }
        var mat = new Material(_decalMaterialBase) { mainTexture = emblem, renderQueue = 3000 };
        Owned.Add(mat);
        return mat;
    }

    /// <summary>
    /// The emblem on both sides of each fin: the fin mesh's thinnest axis is its side, the axis nearest the aircraft's
    /// up is the emblem's up, and the emblem takes 55% of the fin's smaller side, a hair off the skin.
    /// </summary>
    private static void AddTailEmblem(Aircraft a, ActiveEncounter enc)
    {
        var emblem = enc.Emblem?.FrameAt(0f);
        if (emblem == null || a.partLookup == null) return;
        var mat = DecalMaterial(emblem);
        if (mat == null) { Plugin.Log.LogWarning("Generated livery: no shader for the tail emblem."); return; }
        int placed = 0;
        var spots = new List<(Vector3 pos, float size)>(); // decals already placed (world), to skip overlapping fin parts
        // True fins first (vstab, vert, fin); rudders or a generic "tail" part only for jets without them (a "tail"
        // part is often the rear fuselage).
        var named = a.partLookup.Where(p => p != null).ToList();
        var fins = named.Where(p => new[] { "vstab", "vert", "fin" }.Any(w => p.name.ToLowerInvariant().Contains(w))).ToList();
        if (fins.Count == 0) fins = named.Where(p => p.name.ToLowerInvariant().Contains("rudder")).ToList();
        if (fins.Count == 0) fins = named.Where(p => p.name.ToLowerInvariant().Contains("tail")).ToList();
        foreach (var part in fins)
        {
            var mf = part.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null)
                         .OrderByDescending(f => f.sharedMesh.bounds.size.sqrMagnitude).FirstOrDefault();
            if (mf == null) continue;
            var b = mf.sharedMesh.bounds;
            Vector3 size = b.size;
            int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            Vector3 normal = axes[thin];
            // Of the other two, the one closest to the aircraft's up is the emblem's up.
            var others = Enumerable.Range(0, 3).Where(i => i != thin).ToList();
            Vector3 aircraftUpLocal = mf.transform.InverseTransformDirection(a.transform.up);
            int upAxis = Mathf.Abs(Vector3.Dot(axes[others[0]], aircraftUpLocal)) >= Mathf.Abs(Vector3.Dot(axes[others[1]], aircraftUpLocal)) ? others[0] : others[1];
            Vector3 up = axes[upAxis] * Mathf.Sign(Vector3.Dot(axes[upAxis], aircraftUpLocal) + 1e-4f);
            float side = Mathf.Min(size[others[0]], size[others[1]]) * 0.55f;
            if (side * Mathf.Abs(mf.transform.lossyScale.x) < 0.2f) continue; // under 20 cm across in the world: too small to read
            // Two parts of the same fin (a skin and its spar, vstab1 and vstab2) would stack two emblems.
            Vector3 centreWorld = mf.transform.TransformPoint(b.center);
            float sideWorld = side * Mathf.Abs(mf.transform.lossyScale.x);
            if (spots.Any(sp => Vector3.Distance(sp.pos, centreWorld) < Mathf.Max(sp.size, sideWorld) * 0.6f)) continue;
            spots.Add((centreWorld, sideWorld));
            for (int s = -1; s <= 1; s += 2)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "NOAH tail emblem";
                UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
                quad.transform.SetParent(mf.transform, false);
                quad.transform.localPosition = b.center + normal * s * (size[thin] * 0.5f + size.magnitude * 0.004f); // a hair off the skin, in proportion
                // A quad faces -forward: look along the side normal, emblem up along the fin's up.
                quad.transform.localRotation = Quaternion.LookRotation(-normal * s, up);
                // The quad is a child of the fin mesh, so it is sized in the mesh's own units (as its bounds are).
                quad.transform.localScale = new Vector3(side, side, 1f);
                var mr = quad.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                placed++;
            }
        }
        Plugin.Log.LogInfo($"Generated livery: {placed} tail emblem decal(s) on {a.definition?.unitName}" + (fins.Count == 0 ? " (no fin-like parts found)" : $" ({string.Join(", ", fins.Select(p => p.name))})"));
    }
}
