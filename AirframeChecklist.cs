using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Which airframes random aces fly, and which weapons each may carry.
/// AllowedAirframes: "" = defaults (fixed-wing aircraft with any anti-air role), "none" = nothing, else keys joined by ';'.
/// DisabledWeapons: "airframeKey|weaponKey" entries joined by ';'. Nuclear weapons are never used.
/// </summary>
internal static class AirframeChecklist
{
    private const string None = "none";

    private static Vector2 _scroll;
    private static readonly HashSet<string> Expanded = new();
    private static readonly Dictionary<AircraftDefinition, List<WeaponMount>> WeaponCache = new();
    private static PropertyInfo _loaderIsLoaded;
    private static object _loader;

    internal static string Key(AircraftDefinition a) => string.IsNullOrEmpty(a.jsonKey) ? a.name : a.jsonKey;
    internal static string Key(WeaponMount w) => string.IsNullOrEmpty(w.jsonKey) ? w.name : w.jsonKey;

    private static HashSet<string> Split(string s) =>
        new(s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

    internal static bool IsDefaultAirframe(AircraftDefinition a) =>
        a.unitPrefab != null && a.unitPrefab.GetComponentInChildren<HeloControlsFilter>(true) == null && a.roleIdentity.antiAir > 0f;

    internal static bool IsAirframeAllowed(AircraftDefinition a)
    {
        string v = Plugin.AllowedAirframes.Value.Trim();
        if (v.Length == 0) return IsDefaultAirframe(a);
        if (v.Equals(None, StringComparison.OrdinalIgnoreCase)) return false;
        return Split(v).Contains(Key(a));
    }

    internal static bool IsWeaponAllowed(AircraftDefinition a, WeaponMount w)
    {
        if (w.info != null && w.info.nuclear) return false;
        return !Split(Plugin.DisabledWeapons.Value).Contains(Key(a) + "|" + Key(w));
    }

    // ---- post-stall capability ---------------------------------------------------------------------------
    // PostStallAirframes holds overrides: "+key" (capable, and flagged: any tier may fly post-stall in it) or "-key"
    // (not capable). Without one, an airframe is capable if any of its engines has thrust vectoring.

    private static readonly Dictionary<AircraftDefinition, bool> VectoringCache = new();

    /// <summary>True if any engine on the prefab vectors in pitch (Turbofan / Turbojet / other engines with a thrustVectoring field).</summary>
    internal static bool HasThrustVectoring(AircraftDefinition a)
    {
        if (a == null) return false;
        if (VectoringCache.TryGetValue(a, out bool cached)) return cached;
        bool found = false;
        try
        {
            if (a.unitPrefab != null)
            {
                foreach (var c in a.unitPrefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (c == null) continue;
                    var f = c.GetType().GetField("thrustVectoring", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (f == null || f.FieldType != typeof(Vector3)) continue;
                    var v = (Vector3)f.GetValue(c);
                    if (Mathf.Abs(v.x) > 0.01f) { found = true; break; }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Thrust vectoring check failed for {a.unitName}: {ex.Message}");
        }
        VectoringCache[a] = found;
        return found;
    }

    private static string PostStallOverride(AircraftDefinition a)
    {
        string key = Key(a);
        foreach (var e in Split(Plugin.PostStallAirframes.Value))
            if (e.Length > 1 && e.Substring(1).Equals(key, StringComparison.OrdinalIgnoreCase)) return e.Substring(0, 1);
        return null;
    }

    internal static bool IsPostStallCapable(AircraftDefinition a) =>
        a != null && (PostStallOverride(a) is { } o ? o == "+" : HasThrustVectoring(a));

    /// <summary>Flagged by hand: aces of any tier may fly post-stall manoeuvres in it.</summary>
    internal static bool IsPostStallFlagged(AircraftDefinition a) => a != null && PostStallOverride(a) == "+";

    private static void SetPostStall(AircraftDefinition a, bool on)
    {
        string key = Key(a);
        var entries = Split(Plugin.PostStallAirframes.Value)
            .Where(e => !(e.Length > 1 && e.Substring(1).Equals(key, StringComparison.OrdinalIgnoreCase))).ToList();
        if (on != HasThrustVectoring(a)) entries.Add((on ? "+" : "-") + key); // matching the default needs no entry
        Plugin.PostStallAirframes.Value = string.Join(";", entries);
    }

    internal static bool EncyclopediaReady()
    {
        try
        {
            if (_loaderIsLoaded == null)
            {
                _loader = typeof(Encyclopedia).GetField("loader", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                _loaderIsLoaded = _loader?.GetType().GetProperty("IsLoaded");
            }
            return _loaderIsLoaded != null && (bool)_loaderIsLoaded.GetValue(_loader);
        }
        catch
        {
            return false;
        }
    }

    private static List<WeaponMount> WeaponsFor(AircraftDefinition a)
    {
        if (WeaponCache.TryGetValue(a, out var list)) return list;
        list = new List<WeaponMount>();
        var wm = a.unitPrefab != null ? a.unitPrefab.GetComponent<Aircraft>()?.weaponManager : null;
        if (wm?.hardpointSets != null)
        {
            foreach (var set in wm.hardpointSets)
            foreach (var w in set.weaponOptions)
                if (w != null && (w.info == null || !w.info.nuclear) && !list.Contains(w))
                    list.Add(w);
        }
        list.Sort((x, y) => string.Compare(Label(x), Label(y), StringComparison.OrdinalIgnoreCase));
        WeaponCache[a] = list;
        return list;
    }

    private static string Label(WeaponMount w) =>
        !string.IsNullOrEmpty(w.mountName) ? w.mountName : w.info != null && !string.IsNullOrEmpty(w.info.weaponName) ? w.info.weaponName : w.name;

    private static void SetAirframes(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        Plugin.AllowedAirframes.Value = list.Count == 0 ? None : string.Join(";", list);
    }

    internal static void Draw(ConfigEntryBase _)
    {
        GUILayout.BeginVertical();
        if (!EncyclopediaReady())
        {
            GUILayout.Label("Loading aircraft... (open this menu again once the game has finished loading)");
            GUILayout.EndVertical();
            return;
        }

        var all = Encyclopedia.i.aircraft.Where(a => a != null).OrderBy(a => a.unitName).ToList();
        var allowed = all.Where(IsAirframeAllowed).Select(Key).ToList();

        GUILayout.Label($"Random aces fly the ticked airframes ({allowed.Count}/{all.Count}). Handmade squadrons fly what their file says.");
        GUILayout.Label("Post-stall: aces in this airframe may fly the Herbst and Kulbit (any airframe can Cobra). On by default for thrust " +
                        "vectoring; ticking it by hand also lets aces below PostStallMinTier use post-stall manoeuvres in it.");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Defaults")) Plugin.AllowedAirframes.Value = "";
        if (GUILayout.Button("All")) SetAirframes(all.Select(Key));
        if (GUILayout.Button("None")) SetAirframes(Array.Empty<string>());
        GUILayout.EndHorizontal();

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(320));
        foreach (var a in all)
        {
            string key = Key(a);
            bool on = allowed.Contains(key, StringComparer.OrdinalIgnoreCase);
            GUILayout.BeginHorizontal();
            bool now = GUILayout.Toggle(on, " " + a.unitName);
            if (now != on)
            {
                var next = allowed.Where(k => !k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (now) next.Add(key);
                SetAirframes(next);
            }
            GUILayout.FlexibleSpace();
            bool ps = IsPostStallCapable(a);
            string psLabel = HasThrustVectoring(a) ? " Post-stall (vectoring)" : " Post-stall";
            bool psNow = GUILayout.Toggle(ps, psLabel, GUILayout.Width(170));
            if (psNow != ps) SetPostStall(a, psNow);
            bool open = Expanded.Contains(key);
            if (GUILayout.Button(open ? "Weapons ▲" : "Weapons ▼", GUILayout.Width(100)))
            {
                if (open) Expanded.Remove(key); else Expanded.Add(key);
            }
            GUILayout.EndHorizontal();

            if (!open) continue;
            var weapons = WeaponsFor(a);
            if (weapons.Count == 0) GUILayout.Label("      (no weapon options)");
            foreach (var w in weapons)
            {
                string entry = key + "|" + Key(w);
                bool wOn = IsWeaponAllowed(a, w);
                GUILayout.BeginHorizontal();
                GUILayout.Space(24);
                bool wNow = GUILayout.Toggle(wOn, " " + Label(w));
                GUILayout.EndHorizontal();
                if (wNow == wOn) continue;
                var disabled = Split(Plugin.DisabledWeapons.Value);
                if (wNow) disabled.Remove(entry); else disabled.Add(entry);
                Plugin.DisabledWeapons.Value = string.Join(";", disabled);
            }
        }
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }
}
