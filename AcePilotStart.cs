using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Makes sure every ace has an AI at the stick. The game starts an aircraft's AI pilot in Pilot.SetStartingAiState
/// (from the aircraft's onInitialize event), and the AI flies through the aircraft's Autopilot component. Add-on
/// aircraft built only for players can lack that component: the PW-Mk1 has none, so building its AI throws, the jet is
/// left with no pilot state, and it flies straight on full power without ever targeting, firing or manoeuvring.
///
/// For an ace still without a pilot state a second after it appears, on the host and not flown by a player:
/// - no autopilot: one is added, a copy of a stock fighter's plane autopilot (its PID gains and AoA limiter), set up
///   for this jet. The donor is <see cref="DonorKey"/> (the FS-12 Revoker), else any stock jet with one;
/// - a VTOL-type pilot is switched to the Plane type (the game has no AI for VTOL);
/// - the AI is started. One attempt per aircraft, logged with the result.
/// </summary>
internal static class AcePilotStart
{
    private const string DonorKey = "Fighter1";

    private static float _next;
    private static readonly System.Reflection.MethodInfo StartAi = AccessTools.Method(typeof(Pilot), "SetStartingAiState");
    private static readonly Dictionary<Aircraft, float> FirstSeen = new();
    private static readonly HashSet<Aircraft> Tried = new();

    internal static void Tick()
    {
        if (Time.time < _next) return;
        _next = Time.time + 0.5f;
        foreach (var dead in FirstSeen.Keys.Where(k => k == null).ToList()) FirstSeen.Remove(dead);
        Tried.RemoveWhere(k => k == null);
        foreach (var a in Encounters.AllAces.ToList())
        {
            if (a == null || a.disabled || !a.IsServer || a.Player != null || Tried.Contains(a)) continue;
            var pilot = a.pilots != null && a.pilots.Length > 0 ? a.pilots[0] : null;
            if (pilot == null || pilot.currentState != null || pilot.dead || a.NetworkHQ == null)
            {
                FirstSeen.Remove(a);
                continue;
            }
            if (!FirstSeen.TryGetValue(a, out float since)) { FirstSeen[a] = Time.time; continue; }
            if (Time.time - since < 1f) continue;
            Tried.Add(a);
            var type = pilot.pilotType;
            string setup = $"pilot type {type}, autopilot {(a.autopilot != null ? a.autopilot.GetType().Name : "none")}";
            try
            {
                string added = a.autopilot == null ? GiveAutopilot(a) : null;
                if (type is not (Pilot.PilotType.Plane or Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing)) pilot.pilotType = Pilot.PilotType.Plane;
                StartAi.Invoke(pilot, null);
                Plugin.Log.LogInfo($"{a.definition?.unitName} ace had no AI pilot ({setup}){(added != null ? $"; gave it {added}" : "")}; " +
                                   $"AI started: {(pilot.currentState != null ? pilot.currentState.GetType().Name : "still none")}.");
            }
            catch (Exception ex)
            {
                var e = ex.InnerException ?? ex;
                Plugin.Log.LogWarning($"{a.definition?.unitName} ace has no AI pilot ({setup}) and starting one failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    /// <summary>Adds a copy of a stock fighter's plane autopilot to <paramref name="a"/>. Returns what was copied.</summary>
    private static string GiveAutopilot(Aircraft a)
    {
        AutopilotPlane donor = null;
        string donorName = null;
        if (Encyclopedia.Lookup != null && Encyclopedia.Lookup.TryGetValue(DonorKey, out var d) && d?.unitPrefab != null)
        {
            donor = d.unitPrefab.GetComponentInChildren<AutopilotPlane>(true);
            donorName = d.unitName;
        }
        if (donor == null && Encyclopedia.i != null)
            foreach (var def in Encyclopedia.i.aircraft)
            {
                donor = def?.unitPrefab != null ? def.unitPrefab.GetComponentInChildren<AutopilotPlane>(true) : null;
                if (donor != null) { donorName = def.unitName; break; }
            }
        if (donor == null) throw new InvalidOperationException("no stock jet with a plane autopilot to copy");

        // Built inactive so its Awake waits until the settings and the aircraft are in place.
        var holder = new GameObject("NOAcesHigh autopilot");
        holder.SetActive(false);
        holder.transform.SetParent(a.transform, false);
        var ap = holder.AddComponent<AutopilotPlane>();
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(donor), ap);
        ap.aircraft = a;
        holder.SetActive(true); // Awake: registers itself as a.autopilot and sets up its controllers
        return $"a copy of the {donorName}'s autopilot";
    }
}
