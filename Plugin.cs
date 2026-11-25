using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("ArcadeCam", BepInDependency.DependencyFlags.SoftDependency)] // optional: only asked whether its weapon camera is showing
[BepInDependency("UIPlus", BepInDependency.DependencyFlags.SoftDependency)] // optional: HUD integration; plain IMGUI fallbacks without it
[BepInDependency("com.nikkorap.blueprinter", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "NOAcesHigh";
    public const string Name = "NO Aces High";
    public const string Version = "1.2.1";

    internal static ManualLogSource Log;
    internal static Plugin Instance;

    // Encounters
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> Multiplayer;
    internal static ConfigEntry<int> MaxEncounters;
    internal static ConfigEntry<float> UnlockThreat;
    internal static ConfigEntry<float> BaseLossBonus;
    internal static ConfigEntry<float> RollChance;
    internal static ConfigEntry<float> RollInterval;
    internal static ConfigEntry<float> Cooldown;
    internal static ConfigEntry<float> HandmadeWeight;
    internal static ConfigEntry<float> SpawnDistanceKm;
    internal static ConfigEntry<float> VectorUntilKm;

    // Difficulty
    internal static ConfigEntry<float> SkillMultiplier;
    internal static ConfigEntry<int> MaxTier;
    internal static ConfigEntry<bool> LiftLimiter;
    internal static ConfigEntry<float> Tier2G;
    internal static ConfigEntry<float> Tier3G;
    internal static ConfigEntry<float> Tier3Alpha;
    internal static ConfigEntry<bool> AceAggression;
    internal static ConfigEntry<float> EnergyFloor;
    internal static ConfigEntry<float> MissileRangeShare;
    internal static ConfigEntry<bool> WingmanBracket;
    internal static ConfigEntry<bool> PostStall;
    internal static ConfigEntry<int> PostStallTier;
    internal static ConfigEntry<float> PostStallMaxG;
    internal static ConfigEntry<bool> AceHunt;
    internal static ConfigEntry<float> FlareMultiplier;
    internal static ConfigEntry<bool> UnlimitedPower, AlwaysJam, BeamRadarMissiles, CounterMissiles, Retreat, GoDark, ParthianShots;
    internal static ConfigEntry<float> RetreatFuel;
    internal static ConfigEntry<float> FormationSpacing;
    internal static ConfigEntry<bool> IntroPrewarm, IntroFadeCuts, IntroHoldFire;
    internal static ConfigEntry<bool> HealthBars;
    internal static ConfigEntry<bool> GeneratedLiveries;
    internal static ConfigEntry<float> HealthBarsY, HealthBarsX, HealthBarsWidth, HealthBarsScale, HealthBarsRotation;
    internal static ConfigEntry<string> PostStallAirframes;

    // Airframes
    internal static ConfigEntry<string> AllowedAirframes;
    internal static ConfigEntry<string> DisabledWeapons;

    // Intro
    internal static ConfigEntry<bool> PlayIntro;
    internal static ConfigEntry<bool> IntroCameraCut;
    internal static ConfigEntry<float> IntroSlowMotion;
    internal static ConfigEntry<float> IntroMaxWait;
    internal static ConfigEntry<bool> MultiplayerCinematic;
    internal static ConfigEntry<bool> MultiplayerSlowMotion;

    // Variants
    internal static ConfigEntry<NamingScheme> Naming;
    internal static ConfigEntry<EmblemShape> EmblemStyle;
    internal static ConfigEntry<CardStyle> CardStyleSetting;
    internal static ConfigEntry<IntroShots> ShotsSetting;
    internal static ConfigEntry<FormationKind> Formation;
    internal static ConfigEntry<bool> HoldFormation;
    internal static ConfigEntry<float> FormationBreakKm;

    // Squadrons
    internal static ConfigEntry<string> ExcludedSquadrons;

    // Debug
    internal static ConfigEntry<EndEncounterAction> EndAction;
    internal static ConfigEntry<bool> SimulateMultiplayer;

    // HUD and music
    internal static ConfigEntry<bool> ShowThreatBar;
    internal static ConfigEntry<float> ThreatBarX;
    internal static ConfigEntry<float> ThreatBarY, ThreatBarScale, ThreatBarRotation;
    internal static ConfigEntry<bool> MusicEnabled, VoiceEnabled, VoiceRadioFilter, RadioSubtitles, ShowSquadronDown;
    internal static ConfigEntry<float> VoiceVolume, ChatterMinSeconds, ChatterMaxSeconds, ChatterChance;
    internal static ConfigEntry<bool> ChatterEnabled;
    internal static ConfigEntry<bool> MusicGatesIntro;
    internal static ConfigEntry<float> MaxMusicGateSeconds;

    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        BindConfig();

        _harmony = new Harmony(Guid);
        Patches.Apply(_harmony);

        Squadrons.Reload();
        Log.LogInfo($"{Name} {Version} loaded; {Squadrons.Handmade.Count} handmade squadron(s).");
        Music.EnsureFolder();
        if (RecordMode.Requested) RecordMode.Start(this);
    }

    private void BindConfig()
    {
        SettingsMenu.Bind(Config);
        Enabled = Config.Bind(SettingsMenu.Top, "Enabled", true, Order(100, "Launch enemy ace squadrons."));
        const string enc = "1. Encounters";
        const string tac = "3. Tactics";
        const string snd = "8. Sound";
        const string exp = SettingsMenu.Experimental;
        Multiplayer = Config.Bind(enc, "Multiplayer", true, Order(95,
            "Run encounters in multiplayer games you host (or on a dedicated server running the mod). Every player is expected to have " +
            "NO Aces High and its dependencies: the host sends each encounter to everyone, and each player gets the radio as subtitles and the " +
            "intro on their own screen. A player without the mod still sees the radio lines in the message feed. The host's setting decides; on a client this does nothing."));
        MaxEncounters = Config.Bind(enc, "MaxEncountersPerMission", 1,
            new ConfigDescription("Most ace encounters in one mission (all sides together).", new AcceptableValueRange<int>(0, 3), Attr(90)));
        UnlockThreat = Config.Bind(enc, "ThreatToUnlock", 100f,
            new ConfigDescription("Threat a side's pilots must build up before aces can be rolled against that side. Human kills add threat by target type " +
                                  "(fighter 10, bomber/AWACS 20, ship 30, vehicle 2, building 1). The aces hunt the pilot of that side who has done the most.",
                new AcceptableValueRange<float>(10f, 1000f), Attr(80)));
        BaseLossBonus = Config.Bind(enc, "ThreatPerBaseLost", 60f,
            new ConfigDescription("Threat a side gains when it captures an airbase.", new AcceptableValueRange<float>(0f, 500f), Attr(70)));
        RollChance = Config.Bind(enc, "RollChance", 0.35f,
            new ConfigDescription("Chance per roll that aces launch, once unlocked.", new AcceptableValueRange<float>(0f, 1f), Attr(60)));
        RollInterval = Config.Bind(enc, "SecondsBetweenRolls", 90f,
            new ConfigDescription("Seconds between rolls once unlocked.", new AcceptableValueRange<float>(15f, 600f), Attr(50)));
        Cooldown = Config.Bind(enc, "CooldownSeconds", 300f,
            new ConfigDescription("Seconds after an encounter ends before the next roll.", new AcceptableValueRange<float>(0f, 1800f), Attr(40)));
        HandmadeWeight = Config.Bind(enc, "HandmadeSquadronChance", 0.3f,
            new ConfigDescription("Chance an encounter uses a handmade squadron (if any are installed) instead of a random one.", new AcceptableValueRange<float>(0f, 1f), Attr(30)));
        SpawnDistanceKm = Config.Bind(enc, "SpawnDistanceKm", 60f,
            new ConfigDescription("How far from the player the ace flight appears.", new AcceptableValueRange<float>(20f, 150f), Attr(20)));
        VectorUntilKm = Config.Bind(enc, "VectorUntilKm", 30f,
            new ConfigDescription("Enemy ground control keeps the aces told where the player is until they are this close; 0 turns vectoring off. Other enemy units see that track too.", new AcceptableValueRange<float>(0f, 150f), Attr(10)));

        const string dif = "2. Difficulty";
        SkillMultiplier = Config.Bind(dif, "AceSkillMultiplier", 1f,
            new ConfigDescription("Multiplies ace skill (missile evasion) and bravery.", new AcceptableValueRange<float>(0.5f, 2f), Attr(100)));
        MaxTier = Config.Bind(dif, "MaxTier", 3,
            new ConfigDescription("Highest ace tier that can appear.", new AcceptableValueRange<int>(1, 3), Attr(90)));
        LiftLimiter = Config.Bind(dif, "LiftLimiter", true, Order(80,
            "Tier 2 and 3 aces fly with a higher fly-by-wire g limit (and tier 3 a higher AoA limit). Back to stock below 300 m radar altitude."));
        Tier2G = Config.Bind(dif, "Tier2GLimit", 12f,
            new ConfigDescription("G limit for tier 2 aces. Pilots take damage above 20 g.", new AcceptableValueRange<float>(9f, 19f), Attr(70)));
        Tier3G = Config.Bind(dif, "Tier3GLimit", 16f,
            new ConfigDescription("G limit for tier 3 aces. Pilots take damage above 20 g.", new AcceptableValueRange<float>(9f, 19f), Attr(60)));
        Tier3Alpha = Config.Bind(dif, "Tier3AoALimit", 35f,
            new ConfigDescription("AoA limiter for tier 3 aces, in degrees (it only acts below corner speed).", new AcceptableValueRange<float>(20f, 60f), Attr(50)));
        AceAggression = Config.Bind(dif, "FullAggression", true, Order(40,
            "Tier 2 and 3 aces turn and bank at full aggression while they have the speed for it (see EnergyFloor)."));
        EnergyFloor = Config.Bind(dif, "EnergyFloor", 0.8f,
            new ConfigDescription("Below this share of corner speed aces fly gently to rebuild energy instead of pulling.", new AcceptableValueRange<float>(0.5f, 1.2f), Attr(30)));
        MissileRangeShare = Config.Bind(dif, "MissileRangeShare", 0.6f,
            new ConfigDescription("Aces hold missiles against aircraft until the target is inside this share of the missile's max range (1 = stock).", new AcceptableValueRange<float>(0.3f, 1f), Attr(20)));
        WingmanBracket = Config.Bind(dif, "WingmanBracket", true, Order(10,
            "Ace wingmen closing from beyond 6 km come in 35 degrees off the line to their target, alternating sides, then converge."));
        PostStall = Config.Bind(dif, "PostStallManeuvers", true, Order(9,
            "Aces fly scripted post-stall manoeuvres, each ending in a recovery to 250 km/h: Cobra (someone closing from behind within " +
            "1.5 km; any airframe), Herbst / J-turn (target behind, 1.5-8 km) and Kulbit nose snap (target 40-90 deg off the nose in " +
            "missile range), the last two only in post-stall capable airframes (see 3. Airframes). Never below 1,500 m; any manoeuvre " +
            "breaks off into the recovery below 600 m."));
        PostStallTier = Config.Bind(dif, "PostStallMinTier", 3,
            new ConfigDescription("Lowest ace tier that flies post-stall manoeuvres (airframes ticked Post-stall by hand allow any tier).",
                new AcceptableValueRange<int>(1, 3), Attr(8)));
        AceHunt = Config.Bind(dif, "HuntMode", true, Order(8,
            "Close in, aces stop flying like the stock AI: with a target ahead of the wing line inside 4 km they roll the lift onto it and pull as hard as the airframe takes (PostStallMaxG and the joint-load governor), use lag pursuit instead of overshooting, ease off when slow, shoot guns inside 1.2 km and a missile further out, break off for missiles or the ground, and fly straight into the hunt after a post-stall manoeuvre."));
        PostStallMaxG = Config.Bind(dif, "PostStallMaxG", 14f,
            new ConfigDescription("Scripted pulls (manoeuvres and the hunt) ease off above this g, or when the airframe's joints carry 75% of their break load.",
                new AcceptableValueRange<float>(6f, 19f), Attr(7)));
        FlareMultiplier = Config.Bind(dif, "AceFlareMultiplier", 4f,
            new ConfigDescription("Aces carry this many times the normal flare load, and pop flares during every post-stall manoeuvre.",
                new AcceptableValueRange<float>(1f, 8f), Attr(6)));

        UnlimitedPower = Config.Bind(tac, "UnlimitedPower", true, Order(5,
            "Aces' capacitors never run down: radar, jammers and energy weapons always have power."));
        AlwaysJam = Config.Bind(tac, "AlwaysJam", true, Order(5,
            "Aces run their jammers all the time while fighting: self-protection ECM against radar missiles (an un-locked radar seeker cannot acquire them inside 5 km), and any jamming pod on the pilot they are hunting. Off while the flight runs dark."));
        BeamRadarMissiles = Config.Bind(tac, "BeamRadarMissiles", true, Order(5,
            "Against a radar-guided missile still several seconds out, aces turn to put it on the beam (where its Doppler return is weakest), drop toward the ground clutter with their jammers running, before the last-second break."));
        CounterMissiles = Config.Bind(tac, "CounterMissiles", true, Order(4,
            "Aces may spend a missile on an incoming missile when it is worth it: a radar missile flares cannot stop, a missile from behind, a slow jet, more than one inbound, flares gone; weighed against how many missiles they have left. Only weapons the game rates as anti-missile, never the last one unless desperate, never at tier 1."));
        Retreat = Config.Bind(tac, "RetreatWhenSpent", true, Order(4,
            "An ace that is out of missiles and gun ammunition (winchester), or low on fuel, calls it and leaves the fight for the map edge, firing back at anyone chasing it."));
        RetreatFuel = Config.Bind(tac, "RetreatFuelShare", 0.15f,
            new ConfigDescription("Fuel share at which an ace retreats.", new AcceptableValueRange<float>(0.03f, 0.5f), Attr(4)));
        GoDark = Config.Bind(tac, "GoDark", true, Order(3,
            "A flight may switch its radars and jammers off on the way in, but only when it would really drop off the scope: its side already tracks the target, and no radar or spotter of the hunted side sees the flight or has it inside radar range (AWACS, ships and ground radars share contacts over datalink, so going dark under their coverage would change nothing). They light up again when spotted anyway, to shoot a radar missile, when the target is lost, when fired on, or close in."));
        ParthianShots = Config.Bind(tac, "ParthianShots", true, Order(3,
            "Aces extending away or retreating fire back at a pursuer behind them: radar missiles over the shoulder; post-stall airframes flip the nose round for an infrared shot."));

        const string air = "5. Airframes";
        AllowedAirframes = Config.Bind(air, "AllowedAirframes", "",
            new ConfigDescription("Airframes random aces may fly. Edit with the checklist.", null,
                new ConfigurationManagerAttributes { CustomDrawer = AirframeChecklist.Draw, HideDefaultButton = true, HideSettingName = true, Order = 10 }));
        DisabledWeapons = Config.Bind(air, "DisabledWeapons", "",
            new ConfigDescription("Airframe|weapon pairs random aces may not carry. Edited with the airframe checklist.", null,
                new ConfigurationManagerAttributes { Browsable = false }));
        Config.Bind(air, "Loadouts", "", new ConfigDescription("Look at every jet's loadouts and build your own presets.", null,
            new ConfigurationManagerAttributes { CustomDrawer = Loadouts.Draw, HideDefaultButton = true, HideSettingName = true }));
        PostStallAirframes = Config.Bind(air, "PostStallAirframes", "",
            new ConfigDescription("Post-stall overrides: +key capable (and flagged), -key not capable. Edited with the airframe checklist.", null,
                new ConfigurationManagerAttributes { Browsable = false }));

        const string intro = "6. Intro";
        PlayIntro = Config.Bind(intro, "PlayIntro", true, Order(40, "Introduce each ace squadron with its title card (emblem, name, title, lead pilot)."));
        IntroCameraCut = Config.Bind(intro, "CinematicCamera", true, Order(30,
            "Single player: cut away to the aces in slow motion behind the title card. Off: the title card shows over normal play. " +
            "In multiplayer the intro is the title card only unless MultiplayerCinematic is on."));
        MultiplayerCinematic = Config.Bind(exp, "MultiplayerCinematic", false, Order(6,
            "EXPERIMENTAL. In multiplayer (or simulated), also cut your camera away to the aces for the intro. Everyone with the mod decides " +
            "this for their own screen. The cut waits for the same safety checks as single player and ends at once if a missile is fired at " +
            "you or you drop below 300 m. Off: the title card shows over normal play."));
        MultiplayerSlowMotion = Config.Bind(exp, "MultiplayerSlowMotion", true, Order(5,
            "EXPERIMENTAL, with MultiplayerCinematic. Slow the game during the cut as in single player. Untested: it changes the game speed " +
            "on your machine only. On a client your own aircraft (simulated on your machine) slows with it while the server runs on; " +
            "on the host it slows the server, and so the whole game, for everyone. Off: the cut plays at full speed."));
        IntroHoldFire = Config.Bind(tac, "AcesHoldFireDuringIntro", true, Order(6,
            "Aces release no weapons (missiles, guns, counter-missile shots, jamming) from the moment their intro is queued until it ends: " +
            "its music, the wait for a safe moment, the camera shots and the title card. They still fly and defend themselves."));
        IntroPrewarm = Config.Bind(intro, "PrewarmShots", true, Order(4,
            "Render the first intro shot on a hidden camera for a moment before cutting to it, so terrain, models and textures there are loaded (less pop-in)."));
        IntroFadeCuts = Config.Bind(intro, "FadeCameraJumps", true, Order(4,
            "Dip to black for a fraction of a second at the cut to the aces and the cut back, hiding what still pops in after a long camera jump."));
        IntroSlowMotion = Config.Bind(intro, "SlowMotion", 0.35f,
            new ConfigDescription("Game speed during the cinematic (1 = none). Lower is more dramatic but the fly-by moves less.", new AcceptableValueRange<float>(0.1f, 1f), Attr(20)));
        IntroMaxWait = Config.Bind(intro, "MaxWaitSeconds", 20f,
            new ConfigDescription("The cinematic waits until it is safe to cut away (no missile inbound, above 300 m, gear up, not pulling hard). After this long it shows the title card alone.", new AcceptableValueRange<float>(0f, 120f), Attr(10)));

        const string hud = "7. HUD";
        ShowThreatBar = Config.Bind(hud, "ThreatBar", true, Order(100,
            "Show an ace threat gauge on the HUD: how close the enemy is to sending aces at your side, then who is inbound and how many are left. " +
            "Lives inside the UIPlus HUD (its colour and font); a plain bar is drawn instead when that HUD is not showing."));
        ThreatBarX = Config.Bind(hud, "ThreatBarX", 0.5f,
            new ConfigDescription("Threat gauge centre across the screen (0 = left, 1 = right).", new AcceptableValueRange<float>(0f, 1f), Attr(90)));
        ThreatBarY = Config.Bind(hud, "ThreatBarY", 0.86f,
            new ConfigDescription("Threat gauge centre up the screen (0 = bottom, 1 = top).", new AcceptableValueRange<float>(0f, 1f), Attr(80)));
        ThreatBarScale = Config.Bind(hud, "ThreatBarScale", 1f,
            new ConfigDescription("Threat gauge size (1 = as designed). Also set in UIPlus's Customize HUD Layout screen.", new AcceptableValueRange<float>(0.3f, 3f), Attr(79)));
        ThreatBarRotation = Config.Bind(hud, "ThreatBarRotation", 0f,
            new ConfigDescription("Threat gauge rotation in degrees, anticlockwise.", new AcceptableValueRange<float>(-180f, 180f), Attr(78)));
        HealthBars = Config.Bind(hud, "SquadronHealthBars", true, Order(90,
            "Health bars along the bottom of the screen for the ace squadron hunting your side: its name, then one bar per aircraft, in the HUD Danger colour (Accessibility colour theme). Health is the mass-weighted condition of the aircraft's parts (the hit points the game's own damage display uses)."));
        HealthBarsY = Config.Bind(hud, "SquadronHealthBarsY", 0.04f,
            new ConfigDescription("Height of the health bars on screen (0 = bottom, 1 = top).", new AcceptableValueRange<float>(0f, 1f), Attr(89)));
        HealthBarsX = Config.Bind(hud, "SquadronHealthBarsX", 0.5f,
            new ConfigDescription("Centre of the health bars across the screen (0 = left, 1 = right). Also set in UIPlus's Customize HUD Layout screen.", new AcceptableValueRange<float>(0f, 1f), Attr(88)));
        HealthBarsWidth = Config.Bind(hud, "SquadronHealthBarsWidth", 0.66f,
            new ConfigDescription("Width of the row of health bars, as a fraction of the screen width.", new AcceptableValueRange<float>(0.1f, 1f), Attr(87)));
        HealthBarsScale = Config.Bind(hud, "SquadronHealthBarsScale", 0.8f,
            new ConfigDescription("Thickness of the bars and size of the squadron name (1 = as designed).", new AcceptableValueRange<float>(0.3f, 3f), Attr(86)));
        HealthBarsRotation = Config.Bind(hud, "SquadronHealthBarsRotation", 0f,
            new ConfigDescription("Rotation of the health bars in degrees, anticlockwise, about their bottom centre.", new AcceptableValueRange<float>(-180f, 180f), Attr(85)));
        MusicEnabled = Config.Bind(snd, "Music", true, Order(70,
            "Play ace music for the side being hunted, through the game's music (its volume setting applies). Slots: intro (as the aces are called in), " +
            "cutscene (during the intro), loop (the sortie, looping), outro (aces beaten) and escape (aces withdrew; falls back to outro). " +
            "Put intro.ogg, loop.ogg... (.ogg, .wav or .mp3) in BepInEx/config/NOAcesHigh/Music, or in Music/BDF and Music/PALA for one side's aces, " +
            "or name them in a squadron's \"music\" entry. Missing slots are skipped."));
        ChatterEnabled = Config.Bind(snd, "AmbientChatter", true, Order(65,
            "Aces talk now and then without any event to set them off: banter on the way in and during the fight, from a squadron's \"chatterIngress\" / \"chatterFight\" / \"chatter\" lines, or built-in ones. Never during the intro or over another radio line."));
        ChatterMinSeconds = Config.Bind(snd, "ChatterMinSeconds", 25f,
            new ConfigDescription("Shortest gap between ambient lines, in seconds.", new AcceptableValueRange<float>(5f, 300f), Attr(64)));
        ChatterMaxSeconds = Config.Bind(snd, "ChatterMaxSeconds", 60f,
            new ConfigDescription("Longest gap between ambient lines, in seconds (each gap is picked at random between the two).", new AcceptableValueRange<float>(5f, 600f), Attr(63)));
        ChatterChance = Config.Bind(snd, "ChatterChance", 0.7f,
            new ConfigDescription("Chance an ace speaks when a gap ends (so the rhythm is not regular).", new AcceptableValueRange<float>(0f, 1f), Attr(62)));
        VoiceEnabled = Config.Bind(snd, "VoiceLines", true, Order(69,
            "Play recorded radio lines from a squadron's \"voice\" entry (see the squadron README)."));
        VoiceVolume = Config.Bind(snd, "VoiceVolume", 0.8f,
            new ConfigDescription("Volume of recorded radio lines.", new AcceptableValueRange<float>(0f, 1f), Attr(68)));
        VoiceRadioFilter = Config.Bind(snd, "VoiceRadioFilter", true, Order(67,
            "Play recorded lines through a radio filter (thin and slightly crunchy), as if heard over the radio."));
        ShowSquadronDown = Config.Bind(hud, "SquadronDownCard", true, Order(65,
            "When a squadron hunting your side is wiped out: its emblem stamped DESTROYED, how long it took, how many of its aces were yours, and any medal you just earned."));
        RadioSubtitles = Config.Bind(hud, "RadioSubtitles", true, Order(66,
            "Show the aces' and controllers' radio lines as text. Off: only recorded lines are heard, nothing is shown."));
        MusicGatesIntro = Config.Bind(snd, "IntroWaitsForMusic", true, Order(60,
            "The intro cinematic / title card waits for the intro music to play out, so the build-up leads into it."));
        MaxMusicGateSeconds = Config.Bind(snd, "MaxIntroMusicWait", 40f,
            new ConfigDescription("Longest the intro waits for the intro music, in seconds.", new AcceptableValueRange<float>(0f, 180f), Attr(50)));

        const string variants = "4. Squadrons";
        Naming = Config.Bind(variants, "Naming", NamingScheme.Random, new ConfigDescription(
            "How random squadrons are named. Classic: Grey Wolves, Viper 1. Team: Vermilion Team of the <faction> Vanguard Squadron, Vermilion I. " +
            "Unit: Umbra Solis, 41st Unit, 3rd Tactical Fighter Wing, Vesper 22. Regiment: Sapsan, 117th Guards Fighter Aviation Regiment, Sapsan 01. " +
            "Navy: Black Herons, Strike Fighter Squadron 104 (VFA-104), Nomad 201. Mercenary: Sable Company, Sable Aerospace Contractors, Sable 1. " +
            "Legion: Fulminata, XIII Air Legion, Fulminata I. Guard: The Iron Guard, Presidential Honour Guard Squadron, Lion 1. " +
            "Experimental: Sigma Flight, Project Nightjar, Sigma 01. Militia: Coast Irregulars, Volunteer Air Group, Drifter 1. Handmade squadrons can pick their own with \"naming\".", null, Attr(70)));
        EmblemStyle = Config.Bind(variants, "EmblemShape", EmblemShape.Match, new ConfigDescription(
            "Generated emblem for squadrons without artwork: fourteen frames, the newer eight with one of eight centre motifs. Match picks one that suits the naming (Team: pennant, arrowhead, delta, bolt; Unit: shield, crescent, hexagon, diamond...).", null, Attr(60)));
        CardStyleSetting = Config.Bind(variants, "TitleCard", CardStyle.Match, new ConfigDescription(
            "Intro title card. Caution: faded emblem behind a blinking CAUTION box. Unit: bottom-left emblem and unit caption. " +
            "PwBoss / Stamp / Typewriter: centred card with letterbox. Match picks one that suits the naming.", null, Attr(50)));
        ShotsSetting = Config.Bind(variants, "IntroShots", IntroShots.Random, new ConfigDescription(
            "Camera shot list. Classic: fly-by, orbit, tail. Tails: along the fins. HeadOn: long lens head-on, then from below. " +
            "Cockpit: over a wingman's canopy. Lead: the lead alone, popping flares. Nose: under the nose, along the fuselage. Distant: extreme long lens, then a beam pass. " +
            "Escort: riding behind the lead, a rack focus from a wingman's wingtip, then straight down. Silhouette: against the sun, a push-in to the canopy, the formation from behind. Overhead: straight down, a beam pass, riding behind the lead.", null, Attr(40)));
        Formation = Config.Bind(variants, "Formation", FormationKind.Random, new ConfigDescription(
            "Formation random squadrons spawn, fly in and egress in. Random picks one for the flight size.", null, Attr(30)));
        HoldFormation = Config.Bind(variants, "HoldFormation", true, Order(20,
            "Wingmen hold formation on the lead on the way in, and break to engage."));
        GeneratedLiveries = Config.Bind(exp, "GeneratedLiveries", false, Order(5,
            "EXPERIMENTAL. Squadrons without a livery of their own get one made on the spot: the jet's livery recoloured to the squadron colour (shading and panel lines kept), and the squadron emblem on both sides of the tail fins. Made once per airframe and colour, over a few frames; every player builds the same one."));
        FormationSpacing = Config.Bind(variants, "FormationSpacing", 70f, new ConfigDescription(
            "Distance between slots on the way in (metres). Within 2.5 km of its slot a wingman flies the slot (and the rejoin) itself, matching the lead's attitude, instead of the stock autopilot.", new AcceptableValueRange<float>(40f, 200f), Attr(15)));
        FormationBreakKm = Config.Bind(variants, "FormationBreakKm", 25f, new ConfigDescription(
            "The flight breaks formation when any ace is this close to a pilot of the side it is hunting, a missile is fired at one, or after 6 minutes.", new AcceptableValueRange<float>(5f, 80f), Attr(10)));

        Config.Bind(variants, "Installed", "", new ConfigDescription("Installed handmade squadrons.", null,
            new ConfigurationManagerAttributes { CustomDrawer = SquadronMenu.Draw, HideDefaultButton = true, HideSettingName = true, Order = 10 }));
        ExcludedSquadrons = Config.Bind(variants, "ExcludedSquadrons", "", new ConfigDescription(
            "Squadron folders taken out of the random rotation. Edited with the squadron list.", null,
            new ConfigurationManagerAttributes { Browsable = false }));

        const string dbg = exp;
        EndAction = Config.Bind(dbg, "EndEncounterAction", EndEncounterAction.Egress,
            new ConfigDescription("What the End encounter button does. Egress: the aces drop all targets and run at full throttle for the nearest map edge that does not lead back past the player, defending only against missiles, and are removed when they reach it. Despawn: they vanish at once. Aces that time out of an encounter always egress.", null, Attr(20)));
        SimulateMultiplayer = Config.Bind(dbg, "SimulateMultiplayer", false, Order(15,
            "Play single player under the multiplayer rules: title card only unless MultiplayerCinematic (slow motion only with MultiplayerSlowMotion), " +
            "the opening line on the host's fixed delay, and radio lines sent through the same mission-message path the host uses for clients."));
        SettingsMenu.Arrange(Config);
    }

    /// <summary>Testing buttons, drawn inside the Experimental and testing section.</summary>
    internal static void DrawDebugTools()
    {
        GUILayout.BeginVertical();
        if (GUILayout.Button("Spawn an ace squadron now (closes this window, 2 s)"))
            SquadronMenu.Message = DebugQueue.Queue("Spawning a squadron", () => SquadronMenu.Message = Encounters.DebugSpawnNow());
        string endLabel = EndAction.Value == EndEncounterAction.Despawn ? "End encounter (despawn aces)" : "End encounter (aces egress)";
        if (GUILayout.Button(endLabel))
            Encounters.DebugEndEncounter();
        if (GUILayout.Button("Replay intro for the active squadron"))
            SquadronMenu.Message = DebugQueue.Queue("Replaying the intro", Encounters.DebugReplayIntro);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Lead flies:");
        foreach (Manoeuvre m in Enum.GetValues(typeof(Manoeuvre)))
        {
            var forced = m;
            if (GUILayout.Button(m.ToString()))
                SquadronMenu.Message = DebugQueue.Queue($"Lead flies {forced}", () => SquadronMenu.Message = global::NOAcesHigh.PostStall.Force(forced));
        }
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Reload squadron files"))
            Squadrons.Reload();
        GUILayout.Label(Encounters.StatusLine());
        if (!string.IsNullOrEmpty(SquadronMenu.Message)) GUILayout.Label(SquadronMenu.Message);
        GUILayout.EndVertical();
    }

    private static ConfigDescription Order(int order, string text) => new(text, null, Attr(order));
    private static ConfigurationManagerAttributes Attr(int order) => new() { Order = order };

    private void Update()
    {
        try
        {
            Encounters.Tick();
        }
        catch (Exception ex)
        {
            Log.LogError("Encounter tick failed: " + ex);
        }
        try
        {
            NetSync.Tick();
            Music.Tick();
            ThreatBar.Tick();
            SquadronHealth.Tick();
            LiveryForge.Tick();
        }
        catch (Exception ex)
        {
            Log.LogError("Encounter sync tick failed: " + ex);
        }
        try
        {
            DebugQueue.Tick();
            Emblem.Tick();
            IntroDirector.Tick();
            SettingsMenu.Tick();
            VoiceLines.Tick();
            Stats.Tick();
            Breakups.Tick();
        }
        catch (Exception ex)
        {
            Log.LogError("Intro tick failed: " + ex);
            IntroDirector.Abort();
        }
    }

    private void OnGUI()
    {
        try
        {
            IntroDirector.OnGUI(); // under the title card
            TitleCard.OnGUI();
            ThreatBar.OnGUI();
            SquadronHealth.OnGUI();
            SquadronDown.OnGUI();
        }
        catch (Exception ex)
        {
            Log.LogError("Title card failed: " + ex);
            TitleCard.Hide();
        }
    }

    private void OnDestroy()
    {
        IntroDirector.Abort();
        _harmony?.UnpatchSelf();
    }
}

internal enum EndEncounterAction
{
    Egress,
    Despawn,
}

// Read by BepInEx.ConfigurationManager through reflection; field names must match.
#pragma warning disable CS0649
internal sealed class ConfigurationManagerAttributes
{
    public Action<ConfigEntryBase> CustomDrawer;
    public bool? HideDefaultButton;
    public bool? HideSettingName;
    public bool? Browsable;
    public int? Order;
    public bool? IsAdvanced;
    public string Category;
}
#pragma warning restore CS0649
