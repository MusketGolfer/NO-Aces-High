using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// The energy picture between an ace and one opponent, from the airframe profiles (measured or estimated for any jet):
/// - energy height, h + v^2/2g, for each, and the difference;
/// - turn: sustained turn rates (who wins a level turning fight);
/// - climb: thrust-to-weight (who wins in the vertical).
///
/// Used to choose how to fight (PostStall): commit to turning only against a jet that does not out-turn us; extend
/// and come back rather than turn with one that does; take the fight vertical (the energy trap) when we out-climb
/// them; and against an attacker on the tail, pick the escape that suits: a break and jinks if we out-turn them,
/// the vertical if we out-climb them, a diving extension if they beat us at both.
/// </summary>
internal readonly struct EnergyPicture
{
    public readonly float EnergyLead;   // m of energy height we have over them (negative: they have more)
    public readonly float TurnRatio;    // our sustained turn / theirs
    public readonly float ClimbRatio;   // our thrust-to-weight / theirs
    public readonly float OurTW;

    public EnergyPicture(float energyLead, float turnRatio, float climbRatio, float ourTW)
    {
        EnergyLead = energyLead;
        TurnRatio = turnRatio;
        ClimbRatio = climbRatio;
        OurTW = ourTW;
    }

    /// <summary>We win a level turning fight.</summary>
    public bool OutTurns => TurnRatio > 1.08f;
    /// <summary>They win a level turning fight.</summary>
    public bool OutTurned => TurnRatio < 0.92f;
    /// <summary>We win in the vertical: clearly more thrust for our weight, and enough to climb hard.</summary>
    public bool OutClimbs => ClimbRatio > 1.25f && OurTW > 0.85f;
    /// <summary>They beat us turning and climbing, with no energy in hand: do not fight on their terms.</summary>
    public bool Outmatched => OutTurned && ClimbRatio < 1.05f && EnergyLead < 500f;

    public override string ToString() =>
        $"energy {(EnergyLead >= 0 ? "+" : "")}{EnergyLead:0} m, turn x{TurnRatio:0.00}, climb x{ClimbRatio:0.00}" +
        (OutTurns ? ", out-turns them" : OutTurned ? ", out-turned" : "") + (OutClimbs ? ", out-climbs them" : "") + (Outmatched ? ", outmatched" : "");
}

internal static class EnergyFight
{
    internal static float EnergyHeight(Aircraft a) =>
        a == null ? 0f : a.GlobalPosition().y + a.speed * a.speed / (2f * 9.81f);

    internal static EnergyPicture Compare(Aircraft us, Aircraft them)
    {
        var p = AirframeProfiles.For(us);
        var q = AirframeProfiles.For(them);
        float ourTurn = p?.SustainedTurn ?? 14f, theirTurn = q?.SustainedTurn ?? 14f;
        float ourTW = p?.ThrustToWeight ?? 0.8f, theirTW = q?.ThrustToWeight ?? 0.8f;
        return new EnergyPicture(
            EnergyHeight(us) - EnergyHeight(them),
            ourTurn / Mathf.Max(theirTurn, 1f),
            ourTW / Mathf.Max(theirTW, 0.05f),
            ourTW);
    }
}
