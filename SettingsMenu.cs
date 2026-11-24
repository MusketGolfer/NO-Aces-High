using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

internal enum DifficultyPreset { Custom, Rookie, Veteran, Ace, Nightmare }

/// <summary>
/// The settings page's layout and its extras: section order and entry order, the overview at the top (your record,
/// difficulty presets, saved profiles, this-mission switch), and the collapsed "Experimental and testing" section at
/// the bottom. Settings that moved section keep their values (read back from the old section on first load).
/// </summary>
internal static class SettingsMenu
{
    internal const string Top = "0. NO Aces High";
    internal const string Experimental = "9. Experimental and testing";

    /// <summary>Display order inside each section (first = top). Entries not listed keep their own order below these.</summary>
    private static readonly string[] Order =
    {
        // 0
        "Overview", "Enabled",
        // 1. Encounters
        "MaxEncountersPerMission", "ThreatToUnlock", "RollChance", "SecondsBetweenRolls", "CooldownSeconds", "ThreatPerBaseLost",
        "HandmadeSquadronChance", "MaxAcesPerSquadron", "SpawnDistanceKm", "VectorUntilKm", "Multiplayer", "AcesInPvp",
        // 2. Difficulty
        "MaxTier", "AceSkillMultiplier", "HuntMode", "FullAggression", "PostStallManeuvers", "PostStallMinTier", "AceFlareMultiplier",
        "MissileRangeShare", "EnergyFloor", "WingmanBracket", "LiftLimiter", "Tier2GLimit", "Tier3GLimit", "Tier3AoALimit", "PostStallMaxG",
        // 3. Tactics
        "AcesHoldFireDuringIntro", "CounterMissiles", "BeamRadarMissiles", "AlwaysJam", "GoDark", "ParthianShots", "RetreatWhenSpent",
        "RetreatFuelShare", "UnlimitedPower",
        // 4. Squadrons
        "Installed", "Naming", "TitleCard", "IntroShots", "EmblemShape", "Formation", "HoldFormation", "FormationSpacing", "FormationBreakKm",
        // 5. Airframes
        "AllowedAirframes", "Loadouts",
        // 6. Intro
        "PlayIntro", "CinematicCamera", "SlowMotion", "MaxWaitSeconds", "PrewarmShots", "FadeCameraJumps",
        // 7. HUD
        "ThreatBar", "SquadronHealthBars", "RadioSubtitles", "ThreatBarX", "ThreatBarY", "ThreatBarScale", "ThreatBarRotation",
        "SquadronHealthBarsX", "SquadronHealthBarsY", "SquadronHealthBarsWidth", "SquadronHealthBarsScale", "SquadronHealthBarsRotation",
        // 8. Sound
        "Music", "IntroWaitsForMusic", "MaxIntroMusicWait", "VoiceLines", "VoiceVolume", "VoiceRadioFilter",
    };

    /// <summary>Fine placement numbers, shown only with the settings window's "Advanced settings" ticked.</summary>
    private static readonly HashSet<string> Advanced = new()
    {
        "ThreatBarX", "ThreatBarY", "ThreatBarScale", "ThreatBarRotation", "SquadronHealthBarsX", "SquadronHealthBarsY",
        "SquadronHealthBarsWidth", "SquadronHealthBarsScale", "SquadronHealthBarsRotation", "Tier3AoALimit", "EnergyFloor",
    };

    private static ConfigFile _config;
    private static bool _showExperimental, _showProfiles, _showStats = true;
    private static string _profileName = "";
    private static string _message = "";

    internal static ConfigEntry<bool> FirstRunShown;
    internal static ConfigEntry<bool> AcesInPvp;
    internal static ConfigEntry<int> MaxAcesPerSquadron;

    /// <summary>No aces for the rest of this mission (reset when the mission changes).</summary>
    internal static bool SkipThisMission;

    internal static void Bind(ConfigFile config)
    {
        _config = config;
        config.Bind(Top, "Overview", "", new ConfigDescription("Your record, difficulty presets and saved settings.", null,
            new ConfigurationManagerAttributes { CustomDrawer = DrawOverview, HideDefaultButton = true, HideSettingName = true }));
        FirstRunShown = config.Bind(Top, "FirstRunShown", false, new ConfigDescription("The welcome message has been shown.", null,
            new ConfigurationManagerAttributes { Browsable = false }));
        AcesInPvp = config.Bind("1. Encounters", "AcesInPvp", false, new ConfigDescription(
            "Run encounters in multiplayer games where human players fly on more than one side (player versus player). Off by default: " +
            "the aces would pick on one side.", null, new ConfigurationManagerAttributes()));
        MaxAcesPerSquadron = config.Bind("1. Encounters", "MaxAcesPerSquadron", 16, new ConfigDescription(
            "Most aces any squadron may launch with (handmade squadrons list up to 16). Lower it on a slower PC: every ace is a full aircraft " +
            "with its own physics and AI.", new AcceptableValueRange<int>(1, 16), new ConfigurationManagerAttributes()));
    }

    /// <summary>After everything is bound: order, advanced flags, the experimental section folded away, old values carried over.</summary>
    internal static void Arrange(ConfigFile config)
    {
        // Bound last: the settings window lists sections in the order they were first registered.
        config.Bind(Experimental, "Panel", "", new ConfigDescription("Experimental features and testing tools.", null,
            new ConfigurationManagerAttributes { CustomDrawer = DrawExperimental, HideDefaultButton = true, HideSettingName = true }));
        foreach (var pair in config)
        {
            var entry = pair.Value;
            var attrs = entry.Description.Tags.OfType<ConfigurationManagerAttributes>().FirstOrDefault();
            if (attrs == null) continue;
            int i = Array.IndexOf(Order, pair.Key.Key);
            if (i >= 0) attrs.Order = 1000 - i;
            if (Advanced.Contains(pair.Key.Key)) attrs.IsAdvanced = true;
            if (pair.Key.Section == Experimental && pair.Key.Key != "Panel") attrs.Browsable = false;
        }
        Migrate(config);
    }

    /// <summary>Settings saved under an older section name take their old value.</summary>
    private static void Migrate(ConfigFile config)
    {
        try
        {
            var prop = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
            if (prop?.GetValue(config) is not Dictionary<ConfigDefinition, string> orphans || orphans.Count == 0) return;
            int moved = 0;
            foreach (var pair in config)
            {
                var old = orphans.Keys.FirstOrDefault(k => k.Key == pair.Key.Key && k.Section != pair.Key.Section);
                if (old == null) continue;
                try { pair.Value.SetSerializedValue(orphans[old]); moved++; } catch { /* old value no longer valid */ }
                orphans.Remove(old);
            }
            if (moved > 0)
            {
                config.Save();
                Plugin.Log.LogInfo($"Settings: {moved} setting(s) carried over to their new sections.");
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Settings: could not carry old settings over: " + ex.Message); }
    }

    // ---- presets ----------------------------------------------------------------------------------------

    private static readonly Dictionary<DifficultyPreset, Dictionary<string, object>> Presets = new()
    {
        [DifficultyPreset.Rookie] = new()
        {
            ["MaxTier"] = 1, ["AceSkillMultiplier"] = 0.7f, ["HuntMode"] = false, ["FullAggression"] = false, ["PostStallManeuvers"] = false,
            ["PostStallMinTier"] = 3, ["AceFlareMultiplier"] = 1.5f, ["MissileRangeShare"] = 0.5f, ["LiftLimiter"] = false, ["CounterMissiles"] = false,
            ["BeamRadarMissiles"] = false, ["AlwaysJam"] = false, ["GoDark"] = false, ["ParthianShots"] = false, ["UnlimitedPower"] = false,
            ["RollChance"] = 0.25f, ["MaxEncountersPerMission"] = 1,
        },
        [DifficultyPreset.Veteran] = new()
        {
            ["MaxTier"] = 2, ["AceSkillMultiplier"] = 0.85f, ["HuntMode"] = true, ["FullAggression"] = true, ["PostStallManeuvers"] = true,
            ["PostStallMinTier"] = 3, ["AceFlareMultiplier"] = 2.5f, ["MissileRangeShare"] = 0.6f, ["LiftLimiter"] = true, ["CounterMissiles"] = false,
            ["BeamRadarMissiles"] = true, ["AlwaysJam"] = true, ["GoDark"] = true, ["ParthianShots"] = false, ["UnlimitedPower"] = true,
            ["RollChance"] = 0.3f, ["MaxEncountersPerMission"] = 1,
        },
        [DifficultyPreset.Ace] = new() // the defaults
        {
            ["MaxTier"] = 3, ["AceSkillMultiplier"] = 1f, ["HuntMode"] = true, ["FullAggression"] = true, ["PostStallManeuvers"] = true,
            ["PostStallMinTier"] = 3, ["AceFlareMultiplier"] = 4f, ["MissileRangeShare"] = 0.6f, ["LiftLimiter"] = true, ["CounterMissiles"] = true,
            ["BeamRadarMissiles"] = true, ["AlwaysJam"] = true, ["GoDark"] = true, ["ParthianShots"] = true, ["UnlimitedPower"] = true,
            ["RollChance"] = 0.35f, ["MaxEncountersPerMission"] = 1,
        },
        [DifficultyPreset.Nightmare] = new()
        {
            ["MaxTier"] = 3, ["AceSkillMultiplier"] = 1.5f, ["HuntMode"] = true, ["FullAggression"] = true, ["PostStallManeuvers"] = true,
            ["PostStallMinTier"] = 2, ["AceFlareMultiplier"] = 6f, ["MissileRangeShare"] = 0.75f, ["LiftLimiter"] = true, ["CounterMissiles"] = true,
            ["BeamRadarMissiles"] = true, ["AlwaysJam"] = true, ["GoDark"] = true, ["ParthianShots"] = true, ["UnlimitedPower"] = true,
            ["RollChance"] = 0.5f, ["MaxEncountersPerMission"] = 2,
        },
    };

    private static readonly Dictionary<DifficultyPreset, string> PresetNotes = new()
    {
        [DifficultyPreset.Rookie] = "Tier 1 only, stock-like flying, no tricks.",
        [DifficultyPreset.Veteran] = "Up to tier 2, the hunt and jammers, no post-stall aces.",
        [DifficultyPreset.Ace] = "The intended experience (the defaults).",
        [DifficultyPreset.Nightmare] = "Sharper, braver, post-stall from tier 2, two squadrons a mission.",
    };

    private static ConfigEntryBase Find(string key) => _config?.FirstOrDefault(p => p.Key.Key == key).Value;

    private static DifficultyPreset Matching()
    {
        foreach (var preset in Presets)
            if (preset.Value.All(kv => Find(kv.Key) is { } e && Equals(e.BoxedValue, Convert.ChangeType(kv.Value, e.SettingType))))
                return preset.Key;
        return DifficultyPreset.Custom;
    }

    private static void Apply(DifficultyPreset preset)
    {
        foreach (var kv in Presets[preset])
        {
            var e = Find(kv.Key);
            if (e != null) e.BoxedValue = Convert.ChangeType(kv.Value, e.SettingType);
        }
        _message = $"{preset} applied.";
    }

    // ---- profiles -----------------------------------------------------------------------------------------

    private static string ProfileFolder => Path.Combine(BepInEx.Paths.ConfigPath, "NOAcesHigh", "Profiles");

    private static IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>> Saveable() =>
        _config.Where(p => p.Key.Key is not ("Overview" or "Panel" or "Installed" or "FirstRunShown" or "Loadouts"));

    private static void SaveProfile(string name)
    {
        name = string.Concat((name ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) { _message = "Type a name for the profile first."; return; }
        Directory.CreateDirectory(ProfileFolder);
        var lines = Saveable().Select(p => $"{p.Key.Section}|{p.Key.Key}={p.Value.GetSerializedValue().Replace("\n", "\\n")}");
        File.WriteAllLines(Path.Combine(ProfileFolder, name + ".txt"), lines);
        _message = $"Saved \"{name}\".";
    }

    private static void LoadProfile(string file)
    {
        int n = 0;
        foreach (var line in File.ReadAllLines(file))
        {
            int bar = line.IndexOf('|'), eq = line.IndexOf('=');
            if (bar < 0 || eq < bar) continue;
            string key = line.Substring(bar + 1, eq - bar - 1);
            var e = Find(key);
            if (e == null) continue;
            try { e.SetSerializedValue(line.Substring(eq + 1).Replace("\\n", "\n")); n++; } catch { /* skip a bad value */ }
        }
        _message = $"Loaded \"{Path.GetFileNameWithoutExtension(file)}\" ({n} settings).";
    }

    // ---- drawing --------------------------------------------------------------------------------------------

    private static GUIStyle _rich;
    private static GUIStyle Rich => _rich ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };

    private static void DrawOverview(ConfigEntryBase _)
    {
        GUILayout.BeginVertical();

        _showStats = Foldout(_showStats, "Your record against the aces");
        if (_showStats)
        {
            Stats.Draw();
            if (GUILayout.Button("Reset record", GUILayout.Width(120))) { Stats.ResetAll(); _message = "Record reset."; }
        }

        GUILayout.Space(6);
        var current = Matching();
        GUILayout.Label($"<b>Difficulty</b>: {current}" + (current == DifficultyPreset.Custom ? " (your own mix)" : $" - {PresetNotes[current]}"), Rich);
        GUILayout.BeginHorizontal();
        foreach (DifficultyPreset p in Enum.GetValues(typeof(DifficultyPreset)))
        {
            if (p == DifficultyPreset.Custom) continue;
            var keep = GUI.color;
            if (p == current) GUI.color = new Color(1f, 0.85f, 0.4f);
            if (GUILayout.Button(new GUIContent(p.ToString(), PresetNotes[p]))) Apply(p);
            GUI.color = keep;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        _showProfiles = Foldout(_showProfiles, "Saved settings (profiles)");
        if (_showProfiles)
        {
            GUILayout.BeginHorizontal();
            _profileName = GUILayout.TextField(_profileName, GUILayout.MinWidth(160));
            if (GUILayout.Button("Save current settings", GUILayout.Width(170))) SaveProfile(_profileName);
            GUILayout.EndHorizontal();
            if (Directory.Exists(ProfileFolder))
                foreach (var file in Directory.GetFiles(ProfileFolder, "*.txt").OrderBy(f => f))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Path.GetFileNameWithoutExtension(file));
                    if (GUILayout.Button("Load", GUILayout.Width(60))) LoadProfile(file);
                    if (GUILayout.Button("Delete", GUILayout.Width(60))) { File.Delete(file); _message = "Profile deleted."; }
                    GUILayout.EndHorizontal();
                }
        }

        GUILayout.Space(6);
        if (Session.Authoritative && Session.InMission || SkipThisMission)
        {
            string label = SkipThisMission ? "Aces are off for this mission (click to allow them again)" : "No aces for the rest of this mission";
            if (GUILayout.Button(label)) SkipThisMission = !SkipThisMission;
        }
        if (Patches.Failed.Count > 0)
            GUILayout.Label($"<color=#FFB040>Some features are off: the game has changed since this version ({string.Join(", ", Patches.Failed)}). Look for an update.</color>", Rich);
        if (!string.IsNullOrEmpty(_message)) GUILayout.Label(_message);
        GUILayout.EndVertical();
    }

    private static bool Foldout(bool open, string title) =>
        GUILayout.Toggle(open, (open ? "▼ " : "► ") + title, GUI.skin.button, GUILayout.ExpandWidth(true)) ;

    private static void DrawExperimental(ConfigEntryBase _)
    {
        GUILayout.BeginVertical();
        _showExperimental = Foldout(_showExperimental, "Experimental features and testing tools");
        if (_showExperimental)
        {
            GUILayout.Label("These are unfinished or for testing; they may misbehave.", Rich);
            foreach (var pair in _config.Where(p => p.Key.Section == Experimental && p.Key.Key != "Panel").OrderBy(p => p.Key.Key))
                DrawEntry(pair.Value);
            GUILayout.Space(6);
            Plugin.DrawDebugTools();
        }
        GUILayout.EndVertical();
    }

    /// <summary>A plain editor for one setting: toggle, cycling button, slider or text box, with its description as a tooltip.</summary>
    private static void DrawEntry(ConfigEntryBase e)
    {
        string desc = e.Description?.Description ?? "";
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(e.Definition.Key, desc), GUILayout.Width(200));
        object v = e.BoxedValue;
        if (e.SettingType == typeof(bool))
        {
            bool b = GUILayout.Toggle((bool)v, (bool)v ? "On" : "Off");
            if (b != (bool)v) e.BoxedValue = b;
        }
        else if (e.SettingType.IsEnum)
        {
            if (GUILayout.Button(v.ToString()))
            {
                var all = Enum.GetValues(e.SettingType);
                int i = Array.IndexOf(all, v);
                e.BoxedValue = all.GetValue((i + 1) % all.Length);
            }
        }
        else if (e.SettingType == typeof(float) && e.Description?.AcceptableValues is AcceptableValueRange<float> fr)
        {
            float f = GUILayout.HorizontalSlider((float)v, fr.MinValue, fr.MaxValue);
            GUILayout.Label(f.ToString("0.##"), GUILayout.Width(50));
            if (Math.Abs(f - (float)v) > 1e-4f) e.BoxedValue = f;
        }
        else
        {
            string s = GUILayout.TextField(e.GetSerializedValue());
            if (s != e.GetSerializedValue()) try { e.SetSerializedValue(s); } catch { /* typing */ }
        }
        GUILayout.EndHorizontal();
        if (desc.Length > 0) GUILayout.Label($"<size=11>{desc}</size>", Rich);
    }

    // ---- welcome --------------------------------------------------------------------------------------------

    /// <summary>Once, the first time you fly with the mod: where its settings and squadron folder are.</summary>
    internal static void Tick()
    {
        if (FirstRunShown == null || FirstRunShown.Value || Session.LocalAircraft == null) return;
        FirstRunShown.Value = true;
        Chatter.ShowLocal("NO Aces High", new Color(1f, 0.8f, 0.3f),
            "Enemy ace squadrons may come for you once your side makes enough noise. Settings: F1, then NO Aces High. " +
            "Your own squadrons go in BepInEx/config/NOAcesHigh/Squadrons (see the README there).");
    }
}
