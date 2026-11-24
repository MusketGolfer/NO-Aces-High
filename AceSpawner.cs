using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NuclearOption.SavedMission;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NOAcesHigh;

/// <summary>Turns a (possibly empty) squadron definition into a live ace flight near the player.</summary>
internal static class AceSpawner
{
    private const float SpawnSpeed = 230f;      // m/s, fast cruise
    internal const int MaxMembers = 16;
    private static readonly float[] TierSkill = { 1.5f, 2.0f, 2.5f };
    private static readonly float[] TierBravery = { 0.8f, 0.9f, 1.0f };

    // Accent colours for squadrons that do not set one: muted military tones plus a few loud ones.
    private static readonly Color[] Palette =
    {
        new(0.75f, 0.18f, 0.16f), new(0.85f, 0.65f, 0.20f), new(0.60f, 0.65f, 0.70f), new(0.20f, 0.45f, 0.80f),
        new(0.55f, 0.20f, 0.65f), new(0.25f, 0.62f, 0.38f), new(0.90f, 0.45f, 0.15f), new(0.85f, 0.85f, 0.88f),
    };

    /// <summary>Launches aces against <paramref name="targetHQ"/>'s side, hunting <paramref name="player"/>.</summary>
    internal static ActiveEncounter Spawn(SquadronDef def, Aircraft player, FactionHQ targetHQ, int encounterIndex)
    {
        if (player == null) return null;
        return SpawnAt(def, player.GlobalPosition(), player.Player, targetHQ, encounterIndex, null, null);
    }

    /// <summary>
    /// The spawn itself, around <paramref name="anchor"/> (normally the hunted pilot). <paramref name="distance"/> and
    /// <paramref name="speed"/> override the spawn distance setting and the fast-cruise spawn speed (record mode uses them).
    /// </summary>
    internal static ActiveEncounter SpawnAt(SquadronDef def, GlobalPosition anchor, NuclearOption.Networking.Player targetPlayer,
        FactionHQ targetHQ, int encounterIndex, float? distance, float? speed)
    {
        try
        {
            return SpawnInner(def, anchor, targetPlayer, targetHQ, encounterIndex, distance, speed);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("Ace spawn failed: " + ex);
            return null;
        }
    }

    /// <summary>Record mode: one ordinary AI aircraft (not an ace) with an air-to-air standard loadout.</summary>
    internal static Aircraft SpawnPlain(AircraftDefinition airframe, FactionHQ hq, GlobalPosition pos, Quaternion rotation, float speed, string name)
    {
        try
        {
            Loadout loadout = PickLoadout(new SquadronDef(), airframe, hq, out float fuel);
            var livery = new LiveryKey(airframe.aircraftParameters.GetRandomLiveryForFaction(hq.faction));
            return NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, airframe.unitPrefab, loadout, fuel, livery, pos, rotation,
                rotation * Vector3.forward * speed, null, hq, name, 2f, 1f);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("Ace spawn failed: " + ex);
            return null;
        }
    }

    /// <summary>The side that flies a squadron against <paramref name="targetHQ"/>: its own faction if it names one, else the opponent.</summary>
    internal static FactionHQ AceSideFor(SquadronDef def, FactionHQ targetHQ)
    {
        FactionHQ own = Factions.HQFor(Factions.Parse(def.Faction));
        return own != null && own != targetHQ ? own : Factions.Opponent(targetHQ);
    }

    private static ActiveEncounter SpawnInner(SquadronDef def, GlobalPosition anchor, NuclearOption.Networking.Player targetPlayer,
        FactionHQ targetHQ, int encounterIndex, float? distanceOverride, float? speedOverride)
    {
        if (targetHQ == null) return null;
        FactionHQ enemy = AceSideFor(def, targetHQ);
        if (enemy == null)
        {
            Plugin.Log.LogWarning("No enemy faction to launch aces for.");
            return null;
        }
        Side side = Factions.Of(enemy);

        AircraftDefinition airframe = PickAirframe(def);
        if (airframe == null)
        {
            Plugin.Log.LogWarning(def.Handmade
                ? $"Squadron '{def.Squadron}' has no installed airframe ({string.Join(", ", def.Aircraft ?? new List<string>())}); skipped."
                : "No airframes are allowed in the checklist; no aces launched.");
            return null;
        }

        int maxTier = Mathf.Clamp(Plugin.MaxTier.Value, 1, 3);
        int tier = Mathf.Clamp(def.Tier ?? Mathf.Min(maxTier, Random.Range(1, maxTier + 1) + encounterIndex), 1, maxTier);
        if (def.Tier.HasValue) tier = Mathf.Clamp(def.Tier.Value, 1, 3);

        // Naming: a handmade squadron that names itself and picks no scheme keeps the plain classic style, so a
        // random scheme does not bolt an organisation onto it.
        int memberCount = def.Members is { Count: > 0 } ? Mathf.Min(def.Members.Count, MaxMembers) : Mathf.Clamp(tier + Random.Range(0, 2), 2, 4);
        memberCount = Mathf.Clamp(memberCount, 1, SettingsMenu.MaxAcesPerSquadron?.Value ?? MaxMembers);
        // A comma-separated list in any of these fields means "one of these": picked here, once per flight.
        string naming = Variants.Pick(def.Naming), colour = Variants.Pick(def.Color), shapeText = Variants.Pick(def.EmblemShape),
            cardText = Variants.Pick(def.IntroStyle), shotsText = Variants.Pick(def.IntroShots), formationText = Variants.Pick(def.Formation);
        NamingScheme scheme = def.Handmade && string.IsNullOrWhiteSpace(naming) && !string.IsNullOrWhiteSpace(def.Squadron)
            ? NamingScheme.Classic
            : Nomenclature.ResolveScheme(naming, side);
        SquadronIdentity id = Nomenclature.Create(scheme, enemy, memberCount);

        var enc = new ActiveEncounter
        {
            Def = def,
            Side = side,
            TargetHQ = targetHQ,
            TargetPlayer = targetPlayer,
            Scheme = id.Scheme,
            SquadronName = Or(def.Squadron, id.Name),
            Title = Or(def.Title, id.Title),
            Unit = Or(def.Unit, id.Unit),
            Designation = Or(def.Designation, id.Designation),
            Tier = tier,
            EnemyHQ = enemy,
            StartTime = Time.time,
        };
        enc.Colour = Variants.IsRandom(colour) ? Color.HSVToRGB(Random.value, Random.Range(0.45f, 0.85f), Random.Range(0.75f, 1f))
            : !string.IsNullOrWhiteSpace(colour) && ColorUtility.TryParseHtmlString(colour, out var c)
            ? c
            : id.Colour ?? SidePalette(side);

        EmblemShape shape = Variants.IsRandom(shapeText) ? Variants.Any<EmblemShape>()
            : Variants.TryParse(shapeText, out EmblemShape s) && s != EmblemShape.Match ? s : Plugin.EmblemStyle.Value;
        if (shape == EmblemShape.Match) shape = Nomenclature.ShapeFor(id.Scheme, Emblem.Seed(enc.SquadronName));
        enc.Shape = shape;
        enc.NetId = NetSync.NewEncounterId();
        enc.Emblem = def.LoadedEmblem ?? Emblem.Generate(enc.SquadronName, enc.Colour, shape, side);

        CardStyle card = Variants.IsRandom(cardText) ? Variants.Any<CardStyle>()
            : Variants.TryParse(cardText, out CardStyle cs) && cs != CardStyle.Match ? cs : Plugin.CardStyleSetting.Value;
        enc.Card = card == CardStyle.Match ? Nomenclature.CardFor(id.Scheme) : card;
        enc.Shots = Variants.IsRandom(shotsText) ? IntroShots.Random // picked for the flight size when the intro plays
            : Variants.TryParse(shotsText, out IntroShots shots) && shots != IntroShots.Random ? shots : Plugin.ShotsSetting.Value;

        List<MemberDef> members = BuildMembers(def, memberCount, id);
        enc.Formation = Formations.Resolve(formationText, members.Count);
        enc.FormationHeld = Plugin.HoldFormation.Value && members.Count > 1;
        LiveryKey livery = ResolveLivery(def, airframe, enemy);

        // Come from the enemy's side of the map: the direction of their nearest airbase, else anywhere.
        GlobalPosition playerPos = anchor;
        Vector3 fromDir = DirectionToNearestBase(enemy, playerPos);
        float distance = distanceOverride ?? Plugin.SpawnDistanceKm.Value * 1000f;
        float altitude = Mathf.Clamp(playerPos.y + 1000f, 3000f, 9000f);
        // Not on top of the hunted side's ships, air defences, bases or aircraft (a set test distance is left alone).
        if (distanceOverride == null) SafeSpawn(targetHQ, playerPos, ref fromDir, ref distance);
        GlobalPosition leadPos = playerPos + fromDir * distance;
        leadPos = new GlobalPosition(leadPos.x, altitude, leadPos.z);
        Quaternion heading = Quaternion.LookRotation(-fromDir, Vector3.up);
        Vector3 velocity = heading * Vector3.forward * (speedOverride ?? SpawnSpeed);

        var spawner = NetworkSceneSingleton<Spawner>.i;
        int wingman = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double tLoadout = 0, tSpawn = 0, tFlares = 0;
        for (int i = 0; i < members.Count; i++)
        {
            MemberDef m = members[i];
            // Spawn already in formation: the lead in slot 0, wingmen in squadron order after it.
            int slot = m.Lead ? 0 : ++wingman;
            GlobalPosition pos = Formations.SlotPosition(leadPos, -fromDir, Formations.Offset(enc.Formation, slot));
            // A pilot may fly its own airframe (first installed of its list); otherwise the squadron's.
            AircraftDefinition frame = MemberAirframe(m) ?? airframe;
            LiveryKey memberLivery = m.Livery == null && frame == airframe ? livery : ResolveLivery(def, frame, enemy, m);
            double t0 = sw.Elapsed.TotalMilliseconds;
            Loadout loadout = PickLoadout(def, frame, enemy, out float fuel, m);
            double t1 = sw.Elapsed.TotalMilliseconds;
            tLoadout += t1 - t0;
            float skill = (m.Skill ?? TierSkill[tier - 1]) * Plugin.SkillMultiplier.Value;
            float bravery = Mathf.Clamp01(TierBravery[tier - 1] * Plugin.SkillMultiplier.Value);

            Aircraft aircraft = spawner.SpawnAircraft(null, frame.unitPrefab, loadout, fuel, memberLivery, pos, heading, velocity,
                null, enemy, m.Callsign, skill, bravery);
            double t2 = sw.Elapsed.TotalMilliseconds;
            tSpawn += t2 - t1;
            if (aircraft == null) continue;
            AceFlight.BoostFlares(aircraft, Plugin.FlareMultiplier.Value);
            tFlares += sw.Elapsed.TotalMilliseconds - t2;
            enc.Members.Add(new AceMember { Aircraft = aircraft, Callsign = m.Callsign, PilotName = m.Name, Lead = m.Lead });
        }

        Plugin.Log.LogInfo($"Spawn timing: {members.Count} aces, loadouts {tLoadout:0} ms, SpawnAircraft {tSpawn:0} ms, flares {tFlares:0} ms.");
        if (enc.Members.Count == 0) return null;
        if (!enc.Members.Any(x => x.Lead)) enc.Members[0].Lead = true;
        return enc;
    }

    /// <summary>A colour from the side's palette (BDF blues and greys, PALA reds and olives), else the general one.</summary>
    private static Color SidePalette(Side side)
    {
        Color[] pool = side switch { Side.BDF => Factions.BdfPalette, Side.PALA => Factions.PalaPalette, _ => Palette };
        return pool[Random.Range(0, pool.Length)];
    }

    /// <summary>
    /// Moves the spawn point off the hunted side's forces: no ship within 30 km (carriers and destroyers carry long-range
    /// SAMs), no air-defence vehicle or emplacement within 15 km, no aircraft within 6 km. Tries the planned bearing first, then
    /// swings round it 20 deg at a time both ways, at the planned distance and a little further and nearer; if nowhere is
    /// clear, takes the spot farthest from trouble.
    /// </summary>
    private static void SafeSpawn(FactionHQ hunted, GlobalPosition anchor, ref Vector3 dir, ref float distance)
    {
        try
        {
            var threats = new List<(Vector3 pos, float radius, string what)>();
            foreach (var u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || u.NetworkHQ != hunted) continue;
                float r;
                if (u is Ship) r = 30000f;
                else if (u is Aircraft) r = 6000f;
                else if ((u is GroundVehicle || u is Building) && HasAntiAir(u)) r = 15000f;
                else continue; // trucks, depots, scenery, missiles...
                Vector3 p = u.GlobalPosition().AsVector3();
                threats.Add((new Vector3(p.x, 0f, p.z), r, u.definition != null ? u.definition.unitName : u.name));
            }
            if (threats.Count == 0) return;

            Vector3 a0 = anchor.AsVector3();
            a0.y = 0f;
            float Clearance(Vector3 d, float dist, out string near)
            {
                Vector3 at = a0 + d * dist;
                float worst = float.MaxValue;
                near = null;
                foreach (var t in threats)
                {
                    float c = (at - t.pos).magnitude - t.radius;
                    if (c < worst) { worst = c; near = t.what; }
                }
                return worst;
            }

            float first = Clearance(dir, distance, out string firstNear);
            if (first >= 0f) return;
            Vector3 bestDir = dir;
            float bestDist = distance, bestClear = first;
            foreach (float scale in new[] { 1f, 1.25f, 0.8f })
            for (int step = 0; step <= 9; step++)
            foreach (int sign in new[] { 1, -1 })
            {
                if (step == 0 && sign < 0) continue;
                Vector3 d = Quaternion.Euler(0f, sign * step * 20f, 0f) * dir;
                float dist = distance * scale;
                float c = Clearance(d, dist, out _);
                if (c >= 0f)
                {
                    Plugin.Log.LogInfo($"Spawn point moved {sign * step * 20:0} deg round and to {dist / 1000f:0} km: the planned one was {-first / 1000f:0.0} km inside the cover of a {firstNear}.");
                    dir = d;
                    distance = dist;
                    return;
                }
                if (c > bestClear) { bestClear = c; bestDir = d; bestDist = dist; }
            }
            Plugin.Log.LogInfo($"No spawn point clear of the hunted side's forces; using the least covered ({-bestClear / 1000f:0.0} km inside).");
            dir = bestDir;
            distance = bestDist;
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Spawn safety check failed, spawning at the planned point: " + ex.Message); }
    }

    private static bool HasAntiAir(Unit u)
    {
        foreach (var st in u.weaponStations)
        {
            var info = st?.WeaponInfo;
            if (info != null && info.effectiveness.antiAir >= 0.3f) return true;
        }
        return false;
    }

    private static Vector3 DirectionToNearestBase(FactionHQ enemy, GlobalPosition from)
    {
        Vector3 best = Vector3.zero;
        float bestDist = float.MaxValue;
        foreach (var airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase == null || airbase.CurrentHQ != enemy) continue;
            Vector3 d = airbase.transform.GlobalPosition() - from;
            d.y = 0f;
            if (d.magnitude < bestDist && d.sqrMagnitude > 1f)
            {
                bestDist = d.magnitude;
                best = d.normalized;
            }
        }
        if (best == Vector3.zero)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            best = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
        }
        return best;
    }

    /// <summary>A member's own airframe: the first installed of its "aircraft" list (exact, then partial name); null if none.</summary>
    private static AircraftDefinition MemberAirframe(MemberDef m)
    {
        if (m.Aircraft is not { Count: > 0 }) return null;
        var all = Encyclopedia.i.aircraft.Where(a => a != null && a.unitPrefab != null).ToList();
        foreach (var w in m.Aircraft)
        {
            var match = all.FirstOrDefault(a => Matches(a, w)) ??
                        all.FirstOrDefault(a => !string.IsNullOrWhiteSpace(w) && (a.unitName ?? "").IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null) return match;
        }
        Plugin.Log.LogWarning($"Ace {m.Callsign}: none of its aircraft ({string.Join(", ", m.Aircraft)}) is installed; flying the squadron's.");
        return null;
    }

    private static AircraftDefinition PickAirframe(SquadronDef def)
    {
        var all = Encyclopedia.i.aircraft.Where(a => a != null && a.unitPrefab != null).ToList();
        if (def.Aircraft is { Count: > 0 })
        {
            foreach (var wanted in def.Aircraft)
            {
                var match = all.FirstOrDefault(a => Matches(a, wanted)) ??
                            all.FirstOrDefault(a => !string.IsNullOrWhiteSpace(wanted) && (a.unitName ?? "").IndexOf(wanted.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null) return match;
            }
            return def.Handmade ? null : PickAllowed(all);
        }
        return PickAllowed(all);
    }

    private static AircraftDefinition PickAllowed(List<AircraftDefinition> all)
    {
        var allowed = all.Where(AirframeChecklist.IsAirframeAllowed).ToList();
        return allowed.Count == 0 ? null : allowed[Random.Range(0, allowed.Count)];
    }

    private static bool Matches(AircraftDefinition a, string wanted) =>
        string.Equals(AirframeChecklist.Key(a), wanted, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a.unitName, wanted, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a.name, wanted, StringComparison.OrdinalIgnoreCase);

    private static List<MemberDef> BuildMembers(SquadronDef def, int count, SquadronIdentity id)
    {
        var list = new List<MemberDef>();
        if (def.Members is { Count: > 0 })
        {
            foreach (var m in def.Members.OrderByDescending(x => x.Lead).Take(count)) // the lead always flies
                list.Add(new MemberDef { Callsign = m.Callsign, Name = m.Name, Lead = m.Lead, Skill = m.Skill, Aircraft = m.Aircraft, Loadout = m.Loadout, Livery = m.Livery });
        }
        else
        {
            for (int i = 0; i < count; i++) list.Add(new MemberDef());
        }

        // Missing callsigns follow the naming scheme: Viper 1, Vermilion II, Vesper 99, Sapsan 02, Nomad 202...
        for (int i = 0; i < list.Count; i++)
        {
            string generated = i < id.Callsigns.Count ? id.Callsigns[i] : $"{id.Name} {i + 1}";
            list[i].Callsign = Or(list[i].Callsign, generated);
            list[i].Name = Or(list[i].Name, RandomNames.PilotName());
        }
        if (!list.Any(m => m.Lead)) list[0].Lead = true;
        return list;
    }

    private static string Or(string preferred, string fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback ?? "" : preferred;

    /// <summary>Installed airframe this squadron would fly, or null (the squadron menu shows this).</summary>
    internal static AircraftDefinition InstalledAirframe(SquadronDef def)
    {
        if (def.Aircraft is not { Count: > 0 }) return null;
        var all = Encyclopedia.i.aircraft.Where(a => a != null && a.unitPrefab != null).ToList();
        // Exact key or name first; then a name that contains what was written ("Revoker" finds "FS-12 Revoker").
        return def.Aircraft.Select(w => all.FirstOrDefault(a => Matches(a, w)) ??
                                        all.FirstOrDefault(a => !string.IsNullOrWhiteSpace(w) && (a.unitName ?? "").IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                           .FirstOrDefault(a => a != null);
    }

    private static LiveryKey ResolveLivery(SquadronDef def, AircraftDefinition airframe, FactionHQ enemy, MemberDef member = null)
    {
        var liveries = airframe.aircraftParameters.liveries;
        // The pilot's own, then the squadron's: the entry made for this jet, else one for any jet.
        JObject l = Liveries.For(member?.Livery, airframe) ?? Liveries.For(def.Livery, airframe);
        if (l == null && (member?.Livery ?? def.Livery) != null)
            Plugin.Log.LogInfo($"Squadron '{def.Squadron}': no livery listed for the {airframe.unitName}; using one of its own.");
        if (l != null)
        {
            try
            {
                if (l.TryGetValue("workshop", out var ws))
                {
                    Liveries.WorkshopAircraft(ws.ToString(), out bool installed);
                    if (installed) return new LiveryKey(LiveryKey.KeyType.Workshop, 0, ws.ToString());
                    Plugin.Log.LogWarning($"Squadron '{def.Squadron}': workshop livery {ws} is not installed (subscribe to it on the Steam Workshop); using a random one.");
                    return new LiveryKey(airframe.aircraftParameters.GetRandomLiveryForFaction(enemy.faction));
                }
                if (l.TryGetValue("appdata", out var ad))
                    return new LiveryKey(LiveryKey.KeyType.AppData, 0, ad.ToString());
                if (l.TryGetValue("builtin", out var bi))
                {
                    if (bi.Type == JTokenType.Integer && (int)bi >= 0 && (int)bi < liveries.Count)
                        return new LiveryKey((int)bi);
                    int byName = liveries.FindIndex(x => string.Equals(x.name, bi.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (byName >= 0) return new LiveryKey(byName);
                    Plugin.Log.LogWarning($"Squadron '{def.Squadron}': built-in livery '{bi}' not found on {airframe.unitName}; using a random one.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Squadron '{def.Squadron}': bad livery entry ({ex.Message}); using a random one.");
            }
        }
        return new LiveryKey(airframe.aircraftParameters.GetRandomLiveryForFaction(enemy.faction));
    }

    /// <summary>A named or random standard loadout whose weapons are all allowed; otherwise one built from allowed options.</summary>
    private static Loadout PickLoadout(SquadronDef def, AircraftDefinition airframe, FactionHQ enemy, out float fuel, MemberDef member = null)
    {
        // Written by hand: the pilot's own, then the squadron's (a name, or built by station).
        string who = $"Squadron '{def.Squadron}'" + (member?.Callsign != null ? $", {member.Callsign}" : "");
        var own = Loadouts.FromJson(member?.Loadout, airframe, who, out fuel) ?? Loadouts.FromJson(def.Loadout, airframe, who, out fuel);
        if (own != null) return own;
        // Presets saved in the F1 loadout editor and marked for aces.
        if (def.Loadout == null)
        {
            var marked = Loadouts.ForAces(airframe);
            if (marked.Count > 0)
            {
                var m = marked[Random.Range(0, marked.Count)];
                fuel = m.fuel;
                Plugin.Log.LogInfo($"Ace loadout for {airframe.unitName}: preset {m.name}.");
                return m.loadout;
            }
        }

        var parameters = airframe.aircraftParameters;
        fuel = parameters.DefaultFuelLevel;
        WeaponManager wm = airframe.unitPrefab.GetComponent<Aircraft>().weaponManager;
        var standards = (parameters.StandardLoadouts ?? Array.Empty<StandardLoadout>())
            .Where(s => s != null && !s.disabled && s.loadout != null && s.AllowedByHQ(wm, enemy)).ToList();


        // Handmade squadrons chose their jet on purpose, so they skip the weapon checklist.
        var usable = def.Handmade ? standards : standards.Where(s => LoadoutAllowed(airframe, s.loadout)).ToList();
        // The best air-to-air loadouts: scored on real air-to-air missiles (the game rates bombs above zero
        // against air too, so "any anti-air" let strike loadouts through), then any within 85% of the best.
        if (usable.Count > 0)
        {
            float best = usable.Max(s => AirToAirScore(s.loadout));
            var pool = usable.Where(s => AirToAirScore(s.loadout) >= best * 0.85f).ToList();
            var pick = pool[Random.Range(0, pool.Count)];
            fuel = pick.FuelRatio;
            Plugin.Log.LogInfo($"Ace loadout for {airframe.unitName}: {pick.Name} (air-to-air score {AirToAirScore(pick.loadout):0.0}, best {best:0.0}).");
            return pick.loadout;
        }
        return BuildLoadout(airframe, wm);
    }

    /// <summary>A real air-to-air weapon: a missile (not a bomb) rated at least 0.5 against air and more than against the surface.</summary>
    internal static bool IsAirToAirMissile(WeaponInfo i) =>
        i != null && i.missile && !i.bomb && !i.gun && i.effectiveness.antiAir >= 0.5f && i.effectiveness.antiAir > i.effectiveness.antiSurface;

    private static readonly System.Collections.Generic.Dictionary<WeaponInfo, bool> RadarSeeker = new();

    /// <summary>
    /// A radar-guided missile (active or semi-active), from the seeker on the weapon's prefab: the game's own
    /// target requirements do not say (its ARH missiles carry no minimum radar return).
    /// </summary>
    internal static bool IsRadarMissile(WeaponInfo i)
    {
        if (i == null || !i.missile || i.weaponPrefab == null) return false;
        if (RadarSeeker.TryGetValue(i, out bool radar)) return radar;
        try
        {
            radar = i.weaponPrefab.GetComponentInChildren<ARHSeeker>(true) != null || i.weaponPrefab.GetComponentInChildren<SARHSeeker>(true) != null;
        }
        catch { radar = i.targetRequirements.minRadar > 0f; }
        RadarSeeker[i] = radar;
        return radar;
    }

    /// <summary>Air-to-air missiles carried, weighted toward range (a 35 km radar missile counts double a 10 km one); bombs count against it.</summary>
    private static float AirToAirScore(Loadout loadout)
    {
        float score = 0f;
        foreach (var w in loadout.weapons)
        {
            if (w?.info == null) continue;
            int n = Mathf.Max(1, w.ammo);
            if (IsAirToAirMissile(w.info)) score += n * (1f + Mathf.Clamp(w.info.targetRequirements.maxRange / 35000f, 0f, 1.5f));
            else if (w.info.bomb || w.info.effectiveness.antiSurface > w.info.effectiveness.antiAir) score -= 0.25f * n;
        }
        return score;
    }

    private static bool LoadoutAllowed(AircraftDefinition airframe, Loadout loadout) =>
        loadout.weapons.All(w => w == null || AirframeChecklist.IsWeaponAllowed(airframe, w));

    private static Loadout BuildLoadout(AircraftDefinition airframe, WeaponManager wm)
    {
        var loadout = new Loadout();
        foreach (var set in wm.hardpointSets)
        {
            var options = set.weaponOptions
                .Where(w => w != null && w.info != null && !w.info.nuclear && AirframeChecklist.IsWeaponAllowed(airframe, w))
                .ToList();
            var aa = options.Where(w => IsAirToAirMissile(w.info)).ToList();
            if (aa.Count == 0) aa = options.Where(w => w.info.gun || w.info.effectiveness.antiAir >= 0.5f).ToList();
            var pool = aa.Count > 0 ? aa : options;
            loadout.weapons.Add(pool.Count > 0 ? pool[Random.Range(0, pool.Count)] : null);
        }
        // Clear sets that the chosen mounts block (e.g. a centreline tank over two inner pylons).
        for (int i = 0; i < wm.hardpointSets.Length; i++)
        {
            if (loadout.weapons[i] != null && wm.hardpointSets[i].BlockedByOtherHardpoint(loadout))
                loadout.weapons[i] = null;
        }
        return loadout;
    }
}
