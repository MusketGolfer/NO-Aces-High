using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Record mode -noahLiveryProbe: what generated liveries can work with, per fixed-wing airframe. Spawns each one far
/// away, waits for its livery to load, and logs: the livery texture (size, format, readable), whether it can be copied
/// to a readable texture and how long that takes, whether the part meshes' UVs can be read, and for tail-like parts
/// the area of the livery texture they cover. Nothing is changed on the aircraft.
/// </summary>
internal static class LiveryProbe
{
    private static readonly string[] TailWords = { "vstab", "vert", "fin", "rudder", "tail" };

    internal static IEnumerator Run(Action<string> log, string only)
    {
        var all = Encyclopedia.i.aircraft.Where(a => a != null && a.unitPrefab != null && !(a.unitName ?? "?").StartsWith("?") &&
                                                     a.unitPrefab.GetComponentInChildren<HeloControlsFilter>(true) == null)
                                         .Where(a => only.Length == 0 || only.Split(',').Any(o => o.Trim().Length > 0 && (a.unitName ?? "").IndexOf(o.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                                         .OrderBy(a => a.unitName).ToList();
        FactionHQ hq = Factions.HQFor(Side.BDF) ?? FactionRegistry.GetAllHQs().FirstOrDefault(h => h != null);
        int i = 0;
        foreach (var def in all)
        {
            i++;
            var pos = new GlobalPosition(-12000f + (i % 4) * 3000f, 6000f, 12000f + (i / 4) * 3000f);
            var a = AceSpawner.SpawnPlain(def, hq, pos, Quaternion.identity, 200f, "Probe");
            if (a == null) { log($"livery probe: {def.unitName}: spawn failed"); continue; }
            float waited = 0f;
            var lb = a.GetComponentInChildren<LiveryBehaviour>(true);
            while (waited < 6f && (lb == null || lb.IsLoading || LiveryTexture(a) == null))
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            try { Report(a, log); Materials(a, log); }
            catch (Exception ex) { log($"livery probe: {def.unitName}: failed: {ex.Message}"); }
            try
            {
                var objects = NuclearOption.Networking.NetworkManagerNuclearOption.i?.ServerObjectManager;
                if (objects != null && a.Identity != null) objects.Destroy(a.Identity, true);
                else UnityEngine.Object.Destroy(a.gameObject);
            }
            catch { /* gone */ }
            yield return new WaitForSeconds(0.3f);
        }
    }

    /// <summary>Every renderer's materials: shader and texture slots (name = texture, size), to see where the paint lives.</summary>
    private static void Materials(Aircraft a, Action<string> log)
    {
        var seen = new HashSet<string>();
        foreach (var r in a.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                var slots = new List<string>();
                foreach (var prop in m.GetTexturePropertyNames())
                {
                    var t = m.GetTexture(prop);
                    if (t != null) slots.Add($"{prop}={t.name}({t.width})");
                }
                string line = $"{m.shader?.name} [{m.name}] {string.Join(" ", slots)}";
                if (seen.Add(line)) log($"livery probe: {a.definition.unitName}:   {r.name}: {line}");
            }
        }
    }
    private static Texture LiveryTexture(Aircraft a)
    {
        foreach (var r in a.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty("_Livery") && m.GetTexture("_Livery") != null) return m.GetTexture("_Livery");
        return null;
    }

    private static void Report(Aircraft a, Action<string> log)
    {
        var tex = LiveryTexture(a);
        if (tex == null) { log($"livery probe: {a.definition.unitName}: no _Livery texture found on its materials"); return; }
        string format = tex is Texture2D t2 ? $"{t2.format}, readable {t2.isReadable}, mips {t2.mipmapCount}" : tex.GetType().Name;

        // Copy to a readable texture through the GPU (the only way for a non-readable texture), timed.
        var sw = Stopwatch.StartNew();
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
        copy.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var px = copy.GetPixels32();
        long sum = 0;
        for (int k = 0; k < px.Length; k += 97) sum += px[k].r + px[k].g + px[k].b;
        sw.Stop();
        UnityEngine.Object.Destroy(copy);

        // Meshes: readable UVs, and where the tail-like parts sit on the texture.
        int meshes = 0, readable = 0;
        var tails = new List<string>();
        foreach (var mf in a.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            meshes++;
            if (!mesh.isReadable) continue;
            readable++;
            string partName = mf.GetComponentInParent<UnitPart>(true)?.name ?? "";
            string name = (partName + "/" + mf.gameObject.name).ToLowerInvariant();
            if (!TailWords.Any(w => name.Contains(w))) continue;
            var uv = mesh.uv;
            if (uv == null || uv.Length == 0) continue;
            float minX = uv.Min(u => u.x), maxX = uv.Max(u => u.x), minY = uv.Min(u => u.y), maxY = uv.Max(u => u.y);
            tails.Add($"{partName}/{mf.gameObject.name} uv ({minX:0.000},{minY:0.000})-({maxX:0.000},{maxY:0.000})");
        }
        var skinned = a.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
        log($"livery probe: {a.definition.unitName}: livery {tex.width}x{tex.height} ({format}); GPU copy + read {sw.ElapsedMilliseconds} ms (avg {sum / Math.Max(1, px.Length / 97) / 3} ); " +
            $"meshes {readable}/{meshes} readable{(skinned > 0 ? $", {skinned} skinned" : "")}; tail parts: {(tails.Count > 0 ? string.Join("; ", tails) : "none found")}");
    }

    // ---- close-up photographs (record mode -noahLiveryShots) ---------------------------------------------

    /// <summary>
    /// For each airframe: a one-ship test squadron in a strong colour, dressed by LiveryForge, photographed from
    /// four angles at close range (left side, right side, top, front quarter) into outDir/livery-*.jpg.
    /// </summary>
    internal static IEnumerator Shots(Action<string> log, string outDir, List<AircraftDefinition> frames, FactionHQ targetHQ)
    {
        var cam = SceneSingleton<CameraStateManager>.i;
        var orbit = new OrbitCamera();
        var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        int index = 0;
        foreach (var frame in frames)
        {
            index++;
            var def = new SquadronDef
            {
                Squadron = "Livery Test", Color = "#C8102E", EmblemShape = "shield", Tier = 2, Naming = "classic",
                Aircraft = new List<string> { AirframeChecklist.Key(frame) },
                Members = new List<MemberDef> { new() { Callsign = "Paint 1", Lead = true } },
            };
            var enc = Encounters.LaunchForTest(def, new GlobalPosition(-8000f + index * 4000f, 5000f, 9000f), targetHQ, 200f);
            var a = enc?.Members.FirstOrDefault()?.Aircraft;
            if (a == null) { log($"livery shots: {frame.unitName}: spawn failed"); continue; }
            float waited = 0f;
            while (waited < 20f && a != null && !a.disabled && !Dressed(a))
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            log($"livery shots: {frame.unitName}: {(Dressed(a) ? $"dressed after {waited:0.0} s" : "NOT dressed after 20 s")}");
            float span = 15f;
            // The aircraft's own size (renderer bounds include trails and effects kilometres long).
            try { span = Mathf.Clamp(a.maxRadius * 2.6f, 10f, 60f); } catch { /* default */ }
            log($"livery shots: {frame.unitName}: camera at {span * 0.9f:0} m (radius {a.maxRadius:0.0} m)");
            var views = new (string name, Vector3 local)[]
            {
                ("left", new Vector3(-1f, 0.15f, -0.1f)), ("right", new Vector3(1f, 0.15f, -0.1f)),
                ("top", new Vector3(0f, 1f, -0.2f)), ("front", new Vector3(-0.6f, 0.2f, 0.8f)),
            };
            foreach (var (name, local) in views)
            {
                orbit.Target = a;
                orbit.Offset = local.normalized * span * 0.9f;
                // As the filmed scenarios do: menus off and the camera re-asserted every frame (the faction menu and
                // the game's own camera states otherwise win).
                for (int f = 0; f < 12; f++)
                {
                    foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                        if (canvas != null && canvas.isRootCanvas) canvas.enabled = false;
                    cam = SceneSingleton<CameraStateManager>.i;
                    if (cam != null && cam.currentState != orbit) cam.SwitchState(orbit);
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                if (tex.width != Screen.width || tex.height != Screen.height) tex.Reinitialize(Screen.width, Screen.height);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                tex.Apply(false);
                string file = System.IO.Path.Combine(outDir, $"livery-{index:00}-{frame.unitName.Replace('/', '-')}-{name}.jpg");
                System.IO.File.WriteAllBytes(file, tex.EncodeToJPG(90));
            }
            Encounters.DespawnActiveForTest();
            yield return new WaitForSeconds(0.5f);
        }
        UnityEngine.Object.Destroy(tex);
    }

    private static bool Dressed(Aircraft a)
    {
        if (a == null) return false;
        foreach (var r in a.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty("_Livery") && m.GetTexture("_Livery") is { } t && t.name.StartsWith("Livery Test")) return true;
        return false;
    }

    /// <summary>Holds the camera at an offset in the aircraft's own frame, looking at it.</summary>
    private sealed class OrbitCamera : CameraBaseState
    {
        public Aircraft Target;
        public Vector3 Offset;

        public override void EnterState(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(Datum.origin);
            cam.transform.SetParent(cam.cameraPivot);
            cam.transform.localPosition = Vector3.zero;
            cam.mainCamera.nearClipPlane = 0.3f;
        }

        public override void LeaveState(CameraStateManager cam) { }

        public override void FixedUpdateState(CameraStateManager cam) { }

        public override void UpdateState(CameraStateManager cam)
        {
            if (Target == null) return;
            Vector3 world = Target.transform.TransformDirection(Offset);
            cam.cameraPivot.localPosition = (Target.transform.position + world).ToGlobalPosition().AsVector3();
            cam.transform.localPosition = Vector3.zero;
            cam.transform.rotation = Quaternion.LookRotation(-world.normalized, Target.transform.up);
            cam.mainCamera.fieldOfView = 50f;
        }
    }
}
