using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

internal enum Manoeuvre
{
    Cobra,
    Herbst,
    Kulbit,
    Break,
    Bell,
    YawBell,
    Hunt,
    Notch,
    Crank,
    Jink,
    Bait,
    Zoom,
}

/// <summary>
/// Phase 4: post-stall manoeuvres. A scripted state takes the controls for a few seconds, then a scripted recovery
/// brings the jet back above 250 km/h and hands back to the normal AI.
///
/// Control: a postfix on Aircraft.FilterInputs, after every flight-control filter has run, replaces the
/// control-surface commands with a body-rate controller (thrust-vectoring nozzles read the same commands). Its signs
/// copy the game's own fly-by-wire loops: pitch and yaw = -K (rate - target), roll = +K (rate - target), so it is
/// stable on any airframe the game's FBW is stable on. Targets come from attitude errors as world angular velocity
/// (omega = K * cross(current, wanted) turns a body axis toward a wanted direction), converted to body axes.
///
/// Offensive / positional:
/// - Cobra: someone closing fast from behind. Wings level, nose past 90 deg AoA so the jet all but stops in the air,
///   drop the nose; if the pursuer overshot into the front quarter, snap the nose onto it (missile hold lifted).
/// - Herbst (J-turn): target behind at 1.5-8 km. Pull into the stall and swing the nose round onto it.
/// - Kulbit nose snap: target 40-90 deg off the nose in missile range. Whip the nose on and hold it for the shot.
/// Defensive (a missile 0.6-2.2 s from impact, late enough that it cannot correct; flares popped):
/// - from behind: Cobra with no bleed, so the missile overshoots the suddenly stopped jet;
/// - from anywhere else: Break, rolling the lift onto the beam of the missile and pulling past the normal limits.
///
/// Gates: Cobra 300-550 km/h (faster entries bleed off at idle first, unless defensive); Herbst under 600 km/h;
/// Kulbit with a missile ready. Herbst, Kulbit and Break need a post-stall capable airframe (thrust vectoring, or
/// flagged). Every manoeuvre aborts into the recovery below 600 m, eases its pull past the g limit or when the
/// airframe's joints get loaded, and the AI's eject-when-nose-against-the-airflow check is held off meanwhile.
/// </summary>
internal static class PostStall
{
    private const float RecoverSpeed = 250f / 3.6f;   // m/s
    private const float AbortAltitude = 600f;
    private const float CheckInterval = 0.25f;
    private const float MinGap = 8f;                    // seconds between two offensive manoeuvres of one ace
    private const float BlendOut = 0.4f;                // seconds handing the surfaces back to the AI
    private const float MaxRecovery = 8f;
    private const float CobraEntry = 520f / 3.6f;      // bleed down to this before the pull
    private const float HerbstEntry = 580f / 3.6f;
    private const float BleedLimit = 6f;               // seconds of idle at most
    private const float DefenceCooldown = 5f;

    private sealed class Run
    {
        public Manoeuvre Kind;
        public int Phase;              // 0.. manoeuvre phases, Recover = recovery, then blend-out
        public float PhaseStart;
        public float Start;
        public Aircraft Target;
        public Unit Threat;            // the missile, for defensive runs
        public Vector3 FallbackDir;    // where to point when there is no target
        public bool Vectoring;
        public bool Forced;
        public bool Defensive;
        public int FlaresLeft;
        public float NextFlare;
        public int AmmoAtStart, MissilesAtStart;
        public float BestAim = 180f;
        public bool MissileFired;
        public float NextMissile;      // earliest time for the next snap missile
        public float BlendStart = -1f;
        public float Side;             // yaw-roll bell: +1 right, -1 left
        public float TierScale = 1f;   // hunt: pull share and aim quality by tier
        public bool Cover;             // hunt: going after a hostile on a squadmate's tail
        public Aircraft Protecting;
        public bool PincerCalled;
        public float NextJink;         // jink: time of the next reversal
        public Vector3 JinkUp;         // jink: the lift direction being pulled toward now
        public Vector3 RidgeFace;      // bait: where the rising ground reaches the ace's height (local)
        public float RidgeCrest;       // bait: the ridge top (local y)
        public float BaitMargin;       // bait: safety factor on the pull-up distance
        public bool Extend;            // jink: dive away and accelerate instead of turning (outmatched)
        public string YoyoLogged;      // hunt: the last yo-yo logged
        public bool MergeChosen;       // hunt: the circle for this merge has been picked
        public float OneCircleUntil;   // hunt: turning away into a one-circle fight until then
    }

    private const int Recover = 100;

    private static readonly Dictionary<Aircraft, Run> Active = new();
    private static readonly Dictionary<Aircraft, float> NextCheck = new();
    private static readonly Dictionary<(Aircraft, Manoeuvre), float> Cooldown = new();
    private static readonly Dictionary<Aircraft, float> LastEnd = new();
    private static readonly Dictionary<Aircraft, float> LastDefence = new();
    private static readonly Dictionary<Aircraft, Missile> LastDefended = new();

    /// <summary>Outcome counters for testing: defensive manoeuvres flown and offensive ones flown.</summary>
    internal static int DefencesFlown, AttacksFlown, HuntsFlown;

    internal static void Apply(Harmony harmony)
    {
        Patches.Patch(harmony, "ace post-stall manoeuvres",
            AccessTools.Method(typeof(Aircraft), nameof(Aircraft.FilterInputs)),
            postfix: nameof(FilterInputsPostfix), owner: typeof(PostStall));
        // The combat AI's 5-second ejection check bails out whenever the nose points against the airflow
        // (AoA past 90 deg), which a Cobra or Herbst does on purpose. Hold it off while manoeuvring, unless the jet
        // is really coming apart.
        Patches.Patch(harmony, "no ejecting mid-manoeuvre",
            AccessTools.Method(typeof(AIPilotCombatModes), "EjectionCheck"),
            prefix: nameof(EjectionCheckPrefix), owner: typeof(PostStall));
    }

    private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> StateAircraft =
        AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");

    private static bool EjectionCheckPrefix(AIPilotCombatModes __instance)
    {
        try
        {
            Aircraft a = StateAircraft(__instance);
            if (FlightTest.IsSubject(a)) return false; // a test jet never bails out mid-test
            // Jets built to point past 90 deg (a profile with an AoA limit of 90 or more, like the PW-Mk1) do it outside
            // the manoeuvres too, so for them the "flying backwards" rule never applies.
            bool pointer = Encounters.IsAce(a) && AirframeProfiles.For(a) is { AlphaLimit: >= 90f };
            if (!IsManoeuvring(a) && !pointer) return true;
            if (a.partDamageTracker != null && a.partDamageTracker.GetDetachedRatio() > 0.12f) return true;
            return a.radarAlt < 40f; // near the ground the stock checks stand
        }
        catch
        {
            return true;
        }
    }

    internal static void Reset()
    {
        Active.Clear();
        FormationFlying.Reset();
        NextCheck.Clear();
        Cooldown.Clear();
        LastEnd.Clear();
        LastDefence.Clear();
        LastDefended.Clear();
        Notched.Clear();
        NextRiposte.Clear();
    }

    internal static bool IsManoeuvring(Aircraft a) => a != null && Active.ContainsKey(a);

    /// <summary>Nose-on-target moments (Kulbit, the Cobra's follow-up snap): the missile hold is off so the AI can shoot.</summary>
    internal static bool WantsShot(Aircraft a) =>
        a != null && Active.TryGetValue(a, out var r) &&
        (r.Kind == Manoeuvre.Kulbit && r.Phase is 0 or 1 || r.Kind == Manoeuvre.Cobra && r.Phase == 3 ||
         r.Kind == Manoeuvre.Herbst && r.Phase == 3 || r.Kind == Manoeuvre.Bell && r.Phase is 3 or 4 ||
         r.Kind == Manoeuvre.YawBell && r.Phase == 2 || r.Kind == Manoeuvre.Hunt && r.Phase < Recover);

    internal static string Describe(Aircraft a) =>
        a != null && Active.TryGetValue(a, out var r) ? $"{r.Kind} ({(r.Phase >= Recover ? "recovering" : "phase " + (r.Phase + 1))})" : "none";

    // ---- the hook -------------------------------------------------------------------------------------

    private static void FilterInputsPostfix(Aircraft __instance)
    {
        Aircraft a = __instance;
        if (FlightTest.Override(a)) return;
        if (FormationFlying.Deconflict(a)) return; // never fly into a squadmate, whatever else is going on
        if (Plugin.HoldFormation.Value && FormationFlying.Fly(a)) return;
        if (FormationFlying.Split(a)) return;
        if (Plugin.HoldFormation.Value && FormationFlying.FlyLead(a)) return;
        if (Plugin.HoldFormation.Value) FormationFlying.LimitLead(a);
        try { TryRiposte(a); } catch (Exception ex) { Plugin.Log.LogWarning("Riposte check failed: " + ex.Message); }
        try
        {
            bool running = Active.TryGetValue(a, out var run);
            if (running && (a == null || a.disabled || !Plugin.PostStall.Value && !run.Forced))
            {
                Active.Remove(a);
                return;
            }
            if (!running && (!Plugin.PostStall.Value || !Encounters.IsAce(a))) return;

            // Look for threats a few times a second. Survival first: a missile cuts into any offensive manoeuvre or a
            // recovery; only a defensive manoeuvre already under way is left to finish.
            float now = Time.time;
            if ((!running || !run.Defensive || run.Phase >= Recover) && (!NextCheck.TryGetValue(a, out var next) || now >= next))
            {
                NextCheck[a] = now + CheckInterval;
                if (TryDefend(a) || TryGunsDefence(a) || !running && TryTrigger(a))
                    running = Active.TryGetValue(a, out run);
            }
            if (running) Drive(a, run);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Post-stall control failed, handing back to the AI: " + ex.Message);
            Active.Remove(a);
        }
    }

    // ---- ripostes ------------------------------------------------------------------------------------

    private static readonly Dictionary<Aircraft, float> NextRiposte = new();

    /// <summary>
    /// The opportunistic shot, checked every physics step whatever the ace is flying (a bell, a Cobra, a missile
    /// break, a notch, a hard turn, or the stock AI): the moment a hostile crosses the nose close enough that the
    /// shot is nearly unavoidable, take it. Missile: within 12 deg (tier 2: 8 deg) of the nose, from the
    /// missile's minimum range (at least 350 m) out to 5 km or 70% of its range, nothing friendly in line, one
    /// every 2.5 s (tier 2: 4 s). Guns: inside 1 km and 3 deg. Never at tier 1, never on camera or in formation.
    /// The hunt's attack phase does its own shooting and is left alone.
    /// </summary>
    private static void TryRiposte(Aircraft a)
    {
        if (a == null || a.disabled || !Encounters.TryGetAce(a, out int tier, out _) || tier <= 1) return;
        if (Encounters.InFormationSlot(a) || IntroGate.Holding || IntroDirector.Playing && IntroDirector.CameraCut) return;
        if (Active.TryGetValue(a, out var run) && run.Kind == Manoeuvre.Hunt && run.Phase == 0) return;
        float now = Time.time;
        if (NextRiposte.TryGetValue(a, out var next) && now < next) return;

        Transform tr = (a.CockpitRB() ?? a.rb).transform;
        Vector3 fwd = tr.forward;
        Aircraft best = null;
        float bestOff = 999f, bestDist = 0f;
        foreach (var h in Encounters.HuntedAircraft())
        {
            if (h == null || h.disabled) continue;
            Vector3 rel = h.transform.position - a.transform.position;
            float off = Vector3.Angle(fwd, rel);
            if (off < bestOff) { best = h; bestOff = off; bestDist = rel.magnitude; }
        }
        if (best == null || FriendlyInLine(a, fwd, bestDist)) return;
        string doing = run != null ? (run.Phase >= Recover ? $"{run.Kind} recovery" : run.Kind.ToString()) : "a turn";

        if (bestDist <= 1000f && bestOff <= 3f)
        {
            try { a.weaponManager?.FireGuns(); } catch { /* no guns */ }
            return;
        }
        float cone = tier >= 4 ? 16f : tier >= 3 ? 12f : 8f;
        if (bestOff > cone || a.weaponStations == null) return;
        foreach (var station in a.weaponStations)
        {
            var info = station?.WeaponInfo;
            if (info == null || !info.missile || info.bomb || info.gun || station.Cargo || station.Ammo <= 0 || info.effectiveness.antiAir < 0.5f || info.effectiveness.antiAir <= info.effectiveness.antiSurface) continue;
            var req = info.targetRequirements;
            if (bestDist < Mathf.Max(req.minRange, 350f) || bestDist > Mathf.Min(5000f, req.maxRange * 0.7f)) continue;
            try
            {
                var wm = a.weaponManager;
                wm.ClearTargetList();
                wm.AddTargetList(best);
                wm.SetActiveStation(station.Number);
                wm.Fire();
                NextRiposte[a] = now + (tier >= 4 ? 1.5f : tier >= 3 ? 2.5f : 4f);
                if (run != null) { run.MissileFired = true; run.NextMissile = now + 2.5f; }
                RipostesFired++;
                Plugin.Log.LogInfo($"{Callsign(a)}: riposte during {doing}, {info.weaponName} at {bestDist:0} m, {bestOff:0.0} deg off the nose.");
                Encounters.Taunt(a, "parthian");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Riposte failed: " + ex.Message);
                NextRiposte[a] = now + 4f;
            }
            return;
        }
    }

    internal static int RipostesFired;

    // ---- triggers -------------------------------------------------------------------------------------

    /// <summary>Allowed to fly post-stall manoeuvres (tier gate or flagged airframe), on top of EligibleBase.</summary>
    private static bool Eligible(Aircraft a, out bool capable) => EligibleBase(a, out capable, out bool postStall, out _) && postStall;

    /// <summary>
    /// An ace that may be flown by this class at all: fighting (not egressing, not holding formation, not on camera).
    /// postStall says whether it may also fly post-stall manoeuvres (its tier or a flagged airframe); every tier hunts.
    /// </summary>
    private static bool EligibleBase(Aircraft a, out bool capable, out bool postStall, out int tier)
    {
        capable = postStall = false;
        if (!Encounters.TryGetAce(a, out tier, out _)) return false;
        if (Encounters.InFormationSlot(a) || Encounters.IsFormationLead(a)) return false;
        if (IntroDirector.Playing && IntroDirector.CameraCut) return false;
        if (StagingHold) return false; // record mode: a staged manoeuvre is coming, fly straight until it is forced
        capable = AirframeChecklist.IsPostStallCapable(a.definition);
        postStall = tier >= Plugin.PostStallTier.Value || AirframeChecklist.IsPostStallFlagged(a.definition);
        return a.rb != null;
    }

    /// <summary>How hard and how cleanly a tier flies the hunt: pull share, and aim wobble in degrees.</summary>
    /// <summary>
    /// The highest AoA the hunt pulls to, from the airframe's fly-by-wire limit (which the survey shows bounds its
    /// sustained turn): a few degrees past it, well past it with vectoring, more for a close snap shot.
    /// </summary>
    internal static float HuntAoACap(AirframeProfile p, bool snapShot)
    {
        float limit = p?.AlphaLimit ?? 25f;
        float margin = p != null && p.Vectoring ? 15f : 4f;
        if (snapShot) margin += 6f;
        // Departure-prone (relaxed stability): stay inside the limit, the override bypasses the stability augmentation.
        if (p != null && (p.Departs || p.RelaxedStability && !p.Measured)) margin = snapShot ? 2f : -2f;
        return Mathf.Max(limit + margin, 14f);
    }

    private static readonly HashSet<Missile> Notched = new();

    private static bool IsRadarGuided(Missile m)
    {
        try
        {
            string s = m.GetSeekerType() ?? "";
            return s.Contains("ARH") || s.Contains("SARH") || s.IndexOf("radar", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    private static float TierScale(int tier) => tier switch { <= 1 => 0.6f, 2 => 0.8f, 3 => 1f, _ => 1.2f };

    private static bool Ready(Aircraft a, Manoeuvre m) => !Cooldown.TryGetValue((a, m), out var until) || Time.time >= until;

    /// <summary>A missile about to arrive: Cobra if it comes from behind, Break otherwise.</summary>
    private static bool TryDefend(Aircraft a)
    {
        // Every tier breaks for missiles; only post-stall tiers (or flagged airframes) do it with a Cobra.
        // A level break works low down too (the notch takes aces down into the clutter); the Cobra needs height.
        if (!EligibleBase(a, out bool capable, out bool postStall, out int tier) || a.radarAlt < 300f) return false;
        var mw = a.GetMissileWarningSystem();
        if (mw == null || !mw.TryGetNearestIncoming(out Missile missile) || missile == null || missile.disabled) return false;
        // The cooldown is per missile: a second missile of a ripple is answered even straight after breaking the first,
        // and the same missile coming back round after an overshoot is answered again once 2.5 s have passed (the
        // old 5 s lock let re-attacking missiles fly in unopposed).
        if (LastDefence.TryGetValue(a, out var last) && LastDefended.TryGetValue(a, out var lastMissile) && lastMissile == missile &&
            Time.time - last < (Active.TryGetValue(a, out var still) && still.Defensive && still.Phase < Recover ? DefenceCooldown : 2.5f)) return false;

        Vector3 rel = missile.transform.position - a.transform.position;
        float dist = rel.magnitude;
        Vector3 dir = rel / Mathf.Max(dist, 1f);
        Vector3 mVel = missile.rb != null ? missile.rb.velocity : -dir * 600f;
        float closing = -Vector3.Dot(mVel - a.rb.velocity, dir);
        if (closing < 50f) return false;
        float impact = dist / closing;
        var profile = AirframeProfiles.For(a);
        float breakEarly = profile?.BreakEarly ?? 0f;
        float lateBy = tier <= 1 ? 0.5f : tier == 2 ? 0.2f : 0f; // greener pilots see it later
        // A radar missile still well out: put it on the beam first (its Doppler return is weakest there) and get
        // down into the clutter, with the jammer on it; the break follows if it is still coming.
        if (impact > 2.2f + breakEarly - lateBy && impact <= 10f && Plugin.BeamRadarMissiles.Value && tier >= 2 && a.radarAlt > 600f
            && IsRadarGuided(missile) && !Notched.Contains(missile))
        {
            if (Active.TryGetValue(a, out var busy) && busy.Defensive && busy.Phase < Recover) return false;
            Notched.Add(missile);
            Begin(a, Manoeuvre.Notch, null, capable, false, missile, $"radar missile {dist:0} m out, {impact:0.0} s: beaming it");
            if (Active.TryGetValue(a, out var notch)) { notch.FlaresLeft = 0; notch.TierScale = TierScale(tier); }
            return true;
        }
        if (impact > 2.2f + breakEarly - lateBy || impact < 0.6f) return false; // late enough that the missile cannot correct (slow rollers start sooner)

        float aspect = Vector3.Angle(a.transform.forward, dir); // 180 = straight up the tail
        Manoeuvre m = aspect > 110f && postStall && (profile?.PostStallSuits ?? true) && a.radarAlt > 1200f ? Manoeuvre.Cobra : Manoeuvre.Break;
        // A plain airframe that rolls too slowly to make a break work leaves it to the stock evasion.
        if (m == Manoeuvre.Break && !capable && (profile == null || profile.RollRate < 90f)) return false;
        if (Active.TryGetValue(a, out var current) && current.Defensive && current.Phase < Recover && current.Kind is not (Manoeuvre.Notch or Manoeuvre.Jink)) return false;
        if (current != null && (!current.Defensive || current.Kind is Manoeuvre.Notch or Manoeuvre.Jink) && current.Phase < Recover)
            Plugin.Log.LogInfo($"{Callsign(a)}: {current.Kind} broken off for a missile.");
        LastDefence[a] = Time.time;
        LastDefended[a] = missile;
        Begin(a, m, null, capable, false, missile, $"missile {dist:0} m out, {impact:0.0} s, {aspect:0} deg off the nose");
        if (Active.TryGetValue(a, out var started)) started.TierScale = TierScale(tier);
        return true;
    }

    // ---- guns defence ---------------------------------------------------------------------------------

    private static readonly Dictionary<Aircraft, float> GunThreatSince = new();

    /// <summary>
    /// Someone behind and aiming: a hostile more than 100 deg off the ace's nose, inside 2.5 km, with the ace within
    /// 15 deg of its own nose. Left alone, the stock AI flies on while they settle into a gun solution (tested: a
    /// guns-only Revoker held one for up to 23 s of 40 at 1.5 km), so every tier answers, after a reaction time
    /// (tier 1: 1 s, 2: 0.6 s, 3: 0.3 s): a hard break across their line of fire; from tier 2, jinking reversals while
    /// they stay on; and when they overshoot, the normal hunt and post-stall reversals take over. A missile cuts in.
    /// </summary>
    private static bool TryGunsDefence(Aircraft a)
    {
        if (!EligibleBase(a, out bool capable, out _, out int tier) || a.radarAlt < 300f) return false;
        if (Active.TryGetValue(a, out var current) && current.Phase < Recover && (current.Defensive || current.Kind == Manoeuvre.Hunt && current.Phase > 0))
            return false;
        Aircraft attacker = AimingAtUs(a, 2500f, 15f);
        float now = Time.time;
        if (attacker == null) { GunThreatSince.Remove(a); return false; }
        if (!GunThreatSince.TryGetValue(a, out float since)) { GunThreatSince[a] = now; return false; }
        float react = tier <= 1 ? 1f : tier == 2 ? 0.6f : tier == 3 ? 0.3f : 0.2f;
        if (now - since < react) return false;
        if (current != null && current.Phase < Recover)
            Plugin.Log.LogInfo($"{Callsign(a)}: {current.Kind} broken off: {attacker.unitName} on the tail.");
        float dist = Vector3.Distance(attacker.transform.position, a.transform.position);
        // Tier 3 and up, low with them close behind and a ridge ahead: lead them at it instead (see Bait in Drive).
        if (TryBait(a, attacker, tier, dist)) { GunThreatSince.Remove(a); return true; }
        // The escape that suits the two jets: the vertical if we out-climb them (tier 3 up; they follow, run out of
        // speed, and we come back down on them), a diving extension if they beat us at turning and climbing, else
        // a break and jinks.
        var pic = EnergyFight.Compare(a, attacker);
        if (tier >= 3 && pic.OutClimbs && a.speed > 550f / 3.6f && a.radarAlt > 1000f && Ready(a, Manoeuvre.Zoom))
        {
            Begin(a, Manoeuvre.Zoom, attacker, capable, false, attacker, $"{attacker.unitName} {dist:0} m behind; {pic}: taking it vertical");
            if (Active.TryGetValue(a, out var zoom)) { zoom.FlaresLeft = 0; zoom.TierScale = TierScale(tier); }
            Cooldown[(a, Manoeuvre.Zoom)] = Time.time + 20f;
            GunThreatSince.Remove(a);
            return true;
        }
        bool extend = pic.Outmatched && a.radarAlt > 1500f && a.speed > 400f / 3.6f;
        Begin(a, Manoeuvre.Jink, attacker, capable, false, attacker,
              $"{attacker.unitName} {dist:0} m behind with a gun solution; {pic}{(extend ? ": extending away" : "")}");
        if (Active.TryGetValue(a, out var run)) { run.FlaresLeft = 0; run.TierScale = TierScale(tier); run.Extend = extend; }
        GunThreatSince.Remove(a);
        return true;
    }

    /// <summary>Testing counters: ridge baits flown, and pursuers lost to the ground during one.</summary>
    internal static int BaitsFlown, BaitKills;

    /// <summary>
    /// The ridge bait: tier 3 and up, under 450 m above the ground, a pursuer inside 1.5 km behind and pointing, a jet
    /// that turns well (a turn fighter, or 8 g and more), and rising ground 1.2-4 km dead ahead that tops out 60-600 m
    /// above it. Fly at the ridge level and pull up as late as the ace's own speed and g allow, with a margin (tier 3:
    /// 25%, tier 4: 12%); a pursuer close behind and fixed on the shot has less room than that.
    /// </summary>
    private static bool TryBait(Aircraft a, Aircraft attacker, int tier, float dist)
    {
        if (!Plugin.TerrainTactics.Value || tier < 3 || a.radarAlt > 450f || dist > 1500f || !Ready(a, Manoeuvre.Bait)) return false;
        var profile = AirframeProfiles.For(a);
        if (profile != null && profile.Style != "turn" && profile.PeakG < 8f) return false;
        if (!TerrainTactics.TryFindRidge(a, out float face, out float crest)) return false;
        Vector3 dir = a.rb.velocity;
        dir.y = 0f;
        dir.Normalize();
        Begin(a, Manoeuvre.Bait, attacker, AirframeChecklist.IsPostStallCapable(a.definition), false, attacker,
              $"{attacker.unitName} {dist:0} m behind, ridge {face:0} m ahead, {crest - a.transform.position.y:0} m above us");
        if (!Active.TryGetValue(a, out var run)) return false;
        run.FlaresLeft = 0;
        run.RidgeFace = a.transform.position + dir * face;
        run.RidgeCrest = crest;
        run.BaitMargin = tier >= 4 ? 1.12f : 1.25f;
        Cooldown[(a, Manoeuvre.Bait)] = Time.time + 30f;
        BaitsFlown++;
        return true;
    }

    /// <summary>The nearest hostile behind the ace (more than 100 deg off its nose) within range, pointing at it.</summary>
    private static Aircraft AimingAtUs(Aircraft a, float range, float cone)
    {
        Aircraft best = null;
        float bestDist = range;
        Vector3 pos = a.transform.position, fwd = a.transform.forward;
        foreach (var h in System.Linq.Enumerable.Concat(Encounters.HuntedAircraft(), Encounters.PursuerCandidates()))
        {
            if (h == null || h.disabled || h == a) continue;
            Vector3 rel = h.transform.position - pos;
            float d = rel.magnitude;
            if (d >= bestDist || Vector3.Angle(fwd, rel) < 100f) continue;
            if (Vector3.Angle(h.transform.forward, -rel) > cone) continue;
            best = h;
            bestDist = d;
        }
        return best;
    }

    private static bool TryTrigger(Aircraft a)
    {
        if (!EligibleBase(a, out bool capable, out bool postStall, out int tier) || a.radarAlt < 600f) return false;
        // Never spend energy on a showpiece with a missile in the air at it.
        var warning = a.GetMissileWarningSystem();
        if (warning != null && warning.IsWarning()) return false;
        if (TryCrank(a, tier)) return true;
        if (TryHunt(a, tier)) return true;
        if (!postStall || a.radarAlt < 1500f) return false;
        if (LastEnd.TryGetValue(a, out var end) && Time.time - end < MinGap) return false;
        float speed = a.speed, alt = a.radarAlt;
        Vector3 pos = a.transform.position, vel = a.rb.velocity, fwd = a.transform.forward;

        Aircraft closest = null;
        float closestDist = float.MaxValue;
        foreach (var h in Encounters.HuntedAircraft())
        {
            if (h == null || h.disabled) continue;
            float d = Vector3.Distance(h.transform.position, pos);
            if (d < closestDist) { closest = h; closestDist = d; }
        }
        if (closest == null) return false;

        Vector3 rel = closest.transform.position - pos;
        float dist = rel.magnitude;
        Vector3 dir = rel / Mathf.Max(dist, 1f);
        float offNose = Vector3.Angle(fwd, dir);
        Vector3 hVel = closest.rb != null ? closest.rb.velocity : Vector3.zero;
        float closing = -Vector3.Dot(hVel - vel, dir); // + = getting closer
        string why = $"target {dist:0} m, {offNose:0} deg off the nose, closing {closing:0} m/s";

        // Someone right on the tail. Overrunning fast: Cobra, so they fly past. Sitting there at matched speed (or by
        // chance when either fits): the yaw-roll bell, which rolls the ace round behind them instead.
        bool onTail = dist < 1500f && offNose > 120f;
        bool suits = AirframeProfiles.For(a)?.PostStallSuits ?? true; // enough low-speed pitch authority for the showpieces
        bool cobraFits = onTail && suits && Ready(a, Manoeuvre.Cobra) && speed > 300f / 3.6f && speed < 550f / 3.6f && closing > 50f;
        bool yawBellFits = onTail && capable && suits && Ready(a, Manoeuvre.YawBell) && speed > 250f / 3.6f && speed < 750f / 3.6f && closing < 120f;
        if (yawBellFits && (!cobraFits || UnityEngine.Random.value < 0.5f))
        {
            Begin(a, Manoeuvre.YawBell, closest, capable, false, null, why);
            return true;
        }
        if (cobraFits)
        {
            Begin(a, Manoeuvre.Cobra, closest, capable, false, null, why);
            return true;
        }

        // Kulbit: target well off the nose but inside missile range, missile ready.
        if (capable && suits && Ready(a, Manoeuvre.Kulbit) && alt > 2500f && offNose >= 40f && offNose <= 90f && MissileReady(a, dist))
        {
            Begin(a, Manoeuvre.Kulbit, closest, capable, false, null, why);
            return true;
        }

        // Target behind and not close enough for a Cobra: reverse rather than fly a long turn.
        // Fast (over 600 km/h): Kvochur's Bell, zooming above the chaser and falling onto it. Slow: Herbst.
        // In between, either.
        // Tier 3 and up reverse from 1 km out: between 1 and 1.5 km a fast ace had nothing that fit (the Cobra and the
        // yaw-roll bell want it slower), and simply flew on.
        bool behind = offNose > 110f && dist > (tier >= 3 ? 1000f : 1500f) && dist < 8000f;
        // The Bell hangs nearly still for a few seconds, so only against a chaser too close to set up a shot on it.
        bool bellFits = capable && suits && Ready(a, Manoeuvre.Bell) && alt > 2500f && speed > 450f / 3.6f && behind && dist < 4500f;
        bool herbstFits = capable && suits && Ready(a, Manoeuvre.Herbst) && alt > 2000f && speed < 600f / 3.6f && speed > 200f / 3.6f && behind && offNose > 120f;
        if (bellFits && (!herbstFits || UnityEngine.Random.value < 0.5f))
        {
            Begin(a, Manoeuvre.Bell, closest, capable, false, null, why);
            return true;
        }
        if (herbstFits)
        {
            Begin(a, Manoeuvre.Herbst, closest, capable, false, null, why);
            return true;
        }
        return false;
    }

    // ---- hunt -----------------------------------------------------------------------------------------

    private const float HuntRange = 4000f, HuntDropRange = 4800f, HuntMaxSeconds = 20f;

    /// <summary>The hunted aircraft nearest the ace, with its distance and angle off the nose.</summary>
    /// <summary>
    /// An airframe that loses missile jousts (AirframeProfile.JoustAverse) does not fly nose-to-nose into one: with a
    /// bandit 4.5-16 km out, coming straight at it, it takes its own radar-missile shot first if it has one in reach,
    /// then cranks 55 deg off the bandit's bearing and drops a little. Less closure gives a missile fired at it
    /// further to fly, and the merge comes with an offset for the hunt to work from. Tier 2 and up, every 20 s.
    /// </summary>
    private static bool TryCrank(Aircraft a, int tier)
    {
        if (tier < 2 || !Ready(a, Manoeuvre.Crank)) return false;
        var profile = AirframeProfiles.For(a);
        if (profile == null || !profile.JoustAverse) return false;
        var target = Nearest(a, out float dist, out float offNose);
        if (target == null || dist < 4500f || dist > 16000f || offNose > 40f) return false;
        float aspect = Vector3.Angle(target.transform.forward, a.transform.position - target.transform.position);
        if (aspect > 35f) return false; // not coming at us
        Begin(a, Manoeuvre.Crank, target, AirframeChecklist.IsPostStallCapable(a.definition), false, null,
            $"bandit {dist:0} m, {aspect:0} deg off its nose: cranking instead of jousting ({profile.Style})");
        var run = Active[a];
        run.TierScale = TierScale(tier);
        Vector3 rel = target.transform.position - a.transform.position;
        run.Side = Vector3.Dot(Vector3.Cross(Vector3.up, a.transform.forward), rel) > 0f ? -1f : 1f; // turn away from the side it is on
        Cooldown[(a, Manoeuvre.Crank)] = Time.time + 20f;
        return true;
    }

    /// <summary>Where to start dragging: 60% of the bandit's longest air-to-air missile range, at most 10 km.</summary>
    private static float DragRange(Aircraft bandit)
    {
        float range = 0f;
        if (bandit?.weaponStations != null)
            foreach (var s in bandit.weaponStations)
                if (s?.WeaponInfo != null && s.Ammo > 0 && AceSpawner.IsAirToAirMissile(s.WeaponInfo))
                    range = Mathf.Max(range, s.WeaponInfo.targetRequirements.maxRange);
        return range > 0f ? Mathf.Min(range * 0.6f, 10000f) : 0f;
    }

    /// <summary>A radar missile at the target if one reaches: within 20 deg of the nose and 70% of its range.</summary>
    private static bool LongShot(Aircraft a, Run run, Vector3 fwd)
    {
        if (run.MissileFired || run.Target == null || a.weaponStations == null) return false;
        Vector3 rel = run.Target.transform.position - a.transform.position;
        float dist = rel.magnitude;
        if (Vector3.Angle(fwd, rel) > 20f || FriendlyInLine(a, fwd, dist)) return false;
        foreach (var station in a.weaponStations)
        {
            var info = station?.WeaponInfo;
            if (info == null || station.Cargo || station.Ammo <= 0 || !AceSpawner.IsAirToAirMissile(info) || !AceSpawner.IsRadarMissile(info)) continue;
            if (dist > info.targetRequirements.maxRange * 0.7f || dist < Mathf.Max(info.targetRequirements.minRange, 1500f)) continue;
            try
            {
                var wm = a.weaponManager;
                wm.ClearTargetList();
                wm.AddTargetList(run.Target);
                wm.SetActiveStation(station.Number);
                wm.Fire();
                run.MissileFired = true;
                Plugin.Log.LogInfo($"{Callsign(a)}: long shot before the crank, {info.weaponName} at {dist:0} m.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Long shot failed: " + ex.Message);
                run.MissileFired = true;
            }
        }
        return false;
    }

    private static Aircraft Nearest(Aircraft a, out float dist, out float offNose)
    {
        Aircraft best = null;
        dist = float.MaxValue;
        offNose = 180f;
        foreach (var h in Encounters.HuntedAircraft())
        {
            if (h == null || h.disabled) continue;
            float d = Vector3.Distance(h.transform.position, a.transform.position);
            if (d < dist) { best = h; dist = d; }
        }
        if (best != null) offNose = Vector3.Angle(a.transform.forward, best.transform.position - a.transform.position);
        return best;
    }

    /// <summary>
    /// Take the fight off the stock AI and hunt. Teamwork first: if a hostile is on a squadmate's tail and pointing
    /// at it, the ace goes after that hostile from any angle (cover). Otherwise the nearest target ahead of the wing
    /// line within 4 km (tier 1 only commits to ones well in front).
    /// </summary>
    private static bool TryHunt(Aircraft a, int tier)
    {
        if (!Plugin.AceHunt.Value) return false;
        float corner = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
        if (a.speed < Mathf.Max(60f, corner * 0.45f)) return false; // let it rebuild speed first
        bool capable = AirframeChecklist.IsPostStallCapable(a.definition);

        var threat = ThreatToSquadmate(a, out Aircraft mate, out float threatDist);
        if (threat != null)
        {
            Begin(a, Manoeuvre.Hunt, threat, capable, false, null, $"covering {Callsign(mate)}: hostile on its tail, {threatDist:0} m from us");
            var run = Active[a];
            run.Cover = true;
            run.Protecting = mate;
            run.TierScale = TierScale(tier);
            Encounters.Taunt(a, "cover");
            return true;
        }

        var target = Nearest(a, out float dist, out float offNose);
        float maxOff = tier <= 1 ? 60f : 100f, maxDist = tier <= 1 ? 3000f : HuntRange;
        if (target == null || dist > maxDist || offNose > maxOff) return false;
        Begin(a, Manoeuvre.Hunt, target, capable, false, null, $"target {dist:0} m, {offNose:0} deg off the nose");
        Active[a].TierScale = TierScale(tier);
        return true;
    }

    /// <summary>A hostile within 2.5 km of a squadmate, behind it and pointing at it; the one nearest this ace (up to 7 km).</summary>
    private static Aircraft ThreatToSquadmate(Aircraft a, out Aircraft mate, out float distFromUs)
    {
        mate = null;
        distFromUs = float.MaxValue;
        Aircraft best = null;
        foreach (var m in Encounters.SquadAircraft())
        {
            if (m == a) continue;
            foreach (var h in Encounters.HuntedAircraft())
            {
                if (h == null || h.disabled) continue;
                Vector3 hToM = m.transform.position - h.transform.position;
                if (hToM.magnitude > 2500f) continue;
                if (Vector3.Angle(m.transform.forward, -hToM) < 120f) continue; // not behind the squadmate
                if (Vector3.Angle(h.transform.forward, hToM) > 40f) continue;  // not pointing at it
                float d = Vector3.Distance(h.transform.position, a.transform.position);
                if (d < distFromUs && d < 7000f)
                {
                    best = h;
                    mate = m;
                    distFromUs = d;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// When a hunt ends. Normal: target lost, beyond 4.8 km, more than 110 deg off the nose, or 20 s. Cover: the
    /// squadmate is safe (down, or nothing within 2.5 km behind it) or the hostile is beyond 7 km; the angle does not
    /// end it, since coming round onto the hostile from anywhere is the point.
    /// </summary>
    private static bool HuntOver(Run run, bool haveTarget, float targetDist, float offNose, float t)
    {
        if (!haveTarget || t > HuntMaxSeconds) return true;
        if (!run.Cover) return targetDist > HuntDropRange || offNose > 110f;
        if (targetDist > 7000f) return true;
        var mate = run.Protecting;
        if (mate == null || mate.disabled) return true;
        Vector3 toMate = mate.transform.position - run.Target.transform.position;
        bool stillOnTail = toMate.magnitude < 2500f && Vector3.Angle(mate.transform.forward, -toMate) > 120f;
        return !stillOnTail && offNose > 110f;
    }

    /// <summary>Another ace already hunting the same target from closer in, if any.</summary>
    private static Aircraft SquadmateNearerOn(Aircraft a, Aircraft target, float ourDist)
    {
        foreach (var pair in Active)
        {
            var other = pair.Key;
            if (other == a || other == null || other.disabled || other == target || other.NetworkHQ != a.NetworkHQ) continue; // same side only
            var r = pair.Value;
            if (r.Kind != Manoeuvre.Hunt || r.Target != target) continue;
            if (Vector3.Distance(other.transform.position, target.transform.position) < ourDist) return other;
        }
        return null;
    }

    /// <summary>A squadmate in the line of fire (within 6 deg of the nose and nearer than the target): hold fire.</summary>
    private static bool FriendlyInLine(Aircraft a, Vector3 fwd, float targetDist)
    {
        foreach (var m in Encounters.SquadAircraft())
        {
            if (m == a) continue;
            Vector3 rel = m.transform.position - a.transform.position;
            if (rel.magnitude < targetDist + 200f && Vector3.Angle(fwd, rel) < 6f) return true;
        }
        return false;
    }

    /// <summary>After an offensive manoeuvre: if the target is now in front and close, keep hunting instead of recovering.</summary>
    private static bool ContinueAsHunt(Aircraft a, Run run, float targetDist, float offNose, float speed)
    {
        if (!Plugin.AceHunt.Value || run.Defensive || run.Kind == Manoeuvre.Hunt || run.Target == null || run.Target.disabled) return false;
        float corner = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
        if (targetDist > 3000f || offNose > 100f || speed < Mathf.Max(55f, corner * 0.4f) || a.radarAlt < 600f) return false;
        Plugin.Log.LogInfo($"{Callsign(a)}: {run.Kind} flows into the hunt, target {targetDist:0} m, {offNose:0} deg off the nose.");
        run.Kind = Manoeuvre.Hunt;
        run.Phase = 0;
        run.PhaseStart = Time.time;
        run.MissileFired = false;
        return true;
    }

    /// <summary>Any loaded air-to-air missile station that can reach <paramref name="dist"/> (not only the one the AI has selected).</summary>
    private static bool MissileReady(Aircraft a, float dist)
    {
        if (a.weaponStations == null) return false;
        foreach (var station in a.weaponStations)
        {
            var info = station?.WeaponInfo;
            if (info == null || !info.missile || info.bomb || info.gun || station.Cargo || station.Ammo <= 0 || info.effectiveness.antiAir < 0.5f || info.effectiveness.antiAir <= info.effectiveness.antiSurface) continue;
            var req = info.targetRequirements;
            if (dist < req.maxRange * 0.85f && dist > Mathf.Max(req.minRange, 800f)) return true;
        }
        return false;
    }

    private static int AmmoLeft(Aircraft a)
    {
        int n = 0;
        if (a.weaponStations != null)
            foreach (var s in a.weaponStations)
                if (s != null && !s.Cargo) n += s.Ammo;
        return n;
    }

    private static int MissilesLeft(Aircraft a)
    {
        int n = 0;
        if (a.weaponStations != null)
            foreach (var s in a.weaponStations)
                if (s?.WeaponInfo != null && !s.WeaponInfo.gun && !s.Cargo) n += s.Ammo;
        return n;
    }

    /// <summary>
    /// The payoff of a nose-pointing manoeuvre: guns when the lead-corrected nose is within 4 deg of a target inside
    /// 1.2 km; further out, one air-to-air missile at it (once per manoeuvre), fired the way the AI fires one.
    /// </summary>
    private static void GunsIfOnTarget(Aircraft a, Run run, Vector3 fwd, Vector3 lookDir)
    {
        if (run.Target == null || run.Target.disabled) return;
        float dist = Vector3.Distance(run.Target.transform.position, a.transform.position);
        float off = Vector3.Angle(fwd, lookDir);
        if (off < run.BestAim) run.BestAim = off;
        if (off > 10f || FriendlyInLine(a, fwd, dist)) return;
        var profile = AirframeProfiles.For(a);
        bool hasGun = profile == null || profile.Guns > 0;
        if (hasGun && dist <= 1200f)
        {
            if (off <= 4f) try { a.weaponManager?.FireGuns(); } catch { /* no guns */ }
            return;
        }
        // Snap missiles: again and again while the shot holds, sooner for a jet that has nothing else.
        if (Time.time < run.NextMissile) return;
        var wm = a.weaponManager;
        if (wm == null || a.weaponStations == null) return;
        foreach (var station in a.weaponStations)
        {
            var info = station?.WeaponInfo;
            if (info == null || !info.missile || info.bomb || info.gun || station.Cargo || station.Ammo <= 0 || info.effectiveness.antiAir < 0.5f || info.effectiveness.antiAir <= info.effectiveness.antiSurface) continue;
            var req = info.targetRequirements;
            if (dist > req.maxRange * 0.9f || dist < Mathf.Max(req.minRange, 300f)) continue;
            try
            {
                wm.ClearTargetList();
                wm.AddTargetList(run.Target);
                wm.SetActiveStation(station.Number);
                wm.Fire();
                run.MissileFired = true;
                run.NextMissile = Time.time + (hasGun ? 6f : 3f);
                Plugin.Log.LogInfo($"{Callsign(a)}: {run.Kind} snap shot, {info.weaponName} at {dist:0} m, {off:0.0} deg off.");
                Encounters.Taunt(a, "snap");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Snap shot failed: " + ex.Message);
                run.MissileFired = true;
                run.NextMissile = Time.time + 6f;
            }
            return;
        }
    }

    private static void Begin(Aircraft a, Manoeuvre m, Aircraft target, bool vectoring, bool forced, Unit threat = null, string why = null)
    {
        float now = Time.time;
        Vector3 flatFwd = Formations.FlatForward(a, a.transform.forward);
        bool defensive = threat != null;
        Active[a] = new Run
        {
            Kind = m,
            Start = now,
            PhaseStart = now,
            Target = target ?? Encounters.ActiveTarget,
            Threat = threat,
            FallbackDir = m is Manoeuvre.Herbst or Manoeuvre.Bell ? -flatFwd : Quaternion.AngleAxis(-60f, Vector3.up) * flatFwd,
            Vectoring = vectoring,
            Forced = forced,
            Defensive = defensive,
            // Flares on every manoeuvre: a rapid string when a missile is coming, a few for show otherwise.
            FlaresLeft = defensive ? 10 : m == Manoeuvre.Hunt ? 0 : 4,
            AmmoAtStart = AmmoLeft(a),
            MissilesAtStart = MissilesLeft(a),
        };
        if (!defensive && m != Manoeuvre.Hunt) Cooldown[(a, m)] = now + (m == Manoeuvre.Cobra ? 20f : 30f);
        if (defensive) DefencesFlown++; else if (m == Manoeuvre.Hunt) HuntsFlown++; else AttacksFlown++;
        Plugin.Log.LogInfo($"{Callsign(a)}: {(defensive ? "defensive " : "")}{m}{(forced ? " (forced)" : "")} at {a.speed * 3.6f:0} km/h, " +
                           $"{a.radarAlt:0} m{(why != null ? "; " + why : "")}.");
    }

    /// <summary>Debug: make the active squadron's lead fly a manoeuvre now (still refuses below 1,000 m).</summary>
    /// <summary>Record mode: no manoeuvres of the aces' own choosing while a staged one is set up.</summary>
    internal static bool StagingHold;
    /// <summary>Record mode: a staged Kulbit may take this long to bring the nose round (0 = the usual 1.4 s).</summary>
    internal static float StagedSnapSeconds;

    internal static string Force(Manoeuvre m, float minAltitude = 1000f)
    {
        var lead = Encounters.ActiveLead;
        if (lead == null) return "No active ace to fly it.";
        if (lead.radarAlt < minAltitude) return $"{Callsign(lead)} is too low ({lead.radarAlt:0} m) for a {m}.";
        if (Active.ContainsKey(lead)) return $"{Callsign(lead)} is already flying a {Active[lead].Kind}.";
        Begin(lead, m, Encounters.ActiveTarget, AirframeChecklist.IsPostStallCapable(lead.definition), true);
        return $"{Callsign(lead)}: {m}.";
    }

    private static string Callsign(Aircraft a) => a.unitName ?? a.name;

    // ---- flying it ------------------------------------------------------------------------------------

    private static void Drive(Aircraft a, Run run)
    {
        float now = Time.time;
        float t = now - run.PhaseStart;
        var inputs = a.GetInputs();
        Rigidbody rb = a.CockpitRB() ?? a.rb;
        Transform tr = rb.transform;
        Vector3 fwd = tr.forward, up = tr.up;
        Vector3 vel = a.rb.velocity;
        Vector3 localVel = tr.InverseTransformDirection(vel);
        float aoa = Mathf.Atan2(-localVel.y, localVel.z) * Mathf.Rad2Deg;
        float speed = vel.magnitude;

        // Anything dangerous: straight to the recovery.
        // Breaks and notches are level-flight defences and may be flown low; everything else aborts below the floor.
        bool lowOk = run.Kind is Manoeuvre.Break or Manoeuvre.Notch or Manoeuvre.Jink or Manoeuvre.Bait;
        // The bait runs at a ridge on purpose; once it is pulling up it finishes the pull (the recovery climbs too gently).
        float abortFloor = run.Kind == Manoeuvre.Bait ? (run.Phase == 1 ? 0f : 40f) : lowOk ? 150f : AbortAltitude;
        if (run.Phase < Recover && a.radarAlt < abortFloor)
        {
            Plugin.Log.LogInfo($"{Callsign(a)}: {run.Kind} aborted below {abortFloor:0} m.");
            NextPhase(run, Recover);
        }

        if (run.FlaresLeft > 0 && now >= run.NextFlare)
        {
            run.FlaresLeft--;
            run.NextFlare = now + (run.Defensive ? 0.25f : 0.4f);
            try { a.countermeasureManager?.PopFlares(); } catch { /* cosmetic and defensive extra only */ }
        }

        Vector3 omega;          // wanted world angular velocity
        float throttle = 1f;
        float pullLimit = 1f;   // scales nose-up pitch targets when the airframe is loaded
        if (a.gForce > Plugin.PostStallMaxG.Value || AceFlight.StressOf(a) > 0.75f) pullLimit = 0.25f;

        bool haveTarget = run.Target != null && !run.Target.disabled;
        float targetDist = haveTarget ? Vector3.Distance(run.Target.transform.position, a.transform.position) : 0f;
        // Lead by roughly the gun's time of flight (and a little more for missiles at range).
        Vector3 lookDir = haveTarget
            ? Lead(run.Target, a.transform.position, Mathf.Clamp(targetDist / 900f, 0.2f, 1.2f))
            : run.FallbackDir;
        float offNose = Vector3.Angle(fwd, lookDir);

        switch (run.Kind, run.Phase)
        {
            // Cobra ---------------------------------------------------------------------------------------
            case (Manoeuvre.Cobra, 0): // wings level, and bleed off speed at idle if it came in too fast
            {
                omega = KeepUp(fwd, up, 3f) + Turn(fwd, FlatOf(vel, fwd), 0.5f);
                bool fast = !run.Defensive && speed > CobraEntry && t < BleedLimit;
                throttle = fast ? 0f : 0.4f;
                if (run.Defensive || !fast && (BankAngle(fwd, up) < 12f || t > 0.6f)) NextPhase(run, 1);
                break;
            }
            case (Manoeuvre.Cobra, 1): // snap the nose up: +right angular velocity lowers the nose, -right raises it
                omega = KeepUp(fwd, up, 2f) + tr.right * (-2.6f * pullLimit);
                throttle = run.Vectoring ? 1f : 0.5f;
                if (aoa > 95f || t > 1.3f) NextPhase(run, 2);
                break;
            case (Manoeuvre.Cobra, 2): // drop it back; then, if the pursuer has gone past, go after it
                omega = KeepUp(fwd, up, 2f) + tr.right * 2.2f;
                throttle = 1f;
                if (aoa < 25f || t > 1.6f)
                {
                    if (haveTarget && !run.Defensive && targetDist < 3000f && offNose < 120f)
                    {
                        Plugin.Log.LogInfo($"{Callsign(a)}: pursuer overshot, {offNose:0} deg off the nose at {targetDist:0} m; going after it.");
                        Encounters.Taunt(a, "overshoot");
                        NextPhase(run, 3);
                    }
                    else NextPhase(run, Recover);
                }
                break;
            case (Manoeuvre.Cobra, 3): // pursue: nose onto the overshooting pursuer, guns when on, missile hold off
                omega = Turn(fwd, lookDir, 4f, 3f) + KeepUp(fwd, up, 0.8f);
                GunsIfOnTarget(a, run, fwd, lookDir);
                if (t > 3f || offNose > 130f) NextPhase(run, Recover);
                break;

            // Herbst --------------------------------------------------------------------------------------
            case (Manoeuvre.Herbst, 0): // too fast for a J-turn: idle, wings level, until slow enough
                omega = KeepUp(fwd, up, 3f) + Turn(fwd, FlatOf(vel, fwd), 0.5f);
                throttle = 0f;
                if (speed <= HerbstEntry || t > BleedLimit) NextPhase(run, 1);
                break;
            case (Manoeuvre.Herbst, 1): // pull into the stall
                omega = KeepUp(fwd, up, 2f) + tr.right * (-2.2f * pullLimit);
                if (aoa > 60f || t > 1.3f) NextPhase(run, 2);
                break;
            case (Manoeuvre.Herbst, 2): // swing the nose round in the stall
                omega = Turn(fwd, lookDir, 3f, 2.5f) + KeepUp(fwd, up, 1f);
                if (offNose < 25f) NextPhase(run, haveTarget ? 3 : Recover);
                else if (t > 2.5f) NextPhase(run, Recover);
                break;
            case (Manoeuvre.Herbst, 3): // hold the nose on for the shot (missile hold off, guns if close)
                omega = Turn(fwd, lookDir, 3.5f, 2.5f) + KeepUp(fwd, up, 0.8f);
                GunsIfOnTarget(a, run, fwd, lookDir);
                if (t > 1.5f) NextPhase(run, Recover);
                break;

            // Kulbit nose snap --------------------------------------------------------------------------
            case (Manoeuvre.Kulbit, 0): // snap onto the target
                omega = Turn(fwd, lookDir, 4f, 3f) + KeepUp(fwd, up, 0.8f);
                if (Vector3.Angle(fwd, lookDir) < 6f || t > (StagedSnapSeconds > 0f ? StagedSnapSeconds : 1.4f)) NextPhase(run, 1);
                break;
            case (Manoeuvre.Kulbit, 1): // hold it there for the shot
                omega = Turn(fwd, lookDir, 3f, 2f) + KeepUp(fwd, up, 0.8f);
                GunsIfOnTarget(a, run, fwd, lookDir);
                if (t > 0.7f) NextPhase(run, Recover);
                break;

            // Yaw-roll bell (bandit sitting on the tail) -------------------------------------------------
            // Pitch up and yaw toward the bandit's side together; when the stall horn would sound, roll the same
            // way as the yaw and keep it coming round, so the jet tumbles off the bandit's nose and ends up behind it.
            case (Manoeuvre.YawBell, 0):
                if (run.Side == 0f) run.Side = YawSide(a, run);
                omega = tr.right * (-2.2f * pullLimit) + tr.up * (1.2f * run.Side);
                throttle = 1f;
                if (aoa > StallHornAoA() || t > 2.5f)
                {
                    Plugin.Log.LogInfo($"{Callsign(a)}: stall horn at {aoa:0} deg AoA, {speed * 3.6f:0} km/h; rolling {(run.Side > 0f ? "right" : "left")}.");
                    NextPhase(run, 1);
                }
                break;
            case (Manoeuvre.YawBell, 1): // roll with the yaw, still pulling, until the nose comes round toward the bandit
                // -forward angular velocity rolls right (right wing down); +forward rolls left.
                omega = tr.forward * (-3f * run.Side) + tr.right * (-1.2f * pullLimit) + tr.up * (0.8f * run.Side);
                throttle = 1f;
                if (haveTarget && offNose < 70f || t > 2.5f) NextPhase(run, haveTarget ? 2 : Recover);
                break;
            case (Manoeuvre.YawBell, 2): // behind it now: nose on, guns or a snap shot
                omega = Turn(fwd, lookDir, 4f, 3f) + KeepUp(fwd, up, 0.8f);
                GunsIfOnTarget(a, run, fwd, lookDir);
                if (t > 3f || offNose > 130f) NextPhase(run, Recover);
                break;

            // Kvochur's Bell ----------------------------------------------------------------------------
            case (Manoeuvre.Bell, 0): // zoom: nose straight up, wings level, full power to carry the climb
                omega = Turn(fwd, Vector3.up, 2.2f, 1.6f * pullLimit) + KeepUp(fwd, up, 1.5f);
                if (fwd.y > 0.96f || t > 3f) NextPhase(run, 1);
                break;
            case (Manoeuvre.Bell, 1): // hang: hold vertical at idle until the climb dies
                omega = Turn(fwd, Vector3.up, 2f, 1f);
                throttle = 0.05f;
                if (vel.y < 5f || speed < 40f || t > 4f) NextPhase(run, 2);
                break;
            case (Manoeuvre.Bell, 2): // the bell: a moment of tail slide, attitude held, nothing commanded
                omega = Vector3.zero;
                throttle = 0.05f;
                if (t > 0.9f || vel.y < -25f) NextPhase(run, 3);
                break;
            case (Manoeuvre.Bell, 3): // fall over: let the nose drop onto the pursuer below (or just over the top)
            {
                Vector3 drop = haveTarget && Vector3.Dot(lookDir, Vector3.down) > -0.2f
                    ? lookDir
                    : (FlatOf(run.FallbackDir, fwd) - Vector3.up * 1.2f).normalized;
                omega = Turn(fwd, drop, 3f, 2.5f);
                throttle = 1f;
                if (haveTarget) GunsIfOnTarget(a, run, fwd, lookDir);
                if (Vector3.Angle(fwd, drop) < 8f && t > 0.8f || t > 3f) NextPhase(run, haveTarget ? 4 : Recover);
                break;
            }
            case (Manoeuvre.Bell, 4): // pounce: hold the nose on for the shot while the speed comes back
                omega = Turn(fwd, lookDir, 3.5f, 2.5f) + KeepUp(fwd, up, 0.5f);
                GunsIfOnTarget(a, run, fwd, lookDir);
                if (t > 1.6f || offNose > 60f) NextPhase(run, Recover);
                break;

            // Break (missile defence) -------------------------------------------------------------------
            case (Manoeuvre.Break, 0):
            {
                // Roll the lift vector toward the missile's beam, then pull hard through it.
                Vector3 threatDir = run.Threat != null && !run.Threat.disabled
                    ? (run.Threat.transform.position - a.transform.position).normalized
                    : -run.FallbackDir;
                Vector3 beam = fwd - threatDir * Vector3.Dot(fwd, threatDir); // nose square to the missile's line
                if (beam.sqrMagnitude < 1e-3f) beam = Vector3.Cross(threatDir, Vector3.up);
                beam.Normalize();
                // Low on energy, a level break just stalls: trade height for speed by breaking down through the beam.
                if (speed < 450f / 3.6f && a.radarAlt > 1500f)
                    beam = (beam + Vector3.down * 0.6f).normalized;
                // Low down, never break toward the ground.
                if (a.radarAlt < 1000f && beam.y < 0f) { beam.y = 0.05f; beam.Normalize(); }
                Vector3 wantUp = beam - fwd * Vector3.Dot(beam, fwd);
                if (wantUp.sqrMagnitude < 1e-3f) wantUp = up;
                Vector3 roll = Turn(up, wantUp.normalized, 4f, 4f);
                roll = fwd * Vector3.Dot(roll, fwd);
                bool aligned = Vector3.Angle(up, wantUp) < 35f;
                omega = roll + (aligned ? tr.right * (-2.6f * pullLimit) : Vector3.zero);
                // Against a radar missile the break turns any jamming pod on it (flares do nothing to it).
                if (run.Threat is Missile bm && !bm.disabled && IsRadarGuided(bm)) AceTactics.JamMissile(a, bm);
                if (t > 2.2f || aligned && Vector3.Angle(fwd, beam) < 15f && t > 0.8f) NextPhase(run, Recover);
                break;
            }

            // Bait: lead a pursuer at a ridge -----------------------------------------------------------------
            // Phase 0: wings level, straight at the ridge, holding height. Phase 1 starts when the ridge face is as
            // close as a pull at the available g needs to clear the crest by 60 m (times the margin, plus 0.3 s):
            // a full pull, wings level, until the flight path clears the top. Gives up (to the recovery) if the
            // pursuer stops chasing before the pull.
            case (Manoeuvre.Bait, 0):
            case (Manoeuvre.Bait, 1):
            {
                var chaser = run.Threat as Aircraft;
                Vector3 toFace = run.RidgeFace - a.transform.position;
                toFace.y = 0f;
                float faceDist = toFace.magnitude;
                float h = run.RidgeCrest + 60f - a.transform.position.y;
                float n = Mathf.Min(AceFlight.SafeG(a), 9f) * 0.85f;
                float r = speed * speed / (Mathf.Max(n, 2f) * 9.81f);
                float need = h <= 0f ? 0f : h < r ? Mathf.Sqrt(r * r - (r - h) * (r - h)) : r;
                if (chaser != null && chaser.disabled && run.Phase <= 1)
                {
                    BaitKills++;
                    Plugin.Log.LogInfo($"{Callsign(a)}: bait: {chaser.unitName} went down at {Time.time - run.Start:0.0} s.");
                    run.Threat = null;
                }
                if (run.Phase == 0)
                {
                    bool chasing = chaser != null && !chaser.disabled &&
                                   Vector3.Distance(chaser.transform.position, a.transform.position) < 2500f &&
                                   Vector3.Angle(chaser.transform.forward, a.transform.position - chaser.transform.position) < 35f;
                    if (!chasing && t > 1f) { NextPhase(run, Recover); omega = Vector3.zero; break; }
                    Vector3 level = toFace.sqrMagnitude > 1f ? toFace.normalized : FlatOf(fwd, fwd);
                    omega = KeepUp(fwd, up, 3f) + Turn(fwd, level, 1.2f);
                    throttle = 1f;
                    if (faceDist <= need * run.BaitMargin + speed * 0.3f || faceDist < 300f)
                    {
                        Plugin.Log.LogInfo($"{Callsign(a)}: bait: pulling up {faceDist:0} m from the ridge (needs {need:0} m at {n:0.0} g, {speed * 3.6f:0} km/h).");
                        NextPhase(run, 1);
                    }
                    break;
                }
                // Pull: wings level, full nose-up; done when the climb clears the crest or we are above it and climbing.
                omega = KeepUp(fwd, up, 3f) + tr.right * (-2.6f * pullLimit);
                throttle = 1f;
                float gamma = Mathf.Asin(Mathf.Clamp(vel.y / Mathf.Max(speed, 1f), -1f, 1f));
                float pathAtFace = a.transform.position.y + Mathf.Tan(gamma) * Mathf.Max(faceDist, 0f);
                if (a.transform.position.y > run.RidgeCrest + 40f && vel.y > 0f || pathAtFace > run.RidgeCrest + 80f && gamma > 0.2f || t > 6f)
                    NextPhase(run, Recover);
                break;
            }

            // Zoom: the energy trap ------------------------------------------------------------------------
            // Phase 0: wings level, nose 70 deg up, full power, away from a chaser that climbs worse. When the chaser
            // runs out of speed (under 320 km/h) or we near ours (75% of corner speed, at least 330 km/h), phase 1:
            // roll the lift onto them and pull down onto them; once they are within 30 deg of the nose the hunt (or
            // the recovery) takes over.
            case (Manoeuvre.Zoom, 0):
            case (Manoeuvre.Zoom, 1):
            {
                var chaser = run.Threat as Aircraft;
                bool live = chaser != null && !chaser.disabled;
                if (!live) { NextPhase(run, Recover); omega = Vector3.zero; break; }
                float cornerZ = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
                if (run.Phase == 0)
                {
                    Vector3 flatZ = FlatOf(vel.sqrMagnitude > 2500f ? vel : fwd, fwd);
                    Vector3 climb = Quaternion.AngleAxis(-70f, Vector3.Cross(Vector3.up, flatZ).normalized) * flatZ;
                    omega = Turn(fwd, climb, 2.5f, 2f) + KeepUp(fwd, up, 1f);
                    throttle = 1f;
                    float chaserSpeed = chaser.speed;
                    if (chaserSpeed < 320f / 3.6f || speed < Mathf.Max(330f / 3.6f, cornerZ * 0.75f) || t > 8f)
                    {
                        Plugin.Log.LogInfo($"{Callsign(a)}: zoom topped out at {speed * 3.6f:0} km/h after {t:0.0} s; {chaser.unitName} at {chaserSpeed * 3.6f:0} km/h, " +
                                           $"{chaser.GlobalPosition().y - a.GlobalPosition().y:0} m relative height. Coming back down on it.");
                        NextPhase(run, 1);
                    }
                    break;
                }
                Vector3 toChaser = (chaser.transform.position - a.transform.position).normalized;
                Vector3 liftTo = toChaser - fwd * Vector3.Dot(toChaser, fwd);
                Vector3 rollZ = liftTo.sqrMagnitude > 1e-4f ? Turn(up, liftTo.normalized, 5f, 4f) : Vector3.zero;
                rollZ = fwd * Vector3.Dot(rollZ, fwd);
                float liftOffZ = liftTo.sqrMagnitude > 1e-4f ? Vector3.Angle(up, liftTo) : 0f;
                omega = rollZ + tr.right * (-2.2f * pullLimit * (liftOffZ > 70f ? 0.3f : 1f));
                throttle = 1f;
                if (Vector3.Angle(fwd, toChaser) < 30f || t > 6f) NextPhase(run, Recover);
                break;
            }

            // Jink: spoil a gun solution from behind ------------------------------------------------------
            // Phase 0, the break: roll the lift toward the side the attacker sits on (straight behind: either side)
            // and pull hard across their line of fire. Phase 1 (tier 2 and up), the jink: while they stay behind and
            // pointing, a new lift direction every 0.8-1.5 s, a quarter to a half turn away, pulled hard, so the
            // aim never settles. Both end when the attacker loses the solution, overshoots or falls back.
            case (Manoeuvre.Jink, 0):
            case (Manoeuvre.Jink, 1):
            {
                var attacker = run.Threat as Aircraft;
                bool live = attacker != null && !attacker.disabled;
                Vector3 rel = live ? attacker.transform.position - a.transform.position : -fwd * 1000f;
                float dist = rel.magnitude;
                Vector3 dir = rel / Mathf.Max(dist, 1f);
                bool behind = Vector3.Angle(fwd, dir) > 90f;
                bool aiming = live && Vector3.Angle(attacker.transform.forward, -dir) < 25f;
                if (!live || !behind || dist > 3000f || !aiming && t > 1f) { NextPhase(run, Recover); omega = Vector3.zero; break; }

                // Outmatched: dive away from them and accelerate, swerving a little either way every 1.5 s.
                if (run.Extend)
                {
                    Vector3 away = -dir;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.01f) away = FlatOf(fwd, fwd);
                    away.Normalize();
                    if (now >= run.NextJink)
                    {
                        run.JinkUp = Quaternion.AngleAxis(UnityEngine.Random.Range(-25f, 25f), Vector3.up) * away;
                        run.NextJink = now + 1.5f;
                    }
                    Vector3 course = run.JinkUp == Vector3.zero ? away : run.JinkUp;
                    float diveDeg = a.radarAlt > 2500f ? 25f : a.radarAlt > 1200f ? 10f : 0f;
                    Vector3 wantedE = Quaternion.AngleAxis(diveDeg, Vector3.Cross(Vector3.up, course).normalized) * course; // + = nose down, as in the recovery
                    omega = Turn(fwd, wantedE, 2.5f, 2f) + KeepUp(fwd, up, 2f);
                    throttle = 1f;
                    if (t > 10f) NextPhase(run, Recover);
                    break;
                }

                if (run.Phase == 0 && run.JinkUp == Vector3.zero)
                {
                    Vector3 side = dir - fwd * Vector3.Dot(dir, fwd);
                    if (side.sqrMagnitude < 0.01f) side = tr.right * (UnityEngine.Random.value < 0.5f ? 1f : -1f);
                    run.JinkUp = side.normalized;
                }
                if (run.Phase == 1 && now >= run.NextJink)
                {
                    float turn = UnityEngine.Random.Range(90f, 150f) * (UnityEngine.Random.value < 0.5f ? 1f : -1f);
                    run.JinkUp = Quaternion.AngleAxis(turn, fwd) * (run.JinkUp == Vector3.zero ? up : run.JinkUp);
                    run.NextJink = now + UnityEngine.Random.Range(1.2f, 2.0f);
                }
                // Low down, never pull toward the ground.
                Vector3 wantUp = run.JinkUp - fwd * Vector3.Dot(run.JinkUp, fwd);
                if (a.radarAlt < 1000f && wantUp.y < 0.2f) wantUp.y = 0.2f;
                if (wantUp.sqrMagnitude < 1e-3f) wantUp = up;
                wantUp.Normalize();
                Vector3 rollPart = Turn(up, wantUp, 4f, 4f);
                rollPart = fwd * Vector3.Dot(rollPart, fwd);
                // Roll and pull at once, the pull growing as the lift comes round (a held pull with no roll is what the
                // stock AI does, and an attacker tracks that easily).
                float misalign = Vector3.Angle(up, wantUp);
                bool aligned = misalign < 40f;
                float pullShare = Mathf.Clamp01(Mathf.Cos(misalign * Mathf.Deg2Rad) * 0.7f + 0.3f);
                float pull = run.TierScale >= 1f ? 1f : run.TierScale; // tier 1: 0.6, tier 2: 0.8 of the hard pull
                // Energy: ease the jinks as the speed falls toward 85% of corner speed (a slow jet is an easy kill).
                float cornerJ = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
                pull *= Mathf.Lerp(0.3f, 1f, Mathf.Clamp01((speed - cornerJ * 0.85f) / (cornerJ * 0.3f)));
                omega = rollPart + tr.right * (-2.6f * pullLimit * pull * pullShare);
                // Closing fast inside 900 m: chop the power so they overshoot.
                float closingJ = live && attacker.rb != null ? -Vector3.Dot(attacker.rb.velocity - vel, dir) : 0f;
                throttle = dist < 900f && closingJ > 30f && speed > 650f / 3.6f ? 0.2f : 1f;
                if (run.Phase == 0 && (t > 2.2f || aligned && t > 1.2f))
                {
                    if (run.TierScale < 0.7f) NextPhase(run, Recover); // tier 1: the break only
                    else { NextPhase(run, 1); run.NextJink = now; }
                }
                else if (run.Phase == 1 && t > 8f) NextPhase(run, Recover);
                break;
            }

            // Notch: beam a radar missile ----------------------------------------------------------------
            case (Manoeuvre.Notch, 0):
            {
                bool missileLive = run.Threat != null && !run.Threat.disabled;
                // What to beam: an active seeker (ARH) sees us from the missile; a semi-active one (SARH) rides the
                // launcher's radar, so the Doppler that matters is along the line to the launcher.
                Unit beamFrom = run.Threat;
                if (missileLive && run.Threat is Missile sm && (sm.GetSeekerType() ?? "") == "SARH" && sm.owner != null && !sm.owner.disabled)
                    beamFrom = sm.owner;
                Vector3 threatDir = missileLive && beamFrom != null ? (beamFrom.transform.position - a.transform.position).normalized : -run.FallbackDir;
                // Level beam: square to the missile's line, on the side nearer the present heading.
                Vector3 flatThreat = new Vector3(threatDir.x, 0f, threatDir.z);
                if (flatThreat.sqrMagnitude < 1e-3f) flatThreat = Vector3.forward;
                Vector3 beam = Vector3.Cross(Vector3.up, flatThreat.normalized);
                if (Vector3.Dot(beam, fwd) < 0f) beam = -beam;
                // Down into the ground clutter: a radar seeker looking down at a low target loses it in the return
                // from the ground (the clutter term grows as the target's height above ground falls and its depth
                // below the seeker grows). Steep while high, levelling out at about 250 m; steeper while slow, to buy
                // speed back.
                float notchCorner = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
                float sink = -vel.y; // m/s, + = going down
                float dive = a.radarAlt > 1500f ? 0.4f : a.radarAlt > 700f ? 0.22f : a.radarAlt > 350f ? 0.08f : -0.12f;
                if (a.radarAlt < sink * 6f) dive = Mathf.Min(dive, -0.1f); // under six seconds from the ground at this sink: pull out
                if (speed < notchCorner * 1.1f && a.radarAlt > 1200f) dive += 0.15f;
                Vector3 want = (beam + Vector3.down * dive).normalized;
                Vector3 wantUp = want - fwd * Vector3.Dot(want, fwd);
                Vector3 roll = wantUp.sqrMagnitude > 1e-3f ? Turn(up, wantUp.normalized, 4f, 4f) : Vector3.zero;
                roll = fwd * Vector3.Dot(roll, fwd);
                float offBeam = Vector3.Angle(fwd, want);
                bool aligned = wantUp.sqrMagnitude < 1e-3f || Vector3.Angle(up, wantUp) < 40f;
                // A sustained turn onto the beam, not a slice: the pull is held under the airframe's AoA limit and
                // eases off below 90% of corner speed (a notch flown at 350 km/h leaves nothing for the break).
                var notchProfile = AirframeProfiles.For(a);
                float notchCap = notchProfile?.AlphaLimit ?? 20f;
                float notchPull = Mathf.Min(offBeam * Mathf.Deg2Rad * 2f, 1.2f) * pullLimit;
                notchPull *= Mathf.Clamp01((notchCap - aoa) / 4f);
                notchPull *= Mathf.Lerp(0.3f, 1f, Mathf.InverseLerp(notchCorner * 0.8f, notchCorner * 0.95f, speed));
                omega = offBeam < 8f ? Turn(fwd, want, 2f, 0.6f) + KeepUp(fwd, up, 1.5f)
                                     : roll + (aligned ? tr.right * -notchPull : Vector3.zero);
                throttle = 1f;
                // Any jamming pod turned on the missile itself (its seeker counts jamming aimed at it).
                if (missileLive && run.Threat is Missile jm) AceTactics.JamMissile(a, jm);
                // Done when the missile is gone, or hand over to the last-second break.
                float impactNow = 99f;
                if (missileLive)
                {
                    Vector3 rel = run.Threat.transform.position - a.transform.position;
                    Vector3 mv = run.Threat.rb != null ? run.Threat.rb.velocity : Vector3.zero;
                    float closing = -Vector3.Dot(mv - vel, rel.normalized);
                    impactNow = closing > 1f ? rel.magnitude / closing : 99f;
                }
                if (!missileLive || t > 12f || a.radarAlt < 180f || impactNow < 2f)
                {
                    Plugin.Log.LogInfo($"{Callsign(a)}: notch {(missileLive ? impactNow < 2f ? "handing to the break" : "timed out" : "worked, missile gone")} after {t:0.0} s.");
                    NextPhase(run, Recover);
                }
                break;
            }

            // Hunt: close-range dogfighting at the airframe's limit -----------------------------------
            // Crank: off the bandit's nose instead of a joust ------------------------------------------
            case (Manoeuvre.Crank, 0):
            {
                if (haveTarget) LongShot(a, run, fwd);
                Vector3 bearing = haveTarget ? run.Target.transform.position - a.transform.position : fwd;
                bearing.y = 0f;
                if (bearing.sqrMagnitude < 1f) bearing = FlatOf(vel, fwd);
                Vector3 crank = Quaternion.AngleAxis(55f * run.Side, Vector3.up) * bearing.normalized;
                float drop = a.radarAlt > 2500f ? 0.08f : a.radarAlt < 1200f ? -0.05f : 0f;
                Vector3 want = (crank + Vector3.down * drop).normalized;
                Vector3 wantUp = want - fwd * Vector3.Dot(want, fwd);
                Vector3 rollW = wantUp.sqrMagnitude > 1e-3f ? Turn(up, wantUp.normalized, 3f, 2.5f) : Vector3.zero;
                rollW = fwd * Vector3.Dot(rollW, fwd);
                float offWant = Vector3.Angle(fwd, want);
                bool rolled = wantUp.sqrMagnitude < 1e-3f || Vector3.Angle(up, wantUp) < 45f;
                omega = offWant < 6f ? Turn(fwd, want, 2f, 0.5f) + KeepUp(fwd, up, 1.5f)
                                     : rollW + (rolled ? tr.right * -Mathf.Min(offWant * Mathf.Deg2Rad * 1.5f, 1f) * pullLimit : Vector3.zero);
                throttle = 1f;
                float aspectNow = haveTarget ? Vector3.Angle(run.Target.transform.forward, a.transform.position - run.Target.transform.position) : 180f;
                float dragAt = DragRange(run.Target);
                if (!haveTarget || targetDist < 4000f || aspectNow > 70f || now - run.Start > 60f || a.radarAlt < 700f)
                {
                    Plugin.Log.LogInfo($"{Callsign(a)}: crank done after {now - run.Start:0.0} s, bandit {targetDist:0} m, {aspectNow:0} deg off its nose.");
                    NextPhase(run, Recover);
                }
                else if (targetDist < dragAt && aspectNow < 35f && t > 2f)
                {
                    // Still coming, now inside its missile's reach: turn tail and make it chase.
                    Plugin.Log.LogInfo($"{Callsign(a)}: dragging, bandit {targetDist:0} m (its missiles reach about {dragAt / 0.6f:0} m).");
                    run.Side = targetDist; // remembered: the drag ends once the range has opened past it
                    NextPhase(run, 1);
                }
                break;
            }
            case (Manoeuvre.Crank, 1): // drag: tail to the bandit, full power, a little lower, until the range opens
            {
                Vector3 away = haveTarget ? a.transform.position - run.Target.transform.position : fwd;
                away.y = 0f;
                if (away.sqrMagnitude < 1f) away = FlatOf(vel, fwd);
                float drop = a.radarAlt > 2000f ? 0.1f : a.radarAlt < 1000f ? -0.05f : 0f;
                Vector3 want = (away.normalized + Vector3.down * drop).normalized;
                Vector3 wantUp = want - fwd * Vector3.Dot(want, fwd);
                Vector3 rollW = wantUp.sqrMagnitude > 1e-3f ? Turn(up, wantUp.normalized, 3f, 2.5f) : Vector3.zero;
                rollW = fwd * Vector3.Dot(rollW, fwd);
                float offWant = Vector3.Angle(fwd, want);
                bool rolled = wantUp.sqrMagnitude < 1e-3f || Vector3.Angle(up, wantUp) < 45f;
                omega = offWant < 6f ? Turn(fwd, want, 2f, 0.5f) + KeepUp(fwd, up, 1.5f)
                                     : rollW + (rolled ? tr.right * -Mathf.Min(offWant * Mathf.Deg2Rad * 1.5f, 1.2f) * pullLimit : Vector3.zero);
                throttle = 1f;
                float aspectNow = haveTarget ? Vector3.Angle(run.Target.transform.forward, a.transform.position - run.Target.transform.position) : 180f;
                if (!haveTarget || a.radarAlt < 600f || now - run.Start > 60f) NextPhase(run, Recover);
                else if (targetDist > run.Side + 2000f || aspectNow > 60f || t > 12f)
                {
                    Plugin.Log.LogInfo($"{Callsign(a)}: pumping back in, bandit {targetDist:0} m, {aspectNow:0} deg off its nose.");
                    NextPhase(run, 2);
                }
                break;
            }
            case (Manoeuvre.Crank, 2): // pump: back round onto the bandit, then crank again (or the hunt takes it close in)
            {
                Vector3 wantUp = lookDir - fwd * Vector3.Dot(lookDir, fwd);
                Vector3 rollW = wantUp.sqrMagnitude > 1e-4f ? Turn(up, wantUp.normalized, 4f, 3f) : Vector3.zero;
                rollW = fwd * Vector3.Dot(rollW, fwd);
                bool rolled = wantUp.sqrMagnitude < 1e-4f || Vector3.Angle(up, wantUp) < 60f;
                omega = rollW + (rolled ? tr.right * -1.2f * pullLimit : Vector3.zero);
                throttle = 1f;
                if (!haveTarget || a.radarAlt < 600f || now - run.Start > 60f) NextPhase(run, Recover);
                else if (offNose < 40f)
                {
                    if (targetDist < 4500f) NextPhase(run, Recover); // close enough: the hunt takes it
                    else
                    {
                        Vector3 rel = run.Target.transform.position - a.transform.position;
                        run.Side = Vector3.Dot(Vector3.Cross(Vector3.up, a.transform.forward), rel) > 0f ? -1f : 1f;
                        run.MissileFired = false; // a fresh long shot on the way back in
                        NextPhase(run, 0);
                    }
                }
                break;
            }

            case (Manoeuvre.Hunt, 1): // extend: run out level-ish at full power, a slight climb, until fast and clear
            {
                float corner = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
                Vector3 outbound = (FlatOf(vel, fwd) + Vector3.up * 0.09f).normalized;
                omega = Turn(fwd, outbound, 1.5f, 1.2f) + KeepUp(fwd, up, 2f);
                if (haveTarget) AceTactics.TryParthian(a, run.Target); // fire back over the shoulder while running out
                throttle = 1f;
                if (a.radarAlt < 500f) NextPhase(run, Recover);
                else if (targetDist > 3000f && speed > corner * 1.15f && t > 3f || t > 10f || !haveTarget) NextPhase(run, 2);
                break;
            }
            case (Manoeuvre.Hunt, 2): // come back round onto the target at a sustainable rate, then hunt again
            {
                Vector3 wantUp = lookDir - fwd * Vector3.Dot(lookDir, fwd);
                Vector3 rollW = wantUp.sqrMagnitude > 1e-4f ? Turn(up, wantUp.normalized, 4f, 3f) : Vector3.zero;
                rollW = fwd * Vector3.Dot(rollW, fwd);
                bool rolled = wantUp.sqrMagnitude < 1e-4f || Vector3.Angle(up, wantUp) < 60f;
                omega = rollW + (rolled ? tr.right * -1.2f * pullLimit : Vector3.zero);
                throttle = 1f;
                if (a.radarAlt < 500f) NextPhase(run, Recover);
                else if (haveTarget && offNose < 45f)
                {
                    Plugin.Log.LogInfo($"{Callsign(a)}: back on the target, {targetDist:0} m; hunting again.");
                    NextPhase(run, 0);
                }
                else if ((t > 12f || !haveTarget) && run.BlendStart < 0f) run.BlendStart = now;
                break;
            }
            case (Manoeuvre.Hunt, < Recover):
            {
                float corner = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
                bool slow = speed < corner * 0.6f;
                Vector3 aim = lookDir;
                float closingT = 0f;
                if (haveTarget)
                {
                    Vector3 tv = run.Target.rb != null ? run.Target.rb.velocity : Vector3.zero;
                    Vector3 los = (run.Target.transform.position - a.transform.position).normalized;
                    closingT = -Vector3.Dot(tv - vel, los);
                    // Too close and closing fast: lag pursuit, aim behind them so as not to fly past into their sights.
                    if (targetDist < 400f && closingT > 60f)
                        aim = (run.Target.transform.position - tv * 0.6f - a.transform.position).normalized;
                    // Yo-yos (tier 2 up): closing too fast with them well off the nose, take the pull out of the
                    // horizontal (high yo-yo) to bleed closure and avoid the overshoot; falling behind with them off the
                    // nose, cut inside below (low yo-yo) to trade height for the speed and angle to catch up.
                    else if (run.TierScale >= 0.8f && targetDist < 2000f && a.radarAlt > 1200f)
                    {
                        float offT = Vector3.Angle(fwd, aim);
                        string yoyo = null;
                        if (closingT > 40f && offT > 25f && offT < 100f) { aim = (aim + Vector3.up * 0.35f).normalized; yoyo = "high"; }
                        else if (closingT < -15f && offT > 20f && offT < 80f) { aim = (aim - Vector3.up * 0.3f).normalized; yoyo = "low"; }
                        if (yoyo != null && run.YoyoLogged != yoyo)
                        {
                            run.YoyoLogged = yoyo;
                            Plugin.Log.LogInfo($"{Callsign(a)}: {yoyo} yo-yo on {run.Target.unitName}, {targetDist:0} m, closing {closingT:0} m/s, {offT:0} deg off.");
                        }
                    }

                    // Pincer: a squadmate nearer the same target takes it head on; this one swings out to the far
                    // flank until 1.5 km out, so the target cannot turn into both.
                    if (targetDist > 1500f && SquadmateNearerOn(a, run.Target, targetDist) is { } mate)
                    {
                        Vector3 tPos = run.Target.transform.position;
                        Vector3 mateLine = tPos - mate.transform.position;
                        mateLine.y = 0f;
                        Vector3 flank = Vector3.Cross(Vector3.up, mateLine.sqrMagnitude > 1f ? mateLine.normalized : fwd);
                        float s = Mathf.Sign(Vector3.Dot(a.transform.position - tPos, flank));
                        if (s == 0f) s = 1f;
                        Vector3 point = tPos + flank * s * Mathf.Min(targetDist * 0.4f, 1500f) * Mathf.Clamp01((targetDist - 1500f) / 1500f);
                        aim = (point - a.transform.position).normalized;
                        if (!run.PincerCalled)
                        {
                            run.PincerCalled = true;
                            Plugin.Log.LogInfo($"{Callsign(a)}: pincer with {Callsign(mate)}, swinging {(s > 0 ? "right" : "left")} flank.");
                            Encounters.Taunt(a, "pincer");
                        }
                    }
                }
                // The merge (tier 2 up): passing close and fast, pick the circle. Turning toward their tail (two-circle,
                // what the hunt does anyway) suits a jet that turns faster; turning away first (one-circle, the two
                // jets nose to nose on the same circle) suits one with the tighter radius but no rate advantage.
                if (haveTarget && run.TierScale >= 0.8f && !run.MergeChosen && targetDist < 1500f && offNose > 70f && closingT > 100f)
                {
                    run.MergeChosen = true;
                    var mp = EnergyFight.Compare(a, run.Target);
                    float rOurs = TurnRadius(a), rTheirs = TurnRadius(run.Target);
                    bool oneCircle = !mp.OutTurns && rOurs < rTheirs * 0.9f;
                    if (oneCircle) run.OneCircleUntil = now + 3.5f;
                    Plugin.Log.LogInfo($"{Callsign(a)}: merge with {run.Target.unitName}: {(oneCircle ? "one-circle" : "two-circle")} " +
                                       $"(radius {rOurs:0} m against {rTheirs:0} m; {mp}).");
                }
                if (haveTarget && now < run.OneCircleUntil)
                {
                    Vector3 right = tr.right;
                    aim = (aim - 2f * right * Vector3.Dot(aim, right)).normalized; // the mirror turn: away from them
                }
                // Lower tiers aim less cleanly.
                if (run.TierScale < 1f)
                {
                    float wobble = (1f - run.TierScale) * 6f * Mathf.Sin(t * 1.7f + a.GetInstanceID());
                    aim = Quaternion.AngleAxis(wobble, up) * aim;
                }
                float off = Vector3.Angle(fwd, aim);
                if (off > 10f)
                {
                    // Fighter-pilot geometry: roll the lift vector onto the target, then pull as hard as the jet takes.
                    Vector3 wantUp = aim - fwd * Vector3.Dot(aim, fwd);
                    Vector3 rollW = wantUp.sqrMagnitude > 1e-4f ? Turn(up, wantUp.normalized, 5f, 4f) : Vector3.zero;
                    rollW = fwd * Vector3.Dot(rollW, fwd);
                    float liftOff = wantUp.sqrMagnitude > 1e-4f ? Vector3.Angle(up, wantUp) : 0f;
                    // Pull only as hard as the fight needs: gently at range (energy is life), everything close in.
                    var profile = AirframeProfiles.For(a);
                    float maxRate = (targetDist > 2500f ? 0.9f : targetDist > 1200f ? 1.8f : 3f) * (profile?.HuntRateScale ?? 1f);
                    float pitchRate = Mathf.Min(off * Mathf.Deg2Rad * 3f, maxRate) * pullLimit;
                    // Losing speed: scale the pull down toward 35% as the jet drops from 0.85 to 0.55 of corner
                    // speed, unless the shot is nearly there.
                    float floor = profile?.EnergyFloor ?? 0.55f;
                    // Against a jet that out-turns us, never bleed into a slow turning fight; against one we
                    // out-turn, the turn is ours to take.
                    if (haveTarget)
                    {
                        var picture = EnergyFight.Compare(a, run.Target);
                        if (picture.OutTurned) floor = Mathf.Max(floor, 0.72f);
                        else if (picture.OutTurns) floor = Mathf.Min(floor, 0.5f);
                    }
                    float energy = Mathf.Clamp01((speed - corner * floor) / (corner * 0.3f));
                    if (off > 20f) pitchRate *= Mathf.Lerp(0.35f, 1f, energy);
                    pitchRate *= run.TierScale;
                    if (liftOff > 70f) pitchRate *= 0.3f;    // roll first, then pull
                    // AoA governor: past the airframe's useful AoA the pull only bleeds speed (or departs), so it
                    // eases off there. Vectoring jets fly well past it; a close snap shot is allowed a little more.
                    float aoaCap = HuntAoACap(profile, targetDist < 1200f && off < 30f);
                    if (aoa > aoaCap) pitchRate *= Mathf.Clamp01(1f - (aoa - aoaCap) / 6f);
                    omega = rollW + tr.right * -pitchRate;
                }
                else
                {
                    omega = Turn(fwd, aim, 5f, 1.5f); // fine tracking for the shot
                }
                throttle = targetDist < 600f && closingT > 80f ? 0.5f : 1f;
                // No gun: close range is wasted (and dangerous); hold off at missile range instead of closing in.
                if ((AirframeProfiles.For(a)?.Guns ?? 1) == 0 && targetDist < 1500f && closingT > 0f) throttle = targetDist < 900f ? 0f : 0.4f;
                GunsIfOnTarget(a, run, fwd, lookDir);

                // Never chase into the ground.
                if (a.radarAlt < 500f || vel.y < -30f && a.radarAlt < -vel.y * 8f)
                {
                    Plugin.Log.LogInfo($"{Callsign(a)}: hunt broken off, ground {a.radarAlt:0} m away.");
                    NextPhase(run, Recover);
                }
                else if (HuntOver(run, haveTarget, targetDist, offNose, t) && run.BlendStart < 0f)
                {
                    // Energy fighters do not stay in a turning fight they are losing: extend, then come back.
                    bool passedBehind = haveTarget && offNose > 110f && targetDist < HuntDropRange && !run.Cover;
                    // Extend and come back: energy fighters always; anyone against a jet that out-turns them; never
                    // a jet that out-turns the target (that turn is the winning move).
                    var pic = haveTarget ? EnergyFight.Compare(a, run.Target) : default;
                    bool extend = haveTarget && ((AirframeProfiles.For(a)?.ExtendAndReengage ?? false) && !pic.OutTurns || pic.OutTurned);
                    if (passedBehind && extend)
                    {
                        Plugin.Log.LogInfo($"{Callsign(a)}: target behind at {targetDist:0} m; extending to come back ({AirframeProfiles.For(a)?.Style}; {pic}).");
                        NextPhase(run, 1);
                    }
                    else if (slow) NextPhase(run, Recover);
                    else run.BlendStart = now;
                }
                break;
            }

            // Recovery ----------------------------------------------------------------------------------
            default:
            {
                // An offensive manoeuvre that ends with the target in front keeps fighting instead of recovering.
                if (ContinueAsHunt(a, run, targetDist, offNose, speed))
                {
                    omega = Vector3.zero;
                    break;
                }
                // Wings level, nose a little low to rebuild speed (a little high if close to the ground). After an
                // offensive manoeuvre it heads for the target rather than wherever the jet was going, so the work
                // the manoeuvre did is not undone.
                float pitchDeg = a.radarAlt < 1200f ? 10f : -8f;
                Vector3 flat = !run.Defensive && haveTarget && targetDist < 8000f
                    ? FlatOf(lookDir, fwd)
                    : FlatOf(vel.sqrMagnitude > 2500f ? vel : fwd, fwd);
                Vector3 wanted = Quaternion.AngleAxis(-pitchDeg, Vector3.Cross(Vector3.up, flat).normalized) * flat;
                omega = Turn(fwd, wanted, 1.5f, 1.5f) + KeepUp(fwd, up, 2f);
                bool recovered = speed >= RecoverSpeed && Mathf.Abs(aoa) < 15f && t > 1f;
                if ((recovered || t > MaxRecovery) && run.BlendStart < 0f) run.BlendStart = now;
                break;
            }
        }

        // Handing back: fade from our commands to what the AI and its filters asked for.
        float k = 1f;
        if (run.BlendStart >= 0f)
        {
            k = 1f - Mathf.Clamp01((now - run.BlendStart) / BlendOut);
            if (k <= 0f)
            {
                Active.Remove(a);
                LastEnd[a] = now;
                if (run.Defensive) Encounters.Taunt(a, "evade");
                int missiles = run.MissilesAtStart - MissilesLeft(a), rounds = run.AmmoAtStart - AmmoLeft(a) - missiles;
                Plugin.Log.LogInfo($"{Callsign(a)}: {run.Kind} done in {now - run.Start:0.0} s, {speed * 3.6f:0} km/h; " +
                                   $"fired {missiles} missile(s), {rounds} round(s){(run.BestAim < 180f ? $", best aim {run.BestAim:0.0} deg" : "")}; back to the AI.");
                return;
            }
        }
        ApplyRates(a, omega, throttle, k);
    }

    /// <summary>
    /// Flies the given world angular velocity: body-rate targets, then surface commands with the game's FBW signs
    /// (pitch and yaw = -K (rate - target), roll = +K (rate - target)), blended over what the AI and its filters
    /// asked for by <paramref name="k"/> (1 = all ours). Also used by the flight-test survey.
    /// </summary>
    internal static void ApplyRates(Aircraft a, Vector3 omega, float throttle, float k = 1f)
    {
        var inputs = a.GetInputs();
        Rigidbody rb = a.CockpitRB() ?? a.rb;
        Transform tr = rb.transform;
        Vector3 target = tr.InverseTransformDirection(omega);
        target = new Vector3(Mathf.Clamp(target.x, -3f, 3f), Mathf.Clamp(target.y, -2f, 2f), Mathf.Clamp(target.z, -5f, 5f));
        // Predictive g cap: while the wing is flying, pitch rate times speed is the load, so cap the commanded rate
        // at the one that makes a safe g now, before a joint is overloaded (the stress governor alone reacts too
        // late at high speed: 3 rad/s at 250 m/s asks for 75 g). Post-stall manoeuvres, at high AoA, are left alone.
        if (!FlightTest.IsSubject(a) && a.rb != null)
        {
            Vector3 lv = tr.InverseTransformDirection(a.rb.velocity);
            float aoa = Mathf.Abs(Mathf.Atan2(-lv.y, lv.z) * Mathf.Rad2Deg);
            if (aoa < 25f)
            {
                float maxRate = AceFlight.SafeG(a) * 9.81f / Mathf.Max(a.rb.velocity.magnitude, 40f);
                target.x = Mathf.Clamp(target.x, -maxRate, maxRate);
            }
        }
        Vector3 rate = tr.InverseTransformDirection(rb.angularVelocity);
        float pitch = Mathf.Clamp(-3f * (rate.x - target.x), -1f, 1f);
        float yaw = Mathf.Clamp(-3f * (rate.y - target.y), -1f, 1f);
        float rollCmd = Mathf.Clamp(1.2f * (rate.z - target.z), -1f, 1f);
        inputs.pitch = Mathf.Lerp(inputs.pitch, pitch, k);
        inputs.yaw = Mathf.Lerp(inputs.yaw, yaw, k);
        inputs.roll = Mathf.Lerp(inputs.roll, rollCmd, k);
        inputs.throttle = Mathf.Lerp(inputs.throttle, throttle, k);
    }

    private static void NextPhase(Run run, int phase)
    {
        run.Phase = phase;
        run.PhaseStart = Time.time;
    }

    // ---- attitude helpers (world space) ---------------------------------------------------------------

    internal static Vector3 TurnToward(Vector3 from, Vector3 to, float gain, float max) => Turn(from, to, gain, max);
    internal static Vector3 KeepWingsLevel(Vector3 fwd, Vector3 up, float gain) => KeepUp(fwd, up, gain);

    /// <summary>Angular velocity that turns <paramref name="from"/> toward <paramref name="to"/>, at most <paramref name="max"/> rad/s.</summary>
    private static Vector3 Turn(Vector3 from, Vector3 to, float gain, float max = 3f)
    {
        to = to.sqrMagnitude > 1e-6f ? to.normalized : from;
        Vector3 axis = Vector3.Cross(from, to);
        float angle = Vector3.Angle(from, to) * Mathf.Deg2Rad;
        if (axis.sqrMagnitude < 1e-6f)
        {
            if (angle < 0.1f) return Vector3.zero;
            axis = Vector3.Cross(from, Vector3.up); // straight behind: any perpendicular will do
        }
        return axis.normalized * Mathf.Min(angle * gain, max);
    }

    /// <summary>Rolls about the nose so the lift vector points up (wings level).</summary>
    /// <summary>Sustained turn radius (m) at corner speed, from the airframe profile.</summary>
    private static float TurnRadius(Aircraft x)
    {
        var p = AirframeProfiles.For(x);
        float corner = x.GetAircraftParameters()?.cornerSpeed ?? p?.CornerSpeed ?? 150f;
        float rate = Mathf.Max(p?.SustainedTurn ?? 14f, 1f) * Mathf.Deg2Rad;
        return corner / rate;
    }

    private static Vector3 KeepUp(Vector3 fwd, Vector3 up, float gain)
    {
        Vector3 wantUp = Vector3.up - fwd * Vector3.Dot(Vector3.up, fwd);
        if (wantUp.sqrMagnitude < 1e-4f) return Vector3.zero; // nose vertical: no meaningful level
        Vector3 w = Turn(up, wantUp.normalized, gain, 3f);
        return fwd * Vector3.Dot(w, fwd); // only the roll part
    }

    private static float BankAngle(Vector3 fwd, Vector3 up)
    {
        Vector3 wantUp = Vector3.up - fwd * Vector3.Dot(Vector3.up, fwd);
        return wantUp.sqrMagnitude < 1e-4f ? 0f : Vector3.Angle(up, wantUp);
    }

    private static Vector3 FlatOf(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        if (v.sqrMagnitude > 1e-4f) return v.normalized;
        fallback.y = 0f;
        return fallback.sqrMagnitude > 1e-4f ? fallback.normalized : Vector3.forward;
    }

    /// <summary>Yaw toward the side the bandit sits on (it then overshoots across the nose the other way); a coin toss if dead astern.</summary>
    private static float YawSide(Aircraft a, Run run)
    {
        if (run.Target != null && !run.Target.disabled)
        {
            float lateral = Vector3.Dot(run.Target.transform.position - a.transform.position, a.transform.right);
            if (Mathf.Abs(lateral) > 15f) return Mathf.Sign(lateral);
        }
        return UnityEngine.Random.value < 0.5f ? -1f : 1f;
    }

    private static float _hornAoA = -1f;

    /// <summary>
    /// The AoA at which the game sounds its stall horn: the HUD's AoADisplay.hornThreshold when a HUD exists
    /// (read once), else 20 deg.
    /// </summary>
    private static float StallHornAoA()
    {
        if (_hornAoA > 0f) return _hornAoA;
        try
        {
            var display = UnityEngine.Object.FindObjectOfType<AoADisplay>(true);
            var field = AccessTools.Field(typeof(AoADisplay), "hornThreshold");
            if (display != null && field?.GetValue(display) is float horn && horn > 5f && horn < 60f)
            {
                _hornAoA = horn;
                Plugin.Log.LogInfo($"Stall horn sounds at {horn:0.#} deg AoA.");
                return horn;
            }
        }
        catch { /* fall back */ }
        return 20f;
    }

    private static Vector3 Lead(Aircraft target, Vector3 from, float seconds)
    {
        Vector3 p = target.transform.position + (target.rb != null ? target.rb.velocity * seconds : Vector3.zero);
        return (p - from).normalized;
    }
}
