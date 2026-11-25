using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NOAcesHigh;

internal sealed class AceMember
{
    public Aircraft Aircraft;
    /// <summary>On a client's copy of an encounter: the aircraft's PersistentID, resolved as it arrives.</summary>
    public uint NetId;
    public string Callsign;
    public string PilotName;
    public bool Lead;
    public bool Alive = true;
}

internal sealed class ActiveEncounter
{
    public SquadronDef Def;
    /// <summary>Identifies the encounter to clients (NetSync).</summary>
    public int NetId;
    public EmblemShape Shape;
    /// <summary>The aces' side (EnemyHQ) as BDF / PALA.</summary>
    public Side Side;
    /// <summary>The side being hunted, and the pilot the aces are after (re-picked if that pilot stops flying).</summary>
    public FactionHQ TargetHQ;
    public Player TargetPlayer;
    public float NextRetarget;
    public NamingScheme Scheme;
    public string SquadronName;
    public string Title;
    public string Unit = "";
    public string Designation = "";
    public CardStyle Card;
    public IntroShots Shots;
    public FormationKind Formation;
    /// <summary>Wingmen hold their slots on the lead until the flight breaks to engage.</summary>
    public bool FormationHeld;
    /// <summary>When the flight broke formation, and the lead's flat heading then (the wingmen split off it).</summary>
    public float BrokeAt = -1f;
    public Vector3 BreakHeading;
    public int Tier;
    public Color Colour;
    public Emblem Emblem;
    public FactionHQ EnemyHQ;
    public readonly List<AceMember> Members = new();
    public float StartTime;
    public float NextVector;
    public bool Ended;

    public AceMember LeadMember => Members.FirstOrDefault(m => m.Lead) ?? Members.FirstOrDefault();
    public int AliveCount => Members.Count(m => m.Alive);
}

/// <summary>Per-mission threat meter, encounter rolls and the live ace flight.</summary>
internal static class Encounters
{
    private const float VectorInterval = 3f;
    private const float MultiplayerIntroLineDelay = 9f;
    private const float EncounterTimeout = 15f * 60f;

    private static int _count;
    private static float _nextRoll;
    private static float _cooldownUntil;
    private static ActiveEncounter _active;
    private static readonly HashSet<Aircraft> AceAircraft = new();

    // Egress: run for the nearest map edge that does not lead back past the player, defending only against
    // missiles, and get removed on reaching it.
    private const float EdgeMargin = 1500f;                 // metres inside the boundary counts as "reached the edge"
    private const float EgressBackstopSeconds = 20f * 60f;  // only if the edge can never be reached
    private static readonly FieldInfo MapDimensions = typeof(DynamicMap).GetField("mapDimensions", BindingFlags.Instance | BindingFlags.NonPublic);
    // Egressing survivors re-form on a leader (the squadron lead, else the next surviving member) in the squadron's
    // formation, closing up the gaps, and leave on one shared bearing.
    private const float FormedWithin = 600f;    // everyone this close to their slot = formed up
    private const float MaxHoldSeconds = 6f * 60f; // formation breaks after this long even if nothing happened
    private sealed class EgressGroup
    {
        public FormationKind Kind;
        public Vector3 Direction;
        public readonly List<Aircraft> Order = new(); // lead first
        public bool Formed;
        public float NextFormedCheck;
    }
    private sealed class EgressInfo
    {
        public EgressGroup Group;
        public float Start;
        public string Callsign;
    }
    private static readonly Dictionary<Aircraft, EgressInfo> Egressing = new();

    internal static bool IsAce(Unit unit) => unit is Aircraft a && AceAircraft.Contains(a);

    /// <summary>Tier and wingman number (0 = lead, 1.. = wingmen in squadron order) of a fighting ace.</summary>
    internal static bool TryGetAce(Aircraft aircraft, out int tier, out int wingman)
    {
        tier = 0;
        wingman = 0;
        if (aircraft == null || !AceAircraft.Contains(aircraft) || _active == null) return false;
        tier = _active.Tier;
        int n = 1;
        foreach (var m in _active.Members)
        {
            if (m.Aircraft == aircraft)
            {
                wingman = m.Lead ? 0 : n;
                return true;
            }
            if (!m.Lead) n++;
        }
        return false;
    }
    internal static bool IsEgressing(Aircraft aircraft) => aircraft != null && Egressing.ContainsKey(aircraft);

    // ---- Formation on the way in ---------------------------------------------------------------------

    /// <summary>The lead of a flight that is still holding formation (its throttle gets capped so wingmen keep up).</summary>
    internal static bool IsFormationLead(Aircraft aircraft) =>
        _active is { Ended: false, FormationHeld: true } && aircraft != null && _active.LeadMember is { Alive: true } lead && lead.Aircraft == aircraft;

    /// <summary>A wingman currently flying its formation slot (not fighting yet).</summary>
    internal static bool InFormationSlot(Aircraft aircraft) =>
        _active is { Ended: false, FormationHeld: true } && TryGetAce(aircraft, out _, out int wingman) && wingman > 0;

    /// <summary>
    /// For the first 3 s after the flight breaks formation, the direction a wingman splits off in: 30 deg off the
    /// lead's heading to its slot's side (alternating for slots straight behind), climbing or descending a little by
    /// its slot's height, so the flight fans out instead of every jet manoeuvring into its neighbour at once.
    /// </summary>
    internal static bool TryGetBreakSplit(Aircraft aircraft, out Vector3 direction)
    {
        direction = Vector3.zero;
        var enc = _active;
        if (enc is not { Ended: false } || enc.BrokeAt < 0f || Time.time - enc.BrokeAt > 3f) return false;
        if (!TryGetAce(aircraft, out _, out int wingman) || wingman == 0) return false;
        Vector3 slot = Formations.Offset(enc.Formation, wingman);
        float side = Mathf.Abs(slot.x) > 1f ? Mathf.Sign(slot.x) : (wingman % 2 == 0 ? 1f : -1f);
        float vertical = slot.y < -1f ? -0.12f : 0.06f * (wingman % 2 == 0 ? 1f : -1f);
        direction = (Quaternion.AngleAxis(30f * side, Vector3.up) * enc.BreakHeading + Vector3.up * vertical).normalized;
        return true;
    }

    /// <summary>The target of a flight holding formation (the lead flies its ingress toward it).</summary>
    internal static Aircraft FormationTarget() => _active is { Ended: false, FormationHeld: true } ? TargetAircraft(_active) : null;

    /// <summary>The leader and slot offset of a wingman holding formation (on the way in); false otherwise.</summary>
    internal static bool TryGetFormationSlot(Aircraft aircraft, out Aircraft leader, out Vector3 offset)
    {
        leader = null;
        offset = Vector3.zero;
        var enc = _active;
        if (enc is not { Ended: false, FormationHeld: true } || aircraft == null) return false;
        if (!TryGetAce(aircraft, out _, out int wingman) || wingman == 0) return false;
        var lead = enc.LeadMember;
        if (lead is not { Alive: true } || lead.Aircraft == null || lead.Aircraft.disabled) return false;
        leader = lead.Aircraft;
        offset = Formations.Offset(enc.Formation, wingman);
        return true;
    }

    /// <summary>Where a wingman holding formation should fly; false if it is not holding one (the stock AI flies it).</summary>
    internal static bool TryGetFormationDestination(Aircraft aircraft, out GlobalPosition destination, out float throttle)
    {
        destination = default;
        throttle = 1f;
        var enc = _active;
        if (enc is not { Ended: false, FormationHeld: true } || aircraft == null) return false;
        if (!TryGetAce(aircraft, out _, out int wingman) || wingman == 0) return false;
        var lead = enc.LeadMember;
        if (lead is not { Alive: true } || lead.Aircraft == null || lead.Aircraft.disabled) return false;

        float leaderThrottle = lead.Aircraft.GetInputs()?.throttle ?? 0.8f;
        Formations.Steer(aircraft, lead.Aircraft, Formations.Offset(enc.Formation, wingman), lead.Aircraft.transform.forward,
            leaderThrottle, out destination, out throttle, out _);
        return true;
    }

    /// <summary>
    /// Breaks formation once the fight is on: a pilot of the hunted side close (the target or anyone with them),
    /// a missile in the air, or held too long.
    /// </summary>
    private static void CheckFormationBreak(ActiveEncounter enc, Aircraft target)
    {
        if (!enc.FormationHeld) return;
        string reason = null;
        var lead = enc.LeadMember;
        if (lead is not { Alive: true } || lead.Aircraft == null) reason = "lead lost";
        else if (Time.time - enc.StartTime > MaxHoldSeconds) reason = "held too long";
        else
        {
            float breakRange = Plugin.FormationBreakKm.Value * 1000f;
            var hunters = Session.FlyingPlayers(enc.TargetHQ).Select(p => p.Aircraft).ToList();
            if (target != null && !hunters.Contains(target)) hunters.Add(target);
            foreach (var m in enc.Members)
            {
                if (!m.Alive || m.Aircraft == null || m.Aircraft.disabled) continue;
                // Range alone does not break them up while their intro is still running (they parade for the camera);
                // a missile in the air still does.
                if (!IntroGate.Holding && hunters.Any(h => (m.Aircraft.GlobalPosition() - h.GlobalPosition()).magnitude < breakRange)) { reason = "in range"; break; }
                var mw = m.Aircraft.GetMissileWarningSystem();
                if (mw != null && mw.IsWarning()) { reason = "missile inbound"; break; }
            }
        }
        if (reason == null) return;
        enc.FormationHeld = false;
        enc.BrokeAt = Time.time;
        enc.BreakHeading = lead?.Aircraft != null ? Formations.FlatForward(lead.Aircraft, lead.Aircraft.transform.forward) : Vector3.forward;
        Plugin.Log.LogInfo($"{enc.SquadronName} broke {Formations.Label(enc.Formation)} formation ({reason}).");
        if (reason != "lead lost") Chatter.Say(enc, "break", enc.LeadMember, 0.3f);
    }

    /// <summary>Half the map's size on X and Z; the map is centred on the global origin.</summary>
    private static bool TryGetMapHalfExtents(out Vector2 half)
    {
        half = default;
        try
        {
            var map = SceneSingleton<DynamicMap>.i;
            if (map != null && MapDimensions?.GetValue(map) is Vector2 dims && dims.x > 0f && dims.y > 0f)
            {
                half = dims * 0.5f;
                return true;
            }
        }
        catch { /* fall through */ }
        var level = NetworkSceneSingleton<LevelInfo>.i;
        if (level != null && level.mapSize > 0f)
        {
            half = Vector2.one * (level.mapSize * 0.5f);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Bearing to the closest map edge, with edges that point toward the player made to look further away,
    /// so a retreating ace does not run back through the person chasing it.
    /// </summary>
    private static Vector3 ExitDirection(Aircraft ace, Aircraft player)
    {
        GlobalPosition p = ace.GlobalPosition();
        Vector3 away = Vector3.zero;
        if (player != null)
        {
            away = p - player.GlobalPosition();
            away.y = 0f;
            away = away.sqrMagnitude > 1f ? away.normalized : Vector3.zero;
        }

        if (!TryGetMapHalfExtents(out var half))
            return away != Vector3.zero ? away : Flat(ace.transform.forward);

        var edges = new (Vector3 dir, float dist)[]
        {
            (Vector3.right, half.x - p.x),
            (Vector3.left, half.x + p.x),
            (Vector3.forward, half.y - p.z),
            (Vector3.back, half.y + p.z),
        };
        Vector3 best = Vector3.right;
        float bestCost = float.MaxValue;
        foreach (var (dir, dist) in edges)
        {
            float towardPlayer = Mathf.Max(0f, -Vector3.Dot(dir, away)); // 1 = straight at the player
            float cost = Mathf.Max(dist, 0f) * (1f + 3f * towardPlayer);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = dir;
            }
        }
        return best;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
    }

    private static bool AtMapEdge(Aircraft aircraft)
    {
        if (!TryGetMapHalfExtents(out var half)) return false;
        GlobalPosition p = aircraft.GlobalPosition();
        return Mathf.Abs(p.x) > half.x - EdgeMargin || Mathf.Abs(p.z) > half.y - EdgeMargin;
    }

    /// <summary>
    /// Where an egressing ace should fly this frame and at what throttle; false if it is not egressing.
    /// The leader flies the exit bearing (easing off until the flight has formed up); wingmen fly to their slot.
    /// </summary>
    internal static bool TryGetEgressDestination(Aircraft aircraft, out GlobalPosition destination, out float throttle)
    {
        destination = default;
        throttle = 1f;
        if (aircraft == null || !Egressing.TryGetValue(aircraft, out var info)) return false;
        var group = info.Group;
        GlobalPosition here = aircraft.GlobalPosition();
        float cruise = aircraft.GetAircraftParameters()?.cruiseThrottle ?? 0.9f;

        Aircraft leader = null;
        int slot = 0, rank = 0;
        foreach (var a in group.Order)
        {
            if (a == null || a.disabled || !Egressing.ContainsKey(a)) continue;
            if (leader == null) leader = a;
            if (a == aircraft) slot = rank;
            rank++;
        }

        if (leader == null || leader == aircraft)
        {
            UpdateFormed(group, leader);
            destination = here + group.Direction * 10000f;
            throttle = group.Formed ? 1f : cruise * 0.85f;
            return true;
        }

        // Wingman: fly to its slot off the leader, along the leader's track.
        float leaderThrottle = group.Formed ? 1f : cruise * 0.85f;
        Formations.Steer(aircraft, leader, Formations.Offset(group.Kind, slot), group.Direction, leaderThrottle,
            out destination, out throttle, out _);
        return true;
    }

    /// <summary>Re-checks twice a second whether every wingman is near its slot.</summary>
    private static void UpdateFormed(EgressGroup group, Aircraft leader)
    {
        if (leader == null || Time.time < group.NextFormedCheck) return;
        group.NextFormedCheck = Time.time + 0.5f;
        Vector3 fwd = Formations.FlatForward(leader, group.Direction);
        GlobalPosition leaderPos = leader.GlobalPosition();
        int slot = 0;
        bool formed = true;
        foreach (var a in group.Order)
        {
            if (a == null || a.disabled || !Egressing.ContainsKey(a)) continue;
            if (slot > 0)
            {
                GlobalPosition s = Formations.SlotPosition(leaderPos, fwd, Formations.Offset(group.Kind, slot));
                if ((s - a.GlobalPosition()).magnitude > FormedWithin) { formed = false; break; }
            }
            slot++;
        }
        if (formed && !group.Formed) Plugin.Log.LogInfo("Egressing aces formed up; leaving at full throttle.");
        group.Formed = formed;
    }

    /// <summary>Human players fly on more than one side (player versus player).</summary>
    private static float _pvpCheckedAt = -10f;
    private static bool _pvp;
    private static bool IsPvp()
    {
        if (!Session.InMultiplayer) return false;
        if (Time.time - _pvpCheckedAt < 5f) return _pvp;
        _pvpCheckedAt = Time.time;
        try { _pvp = FactionRegistry.GetAllHQs().Count(h => h != null && h.GetPlayers(sortByScore: false).Any(p => p != null)) >= 2; }
        catch { _pvp = false; }
        return _pvp;
    }

    /// <summary>
    /// The one place a mission's leftovers are cleared (mission load, back to the menu, or no longer the host): every
    /// system's per-mission state, the music and radio, the intro (which gives back the camera and game speed), the
    /// debug queue and the this-mission switch.
    /// </summary>
    internal static void ResetForMission()
    {
        Threats.Clear();
        PlayerThreat.Clear();
        _count = 0;
        _nextRoll = 0f;
        _cooldownUntil = 0f;
        _active = null;
        AceFlight.Reset();
        AceTactics.Reset();
        SquadronHealth.Reset();
        LiveryForge.Reset();
        PostStall.Reset();
        IntroDirector.Abort();
        Music.Stop();
        AceAircraft.Clear();
        Egressing.Clear();
        Retreated.Clear();
        Chatter.Clear();
        VoiceLines.Reset();
        SquadronDown.Reset();
        AmbientChatter.Reset();
        DebugQueue.Cancel();
        SettingsMenu.SkipThisMission = false;
        _nextTaunt = 0f;
        Plugin.Log.LogInfo("Mission loaded; threat meters reset.");
    }

    // ---- Threat ------------------------------------------------------------------------------------
    // One meter per side. Kills by a side's human players (and bases that side takes) raise it; when it passes the
    // unlock threshold the opposing side may launch aces at that side's most dangerous pilot.

    private static readonly Dictionary<FactionHQ, float> Threats = new();
    private static readonly Dictionary<Player, float> PlayerThreat = new();

    internal static float ThreatOf(FactionHQ hq) => hq != null && Threats.TryGetValue(hq, out var t) ? t : 0f;

    internal static void AddThreat(FactionHQ side, Player player, float amount, string reason)
    {
        if (amount <= 0f || side == null || !Session.InMission) return;
        float before = ThreatOf(side);
        Threats[side] = before + amount;
        if (player != null) PlayerThreat[player] = (PlayerThreat.TryGetValue(player, out var p) ? p : 0f) + amount;
        Plugin.Log.LogDebug($"Threat {Factions.Short(side)} +{amount:0.#} ({reason}) -> {before + amount:0.#}");
        if (before < Plugin.UnlockThreat.Value && before + amount >= Plugin.UnlockThreat.Value)
        {
            _nextRoll = Mathf.Max(_nextRoll, Time.time + Plugin.RollInterval.Value * 0.5f);
            Plugin.Log.LogInfo($"{Factions.Short(side)} threat {before + amount:0} passed {Plugin.UnlockThreat.Value:0}; ace rolls unlocked against them.");
        }
    }

    /// <summary>The pilot of a side the aces should hunt: the one who has done the most damage (or the nearest), among those flying.</summary>
    private static Player PickTarget(FactionHQ hq, Vector3? near = null)
    {
        Player best = null;
        float bestScore = float.MinValue;
        foreach (var p in Session.FlyingPlayers(hq))
        {
            float score = near.HasValue
                ? -(p.Aircraft.transform.position - near.Value).sqrMagnitude
                : (PlayerThreat.TryGetValue(p, out var t) ? t : 0f) + Random.value * 0.01f;
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// The aircraft the active aces are after. If their pilot is dead, respawning or gone, they switch to the
    /// nearest flying pilot of the same side; null if nobody on that side is flying.
    /// </summary>
    internal static Aircraft TargetAircraft(ActiveEncounter enc)
    {
        if (enc == null) return null;
        if (TestTarget != null && !TestTarget.disabled && enc == _active) return TestTarget;
        var p = enc.TargetPlayer;
        if (p != null && p.Aircraft != null && !p.Aircraft.disabled) return p.Aircraft;
        if (Time.time < enc.NextRetarget) return null;
        enc.NextRetarget = Time.time + 5f;
        var lead = enc.Members.FirstOrDefault(m => m.Alive && m.Aircraft != null)?.Aircraft;
        var next = PickTarget(enc.TargetHQ, lead != null ? lead.transform.position : (Vector3?)null);
        if (next == null) return null;
        if (next != p) Plugin.Log.LogInfo($"{enc.SquadronName} now hunting {Session.PlayerName(next)}.");
        enc.TargetPlayer = next;
        return next.Aircraft;
    }

    /// <summary>Aircraft of the hunted side's pilots now flying (plus the target), for manoeuvre triggers.</summary>
    internal static IEnumerable<Aircraft> HuntedAircraft()
    {
        if (_active is not { Ended: false }) yield break;
        var target = TargetAircraft(_active);
        bool targetListed = false;
        foreach (var p in Session.FlyingPlayers(_active.TargetHQ))
        {
            if (p.Aircraft == target) targetListed = true;
            yield return p.Aircraft;
        }
        if (target != null && !targetListed) yield return target;
    }

    /// <summary>The active squadron's lead aircraft if it is still flying (debug manoeuvres).</summary>
    internal static Aircraft ActiveLead =>
        _active is { Ended: false } && _active.LeadMember is { Alive: true } lead && lead.Aircraft != null && !lead.Aircraft.disabled
            ? lead.Aircraft
            : _active?.Members.FirstOrDefault(m => m.Alive && m.Aircraft != null && !m.Aircraft.disabled)?.Aircraft;

    /// <summary>The active encounter's target, for the targeting patch.</summary>
    internal static Aircraft ActiveTarget => _active is { Ended: false } ? TargetAircraft(_active) : null;

    internal static string StatusLine()
    {
        string active = _active is { Ended: false }
            ? $"active: {_active.SquadronName} ({Factions.Short(_active.EnemyHQ)}, {_active.AliveCount}/{_active.Members.Count} alive) hunting {Session.PlayerName(_active.TargetPlayer)}"
            : "no active squadron";
        string threats;
        try
        {
            threats = string.Join(" · ", FactionRegistry.GetAllHQs().Where(h => h != null)
                .Select(h => $"{Factions.Short(h)} threat {ThreatOf(h):0}/{Plugin.UnlockThreat.Value:0}"));
        }
        catch { threats = "no mission"; }
        return $"{Session.Label} · {threats} · encounters {_count}/{Plugin.MaxEncounters.Value} · {active} · {Music.Describe()}";
    }

    internal static void Tick()
    {
        // Not deciding encounters here (menu, editor, multiplayer client): drop anything left from the last mission.
        if (!Session.Authoritative)
        {
            if (_active != null || _count > 0 || Threats.Count > 0) ResetForMission();
            return;
        }

        Chatter.Tick();
        TickEgress();
        AceTactics.TickRetreating();
        if (!Plugin.Enabled.Value || !Session.InMission) return;

        // Runs even while nobody is flying, so a finished fight still ends.
        if (_active is { Ended: false })
        {
            PruneLost(_active);
            if (_active.AliveCount == 0)
            {
                End(_active, "defeated");
                return;
            }
            if (Retreated.Count > 0 && _active.Members.All(m => !m.Alive || m.Aircraft == null || !AceAircraft.Contains(m.Aircraft)))
            {
                End(_active, "withdraw");
                return;
            }
            AceTactics.Tick(_active, TargetAircraft(_active));
            AmbientChatter.Tick(_active, TargetAircraft(_active));
            TickActive(_active, TargetAircraft(_active));
            return;
        }

        if (_count >= Plugin.MaxEncounters.Value) return;
        if (SettingsMenu.SkipThisMission) return;
        if (IsPvp() && !SettingsMenu.AcesInPvp.Value) return;
        float now = Time.time;
        if (now < _cooldownUntil || now < _nextRoll) return;

        // The side with the highest unlocked threat and someone flying gets the roll.
        FactionHQ side = null;
        float best = 0f;
        foreach (var pair in Threats)
        {
            if (pair.Key == null || pair.Value < Plugin.UnlockThreat.Value || pair.Value <= best) continue;
            if (Session.FlyingPlayers(pair.Key).Length == 0) continue;
            side = pair.Key;
            best = pair.Value;
        }
        if (side == null) return;

        _nextRoll = now + Plugin.RollInterval.Value;
        float roll = Random.value;
        bool hit = roll < Plugin.RollChance.Value;
        Plugin.Log.LogInfo($"Ace roll against {Factions.Short(side)} {roll:0.00} vs {Plugin.RollChance.Value:0.00}: {(hit ? "launch" : "miss")}");
        if (!hit) return;
        var target = PickTarget(side);
        if (target != null) Launch(target.Aircraft, side);
    }

    /// <summary>The live encounter on the host, if any (null on a client: see NetSync.Mirror).</summary>
    internal static ActiveEncounter Current => _active;

    internal static bool HasActive => _active is { Ended: false } && _active.AliveCount > 0;

    /// <summary>
    /// Debug/menu: launch a squadron now (a given one, or a random pick) at you, or with nobody local flying
    /// (dedicated server) at the most dangerous pilot of any side. Returns what happened.
    /// </summary>
    internal static string DebugSpawnNow(SquadronDef def = null)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        try { return DebugSpawnNowInner(def); }
        finally { Plugin.Log.LogInfo($"Spawn timing: whole launch {swTotal.Elapsed.TotalMilliseconds:0} ms."); }
    }

    private static string DebugSpawnNowInner(SquadronDef def)
    {
        string fail = null;
        Aircraft target = null;
        FactionHQ side = null;
        if (!Session.InMission)
            fail = Session.InMultiplayer && !Session.IsServer
                ? "Only the host can launch aces in multiplayer."
                : "Needs a mission in progress" + (Session.InMultiplayer && !Plugin.Multiplayer.Value ? " (multiplayer encounters are off)." : ".");
        else
        {
            target = Session.LocalAircraft;
            side = target != null ? Session.LocalHQ : null;
            if (target == null)
            {
                foreach (var hq in FactionRegistry.GetAllHQs().Where(h => h != null).OrderByDescending(ThreatOf))
                {
                    var p = PickTarget(hq);
                    if (p == null) continue;
                    target = p.Aircraft;
                    side = hq;
                    break;
                }
            }
            if (target == null || side == null) fail = "Nobody is flying to launch aces at.";
        }
        if (fail == null && _active is { Ended: false })
        {
            PruneLost(_active);
            if (_active.AliveCount > 0) fail = $"{_active.SquadronName} is already up. End that encounter first.";
            else End(_active, "defeated");
        }
        if (fail != null)
        {
            Plugin.Log.LogWarning("Spawn now: " + fail);
            return fail;
        }

        string note = "";
        Side own = def != null ? Factions.Parse(def.Faction) : Side.Unknown;
        if (own != Side.Unknown && own == Factions.Of(side))
            note = $" ({def.Squadron} normally flies for {own}, your side; launched for {Factions.Short(Factions.Opponent(side))} so it has someone to fight.)";
        var enc = Launch(target, side, def);
        return enc == null
            ? "Spawn failed; see the BepInEx log."
            : $"Launched {enc.SquadronName} ({Factions.Short(enc.EnemyHQ)}): {string.Join(", ", enc.Members.GroupBy(m => m.Aircraft.definition.unitName).Select(g => $"{g.Count()} x {g.Key}"))}, tier {enc.Tier}, " +
              $"{Formations.Label(enc.Formation)}, hunting {Session.PlayerName(enc.TargetPlayer)}.{note}";
    }

    private static ActiveEncounter Launch(Aircraft target, FactionHQ targetHQ, SquadronDef chosen = null)
    {
        SquadronDef def = chosen ?? PickSquadron(Factions.Opponent(targetHQ));
        var encounter = AceSpawner.Spawn(def, target, targetHQ, _count);
        if (encounter == null) return null;

        _active = encounter;
        _count++;
        foreach (var m in encounter.Members)
        {
            AceAircraft.Add(m.Aircraft);
            var member = m;
            m.Aircraft.onDisableUnit += _ => OnAceDown(encounter, member);
        }

        // The first AWACS line carries the encounter to modded clients on the hunted side, who play the intro themselves.
        Chatter.Say(encounter, "awacs", null, 0f, announce: true);
        Chatter.Say(encounter, "allied", null, 1f);
        bool localHunted = Session.LocalHQ != null && Session.LocalHQ == encounter.TargetHQ && Session.LocalAircraft != null;
        if (localHunted) { Music.Begin(encounter); VoiceLines.Prepare(encounter); }
        Stats.OnLaunch(encounter);
        if (Session.MultiplayerRules)
        {
            // Everyone's intro runs on its own clock, so the ace's opening line comes once most of them are done.
            if (localHunted) IntroDirector.Begin(encounter);
            Chatter.Say(encounter, "intro", encounter.LeadMember, MultiplayerIntroLineDelay);
        }
        else if (!localHunted || !IntroDirector.Begin(encounter))
        {
            // Single player: the intro speaks the opening line as the camera returns; without one it follows AWACS.
            Chatter.Say(encounter, "intro", encounter.LeadMember, 4f);
        }
        Plugin.Log.LogInfo($"Launched '{encounter.SquadronName}' for {Factions.Short(encounter.EnemyHQ)} ({encounter.Scheme} naming) tier {encounter.Tier}, " +
                           $"{Formations.Label(encounter.Formation)}, hunting {Session.PlayerName(encounter.TargetPlayer)} ({Session.Label}): " +
                           string.Join(", ", encounter.Members.Select(x => $"{x.Callsign} [{x.Aircraft.definition.unitName}]")));
        return encounter;
    }

    private const float TauntGap = 15f;
    private static float _nextTaunt;

    /// <summary>
    /// An ace mouthing off after a manoeuvre (evade / snap / overshoot lines), spoken by that ace to the side it
    /// hunts. At most one every 15 s per mission, and only some of the time, so it stays a surprise.
    /// </summary>
    internal static void Taunt(Aircraft aircraft, string key)
    {
        var enc = _active;
        if (enc is not { Ended: false } || aircraft == null || Time.time < _nextTaunt || Random.value > 0.6f) return;
        var member = enc.Members.FirstOrDefault(m => m.Aircraft == aircraft && m.Alive);
        if (member == null) return;
        _nextTaunt = Time.time + TauntGap;
        Chatter.Say(enc, key, member, 0.4f);
    }

    /// <summary>The active squadron's aircraft still flying and fighting (teamwork).</summary>
    internal static IEnumerable<Aircraft> SquadAircraft()
    {
        if (_active is not { Ended: false }) yield break;
        foreach (var m in _active.Members)
            if (m.Alive && m.Aircraft != null && !m.Aircraft.disabled && AceAircraft.Contains(m.Aircraft))
                yield return m.Aircraft;
    }

    /// <summary>The live encounter hunting this side, if any (threat bar, music).</summary>
    internal static ActiveEncounter ActiveAgainst(FactionHQ hq) =>
        _active is { Ended: false } && hq != null && _active.TargetHQ == hq ? _active : null;

    internal static int EncountersLeft => Plugin.MaxEncounters.Value - _count;

    internal static bool CoolingDown => Time.time < _cooldownUntil;

    /// <summary>Record mode: an AI aircraft the aces treat as their target pilot.</summary>
    internal static Aircraft TestTarget;

    /// <summary>Record mode: a silent encounter at a chosen point and speed, with no radio or intro.</summary>
    internal static ActiveEncounter LaunchForTest(SquadronDef def, GlobalPosition anchor, FactionHQ targetHQ, float speed)
    {
        if (_active is { Ended: false }) DespawnActiveForTest();
        var encounter = AceSpawner.SpawnAt(def, anchor, null, targetHQ, _count, 0f, speed);
        if (encounter == null) return null;
        _active = encounter;
        foreach (var m in encounter.Members)
        {
            AceAircraft.Add(m.Aircraft);
            var member = m;
            m.Aircraft.onDisableUnit += _ => OnAceDown(encounter, member);
        }
        return encounter;
    }

    /// <summary>Record mode: remove the active encounter's aircraft at once, silently.</summary>
    internal static void DespawnActiveForTest()
    {
        var enc = _active;
        if (enc == null) return;
        enc.Ended = true;
        foreach (var m in enc.Members)
        {
            if (m.Alive && m.Aircraft != null) Despawn(m.Aircraft, m.Callsign);
            m.Alive = false;
            AceAircraft.Remove(m.Aircraft);
        }
        _active = null;
    }

    /// <summary>A handmade squadron in rotation that flies for this side (or either side), else a random one.</summary>
    private static SquadronDef PickSquadron(FactionHQ aceSide)
    {
        Side side = Factions.Of(aceSide);
        var handmade = Squadrons.InRotation()
            .Where(d => Factions.Parse(d.Faction) is var s && (s == Side.Unknown || s == side))
            .ToList();
        if (handmade.Count > 0 && Random.value < Plugin.HandmadeWeight.Value)
            return handmade[Random.Range(0, handmade.Count)];
        return new SquadronDef();
    }

    private static void TickActive(ActiveEncounter enc, Aircraft target)
    {
        float now = Time.time;
        if (now - enc.StartTime > EncounterTimeout)
        {
            End(enc, "withdraw");
            StartEgress(enc);
            return;
        }

        CheckFormationBreak(enc, target);
        if (target == null) return;

        // Their side's ground control keeps the flight pointed at the target until it is close enough to find them itself.
        float vectorRange = Plugin.VectorUntilKm.Value * 1000f;
        if (vectorRange > 0f && now >= enc.NextVector && enc.EnemyHQ != null)
        {
            enc.NextVector = now + VectorInterval;
            float nearest = float.MaxValue;
            foreach (var m in enc.Members)
            {
                if (!m.Alive || m.Aircraft == null) continue;
                nearest = Mathf.Min(nearest, (m.Aircraft.GlobalPosition() - target.GlobalPosition()).magnitude);
            }
            if (nearest > vectorRange)
                enc.EnemyHQ.RpcUpdateTrackingInfo(target.persistentID);
        }
    }

    /// <summary>Aircraft destroyed without raising onDisableUnit (despawned, scene torn down) count as down.</summary>
    private static void PruneLost(ActiveEncounter enc)
    {
        foreach (var m in enc.Members)
        {
            if (m.Alive && (m.Aircraft == null || m.Aircraft.disabled))
                OnAceDown(enc, m);
        }
    }

    private static void OnAceDown(ActiveEncounter enc, AceMember member)
    {
        if (!member.Alive || enc.Ended) return;
        member.Alive = false;
        AceAircraft.Remove(member.Aircraft);
        Plugin.Log.LogInfo($"Ace down: {member.Callsign} of {enc.SquadronName}");
        Stats.OnAceDown(enc, member.Aircraft, enc.Tier);

        if (enc.AliveCount == 0) return; // the 'defeated' line covers it on the next tick
        var speaker = enc.Members.FirstOrDefault(m => m.Alive);
        if (member.Lead)
            Chatter.Say(enc, "leadDown", speaker, 0.5f);
        else
            Chatter.Say(enc, "wingmanDown", speaker, 0.5f, member.Callsign);
    }

    /// <summary>Debug: end the encounter; the survivors egress or despawn per the EndEncounterAction setting.</summary>
    internal static void DebugEndEncounter()
    {
        if (_active is not { Ended: false })
        {
            Plugin.Log.LogWarning("End encounter: no squadron is active.");
            return;
        }
        var enc = _active;
        PruneLost(enc);
        End(enc, "ended from the debug menu", announce: false);
        if (Plugin.EndAction.Value == EndEncounterAction.Despawn)
        {
            int removed = 0;
            foreach (var m in enc.Members)
            {
                if (m.Alive && Despawn(m.Aircraft, m.Callsign)) removed++;
                m.Alive = false;
            }
            Plugin.Log.LogInfo($"Despawned {removed} ace aircraft of {enc.SquadronName}.");
        }
        else
        {
            StartEgress(enc);
        }
    }

    /// <summary>
    /// One ace leaves the fight on its own (winchester, bingo fuel): it stops hunting, says so, and runs for the map
    /// edge away from its target. When the last fighting ace has gone the encounter ends as a withdrawal.
    /// </summary>
    internal static void RetreatMember(Aircraft aircraft, string lineKey, string why)
    {
        var enc = _active;
        if (enc is not { Ended: false } || aircraft == null || Egressing.ContainsKey(aircraft)) return;
        var member = enc.Members.FirstOrDefault(m => m.Alive && m.Aircraft == aircraft);
        if (member == null) return;
        Aircraft target = TargetAircraft(enc) ?? Session.LocalAircraft;
        var group = new EgressGroup { Kind = enc.Formation, Direction = ExitDirection(aircraft, target), Formed = true };
        group.Order.Add(aircraft);
        AceAircraft.Remove(aircraft);
        Egressing[aircraft] = new EgressInfo { Group = group, Start = Time.time, Callsign = member.Callsign };
        Retreated.Add(aircraft);
        _lastTargetHQ = enc.TargetHQ;
        Plugin.Log.LogInfo($"{member.Callsign} of {enc.SquadronName} retreating: {why}.");
        Chatter.Say(enc, lineKey, member, 0.3f);
        if (enc.Members.All(m => !m.Alive || m.Aircraft == null || !AceAircraft.Contains(m.Aircraft)))
            End(enc, "withdraw");
    }

    private static FactionHQ _lastTargetHQ;

    /// <summary>Who may be chasing a retreating ace: the hunted side's pilots (also after the encounter ended) and the test target.</summary>
    internal static IEnumerable<Aircraft> PursuerCandidates()
    {
        if (TestTarget != null && !TestTarget.disabled) yield return TestTarget;
        var hq = _active?.TargetHQ ?? _lastTargetHQ;
        if (hq == null) yield break;
        foreach (var p in Session.FlyingPlayers(hq))
            if (p.Aircraft != null && p.Aircraft != TestTarget) yield return p.Aircraft;
    }

    /// <summary>Aces that left the fight on their own (they still defend themselves and fire back).</summary>
    internal static readonly HashSet<Aircraft> Retreated = new();
    internal static bool IsRetreating(Aircraft a) => a != null && Retreated.Contains(a) && Egressing.ContainsKey(a);

    /// <summary>Survivors stop fighting, re-form and run for a map edge away from their target.</summary>
    private static void StartEgress(ActiveEncounter enc)
    {
        Aircraft player = TargetAircraft(enc) ?? Session.LocalAircraft;
        bool havePlayer = player != null;
        // Lead first, then the rest in squadron order: that is the order of the slots.
        var survivors = enc.Members.Where(m => m.Alive && m.Aircraft != null && !m.Aircraft.disabled)
                                   .OrderByDescending(m => m.Lead).ToList();
        if (survivors.Count == 0) return;

        enc.FormationHeld = false;
        var group = new EgressGroup { Kind = enc.Formation, Direction = ExitDirection(survivors[0].Aircraft, havePlayer ? player : null) };
        foreach (var m in survivors)
        {
            AceAircraft.Remove(m.Aircraft); // no longer hunts the player
            group.Order.Add(m.Aircraft);
            Egressing[m.Aircraft] = new EgressInfo { Group = group, Start = Time.time, Callsign = m.Callsign };
        }
        Plugin.Log.LogInfo($"{survivors.Count} ace(s) of {enc.SquadronName} egressing; forming on {survivors[0].Callsign}.");
    }

    /// <summary>Removes egressing aces when they reach the map edge.</summary>
    private static void TickEgress()
    {
        if (Egressing.Count == 0) return;
        foreach (var pair in Egressing.ToList())
        {
            Aircraft a = pair.Key;
            if (a == null || a.disabled)
            {
                Egressing.Remove(a);
                continue;
            }
            bool edge = AtMapEdge(a);
            if (edge || Time.time - pair.Value.Start > EgressBackstopSeconds)
            {
                Despawn(a, pair.Value.Callsign);
                Plugin.Log.LogInfo($"{pair.Value.Callsign} egressed and was removed ({(edge ? "reached the map edge" : "time limit")}).");
            }
        }
    }

    private static bool Despawn(Aircraft aircraft, string callsign)
    {
        if (aircraft == null) return false;
        Egressing.Remove(aircraft);
        AceAircraft.Remove(aircraft);
        try
        {
            var objects = NetworkManagerNuclearOption.i?.ServerObjectManager;
            if (objects != null && aircraft.Identity != null)
                objects.Destroy(aircraft.Identity, true);
            else
                Object.Destroy(aircraft.gameObject);
            return true;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Could not despawn {callsign}: {ex.Message}");
            return false;
        }
    }

    internal static void DebugReplayIntro()
    {
        if (_active is not { Ended: false })
        {
            Plugin.Log.LogWarning("Replay intro: no squadron is active.");
            return;
        }
        if (!IntroDirector.Begin(_active))
            Plugin.Log.LogWarning("Replay intro: intros are turned off (4. Intro > PlayIntro).");
    }

    private static void End(ActiveEncounter enc, string reason, bool announce = true)
    {
        if (IntroDirector.Encounter == enc) IntroDirector.Abort();
        Music.End(enc, reason);
        Stats.OnEnd(enc, reason);
        enc.Ended = true;
        _cooldownUntil = Time.time + Plugin.Cooldown.Value;
        if (announce)
            Chatter.Say(enc, reason, reason == "withdraw" ? enc.Members.FirstOrDefault(m => m.Alive) : null, 1f, closes: true);
        foreach (var m in enc.Members) AceAircraft.Remove(m.Aircraft);
        Plugin.Log.LogInfo($"Encounter with {enc.SquadronName} ended: {reason}.");
    }
}
