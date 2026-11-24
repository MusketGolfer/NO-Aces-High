using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Squadron liveries. "livery" is one entry or a list of them, and each entry is for one jet:
///
///   "livery": [ { "workshop": 3841481598 }, { "workshop": 3414171937 } ]
///
/// A Steam Workshop livery names its aircraft in its own files, so each jet the squadron flies picks the entry made
/// for it (a squadron with backup jets can list a livery per jet). An entry can name its jet with "aircraft" (needed
/// for appdata liveries, and to override). Entries with no known jet suit any jet, after the ones that name it.
/// Members can have their own "livery", written the same way.
/// </summary>
internal static class Liveries
{
    /// <summary>The game's Steam Workshop content folder (steamapps/workshop/content/2168680).</summary>
    internal static string WorkshopFolder
    {
        get
        {
            try
            {
                var game = Directory.GetParent(Application.dataPath); // ...\common\Nuclear Option
                var steamapps = game?.Parent?.Parent;                 // ...\steamapps
                return steamapps == null ? null : Path.Combine(steamapps.FullName, "workshop", "content", "2168680");
            }
            catch { return null; }
        }
    }

    private static readonly Dictionary<string, string> MetaAircraft = new();

    /// <summary>The aircraft a workshop livery is for, from its meta.json; null if not installed or not stated.</summary>
    internal static string WorkshopAircraft(string id, out bool installed)
    {
        installed = false;
        string folder = WorkshopFolder == null ? null : Path.Combine(WorkshopFolder, id);
        if (folder == null || !Directory.Exists(folder)) return null;
        installed = true;
        if (MetaAircraft.TryGetValue(id, out var cached)) return cached;
        string aircraft = null;
        try
        {
            string meta = Path.Combine(folder, "meta.json");
            if (File.Exists(meta)) aircraft = JObject.Parse(File.ReadAllText(meta)).Value<string>("Aircraft");
        }
        catch { /* no readable meta */ }
        MetaAircraft[id] = aircraft;
        return aircraft;
    }

    internal static IEnumerable<JObject> Entries(JToken token) =>
        token == null ? Enumerable.Empty<JObject>()
            : token.Type == JTokenType.Array ? token.Children<JObject>()
            : token is JObject o ? new[] { o } : Enumerable.Empty<JObject>();

    /// <summary>The jet an entry is for: its "aircraft", else a workshop livery's own; null = any jet.</summary>
    internal static string AircraftOf(JObject entry)
    {
        string named = entry.Value<string>("aircraft");
        if (!string.IsNullOrWhiteSpace(named)) return named;
        if (entry.TryGetValue("workshop", out var ws)) return WorkshopAircraft(ws.ToString(), out _);
        return null;
    }

    private static bool Fits(string wanted, AircraftDefinition a)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return false;
        string w = wanted.Trim();
        return string.Equals(a.unitName, w, StringComparison.OrdinalIgnoreCase)
               || string.Equals(AirframeChecklist.Key(a), w, StringComparison.OrdinalIgnoreCase)
               || (a.unitName ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0
               || w.IndexOf(a.unitName ?? "\u0000", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The entry for this jet: one made for it, else one for any jet; null if none fits.</summary>
    internal static JObject For(JToken token, AircraftDefinition a)
    {
        var all = Entries(token).ToList();
        return all.FirstOrDefault(e => Fits(AircraftOf(e), a)) ?? all.FirstOrDefault(e => AircraftOf(e) == null);
    }
}
