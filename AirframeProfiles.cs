using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// How an ace should fight in a given airframe, worked out from the airframe's numbers rather than by hand, so modded
/// aircraft are flown sensibly without anyone tuning them.
///
/// Two sources, best first:
/// 1. Measured: the flight-test survey (record mode -noahSurvey) flies every fixed-wing airframe through the same
///    tests (level acceleration, maximum sustained turn, roll, low-speed pitch) and stores the results in
///    BepInEx/config/NOAcesHigh/airframe-profiles.json. Run it once after installing new aircraft mods.
/// 2. Estimated: from static stats read off the live aircraft (thrust-to-weight, wing loading, corner speed, top
///    speed, g limit, thrust vectoring, weapons), using rules fitted to the measured vanilla fleet (see AI-NOTES.md).
///
/// The profile then sets the tactics: style (turn fighter, energy fighter, interceptor, striker), how hard the hunt
/// pulls at range, the speed it refuses to bleed below, whether it extends and re-engages after a pass, how early it
/// starts a missile break, and whether post-stall manoeuvres suit the jet.
/// </summary>
internal sealed class AirframeProfile
{
    // Static (read from the aircraft)
    public string Key, Name;
    public float Mass, Thrust, ThrustToWeight, WingArea, WingLoading, CornerSpeed, MaxSpeed, GLimit;
    public bool Vectoring, Propeller, RelaxedStability; // RelaxedStability: has the game's canard stability controller
    public int Guns;
    public float MissileRange, AntiAir;
    public float AlphaLimit = 25f, FbwGLimit = 9f, FbwRollRate = 6f; // the fly-by-wire limits (deg, g, rad/s)

    // Measured by the survey (0 = not measured)
    public bool Measured;
    public float SustainedTurn;    // deg/s, full throttle, from corner speed, average of seconds 2-8
    public float PeakTurn;         // deg/s
    public float TurnBleed;        // m/s lost per second in that turn
    public float Accel;            // m/s per second, level, full throttle, from 200 m/s
    public float RollRate;         // deg/s, peak
    public float SlowPitch;        // deg/s, peak, pitching up at half corner speed
    public float PeakG;
    public float MaxTurnAoA;      // deg, highest AoA in the turn test
    public bool Departs;           // AoA ran away in the turn test: needs the stability augmentation the override bypasses

    // Derived tactics
    public string Style = "turn";
    public float HuntRateScale = 1f;    // multiplies the hunt's pull rates
    public float EnergyFloor = 0.55f;   // share of corner speed the hunt scales its pull down toward
    public bool ExtendAndReengage;      // after a missed pass: run out, then come back (boom and zoom)
    public float BreakEarly;            // seconds added to the missile-break window (slow-rolling jets start sooner)
    public bool PostStallSuits = true;  // low-speed authority good enough for the post-stall manoeuvres
    public bool JoustAverse;            // loses head-on missile fights: cranks off the bandit's nose instead (PostStall.TryCrank)
    public string Notes = "";
}

internal static class AirframeProfiles
{
    private static readonly Dictionary<string, AirframeProfile> Cache = new();
    private static Dictionary<string, AirframeProfile> _measured;

    internal static string StorePath => Path.Combine(BepInEx.Paths.ConfigPath, "NOAcesHigh", "airframe-profiles.json");

    /// <summary>The profile for a live aircraft: measured if surveyed, else estimated from its stats (cached per airframe).</summary>
    internal static AirframeProfile For(Aircraft a)
    {
        if (a == null || a.definition == null) return null;
        string key = AirframeChecklist.Key(a.definition);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var p = ReadStatic(a);
        LoadMeasured();
        if (_measured != null && _measured.TryGetValue(key, out var m) && m.Measured)
        {
            p.Measured = true;
            p.SustainedTurn = m.SustainedTurn;
            p.PeakTurn = m.PeakTurn;
            p.TurnBleed = m.TurnBleed;
            p.Accel = m.Accel;
            p.RollRate = m.RollRate;
            p.SlowPitch = m.SlowPitch;
            p.PeakG = m.PeakG;
            p.MaxTurnAoA = m.MaxTurnAoA;
            p.Departs = m.Departs;
        }
        else Estimate(p);
        Derive(p);
        Cache[key] = p;
        Plugin.Log.LogInfo($"Airframe profile {p.Name}: {(p.Measured ? "measured" : "estimated")} {p.Style}; turn {p.SustainedTurn:0.0} deg/s, " +
                           $"bleed {p.TurnBleed:0.0} m/s2, accel {p.Accel:0.0} m/s2, roll {p.RollRate:0} deg/s, T/W {p.ThrustToWeight:0.00}, " +
                           $"wing load {p.WingLoading:0} kg/m2. {p.Notes}");
        return p;
    }

    internal static void ClearCache()
    {
        Cache.Clear();
        _measured = null;
    }

    // ---- static stats -------------------------------------------------------------------------------------

    /// <summary>Reads the static numbers from a live aircraft (its parts and engines are initialised then).</summary>
    internal static AirframeProfile ReadStatic(Aircraft a)
    {
        var d = a.definition;
        var ps = a.GetAircraftParameters();
        var p = new AirframeProfile
        {
            Key = AirframeChecklist.Key(d),
            Name = d.unitName,
            CornerSpeed = ps?.cornerSpeed ?? 150f,
            MaxSpeed = ps?.maxSpeed ?? 300f,
            GLimit = ps?.aircraftGLimit ?? 9f,
            Vectoring = AirframeChecklist.HasThrustVectoring(d),
            AntiAir = d.roleIdentity.antiAir,
            RelaxedStability = a.GetComponentInChildren<RelaxedStabilityController>(true) != null,
        };
        try
        {
            float mass = 0f, area = 0f;
            if (a.GetMaxThrust(out float shipThrust) && shipThrust > 0f) p.Thrust = shipThrust;
            if (a.partLookup != null)
                foreach (var part in a.partLookup)
                {
                    if (part == null) continue;
                    mass += part.mass;
                    if (part is AeroPart aero) area += aero.WingArea;
                }
            if (d.mass > 0f) mass = d.mass; // the merged rigid body uses the definition mass
            if (mass <= 0f && a.rb != null) mass = a.rb.mass;
            p.Mass = mass;
            p.WingArea = area;
            float thrust = 0f;
            var engineList = a.engines != null && a.engines.Count > 0 ? (IEnumerable<IEngine>)a.engines : a.GetComponentsInChildren<IEngine>(true);
            foreach (var engine in engineList)
                thrust += Mathf.Max(0f, engine.GetMaxThrust());
            if (p.Thrust <= 0f) p.Thrust = thrust;
            // Propeller aircraft report shaft power, not thrust: use the thrust that power gives at corner speed
            // (about 80% propeller efficiency), which is where the ace fights.
            if (p.Thrust <= 0f && a.GetMaxPower(out float power) && power > 0f)
            {
                p.Thrust = power * 0.8f / Mathf.Max(60f, p.CornerSpeed);
                p.Propeller = true;
            }
            p.ThrustToWeight = mass > 0f ? p.Thrust / (mass * 9.81f) : 0f;
            p.WingLoading = area > 0f ? mass / area : 0f;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Airframe profile: could not read {d.unitName}'s mass/thrust: {ex.Message}");
        }
        try
        {
            var fbw = a.GetControlsFilter()?.GetFlyByWire();
            if (fbw != null && fbw.Enabled)
            {
                var t = typeof(ControlsFilter.FlyByWire);
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                if (t.GetField("alphaLimiter", F)?.GetValue(fbw) is float alpha && alpha > 5f) p.AlphaLimit = alpha;
                if (t.GetField("gLimitPositive", F)?.GetValue(fbw) is float g && g > 1f) p.FbwGLimit = g;
                if (t.GetField("maxRollAngularVel", F)?.GetValue(fbw) is float roll && roll > 0.5f) p.FbwRollRate = roll;
            }
        }
        catch { /* fly-by-wire limits are optional data */ }
        try
        {
            if (a.weaponStations != null)
                foreach (var s in a.weaponStations)
                {
                    var info = s?.WeaponInfo;
                    if (info == null) continue;
                    if (info.gun) p.Guns++;
                    else if (info.missile && info.effectiveness.antiAir > info.effectiveness.antiSurface)
                        p.MissileRange = Mathf.Max(p.MissileRange, info.targetRequirements.maxRange);
                }
        }
        catch { /* weapons are optional data */ }
        return p;
    }

    // ---- estimates ------------------------------------------------------------------------------------------

    /// <summary>
    /// Performance estimates from static stats, for airframes the survey has not measured. The coefficients come
    /// from the measured vanilla fleet (AI-NOTES.md); they only need to rank jets sensibly, not predict exactly.
    /// </summary>
    internal static void Estimate(AirframeProfile p)
    {
        float tw = p.ThrustToWeight > 0f ? p.ThrustToWeight : 0.7f;
        float wl = p.WingLoading > 0f ? p.WingLoading : 400f;
        // Sustained turn grows with thrust-to-weight and falls with wing loading; vectoring adds a little.
        p.SustainedTurn = Mathf.Clamp(Coef.TurnBase + Coef.TurnPerTW * tw - Coef.TurnPerWL * wl + Coef.TurnPerAlpha * p.AlphaLimit + (p.Vectoring ? Coef.TurnVectoring : 0f), 4f, 30f);
        p.PeakTurn = p.SustainedTurn * 1.35f;
        // Bleed in a max turn falls with thrust-to-weight.
        p.TurnBleed = Mathf.Clamp(Coef.BleedBase - Coef.BleedPerTW * tw, -5f, 25f);
        p.Accel = Mathf.Clamp(Coef.AccelPerTW * tw - Coef.AccelDrag, -3f, 20f);
        p.RollRate = Mathf.Clamp(Coef.RollBase - Coef.RollPerWL * wl, 60f, 300f);
        p.SlowPitch = p.Vectoring ? 100f : Mathf.Clamp(15f + 30f * tw, 20f, 60f);
        p.PeakG = Mathf.Min(p.GLimit * 1.4f, 14f);
    }

    /// <summary>Estimator coefficients, fitted to the measured vanilla fleet; see AI-NOTES.md.</summary>
    internal static class Coef
    {
        // Fitted to the 2026-11-24 survey of 17 airframes (sustained turn within about 3 deg/s; see AI-NOTES.md).
        public static float TurnBase = 13.60f, TurnPerTW = 0.13f, TurnPerWL = 0.0350f, TurnPerAlpha = 0.213f, TurnVectoring = 0.20f;
        public static float BleedBase = 3.24f, BleedPerTW = 1.52f;
        public static float AccelPerTW = 4.78f, AccelDrag = 0.87f;
        public static float RollBase = 230f, RollPerWL = 0.564f;
    }

    // ---- tactics ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Turns performance into tactics. Thresholds are relative to the surveyed fleet (its median sustained turn and
    /// acceleration), so they hold for jets the fleet has never seen. See AI-NOTES.md for the reasoning.
    /// </summary>
    internal static void Derive(AirframeProfile p)
    {
        var notes = new List<string>();
        float thr = Coef_TurnFighter, acc = Coef_FleetAccel;
        // Energy ratio: how much speed a hard turn costs against how fast the jet gets it back.
        float energyRatio = p.TurnBleed / Mathf.Max(0.5f, p.Accel);
        bool fast = p.MaxSpeed > 0f && p.CornerSpeed > 0f && p.MaxSpeed > p.CornerSpeed * 2.6f;
        bool turner = p.SustainedTurn >= thr || p.Vectoring && p.SustainedTurn >= thr * 0.85f;
        bool poorTurner = p.SustainedTurn < thr * 0.9f;
        p.ExtendAndReengage = false;

        if (p.AntiAir < 0.25f && p.Guns == 0 && p.MissileRange <= 0f)
        {
            p.Style = "striker";
            p.HuntRateScale = 0.6f;
            p.EnergyFloor = 0.75f;
            p.ExtendAndReengage = true;
            notes.Add("Little air-to-air: fights only when forced, keeps its speed.");
        }
        else if (turner)
        {
            p.Style = "turn";
            p.HuntRateScale = 1f;
            p.EnergyFloor = 0.5f;
            notes.Add("Out-turns most jets: commits to the turning fight and guns.");
        }
        else if (fast && poorTurner && p.Accel >= acc * 0.9f)
        {
            p.Style = "interceptor";
            p.HuntRateScale = 0.7f;
            p.EnergyFloor = 0.75f;
            p.ExtendAndReengage = true;
            notes.Add("Fast, turns poorly: slashes with missiles, extends and comes back rather than turning.");
        }
        else if (poorTurner && p.Accel < acc * 0.6f)
        {
            p.Style = "heavy";
            p.HuntRateScale = 0.75f;
            p.EnergyFloor = 0.72f;
            p.ExtendAndReengage = true;
            notes.Add("Neither turns nor accelerates well: missile shots and slashing passes, never a turning fight.");
        }
        else if (poorTurner || energyRatio > 1.4f)
        {
            p.Style = "energy";
            p.HuntRateScale = 0.85f;
            p.EnergyFloor = 0.68f;
            p.ExtendAndReengage = true;
            notes.Add("Accelerates better than it turns: boom and zoom, never let it get slow.");
        }
        else
        {
            p.Style = "balanced";
            p.HuntRateScale = 0.95f;
            p.EnergyFloor = 0.55f;
            notes.Add("Balanced: turns when it pays, holds energy otherwise.");
        }

        p.BreakEarly = p.RollRate < 100f ? 0.6f : p.RollRate < 150f ? 0.3f : 0f; // fleet spans 31-270 deg/s
        if (p.BreakEarly > 0f) notes.Add($"Slow roll: starts missile breaks {p.BreakEarly:0.0} s earlier.");
        // Head-on missile fights go to the jet that turns and points best; energy, heavy and strike styles, and
        // non-vectoring jets limited to 15 deg AoA or less, lost them in testing (the Vagrant 1 in 9), so they crank.
        p.JoustAverse = p.Style is "energy" or "heavy" or "striker" || !p.Vectoring && p.AlphaLimit <= 15f;
        if (p.JoustAverse) notes.Add("Avoids missile jousts: shoots first if it can, then cranks off the bandit's nose.");
        p.PostStallSuits = p.Vectoring || p.SlowPitch >= 45f && p.ThrustToWeight >= 0.8f;
        if (!p.PostStallSuits) notes.Add("Weak low-speed pitch: no post-stall showpieces.");
        if (p.Departs || p.RelaxedStability && !p.Measured)
        {
            // Pitch runs away past the limit without the game's stability augmentation: fight inside the envelope.
            p.PostStallSuits = false;
            p.HuntRateScale *= 0.85f;
            p.EnergyFloor = Mathf.Max(p.EnergyFloor, 0.65f);
            notes.Add(p.Departs ? $"Departs past {p.AlphaLimit:0} deg AoA (reached {p.MaxTurnAoA:0} in the survey): no post-stall, pulls stay inside the limit."
                                : "Relaxed-stability design: no post-stall, pulls stay inside the AoA limit.");
        }
        float cap = PostStall.HuntAoACap(p, false);
        notes.Add($"Pulls to {cap:0} deg AoA in the hunt (fly-by-wire limit {p.AlphaLimit:0}).");
        p.Notes = string.Join(" ", notes);
    }

    /// <summary>Sustained turn rate (deg/s) that marks a turn fighter: the fleet median x 1.1 (2026-11-24 survey).</summary>
    internal static float Coef_TurnFighter = 14.5f;

    /// <summary>Median level acceleration from corner speed of the surveyed fleet (m/s per second).</summary>
    internal static float Coef_FleetAccel = 6.5f;

    // ---- the measured store -----------------------------------------------------------------------------------

    private static void LoadMeasured()
    {
        if (_measured != null) return;
        _measured = new Dictionary<string, AirframeProfile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StorePath)) return;
            var file = JsonConvert.DeserializeObject<Store>(File.ReadAllText(StorePath));
            if (file?.Airframes != null)
                foreach (var p in file.Airframes)
                    if (!string.IsNullOrEmpty(p.Key)) _measured[p.Key] = p;
            if (file?.TurnFighterThreshold > 0f) Coef_TurnFighter = file.TurnFighterThreshold;
            if (file?.FleetAccel > 0f) Coef_FleetAccel = file.FleetAccel;
            if (file?.Coefficients != null) file.Coefficients.Apply();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Airframe profiles: could not read " + StorePath + ": " + ex.Message);
        }
    }

    internal static void Save(List<AirframeProfile> measured, float turnFighterThreshold, float fleetAccel, Coefficients coefficients, bool merge = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
        var all = new List<AirframeProfile>(measured);
        Store old = null;
        if (merge && File.Exists(StorePath))
        {
            // A partial survey adds or replaces its airframes and keeps the rest (and the fleet-wide numbers).
            try { old = JsonConvert.DeserializeObject<Store>(File.ReadAllText(StorePath)); }
            catch { old = null; }
            if (old?.Airframes != null)
                all.AddRange(old.Airframes.Where(o => o != null && measured.All(m => !string.Equals(m.Key, o.Key, StringComparison.OrdinalIgnoreCase))));
        }
        var store = new Store
        {
            Note = "Measured by NO Aces High's flight-test survey (record mode -noahSurvey). Aces fly any airframe listed here " +
                   "from these numbers; anything else is estimated from its stats with the coefficients below.",
            Surveyed = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            TurnFighterThreshold = old != null && old.TurnFighterThreshold > 0f ? old.TurnFighterThreshold : turnFighterThreshold,
            FleetAccel = old != null && old.FleetAccel > 0f ? old.FleetAccel : fleetAccel,
            Coefficients = old?.Coefficients ?? coefficients,
            Airframes = all.OrderBy(p => p.Name).ToList(),
        };
        File.WriteAllText(StorePath, JsonConvert.SerializeObject(store, Formatting.Indented));
        ClearCache();
    }

    internal sealed class Store
    {
        public string Note;
        public string Surveyed;
        public float TurnFighterThreshold;
        public float FleetAccel;
        public Coefficients Coefficients;
        public List<AirframeProfile> Airframes;
    }

    /// <summary>Estimator coefficients as stored alongside the measurements.</summary>
    internal sealed class Coefficients
    {
        public float TurnBase, TurnPerTW, TurnPerWL, TurnPerAlpha, TurnVectoring, BleedBase, BleedPerTW, AccelPerTW, AccelDrag, RollBase, RollPerWL;

        public static Coefficients Current() => new()
        {
            TurnBase = Coef.TurnBase, TurnPerTW = Coef.TurnPerTW, TurnPerWL = Coef.TurnPerWL, TurnPerAlpha = Coef.TurnPerAlpha, TurnVectoring = Coef.TurnVectoring,
            BleedBase = Coef.BleedBase, BleedPerTW = Coef.BleedPerTW, AccelPerTW = Coef.AccelPerTW, AccelDrag = Coef.AccelDrag,
            RollBase = Coef.RollBase, RollPerWL = Coef.RollPerWL,
        };

        public void Apply()
        {
            if (TurnPerTW == 0f && TurnBase == 0f) return;
            Coef.TurnBase = TurnBase; Coef.TurnPerTW = TurnPerTW; Coef.TurnPerWL = TurnPerWL; Coef.TurnPerAlpha = TurnPerAlpha; Coef.TurnVectoring = TurnVectoring;
            Coef.BleedBase = BleedBase; Coef.BleedPerTW = BleedPerTW; Coef.AccelPerTW = AccelPerTW; Coef.AccelDrag = AccelDrag;
            Coef.RollBase = RollBase; Coef.RollPerWL = RollPerWL;
        }
    }
}
