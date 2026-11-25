using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NOAcesHigh;

/// <summary>Camera shot lists, after Project Wingman's and Ace Combat 7's squadron intros.</summary>
internal enum IntroShots
{
    Random,
    /// <summary>Fly-by, orbit, tail close-up, formation from behind.</summary>
    Classic,
    /// <summary>Close along the tail fins down the formation, then a wide shot from below.</summary>
    Tails,
    /// <summary>Long lens on the formation coming head-on, a roll-by underneath, then wide from below.</summary>
    HeadOn,
    /// <summary>Over a wingman's canopy at the rest of the flight, then a low chase from behind.</summary>
    Cockpit,
    /// <summary>The lead alone: a long-lens approach, a head-on close-up popping flares, a side pass.</summary>
    Lead,
    /// <summary>Under the nose looking up at the canopy, a slide along the fuselage, then the formation from ahead.</summary>
    Nose,
    /// <summary>Extreme long lens from far off, a beam pass on the lead, then the formation side-on.</summary>
    Distant,
    /// <summary>Riding behind the lead with the wingmen in frame, a rack focus from a wingman's wingtip onto the lead, then straight down on the formation.</summary>
    Escort,
    /// <summary>The flight against the sun, a long push-in onto the lead's canopy, then the whole formation from behind.</summary>
    Silhouette,
    /// <summary>Straight down on the formation as it turns under the camera, a beam pass, then riding behind the lead.</summary>
    Overhead,
}

/// <summary>Title-card designs.</summary>
internal enum CardStyle
{
    Match,
    /// <summary>Centred emblem that slams in with a flash; name, title, pilot below; letterbox.</summary>
    PwBoss,
    /// <summary>Everything lands at once with a shake; letterbox.</summary>
    Stamp,
    /// <summary>Fades in and types on; letterbox.</summary>
    Typewriter,
    /// <summary>Project Wingman: faded emblem behind a thin box with a blinking "-- CAUTION --" and the unit line.</summary>
    Caution,
    /// <summary>Ace Combat 7: bottom-left emblem, big name, small unit number and organisation line.</summary>
    Unit,
}

internal enum ShotKind
{
    Flyby, Orbit, TailClose, FormationRear,
    TailPan, BelowWide, HeadOn, BelowClose, Cockpit, RearLow,
    LeadApproach, LeadFlares, LeadSide,
    NoseClose, SideSlide, FrontWide,
    Distant, BeamPass, BeamWide,
    ChaseLead, WingmanFocus, TopDown, Silhouette, PushIn,
}

internal readonly struct Shot
{
    public readonly ShotKind Kind;
    public readonly float Start, End;
    public Shot(ShotKind kind, float start, float end) { Kind = kind; Start = start; End = end; }
}

/// <summary>One intro's timing: when the camera cuts in and out, when the card arrives, and what it shows.</summary>
internal sealed class IntroPlan
{
    public IntroShots Shots;
    public CardStyle Card;
    public readonly List<Shot> List = new();
    public float CutIn = 1f;     // camera leaves the player
    public float CameraEnd;      // camera returns; the card starts going
    public float CardAt;         // first card element
    public float Total;          // card fully gone
    public bool LeadFocus;       // the card names the lead pilot instead of the squadron

    public const float OutLength = 0.7f;

    /// <summary>Picks the shot list and card for an encounter. Without the camera cut it is a card over normal play.</summary>
    internal static IntroPlan For(ActiveEncounter enc, bool cinematic)
    {
        var plan = new IntroPlan { Card = enc.Card };
        if (!cinematic)
        {
            plan.Shots = IntroShots.Classic;
            plan.CardAt = 0.5f;
            plan.CameraEnd = 5.1f;
            plan.Total = plan.CameraEnd + 0.8f;
            return plan;
        }

        IntroShots kind = enc.Shots;
        if (kind == IntroShots.Random) kind = PickShots(enc.AliveCount);
        if (enc.AliveCount < 2 && kind is IntroShots.Tails or IntroShots.Cockpit or IntroShots.Escort) kind = IntroShots.Lead;
        plan.Shots = kind;
        plan.LeadFocus = kind == IntroShots.Lead;

        void add(ShotKind k, float end)
        {
            float start = plan.List.Count == 0 ? plan.CutIn : plan.List[plan.List.Count - 1].End;
            plan.List.Add(new Shot(k, start, end));
        }

        switch (kind)
        {
            case IntroShots.Tails:
                add(ShotKind.TailPan, 4.2f); add(ShotKind.BelowWide, 7.8f); plan.CardAt = 4.3f;
                break;
            case IntroShots.HeadOn:
                add(ShotKind.HeadOn, 3.4f); add(ShotKind.BelowClose, 4.8f); add(ShotKind.BelowWide, 7.8f); plan.CardAt = 4.9f;
                break;
            case IntroShots.Cockpit:
                add(ShotKind.Cockpit, 3.6f); add(ShotKind.RearLow, 7.8f); plan.CardAt = 3.7f;
                break;
            case IntroShots.Lead:
                add(ShotKind.LeadApproach, 3.0f); add(ShotKind.LeadFlares, 5.4f); add(ShotKind.LeadSide, 7.8f); plan.CardAt = 3.1f;
                break;
            case IntroShots.Nose:
                add(ShotKind.NoseClose, 3.2f); add(ShotKind.SideSlide, 4.8f); add(ShotKind.FrontWide, 7.8f); plan.CardAt = 4.9f;
                break;
            case IntroShots.Distant:
                add(ShotKind.Distant, 3.4f); add(ShotKind.BeamPass, 4.8f); add(ShotKind.BeamWide, 7.8f); plan.CardAt = 4.9f;
                break;
            case IntroShots.Escort:
                add(ShotKind.ChaseLead, 3.2f); add(ShotKind.WingmanFocus, 5.4f); add(ShotKind.TopDown, 7.8f); plan.CardAt = 3.3f;
                break;
            case IntroShots.Silhouette:
                add(ShotKind.Silhouette, 2.8f); add(ShotKind.PushIn, 5.6f); add(ShotKind.FormationRear, 7.8f); plan.CardAt = 5.7f;
                break;
            case IntroShots.Overhead:
                add(ShotKind.TopDown, 3.4f); add(ShotKind.BeamPass, 4.9f); add(ShotKind.ChaseLead, 7.8f); plan.CardAt = 5.0f;
                break;
            default:
                plan.Shots = IntroShots.Classic;
                add(ShotKind.Flyby, 3.0f); add(ShotKind.Orbit, 5.5f); add(ShotKind.TailClose, 7.0f); add(ShotKind.FormationRear, 7.8f);
                plan.CardAt = 3.2f;
                break;
        }
        plan.CameraEnd = plan.List[plan.List.Count - 1].End;
        plan.Total = plan.CameraEnd + 0.8f;
        return plan;
    }

    private static IntroShots PickShots(int alive)
    {
        var pool = new List<IntroShots> { IntroShots.Classic, IntroShots.HeadOn, IntroShots.Lead, IntroShots.Nose, IntroShots.Distant, IntroShots.Silhouette, IntroShots.Overhead };
        if (alive >= 2) pool.AddRange(new[] { IntroShots.Tails, IntroShots.Cockpit, IntroShots.Escort });
        return pool[Random.Range(0, pool.Count)];
    }

    internal int ShotIndexAt(float t)
    {
        for (int i = 0; i < List.Count; i++)
            if (t < List[i].End) return i;
        return List.Count - 1;
    }
}

/// <summary>Places the intro camera for each kind of shot. Fixed-point shots remember where they put the camera.</summary>
internal static class ShotCamera
{
    internal struct Frame
    {
        public Vector3 Position, LookAt, Up;
        public float Fov;
    }

    private static int _placedFor = -1;
    private static GlobalPosition _fixedPoint;
    private static float _flaresAt;
    private static int _flaresLeft;

    internal static void Reset()
    {
        _placedFor = -1;
        _flaresLeft = 0;
    }

    /// <summary>The camera for this frame, or false if there is nothing to film.</summary>
    internal static bool Place(IntroPlan plan, ActiveEncounter enc, float t, float slow, out Frame frame)
    {
        frame = default;
        var flight = enc.Members.Where(m => m.Alive && m.Aircraft != null && !m.Aircraft.disabled)
                               .OrderByDescending(m => m.Lead).Select(m => m.Aircraft).ToList();
        if (flight.Count == 0 || plan.List.Count == 0) return false;
        Aircraft lead = flight[0];

        int index = plan.ShotIndexAt(t);
        Shot shot = plan.List[index];
        float k = Mathf.Clamp01(Mathf.InverseLerp(shot.Start, shot.End, t));
        float ks = Smooth(k);
        bool fresh = _placedFor != index;
        _placedFor = index;

        Vector3 p = lead.transform.position;
        Vector3 vel = lead.rb != null ? lead.rb.velocity : lead.transform.forward * 200f;
        Vector3 fwd = vel.sqrMagnitude > 1f ? vel.normalized : lead.transform.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        right = right.sqrMagnitude > 1e-4f ? right.normalized : lead.transform.right;
        Vector3 up = Vector3.Cross(fwd, right).normalized;
        float size = Size(lead);
        float speed = vel.magnitude;
        float travel = speed * (shot.End - shot.Start) * slow; // metres the flight covers during this shot

        Vector3 centre = Vector3.zero;
        foreach (var a in flight) centre += a.transform.position;
        centre /= flight.Count;
        float span = size * 2.5f;
        foreach (var a in flight) span = Mathf.Max(span, Vector3.Distance(a.transform.position, centre) + size);

        Vector3 cam, look = p;
        Vector3 camUp = up;
        float fov;

        switch (shot.Kind)
        {
            case ShotKind.Flyby:
                if (fresh)
                {
                    float side = (Random.value < 0.5f ? -1f : 1f) * size * 1.6f;
                    _fixedPoint = (p + fwd * travel * 0.55f + right * side + up * size * 0.35f).ToGlobalPosition();
                }
                cam = _fixedPoint.ToLocalPosition();
                fov = FrameFov(Vector3.Distance(cam, p), size * 0.7f, 8f, 60f);
                break;

            case ShotKind.Orbit:
            {
                float angle = Mathf.Lerp(-150f, -80f, ks);
                cam = p + Quaternion.AngleAxis(angle, up) * fwd * size * 2.4f + up * size * 0.45f;
                fov = 42f;
                break;
            }

            case ShotKind.TailClose:
            {
                float scale = Mathf.Lerp(1.15f, 0.85f, ks);
                cam = p + (-fwd * size * 0.9f + up * size * 0.45f + right * size * 0.6f) * scale;
                look = p - fwd * size * 0.35f + up * size * 0.1f;
                fov = 38f;
                break;
            }

            case ShotKind.FormationRear:
            {
                // Back far enough for the whole formation, however big; the lens fits it.
                float back = Mathf.Max(size * 7f, span * 2.8f);
                cam = centre - fwd * back + up * Mathf.Max(size * 2.2f, span * 0.75f) - right * Mathf.Max(size * 1.5f, span * 0.5f);
                look = centre + fwd * span * 0.2f;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 1.05f, 18f, 50f);
                break;
            }

            case ShotKind.TailPan:
            {
                // Slide along the fins in the order they sit across the formation (left to right as the camera sees
                // them), on a smooth curve through them, so a finger-four or a box pans as cleanly as an echelon. At
                // most five fins: past that the pan would whip.
                Vector3 Tail(Aircraft a) => a.transform.position - fwd * Size(a) * 0.42f + up * Size(a) * 0.22f;
                var fins = flight.OrderBy(a => Vector3.Dot(a.transform.position - centre, right)).Select(Tail).ToList();
                if (fins.Count > 5)
                {
                    int leadIdx = flight.Take(1).Select(l => fins.IndexOf(Tail(l))).First();
                    int from = Mathf.Clamp(leadIdx - 2, 0, fins.Count - 5);
                    fins = fins.GetRange(from, 5);
                }
                Vector3 tail = fins.Count == 1 ? fins[0] : Spline(fins, ks);
                cam = tail - fwd * size * 0.75f - right * size * 0.55f + up * size * 0.02f;
                look = tail + fwd * size * 0.6f + right * size * 0.45f;
                fov = 48f;
                break;
            }

            case ShotKind.BelowWide:
                cam = centre - up * span * 0.9f + fwd * span * Mathf.Lerp(1.0f, 0.75f, ks) - right * span * 0.35f;
                look = centre + up * span * 0.05f;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 0.85f, 20f, 70f);
                break;

            case ShotKind.HeadOn:
                if (fresh)
                {
                    float d = travel + Mathf.Max(span * 4f, size * 18f);
                    _fixedPoint = (centre + fwd * d + right * span * 0.15f + up * span * 0.12f).ToGlobalPosition();
                }
                cam = _fixedPoint.ToLocalPosition();
                look = centre;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 0.9f, 3f, 50f);
                break;

            case ShotKind.BelowClose:
                cam = p - up * size * 1.1f + fwd * size * Mathf.Lerp(1.2f, -1.6f, ks) + right * size * 0.35f;
                fov = 55f;
                break;

            case ShotKind.Cockpit:
            {
                // Riding on the last wingman, just above and behind its canopy, looking at the rest of the flight.
                Aircraft w = flight[flight.Count - 1];
                float ws = Size(w);
                cam = w.transform.position + w.transform.up * ws * 0.2f + w.transform.forward * ws * 0.12f;
                Vector3 others = Vector3.zero;
                for (int i = 0; i < flight.Count - 1; i++) others += flight[i].transform.position;
                look = others / Mathf.Max(1, flight.Count - 1);
                camUp = w.transform.up;
                fov = 58f;
                break;
            }

            case ShotKind.RearLow:
                cam = centre - fwd * span * Mathf.Lerp(2.4f, 2.0f, ks) - up * span * 0.12f + right * span * 0.2f;
                look = centre + fwd * span * 0.5f;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 0.8f, 15f, 60f);
                break;

            case ShotKind.LeadApproach:
                if (fresh)
                    _fixedPoint = (p + fwd * (travel * 1.05f + size * 6f) - up * size * 1.6f + right * size * 0.8f).ToGlobalPosition();
                cam = _fixedPoint.ToLocalPosition();
                fov = FrameFov(Vector3.Distance(cam, p), size * 0.8f, 4f, 60f);
                break;

            case ShotKind.LeadFlares:
                if (fresh)
                {
                    _flaresLeft = 3;
                    _flaresAt = t;
                }
                if (_flaresLeft > 0 && t >= _flaresAt)
                {
                    _flaresLeft--;
                    _flaresAt = t + 0.45f;
                    PopFlares(lead);
                }
                cam = p + (fwd * size * 2.0f - up * size * 0.22f + right * size * 0.35f) * Mathf.Lerp(1.1f, 0.9f, ks);
                look = p + fwd * size * 0.15f;
                fov = 50f;
                break;

            case ShotKind.LeadSide:
                cam = p + right * size * 0.95f + up * size * 0.06f + fwd * size * Mathf.Lerp(0.65f, -0.55f, ks);
                look = p + fwd * size * Mathf.Lerp(0.35f, -0.3f, ks);
                fov = 46f;
                break;

            case ShotKind.NoseClose:
                cam = p + fwd * size * Mathf.Lerp(0.78f, 1.1f, ks) - up * size * 0.07f - right * size * 0.1f;
                look = p + fwd * size * 0.22f + up * size * 0.06f;
                fov = 55f;
                break;

            case ShotKind.SideSlide:
                cam = p + right * size * 0.62f + up * size * 0.12f + fwd * size * Mathf.Lerp(0.4f, 0.05f, ks);
                look = p + fwd * size * Mathf.Lerp(0.28f, 0f, ks) + up * size * 0.05f;
                fov = 50f;
                break;

            case ShotKind.FrontWide:
                cam = centre + fwd * span * Mathf.Lerp(2.1f, 1.9f, ks) + up * span * 0.4f - right * span * 0.45f;
                look = centre - fwd * span * 0.15f;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 0.85f, 15f, 60f);
                break;

            case ShotKind.Distant:
                if (fresh)
                {
                    float d = Mathf.Max(3500f, span * 20f);
                    Vector3 dir = (fwd * 0.55f + right * 0.83f).normalized;
                    _fixedPoint = (centre + dir * d - up * span * 0.3f).ToGlobalPosition();
                }
                cam = _fixedPoint.ToLocalPosition();
                look = centre;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 2.2f, 1.5f, 25f);
                break;

            case ShotKind.BeamPass:
                cam = p + right * size * 2.2f + up * size * 0.15f + fwd * size * Mathf.Lerp(1.2f, -1.2f, ks);
                look = p + fwd * size * Mathf.Lerp(0.3f, -0.3f, ks);
                fov = 38f;
                break;

            case ShotKind.ChaseLead:
            {
                // Riding behind and above the lead at its speed, easing in, the wingmen sliding about the frame.
                float back = Mathf.Lerp(2.3f, 1.7f, ks);
                cam = p - fwd * size * back + up * size * 0.42f + right * size * 0.2f;
                look = p + fwd * size * 3f + (centre - p) * 0.35f;
                fov = Mathf.Max(48f, FrameFov(Vector3.Distance(cam, centre), span * 0.6f, 40f, 65f));
                break;
            }

            case ShotKind.WingmanFocus:
            {
                // From just off a wingman's wingtip: its canopy first, then the lens swings onto the lead.
                Aircraft w = flight.Count > 1 ? flight[1] : lead;
                float ws = Size(w);
                Vector3 wf = w.transform.forward, wr = w.transform.right;
                float sideSign = Vector3.Dot(w.transform.position - p, right) >= 0f ? 1f : -1f;
                cam = w.transform.position + wr * ws * 0.62f * sideSign - wf * ws * 0.35f + Vector3.up * ws * 0.12f;
                Vector3 canopy = w.transform.position + wf * ws * 0.25f + w.transform.up * ws * 0.08f;
                look = Vector3.Lerp(canopy, p + fwd * size * 0.1f, Smooth(Mathf.InverseLerp(0.25f, 0.8f, k)));
                camUp = Vector3.Slerp(w.transform.up, Vector3.up, 0.5f);
                fov = Mathf.Lerp(55f, 32f, Smooth(Mathf.InverseLerp(0.25f, 0.9f, k)));
                break;
            }

            case ShotKind.TopDown:
            {
                // Straight down on the formation, turning slowly, high enough to hold all of it.
                cam = centre + Vector3.up * Mathf.Max(size * 6f, span * 2.6f) + fwd * span * 0.15f;
                look = centre;
                camUp = Quaternion.AngleAxis(Mathf.Lerp(-18f, 18f, ks), Vector3.up) * fwd;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 1.1f, 20f, 65f);
                break;
            }

            case ShotKind.Silhouette:
            {
                // The flight between the camera and the sun, low lens, a slight drift.
                Vector3 toSun = SunDirection();
                if (fresh)
                {
                    Vector3 away = -toSun; // camera on the far side of the flight from the sun
                    away.y = Mathf.Min(away.y, -0.08f); // a little below, looking up into it
                    _fixedPoint = (centre + away.normalized * Mathf.Max(span * 5f, size * 22f) + fwd * travel * 0.5f).ToGlobalPosition();
                }
                cam = _fixedPoint.ToLocalPosition();
                look = centre;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 1.2f, 6f, 45f);
                break;
            }

            case ShotKind.PushIn:
            {
                // A long, slow push from off the lead's front quarter onto its canopy.
                float far = Mathf.Lerp(9f, 1.15f, ks);
                cam = p + (fwd * 0.55f + right * 0.8f + up * 0.18f).normalized * size * far;
                look = p + fwd * size * 0.25f + up * size * 0.08f;
                fov = Mathf.Lerp(28f, 50f, ks);
                break;
            }

            default: // BeamWide
                cam = centre + right * span * 2.2f + fwd * span * Mathf.Lerp(0.7f, 0.4f, ks) + up * span * 0.2f;
                look = centre;
                fov = FrameFov(Vector3.Distance(cam, centre), span * 0.9f, 12f, 55f);
                break;
        }

        // Never put the camera underground.
        float groundY = lead.transform.position.y - Mathf.Max(0f, lead.radarAlt);
        if (cam.y < groundY + 40f) cam.y = groundY + 40f;

        frame = new Frame { Position = cam, LookAt = look, Up = camUp, Fov = fov };
        return true;
    }

    /// <summary>Catmull-Rom through the points, t from 0 (first) to 1 (last): smooth speed and direction through each point.</summary>
    private static Vector3 Spline(List<Vector3> pts, float t)
    {
        int n = pts.Count;
        float s = Mathf.Clamp01(t) * (n - 1);
        int i = Mathf.Min(Mathf.FloorToInt(s), n - 2);
        float u = s - i;
        Vector3 p0 = pts[Mathf.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Mathf.Min(i + 2, n - 1)];
        return 0.5f * (2f * p1 + (p2 - p0) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u + (3f * p1 - p0 - 3f * p2 + p3) * u * u * u);
    }

    /// <summary>Direction towards the sun (the scene's main light), or a high westerly sky if there is none.</summary>
    private static Vector3 SunDirection()
    {
        var sun = RenderSettings.sun;
        if (sun != null) return -sun.transform.forward;
        foreach (var l in Object.FindObjectsOfType<Light>())
            if (l.type == LightType.Directional) return -l.transform.forward;
        return new Vector3(-0.6f, 0.5f, 0.2f).normalized;
    }

    internal static float Size(Aircraft a) => Mathf.Max(a.definition.length, a.definition.width, 10f);

    private static float FrameFov(float distance, float radius, float min, float max) =>
        Mathf.Clamp(2f * Mathf.Atan(radius / Mathf.Max(distance, 1f)) * Mathf.Rad2Deg, min, max);

    private static float Smooth(float x) => x * x * (3f - 2f * x);

    private static void PopFlares(Aircraft a)
    {
        try { a.countermeasureManager?.PopFlares(); }
        catch { /* cosmetic only */ }
    }
}
