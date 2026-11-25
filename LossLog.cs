using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Why an ace went down, in one log line: who did the most damage, what it was doing, and its last hits and lost parts.
/// The game itself records only damage credit, which a crash or an airframe that tears itself apart never gets.
/// </summary>
internal static class LossLog
{
    private sealed class Watch
    {
        public readonly List<string> Events = new();
        public float Start;
        public bool OverG;          // a part came off undamaged: the joints broke
        public bool Outside;        // something else damaged it
        public bool Impact;         // impact damage (the ground, the sea, another aircraft)
        public bool ImpactHigh;     // impact damage taken well above the ground: a blast's shockwave or a collision
    }

    private static readonly Dictionary<Aircraft, Watch> Watched = new();
    private static readonly AccessTools.FieldRef<Unit, Dictionary<PersistentID, float>> Credit =
        AccessTools.FieldRefAccess<Unit, Dictionary<PersistentID, float>>("damageCredit");

    /// <summary>Starts recording an ace's damage (called as it spawns).</summary>
    internal static void Track(Aircraft a)
    {
        if (a == null || Watched.ContainsKey(a)) return;
        foreach (var dead in Watched.Keys.Where(k => k == null).ToList()) Watched.Remove(dead);
        var w = new Watch { Start = Time.time };
        Watched[a] = w;
        if (a.partLookup == null) return;
        foreach (var part in a.partLookup)
        {
            if (part == null) continue;
            var p = part;
            p.onApplyDamage += e =>
            {
                float dmg = e.pierceDamage + e.blastDamage + e.fireDamage + e.impactDamage;
                if (dmg < 0.5f && !e.detached) return;
                if (e.detached && dmg < 0.5f) w.OverG = true;
                if (e.pierceDamage + e.blastDamage + e.fireDamage >= 0.5f) w.Outside = true;
                if (e.impactDamage >= 0.5f) w.Impact = true;
                if (e.impactDamage >= 0.5f && a != null && a.radarAlt > 50f) w.ImpactHigh = true;
                string what = e.detached && dmg < 0.5f ? "came off (joint broke)"
                    : $"{Kinds(e)}{(e.detached ? ", came off" : "")}, hp {e.hitPoints:0}";
                w.Events.Add($"t+{Time.time - w.Start:0.0}s {p.name} {what} ({(a != null ? a.gForce : 0f):0.0} g, {(a != null ? a.speed * 3.6f : 0f):0} km/h)");
                if (w.Events.Count > 8) w.Events.RemoveAt(0);
            };
        }
    }

    private static string Kinds(UnitPart.OnApplyDamage e)
    {
        var parts = new List<string>();
        if (e.pierceDamage >= 0.5f) parts.Add($"pierce {e.pierceDamage:0}");
        if (e.blastDamage >= 0.5f) parts.Add($"blast {e.blastDamage:0}");
        if (e.fireDamage >= 0.5f) parts.Add($"fire {e.fireDamage:0}");
        if (e.impactDamage >= 0.5f) parts.Add($"impact {e.impactDamage:0}");
        return string.Join(" ", parts);
    }

    /// <summary>Logs the cause of an ace's loss.</summary>
    internal static void Report(AceMember member)
    {
        try
        {
            var a = member?.Aircraft;
            Watched.TryGetValue(a, out var w);
            string credit = "none";
            string top = null;
            if (a != null)
            {
                var c = Credit(a);
                if (c != null && c.Count > 0)
                {
                    float total = c.Values.Sum();
                    credit = string.Join(", ", c.OrderByDescending(kv => kv.Value).Take(3).Select(kv =>
                        $"{Name(kv.Key)} {(total > 0f ? kv.Value / total : 0f):P0}"));
                    top = Name(c.OrderByDescending(kv => kv.Value).First().Key);
                }
            }
            string pilot = "?";
            try { var pl = a?.pilots != null && a.pilots.Length > 0 ? a.pilots[0] : null; pilot = pl == null ? "none" : pl.dead ? "dead" : pl.ejected ? "ejected" : "alive"; } catch { /* gone */ }

            // Missile and shell damage often arrives as "impact", so another unit's damage credit decides first.
            bool low = a != null && a.radarAlt < 15f;
            string verdict;
            if (w == null) verdict = "unknown (not watched)";
            else if (top != null && w.OverG) verdict = $"shot down by {top} (it broke up after the hits)";
            else if (top != null && !(low && !w.Outside && w.Events.Count == 0)) verdict = $"shot down by {top}";
            else if (w.OverG) verdict = "broke up under its own g";
            else if (w.ImpactHigh) verdict = "hit by a blast's shockwave or a collision (no damage credit: a missile bursting close counts as this)";
            else if (low || w.Impact) verdict = "hit the ground or the sea";
            else if (a == null) verdict = "despawned";
            else verdict = "no damage recorded (removed, or crashed out of view)";

            string state = a == null ? "" : $"; {a.speed * 3.6f:0} km/h, {a.radarAlt:0} m above ground, {a.gForce:0.0} g, flying {PostStall.Describe(a)}";
            Plugin.Log.LogInfo($"Loss: {member?.Callsign} {verdict}; pilot {pilot}; damage credit {credit}{state}; " +
                               $"last events: {(w == null || w.Events.Count == 0 ? "none" : string.Join(" | ", w.Events))}");
            if (a != null) Watched.Remove(a);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Loss report failed: " + ex.Message);
        }
    }

    private static string Name(PersistentID id)
    {
        try
        {
            if (UnitRegistry.TryGetUnit(id, out Unit u) && u != null)
            {
                string player = u is Aircraft ac ? Session.PlayerName(ac.Player) : null;
                return string.IsNullOrEmpty(player) ? u.unitName : $"{player} ({u.unitName})";
            }
        }
        catch { /* fall through */ }
        return "unit " + id;
    }
}
