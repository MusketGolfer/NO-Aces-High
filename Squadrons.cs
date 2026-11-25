using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NOAcesHigh;

// SquadronDef / MemberDef fields are filled by Json.NET.
#pragma warning disable CS0649

/// <summary>A squadron as written in squadron.json. Every field is optional; missing ones are rolled at spawn.</summary>
internal sealed class SquadronDef
{
    [JsonProperty("squadron")] public string Squadron;
    /// <summary>"BDF" / "Boscali" or "PALA" / "Primeva": only that side launches it. Unset = either side.</summary>
    [JsonProperty("faction")] public string Faction;
    [JsonProperty("title")] public string Title;
    /// <summary>"emblem.png", or a folder of numbered PNG frames ("emblem/0001.png" ...).</summary>
    [JsonProperty("emblem")] public string Emblem;
    /// <summary>The emblem to use: "emblem" may list backups ("mine.png, fallback.png"); the first one present wins.</summary>
    [JsonIgnore] public string EmblemFile
    {
        get
        {
            var all = Variants.Entries(Emblem);
            if (all.Length == 0) return null;
            if (Folder != null)
                foreach (var e in all)
                    if (File.Exists(Path.Combine(Folder, e)) || Directory.Exists(Path.Combine(Folder, e))) return e;
            return all[0];
        }
    }
    [JsonProperty("emblemFps")] public float? EmblemFps;
    [JsonProperty("color")] public string Color;
    /// <summary>Title card: pw-boss, stamp, typewriter, caution, unit (or unset to suit the naming scheme).</summary>
    [JsonProperty("introStyle")] public string IntroStyle;
    /// <summary>Camera shot list: classic, tails, headon, cockpit, lead, nose, distant (or unset/random).</summary>
    [JsonProperty("introShots")] public string IntroShots;
    /// <summary>classic, team, unit, regiment, navy: fills whatever name fields are missing in that tradition.</summary>
    [JsonProperty("naming")] public string Naming;
    /// <summary>Unit number shown after the name, e.g. "41st Unit".</summary>
    [JsonProperty("unit")] public string Unit;
    /// <summary>Parent organisation, e.g. "3rd Tactical Fighter Wing, 8th Aggressor Squadron".</summary>
    [JsonProperty("designation")] public string Designation;
    /// <summary>Generated emblem when there is no artwork: roundel, shield, pennant, arrowhead, wings, crescent.</summary>
    [JsonProperty("emblemShape")] public string EmblemShape;
    /// <summary>echelonright, echelonleft, vic, fingerfour, lineabreast, trail, diamond, box.</summary>
    [JsonProperty("formation")] public string Formation;
    [JsonProperty("tier")] public int? Tier;
    [JsonProperty("aircraft")] public List<string> Aircraft;
    /// <summary>{"builtin": 2} or {"builtin": "name"}, {"appdata": "folder"}, {"workshop": 123}.</summary>
    /// <summary>One livery or a list, each for one jet (see Liveries).</summary>
    [JsonProperty("livery")] public JToken Livery;
    /// <summary>Unset/"default" = the best air-to-air loadout; a loadout name (standard or saved preset); or a built one (see Loadouts).</summary>
    [JsonProperty("loadout")] public JToken Loadout;
    [JsonProperty("members")] public List<MemberDef> Members;
    [JsonProperty("lines")] public Dictionary<string, List<string>> Lines;
    /// <summary>Music per slot: intro, cutscene, loop, outro, escape (paths relative to the squadron folder).</summary>
    [JsonProperty("music")] public Dictionary<string, string> Music;
    /// <summary>Recorded lines per line key, in the order of "lines" (see VoiceLines).</summary>
    [JsonProperty("voice")] public JObject Voice;

    [JsonIgnore] public string Folder;
    [JsonIgnore] public bool Handmade;
    /// <summary>Loaded once with the squadron so the intro never hitches on disk reads.</summary>
    [JsonIgnore] public Emblem LoadedEmblem;
    /// <summary>What SquadronCheck found wrong with the file (shown in the squadron menu).</summary>
    [JsonIgnore] public List<string> Problems = new();

    /// <summary>Folder name: stable across reloads, used to remember which squadrons are in the random rotation.</summary>
    [JsonIgnore] public string Key => string.IsNullOrEmpty(Folder) ? Squadron ?? "" : Path.GetFileName(Folder);
}

internal sealed class MemberDef
{
    [JsonProperty("callsign")] public string Callsign;
    [JsonProperty("name")] public string Name;
    [JsonProperty("lead")] public bool Lead;
    [JsonProperty("skill")] public float? Skill;
    /// <summary>This pilot's own airframe, as a priority list like the squadron's (the first installed is flown); unset = the squadron's.</summary>
    [JsonProperty("aircraft")] public List<string> Aircraft;
    /// <summary>This pilot's own loadout, like the squadron's "loadout"; unset = the squadron's.</summary>
    [JsonProperty("loadout")] public JToken Loadout;
    /// <summary>This pilot's own livery (one or a list, like the squadron's); unset = the squadron's.</summary>
    [JsonProperty("livery")] public JToken Livery;
}

internal static class Squadrons
{
    internal static readonly List<SquadronDef> Handmade = new();

    /// <summary>Handmade squadrons the random rolls may pick (the squadron menu can take one out of rotation).</summary>
    internal static List<SquadronDef> InRotation() => Handmade.Where(IsInRotation).ToList();

    internal static bool IsInRotation(SquadronDef def) => !ExcludedKeys().Contains(def.Key);

    internal static void SetInRotation(SquadronDef def, bool on)
    {
        var keys = ExcludedKeys();
        if (on) keys.Remove(def.Key);
        else keys.Add(def.Key);
        Plugin.ExcludedSquadrons.Value = string.Join(";", keys.OrderBy(k => k));
    }

    private static HashSet<string> ExcludedKeys() =>
        new((Plugin.ExcludedSquadrons.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    internal static string UserFolder => Path.Combine(Paths.ConfigPath, "NOAcesHigh", "Squadrons");

    internal static void Reload()
    {
        Handmade.Clear();
        SquadronCheck.FailedFiles.Clear();
        try { Directory.CreateDirectory(UserFolder); } catch { /* read-only config dir: just skip */ }
        SquadronGuide.EnsureUserFolder();

        var roots = new List<string> { UserFolder };
        try
        {
            roots.AddRange(Directory.GetDirectories(Paths.PluginPath, "AceSquadrons", SearchOption.AllDirectories));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Could not scan plugins for AceSquadrons folders: " + ex.Message);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir).StartsWith("_")) continue; // templates and set-aside squadrons
                var file = Path.Combine(dir, "squadron.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var raw = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                    var def = raw.ToObject<SquadronDef>();
                    if (def == null) continue;
                    def.Folder = dir;
                    def.Handmade = true;
                    def.LoadedEmblem = Emblem.Load(def);
                    def.Problems = SquadronCheck.Validate(def, raw);
                    Handmade.Add(def);
                    Plugin.Log.LogInfo($"Loaded squadron '{def.Squadron ?? Path.GetFileName(dir)}' from {file}" +
                                       (def.Problems.Count > 0 ? $" with {def.Problems.Count} problem(s):\n  " + string.Join("\n  ", def.Problems) : ""));
                }
                catch (JsonException ex)
                {
                    string what = $"{Path.GetFileName(dir)}/squadron.json could not be read: {ex.Message}";
                    SquadronCheck.FailedFiles.Add(what);
                    Plugin.Log.LogWarning(what);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Skipping {file}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}

/// <summary>Fills a squadron's missing identity fields from word lists.</summary>
internal static class RandomNames
{
    private static readonly Random Rng = new();

    private static readonly string[] Adjectives =
    {
        "Grey", "Crimson", "Iron", "Silent", "Black", "Golden", "Frozen", "Storm", "Ashen", "Scarlet",
        "Midnight", "Thunder", "Sable", "Burning", "Hollow", "Steel", "Pale", "Shattered", "Cobalt", "Ivory",
        "Red", "White", "Blue", "Ghost", "Silver", "Dusk", "Dawn", "Northern", "Southern", "Wild", "Savage", "Lone", "Twin",
        "Crooked", "Broken", "Bleeding", "Laughing", "Howling", "Weeping", "Winter", "Summer", "Bone", "Copper", "Bronze", "Obsidian",
        "Smoking", "Restless", "Vengeful", "Ancient", "Feral", "Grim", "Lucky", "Nameless", "Cursed", "Blind", "Sleepless",
        "Iron-Willed", "Sun", "Moon", "Star",
    };

    private static readonly string[] Animals =
    {
        "Wolves", "Ravens", "Vipers", "Hounds", "Falcons", "Jackals", "Hornets", "Serpents", "Lions", "Sharks",
        "Owls", "Scorpions", "Panthers", "Mantises", "Condors", "Wyverns", "Stags", "Herons", "Bears", "Spiders",
        "Hawks", "Eagles", "Kites", "Kestrels", "Ospreys", "Harriers", "Crows", "Magpies", "Shrikes", "Swifts", "Tigers", "Leopards",
        "Lynxes", "Cobras", "Adders", "Barracudas", "Marlins", "Orcas", "Wasps", "Locusts", "Bats", "Foxes", "Coyotes", "Boars",
        "Rams", "Bulls", "Mustangs", "Dragons", "Griffins", "Basilisks", "Hydras", "Krakens", "Phoenixes", "Gargoyles", "Banshees",
        "Valkyries", "Djinn", "Harpies", "Centaurs", "Minotaurs",
    };

    private static readonly string[] TitleNouns =
    {
        "Knights", "Shadows", "Reapers", "Wardens", "Hunters", "Ghosts", "Blades", "Saints", "Butchers", "Kings",
        "Devils", "Angels", "Lords", "Widows", "Furies", "Executioners", "Prophets", "Heralds", "Wolves", "Jackals", "Thieves",
        "Pilgrims", "Sentinels", "Outlaws", "Phantoms", "Heroes", "Monsters", "Martyrs", "Ravens", "Crows",
    };

    private static readonly string[] Places =
    {
        "the North", "the Iron Coast", "the Southern Front", "the Red Sky", "the Frozen Strait", "the Last Line",
        "the Eastern Sea", "the Ash Plains", "the High Desert", "the Storm Belt",
        "the Western Wall", "the Grey Mountains", "the Salt Flats", "the Burning Coast", "the Silent Valley", "the Black Forest",
        "the Long Night", "the Broken Isles", "the Northern Lights", "the Cold Front", "the Glass Lakes", "the Red River",
        "the Dead Sea", "the White Wastes", "the Thunder Coast", "the Twin Peaks", "the Deep South", "the Endless Steppe",
        "the Sunken City", "the Iron Hills",
    };

    private static readonly string[] Callsigns =
    {
        "Viper", "Ghost", "Wolf", "Raven", "Saber", "Talon", "Reaper", "Lancer", "Spectre", "Mamba",
        "Cyclone", "Jester", "Hydra", "Onyx", "Fang", "Warden", "Echo", "Banshee", "Kestrel", "Nomad",
        "Cobra", "Hammer", "Anvil", "Bandit", "Joker", "Ace", "Rogue", "Shadow", "Blaze", "Frost", "Havoc", "Venom", "Diablo",
        "Rapier", "Cutlass", "Scimitar", "Halberd", "Mace", "Javelin", "Arrow", "Bolt", "Spike", "Razor", "Scythe", "Thunder",
        "Typhoon", "Monsoon", "Avalanche", "Volcano", "Meteor", "Comet", "Orbit", "Vector", "Zero", "Static", "Gremlin", "Goblin",
        "Wraith", "Ronin", "Shogun",
    };

    private static readonly string[] FirstNames =
    {
        "Viktor", "Anya", "Marek", "Lena", "Dmitri", "Sasha", "Konrad", "Irina", "Tomas", "Mila",
        "Arkady", "Vera", "Henrik", "Katya", "Oskar", "Nadia", "Emil", "Yelena", "Radek", "Ilse",
        "Lukas", "Mira", "Stefan", "Petra", "Anton", "Zofia", "Matteo", "Ingrid", "Pavel", "Elsa", "Bruno", "Daria", "Felix",
        "Greta", "Igor", "Hana", "Jonas", "Leni", "Kasimir", "Olga", "Rafael", "Sonja", "Teodor", "Ursula", "Valentin", "Wanda",
        "Yuri", "Zara", "Adrian", "Bianca", "Cyril", "Dora", "Edvard", "Freya", "Gustav", "Helena", "Ivo", "Jana", "Karel", "Lidia",
    };

    private static readonly string[] LastNames =
    {
        "Halden", "Vasko", "Brenner", "Sokolova", "Kral", "Dragunov", "Lindqvist", "Marchetti", "Novak", "Reyes",
        "Varga", "Orlov", "Stenberg", "Kovac", "Ilyin", "Ferro", "Dvorak", "Zeller", "Morozova", "Haas",
        "Albrecht", "Bauer", "Castellano", "Dimitrov", "Engel", "Falk", "Grieg", "Horvat", "Ivanova", "Jansen", "Kessler",
        "Lazar", "Mertens", "Nordin", "Ostrowski", "Petrov", "Quist", "Rasmussen", "Sorensen", "Tanaka", "Ulrich", "Volkov",
        "Weiss", "Yilmaz", "Zielinski", "Abramov", "Becker", "Costa", "Dahl", "Eriksen", "Fischer", "Gallo", "Hoffmann", "Ivic",
        "Jovanovic", "Klein", "Lindgren", "Moretti", "Nikolaev", "Okafor",
    };

    internal static readonly Dictionary<string, string[]> DefaultLines = new()
    {
        ["awacs"] = new[]
        {
            "AWACS: New contacts inbound, they're flying in tight formation... Ace emblem confirmed. Watch yourself.",
            "AWACS: Heads up, the enemy just put up their best. {squadron}, closing on your position.",
            "AWACS: Bandits inbound, high and fast. That's {squadron}. Don't get greedy.",
        },
        ["intro"] = new[]
        {
            "You've made a lot of noise out here. Time to end it.",
            "So you're the one. {squadron}, engage.",
            "Command wants you gone. Personally, I just want to see if you're any good.",
            "All flights, this one's mine. Stay on my wing.",
        },
        // Ambient banter (AmbientChatter): no event, just the aces talking.
        ["chatterIngress"] = new[]
        {
            "Tighten it up. We do this clean.",
            "{target}. I've read the reports. Let's see if they're true.",
            "Weather's good. Good day for it.",
            "Stay off the radio unless it matters.",
            "Twenty minutes and we're home. Make them count.",
            "Feels quiet. I don't like quiet.",
            "Nobody gets ahead of me on this one.",
            "{squadron}, check in. ...Good.",
        },
        ["chatterFight"] = new[]
        {
            "Keep the pressure on. Don't let them breathe.",
            "That one's good. Better than I expected.",
            "Watch your six, I'm not losing anyone today.",
            "Is that all you've got, {target}?",
            "Stay with me. Don't chase.",
            "They're getting tired. I can feel it.",
            "Hah! This is more like it!",
            "Patience. They'll make a mistake.",
            "Don't get greedy. Pick your shot.",
            "{target} flies like they've got something to prove.",
        },
        ["chatter"] = new[]
        {
            "Stay sharp.",
            "Eyes open.",
        },
        ["wingmanDown"] = new[]
        {
            "{callsign}! ...You'll pay for that.",
            "We lost {callsign}! Stay focused, stay on the target!",
            "Damn it, {callsign} is down!",
        },
        ["leadDown"] = new[]
        {
            "Lead is down! ...Break off, break off!",
            "No... Lead's gone. Everyone, disengage!",
        },
        ["defeated"] = new[]
        {
            "AWACS: {squadron} is broken. Outstanding flying.",
            "AWACS: That's the last of {squadron}. They'll be talking about this one.",
        },
        ["evade"] = new[]
        {
            "Too slow.",
            "Is that all you've got?",
            "Missed me. Try again.",
            "You'll need more than that.",
        },
        ["snap"] = new[]
        {
            "Fox two. Got you.",
            "Right there. Don't move.",
            "In my sights.",
        },
        ["cover"] = new[]
        {
            "Get off my wingman!",
            "I see him. Coming around.",
            "Hold on, I've got your six.",
            "Break right, I'm on him.",
        },
        ["pincer"] = new[]
        {
            "Taking the far side. Squeeze him.",
            "I'll cut him off. Drive him to me.",
            "Pincer. Nowhere to run.",
        },
        ["winchester"] = new[]
        {
            "Winchester. I'm out, heading home.",
            "Nothing left to shoot. Breaking off.",
            "I'm dry. You got lucky this time.",
        },
        ["bingo"] = new[]
        {
            "Bingo fuel. I'm leaving.",
            "Fuel's low. Disengaging.",
            "Bingo. This isn't over.",
        },
        ["dark"] = new[]
        {
            "Radars off. Let him wonder.",
            "Going quiet.",
        },
        ["intercept"] = new[]
        {
            "Missile inbound, shooting it down.",
            "Not today. Killing that missile.",
        },
        ["parthian"] = new[]
        {
            "You followed me in. Mistake.",
            "Right across my nose. Fox two.",
            "Still on my tail? Have one of these.",
        },
        ["overshoot"] = new[]
        {
            "You flew right past me.",
            "Overshoot. Now I'm behind you.",
            "Rookie mistake.",
        },
        ["allied"] = new[]
        {
            "Command: {squadron} is airborne, moving to intercept {target}.",
            "Command: Friendly aces launching. {squadron} has {target}.",
        },
        ["break"] = new[]
        {
            "{squadron}, break formation. Weapons free.",
            "Break, break. Take them apart.",
            "Contact. {squadron}, engage at will.",
            "Splitting up. Don't let them get behind you.",
        },
        ["withdraw"] = new[]
        {
            "This isn't over. {squadron}, withdrawing.",
        },
    };

    internal static T Pick<T>(IList<T> list) => list[Rng.Next(list.Count)];

    internal static string SquadronName() => $"{Pick(Adjectives)} {Pick(Animals)}";
    internal static string Title() => $"The {Pick(TitleNouns)} of {Pick(Places)}";
    internal static string Callsign() => Pick(Callsigns);
    internal static string PilotName() => $"{Pick(FirstNames)} {Pick(LastNames)}";
    internal static string Place() => Pick(Places);

    /// <summary>The squadron's own line for the key, else a default: side-flavoured ones (BDF/PALA) more often than generic.</summary>
    internal static string Line(SquadronDef def, string key, Side aceSide = Side.Unknown)
    {
        if (def.Lines != null && def.Lines.TryGetValue(key, out var own) && own is { Count: > 0 })
            return Pick(own);
        string[] sided = null;
        if (Factions.Lines.TryGetValue(aceSide, out var bySide)) bySide.TryGetValue(key, out sided);
        DefaultLines.TryGetValue(key, out var generic);
        if (sided is { Length: > 0 } && (generic == null || Rng.NextDouble() < 0.65)) return Pick(sided);
        return generic is { Length: > 0 } ? Pick(generic) : null;
    }

    internal static string Callsign(Side side) => side switch
    {
        Side.BDF => Pick(Factions.BdfCallsigns),
        Side.PALA => Pick(Factions.PalaCallsigns),
        _ => Callsign(),
    };

    internal static string Place(Side side) => side switch
    {
        Side.BDF when Rng.NextDouble() < 0.6 => Pick(Factions.BdfPlaces),
        Side.PALA when Rng.NextDouble() < 0.6 => Pick(Factions.PalaPlaces),
        _ => Place(),
    };

    internal static string Title(Side side) => $"The {Pick(TitleNouns)} of {Place(side)}";
}
