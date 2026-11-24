using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// The flight-test survey (record mode -noahSurvey): every fixed-wing airframe installed, vanilla or modded, flies the
/// same tests under the ace's own controller, and the results become its AirframeProfile.
///
/// Each test jet flies alone, tens of kilometres from a parked camera (so the game keeps it in single-body physics,
/// and setting its speed between tests is safe):
/// 1. accel: level at full throttle from 200 m/s for 5 s;
/// 2. turn: from corner speed, banked 80 deg and pulling as hard as the ace may (PostStallMaxG) for 8 s at full
///    throttle: sustained and peak turn rate, speed lost per second, peak g;
/// 3. roll: full roll command for 1.5 s from 1.1x corner speed: peak roll rate;
/// 4. slow pitch: full pitch-up for 1.5 s at half corner speed: peak pitch rate (post-stall authority).
/// Afterwards the estimator coefficients are refitted to everything measured and saved with it.
/// </summary>
internal static class FlightTest
{
    private static Aircraft _subject;

    internal static bool IsSubject(Aircraft a) => a != null && a == _subject;
    private static Func<Aircraft, (Vector3 omega, float throttle)> _control;

    /// <summary>Called from the FilterInputs hook: flies the test jet; false for every other aircraft.</summary>
    internal static bool Override(Aircraft a)
    {
        if (_subject == null || a != _subject || _control == null) return false;
        var (omega, throttle) = _control(a);
        PostStall.ApplyRates(a, omega, throttle);
        return true;
    }

    /// <summary>The test jet's AI does not steer it at all: its autopilot is skipped while it is under test.</summary>
    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        Patches.Patch(harmony, "flight-test pilot",
            HarmonyLib.AccessTools.Method(typeof(AutopilotPlane), nameof(AutopilotPlane.AutoAim),
                new[] { typeof(GlobalPosition), typeof(bool), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(Vector3) }),
            prefix: nameof(AutoAimPrefix), owner: typeof(FlightTest));
    }

    private static bool AutoAimPrefix(AutopilotPlane __instance) => !IsSubject(__instance.aircraft);

    /// <summary>
    /// The test jet always flies as one rigid body: jointed physics (near the camera) would tear it apart when the
    /// survey resets its speed between tests.
    /// </summary>
    private static bool PhysicsLodPrefix(Aircraft __instance)
    {
        if (!IsSubject(__instance)) return true;
        ForceSimple(__instance);
        return false;
    }

    /// <summary>
    /// Keeps the test jet in the game's full jointed physics, the model a locally simulated aircraft spawns with
    /// (merging mid-flight leaves a wrong inertia tensor: 800 deg/s rolls). Speed resets move every part together.
    /// </summary>
    private static void ForceSimple(Aircraft a)
    {
        try { if (a.simplePhysics) a.SetComplexPhysics(); }
        catch (Exception ex) { Plugin.Log.LogWarning("flight test: could not split the test jet: " + ex.Message); }
    }

    internal static void ApplyPhysicsPatch(HarmonyLib.Harmony harmony)
    {
        Patches.Patch(harmony, "flight-test: rigid body",
            HarmonyLib.AccessTools.Method(typeof(Aircraft), "CheckPhysicsLod"),
            prefix: nameof(PhysicsLodPrefix), owner: typeof(FlightTest));
    }

    /// <summary>
    /// The test jet's pilot step is skipped: no AI state writing the controls, and no g damage from the instant
    /// speed changes between tests (the pilot reads g from velocity change per step).
    /// </summary>
    private static bool PilotStepPrefix(NuclearOption.Jobs.PartResult __result, Pilot __instance)
    {
        if (__instance == null || !IsSubject(__instance.aircraft)) return true;
        __instance.velocityPrev = Vector3.zero; // "no previous sample": no g spike when the step resumes
        return false;
    }

    internal static void ApplyPilotPatch(HarmonyLib.Harmony harmony)
    {
        Patches.Patch(harmony, "flight-test: pilot off",
            HarmonyLib.AccessTools.Method(typeof(Pilot), "Pilot_OnAeroInputsApplied"),
            prefix: nameof(PilotStepPrefix), owner: typeof(FlightTest));
    }

    /// <summary>Applies the test pilot's commands every physics step, whatever the AI's state is doing.</summary>
    private static IEnumerator ControlLoop()
    {
        while (_running)
        {
            yield return new WaitForFixedUpdate();
            var a = _subject;
            if (a == null || a.disabled || _control == null) continue;
            try
            {
                var (omega, throttle) = _control(a);
                PostStall.ApplyRates(a, omega, throttle);
            }
            catch { /* the jet went away mid-step */ }
        }
    }

    private static bool _running;

    internal static IEnumerator Survey(string outDir, Action<string> log, string only = "")
    {
        var all = Encyclopedia.i.aircraft.Where(a => a != null && a.unitPrefab != null && !(a.unitName ?? "?").StartsWith("?") &&
                                                     a.unitPrefab.GetComponentInChildren<HeloControlsFilter>(true) == null)
                                         .Where(a => only.Length == 0 || only.Split(',').Any(o => o.Trim().Length > 0 && (a.unitName ?? "").IndexOf(o.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                                         .OrderBy(a => a.unitName).ToList();
        log($"survey: {all.Count} fixed-wing airframes");
        bool verbose = only.Length > 0;
        FactionHQ hq = Factions.HQFor(Side.BDF) ?? FactionRegistry.GetAllHQs().FirstOrDefault(h => h != null);
        var park = new ParkedCamera();
        _running = true;
        Plugin.Instance.StartCoroutine(ControlLoop());
        var results = new List<AirframeProfile>();

        int i = 0;
        foreach (var def in all)
        {
            i++;
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam != null && cam.currentState != park) cam.SwitchState(park);

            var pos = new GlobalPosition(-12000f + (i % 3) * 4000f, 5000f, -12000f + (i / 3 % 4) * 4000f);
            var rot = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            Aircraft a = AceSpawner.SpawnPlain(def, hq, pos, rot, 200f, "Test");
            if (a == null)
            {
                log($"survey: {def.unitName}: spawn failed");
                continue;
            }
            _subject = a;
            _control = Level;
            ForceSimple(a);
            yield return new WaitForSeconds(1f);
            if (a == null || a.disabled) { log($"survey: {def.unitName}: lost on spawn"); continue; }
            ForceSimple(a);

            var p = AirframeProfiles.ReadStatic(a);
            float corner = Mathf.Max(60f, p.CornerSpeed);
            _alpha = p.AlphaLimit;

            // 1. Level acceleration.
            _control = Level;
            SetSpeed(a, corner);
            yield return new WaitForSeconds(1f);
            float v0 = a.rb.velocity.magnitude;
            for (int s = 0; s < 5; s++)
            {
                if (verbose && !Lost(a)) log($"  {def.unitName} accel t{s}: {Diag(a)}");
                yield return new WaitForSeconds(1f);
            }
            if (Lost(a)) { log($"survey: {def.unitName}: lost in accel; {_lastSeen}"); continue; }
            p.Accel = (a.rb.velocity.magnitude - v0) / 5f;

            // 2. Maximum sustained turn.
            SetSpeed(a, corner);
            _control = MaxTurn;
            float start = Time.time, lastHeading = Heading(a), turned = 0f, peakRate = 0f, peakG = 0f;
            float sumRate = 0f, nextDiag = 0f, maxAoa = 0f; int samples = 0;
            while (Time.time - start < 8f)
            {
                yield return new WaitForFixedUpdate();
                if (Lost(a)) break;
                float h = Heading(a);
                float dh = Mathf.Abs(Mathf.DeltaAngle(lastHeading, h));
                lastHeading = h;
                float rate = dh / Time.fixedDeltaTime;
                turned += dh;
                if (Time.time - start > 0.5f) peakRate = Mathf.Max(peakRate, rate);
                if (Time.time - start > 2f) { sumRate += rate; samples++; }
                if (Time.time - start > 0.5f) peakG = Mathf.Max(peakG, a.gForce);
                maxAoa = Mathf.Max(maxAoa, _prevAoa);
                if (verbose && Time.time - nextDiag >= 0f) { nextDiag = Time.time + 1f; log($"  {def.unitName} turn: rate {rate:0.0} {Diag(a)}"); }
            }
            if (Lost(a)) { log($"survey: {def.unitName}: lost in the turn; {_lastSeen}"); continue; }
            p.SustainedTurn = samples > 0 ? sumRate / samples : 0f;
            p.PeakTurn = peakRate;
            p.TurnBleed = (corner - a.rb.velocity.magnitude) / 8f;
            p.PeakG = peakG;
            // A jet whose AoA runs far past its fly-by-wire limit under a steady pull departs without the game's
            // stability augmentation (relaxed-stability designs): its turn figure is then the estimate, not the tumble.
            p.MaxTurnAoA = maxAoa;
            p.Departs = maxAoa > _alpha + 15f;
            if (p.Departs)
            {
                var est = new AirframeProfile { ThrustToWeight = p.ThrustToWeight, WingLoading = p.WingLoading, AlphaLimit = p.AlphaLimit, Vectoring = p.Vectoring, GLimit = p.GLimit };
                AirframeProfiles.Estimate(est);
                log($"survey: {def.unitName} departed in the turn (AoA {maxAoa:0} deg against a limit of {_alpha:0}); using the estimated turn {est.SustainedTurn:0.0} deg/s");
                p.SustainedTurn = est.SustainedTurn;
                p.PeakTurn = Mathf.Max(p.PeakTurn, est.PeakTurn);
                p.TurnBleed = est.TurnBleed;
            }

            // 3. Roll rate.
            SetSpeed(a, corner * 1.1f);
            _control = Level;
            yield return new WaitForSeconds(1f);
            _control = Roll;
            float peakRoll = 0f;
            start = Time.time;
            while (Time.time - start < 1.5f)
            {
                yield return new WaitForFixedUpdate();
                if (Lost(a)) break;
                var rb = a.CockpitRB() ?? a.rb;
                peakRoll = Mathf.Max(peakRoll, Mathf.Abs(rb.transform.InverseTransformDirection(rb.angularVelocity).z) * Mathf.Rad2Deg);
                if (verbose && Time.time - nextDiag >= 0f) { nextDiag = Time.time + 0.3f; log($"  {def.unitName} roll: {Diag(a)}"); }
            }
            if (Lost(a)) { log($"survey: {def.unitName}: lost in the roll; {_lastSeen}"); continue; }
            p.RollRate = peakRoll;

            // 4. Low-speed pitch authority.
            _control = Level;
            SetSpeed(a, corner * 0.5f);
            yield return new WaitForSeconds(0.3f);
            _control = PitchUp;
            float peakPitch = 0f;
            start = Time.time;
            while (Time.time - start < 1.5f)
            {
                yield return new WaitForFixedUpdate();
                if (Lost(a)) break;
                var rb = a.CockpitRB() ?? a.rb;
                peakPitch = Mathf.Max(peakPitch, Mathf.Abs(rb.transform.InverseTransformDirection(rb.angularVelocity).x) * Mathf.Rad2Deg);
            }
            p.SlowPitch = peakPitch;
            p.Measured = !Lost(a) && p.SustainedTurn > 0f;

            _subject = null;
            _control = null;
            Despawn(a);
            results.Add(p);
            log(string.Format(CultureInfo.InvariantCulture,
                "survey: {0,-28} T/W {1:0.00}  wingload {2,4:0}  corner {3,3:0}  turn {4,4:0.0} (peak {5,4:0.0}) deg/s  bleed {6,5:0.0}  accel {7,5:0.0}  roll {8,3:0}  slowpitch {9,3:0}  peak g {10,4:0.0}  vec {11}  alpha {12:0} (max {14:0})  fbw g {13:0.0}{15}",
                p.Name, p.ThrustToWeight, p.WingLoading, p.CornerSpeed, p.SustainedTurn, p.PeakTurn, p.TurnBleed, p.Accel, p.RollRate, p.SlowPitch, p.PeakG, p.Vectoring, p.AlphaLimit, p.FbwGLimit, p.MaxTurnAoA, (p.RelaxedStability ? "  relaxed" : "") + (p.Departs ? "  DEPARTS" : "")));
            yield return new WaitForSeconds(0.5f);
        }

        _running = false;
        bool partial = only.Length > 0;
        // Refit the estimators to the fleet (a full survey only) and store everything.
        var good = results.Where(r => r.Measured && r.Mass > 0f && r.Thrust > 0f && r.WingArea > 0f && r.CornerSpeed >= 60f).ToList(); // ultralights fly outside the fit
        if (!partial && good.Count > 0)
        {
            Fit(good, log);
            AirframeProfiles.Coef_TurnFighter = good.Select(r => r.SustainedTurn).OrderBy(x => x).ElementAt(good.Count / 2) * 1.1f;
            AirframeProfiles.Coef_FleetAccel = good.Select(r => r.Accel).OrderBy(x => x).ElementAt(good.Count / 2);
        }
        foreach (var r in results)
        {
            if (!r.Measured) AirframeProfiles.Estimate(r);
            AirframeProfiles.Derive(r);
            log($"survey: {r.Name}: {r.Style}. {r.Notes}");
        }
        AirframeProfiles.Save(results.Where(r => r.Measured).ToList(), AirframeProfiles.Coef_TurnFighter, AirframeProfiles.Coef_FleetAccel,
            AirframeProfiles.Coefficients.Current(), merge: partial);
        WriteCsv(Path.Combine(outDir, "airframe-survey.csv"), results);
        log($"survey: saved {results.Count(r => r.Measured)} measured profiles to {AirframeProfiles.StorePath}{(partial ? " (merged)" : "")}; " +
            $"turn-fighter threshold {AirframeProfiles.Coef_TurnFighter:0.0} deg/s, fleet accel {AirframeProfiles.Coef_FleetAccel:0.0} m/s2");
    }

    // ---- the test pilot ----------------------------------------------------------------------------------

    private static (Vector3, float) Level(Aircraft a)
    {
        var tr = (a.CockpitRB() ?? a.rb).transform;
        Vector3 flat = a.rb.velocity;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 1f ? flat.normalized : tr.forward;
        return (PostStall.TurnToward(tr.forward, flat, 2f, 1.5f) + PostStall.KeepWingsLevel(tr.forward, tr.up, 3f), 1f);
    }

    private static (Vector3, float) MaxTurn(Aircraft a)
    {
        var tr = (a.CockpitRB() ?? a.rb).transform;
        Vector3 fwd = tr.forward;
        // Hold an 80 deg bank to the right, then pull as hard as the ace may.
        Vector3 level = Vector3.up - fwd * Vector3.Dot(Vector3.up, fwd);
        // A level turn: steepen the bank while climbing, shallow it while sinking.
        float bank = Mathf.Clamp(78f + 0.8f * a.rb.velocity.y, 40f, 89f);
        Vector3 wantUp = level.sqrMagnitude > 1e-4f ? Quaternion.AngleAxis(-bank, fwd) * level.normalized : tr.up;
        Vector3 roll = PostStall.TurnToward(tr.up, wantUp, 4f, 4f);
        roll = fwd * Vector3.Dot(roll, fwd);
        // Pull to just under the fly-by-wire AoA limit (the edge of a sustained turn), within the ace's g and structure limits.
        Vector3 lv = tr.InverseTransformDirection(a.rb.velocity);
        float aoa = Mathf.Atan2(-lv.y, lv.z) * Mathf.Rad2Deg;
        // Feed-forward: the pitch rate the present load already turns the flight path at; then close the AoA error,
        // damped by the AoA rate (the vectoring jets overshoot otherwise).
        float aoaRate = (aoa - _prevAoa) / Time.fixedDeltaTime;
        _prevAoa = aoa;
        float want = Mathf.Max(12f, _alpha);
        float ff = Mathf.Max(0f, a.gForce) * 9.81f / Mathf.Max(30f, a.speed);
        float pull = Mathf.Clamp(ff + 0.05f * (want - aoa) - 0.02f * aoaRate, -1f, 3f);
        if (a.gForce > Plugin.PostStallMaxG.Value || AceFlight.StressOf(a) > 0.75f) pull = Mathf.Min(pull, 0f);
        return (roll + tr.right * -pull, 1f);
    }

    private static (Vector3, float) Roll(Aircraft a)
    {
        var tr = (a.CockpitRB() ?? a.rb).transform;
        return (tr.forward * -5f, 1f);
    }

    private static (Vector3, float) PitchUp(Aircraft a)
    {
        var tr = (a.CockpitRB() ?? a.rb).transform;
        return (tr.right * -3f, 1f);
    }

    private static string Diag(Aircraft a)
    {
        try
        {
            var inp = a.GetInputs();
            float thrust = 0f;
            if (a.engines != null) foreach (var e in a.engines) thrust += e.GetThrust();
            var tr = (a.CockpitRB() ?? a.rb).transform;
            Vector3 lv = tr.InverseTransformDirection(a.rb.velocity);
            Vector3 w = tr.InverseTransformDirection((a.CockpitRB() ?? a.rb).angularVelocity) * Mathf.Rad2Deg;
            return string.Format(CultureInfo.InvariantCulture,
                "in p{0:0.00} r{1:0.00} y{2:0.00} t{3:0.00} | thrust {4:0} N ign {5} simple {6} | spd {7:0} vs {8:0} aoa {9:0.0} pitchAtt {10:0} bank {11:0} | w {12:0},{13:0},{14:0} g {15:0.0}",
                inp.pitch, inp.roll, inp.yaw, inp.throttle, thrust, a.Ignition, a.simplePhysics, a.speed, a.rb.velocity.y,
                Mathf.Atan2(-lv.y, lv.z) * Mathf.Rad2Deg, Mathf.Asin(Mathf.Clamp(tr.forward.y, -1f, 1f)) * Mathf.Rad2Deg,
                Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, tr.forward), tr.up, tr.forward), w.x, w.y, w.z, a.gForce);
        }
        catch (Exception ex) { return "diag failed: " + ex.Message; }
    }

    private static string _lastSeen = "";
    private static float _alpha = 25f, _prevAoa;

    private static bool Lost(Aircraft a)
    {
        if (a == null || a.disabled) return true;
        _lastSeen = $"pilot {(a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null ? a.pilots[0].currentState?.GetType().Name : "none")}, last seen at {a.radarAlt:0} m radar alt, {a.speed * 3.6f:0} km/h, pos ({a.GlobalPosition().x:0}, {a.GlobalPosition().y:0}, {a.GlobalPosition().z:0}), g {a.gForce:0.0}";
        return false;
    }

    private static float Heading(Aircraft a)
    {
        Vector3 v = a.rb.velocity;
        return Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
    }

    /// <summary>Puts the jet level along its current heading at a given speed (single-body physics only).</summary>
    private static void SetSpeed(Aircraft a, float speed)
    {
        Vector3 f = a.transform.forward;
        f.y = 0f;
        f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        // Every part's body gets the same velocity, so no joint is loaded by the reset.
        var bodies = new HashSet<Rigidbody> { a.rb };
        if (a.partLookup != null)
            foreach (var part in a.partLookup)
                if (part != null && part.rb != null) bodies.Add(part.rb);
        foreach (var rb in bodies)
        {
            rb.velocity = f * speed;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private static void Despawn(Aircraft a)
    {
        try
        {
            var objects = NuclearOption.Networking.NetworkManagerNuclearOption.i?.ServerObjectManager;
            if (objects != null && a.Identity != null) objects.Destroy(a.Identity, true);
            else UnityEngine.Object.Destroy(a.gameObject);
        }
        catch { /* gone */ }
    }

    // ---- fitting ------------------------------------------------------------------------------------------

    /// <summary>Least-squares fits of the estimators against the measured fleet (only when there are enough points).</summary>
    private static void Fit(List<AirframeProfile> fleet, Action<string> log)
    {
        if (fleet.Count < 7)
        {
            log($"survey: only {fleet.Count} complete measurements; keeping the default estimator coefficients");
            return;
        }
        var c = AirframeProfiles.Coef.TurnBase; // touch to keep the class initialised
        var turn = Solve(fleet.Select(r => new[] { 1f, r.ThrustToWeight, r.WingLoading, r.Vectoring ? 1f : 0f, r.AlphaLimit }).ToList(),
                         fleet.Select(r => r.SustainedTurn).ToList());
        if (turn != null)
        {
            AirframeProfiles.Coef.TurnBase = turn[0];
            AirframeProfiles.Coef.TurnPerTW = turn[1];
            AirframeProfiles.Coef.TurnPerWL = -turn[2];
            AirframeProfiles.Coef.TurnVectoring = turn[3];
            AirframeProfiles.Coef.TurnPerAlpha = turn[4];
        }
        var bleed = Solve(fleet.Select(r => new[] { 1f, r.ThrustToWeight }).ToList(), fleet.Select(r => r.TurnBleed).ToList());
        if (bleed != null)
        {
            AirframeProfiles.Coef.BleedBase = bleed[0];
            AirframeProfiles.Coef.BleedPerTW = -bleed[1];
        }
        var accel = Solve(fleet.Select(r => new[] { 1f, r.ThrustToWeight }).ToList(), fleet.Select(r => r.Accel).ToList());
        if (accel != null)
        {
            AirframeProfiles.Coef.AccelDrag = -accel[0];
            AirframeProfiles.Coef.AccelPerTW = accel[1];
        }
        var roll = Solve(fleet.Select(r => new[] { 1f, r.WingLoading }).ToList(), fleet.Select(r => r.RollRate).ToList());
        if (roll != null)
        {
            AirframeProfiles.Coef.RollBase = roll[0];
            AirframeProfiles.Coef.RollPerWL = -roll[1];
        }
        var k = AirframeProfiles.Coefficients.Current();
        log(string.Format(CultureInfo.InvariantCulture,
            "survey: fitted turn = {0:0.00} + {1:0.00} T/W - {2:0.0000} wingload + {3:0.00} vectoring + {10:0.000} alpha; bleed = {4:0.00} - {5:0.00} T/W; " +
            "accel = {6:0.00} T/W - {7:0.00}; roll = {8:0} - {9:0.000} wingload", k.TurnBase, k.TurnPerTW, k.TurnPerWL, k.TurnVectoring,
            k.BleedBase, k.BleedPerTW, k.AccelPerTW, k.AccelDrag, k.RollBase, k.RollPerWL, k.TurnPerAlpha));
        _ = c;
    }

    /// <summary>Ordinary least squares via the normal equations; null if singular.</summary>
    private static float[] Solve(List<float[]> rows, List<float> y)
    {
        int n = rows[0].Length;
        var m = new double[n, n + 1];
        for (int r = 0; r < rows.Count; r++)
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) m[i, j] += rows[r][i] * rows[r][j];
                m[i, n] += rows[r][i] * y[r];
            }
        for (int i = 0; i < n; i++) m[i, i] += 1e-6; // a touch of ridge so a constant column (no vectoring in fleet) is safe
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-9) return null;
            for (int j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int j = 0; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(m[i, n] / m[i, i]);
        return x;
    }

    private static void WriteCsv(string path, List<AirframeProfile> rows)
    {
        var sb = new StringBuilder("name,key,measured,style,mass_kg,thrust_N,thrust_to_weight,wing_area_m2,wing_loading,corner_ms,max_speed_ms,g_limit,vectoring,guns,missile_range_m,sustained_turn_dps,peak_turn_dps,turn_bleed_ms2,accel_ms2,roll_dps,slow_pitch_dps,peak_g,notes\n");
        foreach (var p in rows)
            sb.AppendLine(string.Join(",", new object[]
            {
                Q(p.Name), Q(p.Key), p.Measured, p.Style, F(p.Mass), F(p.Thrust), F(p.ThrustToWeight, "0.000"), F(p.WingArea), F(p.WingLoading),
                F(p.CornerSpeed), F(p.MaxSpeed), F(p.GLimit), p.Vectoring, p.Guns, F(p.MissileRange), F(p.SustainedTurn, "0.0"), F(p.PeakTurn, "0.0"),
                F(p.TurnBleed, "0.0"), F(p.Accel, "0.0"), F(p.RollRate), F(p.SlowPitch), F(p.PeakG, "0.0"), Q(p.Notes),
            }));
        File.WriteAllText(path, sb.ToString());
    }

    private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";
    private static string F(float v, string fmt = "0") => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>Holds the game camera high above the map centre, far from every test jet.</summary>
    private sealed class ParkedCamera : CameraBaseState
    {
        public override void EnterState(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(Datum.origin);
            cam.transform.SetParent(cam.cameraPivot);
            cam.transform.localPosition = Vector3.zero;
        }

        public override void LeaveState(CameraStateManager cam) { }

        public override void FixedUpdateState(CameraStateManager cam) { }

        public override void UpdateState(CameraStateManager cam)
        {
            cam.cameraPivot.localPosition = new GlobalPosition(16000f, 12000f, 16000f).AsVector3();
            cam.transform.localPosition = Vector3.zero;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.down + Vector3.forward * 0.3f, Vector3.forward);
        }
    }
}
