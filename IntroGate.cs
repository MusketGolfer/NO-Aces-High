using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Weapons tight until the introduction is over. From the moment an encounter's intro is queued (its music, the wait
/// for a safe moment, the camera shots, the title card) until it ends, an ace's weapon manager will not release
/// anything: no missiles, no guns, no counter-missile shots, no jamming of the target. They still fly, turn, flare
/// and dodge, so a player who opens fire early is not shooting sitting ducks, but nothing is off the rails before the
/// camera has shown who is coming.
/// </summary>
internal static class IntroGate
{
    private static bool _logged;

    /// <summary>True while aces must hold fire.</summary>
    internal static bool Holding => Plugin.IntroHoldFire.Value && IntroDirector.Active;

    internal static void Apply(Harmony harmony)
    {
        Patches.Patch(harmony, "intro gate: aces hold fire (weapons)",
            AccessTools.Method(typeof(WeaponManager), nameof(WeaponManager.Fire)), prefix: nameof(FirePrefix), owner: typeof(IntroGate));
        Patches.Patch(harmony, "intro gate: aces hold fire (guns)",
            AccessTools.Method(typeof(WeaponManager), nameof(WeaponManager.FireGuns)), prefix: nameof(FirePrefix), owner: typeof(IntroGate));
    }

    internal static void Reset() => _logged = false;

    private static bool FirePrefix(Aircraft ___aircraft)
    {
        if (!Holding) return true;
        try
        {
            var a = ___aircraft;
            if (a == null || !(Encounters.IsAce(a) || Encounters.IsRetreating(a))) return true;
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"Intro gate: {a.definition?.unitName} held fire, the introduction is still running.");
            }
            return false;
        }
        catch { return true; }
    }
}
