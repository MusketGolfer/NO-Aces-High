using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Airframes tearing themselves apart. Two causes, two guards:
///
/// 1. Physics mode switches. The game simulates aircraft within 10 km of the camera as jointed parts and the rest as
///    one body, switching as the camera moves. The intro's camera cut moves it tens of kilometres at once, so whole
///    groups switch in a moment (the player's own jet as the camera leaves and comes back, the aces as it arrives),
///    and a switch made mid-manoeuvre can snap a joint. While the intro camera is cut away, and for 3 s after it
///    returns, no aircraft changes physics mode.
/// 2. Pulls past what the joints take. The aces' control law (PostStall.ApplyRates) caps the commanded pitch rate at
///    the one that makes a safe g at the present speed (see SafeG), whenever the wing is flying (AoA under 25 deg).
///
/// Every part an ace or the local player loses is logged with g, speed, joint stress and physics mode.
/// </summary>
internal static class Breakups
{
    private static float _freezeUntil;

    internal static void Apply(Harmony harmony)
    {
        Patches.Patch(harmony, "breakups: no physics switch around the intro cut",
            AccessTools.Method(typeof(Aircraft), "CheckPhysicsLod"), prefix: nameof(CheckPhysicsLodPrefix), owner: typeof(Breakups));
        Patches.Patch(harmony, "breakups: log lost parts",
            AccessTools.Method(typeof(UnitPart), nameof(UnitPart.Detach)), prefix: nameof(DetachPrefix), owner: typeof(Breakups));
    }

    /// <summary>Called every frame: holds the freeze while the camera is away and 3 s after it is back.</summary>
    internal static void Tick()
    {
        if (IntroDirector.Playing && IntroDirector.CameraCut) _freezeUntil = Time.unscaledTime + 3f;
    }

    private static bool CheckPhysicsLodPrefix(Aircraft __instance)
    {
        if (FlightTest.IsSubject(__instance)) return true; // the survey handles its own jet
        return Time.unscaledTime >= _freezeUntil;
    }

    private static readonly Dictionary<Aircraft, float> LastLog = new();

    private static void DetachPrefix(UnitPart __instance)
    {
        try
        {
            var a = __instance.parentUnit as Aircraft;
            if (a == null || a.disabled) return;
            bool ace = Encounters.IsAce(a) || Encounters.IsRetreating(a);
            bool player = a == Session.LocalAircraft;
            if (!ace && !player) return;
            if (LastLog.TryGetValue(a, out var t) && Time.time - t < 0.5f) return; // one line per breakup, not per part
            LastLog[a] = Time.time;
            Plugin.Log.LogInfo($"Breakup: {(player ? "player" : "ace")} {a.definition?.unitName} lost {__instance.name} at {a.gForce:0.0} g, " +
                               $"{a.speed * 3.6f:0} km/h, joint stress {AceFlight.StressOf(a):P0}, {(a.simplePhysics ? "single-body" : "jointed")} physics, " +
                               $"hp {__instance.hitPoints:0}{(IntroDirector.Playing ? ", during the intro" : "")}{(Time.unscaledTime < _freezeUntil ? " (physics switches frozen)" : "")}, " +
                               $"flying {PostStall.Describe(a)}.");
        }
        catch { /* logging only */ }
    }
}
