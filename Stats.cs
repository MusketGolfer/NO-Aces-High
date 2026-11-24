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
        // For its kill marking: the squadron's emblem, made again from these (or its handmade folder).
        public string Colour, Shape, Side, Key;
    }

    internal sealed class Record
    {
        public int Encounters, Beaten, DrivenOff, Escaped, AcesDowned, AcesDownedByOthers, TimesDowned;
        public int TierThreeDowned;
        public float FastestWin;
        public string FastestWinSquadron;
        public Dictionary<string, SquadronRecord> Squadrons = new();
        public Dictionary<string, string> Medals = new(); // medal id -> date earned
    }

    // ---- medals -----------------------------------------------------------------------------------------

    internal sealed class Medal
    {
        public string Id, Name, How;
        public Func<Record, Win, bool> Earned;
    }

    /// <summary>The fight just won, for medals that depend on it (null when checking after something else).</summary>
    internal sealed class Win
    {
        public string Squadron;
        public float Seconds;
        public int Aces, Mine, Tier;
        public bool HadDownedYou;
    }

    internal static readonly Medal[] Medals =
    {
        new() { Id = "first-ace", Name = "First Blood", How = "Shoot down an ace.", Earned = (r, w) => r.AcesDowned >= 1 },
        new() { Id = "giant", Name = "Giant Killer", How = "Shoot down a tier 3 ace.", Earned = (r, w) => r.TierThreeDowned >= 1 },
        new() { Id = "ace-hunter", Name = "Ace Hunter", How = "Shoot down 10 aces.", Earned = (r, w) => r.AcesDowned >= 10 },
        new() { Id = "first-squadron", Name = "Squadron Breaker", How = "Beat a squadron (every ace down).", Earned = (r, w) => r.Beaten >= 1 },
        new() { Id = "lightning", Name = "Lightning Strike", How = "Beat a squadron in under 3 minutes.", Earned = (r, w) => w != null && w.Seconds < 180f },
        new() { Id = "lone-wolf", Name = "Lone Wolf", How = "Shoot down every ace of a squadron yourself.", Earned = (r, w) => w != null && w.Aces >= 2 && w.Mine >= w.Aces },
        new() { Id = "odds", Name = "Against the Odds", How = "Beat a squadron of 8 or more.", Earned = (r, w) => w != null && w.Aces >= 8 },
        new() { Id = "top-tier", Name = "Crown of Aces", How = "Beat a tier 3 squadron.", Earned = (r, w) => w != null && w.Tier >= 3 },
        new() { Id = "nemesis", Name = "Score Settled", How = "Beat a squadron that has shot you down before.", Earned = (r, w) => w != null && w.HadDownedYou },
        new() { Id = "gallery", Name = "Rogues' Gallery", How = "Beat 5 different squadrons.", Earned = (r, w) => r.Squadrons.Count(s => s.Value.Beaten > 0) >= 5 },
    };

    /// <summary>Awards any medals now earned; returns the new ones.</summary>
    private static List<Medal> Award(Win win)
    {
        var fresh = new List<Medal>();
        foreach (var m in Medals)
        {
            if (Current.Medals.ContainsKey(m.Id)) continue;
            bool earned;
            try { earned = m.Earned(Current, win); } catch { earned = false; }
            if (!earned) continue;
            Current.Medals[m.Id] = DateTime.Now.ToString("yyyy-MM-dd");
            fresh.Add(m);
            Plugin.Log.LogInfo($"Medal earned: {m.Name} ({m.How})");
        }
        return fresh;
    }

    private static readonly Dictionary<ActiveEncounter, int> MineThisFight = new();

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
            _r.Medals ??= new Dictionary<string, string>();
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
        s.Colour = "#" + ColorUtility.ToHtmlStringRGB(enc.Colour);
        s.Shape = enc.Shape.ToString();
        s.Side = enc.Side.ToString();
        s.Key = enc.Def is { Handmade: true } ? enc.Def.Key : null;
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
            MineThisFight[enc] = MineThisFight.TryGetValue(enc, out int n) ? n + 1 : 1;
            var fresh = Award(null);
            foreach (var m in fresh) Chatter.ShowLocal("Medal", new Color(1f, 0.8f, 0.3f), $"{m.Name}: {m.How}");
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
            MineThisFight.TryGetValue(enc, out int mine);
            var win = new Win { Squadron = enc.SquadronName, Seconds = secs, Aces = enc.Members.Count, Mine = mine, Tier = enc.Tier, HadDownedYou = s.DownedYou > 0 };
            var fresh = Award(win);
            SquadronDown.Show(enc, secs, mine, enc.Members.Count, fresh.Select(m => m.Name).ToList());
        }
        else if (reason == "withdraw") Current.DrivenOff++;
        else Current.Escaped++;
        MineThisFight.Remove(enc);
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
        DrawMarkings(r);
        DrawMedals(r);
        var top = r.Squadrons.OrderByDescending(p => p.Value.Met).Take(6).ToList();
        if (top.Count > 0)
            GUILayout.Label("  " + string.Join("\n  ", top.Select(p =>
                $"{p.Key}: met {p.Value.Met}, beaten {p.Value.Beaten}, {p.Value.AcesDowned} downed by you, got you {p.Value.DownedYou}" +
                (p.Value.FastestWin > 0f ? $", best {Mmss(p.Value.FastestWin)}" : ""))));
    }

    // ---- kill markings and medals on the record page --------------------------------------------------

    private static readonly Dictionary<string, Texture2D> Markings = new();

    /// <summary>A beaten squadron's emblem: its handmade artwork, else made again from its name, colour and shape.</summary>
    internal static Texture2D MarkingFor(string name, SquadronRecord s)
    {
        if (Markings.TryGetValue(name, out var cached)) return cached;
        Texture2D tex = null;
        try
        {
            var handmade = s.Key != null ? NOAcesHigh.Squadrons.Handmade.FirstOrDefault(d => d.Key == s.Key) : null;
            tex = handmade?.LoadedEmblem?.FrameAt(0f);
            if (tex == null)
            {
                ColorUtility.TryParseHtmlString(s.Colour ?? "#C9B26B", out var colour);
                Enum.TryParse(s.Shape ?? "Roundel", out EmblemShape shape);
                Enum.TryParse(s.Side ?? "Unknown", out Side side);
                tex = Emblem.Generate(name, colour.a > 0f ? colour : new Color(0.79f, 0.7f, 0.42f), shape, side)?.FrameAt(0f);
            }
        }
        catch { /* no marking */ }
        Markings[name] = tex;
        return tex;
    }

    private static void DrawMarkings(Record r)
    {
        var beaten = r.Squadrons.Where(p => p.Value.Beaten > 0).OrderByDescending(p => p.Value.Beaten).ToList();
        if (beaten.Count == 0) return;
        GUILayout.Label("<b>Kill markings</b>", Rich);
        GUILayout.BeginHorizontal();
        int n = 0;
        foreach (var p in beaten)
        {
            if (n > 0 && n % 10 == 0) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            var tex = MarkingFor(p.Key, p.Value);
            string tip = $"{p.Key}: beaten {p.Value.Beaten}x" + (p.Value.FastestWin > 0f ? $", best {Mmss(p.Value.FastestWin)}" : "");
            GUILayout.BeginVertical(GUILayout.Width(52));
            if (tex != null) GUILayout.Label(new GUIContent(tex, tip), GUILayout.Width(48), GUILayout.Height(48));
            else GUILayout.Label(new GUIContent("✕", tip), GUILayout.Width(48), GUILayout.Height(48));
            GUILayout.Label(p.Value.Beaten > 1 ? $"x{p.Value.Beaten}" : " ", GUILayout.Width(48));
            GUILayout.EndVertical();
            n++;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private static void DrawMedals(Record r)
    {
        GUILayout.Label($"<b>Medals</b> ({r.Medals.Count}/{Medals.Length})", Rich);
        GUILayout.BeginHorizontal();
        int n = 0;
        foreach (var m in Medals)
        {
            if (n > 0 && n % 5 == 0) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            bool has = r.Medals.TryGetValue(m.Id, out string date);
            var keep = GUI.color;
            GUI.color = has ? new Color(1f, 0.82f, 0.35f) : new Color(1f, 1f, 1f, 0.35f);
            GUILayout.Label(new GUIContent((has ? "★ " : "☆ ") + m.Name, m.How + (has ? $" Earned {date}." : " Not earned yet.")), GUILayout.Width(150));
            GUI.color = keep;
            n++;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private static GUIStyle _rich;
    private static GUIStyle Rich => _rich ??= new GUIStyle(GUI.skin.label) { richText = true };
}
