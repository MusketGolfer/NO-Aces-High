using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// The aces' systems and judgement outside the stick (host only, every frame from Encounters.Tick):
/// - power: capacitors kept full (UnlimitedPower);
/// - jamming: self-protection ECM fired every frame (a radar seeker that has not acquired cannot inside 5 km
///   against more than 2 ECM, and ECM is subtracted from every radar return), and jamming pods on the target;
/// - going dark: the flight's radars and jammers off on the way in while their side tracks the target;
/// - retreat: winchester or bingo fuel, one ace at a time;
/// - counter-missile shots: a missile spent on an incoming missile when it is worth it;
/// - Parthian shots: firing back at a pursuer behind (retreating aces here; the hunt's extend phase calls in).
/// </summary>
internal static class AceTactics
{
    private static readonly Dictionary<Aircraft, RadarJammer[]> Jammers = new();
    private static readonly Dictionary<Aircraft, float> NextIntercept = new();
    private static readonly Dictionary<Aircraft, float> NextParthian = new();
    private static readonly HashSet<Missile> Engaged = new();
    private static readonly HashSet<string> WeaponsLogged = new();
    private static readonly HashSet<Aircraft> RetreatChecked = new();
    private static float _nextRetreatCheck;

    // Going dark (flight-wide).
    private static bool _dark;
    private static float _darkChange, _nextDarkCheck;
    private static bool _darkAnnounced;
    private const float DarkMinKm = 12f, DarkMaxKm = 60f, DarkMinHold = 10f, LitMinHold = 15f;

    private static readonly MethodInfo RpcToggleRadar = typeof(Aircraft).GetMethod("RpcToggleRadar", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool Dark => _dark;

    internal static void Reset()
    {
        Jammers.Clear();
        NextIntercept.Clear();
        NextParthian.Clear();
        Engaged.Clear();
        RetreatChecked.Clear();
        _dark = false;
        _darkChange = _nextDarkCheck = 0f;
        _darkAnnounced = false;
    }

    internal static void Tick(ActiveEncounter enc, Aircraft target)
    {
        var fighting = Encounters.SquadAircraft().ToList();
        UpdateDark(enc, target, fighting);

        foreach (var a in fighting)
        {
            if (a == null || a.disabled) continue;
            if (Plugin.UnlimitedPower.Value) KeepCharged(a);
            bool emit = !_dark || Encounters.IsRetreating(a);
            SetRadar(a, emit);
            if (Plugin.AlwaysJam.Value && emit && !IntroGate.Holding) Jam(a, target);
        }

        float now = Time.time;
        foreach (var a in fighting)
        {
            if (a == null || a.disabled) continue;
            LogWeaponsOnce(a);
            if (Plugin.CounterMissiles.Value && !IntroGate.Holding) TryIntercept(a, enc.Tier);
        }
        if (Plugin.Retreat.Value && now >= _nextRetreatCheck)
        {
            _nextRetreatCheck = now + 1f;
            foreach (var a in fighting) CheckRetreat(a);
        }
    }

    /// <summary>
    /// Aces that left the fight on their own: power and ECM kept up, radars on, and fire back at anyone chasing.
    /// Runs every frame, also after the encounter itself has ended.
    /// </summary>
    internal static void TickRetreating()
    {
        foreach (var a in Encounters.Retreated.Where(Encounters.IsRetreating).ToList())
        {
            if (a == null || a.disabled) continue;
            if (Plugin.UnlimitedPower.Value) KeepCharged(a);
            SetRadar(a, true);
            var pursuer = NearestPursuer(a);
            if (Plugin.AlwaysJam.Value) Jam(a, pursuer);
            if (Plugin.ParthianShots.Value) TryParthian(a, pursuer);
        }
    }

    // ---- power and jamming --------------------------------------------------------------------------

    private static void KeepCharged(Aircraft a)
    {
        try { a.GetPowerSupply()?.SetFullyCharged(); } catch { /* no power supply */ }
    }

    private static void Jam(Aircraft a, Aircraft target)
    {
        if (!Jammers.TryGetValue(a, out var list))
        {
            list = a.GetComponentsInChildren<RadarJammer>(true);
            Jammers[a] = list;
            if (list.Length > 0) Plugin.Log.LogInfo($"{a.definition?.unitName}: {list.Length} self-protection jammer(s), running constantly.");
        }
        foreach (var j in list)
            if (j != null && j.aircraft != null)
                try { j.Fire(); } catch { /* jammer gone */ }

        // Jamming pods (offensive): on the target's radar while it is in reach and tracked.
        if (target == null || a.weaponStations == null) return;
        float dist = Vector3.Distance(a.transform.position, target.transform.position);
        if (dist > 40000f) return;
        foreach (var station in a.weaponStations)
        {
            if (station?.WeaponInfo == null || !station.WeaponInfo.jammer) continue;
            foreach (var w in station.Weapons)
                if (w is JammingPod pod)
                    try
                    {
                        pod.SetTarget(target);
                        pod.Fire(a, target, a.rb != null ? a.rb.velocity : Vector3.zero, station, target.GlobalPosition());
                    }
                    catch { /* pod gone */ }
        }
    }

    private static void SetRadar(Aircraft a, bool on)
    {
        var radar = a.radar;
        if (radar == null || radar.activated == on) return;
        try
        {
            if (RpcToggleRadar != null) RpcToggleRadar.Invoke(a, new object[] { on });
            else radar.activated = on;
        }
        catch { radar.activated = on; }
    }

    /// <summary>Jamming pods turned on an incoming radar missile (its seeker counts jamming aimed at it).</summary>
    internal static void JamMissile(Aircraft a, Missile m)
    {
        if (a?.weaponStations == null || m == null || m.disabled) return;
        foreach (var station in a.weaponStations)
        {
            if (station?.WeaponInfo == null || !station.WeaponInfo.jammer) continue;
            foreach (var w in station.Weapons)
                if (w is JammingPod pod)
                    try
                    {
                        pod.SetTarget(m);
                        pod.Fire(a, m, a.rb != null ? a.rb.velocity : Vector3.zero, station, m.GlobalPosition());
                    }
                    catch { /* pod gone */ }
        }
    }

    // ---- going dark ------------------------------------------------------------------------------------

    /// <summary>
    /// The flight runs dark (radars and jammers off) on the way in while their side holds a track on the target
    /// without them: from 60 km down to 12 km, nothing fired at them, at least 10 s at a time. They light up when
    /// the track goes stale, inside 12 km, when a missile is in the air at one of them, and stay lit 15 s.
    /// </summary>
    private static void UpdateDark(ActiveEncounter enc, Aircraft target, List<Aircraft> flight)
    {
        float now = Time.time;
        if (now < _nextDarkCheck) return;
        _nextDarkCheck = now + 0.5f;
        bool want = false, danger = false;
        if (Plugin.GoDark.Value && target != null && flight.Count > 0 && enc.EnemyHQ != null)
        {
            float nearest = flight.Min(a => Vector3.Distance(a.transform.position, target.transform.position));
            bool tracked = false;
            try { tracked = enc.EnemyHQ.IsTargetBeingTracked(target); } catch { /* no tracking info */ }
            bool fired = flight.Any(a => a.GetMissileWarningSystem() is { } mw && mw.IsWarning());
            want = tracked && !fired && nearest > DarkMinKm * 1000f && nearest < DarkMaxKm * 1000f;
            // Lights straight up for danger; otherwise only after the minimum time either way.
            danger = fired || !tracked || nearest <= DarkMinKm * 1000f;
            if (_dark && danger) want = false;
        }
        if (want == _dark) return;
        float held = now - _darkChange;
        if (want && held < LitMinHold) return;
        if (!want && !danger && held < DarkMinHold && Plugin.GoDark.Value && target != null) return; // danger lights up at once
        _dark = want;
        _darkChange = now;
        Plugin.Log.LogInfo($"{enc.SquadronName}: {(want ? "going dark (radars and jammers off)" : "lighting up")}.");
        if (want && !_darkAnnounced)
        {
            _darkAnnounced = true;
            var lead = flight.FirstOrDefault();
            if (lead != null) Encounters.Taunt(lead, "dark");
        }
    }

    // ---- retreat ---------------------------------------------------------------------------------------

    private static void CheckRetreat(Aircraft a)
    {
        if (a == null || a.disabled) return;
        float fuel = 1f;
        try { fuel = a.GetFuelLevel(); } catch { /* no tanks */ }
        if (fuel > 0f && fuel < Plugin.RetreatFuel.Value)
        {
            Encounters.RetreatMember(a, "bingo", $"fuel at {fuel:P0}");
            return;
        }
        if (Winchester(a, out string why)) Encounters.RetreatMember(a, "winchester", why);
    }

    /// <summary>No air-to-air missile left, and no gun or under a tenth of its rounds.</summary>
    internal static bool Winchester(Aircraft a, out string why)
    {
        why = null;
        if (a.weaponStations == null) return false;
        int missiles = 0, rounds = 0, fullRounds = 0;
        foreach (var s in a.weaponStations)
        {
            var info = s?.WeaponInfo;
            if (info == null || s.Cargo) continue;
            if (info.gun) { rounds += s.Ammo; fullRounds += s.FullAmmo; }
            else if (info.missile && !info.bomb && info.effectiveness.antiAir >= 0.5f) missiles += s.Ammo;
        }
        if (missiles > 0) return false;
        if (fullRounds > 0 && rounds > fullRounds * 0.1f) return false;
        why = fullRounds > 0 ? $"no missiles, {rounds} rounds left" : "no missiles and no gun";
        return true;
    }

    // ---- counter-missile shots ------------------------------------------------------------------------

    /// <summary>
    /// Spend a missile on an incoming one when the trade is worth it. Threat (what the shot would save): 0.35,
    /// + 0.3 radar-guided (flares do nothing), + 0.15 slow, + 0.2 more than one inbound, + 0.2 out of flares,
    /// + 0.15 the launcher is ahead too (shooting keeps the nose on the attack instead of breaking off it),
    /// - 0.25 an infrared missile with flares in hand. Cost: 0.6 / missiles left. Fire when threat - cost is at
    /// least 0.35; the last missile only when threat is at least 0.85; tier 2 only from 0.7 threat; never tier 1.
    /// The shot needs the missile within 35 deg of the nose and 3-12 s out.
    /// </summary>
    private static void TryIntercept(Aircraft a, int tier)
    {
        if (tier <= 1) return;
        float now = Time.time;
        if (NextIntercept.TryGetValue(a, out var next) && now < next) return;
        NextIntercept[a] = now + 0.5f;
        var mw = a.GetMissileWarningSystem();
        if (mw == null || mw.knownMissiles == null) return;
        var inbound = mw.knownMissiles.Where(m => m != null && !m.disabled).ToList();
        if (inbound.Count == 0) return;

        Missile best = null;
        float bestImpact = 0f;
        foreach (var m in inbound)
        {
            if (Engaged.Contains(m)) continue;
            Vector3 rel = m.transform.position - a.transform.position;
            float dist = rel.magnitude;
            Vector3 mv = m.rb != null ? m.rb.velocity : Vector3.zero;
            float closing = -Vector3.Dot(mv - a.rb.velocity, rel / Mathf.Max(dist, 1f));
            if (closing < 50f) continue;
            float impact = dist / closing;
            if (impact < 3f || impact > 12f) continue;
            if (Vector3.Angle(a.transform.forward, rel) > 35f) continue;
            if (best == null || impact < bestImpact) { best = m; bestImpact = impact; }
        }
        if (best == null) return;

        float bestDist = Vector3.Distance(best.transform.position, a.transform.position);
        WeaponStation station = null;
        int missilesLeft = 0;
        foreach (var s in a.weaponStations ?? new List<WeaponStation>())
        {
            var info = s?.WeaponInfo;
            if (info == null || s.Cargo || !info.missile || info.bomb || s.Ammo <= 0) continue;
            if (info.effectiveness.antiAir >= 0.5f) missilesLeft += s.Ammo;
            if (info.effectiveness.antiMissile < 0.2f) continue;
            var req = info.targetRequirements;
            if (bestDist > req.maxRange * 0.8f || bestDist < Mathf.Max(req.minRange, 400f)) continue;
            if (station == null || info.effectiveness.antiMissile > station.WeaponInfo.effectiveness.antiMissile) station = s;
        }
        if (station == null) return;
        missilesLeft = Mathf.Max(missilesLeft, station.Ammo);

        string seeker = "";
        try { seeker = best.GetSeekerType() ?? ""; } catch { /* unknown seeker */ }
        bool radarGuided = seeker.Contains("ARH") || seeker.Contains("SARH") || seeker.Contains("Radar");
        float corner = a.GetAircraftParameters()?.cornerSpeed ?? 150f;
        int flares = 0;
        try { flares = a.GetComponentsInChildren<FlareEjector>(true).Sum(f => f.ammo); } catch { /* none */ }

        float threat = 0.35f;
        if (radarGuided) threat += 0.3f;
        if (a.speed < corner * 0.75f) threat += 0.15f;
        if (inbound.Count > 1) threat += 0.2f;
        if (flares == 0) threat += 0.2f;
        if (!radarGuided && flares > 2) threat -= 0.25f;
        var launcher = best.owner;
        if (launcher != null && Vector3.Angle(a.transform.forward, launcher.transform.position - a.transform.position) < 30f) threat += 0.15f;
        float cost = 0.6f / Mathf.Max(1, missilesLeft);
        bool ok = threat - cost >= 0.35f && (missilesLeft > 1 || threat >= 0.85f) && (tier >= 3 || threat >= 0.7f);
        if (!ok) return;

        try
        {
            var wm = a.weaponManager;
            wm.ClearTargetList();
            wm.AddTargetList(best);
            wm.SetActiveStation(station.Number);
            wm.Fire();
            Engaged.Add(best);
            NextIntercept[a] = now + 2f;
            Plugin.Log.LogInfo($"{a.definition?.unitName}: counter-missile shot, {station.WeaponInfo.weaponName} at a {seeker} missile {bestDist:0} m out " +
                               $"({bestImpact:0.0} s); threat {threat:0.00} against cost {cost:0.00} ({missilesLeft} missiles).");
            Encounters.Taunt(a, "intercept");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Counter-missile shot failed: " + ex.Message);
            Engaged.Add(best);
        }
    }

    // ---- Parthian shots ---------------------------------------------------------------------------------

    private static Aircraft NearestPursuer(Aircraft a)
    {
        Aircraft best = null;
        float bestDist = float.MaxValue;
        foreach (var h in Encounters.PursuerCandidates())
        {
            if (h == null || h.disabled) continue;
            float d = Vector3.Distance(h.transform.position, a.transform.position);
            if (d < bestDist) { best = h; bestDist = d; }
        }
        return best;
    }

    /// <summary>
    /// A radar missile over the shoulder at a pursuer in the rear hemisphere, 1.5 km to half the missile's range,
    /// that is closing or holding on: the missile flies out on the track its side holds and goes active on its own.
    /// One every 8 s.
    /// </summary>
    internal static bool TryParthian(Aircraft a, Aircraft pursuer)
    {
        if (!Plugin.ParthianShots.Value || a == null || pursuer == null || pursuer.disabled || a.weaponStations == null) return false;
        float now = Time.time;
        if (NextParthian.TryGetValue(a, out var next) && now < next) return false;
        Vector3 rel = pursuer.transform.position - a.transform.position;
        float dist = rel.magnitude;
        if (Vector3.Angle(a.transform.forward, rel) < 100f) return false; // not behind
        Vector3 pv = pursuer.rb != null ? pursuer.rb.velocity : Vector3.zero;
        float closing = -Vector3.Dot(pv - a.rb.velocity, rel / Mathf.Max(dist, 1f));
        if (closing < -30f) return false; // falling away: not worth a missile
        foreach (var s in a.weaponStations)
        {
            var info = s?.WeaponInfo;
            if (info == null || s.Cargo || !info.missile || info.bomb || s.Ammo <= 0 || info.effectiveness.antiAir < 0.5f) continue;
            if (!AceSpawner.IsRadarMissile(info)) continue; // radar missiles only: an IR seeker cannot see behind
            if (dist < 1500f || dist > info.targetRequirements.maxRange * 0.5f) continue;
            try
            {
                var wm = a.weaponManager;
                wm.ClearTargetList();
                wm.AddTargetList(pursuer);
                wm.SetActiveStation(s.Number);
                wm.Fire();
                NextParthian[a] = now + 8f;
                Plugin.Log.LogInfo($"{a.definition?.unitName}: Parthian shot, {info.weaponName} over the shoulder at a pursuer {dist:0} m behind, closing {closing:0} m/s.");
                Encounters.Taunt(a, "parthian");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Parthian shot failed: " + ex.Message);
                NextParthian[a] = now + 8f;
                return false;
            }
        }
        return false;
    }

    // ---- diagnostics ------------------------------------------------------------------------------------

    private static void LogWeaponsOnce(Aircraft a)
    {
        string key = a.definition?.unitName ?? "?";
        if (a.weaponStations == null || !WeaponsLogged.Add(key)) return;
        var parts = a.weaponStations.Where(s => s?.WeaponInfo != null && !s.Cargo).Select(s =>
        {
            var i = s.WeaponInfo;
            return $"{i.weaponName} x{s.Ammo} (air {i.effectiveness.antiAir:0.00}, missile {i.effectiveness.antiMissile:0.00}, " +
                   $"{(AceSpawner.IsRadarMissile(i) ? "radar-guided" : i.missile ? "IR" : "unguided")}, range {i.targetRequirements.minRange:0}-{i.targetRequirements.maxRange:0}" +
                   $"{(i.jammer ? ", jammer" : "")}{(i.gun ? ", gun" : "")})";
        });
        Plugin.Log.LogInfo($"{key} weapons: {string.Join("; ", parts)}");
    }
}
