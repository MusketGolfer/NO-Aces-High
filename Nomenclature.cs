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
    /// <summary>A private military contractor: "Sable Company" of "Sable Aerospace Contractors", "Wings for Hire", Sable 1, 2.</summary>
    Mercenary,
    /// <summary>A legion with a Latin cognomen: "Fulminata", "XIII Air Legion", Fulminata I, II.</summary>
    Legion,
    /// <summary>An honour guard: "The Iron Guard", "Presidential Honour Guard Squadron", heraldic callsigns (Lion 1, 2).</summary>
    Guard,
    /// <summary>A test unit: "Sigma Flight", "Advanced Weapons Test Wing, 6th Test and Evaluation Squadron", "Project Nightjar", Sigma 01.</summary>
    Experimental,
    /// <summary>Volunteers and irregulars: "Coast Irregulars", "Free Volunteer Air Group", Drifter 1, 2.</summary>
    Militia,
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
        ("Cerulean", new Color(0.16f, 0.48f, 0.74f)), ("Cardinal", new Color(0.70f, 0.10f, 0.18f)), ("Saffron", new Color(0.96f, 0.72f, 0.18f)),
        ("Bronze", new Color(0.70f, 0.50f, 0.24f)), ("Copper", new Color(0.78f, 0.45f, 0.26f)), ("Pewter", new Color(0.56f, 0.58f, 0.60f)),
        ("Graphite", new Color(0.24f, 0.25f, 0.28f)), ("Sapphire", new Color(0.12f, 0.30f, 0.66f)), ("Ruby", new Color(0.80f, 0.08f, 0.24f)),
        ("Emerald", new Color(0.14f, 0.58f, 0.34f)), ("Obsidian", new Color(0.16f, 0.15f, 0.20f)), ("Pearl", new Color(0.92f, 0.90f, 0.86f)),
        ("Rust", new Color(0.66f, 0.30f, 0.16f)), ("Teal", new Color(0.12f, 0.52f, 0.54f)), ("Magenta", new Color(0.78f, 0.18f, 0.56f)),
        ("Cyan", new Color(0.22f, 0.72f, 0.82f)), ("Olive", new Color(0.44f, 0.48f, 0.24f)), ("Sand", new Color(0.80f, 0.70f, 0.50f)),
        ("Steel", new Color(0.50f, 0.56f, 0.62f)), ("Ochre", new Color(0.78f, 0.56f, 0.20f)), ("Maroon", new Color(0.50f, 0.10f, 0.14f)),
        ("Navy", new Color(0.12f, 0.18f, 0.40f)), ("Frost", new Color(0.78f, 0.88f, 0.94f)), ("Lime", new Color(0.56f, 0.80f, 0.20f)),
        ("Coral", new Color(0.94f, 0.48f, 0.40f)), ("Plum", new Color(0.46f, 0.22f, 0.44f)), ("Smoke", new Color(0.46f, 0.46f, 0.48f)),
        ("Cinnabar", new Color(0.86f, 0.30f, 0.22f)), ("Topaz", new Color(0.90f, 0.66f, 0.30f)), ("Midnight", new Color(0.10f, 0.12f, 0.24f)),
        ("Platinum", new Color(0.86f, 0.86f, 0.82f)), ("Garnet", new Color(0.56f, 0.12f, 0.16f)),
    };

    private static readonly string[] TeamOrgs =
    {
        "Vanguard", "Sentinel", "Interdiction", "Expeditionary", "Aggressor", "Guardian", "Pathfinder", "Enforcer",
        "Special Air", "Rapid Response",
        "Air Dominance", "Frontier", "Strategic", "Long Range", "Escort", "Night Fighter", "Intercept", "Recon-Strike", "Shock",
        "Spearhead", "Deep Strike", "Quick Reaction", "Combat Air Patrol", "Air Assault", "Tactical Response", "Spectre", "Reserve",
        "Coastal", "Highland", "Winter",
    };

    private static readonly string[] LatinNouns =
    {
        "Umbra", "Ira", "Ventus", "Stella", "Ferrum", "Nox", "Ignis", "Gladius", "Corona", "Aquila", "Fulmen",
        "Sagitta", "Vox", "Ala", "Ultio", "Lupus", "Nubes", "Hasta",
        "Tempestas", "Lux", "Mors", "Fatum", "Furor", "Glacies", "Ruina", "Flamma", "Sidus", "Draco", "Ensis", "Falx", "Vindex",
        "Custos", "Silentium", "Turbo", "Procella", "Tonitrus", "Fulgur", "Saxum", "Ventura", "Imber", "Cruor", "Aurum", "Argentum",
        "Spina", "Corvus", "Accipiter", "Leo", "Ursa", "Vulpes", "Serpens", "Scutum", "Aegis", "Murmur", "Anima",
    };

    private static readonly string[] LatinGenitives =
    {
        "Solis", "Noctis", "Caeli", "Maris", "Tempestatis", "Aquilonis", "Mortis", "Aurorae", "Hiemis", "Belli",
        "Ferri", "Stellarum", "Ignis", "Fati",
        "Lunae", "Ventorum", "Fulminis", "Sanguinis", "Montium", "Silvae", "Orientis", "Occidentis", "Septentrionis", "Meridiei",
        "Glaciei", "Tenebrarum", "Lucis", "Irae", "Victoriae", "Gloriae", "Honoris", "Vindictae", "Aeternitatis", "Umbrarum",
        "Procellae", "Tonitrus", "Draconis", "Leonis", "Corvi", "Lupi", "Aquilae", "Regis",
    };

    private static readonly string[] WingTypes =
    {
        "Special Operations", "Tactical Fighter", "Air Superiority", "Strike", "Interceptor", "Expeditionary", "Composite",
        "Fighter", "Air Defence", "Air Combat", "Long Range Strike", "Electronic Warfare", "Aggressor", "Training", "Rapid Reaction",
        "Carrier Air", "Reconnaissance", "Frontier Defence", "Night Operations", "Escort", "Air Mobility",
    };

    private static readonly string[] SquadronTypes =
    {
        "Tactical", "Fighter", "Aggressor", "Interceptor", "Special Tactics", "Air Superiority", "Strike Fighter", "Night Fighter",
        "Escort Fighter", "Combat", "Air Defence", "Adversary", "Weapons", "Pursuit", "Expeditionary Fighter",
    };

    private static readonly string[] UnitCallsigns =
    {
        "Vesper", "Wraith", "Specter", "Phantom", "Eclipse", "Halo", "Cipher", "Nimbus", "Gale", "Shroud", "Cinder", "Moth",
        "Zenith", "Nadir", "Aurora", "Solstice", "Equinox", "Meridian", "Penumbra", "Corona", "Quasar", "Pulsar", "Nova", "Cirrus",
        "Stratus", "Tempest", "Monsoon", "Zephyr", "Sirocco", "Mistral", "Haze", "Ember", "Ashfall", "Lantern", "Wisp", "Glimmer",
    };

    private static readonly string[] RegimentNames =
    {
        "Berkut", "Sokol", "Krechet", "Strizh", "Vityaz", "Grom", "Molniya", "Burya", "Sapsan", "Korshun", "Yastreb",
        "Orlan", "Metel", "Kometa",
        "Vikhr", "Tayfun", "Grad", "Shtorm", "Zarnitsa", "Strela", "Kinzhal", "Shkval", "Tuman", "Pozhar", "Zvezda", "Raduga",
        "Volna", "Sever", "Ural", "Taiga", "Kondor", "Filin", "Lun", "Albatros", "Drozd", "Voron", "Sych", "Kobchik", "Lebed",
        "Medved", "Rys", "Bars",
    };

    private static readonly string[] RegimentTypes =
    {
        "Guards Fighter Aviation Regiment", "Fighter Aviation Regiment", "Independent Fighter Squadron",
        "Guards Interceptor Regiment", "Naval Fighter Aviation Regiment",
        "Air Defence Fighter Regiment", "Independent Guards Air Squadron", "Mixed Aviation Regiment", "Long Range Interceptor Regiment",
        "Fighter-Bomber Aviation Regiment", "Guards Air Superiority Regiment", "Frontal Aviation Regiment", "Red Banner Air Regiment",
        "Separate Fighter Aviation Squadron", "Training Aviation Regiment",
    };

    private static readonly string[] RegimentTitles =
    {
        "Defenders of {place}", "Hammer of {place}", "Order of the Red Sky", "Heroes of {place}", "Guardians of {place}",
        "Sword of {place}",
        "Shield of {place}", "Wolves of {place}", "Pride of {place}", "Eagles of {place}", "Storm over {place}", "Iron Wing of {place}",
        "Order of the Iron Star", "Order of the White Feather", "The Unbroken", "The Undefeated", "First over {place}",
        "Last Guard of {place}",
    };

    private static readonly string[] NavyTypes =
    {
        "Strike Fighter Squadron {n} (VFA-{n})", "Fighter Squadron {n} (VF-{n})", "Marine Fighter Attack Squadron {n} (VMFA-{n})",
        "Fighter Composite Squadron {n} (VFC-{n})", "Marine Fighter Squadron {n} (VMF-{n})", "Air Test and Evaluation Squadron {n} (VX-{n})",
        "Electronic Attack Squadron {n} (VAQ-{n})", "Fighter Weapons Squadron {n} (VFW-{n})", "Marine All-Weather Fighter Squadron {n} (VMF(AW)-{n})",
    };

    // ---- the newer schemes ----
    private static readonly string[] CompanyWords =
    {
        "Sable", "Blackgate", "Ironclad", "Redline", "Greyhound", "Longbow", "Crossbones", "Silverlake", "Highmark", "Hammerfall",
        "Northstar", "Deadeye", "Gunmetal", "Stonewall", "Wildcard", "Kingsguard", "Ravenhook", "Blackwater", "Brightwing", "Coldiron",
    };
    private static readonly string[] CompanyKinds =
    {
        "Aerospace Contractors", "Air Services", "Defence Solutions", "Aviation Group", "Security Consultants", "Tactical Aviation",
        "Private Air Corps", "Flight Operations", "Risk Management", "Air Logistics",
    };
    private static readonly string[] MercTitles =
    {
        "Wings for Hire", "Paid in Full", "No Flag, No Mercy", "The Highest Bidder", "Business as Usual", "Contract Killers",
        "The Price of Victory", "Loyal to the Cheque", "Freelance Thunder", "Everything Has a Price",
    };
    private static readonly string[] LegionCognomens =
    {
        "Fulminata", "Victrix", "Ferrata", "Gemina", "Rapax", "Fretensis", "Augusta", "Primigenia", "Apollinaris", "Macedonica",
        "Adiutrix", "Valeria", "Italica", "Fidelis", "Invicta", "Scythica", "Deiotariana", "Parthica", "Minervia", "Cyrenaica",
    };
    private static readonly string[] LegionTitles =
    {
        "Thunderbolt Legion", "The Iron Legion", "Ever Victorious", "Faithful and Steadfast", "The Twin Legion", "Hawks of the Eagle",
        "The Unconquered", "Bearers of the Standard", "First to the Sky", "Shield of the Republic",
    };
    private static readonly string[] GuardWords =
    {
        "Iron", "Golden", "White", "Silver", "Crimson", "Black", "Old", "Royal", "Steel", "Ivory", "Azure", "Storm", "Winter", "Sun",
        "Lion",
    };
    private static readonly string[] GuardKinds =
    {
        "Presidential Honour Guard Squadron", "Capital Air Guard", "Ceremonial Flight", "Household Air Squadron", "Guards Air Wing",
        "State Honour Squadron", "Palace Air Defence Flight", "Colour Guard Squadron",
    };
    private static readonly string[] HeraldicCallsigns =
    {
        "Lion", "Griffin", "Unicorn", "Stag", "Eagle", "Dragon", "Falcon", "Boar", "Bear", "Wyvern", "Hart", "Swan", "Raven", "Phoenix",
    };
    private static readonly string[] GreekLetters =
    {
        "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa", "Lambda", "Mu", "Nu", "Xi", "Omicron",
        "Pi", "Rho", "Sigma", "Tau", "Upsilon", "Phi", "Chi", "Psi", "Omega",
    };
    private static readonly string[] ProjectCodenames =
    {
        "Nightjar", "Glasshouse", "Overcast", "Bluebird", "Tinderbox", "Quicksilver", "Long Shadow", "Whiteout", "Firebreak",
        "Sundog", "Halcyon", "Starling", "Paper Moon", "Tripwire", "Black Mirror", "Kestrel Eye", "Deep Blue", "Clockwork",
    };
    private static readonly string[] TestUnits =
    {
        "Advanced Weapons Test Wing", "Flight Test Centre", "Experimental Aviation Group", "Combat Development Wing",
        "Special Projects Division", "Air Warfare Test Group",
    };
    private static readonly string[] MilitiaPlaces =
    {
        "Coast", "Valley", "Ridge", "Harbour", "Delta", "Steppe", "Marsh", "Hill Country", "Borderland", "River", "Plateau", "Lakeside",
        "Forest", "Canyon", "Dune",
    };
    private static readonly string[] MilitiaKinds = { "Irregulars", "Volunteers", "Militia", "Defenders", "Rangers", "Free Flyers", "Home Guard", "Partisans" };
    private static readonly string[] MilitiaCallsigns =
    {
        "Drifter", "Tinker", "Ploughman", "Ferryman", "Scrapper", "Shepherd", "Vagrant", "Smuggler", "Fisher", "Tramp", "Mule",
        "Crow", "Bandit", "Hobo", "Peddler",
    };
    private static readonly string[] MilitiaTitles =
    {
        "Hold the Line", "Home Soil", "Nobody's Army", "The Last Defence", "Scrap and Spite", "Farmers with Fighters",
        "Patched and Dangerous", "Our Sky, Our Rules",
    };

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
            Side.BDF => new[] { NamingScheme.Team, NamingScheme.Team, NamingScheme.Navy, NamingScheme.Navy, NamingScheme.Unit, NamingScheme.Unit, NamingScheme.Classic,
                                NamingScheme.Guard, NamingScheme.Guard, NamingScheme.Experimental, NamingScheme.Mercenary },
            Side.PALA => new[] { NamingScheme.Regiment, NamingScheme.Regiment, NamingScheme.Regiment, NamingScheme.Unit, NamingScheme.Unit, NamingScheme.Classic, NamingScheme.Team,
                                 NamingScheme.Legion, NamingScheme.Militia, NamingScheme.Militia, NamingScheme.Mercenary },
            _ => new[] { NamingScheme.Classic, NamingScheme.Team, NamingScheme.Unit, NamingScheme.Regiment, NamingScheme.Navy,
                         NamingScheme.Mercenary, NamingScheme.Legion, NamingScheme.Guard, NamingScheme.Experimental, NamingScheme.Militia },
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
            case NamingScheme.Mercenary:
            {
                string word = RandomNames.Pick(CompanyWords);
                id.Name = $"{word} {RandomNames.Pick(new[] { "Company", "Group", "Squadron", "Flight", "Detachment" })}";
                id.Designation = $"{word} {RandomNames.Pick(CompanyKinds)}";
                id.Title = RandomNames.Pick(MercTitles);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {i + 1}");
                break;
            }
            case NamingScheme.Legion:
            {
                string cog = RandomNames.Pick(LegionCognomens);
                id.Name = cog;
                id.Unit = $"{RomanFull(Random.Range(1, 31))} Air Legion";
                id.Designation = $"{(side == Side.Unknown ? "" : faction + " ")}{RandomNames.Pick(new[] { "Legionary Air Corps", "Air Legion Command", "Legion Aviation" })}";
                id.Title = RandomNames.Pick(LegionTitles);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{cog} {Roman(i + 1)}");
                break;
            }
            case NamingScheme.Guard:
            {
                id.Name = $"The {RandomNames.Pick(GuardWords)} Guard";
                id.Designation = $"{(side == Side.Unknown ? "" : faction + " ")}{RandomNames.Pick(GuardKinds)}";
                id.Title = RandomNames.Pick(new[] { "Defenders of {place}", "Honour Above All", "Sworn to {place}", "The Crown's Own", "Ever Watchful" }).Replace("{place}", place);
                string word = RandomNames.Pick(HeraldicCallsigns);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {i + 1}");
                break;
            }
            case NamingScheme.Experimental:
            {
                string letter = RandomNames.Pick(GreekLetters);
                id.Name = $"{letter} Flight";
                id.Unit = $"{Ordinal(Random.Range(1, 20))} Test and Evaluation Squadron";
                id.Designation = $"{(side == Side.Unknown ? "" : faction + " ")}{RandomNames.Pick(TestUnits)}";
                id.Title = $"Project {RandomNames.Pick(ProjectCodenames)}";
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{letter} {i + 1:00}");
                break;
            }
            case NamingScheme.Militia:
            {
                string where = RandomNames.Pick(MilitiaPlaces);
                id.Name = $"{where} {RandomNames.Pick(MilitiaKinds)}";
                id.Designation = $"{(side == Side.PALA ? "Free" : side == Side.BDF ? "Boscali" : "Independent")} Volunteer Air Group";
                id.Title = RandomNames.Pick(MilitiaTitles);
                string word = RandomNames.Pick(MilitiaCallsigns);
                for (int i = 0; i < members; i++) id.Callsigns.Add($"{word} {i + 1}");
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
            NamingScheme.Team => new[] { EmblemShape.Pennant, EmblemShape.Arrowhead, EmblemShape.Delta, EmblemShape.Bolt },
            NamingScheme.Unit => new[] { EmblemShape.Shield, EmblemShape.Crescent, EmblemShape.Hexagon, EmblemShape.Diamond },
            NamingScheme.Regiment => new[] { EmblemShape.Roundel, EmblemShape.Shield, EmblemShape.Sunburst, EmblemShape.Cross },
            NamingScheme.Navy => new[] { EmblemShape.Wings, EmblemShape.Shield, EmblemShape.Tab, EmblemShape.Diamond },
            NamingScheme.Mercenary => new[] { EmblemShape.Spade, EmblemShape.Tab, EmblemShape.Bolt, EmblemShape.Diamond },
            NamingScheme.Legion => new[] { EmblemShape.Shield, EmblemShape.Sunburst, EmblemShape.Cross, EmblemShape.Wings },
            NamingScheme.Guard => new[] { EmblemShape.Cross, EmblemShape.Shield, EmblemShape.Crescent, EmblemShape.Sunburst },
            NamingScheme.Experimental => new[] { EmblemShape.Hexagon, EmblemShape.Delta, EmblemShape.Bolt, EmblemShape.Tab },
            NamingScheme.Militia => new[] { EmblemShape.Tab, EmblemShape.Roundel, EmblemShape.Arrowhead, EmblemShape.Spade },
            _ => new[] { EmblemShape.Roundel, EmblemShape.Arrowhead, EmblemShape.Wings, EmblemShape.Crescent, EmblemShape.Spade,
                         EmblemShape.Delta, EmblemShape.Bolt, EmblemShape.Hexagon, EmblemShape.Diamond, EmblemShape.Sunburst },
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
            NamingScheme.Mercenary => new[] { CardStyle.Stamp, CardStyle.Caution },
            NamingScheme.Legion => new[] { CardStyle.PwBoss, CardStyle.Unit },
            NamingScheme.Guard => new[] { CardStyle.PwBoss, CardStyle.Typewriter },
            NamingScheme.Experimental => new[] { CardStyle.Caution, CardStyle.Typewriter },
            NamingScheme.Militia => new[] { CardStyle.Stamp, CardStyle.Typewriter },
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

    /// <summary>Roman numerals for any 1-3999.</summary>
    internal static string RomanFull(int n)
    {
        var map = new (int v, string s)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new System.Text.StringBuilder();
        foreach (var (v, s) in map) while (n >= v) { sb.Append(s); n -= v; }
        return sb.ToString();
    }

    internal static string Roman(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => RomanFull(n),
    };
}
