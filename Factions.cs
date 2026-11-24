using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

internal enum Side
{
    Unknown,
    /// <summary>Boscali Defense Force (factionName "Boscali").</summary>
    BDF,
    /// <summary>Primeva Armed Liberation Front (factionName "Primeva").</summary>
    PALA,
}

/// <summary>
/// The two sides and what they flavour: names, callsigns, colours and radio lines. BDF is a standing defence force
/// (formal units, naval squadrons, blue and grey); PALA is a liberation front (regiments, partisan names, red and olive).
/// </summary>
internal static class Factions
{
    internal static Side Of(FactionHQ hq)
    {
        string name = hq != null && hq.faction != null ? hq.faction.factionName : null;
        if (string.IsNullOrEmpty(name)) return Side.Unknown;
        if (name.IndexOf("Boscali", StringComparison.OrdinalIgnoreCase) >= 0) return Side.BDF;
        if (name.IndexOf("Primeva", StringComparison.OrdinalIgnoreCase) >= 0) return Side.PALA;
        return Side.Unknown;
    }

    internal static string Short(FactionHQ hq) => Of(hq) switch
    {
        Side.BDF => "BDF",
        Side.PALA => "PALA",
        _ => hq != null && hq.faction != null ? hq.faction.factionName : "Enemy",
    };

    internal static string Long(FactionHQ hq) => Of(hq) switch
    {
        Side.BDF => "Boscali Defense Force",
        Side.PALA => "Primeva Armed Liberation Front",
        _ => Short(hq),
    };

    internal static string Country(FactionHQ hq) => Of(hq) switch
    {
        Side.BDF => "Boscali",
        Side.PALA => "Primeva",
        _ => Short(hq),
    };

    /// <summary>"BDF", "Boscali", "Boscali Defense Force", "PALA", "Primeva"... in squadron.json; empty = either side.</summary>
    internal static Side Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Side.Unknown;
        string t = text.Trim();
        if (t.Equals("BDF", StringComparison.OrdinalIgnoreCase) || t.IndexOf("Boscali", StringComparison.OrdinalIgnoreCase) >= 0) return Side.BDF;
        if (t.Equals("PALA", StringComparison.OrdinalIgnoreCase) || t.IndexOf("Primeva", StringComparison.OrdinalIgnoreCase) >= 0) return Side.PALA;
        return Side.Unknown;
    }

    internal static FactionHQ HQFor(Side side) =>
        side == Side.Unknown ? null : FactionRegistry.GetAllHQs().FirstOrDefault(h => h != null && Of(h) == side);

    /// <summary>The side fighting <paramref name="hq"/>: of the others, the one holding the most airbases.</summary>
    internal static FactionHQ Opponent(FactionHQ hq)
    {
        FactionHQ best = null;
        int bestBases = -1;
        foreach (var other in FactionRegistry.GetAllHQs())
        {
            if (other == null || other == hq) continue;
            int bases = FactionRegistry.airbaseLookup.Values.Count(a => a != null && a.CurrentHQ == other);
            if (bases > bestBases)
            {
                best = other;
                bestBases = bases;
            }
        }
        return best;
    }

    // ---- flavour --------------------------------------------------------------------------------------

    internal static readonly string[] BdfCallsigns = { "Lancer", "Sentinel", "Warden", "Saber", "Halo", "Paladin", "Corsair", "Bastion", "Aegis", "Kestrel" };
    internal static readonly string[] PalaCallsigns = { "Partisan", "Ember", "Spark", "Hammer", "Comet", "Thorn", "Banner", "Wildfire", "Sickle", "Torch" };

    internal static readonly string[] BdfTeamOrgs = { "Vanguard", "Sentinel", "Aggressor", "Home Guard", "Rapid Reaction", "Expeditionary", "Air Defence" };
    internal static readonly string[] PalaTeamOrgs = { "Liberation", "Revolutionary", "People's", "Partisan", "Vanguard", "Red Banner", "Free Primeva" };

    internal static readonly string[] BdfColourWords = { "Cobalt", "Azure", "Slate", "Silver", "Ivory", "Gold", "Indigo", "Ash" };
    internal static readonly string[] PalaColourWords = { "Crimson", "Vermilion", "Scarlet", "Ember", "Amber", "Onyx", "Jade", "Ash" };

    internal static readonly string[] PalaRegimentTypes =
    {
        "Liberation Guards Air Regiment", "Revolutionary Fighter Regiment", "People's Air Defence Regiment", "Partisan Air Brigade",
        "Red Banner Fighter Regiment",
    };

    internal static readonly string[] BdfPlaces = { "the Boscali Coast", "the Capital", "the Northern Shield", "the Boscali Skies", "the Border" };
    internal static readonly string[] PalaPlaces = { "the Highlands", "Free Primeva", "the Mountain Front", "the Uprising", "the Red Dawn" };

    internal static readonly Color[] BdfPalette =
    {
        new(0.20f, 0.42f, 0.80f), new(0.60f, 0.65f, 0.70f), new(0.85f, 0.85f, 0.88f), new(0.86f, 0.70f, 0.28f), new(0.30f, 0.62f, 0.92f),
    };
    internal static readonly Color[] PalaPalette =
    {
        new(0.78f, 0.16f, 0.14f), new(0.92f, 0.42f, 0.14f), new(0.42f, 0.48f, 0.26f), new(0.80f, 0.68f, 0.45f), new(0.30f, 0.30f, 0.34f),
    };

    /// <summary>Default radio lines by the aces' side. They mix with the generic pools in RandomNames.</summary>
    internal static readonly Dictionary<Side, Dictionary<string, string[]>> Lines = new()
    {
        [Side.BDF] = new()
        {
            ["awacs"] = new[]
            {
                "AWACS: Boscali Defense Force interceptors scrambling. That's {squadron}, their best. Heads up.",
                "AWACS: The BDF just put {squadron} in the air. They're vectoring on {target}.",
                "AWACS: New contacts from Boscali territory, flying tight. Ace markings: {designation}.",
            },
            ["intro"] = new[]
            {
                "This is Boscali airspace. You don't get to leave it.",
                "Defense Force pilots, weapons free. Bring them down.",
                "{squadron} to all BDF units: we have the intruder. Stay clear.",
            },
            ["allied"] = new[]
            {
                "BDF Command: {squadron} is airborne and moving to intercept {target}. Give them room.",
                "BDF Command: Scrambling {squadron}. {target} is theirs.",
            },
            ["defeated"] = new[]
            {
                "AWACS: {squadron} is gone. The Defense Force just lost its pride.",
                "AWACS: That's the end of {squadron}. Boscali will feel that one.",
            },
            ["withdraw"] = new[] { "{squadron}, return to base. Boscali needs us in one piece." },
        },
        [Side.PALA] = new()
        {
            ["awacs"] = new[]
            {
                "AWACS: Liberation Front fighters inbound, low on the radar and fast. Ace markings: {squadron}.",
                "AWACS: PALA's {squadron} is hunting {target}. Watch your six.",
                "AWACS: Contacts out of Primeva. That's {designation}. They don't take prisoners.",
            },
            ["intro"] = new[]
            {
                "For the Front. {squadron}, engage.",
                "You've burned enough of Primeva. Now it's your turn.",
                "Every pilot in the Front knows your name. Let's see if you're worth it.",
            },
            ["allied"] = new[]
            {
                "Front Command: {squadron} has taken off. {target} won't see the morning.",
                "Front Command: All PALA units, {squadron} is engaging {target}. Stay out of their way.",
            },
            ["defeated"] = new[]
            {
                "AWACS: {squadron} is down. That'll hurt the Front.",
                "AWACS: The Liberation Front just lost {squadron}. Good kill.",
            },
            ["withdraw"] = new[] { "Fall back. The Front will need us tomorrow." },
        },
    };
}
