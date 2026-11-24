using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NOAcesHigh;

internal enum FormationKind
{
    Random,
    EchelonRight,
    EchelonLeft,
    Vic,
    FingerFour,
    LineAbreast,
    Trail,
    Diamond,
    Box,
}

/// <summary>
/// Slot geometry for ace flights and the station-keeping that holds them in it. Slot 0 is the leader; offsets are
/// (right, up, back) in metres relative to the leader's flat track.
/// </summary>
internal static class Formations
{
    /// <summary>Slot spacing (FormationSpacing setting).</summary>
    internal static float Spacing => Plugin.FormationSpacing?.Value ?? 70f;
    private const float StepDown = 25f;          // stacked slots sit this far below to stay out of wake
    private const float InterceptBeyond = 1500f; // further than this from the slot: cut across at full throttle

    internal static Vector3 Offset(FormationKind kind, int slot)
    {
        if (slot <= 0) return Vector3.zero;
        float s = Spacing;
        switch (kind)
        {
            case FormationKind.EchelonLeft:
                return new Vector3(-s * slot, 0f, 0.6f * s * slot);
            case FormationKind.Vic:
            {
                // #2 left, #3 right, #4 further left...
                int rank = (slot + 1) / 2;
                float side = slot % 2 == 1 ? -1f : 1f;
                return new Vector3(side * s * rank, 0f, 0.7f * s * rank);
            }
            case FormationKind.FingerFour:
                // Lead, #2 off the left, the element lead #3 off the right and #4 outside it.
                return slot switch
                {
                    1 => new Vector3(-s, 0f, 0.6f * s),
                    2 => new Vector3(1.2f * s, 0f, 0.6f * s),
                    _ => new Vector3((1.2f + slot - 2) * s, 0f, 0.6f * s * (slot - 1)),
                };
            case FormationKind.LineAbreast:
            {
                int rank = (slot + 1) / 2;
                float side = slot % 2 == 1 ? 1f : -1f;
                return new Vector3(side * 1.4f * s * rank, 0f, 0f);
            }
            case FormationKind.Trail:
                return new Vector3(0f, -StepDown * slot, 1.3f * s * slot);
            case FormationKind.Diamond:
                return slot switch
                {
                    1 => new Vector3(-s, 0f, 0.7f * s),
                    2 => new Vector3(s, 0f, 0.7f * s),
                    3 => new Vector3(0f, -StepDown, 1.4f * s),
                    _ => new Vector3(s * (slot - 2), 0f, 0.7f * s * (slot - 1)),
                };
            case FormationKind.Box:
                return slot switch
                {
                    1 => new Vector3(1.3f * s, 0f, 0f),
                    2 => new Vector3(0f, -StepDown, 1.3f * s),
                    3 => new Vector3(1.3f * s, -StepDown, 1.3f * s),
                    _ => new Vector3(0.65f * s, -2f * StepDown, 1.3f * s * (slot - 2)),
                };
            default:
                return new Vector3(s * slot, 0f, 0.6f * s * slot);
        }
    }

    /// <summary>A formation that suits the flight's size.</summary>
    internal static FormationKind PickFor(int count)
    {
        FormationKind[] pool = count switch
        {
            <= 2 => new[] { FormationKind.EchelonRight, FormationKind.EchelonLeft, FormationKind.LineAbreast, FormationKind.Trail },
            3 => new[] { FormationKind.Vic, FormationKind.EchelonRight, FormationKind.EchelonLeft, FormationKind.Trail },
            _ => new[] { FormationKind.FingerFour, FormationKind.Diamond, FormationKind.Box, FormationKind.Vic, FormationKind.EchelonRight },
        };
        return pool[Random.Range(0, pool.Length)];
    }

    /// <summary>squadron.json value, else the config setting, else one picked for the flight size.</summary>
    internal static FormationKind Resolve(string fromJson, int count)
    {
        if (Variants.IsRandom(fromJson)) return PickFor(count);
        if (Variants.TryParse(fromJson, out FormationKind k) && k != FormationKind.Random) return k;
        k = Plugin.Formation.Value;
        return k == FormationKind.Random ? PickFor(count) : k;
    }

    internal static Vector3 FlatForward(Aircraft a, Vector3 fallback)
    {
        Vector3 v = a != null && a.rb != null ? a.rb.velocity : Vector3.zero;
        v.y = 0f;
        if (v.sqrMagnitude > 100f) return v.normalized;
        if (a != null)
        {
            Vector3 f = a.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 1e-4f) return f.normalized;
        }
        return fallback;
    }

    internal static GlobalPosition SlotPosition(GlobalPosition leaderPos, Vector3 fwd, Vector3 offset)
    {
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        return leaderPos + right * offset.x + Vector3.up * offset.y - fwd * offset.z;
    }

    /// <summary>
    /// Where a wingman should aim to hold its slot off the leader, and at what throttle. Far out it cuts across to
    /// where the slot will be; close in it flies down the leader's track and trims throttle on the along-track error.
    /// </summary>
    internal static void Steer(Aircraft self, Aircraft leader, Vector3 offset, Vector3 fallbackDir, float leaderThrottle,
        out GlobalPosition destination, out float throttle, out float slotError)
    {
        Vector3 fwd = FlatForward(leader, fallbackDir);
        GlobalPosition leaderPos = leader.GlobalPosition();
        GlobalPosition slotPos = SlotPosition(leaderPos, fwd, offset);
        Vector3 toSlot = slotPos - self.GlobalPosition();
        slotError = toSlot.magnitude;

        if (slotError > InterceptBeyond)
        {
            Vector3 leaderVel = leader.rb != null ? leader.rb.velocity : fwd * 200f;
            destination = slotPos + leaderVel * 4f;
            throttle = 1f;
        }
        else
        {
            destination = slotPos + fwd * 1000f;
            float along = Vector3.Dot(toSlot, fwd); // + = slot is ahead of us
            throttle = Mathf.Clamp(leaderThrottle + along * 0.002f, 0.15f, 1f);
        }
        destination.y = slotPos.y; // hold the slot's altitude, not the terrain-following height
    }

    internal static string Label(FormationKind k) => k switch
    {
        FormationKind.EchelonRight => "echelon right",
        FormationKind.EchelonLeft => "echelon left",
        FormationKind.Vic => "vic",
        FormationKind.FingerFour => "finger-four",
        FormationKind.LineAbreast => "line abreast",
        FormationKind.Trail => "trail",
        FormationKind.Diamond => "diamond",
        FormationKind.Box => "box",
        _ => "random",
    };
}

/// <summary>Parses the variant names used in squadron.json ("pw-boss", "finger_four", "HeadOn"...) into enums.</summary>
internal static class Variants
{
    internal static bool TryParse<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string squashed = text.Replace("-", "").Replace("_", "").Replace(" ", "");
        return Enum.TryParse(squashed, ignoreCase: true, out value) && Enum.IsDefined(typeof(T), value);
    }

    /// <summary>The entries of a comma-separated list ("headon, lead, distant"), trimmed, empty ones dropped.</summary>
    internal static string[] Entries(string text) =>
        string.IsNullOrWhiteSpace(text) ? Array.Empty<string>()
            : System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(System.Linq.Enumerable.Select(text.Split(','), e => e.Trim()), e => e.Length > 0));

    /// <summary>One entry of a comma-separated list, picked at random (a single value comes back as it is).</summary>
    internal static string Pick(string text)
    {
        var all = Entries(text);
        return all.Length == 0 ? text : all[UnityEngine.Random.Range(0, all.Length)];
    }

    /// <summary>The squadron.json value "random": pick afresh among every choice, whatever the config says.</summary>
    internal static bool IsRandom(string text) => text != null && text.Trim().Equals("random", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any value of the enum except the "random" and "match" placeholders.</summary>
    internal static T Any<T>() where T : struct, Enum
    {
        var all = new System.Collections.Generic.List<T>();
        foreach (T v in Enum.GetValues(typeof(T)))
        {
            string n = v.ToString();
            if (n != "Random" && n != "Match") all.Add(v);
        }
        return all[UnityEngine.Random.Range(0, all.Count)];
    }
}
