using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuclearOption.SavedMission;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NOAcesHigh;

/// <summary>A loadout written by hand: in squadron.json, or saved from the F1 loadout editor.</summary>
internal sealed class LoadoutSpec
{
    [JsonProperty("name")] public string Name;
    /// <summary>A standard loadout (or saved preset) to start from; unlisted stations keep its weapons. Unset: each
    /// unlisted station gets the best air-to-air weapon it can carry.</summary>
    [JsonProperty("base")] public string Base;
    /// <summary>Fuel, 0-1 (unset: the base's, else the jet's default).</summary>
    [JsonProperty("fuel")] public float? Fuel;
    /// <summary>Station (hardpoint group name, or its number from 1) -> weapon (its name, or "empty").</summary>
    [JsonProperty("stations")] public Dictionary<string, string> Stations = new();
    /// <summary>Saved presets only: random aces flying this jet pick among the presets marked for them.</summary>
    [JsonProperty("aces", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Aces;
}

/// <summary>
/// Aircraft loadouts. Three sources, all by name: the jet's standard loadouts (the game's loadout menu), presets saved
/// with the F1 loadout editor (BepInEx/config/NOAcesHigh/Loadouts/&lt;jet&gt;.json), and loadouts built in a squadron
/// file. A built loadout names weapons per station:
///
///   "loadout": { "base": "Air Superiority", "fuel": 0.8, "stations": { "Outer": "AAM-36 Scimitar", "3": "empty" } }
///
/// Stations are the jet's hardpoint groups, by name or number (1 = first); weapons by their name in the game (full or
/// short), or "empty". Unlisted stations keep the base loadout's weapon, or with no base get the best air-to-air
/// weapon that fits.
/// </summary>
internal static class Loadouts
{
    internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "NOAcesHigh", "Loadouts");

    private static readonly Dictionary<string, List<LoadoutSpec>> Saved = new();

    // ---- data ---------------------------------------------------------------------------------------------

    internal static WeaponManager Manager(AircraftDefinition a) => a?.unitPrefab != null ? a.unitPrefab.GetComponent<Aircraft>()?.weaponManager : null;

    internal static HardpointSet[] Stations(AircraftDefinition a) => Manager(a)?.hardpointSets ?? Array.Empty<HardpointSet>();

    private static string BaseName(HardpointSet set, int index) => string.IsNullOrWhiteSpace(set.name) ? $"Station {index + 1}" : set.name.Trim();

    /// <summary>A station's label, unique on the jet: its hardpoint group name, with " (2)" etc. when a name repeats.</summary>
    internal static string StationName(HardpointSet[] sets, int index)
    {
        string name = BaseName(sets[index], index);
        int before = 0;
        for (int i = 0; i < index; i++) if (string.Equals(BaseName(sets[i], i), name, StringComparison.OrdinalIgnoreCase)) before++;
        return before == 0 ? name : $"{name} ({before + 1})";
    }

    internal static string WeaponName(WeaponMount w) =>
        w == null ? "empty" : !string.IsNullOrWhiteSpace(w.mountName) ? w.mountName : w.info != null ? w.info.weaponName : w.name;

    internal static IEnumerable<StandardLoadout> Standard(AircraftDefinition a) =>
        (a?.aircraftParameters?.StandardLoadouts ?? Array.Empty<StandardLoadout>()).Where(s => s != null && !s.disabled && s.loadout != null);

    internal static List<LoadoutSpec> SavedFor(AircraftDefinition a)
    {
        string key = AirframeChecklist.Key(a);
        if (Saved.TryGetValue(key, out var list)) return list;
        list = new List<LoadoutSpec>();
        string file = Path.Combine(Folder, Safe(key) + ".json");
        try { if (File.Exists(file)) list = JsonConvert.DeserializeObject<List<LoadoutSpec>>(File.ReadAllText(file)) ?? list; }
        catch (Exception ex) { Plugin.Log.LogWarning($"Loadouts: could not read {file}: {ex.Message}"); }
        Saved[key] = list;
        return list;
    }

    internal static void Save(AircraftDefinition a)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, Safe(AirframeChecklist.Key(a)) + ".json"), JsonConvert.SerializeObject(SavedFor(a), Formatting.Indented));
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Loadouts: could not save: " + ex.Message); }
    }

    private static string Safe(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars()));

    // ---- building -------------------------------------------------------------------------------------------

    private static int FindStation(HardpointSet[] sets, string key)
    {
        if (int.TryParse(key.Trim(), out int n) && n >= 1 && n <= sets.Length) return n - 1;
        for (int i = 0; i < sets.Length; i++)
            if (string.Equals(StationName(sets, i), key.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool Matches(WeaponMount w, string name)
    {
        if (w == null) return false;
        name = name.Trim();
        return new[] { w.mountName, w.jsonKey, w.name, w.info?.weaponName, w.info?.shortName }
            .Any(n => !string.IsNullOrWhiteSpace(n) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every weapon a station can carry: its listed options, then any other known mount by that name.</summary>
    private static WeaponMount FindWeapon(HardpointSet set, string name, out bool offList)
    {
        offList = false;
        var listed = set.weaponOptions.FirstOrDefault(w => Matches(w, name));
        if (listed != null) return listed;
        return null;
    }

    /// <summary>Builds a spec into a loadout for the jet. Problems (unknown stations or weapons) are reported, and those entries skipped.</summary>
    internal static Loadout Build(AircraftDefinition a, LoadoutSpec spec, out float fuel, List<string> problems)
    {
        var wm = Manager(a);
        var sets = Stations(a);
        fuel = a.aircraftParameters.DefaultFuelLevel;
        var loadout = new Loadout();
        Loadout start = null;
        if (!string.IsNullOrWhiteSpace(spec.Base))
        {
            if (TryNamed(a, spec.Base, out var b, out float baseFuel, problems, depth: 1)) { start = b; fuel = baseFuel; }
            else problems?.Add($"base \"{spec.Base}\" is not a loadout of the {a.unitName}");
        }
        for (int i = 0; i < sets.Length; i++)
            loadout.weapons.Add(start != null && i < start.weapons.Count ? start.weapons[i] : start == null ? BestAirToAir(a, sets[i]) : null);
        foreach (var pair in spec.Stations ?? new Dictionary<string, string>())
        {
            int i = FindStation(sets, pair.Key);
            if (i < 0) { problems?.Add($"the {a.unitName} has no station \"{pair.Key}\" (it has {string.Join(", ", sets.Select((x, i) => StationName(sets, i)))})"); continue; }
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Trim().Equals("empty", StringComparison.OrdinalIgnoreCase)) { loadout.weapons[i] = null; continue; }
            var w = FindWeapon(sets[i], pair.Value, out _);
            if (w == null) { problems?.Add($"station \"{StationName(sets, i)}\" of the {a.unitName} cannot carry \"{pair.Value}\" (it can carry {string.Join(", ", sets[i].weaponOptions.Where(x => x != null).Select(WeaponName))})"); continue; }
            loadout.weapons[i] = w;
        }
        if (spec.Fuel.HasValue) fuel = Mathf.Clamp01(spec.Fuel.Value);
        // Clear stations the chosen mounts block (a centreline tank over two inner pylons, say).
        for (int i = 0; i < sets.Length; i++)
            if (loadout.weapons[i] != null && sets[i].BlockedByOtherHardpoint(loadout))
            {
                problems?.Add($"station \"{StationName(sets, i)}\" is blocked by another station's weapon, so it is left empty");
                loadout.weapons[i] = null;
            }
        return loadout;
    }

    private static WeaponMount BestAirToAir(AircraftDefinition a, HardpointSet set)
    {
        var options = set.weaponOptions.Where(w => w != null && w.info != null && !w.info.nuclear).ToList();
        var aa = options.Where(w => AceSpawner.IsAirToAirMissile(w.info)).ToList();
        if (aa.Count == 0) aa = options.Where(w => w.info.gun || w.info.effectiveness.antiAir >= 0.5f).ToList();
        return aa.Count > 0 ? aa.OrderByDescending(w => w.info.targetRequirements.maxRange).First() : null;
    }

    /// <summary>A loadout by name: a standard one, then a saved preset.</summary>
    internal static bool TryNamed(AircraftDefinition a, string name, out Loadout loadout, out float fuel, List<string> problems = null, int depth = 0)
    {
        loadout = null;
        fuel = a.aircraftParameters.DefaultFuelLevel;
        var std = Standard(a).FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (std != null) { loadout = std.loadout; fuel = std.FuelRatio; return true; }
        var preset = SavedFor(a).FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset != null && depth < 3) { loadout = Build(a, preset, out fuel, problems); return true; }
        return false;
    }

    /// <summary>
    /// A squadron (or member) "loadout" value for a jet: a name, or a built loadout. Null when unset or "default", or
    /// (with a warning) when it does not fit this jet, so the caller picks one as usual.
    /// </summary>
    internal static Loadout FromJson(JToken token, AircraftDefinition a, string who, out float fuel)
    {
        fuel = a.aircraftParameters.DefaultFuelLevel;
        if (token == null || token.Type == JTokenType.Null) return null;
        var problems = new List<string>();
        if (token.Type == JTokenType.String)
        {
            string name = token.ToString();
            if (string.IsNullOrWhiteSpace(name) || name.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
            if (TryNamed(a, name, out var named, out fuel, problems)) { Report(who, a, problems); return named; }
            Plugin.Log.LogWarning($"{who}: loadout \"{name}\" is not a loadout of the {a.unitName} (it has {string.Join(", ", Standard(a).Select(s => s.Name).Concat(SavedFor(a).Select(s => s.Name)))}); picking one.");
            return null;
        }
        if (token.Type == JTokenType.Object)
        {
            LoadoutSpec spec;
            try { spec = token.ToObject<LoadoutSpec>(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"{who}: loadout could not be read: {ex.Message}"); return null; }
            var built = Build(a, spec, out fuel, problems);
            Report(who, a, problems);
            return built;
        }
        Plugin.Log.LogWarning($"{who}: \"loadout\" should be a name or {{ \"stations\": ... }}.");
        return null;
    }

    private static void Report(string who, AircraftDefinition a, List<string> problems)
    {
        foreach (var p in problems) Plugin.Log.LogWarning($"{who}: loadout on the {a.unitName}: {p}.");
    }

    /// <summary>Saved presets marked for aces on this jet, built; empty if none.</summary>
    internal static List<(string name, Loadout loadout, float fuel)> ForAces(AircraftDefinition a) =>
        SavedFor(a).Where(s => s.Aces).Select(s => (s.Name, Build(a, s, out float f, null), f)).ToList();

    /// <summary>A loadout as a spec (station name -> weapon name), for the editor and for copying into a squadron file.</summary>
    internal static LoadoutSpec ToSpec(AircraftDefinition a, Loadout loadout, string name, float fuel)
    {
        var sets = Stations(a);
        var spec = new LoadoutSpec { Name = name, Fuel = (float)Math.Round(fuel, 2) };
        for (int i = 0; i < sets.Length; i++)
            spec.Stations[StationName(sets, i)] = WeaponName(i < loadout.weapons.Count ? loadout.weapons[i] : null);
        return spec;
    }

    // ---- F1 editor ------------------------------------------------------------------------------------------

    private static AircraftDefinition _jet;
    private static Vector2 _jetScroll;
    private static bool _pickingJet;
    private static string _status = "";

    private static List<AircraftDefinition> Jets() =>
        Encyclopedia.i == null ? new List<AircraftDefinition>()
            : Encyclopedia.i.aircraft.Where(a => a != null && a.unitPrefab != null && !string.IsNullOrWhiteSpace(a.unitName) && !a.unitName.Contains("?") && Stations(a).Length > 0).OrderBy(a => a.unitName).ToList();

    internal static void Draw(ConfigEntryBase entry)
    {
        GUILayout.BeginVertical();
        var jets = Jets();
        if (jets.Count == 0) { GUILayout.Label("Aircraft are not loaded yet (open this from the main menu or in a mission)."); GUILayout.EndVertical(); return; }
        _jet ??= jets.FirstOrDefault(j => j.unitName.IndexOf("Revoker", StringComparison.OrdinalIgnoreCase) >= 0) ?? jets[0];

        GUILayout.BeginHorizontal();
        GUILayout.Label("Aircraft:", GUILayout.Width(70));
        if (GUILayout.Button(_jet.unitName + (_pickingJet ? "  ▲" : "  ▼"))) _pickingJet = !_pickingJet;
        GUILayout.EndHorizontal();
        if (_pickingJet)
        {
            _jetScroll = GUILayout.BeginScrollView(_jetScroll, GUILayout.Height(160));
            foreach (var j in jets)
                if (GUILayout.Button(j.unitName)) { _jet = j; _pickingJet = false; }
            GUILayout.EndScrollView();
        }

        var a = _jet;
        var sets = Stations(a);
        GUILayout.Label($"<b>Standard loadouts</b> (the game's; read only)", Rich);
        foreach (var s in Standard(a))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{s.Name}</b>  fuel {s.FuelRatio:P0}: " + string.Join(" · ", sets.Select((set, i) =>
                $"{StationName(sets, i)}: {WeaponName(i < s.loadout.weapons.Count ? s.loadout.weapons[i] : null)}")), Rich);
            if (GUILayout.Button("Copy to my presets", GUILayout.Width(140)))
            {
                var spec = ToSpec(a, s.loadout, s.Name + " (mine)", s.FuelRatio);
                SavedFor(a).Add(spec);
                Save(a);
                _status = $"Copied \"{s.Name}\".";
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(6);
        GUILayout.Label("<b>My presets</b> (saved at once; a squadron uses one with \"loadout\": \"its name\")", Rich);
        var mine = SavedFor(a);
        if (GUILayout.Button("New preset (best air-to-air on every station)", GUILayout.Width(320)))
        {
            var built = Build(a, new LoadoutSpec(), out float f, null);
            mine.Add(ToSpec(a, built, $"Preset {mine.Count + 1}", f));
            Save(a);
        }
        for (int p = 0; p < mine.Count; p++)
        {
            var spec = mine[p];
            bool changed = false;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            string name = GUILayout.TextField(spec.Name ?? "", GUILayout.MinWidth(180));
            if (name != spec.Name) { spec.Name = name; changed = true; }
            bool aces = GUILayout.Toggle(spec.Aces, "Random aces use this");
            if (aces != spec.Aces) { spec.Aces = aces; changed = true; }
            if (GUILayout.Button("Copy as squadron JSON", GUILayout.Width(170)))
            {
                GUIUtility.systemCopyBuffer = "\"loadout\": " + JsonConvert.SerializeObject(new { stations = spec.Stations, fuel = spec.Fuel }, Formatting.Indented);
                _status = "Copied to the clipboard: paste it into a squadron.json (or a member).";
            }
            if (GUILayout.Button("Delete", GUILayout.Width(60))) { mine.RemoveAt(p); Save(a); GUILayout.EndHorizontal(); GUILayout.EndVertical(); break; }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Fuel {(spec.Fuel ?? a.aircraftParameters.DefaultFuelLevel):P0}", GUILayout.Width(80));
            float fuel = GUILayout.HorizontalSlider(spec.Fuel ?? a.aircraftParameters.DefaultFuelLevel, 0.1f, 1f);
            if (Math.Abs(fuel - (spec.Fuel ?? -1f)) > 0.004f) { spec.Fuel = (float)Math.Round(fuel, 2); changed = true; }
            GUILayout.EndHorizontal();

            for (int i = 0; i < sets.Length; i++)
            {
                string station = StationName(sets, i);
                spec.Stations.TryGetValue(station, out string current);
                var options = new List<WeaponMount> { null };
                options.AddRange(sets[i].weaponOptions.Where(w => w != null));
                int at = Math.Max(0, options.FindIndex(w => w == null ? (current == null || current == "empty") : Matches(w, current ?? "")));
                GUILayout.BeginHorizontal();
                GUILayout.Label(station, GUILayout.Width(180));
                if (GUILayout.Button("◄", GUILayout.Width(28))) { at = (at - 1 + options.Count) % options.Count; spec.Stations[station] = WeaponName(options[at]); changed = true; }
                GUILayout.Label(WeaponName(options[at]), GUILayout.MinWidth(180));
                if (GUILayout.Button("►", GUILayout.Width(28))) { at = (at + 1) % options.Count; spec.Stations[station] = WeaponName(options[at]); changed = true; }
                GUILayout.EndHorizontal();
            }
            var problems = new List<string>();
            Build(a, spec, out _, problems);
            foreach (var pr in problems) GUILayout.Label($"<color=#FFB040>{pr}</color>", Rich);
            GUILayout.EndVertical();
            if (changed) Save(a);
        }
        if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
        GUILayout.EndVertical();
    }

    private static GUIStyle _rich;
    private static GUIStyle Rich => _rich ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
}

/// <summary>Record mode: a preset to show in the loadout editor, removed again afterwards.</summary>
internal static class LoadoutsTest
{
    private static AircraftDefinition _jet;

    internal static void Add()
    {
        _jet = Encyclopedia.i.aircraft.FirstOrDefault(a => a != null && a.unitName != null && a.unitName.Contains("Revoker"));
        if (_jet == null) return;
        var built = Loadouts.Build(_jet, new LoadoutSpec(), out float fuel, null);
        Loadouts.SavedFor(_jet).Add(Loadouts.ToSpec(_jet, built, "Record-mode test preset", fuel));
    }

    internal static void Remove()
    {
        if (_jet == null) return;
        Loadouts.SavedFor(_jet).RemoveAll(s => s.Name == "Record-mode test preset");
    }
}
