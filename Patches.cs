using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NOAcesHigh;

internal static class Patches
{
    /// <summary>Applies each patch on its own so a game update that breaks one leaves the rest working.</summary>
    internal static void Apply(Harmony harmony)
    {
        Patch(harmony, "threat from kills",
            AccessTools.Method(typeof(FactionHQ), nameof(FactionHQ.ReportKillAction)),
            postfix: nameof(ReportKillActionPostfix));
        Patch(harmony, "threat from captured bases",
            AccessTools.Method(typeof(Airbase), "CaptureFaction"),
            prefix: nameof(CaptureFactionPrefix), postfix: nameof(CaptureFactionPostfix));
        // MissionManager.OnDestroy sets onMissionLoad to null, so subscribing to the event does not survive a
        // return to the menu; patch the method that raises it instead.
        Patch(harmony, "reset on mission load",
            AccessTools.Method(typeof(MissionManager), nameof(MissionManager.SetMission)),
            postfix: nameof(SetMissionPostfix));
        // Egress replaces the attack half of the combat state; RunEvadeMode (missile defence) still runs after it.
        Patch(harmony, "ace egress and formation steering",
            AccessTools.Method(typeof(AIPilotCombatModes), "RunAttackMode"),
            prefix: nameof(RunAttackModePrefix), postfix: nameof(RunAttackModePostfix));
        Patch(harmony, "formation: no dive on a radar lock",
            AccessTools.Method(typeof(AIPilotCombatModes), "RunEvadeMode"),
            prefix: nameof(RunEvadeModePrefix));
        foreach (var name in new[] { "ChooseCombatMode", "AssessHQTargets", "ManageTarget" })
            Patch(harmony, "ace egress: no " + name,
                AccessTools.Method(typeof(AIPilotCombatModes), name),
                prefix: nameof(SkipWhileEgressingPrefix));
        AceFlight.Apply(harmony);
        NetSync.Apply(harmony);
        PostStall.Apply(harmony);
        FlightTest.Apply(harmony);
        FlightTest.ApplyPilotPatch(harmony);
        FlightTest.ApplyPhysicsPatch(harmony);
        LiveryForge.ApplyPatch(harmony);
        Breakups.Apply(harmony);
        IntroGate.Apply(harmony);
        HudLayoutBridge.Apply(harmony);
        Chatter.ApplyUiPlusPatch(harmony);
        Patch(harmony, "aces prefer the player",
            AccessTools.Method(typeof(CombatAI), nameof(CombatAI.ChooseHQTarget)),
            postfix: nameof(ChooseHQTargetPostfix));
    }

    /// <summary>Features whose patch could not be applied (shown at the top of the settings).</summary>
    internal static readonly System.Collections.Generic.List<string> Failed = new();

    internal static void Patch(Harmony harmony, string what, MethodBase target, string prefix = null, string postfix = null, Type owner = null)
    {
        owner ??= typeof(Patches);
        if (target == null)
        {
            Plugin.Log.LogError($"Patch target for '{what}' not found; that feature is off. The game may have updated.");
            Failed.Add(what);
            return;
        }
        try
        {
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(owner, prefix),
                postfix: postfix == null ? null : new HarmonyMethod(owner, postfix));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Patching '{what}' failed; that feature is off. {ex.Message}");
            Failed.Add(what);
        }
    }

    private static void SetMissionPostfix()
    {
        try
        {
            Encounters.ResetForMission();
            NetSync.Reset();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Mission reset failed: " + ex.Message);
        }
    }

    // ---- Threat meter -------------------------------------------------------------------------

    private static void ReportKillActionPostfix(Player player, Unit target, float factor)
    {
        try
        {
            // Human kills only (AI kills report no player); each side's pilots raise their own side's meter.
            if (target == null || target is Missile || player == null || player.HQ == null) return;
            if (target.NetworkHQ == player.HQ) return; // friendly fire is not a threat to the enemy
            Encounters.AddThreat(player.HQ, player, ThreatValue(target) * factor,
                target.definition != null ? target.definition.unitName : target.name);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Threat from kill failed: " + ex.Message);
        }
    }

    /// <summary>Fighter 10, other fixed-wing (bomber, AWACS, transport) 20, helicopter 6, ship 30, vehicle 2, building 1.</summary>
    internal static float ThreatValue(Unit target)
    {
        switch (target)
        {
            case Aircraft aircraft:
                if (aircraft.GetControlsFilter() is HeloControlsFilter) return 6f;
                return aircraft.definition.roleIdentity.antiAir >= 0.2f ? 10f : 20f;
            case Ship:
                return 30f;
            case Building:
                return 1f;
            default:
                return 2f;
        }
    }

    private static void CaptureFactionPrefix(Airbase __instance, out FactionHQ __state)
    {
        __state = __instance.CurrentHQ;
    }

    private static void CaptureFactionPostfix(Airbase __instance, FactionHQ __state)
    {
        try
        {
            // The side that took the base becomes more of a threat to the side that lost it.
            FactionHQ before = __state, after = __instance.CurrentHQ;
            if (before == null || after == null || before == after) return;
            Encounters.AddThreat(after, null, Plugin.BaseLossBonus.Value, $"{Factions.Short(before)} lost airbase {__instance.name}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Threat from base capture failed: " + ex.Message);
        }
    }

    // ---- Egress -------------------------------------------------------------------------------

    private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> StateAircraft =
        AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
    private static readonly AccessTools.FieldRef<PilotBaseState, GlobalPosition> StateDestination =
        AccessTools.FieldRefAccess<PilotBaseState, GlobalPosition>("destination");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, Unit> CurrentTarget =
        AccessTools.FieldRefAccess<AIPilotCombatModes, Unit>("currentTarget");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, bool> AimVelocity =
        AccessTools.FieldRefAccess<AIPilotCombatModes, bool>("aimVelocity");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, bool> FollowTerrain =
        AccessTools.FieldRefAccess<AIPilotCombatModes, bool>("followTerrain");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, bool> IgnoreCollision =
        AccessTools.FieldRefAccess<AIPilotCombatModes, bool>("ignoreCollision");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, float> AimEffort =
        AccessTools.FieldRefAccess<AIPilotCombatModes, float>("aimEffort");
    private static readonly AccessTools.FieldRef<AIPilotCombatModes, Vector3> AimTargetVel =
        AccessTools.FieldRefAccess<AIPilotCombatModes, Vector3>("aimTargetVel");

    /// <summary>
    /// An egressing ace drops its target and all attack logic and runs for the map edge at full throttle.
    /// Skipping only this leaves RunEvadeMode free to bend that course to defeat missiles and pop countermeasures.
    /// </summary>
    /// A wingman holding formation on the way in does the same with its slot as the destination, keeping its target.
    /// Destinations carry the slot's altitude, so terrain following (which holds a radar height instead) is off;
    /// the terrain warning still pulls up.
    /// </summary>
    private static bool RunAttackModePrefix(AIPilotCombatModes __instance)
    {
        try
        {
            Aircraft aircraft = StateAircraft(__instance);
            bool egress = Encounters.TryGetEgressDestination(aircraft, out var destination, out float throttle);
            if (!egress && !Encounters.TryGetFormationDestination(aircraft, out destination, out throttle)) return true;
            if (egress) CurrentTarget(__instance) = null;
            StateDestination(__instance) = destination;
            AimVelocity(__instance) = false;
            FollowTerrain(__instance) = false;
            IgnoreCollision(__instance) = false;
            AimEffort(__instance) = egress ? 0.5f : 0.6f;
            AimTargetVel(__instance) = Vector3.zero;
            aircraft.GetInputs().throttle = throttle;
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace formation steering failed: " + ex.Message);
            return true;
        }
    }

    private static readonly AccessTools.FieldRef<AIPilotCombatModes, List<Missile>> MissileAlerts =
        AccessTools.FieldRefAccess<AIPilotCombatModes, List<Missile>>("missileAlerts");

    /// <summary>
    /// A flight holding formation does not dive for the deck when it is only locked: the stock radar evasion drops
    /// the target height to 10 m on a lock alone. With a missile actually in the air the formation breaks anyway
    /// and the evasion runs as normal.
    /// </summary>
    private static bool RunEvadeModePrefix(AIPilotCombatModes __instance, ref GlobalPosition evadeDestination)
    {
        try
        {
            Aircraft aircraft = StateAircraft(__instance);
            // Lock-only evasion is skipped in formation, and for any ace while the intro runs (holding fire).
            if (!(Encounters.InFormationSlot(aircraft) || Encounters.IsFormationLead(aircraft) || (IntroGate.Holding && Encounters.IsAce(aircraft)))) return true;
            var alerts = MissileAlerts(__instance);
            if (alerts != null && alerts.Count > 0) return true;
            evadeDestination = StateDestination(__instance);
            return false;
        }
        catch { return true; }
    }

    /// <summary>While the flight holds formation its lead flies no faster than cruise, so the wingmen can keep station.</summary>
    private static void RunAttackModePostfix(AIPilotCombatModes __instance)
    {
        try
        {
            Aircraft aircraft = StateAircraft(__instance);
            if (!Encounters.IsFormationLead(aircraft)) return;
            var inputs = aircraft.GetInputs();
            float cruise = aircraft.GetAircraftParameters()?.cruiseThrottle ?? 0.85f;
            if (inputs.throttle > cruise) inputs.throttle = cruise;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace formation lead throttle failed: " + ex.Message);
        }
    }

    /// <summary>No target picks or attack-mode changes while egressing (those can fire weapons).</summary>
    private static bool SkipWhileEgressingPrefix(AIPilotCombatModes __instance)
    {
        try
        {
            return !Encounters.IsEgressing(StateAircraft(__instance));
        }
        catch
        {
            return true;
        }
    }

    // ---- Targeting ----------------------------------------------------------------------------

    /// <summary>Aces go for their target pilot whenever their HQ has a good track on them and a weapon can use it.</summary>
    private static void ChooseHQTargetPostfix(Unit searcher, List<WeaponStation> stationList, ref CombatAI.TargetSearchResults __result)
    {
        try
        {
            if (!Encounters.IsAce(searcher)) return;
            Aircraft player = Encounters.ActiveTarget;
            if (player == null || player.disabled) return;
            if (__result.target == player) return;

            FactionHQ hq = searcher.NetworkHQ;
            if (hq == null || !hq.trackingDatabase.TryGetValue(player.persistentID, out var tracking)) return;
            if (!hq.IsTargetPositionAccurate(player, 1000f)) return;

            float range = FastMath.Distance(tracking.GetPosition(), searcher.GlobalPosition());
            WeaponStation best = null;
            float bestOpportunity = 0f;
            foreach (var station in stationList)
            {
                if (station.Ammo <= 0 || station.Cargo) continue;
                var ot = CombatAI.AnalyzeTarget(station, searcher, tracking, 0f, range, 100f);
                if (ot.opportunity > bestOpportunity)
                {
                    bestOpportunity = ot.opportunity;
                    best = station;
                }
            }
            if (best != null)
                __result = new CombatAI.TargetSearchResults(player, best, bestOpportunity, __result.outOfAmmo);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Ace targeting failed: " + ex.Message);
        }
    }
}
