using System;
using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Close formation on the way in. The stock autopilot only flies toward a point, which holds a slot to within a
/// few hundred metres; inside 400 m of its slot a wingman is flown here instead, from the FilterInputs hook:
/// - velocity: the lead's velocity plus a correction toward the slot (0.3 per second of the error, at most 25 m/s),
///   with the nose offset by the jet's own AoA so the flight path, not the nose, follows it;
/// - roll: matches the lead's bank, so turns are flown together;
/// - throttle: the lead's, trimmed on the speed it needs;
/// - spacing: anything of the flight closer than 30 m pushes it away.
/// It hands back to the stock autopilot if the slot is far, the lead pulls hard, or the ground is close.
/// </summary>
internal static class FormationFlying
{
    private const float TakeOverWithin = 2500f, ReleaseBeyond = 3000f;
    private static readonly System.Collections.Generic.HashSet<Aircraft> Holding = new();

    internal static void Reset()
    {
        Holding.Clear();
        LeadPlan.Clear();
    }

    internal static bool Trace = Environment.GetCommandLineArgs().Contains("-noahTraceFormation");
    private static Aircraft _traced;
    private static float _nextTrace;

    /// <summary>
    /// While the flight holds formation its lead flies at 90% of cruise throttle and turns at no more than 35 deg of bank and half pull, so the
    /// wingmen (an outside slot flies a wider circle) can stay with it. Applied after the stock AI's inputs.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<Aircraft, (float speed, float alt)> LeadPlan = new();

    /// <summary>
    /// While the flight holds formation its lead flies a calm ingress itself instead of the stock combat AI (which
    /// zooms and turns hard as it sets up attacks, and nothing can hold formation on that): straight for the target
    /// at its entry speed (at 90% of cruise throttle, so the wingmen have power in hand), holding its entry height,
    /// banking no more than 30 deg.
    /// </summary>
    internal static bool FlyLead(Aircraft a)
    {
        if (!Encounters.IsFormationLead(a) || a.rb == null) { LeadPlan.Remove(a); return false; }
        var target = Encounters.FormationTarget();
        if (target == null) return false;
        if (!LeadPlan.TryGetValue(a, out var plan))
        {
            plan = (Mathf.Max(a.speed, 150f), a.transform.position.y);
            LeadPlan[a] = plan;
        }
        Vector3 to = target.transform.position - a.transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 1f) return false;
        float climb = Mathf.Clamp((plan.alt - a.transform.position.y) * 0.08f, -15f, 15f);
        if (a.radarAlt < 600f) climb = Mathf.Max(climb, 10f);
        Vector3 want = to.normalized * plan.speed + Vector3.up * climb;
        float cruise = a.GetAircraftParameters()?.cruiseThrottle ?? 0.85f;
        Steer(a, want, cruise * 0.9f, 0f, 30f, 0f);
        var inputs = a.GetInputs();
        if (inputs.throttle > cruise * 0.9f) inputs.throttle = cruise * 0.9f;
        return true;
    }

    internal static void LimitLead(Aircraft a)
    {
        if (!Encounters.IsFormationLead(a) || a.rb == null) return;
        var rb = a.CockpitRB() ?? a.rb;
        Transform tr = rb.transform;
        Vector3 fwd = tr.forward, up = tr.up;
        Vector3 level = Vector3.up - fwd * Vector3.Dot(Vector3.up, fwd);
        if (level.sqrMagnitude < 1e-3f) return;
        level.Normalize();
        float bank = Vector3.SignedAngle(level, up, fwd);
        var inputs = a.GetInputs();
        inputs.pitch = Mathf.Clamp(inputs.pitch, -0.5f, 0.5f);
        // No faster than cruise (whatever AI mode it is in), so the wingmen always have throttle in hand to catch up.
        float cruise = a.GetAircraftParameters()?.cruiseThrottle ?? 0.85f;
        if (inputs.throttle > cruise * 0.9f) inputs.throttle = cruise * 0.9f;
        if (Mathf.Abs(bank) <= 35f) return;
        Vector3 wantUp = Quaternion.AngleAxis(Mathf.Sign(bank) * 35f, fwd) * level;
        Vector3 rollW = PostStall.TurnToward(up, wantUp, 3f, 1.5f);
        float target = tr.InverseTransformDirection(fwd * Vector3.Dot(rollW, fwd)).z;
        float rate = tr.InverseTransformDirection(rb.angularVelocity).z;
        inputs.roll = Mathf.Clamp(1.2f * (rate - target), -1f, 1f);
    }

    /// <summary>The 3 s split after a break (Encounters.TryGetBreakSplit): fly the split heading at full power.</summary>
    internal static bool Split(Aircraft a)
    {
        if (a?.rb == null || !Encounters.TryGetBreakSplit(a, out var dir)) return false;
        if (a.GetMissileWarningSystem() is { } mw && mw.TryGetNearestIncoming(out var m) && m != null &&
            Vector3.Distance(m.transform.position, a.transform.position) < 2500f) return false; // a missile close by comes first
        Steer(a, dir * Mathf.Max(a.speed, 150f), 1f, 0f, 70f, 0f);
        return true;
    }

    /// <summary>
    /// Collision avoidance between aces, whatever they are flying: a squadmate within 150 m that will reach us inside
    /// 2.5 s (or anything inside 50 m and closing) gets turned away from, toward vertical separation. Returns true while
    /// it is flying the jet.
    /// </summary>
    internal static bool Deconflict(Aircraft a)
    {
        if (a?.rb == null || !Encounters.IsAce(a)) return false;
        Aircraft worst = null;
        float worstTime = float.MaxValue;
        Vector3 worstRel = Vector3.zero;
        foreach (var other in Encounters.SquadAircraft())
        {
            if (other == null || other == a || other.disabled || other.rb == null) continue;
            Vector3 rel = other.transform.position - a.transform.position;
            float d = rel.magnitude;
            if (d > 150f || d < 0.1f) continue;
            float closing = -Vector3.Dot(other.rb.velocity - a.rb.velocity, rel / d);
            if (closing <= 0f) continue;
            float time = (d - 25f) / closing; // to a 25 m miss
            if (time < 2.5f || d < 50f && closing > 3f) // inside 50 m only when really closing (formation slots sit about that close)
                if (time < worstTime) { worst = other; worstTime = time; worstRel = rel; }
        }
        if (worst == null) return false;
        // Away from it, and up or down away from it (vertical separation is the quickest way apart).
        Vector3 away = -worstRel.normalized;
        float vertical = worstRel.y > 0f ? -1f : 1f;
        Vector3 escape = (away + Vector3.up * vertical * 0.8f).normalized;
        if (a.radarAlt < 300f && escape.y < 0f) escape.y = 0.2f;
        if (Time.time >= _nextDeconflictLog)
        {
            _nextDeconflictLog = Time.time + 2f;
            Plugin.Log.LogInfo($"{a.definition?.unitName}: avoiding a squadmate {worstRel.magnitude:0} m away, {Mathf.Max(0f, worstTime):0.0} s from collision.");
        }
        Steer(a, a.rb.velocity + escape * 60f, 1f, 0f, 80f, 0f);
        return true;
    }

    private static float _nextDeconflictLog;

    internal static bool Fly(Aircraft a)
    {
        if (!Encounters.TryGetFormationSlot(a, out var leader, out var offset))
        {
            Holding.Remove(a);
            return false;
        }
        if (a.rb == null || leader.rb == null) return false;

        Vector3 lv = leader.rb.velocity;
        Vector3 fwdFlat = Formations.FlatForward(leader, leader.transform.forward);
        GlobalPosition slot = Formations.SlotPosition(leader.GlobalPosition(), fwdFlat, offset);
        Vector3 error = slot - a.GlobalPosition();
        float dist = error.magnitude;
        bool holding = Holding.Contains(a);
        if (dist > (holding ? ReleaseBeyond : TakeOverWithin) || leader.gForce > 3.5f || a.radarAlt < 300f)
        {
            Holding.Remove(a);
            return false;
        }
        if (!holding)
        {
            Holding.Add(a);
            if (_traced == null || _traced.disabled) _traced = a;
            Plugin.Log.LogInfo($"{a.definition?.unitName}: in close formation, {dist:0} m from the slot.");
        }

        // Wanted velocity: the slot's own (the lead's, plus the swing of the slot round the lead as it turns:
        // an outside slot has further to go) and a capped pull toward it.
        float headingRate = Vector3.Dot(leader.rb.angularVelocity, Vector3.up);
        Vector3 slotVel = lv + Vector3.Cross(Vector3.up * headingRate, (Vector3)(slot - leader.GlobalPosition()));
        // Up to 25 m/s of closure near the slot, rising to 70 m/s far out (a rejoin after a turn or a dive).
        Vector3 correction = Vector3.ClampMagnitude(error * 0.3f, Mathf.Lerp(25f, 70f, Mathf.InverseLerp(300f, 1500f, dist)));
        // Keep clear of the rest of the flight.
        foreach (var other in Encounters.SquadAircraft())
        {
            if (other == null || other == a || other.disabled) continue;
            Vector3 away = a.transform.position - other.transform.position;
            float d = away.magnitude;
            if (d < 30f && d > 0.1f) correction += away / d * (30f - d) * 0.8f;
        }
        Vector3 want = slotVel + correction;
        if (want.sqrMagnitude < 1f) return false;
        float leadThrottle = leader.GetInputs()?.throttle ?? 0.8f;
        Steer(a, want, leadThrottle, lv.magnitude, 60f, dist);
        return true;
    }

    /// <summary>
    /// Flies a wanted velocity by bank-to-turn. baseThrottle is where the throttle sits at the right speed;
    /// below refSpeed less 50 km/h it goes to full power; maxBank bounds the lift's lean from vertical.
    /// </summary>
    private static void Steer(Aircraft a, Vector3 want, float baseThrottle, float refSpeed, float maxBank, float dist)
    {
        var rb = a.CockpitRB() ?? a.rb;
        Transform tr = rb.transform;
        Vector3 fwd = tr.forward, up = tr.up;
        Vector3 vel = a.rb.velocity;
        float speed = Mathf.Max(vel.magnitude, 30f);
        Vector3 velDir = vel.sqrMagnitude > 1f ? vel.normalized : fwd;

        // Bank-to-turn, like a pilot: the acceleration wanted (close the velocity error in about 2 s, at most
        // 3 g), plus 1 g against gravity, is the lift wanted. Roll the lift vector onto it, pull at the rate that
        // makes it (a / V), and no rudder: sideways corrections flown by skidding cost so much drag the wingman
        // never catches up.
        Vector3 accel = Vector3.ClampMagnitude((want - vel) * 0.5f, 30f);
        // Formation flying never goes inverted or negative: vertical acceleration between -0.6 g and +1.5 g, and
        // the lift within 60 deg of vertical.
        accel.y = Mathf.Clamp(accel.y, -0.6f * 9.81f, 1.5f * 9.81f);
        Vector3 lift = accel + Vector3.up * 9.81f;
        Vector3 liftUp = Vector3.RotateTowards(Vector3.up, lift.normalized, maxBank * Mathf.Deg2Rad, 0f);
        lift = liftUp * lift.magnitude;
        lift -= velDir * Vector3.Dot(lift, velDir); // only the part square to the flight path turns it
        Vector3 omega = Vector3.zero;
        if (lift.sqrMagnitude > 0.01f)
        {
            Vector3 liftDir = lift.normalized;
            Vector3 liftOnPlane = liftDir - fwd * Vector3.Dot(liftDir, fwd);
            if (liftOnPlane.sqrMagnitude > 1e-4f)
            {
                // Gentle roll loop (a hotter one rocked the wings 40 deg either side in level flight).
                Vector3 rollW = PostStall.TurnToward(up, liftOnPlane.normalized, 1.6f, 1.2f);
                omega += fwd * Vector3.Dot(rollW, fwd);
            }
            // The flight path turns at a / V (gravity is carried by the lift, it does not turn the path). Pitch takes
            // that rate once the lift is roughly where it should be; the rudder only the small coordinated share.
            Vector3 accelPerp = accel - velDir * Vector3.Dot(accel, velDir);
            Vector3 pathRate = Vector3.Cross(velDir, accelPerp) / speed;
            float rolledOff = Vector3.Angle(up, liftOnPlane);
            float pitchPart = Vector3.Dot(pathRate, tr.right) * Mathf.Clamp01(1.5f - rolledOff / 60f);
            // Formation is not worth a stall: the pull fades out between 12 and 18 deg AoA.
            float aoaNow = Mathf.Atan2(-tr.InverseTransformDirection(vel).y, tr.InverseTransformDirection(vel).z) * Mathf.Rad2Deg;
            if (pitchPart < 0f) pitchPart *= Mathf.Clamp01((18f - aoaNow) / 6f);
            float yawPart = Vector3.Dot(pathRate, up) * 0.3f;
            omega += tr.right * Mathf.Clamp(pitchPart, -0.6f, 0.6f) + up * yawPart;
        }

        // Throttle on the speed it needs; full power whenever it has dropped below the reference speed less 50 km/h.
        float speedError = want.magnitude - vel.magnitude;
        float throttle = Mathf.Clamp(baseThrottle + speedError * 0.06f, 0f, 1f);
        if (vel.magnitude < refSpeed - 14f) throttle = 1f;
        PostStall.ApplyRates(a, omega, throttle);
        if (Trace && Time.time >= _nextTrace && a == _traced)
        {
            _nextTrace = Time.time + 0.25f;
            var inp = a.GetInputs();
            Vector3 lw = tr.InverseTransformDirection(omega) * Mathf.Rad2Deg, rw = tr.InverseTransformDirection(rb.angularVelocity) * Mathf.Rad2Deg;
            Plugin.Log.LogInfo($"formation trace: err {dist:0} m, want-vel ({(want - vel).x:0},{(want - vel).y:0},{(want - vel).z:0}), accel {accel.magnitude:0}, " +
                               $"bank {Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, fwd), up, fwd):0}, cmd rate {lw.x:0},{lw.y:0},{lw.z:0}, rate {rw.x:0},{rw.y:0},{rw.z:0}, " +
                               $"in p{inp.pitch:0.00} r{inp.roll:0.00} y{inp.yaw:0.00}, g {a.gForce:0.0}");
        }
        // Too fast for the slot (the inside of the lead's turn, a rejoin overshooting): idle and speed brake, rather
        // than hauling the nose up to bleed it off. Otherwise the brake stays in (the stock AI underneath may want it out).
        bool overshooting = speedError < -15f;
        if (overshooting) a.GetInputs().throttle = 0f;
        a.GetInputs().brake = overshooting ? 1f : 0f;
    }
}
