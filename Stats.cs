using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Your record against the aces, kept across missions in BepInEx/config/NOAcesHigh/stats.json and shown at the top of
/// the settings. Counted on this machine for the local player only: encounters that hunted your side while you flew,
/// squadrons beaten (every ace down) or driven off, aces you shot down (you did the most damage), times an ace shot you
/// down, and per squadron: met, beaten, aces downed, fastest win.
/// </summary>
internal static class Stats
{
    internal sealed class SquadronRecord
    {
        public int Met, Beaten, AcesDowned, DownedYou;
        public float FastestWin; // seconds, 0 = never beaten
        public string LastMet;
    }

    internal sealed class Record
    {
        public int Encounters, Beaten, DrivenOff, Escaped, AcesDowned, AcesDownedByOthers, TimesDowned;
        public int TierThreeDowned;
        public float FastestWin;
        public string FastestWinSquadron;
        public Dictionary<string, SquadronRecord> Squadrons = new();
    }

    private static Record _r;
    private static readonly HashSet<ActiveEncounter> Counted = new();
    private static readonly AccessTools.FieldRef<Unit, Dictionary<PersistentID, float>> Credit =
        AccessTools.FieldRefAccess<Unit, Dictionary<PersistentID, float>>("damageCredit");
    private static Aircraft _watchedPlayer;

    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "NOAcesHigh", "stats.json");

    internal static Record Current
    {
        get
        {
            if (_r != null) return _r;
            try { _r = File.Exists(FilePath) ? JsonConvert.DeserializeObject<Record>(File.ReadAllText(FilePath)) : null; }
            catch (Exception ex) { Plugin.Log.LogWarning("Stats: could not read stats.json, starting fresh: " + ex.Message); }
            _r ??= new Record();
            _r.Squadrons ??= new Dictionary<string, SquadronRecord>();
            return _r;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(Current, Formatting.Indented));
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Stats: could not save: " + ex.Message); }
    }

    internal static void ResetAll()
    {
        _r = new Record();
        Save();
    }

    private static bool Mine(ActiveEncounter enc) =>
        enc != null && Session.LocalHQ != null && enc.TargetHQ == Session.LocalHQ;

    private static SquadronRecord For(ActiveEncounter enc)
    {
        string name = enc.SquadronName ?? "?";
        if (!Current.Squadrons.TryGetValue(name, out var s)) Current.Squadrons[name] = s = new SquadronRecord();
        return s;
    }

    /// <summary>A squadron launched against the local player's side.</summary>
    internal static void OnLaunch(ActiveEncounter enc)
    {
        if (!Mine(enc) || !Counted.Add(enc)) return;
        Current.Encounters++;
        var s = For(enc);
        s.Met++;
        s.LastMet = DateTime.Now.ToString("yyyy-MM-dd");
        Save();
    }

    /// <summary>An ace went down: credited to the local player if they did the most damage to it.</summary>
    internal static void OnAceDown(ActiveEncounter enc, Aircraft ace, int tier)
    {
        if (!Mine(enc) || ace == null) return;
        var me = Session.LocalAircraft ?? _watchedPlayer;
        bool mine = false;
        try
        {
            var credit = Credit(ace);
            if (credit != null && credit.Count > 0 && me != null)
                mine = credit.OrderByDescending(c => c.Value).First().Key.Equals(me.persistentID);
        }
        catch { /* no credit data */ }
        if (mine)
        {
            Current.AcesDowned++;
            if (tier >= 3) Current.TierThreeDowned++;
            For(enc).AcesDowned++;
        }
        else Current.AcesDownedByOthers++;
        Save();
    }

    /// <summary>The encounter ended: beaten (all down), or the aces left.</summary>
    internal static void OnEnd(ActiveEncounter enc, string reason)
    {
        if (!Mine(enc) || !Counted.Contains(enc)) return;
        Counted.Remove(enc);
        var s = For(enc);
        if (reason == "defeated")
        {
            Current.Beaten++;
            s.Beaten++;
            float secs = Time.time - enc.StartTime;
            if (s.FastestWin <= 0f || secs < s.FastestWin) s.FastestWin = secs;
            if (Current.FastestWin <= 0f || secs < Current.FastestWin) { Current.FastestWin = secs; Current.FastestWinSquadron = enc.SquadronName; }
        }
        else if (reason == "withdraw") Current.DrivenOff++;
        else Current.Escaped++;
        Save();
    }

    /// <summary>Called every frame: notices the local player's jet going down to an ace.</summary>
    internal static void Tick()
    {
        var me = Session.LocalAircraft;
        if (me != null) { _watchedPlayer = me; return; }
        if (_watchedPlayer == null) return;
        var lost = _watchedPlayer;
        _watchedPlayer = null;
        try
        {
            var credit = Credit(lost);
            if (credit == null || credit.Count == 0) return;
            var top = credit.OrderByDescending(c => c.Value).First().Key;
            foreach (var enc in new[] { Encounters.Current, NetSync.Mirror })
            {
                if (enc == null || !Mine(enc)) continue;
                if (!enc.Members.Any(m => m.Aircraft != null && m.Aircraft.persistentID.Equals(top))) continue;
                Current.TimesDowned++;
                For(enc).DownedYou++;
                Save();
                return;
            }
        }
        catch { /* the jet is gone */ }
    }

    internal static string Mmss(float secs) => secs <= 0f ? "-" : $"{(int)(secs / 60)}:{(int)(secs % 60):00}";

    /// <summary>The block at the top of the settings.</summary>
    internal static void Draw()
    {
        var r = Current;
        GUILayout.Label($"<b>Your record</b>   {r.Encounters} squadrons faced · {r.Beaten} beaten · {r.DrivenOff} driven off · {r.Escaped} got away");
        GUILayout.Label($"Aces you shot down: {r.AcesDowned} ({r.TierThreeDowned} tier 3) · downed by others: {r.AcesDownedByOthers} · times an ace got you: {r.TimesDowned}" +
                        (r.FastestWin > 0f ? $" · fastest win {Mmss(r.FastestWin)} ({r.FastestWinSquadron})" : ""));
        var top = r.Squadrons.OrderByDescending(p => p.Value.Met).Take(6).ToList();
        if (top.Count > 0)
            GUILayout.Label("  " + string.Join("\n  ", top.Select(p =>
                $"{p.Key}: met {p.Value.Met}, beaten {p.Value.Beaten}, {p.Value.AcesDowned} downed by you, got you {p.Value.DownedYou}" +
                (p.Value.FastestWin > 0f ? $", best {Mmss(p.Value.FastestWin)}" : ""))));
    }
}
