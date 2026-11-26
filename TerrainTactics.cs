using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Aces using the ground. The game's missile seekers (radar, semi-active and IR) and its AI's weapon release all need a
/// clear line to the target past terrain and buildings (TargetCalc.LineOfSight / Unit.LineOfSight against
/// PhysicsLayers.StaticsMask), and radar returns weaken near the ground. So:
///
/// - Masking (tier 2 up): with a missile coming or someone behind pointing at it inside 6 km, an ace looks 2-6 km
///   ahead and to the sides for a low point the terrain hides from that threat, and flies there nap-of-the-earth
///   (the game's terrain following, about 60 m up), re-checking every 2 s, until the threat is gone or no cover is
///   found. Against a pursuer this is valley running: it keeps ridges between them.
/// - The ridge bait (tier 3 up, PostStall.Manoeuvre.Bait): see TryFindRidge.
///
/// Only over real terrain: the sea and flat ground offer no cover, and the search simply finds nothing there.
/// </summary>
internal static class TerrainTactics
{
    private sealed class State
    {
        public Unit Threat;
        public Vector3 Point;        // local position of the hiding spot (on the ground)
        public float Since, NextSearch, Until;
        public bool Logged;
    }

    private const float CoverHeight = 60f;   // m above the ground to fly at
    private const float MaxSeconds = 25f;

    private static readonly Dictionary<Aircraft, State> Masking = new();
    private static readonly Dictionary<Aircraft, float> NextCheck = new();

    /// <summary>Testing counters: masking runs started, and how many ended with the threat out of sight.</summary>
    internal static int MaskRuns, MaskHidden;

    internal static void Reset()
    {
        Masking.Clear();
        NextCheck.Clear();
    }

    /// <summary>Ground height (local y) under a point, or NaN over the sea / nothing.</summary>
    internal static float GroundY(Vector3 p)
    {
        if (Physics.Linecast(new Vector3(p.x, p.y + 6000f, p.z), new Vector3(p.x, Datum.LocalSeaY - 50f, p.z), out var hit, PhysicsLayers.StaticsMask))
            return hit.point.y > Datum.LocalSeaY + 1f ? hit.point.y : float.NaN;
        return float.NaN;
    }

    /// <summary>True when terrain blocks the line between the two points (the hit well short of the far end).</summary>
    internal static bool Blocked(Vector3 from, Vector3 to) =>
        Physics.Linecast(from, to, out var hit, PhysicsLayers.StaticsMask) && (to - hit.point).sqrMagnitude > 150f * 150f;

    internal static void Tick()
    {
        if (!Plugin.TerrainTactics.Value) { if (Masking.Count > 0) Masking.Clear(); return; }
        float now = Time.time;
        foreach (var dead in Masking.Keys.Where(k => k == null || k.disabled).ToList()) Masking.Remove(dead);
        foreach (var a in Encounters.AllAces.ToList())
        {
            if (a == null || a.disabled || a.rb == null) continue;
            if (NextCheck.TryGetValue(a, out var next) && now < next) continue;
            NextCheck[a] = now + 0.5f;
            try { Check(a, now); }
            catch (Exception ex) { Plugin.Log.LogWarning("Terrain tactics failed: " + ex.Message); Masking.Remove(a); }
        }
    }

    private static void Check(Aircraft a, float now)
    {
        bool eligible = Encounters.TryGetAce(a, out int tier, out _) && tier >= 2 && !Encounters.InFormationSlot(a) && !Encounters.IsFormationLead(a)
                        && !Encounters.IsEgressing(a) && !IntroGate.Holding && !(IntroDirector.Playing && IntroDirector.CameraCut);
        Unit threat = eligible ? FindThreat(a) : null;
        Masking.TryGetValue(a, out var st);
        if (threat == null)
        {
            if (st != null) End(a, st, "threat gone");
            return;
        }
        if (st != null && (now > st.Until || st.Threat != threat && now - st.Since > 3f)) { End(a, st, now > st.Until ? "time" : "new threat"); st = null; }
        if (st != null && now < st.NextSearch) return;

        Vector3 threatPos = threat.transform.position;
        Vector3 pos = a.transform.position;
        // Already hidden from it here: hold the present spot, low.
        if (st != null && Blocked(threatPos, pos))
        {
            st.NextSearch = now + 2f;
            return;
        }
        if (!TryFindCover(a, threatPos, out Vector3 point, out float dist))
        {
            if (st != null) End(a, st, "no cover left");
            return;
        }
        if (st == null)
        {
            st = new State { Threat = threat, Since = now, Until = now + MaxSeconds };
            Masking[a] = st;
            MaskRuns++;
            Plugin.Log.LogInfo($"{a.unitName}: terrain masking from {threat.unitName}, cover {dist:0} m away at {point.y - Datum.LocalSeaY:0} m.");
        }
        st.Threat = threat;
        st.Point = point;
        st.NextSearch = now + 2f;
    }

    private static void End(Aircraft a, State st, string why)
    {
        bool hidden = st.Threat != null && !st.Threat.disabled && Blocked(st.Threat.transform.position, a.transform.position);
        if (hidden) MaskHidden++;
        Plugin.Log.LogInfo($"{a.unitName}: terrain masking ended after {Time.time - st.Since:0.0} s ({why}){(hidden ? ", out of its sight" : "")}.");
        Masking.Remove(a);
    }

    /// <summary>A missile on its way (more than 3 s out), else a hostile behind inside 6 km pointing within 30 deg.</summary>
    private static Unit FindThreat(Aircraft a)
    {
        var mw = a.GetMissileWarningSystem();
        if (mw != null && mw.TryGetNearestIncoming(out Missile m) && m != null && !m.disabled)
        {
            Vector3 rel = m.transform.position - a.transform.position;
            float closing = m.rb != null ? -Vector3.Dot(m.rb.velocity - a.rb.velocity, rel.normalized) : 600f;
            if (closing > 50f && rel.magnitude / closing > 3f) return m;
        }
        Vector3 pos = a.transform.position, fwd = a.transform.forward;
        Aircraft best = null;
        float bestDist = 6000f;
        foreach (var h in Enumerable.Concat(Encounters.HuntedAircraft(), Encounters.PursuerCandidates()))
        {
            if (h == null || h.disabled || h == a) continue;
            Vector3 rel = h.transform.position - pos;
            float d = rel.magnitude;
            if (d >= bestDist || Vector3.Angle(fwd, rel) < 100f || Vector3.Angle(h.transform.forward, -rel) > 30f) continue;
            best = h;
            bestDist = d;
        }
        return best;
    }

    /// <summary>
    /// The nearest point 2-6 km out, within 100 deg either side of the nose, where the terrain hides an aircraft flying
    /// 60 m up from the threat. Nearer and straighter ahead scores better.
    /// </summary>
    private static bool TryFindCover(Aircraft a, Vector3 threatPos, out Vector3 point, out float dist)
    {
        point = Vector3.zero;
        dist = 0f;
        Vector3 pos = a.transform.position;
        Vector3 flat = a.transform.forward;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f) return false;
        flat.Normalize();
        float bestScore = float.MaxValue;
        for (int ang = -100; ang <= 100; ang += 20)
        {
            Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * flat;
            foreach (float d in new[] { 2000f, 3000f, 4500f, 6000f })
            {
                Vector3 p = pos + dir * d;
                float g = GroundY(p);
                if (float.IsNaN(g)) continue;
                Vector3 spot = new Vector3(p.x, g + CoverHeight, p.z);
                if (!Blocked(threatPos, spot)) continue;
                float score = d + Mathf.Abs(ang) * 25f;
                if (score < bestScore)
                {
                    bestScore = score;
                    point = new Vector3(p.x, g, p.z);
                    dist = d;
                }
            }
        }
        return bestScore < float.MaxValue;
    }

    /// <summary>Steering for the AI while masking: where to fly, how high above the ground, full power.</summary>
    internal static bool TryGetSteer(Aircraft a, out GlobalPosition destination, out float height)
    {
        destination = default;
        height = CoverHeight;
        if (a == null || !Masking.TryGetValue(a, out var st)) return false;
        destination = new Vector3(st.Point.x, st.Point.y + CoverHeight, st.Point.z).ToGlobalPosition();
        return true;
    }

    internal static bool IsMasking(Aircraft a) => a != null && Masking.ContainsKey(a);

    // ---- ridge bait ---------------------------------------------------------------------------------------

    /// <summary>
    /// For the ridge bait: rising ground dead ahead, 1.2-4 km out, that tops out 60-600 m above the ace inside the
    /// next 1.5 km. face = distance to where the ground first reaches the ace's height; crest = the highest ground
    /// (local y) within 1.5 km past that.
    /// </summary>
    internal static bool TryFindRidge(Aircraft a, out float face, out float crest)
    {
        face = 0f;
        crest = 0f;
        Vector3 pos = a.transform.position;
        Vector3 dir = a.rb.velocity;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1f) return false;
        dir.Normalize();
        float alt = pos.y;
        for (float d = 600f; d <= 4000f; d += 100f)
        {
            float g = GroundY(pos + dir * d);
            if (float.IsNaN(g)) return false; // the sea ahead
            if (g < alt - 20f) continue;
            if (d < 1200f) return false;      // already too close to set it up
            face = d;
            crest = g;
            for (float e = d; e <= d + 1500f; e += 100f)
            {
                float h = GroundY(pos + dir * e);
                if (!float.IsNaN(h)) crest = Mathf.Max(crest, h);
            }
            float rise = crest - alt;
            return rise >= 60f && rise <= 600f;
        }
        return false;
    }
}
