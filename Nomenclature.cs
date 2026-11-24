using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NOAcesHigh;

internal enum NamingScheme
{
    Random,
    /// <summary>"Grey Wolves", "The Wolves of the North", Viper 1, 2, 3.</summary>
    Classic,
    /// <summary>Project Wingman style: "Vermilion Team" of the "Primeva Vanguard Squadron", Vermilion I, II, III.</summary>
    Team,
    /// <summary>Ace Combat 7 style: "Umbra Solis", "41st Unit", "3rd Tactical Fighter Wing, 8th Aggressor Squadron", Vesper 22, 99, 358.</summary>
    Unit,
    /// <summary>"Sapsan" of the "117th Guards Fighter Aviation Regiment", Sapsan 01, 02.</summary>
    Regiment,
    /// <summary>"Black Herons", "Strike Fighter Squadron 104 (VFA-104)", Nomad 201, 202.</summary>
    Navy,
}

/// <summary>Everything a squadron is called: the big name, its unit number, its parent organisation and its epithet.</summary>
internal sealed class SquadronIdentity
{
    public NamingScheme Scheme;
    public string Name;
    public string Unit = "";
    public string Designation = "";
    public string Title = "";
    public Color? Colour;            // the Team scheme names the squadron after its colour
    public readonly List<string> Callsigns = new();
}

/// <summary>Builds squadron identities in several naming traditions. Fields set in squadron.json always win.</summary>
internal static class Nomenclature
{
    private static readonly (string Word, Color Colour)[] ColourWords =
    {
        ("Crimson", new Color(0.75f, 0.12f, 0.14f)), ("Vermilion", new Color(0.89f, 0.26f, 0.20f)),
        ("Scarlet", new Color(0.86f, 0.14f, 0.18f)), ("Amber", new Color(0.95f, 0.62f, 0.12f)),
        ("Gold", new Color(0.86f, 0.70f, 0.28f)), ("Ash", new Color(0.62f, 0.62f, 0.60f)),
        ("Slate", new Color(0.44f, 0.52f, 0.60f)), ("Cobalt", new Color(0.18f, 0.36f, 0.78f)),
        ("Azure", new Color(0.30f, 0.62f, 0.92f)), ("Violet", new Color(0.55f, 0.28f, 0.75f)),
        ("Jade", new Color(0.20f, 0.62f, 0.45f)), ("Ember", new Color(0.92f, 0.42f, 0.14f)),
        ("Ivory", new Color(0.90f, 0.88f, 0.80f)), ("Onyx", new Color(0.30f, 0.30f, 0.34f)),
        ("Silver", new Color(0.78f, 0.80f, 0.84f)), ("Indigo", new Color(0.30f, 0.26f, 0.68f)),
    };

    private static readonly string[] TeamOrgs =
    {
        "Vanguard", "Sentinel", "Interdiction", "Expeditionary", "Aggressor", "Guardian", "Pathfinder", "Enforcer",
        "Special Air", "Rapid Response",
    };

    private static readonly string[] LatinNouns =
    {
        "Umbra", "Ira", "Ventus", "Stella", "Ferrum", "Nox", "Ignis", "Gladius", "Corona", "Aquila", "Fulmen",
        "Sagitta", "Vox", "Ala", "Ultio", "Lupus", "Nubes", "Hasta",
    };

    private static readonly string[] LatinGenitives =
    {
        "Solis", "Noctis", "Caeli", "Maris", "Tempestatis", "Aquilonis", "Mortis", "Aurorae", "Hiemis", "Belli",
        "Ferri", "Stellarum", "Ignis", "Fati",
    };

    private static readonly string[] WingTypes =
    {
        "Special Operations", "Tactical Fighter", "Air Superiority", "Strike", "Interceptor", "Expeditionary", "Composite",
    };

    private static readonly string[] SquadronTypes = { "Tactical", "Fighter", "Aggressor", "Interceptor", "Special Tactics" };

    private static readonly string[] UnitCallsigns =
    {
        "Vesper", "Wraith", "Specter", "Phantom", "Eclipse", "Halo", "Cipher", "Nimbus", "Gale", "Shroud", "Cinder", "Moth",
    };

    private static readonly string[] RegimentNames =
    {
        "Berkut", "Sokol", "Krechet", "Strizh", "Vityaz", "Grom", "Molniya", "Burya", "Sapsan", "Korshun", "Yastreb",
        "Orlan", "Metel", "Kometa",
    };

    private static readonly string[] RegimentTypes =
    {
        "Guards Fighter Aviation Regiment", "Fighter Aviation Regiment", "Independent Fighter Squadron",
        "Guards Interceptor Regiment", "Naval Fighter Aviation Regiment",
    };

    private static readonly string[] RegimentTitles =
    {
        "Defenders of {place}", "Hammer of {place}", "Order of the Red Sky", "Heroes of {place}", "Guardians of {place}",
        "Sword of {place}",
    };

    private static readonly string[] NavyTypes = { "Strike Fighter Squadron {n} (VFA-{n})", "Fighter Squadron {n} (VF-{n})", "Marine Fighter Attack Squadron {n} (VMFA-{n})" };

    /// <summary>
    /// squadron.json, else the setting, else random weighted by side: the BDF favours formal team, navy and unit
    /// names; PALA favours regiments and unit names and never uses navy squadron numbers.
    /// </summary>
    internal static NamingScheme ResolveScheme(string fromJson, Side side = Side.Unknown)
    {
        bool pick = Variants.IsRandom(fromJson);
        if (!pick && Variants.TryParse(fromJson, out NamingScheme s) && s != NamingScheme.Random) return s;
        s = pick ? NamingScheme.Random : Plugin.Naming.Value;
        if (s != NamingScheme.Random) return s;
        NamingScheme[] pool = side switch
        {
            Side.BDF => new[] { NamingScheme.Team, NamingScheme.Team, NamingScheme.Navy, NamingScheme.Navy, NamingScheme.Unit, NamingScheme.Unit, NamingScheme.Classic },
            Side.PALA => new[] { NamingScheme.Regiment, NamingScheme.Regiment, NamingScheme.Regiment, NamingScheme.Unit, NamingScheme.Unit, NamingScheme.Classic, NamingScheme.Team },
            _ => new[] { NamingScheme.Classic, NamingScheme.Team, NamingScheme.Unit, NamingScheme.Regiment, NamingScheme.Navy },
        };
        return pool[Random.Range(0, pool.Length)];
    }

    /// <summary>A full identity for the scheme and side; squadron.json fields overwrite the generated ones afterwards.</summary>
    internal static SquadronIdentity Create(NamingScheme scheme, FactionHQ hq, int members)
    {
        var id = new SquadronIdentity { Scheme = scheme };
        Side side = Factions.Of(hq);
        string faction = Factions.Short(hq);
        string place = RandomNames.Place(side);
        switch (scheme)
        {
            case NamingScheme.Team:
            {
                var (word, colour) = TeamColour(side);
                id.Name = word + " Team";
                id.Colour = colour;
                string[] orgs = side switch { Side.BDF => Factions.BdfTeamOrgs, Side.PALA => Factions.PalaTeamOrgs, _ => TeamOrgs };
                id.Designation = $"{faction} {RandomNames.Pick(orgs)} Squadron";
                id.Title = RandomNames.Title(side);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {Roman(i + 1)}");
                break;
            }
            case NamingScheme.Unit:
            {
                id.Name = $"{RandomNames.Pick(LatinNouns)} {RandomNames.Pick(LatinGenitives)}";
                id.Unit = $"{Ordinal(Random.Range(11, 200))} Unit";
                id.Designation = side == Side.PALA
                    ? $"PALA {Ordinal(Random.Range(1, 9))} Liberation Air Wing, {Ordinal(Random.Range(2, 30))} {RandomNames.Pick(new[] { "Partisan", "Strike", "Guards", "Shock" })} Squadron"
                    : $"{(side == Side.BDF ? "BDF " : "")}{Ordinal(Random.Range(1, 9))} {RandomNames.Pick(WingTypes)} Wing, " +
                      $"{Ordinal(Random.Range(2, 30))} {RandomNames.Pick(SquadronTypes)} Squadron";
                id.Title = RandomNames.Title(side);
                string word = side == Side.Unknown ? RandomNames.Pick(UnitCallsigns) : RandomNames.Callsign(side);
                var used = new HashSet<int>();
                for (int i = 0; i < members; i++)
                {
                    int n;
                    do n = i == 0 ? Random.Range(10, 100) : (Random.value < 0.7f ? Random.Range(10, 100) : Random.Range(100, 1000));
                    while (!used.Add(n));
                    id.Callsigns.Add($"{word} {n}");
                }
                break;
            }
            case NamingScheme.Regiment:
            {
                string word = RandomNames.Pick(RegimentNames);
                id.Name = word;
                id.Designation = side == Side.PALA
                    ? $"PALA {Ordinal(Random.Range(1, 60))} {RandomNames.Pick(Factions.PalaRegimentTypes)}"
                    : $"{(side == Side.BDF ? "BDF " : "")}{Ordinal(Random.Range(14, 940))} {RandomNames.Pick(RegimentTypes)}";
                id.Title = RandomNames.Pick(RegimentTitles).Replace("{place}", place);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {i + 1:00}");
                break;
            }
            case NamingScheme.Navy:
            {
                string n = Random.Range(11, 214).ToString(CultureInfo.InvariantCulture);
                id.Name = RandomNames.SquadronName();
                id.Designation = (side == Side.BDF ? "BDF " : side == Side.PALA ? "PALA " : "") + RandomNames.Pick(NavyTypes).Replace("{n}", n);
                id.Title = RandomNames.Title(side);
                string word = RandomNames.Callsign(side);
                int modexBase = Random.Range(1, 4) * 100;
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {modexBase + i + 1}");
                break;
            }
            default:
            {
                id.Scheme = NamingScheme.Classic;
                id.Name = RandomNames.SquadronName();
                id.Title = RandomNames.Title(side);
                string word = RandomNames.Callsign(side);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {i + 1}");
                break;
            }
        }
        return id;
    }

    /// <summary>Team colour word and colour, from the side's palette words when the side is known.</summary>
    private static (string Word, Color Colour) TeamColour(Side side)
    {
        string[] words = side switch { Side.BDF => Factions.BdfColourWords, Side.PALA => Factions.PalaColourWords, _ => null };
        if (words != null)
        {
            string w = RandomNames.Pick(words);
            foreach (var cw in ColourWords)
                if (cw.Word == w) return cw;
        }
        return ColourWords[Random.Range(0, ColourWords.Length)];
    }

    /// <summary>The emblem shapes that suit each scheme; one is picked per squadron.</summary>
    internal static EmblemShape ShapeFor(NamingScheme scheme, int seed)
    {
        EmblemShape[] pool = scheme switch
        {
            NamingScheme.Team => new[] { EmblemShape.Pennant, EmblemShape.Arrowhead },
            NamingScheme.Unit => new[] { EmblemShape.Shield, EmblemShape.Crescent },
            NamingScheme.Regiment => new[] { EmblemShape.Roundel, EmblemShape.Shield },
            NamingScheme.Navy => new[] { EmblemShape.Wings, EmblemShape.Shield },
            _ => new[] { EmblemShape.Roundel, EmblemShape.Arrowhead, EmblemShape.Wings, EmblemShape.Crescent },
        };
        return pool[Mathf.Abs(seed) % pool.Length];
    }

    /// <summary>The title cards that suit each scheme.</summary>
    internal static CardStyle CardFor(NamingScheme scheme)
    {
        CardStyle[] pool = scheme switch
        {
            NamingScheme.Team => new[] { CardStyle.Caution },
            NamingScheme.Unit => new[] { CardStyle.Unit },
            NamingScheme.Regiment => new[] { CardStyle.Stamp, CardStyle.Typewriter },
            NamingScheme.Navy => new[] { CardStyle.PwBoss, CardStyle.Typewriter },
            _ => new[] { CardStyle.PwBoss, CardStyle.Stamp, CardStyle.Typewriter },
        };
        return pool[Random.Range(0, pool.Length)];
    }

    internal static string Ordinal(int n)
    {
        int last2 = n % 100;
        string suffix = last2 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    internal static string Roman(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}
