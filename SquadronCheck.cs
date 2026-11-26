using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Checks a squadron.json for the mistakes that otherwise fail silently (a misspelt field, a value the mod does not
/// know, a missing emblem or music file) and says what was meant. The problems show under the squadron in the F1
/// squadron menu and in the BepInEx log; nothing here stops a squadron from loading.
/// </summary>
internal static class SquadronCheck
{
    /// <summary>Files that could not be read at all (bad JSON), with the line and what is wrong; shown in the menu.</summary>
    internal static readonly List<string> FailedFiles = new();

    private static readonly string[] TopLevel =
    {
        "squadron", "faction", "title", "emblem", "emblemFps", "color", "introStyle", "introShots", "naming", "unit",
        "designation", "emblemShape", "formation", "tier", "aircraft", "livery", "loadout", "members", "lines", "music", "voice",
    };
    private static readonly string[] MemberKeys = { "callsign", "name", "lead", "skill", "aircraft", "loadout", "livery" };

    internal static List<string> Validate(SquadronDef def, JObject raw)
    {
        var p = new List<string>();
        foreach (var prop in raw.Properties())
            if (!TopLevel.Contains(prop.Name))
                p.Add($"Unknown field \"{prop.Name}\"{Suggest(prop.Name, TopLevel)}; it is ignored.");

        if (raw["members"] is JArray members)
            foreach (var m in members.OfType<JObject>())
                foreach (var prop in m.Properties())
                    if (!MemberKeys.Contains(prop.Name))
                        p.Add($"Unknown member field \"{prop.Name}\"{Suggest(prop.Name, MemberKeys)}; it is ignored.");

        CheckLoadout(p, def, def.Loadout, "\"loadout\"");
        if (def.Members != null)
            foreach (var m in def.Members.Where(x => x?.Loadout != null))
                CheckLoadout(p, def, m.Loadout, $"{m.Callsign ?? "a member"}'s \"loadout\"");
        CheckEnum<NamingScheme>(p, "naming", def.Naming);
        CheckEnum<CardStyle>(p, "introStyle", def.IntroStyle);
        CheckEnum<IntroShots>(p, "introShots", def.IntroShots);
        CheckEnum<EmblemShape>(p, "emblemShape", def.EmblemShape);
        CheckEnum<FormationKind>(p, "formation", def.Formation);

        if (!string.IsNullOrWhiteSpace(def.Faction) && Factions.Parse(def.Faction) == Side.Unknown)
            p.Add($"\"faction\": \"{def.Faction}\" is not a side; use \"BDF\" or \"PALA\" (or leave it out for either side).");
        if (def.Tier is < 1 or > 4)
            p.Add($"\"tier\": {def.Tier} is out of range; use 1, 2 or 3 (it is clamped).");
        foreach (var entry in Variants.Entries(def.Color))
            if (!Variants.IsRandom(entry) && !ColorUtility.TryParseHtmlString(entry, out _))
                p.Add($"\"color\": \"{entry}\" is not a colour; use a hex code such as \"#C9B26B\", \"random\", or several separated by commas.");

        if (def.Members is { Count: > 0 })
        {
            int leads = def.Members.Count(m => m != null && m.Lead);
            if (leads == 0) p.Add("No member has \"lead\": true; the first one leads.");
            if (leads > 1) p.Add($"{leads} members have \"lead\": true; only the first of them leads.");
            if (def.Members.Count > AceSpawner.MaxMembers) p.Add($"{def.Members.Count} members; only the first {AceSpawner.MaxMembers} fly.");
            foreach (var m in def.Members)
                if (m != null && m.Skill is < 0f or > 5f) p.Add($"Member {m.Callsign ?? "?"}: \"skill\": {m.Skill} is unusual (the tiers use 1.5, 2 and 2.5).");
        }

        if (!string.IsNullOrWhiteSpace(def.Emblem))
        {
            string path = Path.Combine(def.Folder ?? "", def.EmblemFile ?? "");
            if (!File.Exists(path) && !Directory.Exists(path)) p.Add($"\"emblem\": \"{def.Emblem}\" was not found in the squadron folder.");
            else if (File.Exists(path) && !(path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                p.Add($"\"emblem\": \"{def.Emblem}\" must be a .png or .jpg (or a folder of numbered .png frames).");
            else if (def.LoadedEmblem == null) p.Add($"\"emblem\": \"{def.Emblem}\" could not be read as an image.");
        }

        if (def.Music != null)
        {
            def.Music.TryGetValue("file", out var shared);
            var keys = Music.Slots.Concat(new[] { "file" }).ToArray();
            foreach (var pair in def.Music)
            {
                if (!keys.Contains(pair.Key))
                {
                    p.Add($"Unknown music slot \"{pair.Key}\"{Suggest(pair.Key, keys)}; slots are {string.Join(", ", Music.Slots)} (and \"file\" for one shared track).");
                    continue;
                }
                string error = null;
                var t = pair.Key == "file" ? new Music.TrackRef { Path = pair.Value } : Music.Parse(pair.Value, shared, out error);
                if (t == null) { p.Add($"Music \"{pair.Key}\": {error}."); continue; }
                string file = Path.Combine(def.Folder ?? "", t.Path ?? "");
                if (!File.Exists(file)) p.Add($"Music \"{pair.Key}\": \"{t.Path}\" was not found in the squadron folder.");
                else if (!new[] { ".ogg", ".wav", ".mp3" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    p.Add($"Music \"{pair.Key}\": \"{t.Path}\" must be .ogg, .wav or .mp3.");
            }
        }

        if (def.Voice != null)
        {
            VoiceLines.Read(def, p);
            foreach (var prop in def.Voice.Properties())
                if (prop.Name != "file" && !RandomNames.DefaultLines.ContainsKey(prop.Name))
                    p.Add($"Unknown voice key \"{prop.Name}\"{Suggest(prop.Name, RandomNames.DefaultLines.Keys)}; it is never played.");
        }

        if (def.Lines != null)
            foreach (var key in def.Lines.Keys)
                if (!RandomNames.DefaultLines.ContainsKey(key))
                    p.Add($"Unknown line key \"{key}\"{Suggest(key, RandomNames.DefaultLines.Keys)}; it is never said.");

        CheckLivery(p, def.Livery, "\"livery\"");
        if (def.Members != null)
            foreach (var m in def.Members.Where(x => x?.Livery != null))
                CheckLivery(p, m.Livery, $"{m.Callsign ?? "a member"}'s \"livery\"");
        return p;
    }

    /// <summary>A loadout checked against the squadron's first installed jet (stations and weapons differ per jet).</summary>
    private static void CheckLoadout(List<string> p, SquadronDef def, JToken token, string what)
    {
        if (token == null || token.Type == JTokenType.Null) return;
        AircraftDefinition jet;
        try { jet = AceSpawner.InstalledAirframe(def); } catch { return; }
        if (jet == null) return; // aircraft not loaded yet, or none installed (reported elsewhere)
        if (token.Type == JTokenType.String)
        {
            string name = token.ToString();
            if (name.Length == 0 || name.Equals("default", StringComparison.OrdinalIgnoreCase)) return;
            if (!Loadouts.TryNamed(jet, name, out _, out _))
                p.Add($"{what}: \"{name}\" is not a loadout of the {jet.unitName}; it has {string.Join(", ", Loadouts.Standard(jet).Select(s => s.Name).Concat(Loadouts.SavedFor(jet).Select(s => s.Name)))}.");
            return;
        }
        if (token.Type != JTokenType.Object) { p.Add($"{what} should be a loadout name or {{ \"stations\": {{ ... }} }}."); return; }
        try
        {
            var spec = token.ToObject<LoadoutSpec>();
            var problems = new List<string>();
            Loadouts.Build(jet, spec, out _, problems);
            foreach (var pr in problems) p.Add($"{what} on the {jet.unitName}: {pr}.");
        }
        catch (Exception ex) { p.Add($"{what} could not be read: {ex.Message}"); }
    }

    private static void CheckLivery(List<string> p, JToken token, string what)
    {
        if (token == null || token.Type == JTokenType.Null) return;
        if (token.Type != JTokenType.Object && token.Type != JTokenType.Array) { p.Add($"{what} should be {{ \"workshop\": id }} (or builtin/appdata), or a list of those."); return; }
        foreach (var e in Liveries.Entries(token))
        {
            if (!e.Properties().Any(x => x.Name is "builtin" or "appdata" or "workshop"))
                p.Add($"{what}: each entry needs one of \"builtin\", \"appdata\" or \"workshop\".");
            else if (e.TryGetValue("workshop", out var ws))
            {
                Liveries.WorkshopAircraft(ws.ToString(), out bool installed);
                if (!installed) p.Add($"{what}: workshop livery {ws} is not installed. Subscribe to it on the Steam Workshop (until then a random livery is used).");
            }
        }
    }

    private static void CheckEnum<T>(List<string> p, string field, string value) where T : struct, Enum
    {
        var names = Enum.GetNames(typeof(T)).Select(n => n.ToLowerInvariant()).Where(n => n is not "random" and not "match").ToList();
        names.Add("random");
        // A comma-separated list ("headon, lead") is fine: each entry is checked on its own.
        foreach (var entry in Variants.Entries(value))
        {
            if (Variants.IsRandom(entry) || Variants.TryParse(entry, out T _)) continue;
            p.Add($"\"{field}\": \"{entry}\" is not known{Suggest(entry, names)}; use one of {string.Join(", ", names)}, or several separated by commas (it is skipped).");
        }
    }

    /// <summary>" (did you mean "x"?)" for the closest known name, if one is close.</summary>
    private static string Suggest(string got, IEnumerable<string> known)
    {
        string squashed = got.Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        string best = null;
        int bestD = int.MaxValue;
        foreach (var k in known)
        {
            int d = Distance(squashed, k.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = k; }
        }
        return best != null && bestD <= Math.Max(2, got.Length / 3) ? $" (did you mean \"{best}\"?)" : "";
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
