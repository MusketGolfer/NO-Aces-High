using System;
using System.Linq;
using NuclearOption.Networking;

namespace NOAcesHigh;

/// <summary>
/// Where the mod is allowed to run encounters and which rules apply.
/// - Single player: everything, including the camera cut and slow motion.
/// - Multiplayer: only on the server (host or dedicated), which spawns and flies the aces. Clients need nothing:
///   radio lines reach them as the game's own side-filtered mission messages. Intros are a title card on the host's
///   screen, with no slow motion (the game cannot be slowed for everyone); the camera cut is an experimental option.
/// - SimulateMultiplayer (debug) runs single player under the multiplayer rules.
/// </summary>
internal static class Session
{
    internal static bool InMultiplayer => GameManager.gameState == GameState.Multiplayer;

    /// <summary>Multiplayer rules for intros and radio lines: a real multiplayer game, or simulated in single player.</summary>
    internal static bool MultiplayerRules =>
        InMultiplayer || (GameManager.gameState == GameState.SinglePlayer && Plugin.SimulateMultiplayer.Value);

    internal static bool IsServer
    {
        get
        {
            try { return NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server != null && NetworkManagerNuclearOption.i.Server.Active; }
            catch { return false; }
        }
    }

    /// <summary>This game decides encounters: single player, or the server of a multiplayer game with it enabled.</summary>
    internal static bool Authoritative =>
        GameManager.gameState == GameState.SinglePlayer ||
        (InMultiplayer && Plugin.Multiplayer.Value && IsServer);

    /// <summary>In a running mission with at least two sides.</summary>
    internal static bool InMission
    {
        get
        {
            if (!Authoritative) return false;
            try { return FactionRegistry.GetAllHQs().Count(h => h != null) >= 2; }
            catch { return false; }
        }
    }

    internal static FactionHQ LocalHQ => GameManager.GetLocalHQ(out var hq) ? hq : null;

    internal static Aircraft LocalAircraft =>
        GameManager.GetLocalAircraft(out var a) && a != null && !a.disabled ? a : null;

    /// <summary>Human players of a side currently flying.</summary>
    internal static Player[] FlyingPlayers(FactionHQ hq)
    {
        if (hq == null) return Array.Empty<Player>();
        try
        {
            return hq.GetPlayers(sortByScore: false)
                     .Where(p => p != null && p.Aircraft != null && !p.Aircraft.disabled)
                     .ToArray();
        }
        catch
        {
            return Array.Empty<Player>();
        }
    }

    internal static string PlayerName(Player p)
    {
        try { return p != null ? p.GetDisplayName(PlayerNameContext.ChatOrLeaderboard) : ""; }
        catch { return ""; }
    }

    internal static string Label =>
        GameManager.gameState == GameState.SinglePlayer
            ? (Plugin.SimulateMultiplayer.Value ? "single player (simulating multiplayer)" : "single player")
            : InMultiplayer ? (IsServer ? "multiplayer host" : "multiplayer client (encounters run on the host)") : "not in a mission";
}
