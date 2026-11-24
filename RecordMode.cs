using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Developer tool: puts an ace in staged dogfights and films them, with nobody at the controls. Dormant unless the
/// game is started with -noahRecord, e.g.
///   NuclearOption.exe -autoHost -mission "Free Flight" -socket Offline -state SinglePlayer -noahRecord
///     [-noahOut &lt;folder&gt;] [-noahAircraft Revoker] [-noahBandit &lt;name&gt;] [-noahOnly &lt;scenario&gt;]
/// Each scenario spawns a tier 3 ace and an ordinary hostile AI fighter placed to provoke one manoeuvre (a tailgater,
/// a chaser, a crossing target, a missile shot from behind or head-on). The ace's own triggers decide what to fly;
/// nothing is forced. A two-shot camera frames the ace with the bandit (or the missile once it is close), frames are
/// saved on a fixed 30 fps game clock, and record-log.txt gets a second-by-second account: separation, who is behind
/// whom, missiles fired and whether they missed. The game quits when it has finished.
/// </summary>
internal static class RecordMode
{
    private const int Fps = 30;

    /// <summary>-noahNoVideo: run and log the scenarios without saving frames (airframe sweeps).</summary>
    private static bool NoVideo => _noVideoForced || Environment.GetCommandLineArgs().Any(a => a.Equals("-noahNoVideo", StringComparison.OrdinalIgnoreCase));
    private static bool _noVideoForced;
    private static float _lastFrameReal; // set when the disk fills, so the run carries on and logs instead of dying

    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => a.Equals("-noahRecord", StringComparison.OrdinalIgnoreCase));

    private static string Arg(string name, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return fallback;
    }

    private sealed class Scenario
    {
        public string Name;
        public float AceSpeed;
        public Vector3 BanditOffset;   // in the ace's frame: x right, y up, z forward (metres)
        public float BanditYaw;        // bandit heading relative to the ace's, degrees
        public float BanditSpeed;
        public float Seconds;
        public string Expect;
        public int Aces = 1;
        public int Tier = 3;
        public bool BanditNearWingman;
        public float Fuel = -1f;       // set the aces' fuel share at the start (retreat tests)
        public bool EmptyWeapons;      // empty every weapon station at the start (winchester test)
    }

    private static readonly Scenario[] Scenarios =
    {
        new() { Name = "Cobra-tailgater", AceSpeed = 125f, BanditOffset = new Vector3(0f, 0f, -700f), BanditYaw = 0f, BanditSpeed = 220f, Seconds = 26f,
                Expect = "Cobra as the bandit overruns, then a snap onto it" },
        new() { Name = "Herbst-chaser", AceSpeed = 140f, BanditOffset = new Vector3(150f, 0f, -3500f), BanditYaw = 0f, BanditSpeed = 175f, Seconds = 26f,
                Expect = "Herbst, nose round onto the chaser" },
        new() { Name = "Kulbit-crosser", AceSpeed = 170f, BanditOffset = new Vector3(4000f, 0f, 2500f), BanditYaw = -90f, BanditSpeed = 200f, Seconds = 24f,
                Expect = "Kulbit snap onto the crossing bandit and a missile shot" },
        new() { Name = "Missile-from-behind", AceSpeed = 200f, BanditOffset = new Vector3(0f, 200f, -7000f), BanditYaw = 0f, BanditSpeed = 260f, Seconds = 34f,
                Expect = "the bandit fires; defensive Cobra and flares, missile overshoots" },
        new() { Name = "Missile-head-on", AceSpeed = 200f, BanditOffset = new Vector3(300f, 0f, 14000f), BanditYaw = 180f, BanditSpeed = 230f, Seconds = 34f,
                Expect = "the bandit fires; post-stall break and flares, missile misses" },
        new() { Name = "Bell-pursuer", AceSpeed = 210f, BanditOffset = new Vector3(0f, -300f, -3000f), BanditYaw = 0f, BanditSpeed = 235f, Seconds = 32f,
                Expect = "Kvochur's Bell: zoom, hang, fall onto the pursuer below and shoot" },
        new() { Name = "YawBell-tailsitter-right", AceSpeed = 160f, BanditOffset = new Vector3(60f, 20f, -600f), BanditYaw = 0f, BanditSpeed = 165f, Seconds = 26f,
                Expect = "yaw-roll bell to the right: pitch and yaw, roll at the stall horn, come round behind the bandit" },
        new() { Name = "YawBell-tailsitter-left", AceSpeed = 160f, BanditOffset = new Vector3(-60f, 20f, -600f), BanditYaw = 0f, BanditSpeed = 165f, Seconds = 26f,
                Expect = "yaw-roll bell to the left" },
        new() { Name = "Merge-head-on", AceSpeed = 220f, BanditOffset = new Vector3(150f, 0f, 6000f), BanditYaw = 180f, BanditSpeed = 220f, Seconds = 40f,
                Expect = "head-on merge, then a turning fight: the ace should win it" },
        new() { Name = "Team-cover", Aces = 2, BanditNearWingman = true, AceSpeed = 200f, BanditOffset = new Vector3(0f, 0f, -700f), BanditYaw = 0f, BanditSpeed = 215f, Seconds = 32f,
                Expect = "the lead comes round onto the bandit sitting on the wingman (cover call)" },
        new() { Name = "Team-pincer", Aces = 2, AceSpeed = 210f, BanditOffset = new Vector3(0f, 0f, 7000f), BanditYaw = 180f, BanditSpeed = 210f, Seconds = 40f,
                Expect = "two aces split to the flanks of a head-on bandit and squeeze it" },
        new() { Name = "Formation-ingress", Aces = 4, AceSpeed = 220f, BanditOffset = new Vector3(0f, 0f, 40000f), BanditYaw = 180f, BanditSpeed = 200f, Seconds = 45f,
                Expect = "a tight four-ship holding slots inside ~70 m, running dark while its side tracks the bandit, breaking to engage at 25 km" },
        new() { Name = "Formation-break", Aces = 4, AceSpeed = 230f, BanditOffset = new Vector3(400f, 300f, 27000f), BanditYaw = 180f, BanditSpeed = 260f, Seconds = 50f,
                Expect = "a four-ship breaks formation into a fight without any two aces coming close" },
        new() { Name = "Bingo-retreat", AceSpeed = 230f, BanditOffset = new Vector3(0f, 0f, -3000f), BanditYaw = 0f, BanditSpeed = 260f, Seconds = 30f, Fuel = 0.1f,
                Expect = "bingo call and a run for the edge, with a radar missile over the shoulder at the chaser" },
        new() { Name = "Winchester-retreat", AceSpeed = 230f, BanditOffset = new Vector3(0f, 0f, -2500f), BanditYaw = 0f, BanditSpeed = 250f, Seconds = 15f, EmptyWeapons = true,
                Expect = "winchester call and a run for the edge" },
        new() { Name = "Tier1-merge", Tier = 1, AceSpeed = 220f, BanditOffset = new Vector3(150f, 0f, 6000f), BanditYaw = 180f, BanditSpeed = 220f, Seconds = 40f,
                Expect = "a tier 1 ace hunts too, but softer and less accurately" },
    };

    private static readonly TwoShotCamera Camera = new();
    private static Aircraft _ace, _bandit;
    private static Unit _missile;

    internal static void Start(MonoBehaviour host)
    {
        Plugin.Log.LogInfo("Record mode: waiting for a single player mission.");
        host.StartCoroutine(Run());
    }

    private static IEnumerator Run()
    {
        string outDir = Arg("-noahOut", Path.Combine(BepInEx.Paths.GameRootPath, "NOAcesHigh-recordings"));
        string aceName = Arg("-noahAircraft", "Revoker");
        string banditName = Arg("-noahBandit", aceName);
        string only = Arg("-noahOnly", "");
        int repeats = int.TryParse(Arg("-noahRepeat", "1"), out var r) ? Mathf.Clamp(r, 1, 10) : 1;
        Directory.CreateDirectory(outDir);
        Log(outDir, $"start {DateTime.Now:O}");

        while (!(GameManager.gameState == GameState.SinglePlayer && Session.InMission))
            yield return new WaitForSecondsRealtime(1f);
        Log(outDir, "mission loaded");
        yield return new WaitForSecondsRealtime(8f);

        // -noahAircraft may list several airframes ("Shrike,Ifrit,Agni"): the scenarios run once for each, the bandit
        // flying the same jet unless -noahBandit names one.
        var aceList = aceName.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        bool banditGiven = Environment.GetCommandLineArgs().Any(x => x.Equals("-noahBandit", StringComparison.OrdinalIgnoreCase));
        var aceFrame = FindAirframe(aceList.FirstOrDefault() ?? aceName);
        var banditFrame = banditGiven ? FindAirframe(banditName) ?? aceFrame : aceFrame;
        if (aceFrame == null)
        {
            Log(outDir, $"no airframe matching '{aceName}'; quitting");
            Application.Quit();
            yield break;
        }
        Log(outDir, $"ace airframe {aceFrame.unitName} ({AirframeChecklist.Key(aceFrame)}), vectoring={AirframeChecklist.HasThrustVectoring(aceFrame)}; " +
                    $"bandit {banditFrame.unitName}");

        FactionHQ banditHQ = Factions.HQFor(Side.BDF) ?? FactionRegistry.GetAllHQs().FirstOrDefault(h => h != null);
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("-noahSurvey", StringComparison.OrdinalIgnoreCase)))
        {
            yield return FlightTest.Survey(outDir, line => Log(outDir, line), only);
            Log(outDir, $"finished {DateTime.Now:O}");
            File.WriteAllText(Path.Combine(outDir, "ALL-DONE.txt"), DateTime.Now.ToString("O"));
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
            yield break;
        }
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("-noahLiveryShots", StringComparison.OrdinalIgnoreCase)))
        {
            var shotFrames = aceList.Select(FindAirframe).Where(x => x != null).ToList();
            yield return LiveryProbe.Shots(line => Log(outDir, line), outDir, shotFrames, banditHQ);
            Log(outDir, $"finished {DateTime.Now:O}");
            File.WriteAllText(Path.Combine(outDir, "ALL-DONE.txt"), DateTime.Now.ToString("O"));
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
            yield break;
        }
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("-noahLiveryProbe", StringComparison.OrdinalIgnoreCase)))
        {
            yield return LiveryProbe.Run(line => Log(outDir, line), only);
            Log(outDir, $"finished {DateTime.Now:O}");
            File.WriteAllText(Path.Combine(outDir, "ALL-DONE.txt"), DateTime.Now.ToString("O"));
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
            yield break;
        }
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("-noahHud", StringComparison.OrdinalIgnoreCase)))
        {
            yield return HudDemo(aceFrame, banditHQ, outDir);
            Log(outDir, $"finished {DateTime.Now:O}");
            File.WriteAllText(Path.Combine(outDir, "ALL-DONE.txt"), DateTime.Now.ToString("O"));
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
            yield break;
        }
        int index = 0;
        foreach (var frameName in aceList)
        {
            var thisAce = FindAirframe(frameName);
            if (thisAce == null) { Log(outDir, $"no airframe matching '{frameName}'; skipped"); continue; }
            var thisBandit = banditGiven ? banditFrame : thisAce;
            if (aceList.Count > 1) Log(outDir, $"=== {thisAce.unitName} vs {thisBandit.unitName}");
            // -noahTiers "0,1,2,3": run every scenario at each tier; 0 is the baseline, a plain stock-AI jet in the
            // ace's place (same spawn, same skill as the bandit, no mod behaviour at all).
            var tiers = Arg("-noahTiers", "").Split(',').Select(x => int.TryParse(x.Trim(), out var v) ? v : -1).Where(v => v >= 0 && v <= 3).ToList();
            if (tiers.Count == 0) tiers.Add(-1);
            for (int rep = 0; rep < repeats; rep++)
            foreach (var sc in Scenarios)
            {
                if (only.Length > 0 && !only.Split(',').Any(o => o.Trim().Length > 0 && sc.Name.IndexOf(o.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                foreach (int tier in tiers)
                {
                    if (tier == 0 && sc.Aces > 1) continue; // the baseline is a single jet
                    index++;
                    yield return RunScenario(sc, index, thisAce, thisBandit, banditHQ, outDir, tier);
                    yield return new WaitForSecondsRealtime(2f);
                }
            }
        }

        Log(outDir, $"finished {DateTime.Now:O}");
        File.WriteAllText(Path.Combine(outDir, "ALL-DONE.txt"), DateTime.Now.ToString("O"));
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit();
    }

    /// <summary>
    /// -noahHud: the player's side of an encounter, with the game's own view and UI left on. The local player joins
    /// the BDF in a jet; the threat gauge fills; a test squadron (with test-tone music from -noahMusic &lt;folder&gt;,
    /// if given) is launched at them 20 km out; the intro, red-chevron radio and the fight are filmed.
    /// </summary>
    private static IEnumerator HudDemo(AircraftDefinition airframe, FactionHQ side, string outDir)
    {
        string clip = "HUD-demo";
        if (!GameManager.GetLocalPlayer<Player>(out var player) || player == null)
        {
            Log(outDir, $"{clip}: no local player");
            yield break;
        }
        player.SetFaction(side, skipPreventJoin: true);
        yield return new WaitForSecondsRealtime(1f);
        var rot = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        var me = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(player, airframe.unitPrefab, null, 1f,
            new LiveryKey(airframe.aircraftParameters.GetRandomLiveryForFaction(side.faction)),
            new GlobalPosition(-6000f, 4500f, -6000f), rot, rot * Vector3.forward * 220f, null, side, "Player", 1f, 1f);
        float waited = 0f;
        while (Session.LocalAircraft == null && waited < 10f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        if (Session.LocalAircraft == null)
        {
            Log(outDir, $"{clip}: the player aircraft never arrived ({(me != null ? "spawned, not attached" : "spawn failed")})");
            yield break;
        }
        Log(outDir, $"{clip}: flying {Session.LocalAircraft.definition.unitName} for {Factions.Short(side)}");
        try
        {
            // What the game does after you pick an aircraft: drop the join menu and the full-screen map.
            SceneSingleton<GameplayUI>.i?.CloseJoinMenu();
            SceneSingleton<DynamicMap>.i?.Minimize();
        }
        catch (Exception ex) { Log(outDir, "could not close the menus: " + ex.Message); }

        // Test squadron, with test-tone music if a folder was given.
        string musicDir = Arg("-noahMusic", "");
        var def = new SquadronDef
        {
            Squadron = "Umbra Solis", Unit = "41st Unit", Designation = "PALA 3rd Liberation Air Wing", Title = "The Shade at Noon",
            Naming = "unit", IntroStyle = "unit", IntroShots = "headon", Formation = "diamond", Faction = "PALA", Tier = 3,
            Aircraft = new List<string> { AirframeChecklist.Key(airframe) }, Color = "#C9B26B", EmblemShape = "shield",
            Members = new List<MemberDef>
            {
                new() { Callsign = "Vesper 22", Name = "Arkady Ilyin", Lead = true }, new() { Callsign = "Vesper 99" },
                new() { Callsign = "Vesper 358" }, new() { Callsign = "Vesper 7" },
            },
        };
        if (musicDir.Length > 0 && Directory.Exists(musicDir))
        {
            def.Folder = musicDir;
            def.Music = new Dictionary<string, string>();
            foreach (var slot in Music.Slots)
                if (File.Exists(Path.Combine(musicDir, slot + ".wav"))) def.Music[slot] = slot + ".wav";
        }
        // -noahSquadron "Name": fly a handmade squadron (its own emblem, music, lines) instead of the test one.
        string wantSquadron = Arg("-noahSquadron", "");
        if (wantSquadron.Length > 0)
        {
            var handmade = Squadrons.Handmade.FirstOrDefault(s => (s.Squadron ?? s.Key).IndexOf(wantSquadron, StringComparison.OrdinalIgnoreCase) >= 0);
            if (handmade != null) def = handmade;
            else Log(outDir, $"{clip}: no handmade squadron matching '{wantSquadron}'; using the test one");
        }

        string frames = Path.Combine(outDir, clip);
        Directory.CreateDirectory(frames);
        Time.captureFramerate = Fps;
        var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        int total = Fps * 75, frame = 0;
        bool launched = false;
        float keepSpawn = Plugin.SpawnDistanceKm.Value;
        Plugin.SpawnDistanceKm.Value = 20f;
        string lastMusic = "";
        // -noahLayoutTest: after the intro, open UIPlus's HUD layout editor, move our two items, apply, and film the
        // result; the config is put back afterwards.
        bool layoutTest = Environment.GetCommandLineArgs().Any(a => a.Equals("-noahLayoutTest", StringComparison.OrdinalIgnoreCase));
        // -noahSettingsShot: open the F1 settings window on this mod from 26 s to 32 s, to film the settings page.
        bool settingsShot = Environment.GetCommandLineArgs().Any(a => a.Equals("-noahSettingsShot", StringComparison.OrdinalIgnoreCase));
        // -noahDownTest: at 30 s the player is credited with destroying every ace, to see the Squadron Down card and medals.
        bool downTest = Environment.GetCommandLineArgs().Any(a => a.Equals("-noahDownTest", StringComparison.OrdinalIgnoreCase));
        int layoutStep = 0;
        var keepLayout = (Plugin.ThreatBarX.Value, Plugin.ThreatBarY.Value, Plugin.ThreatBarScale.Value, Plugin.ThreatBarRotation.Value,
            Plugin.HealthBarsX.Value, Plugin.HealthBarsY.Value, Plugin.HealthBarsScale.Value, Plugin.HealthBarsRotation.Value, Plugin.HealthBarsWidth.Value);
        while (frame < total)
        {
            float t = frame / (float)Fps;
            if (downTest && t >= 30f && t < 30.1f) DestroyAces(outDir, clip);
            if (settingsShot && t >= 12f && t < 12.1f) ShowSettings(true);
            if (settingsShot && t >= 12.5f && t < 12.6f) ShowSettings(true, "Aces");
            if (settingsShot && t >= 15f && t < 15.1f) { LoadoutsTest.Add(); ShowSettings(true, "Loadouts"); }
            if (settingsShot && t >= 18f && t < 18.1f) { ShowSettings(false); LoadoutsTest.Remove(); }
            if (layoutTest)
            {
                if (layoutStep == 0 && t >= 40f) { layoutStep++; Log(outDir, $"{clip} t={t:0.0}s: layout editor {HudLayoutBridge.TestOpen()}"); }
                if (layoutStep == 1 && t >= 42f) { layoutStep++; Log(outDir, $"{clip} t={t:0.0}s: editing {HudLayoutBridge.Editing}; health bars moved"); HudLayoutBridge.TestEdit(false, 0.3f, 0.2f, 1.5f, -10f, 0.45f); Log(outDir, HudLayoutBridge.TestProbe()); }
                if (layoutStep == 2 && t >= 45f) { layoutStep++; HudLayoutBridge.TestEdit(true, 0.7f, 0.6f, 1.4f, 20f, 0f); Log(outDir, $"{clip} t={t:0.0}s: gauge moved; " + HudLayoutBridge.TestProbe()); }
                if (layoutStep == 3 && t >= 48f) { layoutStep++; HudLayoutBridge.TestApplyAndClose(); Log(outDir, $"{clip} t={t:0.0}s: applied, editing {HudLayoutBridge.Editing}; gauge {Plugin.ThreatBarX.Value:0.00},{Plugin.ThreatBarY.Value:0.00} x{Plugin.ThreatBarScale.Value} {Plugin.ThreatBarRotation.Value} deg; bars {Plugin.HealthBarsX.Value:0.00},{Plugin.HealthBarsY.Value:0.00} x{Plugin.HealthBarsScale.Value} {Plugin.HealthBarsRotation.Value} deg width {Plugin.HealthBarsWidth.Value}"); }
            }
            // 0-8 s: the threat gauge fills; 9 s: the squadron is launched at us.
            if (t < 8f && frame % 6 == 0) Encounters.AddThreat(side, player, Plugin.UnlockThreat.Value / 40f, "demo");
            if (!launched && t >= 9f)
            {
                launched = true;
                Log(outDir, $"{clip}: {Encounters.DebugSpawnNow(def)}");
            }
            string m = Music.Describe();
            if (m != lastMusic)
            {
                Log(outDir, $"{clip} t={t:0.0}s: {m}");
                lastMusic = m;
            }
            if (frame % Fps == 0)
                Log(outDir, $"{clip} t={t:0}s: {Encounters.StatusLine()}; intro {(IntroDirector.Playing ? (IntroDirector.CameraCut ? "cinematic" : "card") : IntroDirector.Active ? "waiting" : "idle")}");

            float realBefore = _lastFrameReal; _lastFrameReal = Time.realtimeSinceStartup; if (realBefore <= 0f) realBefore = _lastFrameReal;
            yield return new WaitForEndOfFrame();
            float hitch = _lastFrameReal - realBefore;
            if (hitch > 0.1f && t > 5f) Log(outDir, $"{clip} t={t:0.0}s: slow frame {hitch * 1000f:0} ms");
            if (!NoVideo)
            {
                if (tex.width != Screen.width || tex.height != Screen.height) tex.Reinitialize(Screen.width, Screen.height);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                tex.Apply(false);
                try { File.WriteAllBytes(Path.Combine(frames, $"{frame:0000}.jpg"), tex.EncodeToJPG(88)); }
                catch (IOException ex) { Log(outDir, $"{clip}: frame not saved ({ex.Message}); recording without video from here"); _noVideoForced = true; }
            }
            frame++;
            if (Session.LocalAircraft == null && t > 12f)
            {
                Log(outDir, $"{clip}: player aircraft lost at {t:0.0} s");
                total = Mathf.Min(total, frame + Fps * 2);
            }
        }
        Time.captureFramerate = 0;
        UnityEngine.Object.Destroy(tex);
        Plugin.SpawnDistanceKm.Value = keepSpawn;
        if (layoutTest)
        {
            (Plugin.ThreatBarX.Value, Plugin.ThreatBarY.Value, Plugin.ThreatBarScale.Value, Plugin.ThreatBarRotation.Value,
                Plugin.HealthBarsX.Value, Plugin.HealthBarsY.Value, Plugin.HealthBarsScale.Value, Plugin.HealthBarsRotation.Value, Plugin.HealthBarsWidth.Value) = keepLayout;
        }
        File.WriteAllText(Path.Combine(frames, "done.txt"), frame.ToString());
    }

    /// <summary>Opens or closes the F1 window; a search, once it is open, narrows it to matching settings.</summary>
    private static void ShowSettings(bool show, string search = null)
    {
        try
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out var info) || info.Instance == null) return;
            var t = info.Instance.GetType();
            if (search == null) t.GetProperty("DisplayingWindow")?.SetValue(info.Instance, show);
            else t.GetProperty("SearchString")?.SetValue(info.Instance, search);
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Settings shot: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>Record mode: every ace of the current squadron takes a killing blow credited to the local player.</summary>
    private static void DestroyAces(string outDir, string clip)
    {
        var enc = Encounters.Current;
        var me = Session.LocalAircraft;
        if (enc == null || me == null) { Log(outDir, $"{clip}: down test: no squadron or no player"); return; }
        int n = 0;
        foreach (var m in enc.Members.Where(x => x.Alive && x.Aircraft != null && !x.Aircraft.disabled).ToList())
        {
            try
            {
                m.Aircraft.RecordDamage(me.persistentID, 10000f);
                m.Aircraft.DisableUnit();
                n++;
            }
            catch (Exception ex) { Log(outDir, $"{clip}: down test: {m.Callsign}: {ex.Message}"); }
        }
        Log(outDir, $"{clip}: down test: hit {n} ace(s) for the player");
    }
    private static AircraftDefinition FindAirframe(string name) =>
        Encyclopedia.i.aircraft.FirstOrDefault(a => a != null && a.unitPrefab != null &&
            (a.unitName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);

    private static IEnumerator RunScenario(Scenario sc, int index, AircraftDefinition aceFrame, AircraftDefinition banditFrame,
        FactionHQ banditHQ, string outDir, int tierOverride = -1)
    {
        bool baseline = tierOverride == 0;
        string clip = $"{index:00}-{sc.Name}" + (tierOverride == 0 ? "-baseline" : tierOverride > 0 ? $"-tier{tierOverride}" : "");
        HideUi();
        int defencesBefore = PostStall.DefencesFlown, attacksBefore = PostStall.AttacksFlown, huntsBefore = PostStall.HuntsFlown, ripostesBefore = PostStall.RipostesFired;

        // The ace, somewhere different each time near the middle of the map, at about 4 km.
        var def = new SquadronDef
        {
            Squadron = "Test Aces",
            Aircraft = new List<string> { AirframeChecklist.Key(aceFrame) },
            Tier = tierOverride > 0 ? tierOverride : sc.Tier,
            Naming = "classic",
            Members = Enumerable.Range(1, Mathf.Max(1, sc.Aces)).Select(i => new MemberDef { Callsign = $"Ace {i}", Name = i == 1 ? "Test Pilot" : null, Lead = i == 1 }).ToList(),
            Formation = "echelonright",
        };
        var anchor = new GlobalPosition(index * 5000f - 12000f, 3000f, index * 3500f - 9000f);
        var enc = Encounters.LaunchForTest(def, anchor, banditHQ, sc.AceSpeed);
        if (enc == null || enc.Members.Count == 0)
        {
            Log(outDir, $"{clip}: ace spawn failed");
            yield break;
        }
        _ace = enc.Members[0].Aircraft;
        if (baseline)
        {
            // Same place, heading and speed; a plain AI jet instead of the ace.
            var at = _ace.GlobalPosition();
            var heading = _ace.transform.rotation;
            float speed0 = _ace.rb != null ? _ace.rb.velocity.magnitude : sc.AceSpeed;
            var side = enc.EnemyHQ;
            Encounters.DespawnActiveForTest();
            enc = null;
            yield return null;
            _ace = AceSpawner.SpawnPlain(aceFrame, side, at, heading, speed0, "Baseline");
            if (_ace == null) { Log(outDir, $"{clip}: baseline spawn failed"); yield break; }
            yield return new WaitForSeconds(0.2f);
        }
        foreach (var m in enc?.Members ?? new List<AceMember>())
        {
            if (m.Aircraft == null) continue;
            if (sc.Fuel >= 0f)
                foreach (var tank in m.Aircraft.GetFuelTanks()) tank.Refuel(sc.Fuel);
            if (sc.EmptyWeapons && m.Aircraft.weaponStations != null)
                foreach (var st in m.Aircraft.weaponStations) if (st != null && !st.Cargo) st.Ammo = 0;
        }
        var profile = AirframeProfiles.For(_ace);
        if (profile != null) Log(outDir, $"{clip}: {profile.Name} flies as {profile.Style} ({(profile.Measured ? "measured" : "estimated")}). {profile.Notes}");

        // The bandit, placed in the ace's frame.
        Vector3 fwd = _ace.transform.forward;
        fwd.y = 0f;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Transform anchorAce = sc.BanditNearWingman && enc != null && enc.Members.Count > 1 ? enc.Members[1].Aircraft.transform : _ace.transform;
        Vector3 pos = anchorAce.position + right * sc.BanditOffset.x + Vector3.up * sc.BanditOffset.y + fwd * sc.BanditOffset.z;
        Quaternion rot = Quaternion.LookRotation(Quaternion.AngleAxis(sc.BanditYaw, Vector3.up) * fwd, Vector3.up);
        _bandit = AceSpawner.SpawnPlain(banditFrame, banditHQ, pos.ToGlobalPosition(), rot, sc.BanditSpeed, "Bandit");
        if (_bandit == null)
        {
            Log(outDir, $"{clip}: bandit spawn failed");
            Encounters.DespawnActiveForTest();
            yield break;
        }
        Encounters.TestTarget = _bandit;
        // Both sides know where the other is from the start.
        try
        {
            banditHQ.RpcUpdateTrackingInfo(_ace.persistentID);
            (enc?.EnemyHQ ?? _ace.NetworkHQ)?.RpcUpdateTrackingInfo(_bandit.persistentID);
        }
        catch (Exception ex) { Log(outDir, "tracking update failed: " + ex.Message); }

        // Count missiles fired at the ace.
        var missiles = new List<Missile>();
        var mw = _ace.GetMissileWarningSystem();
        Action<MissileWarning.OnMissileWarning> onWarn = e =>
        {
            if (e.missile != null && !missiles.Contains(e.missile))
            {
                missiles.Add(e.missile);
                Log(outDir, $"{clip}: missile fired at the ace, {Vector3.Distance(e.missile.transform.position, _ace.transform.position):0} m out");
            }
        };
        if (mw != null) mw.onMissileWarning += onWarn;

        Log(outDir, $"{clip}: expect {sc.Expect}. Ace {_ace.speed * 3.6f:0} km/h at {_ace.radarAlt:0} m, bandit {Vector3.Distance(_ace.transform.position, _bandit.transform.position):0} m away.");
        EnsureCamera();

        string frames = Path.Combine(outDir, clip);
        Directory.CreateDirectory(frames);
        Time.captureFramerate = Fps;
        int total = Mathf.RoundToInt(sc.Seconds * Fps);
        int frame = 0;
        float onNose = 0f, onTail = 0f; // seconds the ace had the bandit near its nose / the bandit had the ace near its nose
        var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        string lostAt = null;
        string lastNear = null;
        float minSeparation = float.MaxValue;
        string minSepAt = "";
        // Every hit the ace takes, so a loss can be told apart: missile blast, bullets, the ground, or its own g.
        var hits = new List<string>();
        float clipStart = 0f;
        var hooked = new List<(UnitPart part, Action<UnitPart.OnApplyDamage> handler)>();
        if (_ace.partLookup != null)
            foreach (var part in _ace.partLookup)
            {
                if (part == null) continue;
                var p = part;
                Action<UnitPart.OnApplyDamage> h = e =>
                {
                    if (e.pierceDamage + e.blastDamage + e.fireDamage + e.impactDamage < 0.01f && !e.detached) return;
                    hits.Add($"t={Time.time - clipStart:0.0}s {p.name}: pierce {e.pierceDamage:0.#} blast {e.blastDamage:0.#} fire {e.fireDamage:0.#} impact {e.impactDamage:0.#}, hp {e.hitPoints:0}{(e.detached ? ", DETACHED" : "")}" +
                             $" (g {(_ace != null ? _ace.gForce : 0f):0.0}, bandit {(_ace != null && _bandit != null ? Vector3.Distance(_ace.transform.position, _bandit.transform.position) : 0f):0} m)");
                    if (hits.Count > 12) hits.RemoveAt(0);
                };
                p.onApplyDamage += h;
                hooked.Add((p, h));
            }
        clipStart = Time.time;
        while (frame < total)
        {
            EnsureCamera();
            if (frame % 60 == 0) HideUi();
            bool aceUp = _ace != null && !_ace.disabled;
            _missile = aceUp
                ? missiles.Where(m => m != null && !m.disabled)
                          .OrderBy(m => Vector3.Distance(m.transform.position, _ace.transform.position))
                          .FirstOrDefault(m => Vector3.Distance(m.transform.position, _ace.transform.position) < 2500f)
                : null;

            // Closest any two aces of the flight come (formation-break collisions).
            if (enc != null && enc.Members.Count > 1)
                for (int i = 0; i < enc.Members.Count; i++)
                    for (int j = i + 1; j < enc.Members.Count; j++)
                    {
                        var x = enc.Members[i].Aircraft; var y = enc.Members[j].Aircraft;
                        if (x == null || y == null || x.disabled || y.disabled) continue;
                        float sep = Vector3.Distance(x.transform.position, y.transform.position);
                        if (sep < minSeparation) { minSeparation = sep; minSepAt = $"{enc.Members[i].Callsign}/{enc.Members[j].Callsign} at t={frame / (float)Fps:0.0}s"; }
                    }

            if (aceUp && _bandit != null && !_bandit.disabled)
            {
                Vector3 toBandit = _bandit.transform.position - _ace.transform.position;
                float dist = toBandit.magnitude;
                float banditOffAceNose = Vector3.Angle(_ace.transform.forward, toBandit);
                float aceOffBanditNose = Vector3.Angle(_bandit.transform.forward, -toBandit);
                float dt = 1f / Fps;
                if (banditOffAceNose < 30f && dist < 4000f) onNose += dt;
                if (aceOffBanditNose < 30f && dist < 4000f) onTail += dt;
                if (frame % Fps == 0)
                    Log(outDir, $"{clip} t={frame / Fps,2}s: {dist,5:0} m, bandit {banditOffAceNose,3:0} deg off the ace's nose, ace {aceOffBanditNose,3:0} deg off the bandit's nose, " +
                                $"ace {_ace.speed * 3.6f,4:0} km/h {_ace.radarAlt,5:0} m hp {SquadronHealth.Health(_ace) * 100f:0}%, {PostStall.Describe(_ace)}" +
                                (_missile != null ? $", missile {Vector3.Distance(_missile.transform.position, _ace.transform.position):0} m" : "") +
                                FormationNote(enc) + (AceTactics.Dark ? ", DARK" : "") + (Encounters.IsRetreating(_ace) ? ", RETREATING" : ""));
                if (frame % Fps == 0) Log(outDir, $"{clip}   probe: {Probe(missiles)}");
            }
            // The last thing near the ace before it went: which missile, how close, how fast it was closing.
            if (aceUp)
            {
                foreach (var m in missiles)
                {
                    if (m == null || m.disabled) continue;
                    float d = Vector3.Distance(m.transform.position, _ace.transform.position);
                    if (d < 1500f) lastNear = $"{m.GetSeekerType()} missile {d:0} m out, closing {-Vector3.Dot((m.rb != null ? m.rb.velocity : Vector3.zero) - _ace.rb.velocity, (m.transform.position - _ace.transform.position).normalized):0} m/s, at t={frame / (float)Fps:0.0}s";
                }
            }

            yield return new WaitForEndOfFrame();
            if (!NoVideo)
            {
                if (tex.width != Screen.width || tex.height != Screen.height) tex.Reinitialize(Screen.width, Screen.height);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                tex.Apply(false);
                File.WriteAllBytes(Path.Combine(frames, $"{frame:0000}.jpg"), tex.EncodeToJPG(88));
            }
            frame++;
            if (lostAt == null && (_ace == null || _ace.disabled))
            {
                lostAt = $"ace lost at {frame / (float)Fps:0.0} s";
                Log(outDir, $"{clip}: ace lost; last missile close to it: {lastNear ?? "none within 1.5 km"}");
                string pilot = "?";
                try { var pl = _ace?.pilots != null && _ace.pilots.Length > 0 ? _ace.pilots[0] : null; pilot = pl == null ? "none" : pl.dead ? "dead" : pl.ejected ? "ejected" : "alive"; } catch { /* gone */ }
                Log(outDir, $"{clip}: pilot {pilot}; last hits: {(hits.Count > 0 ? string.Join(" | ", hits) : "none recorded")}");
                total = Mathf.Min(total, frame + Fps); // a second of the aftermath
            }
        }
        Time.captureFramerate = 0;
        UnityEngine.Object.Destroy(tex);
        if (mw != null) mw.onMissileWarning -= onWarn;
        foreach (var (part, handler) in hooked) if (part != null) part.onApplyDamage -= handler;

        Log(outDir, $"{clip} RESULT: {(lostAt ?? "lead survived")}, aces alive {(enc != null ? enc.AliveCount : _ace != null && !_ace.disabled ? 1 : 0)}/{(enc != null ? enc.Members.Count : 1)}; offensive manoeuvres {PostStall.AttacksFlown - attacksBefore}, " +
                    $"defensive {PostStall.DefencesFlown - defencesBefore}, hunts {PostStall.HuntsFlown - huntsBefore}, ripostes {PostStall.RipostesFired - ripostesBefore}; missiles fired at the ace {missiles.Count}; " +
                    $"ace had the bandit within 30 deg of its nose for {onNose:0.0} s, bandit had the ace for {onTail:0.0} s; " +
                    $"bandit {(_bandit == null || _bandit.disabled ? "down" : "alive")}." +
                    (minSeparation < float.MaxValue ? $" Closest two aces came: {minSeparation:0} m ({minSepAt})." : ""));
        File.WriteAllText(Path.Combine(frames, "done.txt"), frame.ToString());

        Encounters.TestTarget = null;
        Encounters.DespawnActiveForTest();
        if (baseline) Despawn(_ace);
        Despawn(_bandit);
        _ace = _bandit = null;
        _missile = null;
    }

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, Unit> AiTarget =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, Unit>("currentTarget");

    /// <summary>Diagnostics: missiles in the air at the ace, the ace's own AI target and shots left, radar.</summary>
    private static string Probe(List<Missile> missiles)
    {
        var parts = new List<string>();
        foreach (var m in missiles)
        {
            if (m == null || m.disabled) continue;
            Vector3 rel = m.transform.position - _ace.transform.position;
            float closing = -Vector3.Dot((m.rb != null ? m.rb.velocity : Vector3.zero) - _ace.rb.velocity, rel.normalized);
            parts.Add($"{m.GetSeekerType()} {rel.magnitude:0} m closing {closing:0} ({Vector3.Angle(_ace.transform.forward, rel):0} deg off nose)");
        }
        string target = "?";
        try
        {
            var state = _ace.pilots != null && _ace.pilots.Length > 0 ? _ace.pilots[0]?.currentState : null;
            target = state is AIPilotCombatModes cm ? (AiTarget(cm) is { } t ? t.unitName : "none") : state?.GetType().Name ?? "no pilot";
        }
        catch { /* unknown */ }
        int aceMsl = 0, banditMsl = 0;
        foreach (var s in _ace.weaponStations ?? new List<WeaponStation>()) if (s?.WeaponInfo != null && s.WeaponInfo.missile && !s.WeaponInfo.bomb) aceMsl += s.Ammo;
        foreach (var s in _bandit?.weaponStations ?? new List<WeaponStation>()) if (s?.WeaponInfo != null && s.WeaponInfo.missile && !s.WeaponInfo.bomb) banditMsl += s.Ammo;
        return $"ace AI target {target}, radar {(_ace.radar != null && _ace.radar.activated ? "on" : "off")}, missiles ace {aceMsl} / bandit {banditMsl}" +
               (parts.Count > 0 ? "; inbound: " + string.Join("; ", parts) : "");
    }

    /// <summary>Slot errors of the wingmen while the flight holds formation.</summary>
    private static string FormationNote(ActiveEncounter enc)
    {
        if (enc == null || !enc.FormationHeld || enc.Members.Count < 2) return "";
        var parts = new List<string>();
        foreach (var m in enc.Members)
        {
            if (m.Lead || !m.Alive || m.Aircraft == null) continue;
            if (!Encounters.TryGetFormationSlot(m.Aircraft, out var leader, out var offset)) continue;
            var slot = Formations.SlotPosition(leader.GlobalPosition(), Formations.FlatForward(leader, leader.transform.forward), offset);
            parts.Add($"{(slot - m.Aircraft.GlobalPosition()).magnitude:0}(thr {m.Aircraft.GetInputs().throttle:0.00} brk {m.Aircraft.GetInputs().brake:0.0} aoa {Mathf.Atan2(-m.Aircraft.transform.InverseTransformDirection(m.Aircraft.rb.velocity).y, m.Aircraft.transform.InverseTransformDirection(m.Aircraft.rb.velocity).z) * Mathf.Rad2Deg:0}, {m.Aircraft.speed * 3.6f:0}, {m.Aircraft.radarAlt:0} m)");
        }
        var lead = enc.LeadMember?.Aircraft;
        string leadNote = lead != null ? $"; lead thr {lead.GetInputs().throttle:0.00} {lead.speed * 3.6f:0} km/h, state {lead.pilots?[0]?.currentState?.GetType().Name}" : "";
        return parts.Count > 0 ? $", formation slot errors {string.Join("/", parts)} m{leadNote}" : "";
    }

    private static void Despawn(Aircraft a)
    {
        if (a == null) return;
        try
        {
            var objects = NetworkManagerNuclearOption.i?.ServerObjectManager;
            if (objects != null && a.Identity != null) objects.Destroy(a.Identity, true);
            else UnityEngine.Object.Destroy(a.gameObject);
        }
        catch { /* gone already */ }
    }

    private static void Log(string dir, string line)
    {
        Plugin.Log.LogInfo("Record mode: " + line);
        try { File.AppendAllText(Path.Combine(dir, "record-log.txt"), $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
        catch { /* logging only */ }
    }

    /// <summary>The game's menus and HUD stay out of the footage.</summary>
    private static void HideUi()
    {
        foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            if (canvas != null && canvas.isRootCanvas) canvas.enabled = false;
    }

    private static void EnsureCamera()
    {
        var cam = SceneSingleton<CameraStateManager>.i;
        if (cam != null && cam.currentState != Camera) cam.SwitchState(Camera);
    }

    /// <summary>
    /// Two-shot: looks across the line between the ace and its opponent (the missile once it is close, else the
    /// bandit), far enough back to frame both, with the side and distance smoothed so it does not jump.
    /// </summary>
    private sealed class TwoShotCamera : CameraBaseState
    {
        private Vector3 _side = Vector3.right;
        private float _dist = 80f;
        private bool _primed;

        public override void EnterState(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(Datum.origin);
            cam.transform.SetParent(cam.cameraPivot);
            cam.transform.localPosition = Vector3.zero;
            cam.cameraVelocity = Vector3.zero;
            cam.mainCamera.nearClipPlane = 1f;
            _primed = false;
            try { FlightHud.EnableCanvas(enable: false); } catch { /* no HUD yet */ }
        }

        public override void LeaveState(CameraStateManager cam) { }

        public override void FixedUpdateState(CameraStateManager cam) { }

        public override void UpdateState(CameraStateManager cam)
        {
            var a = _ace;
            if (a == null || a.disabled) return;
            Vector3 p = a.transform.position;
            float size = Mathf.Max(a.definition.length, a.definition.width, 10f);
            Unit other = _missile != null && !_missile.disabled ? _missile : _bandit != null && !_bandit.disabled ? _bandit : null;

            // Over the shoulder: behind the ace on the far side from its opponent, looking past it at the opponent,
            // so the ace stays big in the foreground with the bandit, missiles and flares beyond it.
            Vector3 toOther = other != null ? other.transform.position - p : Formations.FlatForward(a, a.transform.forward);
            Vector3 dir = toOther.sqrMagnitude > 1f ? toOther.normalized : a.transform.forward;
            float k = _primed ? 1f - Mathf.Exp(-Time.deltaTime * 2f) : 1f;
            _side = Vector3.Slerp(_side, dir, k).normalized; // _side holds the smoothed look direction here
            _primed = true;
            Vector3 look = _side;
            Vector3 across = Vector3.Cross(Vector3.up, look);
            if (across.sqrMagnitude < 1e-3f) across = a.transform.right;
            across.Normalize();

            Vector3 camPos = p - look * size * 4.5f + Vector3.up * size * 1.1f + across * size * 1.4f;
            cam.cameraPivot.localPosition = camPos.ToGlobalPosition().AsVector3();
            cam.transform.localPosition = Vector3.zero;
            Vector3 aim = p + look * size * 8f;
            cam.transform.rotation = Quaternion.LookRotation((aim - cam.transform.position).normalized, Vector3.up);
            cam.mainCamera.fieldOfView = 60f;
            cam.cameraVelocity = Vector3.zero;
        }
    }
}
