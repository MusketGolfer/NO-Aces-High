using System;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Phase 3: how aces fly. Every patch acts only on aircraft of the active ace squadron while it is fighting.
///
/// - Limiter: tier 2/3 lift the fly-by-wire g limit (12 / 16 g, under the 20 g at which pilots start taking
///   damage) and tier 3 the AoA limiter, only above a radar-altitude floor.
/// - Aggression and energy: tier 2/3 steer with full aggression (effort > 1) while above a share of corner
///   speed; below it they keep the stock behaviour, which turns gentler and lets them rebuild speed.
/// - Missile discipline: against aircraft, hold missiles until inside a share of their max range.
/// - Bracket: wingmen closing on their target from far out come in 35 degrees off the line, alternating sides.
/// </summary>
internal static class AceFlight
{
    private const float LowFloor = 300f;         // m radar altitude: below this aces fly with stock limits
    private const float BracketBeyond = 6000f;   // m: wingmen bracket only while further out than this
    private const float BracketAngle = 35f;      // degrees off the line to the target

    private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> GLimit =
        AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>("gLimitPositive");
    private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> AlphaLimit =
        AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>("alphaLimiter");
    private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> StateAircraft =
        AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
    private static readonly AccessTools.FieldRef<PilotBaseState, GlobalPosition> StateDestination =
        AccessTools.FieldRefAccess<PilotBaseState, GlobalPosition>("destination");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, Unit> CurrentTarget =
        AccessTools.FieldRefAccess<AIPilotCombatModes, Unit>("currentTarget");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, float> TargetDist =
        AccessTools.FieldRefAccess<AIPilotCombatModes, float>("targetDist");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, float> LastFiredTime =
        AccessTools.FieldRefAccess<AIPilotCombatModes, float>("lastFiredTime");

    internal static void Apply(Harmony harmony)
    {
        void patch(string what, System.Reflection.MethodBase target, string prefix, string postfix) =>
            Patches.Patch(harmony, what, target, prefix, postfix, typeof(AceFlight));

        patch("ace limiter override",
            AccessTools.Method(typeof(ControlsFilter.FlyByWire), nameof(ControlsFilter.FlyByWire.Filter)),
            nameof(FlyByWirePrefix), nameof(FlyByWirePostfix));
        patch("ace aggression and energy",
            AccessTools.Method(typeof(AutopilotPlane), nameof(AutopilotPlane.AutoAim),
                new[] { typeof(GlobalPosition), typeof(bool), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(Vector3) }),
            nameof(AutoAimPrefix), null);
        patch("ace missile discipline",
            AccessTools.Method(typeof(AIPilotCombatModes), "UseMissiles"),
            nameof(UseMissilesPrefix), null);
        patch("ace wingman bracket",
            AccessTools.Method(typeof(AIPilotCombatModes), "FlyToTarget"),
            null, nameof(FlyToTargetPostfix));
    }

    // ---- Limiter ------------------------------------------------------------------------------------

    private struct Saved
    {
        public bool Changed;
        public float G, Alpha;
    }

    /// <summary>
    /// Lifts the g limit for tier 2/3 aces, but only as far as the airframe holds. Aircraft within 10 km of the
    /// camera fly "complex" physics (Aircraft.CheckPhysicsLod): every wing and tail surface is its own body on
    /// FixedJoints with a break force, and an over-g snaps them. Far from the camera the aircraft is one rigid body
    /// and cannot come apart. So:
    /// - while the intro camera is on the aces, no lift (they fly stock for the cameras);
    /// - in simple physics, the configured limit;
    /// - in complex physics, at most StructureMargin of the g the joints would break at, estimated live from
    ///   joint load / break force, never above the configured limit, and stock until there is an estimate;
    /// - any ace whose joints pass Emergency of their break load gets its limit cut to back off, tier 1 included.
    /// </summary>
    private static void FlyByWirePrefix(ControlsFilter.FlyByWire __instance, Aircraft aircraft, out Saved __state)
    {
        __state = default;
        try
        {
            if (!Encounters.TryGetAce(aircraft, out int tier, out _)) return;
            float stock = GLimit(__instance);
            float alpha = AlphaLimit(__instance);
            float g = stock, a = alpha;

            Structure s = aircraft.simplePhysics ? null : MeasureStructure(aircraft);
            bool lift = Plugin.LiftLimiter.Value && tier >= 2 && aircraft.radarAlt >= LowFloor && !IntroOnCamera();
            if (lift)
            {
                float wanted = Mathf.Max(stock, tier >= 4 ? Plugin.Tier3G.Value * 1.3f : tier >= 3 ? Plugin.Tier3G.Value : Plugin.Tier2G.Value);
                if (s == null) g = wanted;
                else if (s.BreakAt(aircraft.speed) > 0f) g = Mathf.Clamp(s.BreakAt(aircraft.speed) * StructureMargin, stock, wanted);
                if (tier >= 3 && g > stock) a = Mathf.Max(alpha, Plugin.Tier3Alpha.Value);
            }
            // The stock limit is no guarantee either: a Compass allows 8 g but sheds its wings near 4.5 g at 950 km/h.
            // Whoever is flying (the stock AI or ours), an ace never asks for more than the airframe takes at this speed.
            // Heading for the ground (under 8 s to impact), it may use most of what the wings take: losing a wing
            // and hitting the ground both end the same way, but a hard pull usually saves it.
            if (s != null && s.BreakAt(aircraft.speed) > 0f)
            {
                float sink = aircraft.rb != null ? -aircraft.rb.velocity.y : 0f;
                bool groundRush = sink > 1f && aircraft.radarAlt / sink < 8f;
                float margin = groundRush ? 0.9f : StructureMargin;
                g = Mathf.Min(g, Mathf.Max(groundRush ? 4f : MinG, s.BreakAt(aircraft.speed) * margin));
            }
            if (s != null && s.Stress > Emergency && aircraft.gForce > 1f)
            {
                // Close to breaking: ask for less than it is pulling now.
                g = Mathf.Min(g, Mathf.Max(2f, aircraft.gForce * (Emergency * 0.85f) / s.Stress));
                a = Mathf.Min(a, alpha);
            }
            if (Mathf.Approximately(g, stock) && Mathf.Approximately(a, alpha)) return;
            __state = new Saved { Changed = true, G = stock, Alpha = alpha };
            GLimit(__instance) = g;
            AlphaLimit(__instance) = a;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace limiter override failed: " + ex.Message);
        }
    }

    // ---- Structure ----------------------------------------------------------------------------------

    private const float StructureMargin = 0.7f; // fly at most this share of the estimated break g (the same margin as SafeG)
    private const float Emergency = 0.65f;      // joint load share at which the governor pulls back
    private const float MinG = 3f;              // never cap an ace below this (it has to be able to pull out of a dive)
    private const float SpeedRef = 220f;        // m/s (790 km/h); readings slower than this count as taken at it

    /// <summary>
    /// Joint load modelled as g x (a + b x speed^2): wing bending grows with g, and at high speed the dynamic pressure
    /// on the surfaces adds to it, so the same g breaks a jet faster. (Compasses that read 7 g at about 800 km/h shed
    /// their wings at 4.4-5.2 g at 950-1010 km/h.) Two coefficients, each the worst reading so far: Kg = stress per g,
    /// and Kq = stress per g per (m/s)^2, with readings below SpeedRef counted at SpeedRef. Until a fast reading
    /// exists, that makes the jet's limit fall with the square of speed above 790 km/h: cautious, but a jet flying
    /// that fast does not need to pull hard. Both decay 1% a second, down to 90% of the worst reading (so one spike
    /// does not cap it for good), and damage raises them.
    /// </summary>
    private sealed class Structure
    {
        public float Stress;        // highest joint load / break load this frame
        public float Kg, Kq, MaxKg, MaxKq;
        public float EstimateTime;
        public bool Logged;

        /// <summary>Estimated g at which the weakest joint goes at this speed (m/s); 0 = no estimate yet.</summary>
        public float BreakAt(float speed)
        {
            float k = Mathf.Max(Kg, Kq * speed * speed);
            return k > 0f ? 1f / k : 0f;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<Aircraft, Structure> Structures = new();

    internal static void Reset() => Structures.Clear();

    private static readonly AccessTools.FieldRef<FlareEjector, int> FlareMaxAmmo =
        AccessTools.FieldRefAccess<FlareEjector, int>("maxAmmo");

    /// <summary>Gives an ace's flare dispensers <paramref name="multiplier"/> times their load (rearming refills to it too).</summary>
    internal static void BoostFlares(Aircraft aircraft, float multiplier)
    {
        if (aircraft == null || multiplier <= 1f) return;
        try
        {
            int before = 0, after = 0;
            foreach (var ejector in aircraft.GetComponentsInChildren<FlareEjector>(true))
            {
                before += ejector.ammo;
                ejector.ammo = Mathf.RoundToInt(ejector.ammo * multiplier);
                if (FlareMaxAmmo(ejector) > 0) FlareMaxAmmo(ejector) = ejector.ammo;
                after += ejector.ammo;
            }
            if (after > 0) Plugin.Log.LogInfo($"{aircraft.unitName} ace: flares {before} -> {after}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Could not add ace flares: " + ex.Message);
        }
    }

    /// <summary>
    /// The g an ace may pull: PostStallMaxG, or 70% of the airframe's estimated break g if that is lower (the estimate
    /// falls as damage weakens the joints), and less again while a joint is already loaded past 60%.
    /// </summary>
    internal static float SafeG(Aircraft aircraft)
    {
        float g = Plugin.PostStallMaxG.Value;
        if (aircraft != null && Structures.TryGetValue(aircraft, out var s))
        {
            float br = s.BreakAt(aircraft.speed);
            if (br > 0f) g = Mathf.Min(g, br * StructureMargin);
            if (s.Stress > 0.6f) g *= Mathf.Lerp(1f, 0.5f, Mathf.InverseLerp(0.6f, 0.9f, s.Stress));
        }
        return Mathf.Max(3f, g);
    }

    /// <summary>Highest joint load / break load measured for this aircraft (0 in simple physics or before any reading).</summary>
    internal static float StressOf(Aircraft aircraft)
    {
        if (aircraft == null || aircraft.simplePhysics) return 0f;
        return MeasureStructure(aircraft).Stress;
    }

    /// <summary>
    /// Reads every part joint's load against its break limit. Joint load scales with g, so g / stress estimates the
    /// g the airframe breaks at (damage lowers break forces, and the estimate follows). The estimate keeps the
    /// lowest recent reading and relaxes upward 2% a second.
    /// </summary>
    private static Structure MeasureStructure(Aircraft aircraft)
    {
        if (!Structures.TryGetValue(aircraft, out var s))
        {
            if (Structures.Count > 32) Prune();
            s = new Structure();
            Structures[aircraft] = s;
        }
        float worst = 0f;
        var parts = aircraft.partLookup;
        if (parts != null)
        {
            foreach (var part in parts)
            {
                if (part is not AeroPart aero || aero.Joints == null) continue;
                foreach (var pj in aero.Joints)
                {
                    Joint j = pj?.joint;
                    if (j == null) continue;
                    worst = Mathf.Max(worst, Ratio(j.currentForce.magnitude, j.breakForce), Ratio(j.currentTorque.magnitude, j.breakTorque));
                }
            }
        }
        s.Stress = worst;

        float now = Time.time;
        float decay = 1f - 0.01f * Mathf.Clamp(now - s.EstimateTime, 0f, 1f);
        s.Kg = Mathf.Max(s.Kg * decay, s.MaxKg * 0.9f);
        s.Kq = Mathf.Max(s.Kq * decay, s.MaxKq * 0.9f);
        s.EstimateTime = now;
        if (aircraft.gForce > 2.5f && worst > 0.05f)
        {
            float v = Mathf.Max(aircraft.speed, SpeedRef);
            float kg = worst / aircraft.gForce, kq = kg / (v * v);
            s.Kg = Mathf.Max(s.Kg, kg);
            s.Kq = Mathf.Max(s.Kq, kq);
            s.MaxKg = Mathf.Max(s.MaxKg, kg);
            s.MaxKq = Mathf.Max(s.MaxKq, kq);
            if (!s.Logged)
            {
                s.Logged = true;
                float b800 = s.BreakAt(800f / 3.6f), b1000 = s.BreakAt(1000f / 3.6f);
                Plugin.Log.LogInfo($"{aircraft.definition.unitName} ace structure: joints at {worst:P0} of break load at {aircraft.gForce:0.0} g, {aircraft.speed * 3.6f:0} km/h; " +
                                   $"break estimated near {b800:0.0} g at 800 km/h and {b1000:0.0} g at 1000 km/h; limits {b800 * StructureMargin:0.0} g and {Mathf.Max(MinG, b1000 * StructureMargin):0.0} g.");
            }
        }
        return s;
    }

    private static float Ratio(float load, float limit) =>
        limit > 0f && !float.IsInfinity(limit) && !float.IsNaN(limit) ? load / limit : 0f;

    private static void Prune()
    {
        foreach (var dead in System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(Structures.Keys, k => k == null)))
            Structures.Remove(dead);
    }

    private static bool IntroOnCamera() => IntroDirector.Playing && IntroDirector.CameraCut;

    private static void FlyByWirePostfix(ControlsFilter.FlyByWire __instance, Saved __state)
    {
        if (!__state.Changed) return;
        GLimit(__instance) = __state.G;
        AlphaLimit(__instance) = __state.Alpha;
    }

    // ---- Aggression and energy ----------------------------------------------------------------------

    /// <summary>
    /// AutoAim softens its turn and bank by airspeed/cornerSpeed unless effort exceeds 1. Aces get the full
    /// version while they have the energy for it, and the stock, gentler version when slow so they recover.
    /// </summary>
    private static void AutoAimPrefix(AutopilotPlane __instance, ref float effort)
    {
        try
        {
            Aircraft aircraft = __instance.aircraft;
            if (!Plugin.AceAggression.Value || !Encounters.TryGetAce(aircraft, out int tier, out _) || tier < 2) return;
            // Station-keeping stays gentle, and nobody pulls hard while the intro camera is on them.
            if (aircraft.radarAlt < LowFloor || Encounters.InFormationSlot(aircraft) || IntroOnCamera()) return;
            float corner = aircraft.GetAircraftParameters()?.cornerSpeed ?? 0f;
            if (corner <= 0f) return;
            if (aircraft.speed >= corner * Plugin.EnergyFloor.Value)
                effort = Mathf.Max(effort, 1.01f);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace aggression failed: " + ex.Message);
        }
    }

    // ---- Missile discipline -------------------------------------------------------------------------

    /// <summary>
    /// The firing branch needs 2.5 s since the last shot. Out of disciplined range, nudging lastFiredTime
    /// forward holds the shot this frame without touching the lead/steering maths around it.
    /// </summary>
    private static void UseMissilesPrefix(AIPilotCombatModes __instance, bool checkMode)
    {
        if (checkMode) return;
        try
        {
            Aircraft aircraft = StateAircraft(__instance);
            if (!Encounters.TryGetAce(aircraft, out _, out _)) return;
            if (PostStall.WantsShot(aircraft))
            {
                // Kulbit snap: the nose is on the target now; let the shot go at once.
                LastFiredTime(__instance) = Mathf.Min(LastFiredTime(__instance), Time.timeSinceLevelLoad - 3f);
                return;
            }
            if (CurrentTarget(__instance) is not Aircraft) return; // discipline is for air targets only
            var station = aircraft.weaponManager.currentWeaponStation;
            if (station?.WeaponInfo == null) return;
            float maxRange = station.WeaponInfo.targetRequirements.maxRange;
            if (TargetDist(__instance) > maxRange * Plugin.MissileRangeShare.Value)
            {
                float hold = Time.timeSinceLevelLoad - 2.4f;
                if (LastFiredTime(__instance) < hold) LastFiredTime(__instance) = hold;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace missile discipline failed: " + ex.Message);
        }
    }

    // ---- Wingman bracket ----------------------------------------------------------------------------

    /// <summary>FlyToTarget picks the destination once a second (checkMode); swing wingmen's off-axis.</summary>
    private static void FlyToTargetPostfix(AIPilotCombatModes __instance, bool checkMode)
    {
        if (!checkMode || !Plugin.WingmanBracket.Value) return;
        try
        {
            Aircraft aircraft = StateAircraft(__instance);
            if (!Encounters.TryGetAce(aircraft, out _, out int wingman) || wingman == 0) return;
            if (CurrentTarget(__instance) == null) return;

            GlobalPosition here = aircraft.GlobalPosition();
            GlobalPosition dest = StateDestination(__instance);
            Vector3 line = dest - here;
            line.y = 0f;
            float dist = line.magnitude;
            if (dist < BracketBeyond) return;

            float side = (wingman % 2 == 1) ? 1f : -1f;
            // Aim at a point off to one side of the target so the approach comes in BracketAngle off the line,
            // fading out as the wingman closes so it converges on the target inside BracketBeyond.
            float fade = Mathf.Clamp01((dist - BracketBeyond) / BracketBeyond);
            Vector3 right = Vector3.Cross(Vector3.up, line / dist);
            float offset = dist * Mathf.Tan(BracketAngle * Mathf.Deg2Rad) * fade;
            StateDestination(__instance) = dest + right * (side * offset);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace bracket failed: " + ex.Message);
        }
    }
}
