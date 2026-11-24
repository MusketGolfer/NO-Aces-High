using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Gets encounters from the host to every client, assuming everyone runs NO Aces High (and its dependencies).
///
/// Transport: the game's own side-filtered mission message (MissionMessages.ShowMessage), so nothing new goes on the
/// wire. Each radio line is sent as its visible text, "Speaker: line", followed by a hidden TextMeshPro span
/// (&lt;size=0&gt;&lt;alpha=#00&gt;) holding base64 JSON: the speaker, colour and line, and on the first line of an
/// encounter the squadron itself (names, look, intro choices, and the aces' PersistentIDs).
///
/// - A modded client (or the host) intercepts the message before the feed shows it: it plays the line as a UIPlus
///   subtitle and, for the side being hunted, builds a local copy of the encounter and plays the intro on its own
///   screen (title card; the camera cut and slow motion only with the experimental settings).
/// - Fallback, for a player with no working copy of the mod: the feed shows the clean "Speaker: line" and hides the rest.
/// </summary>
internal static class NetSync
{
    private const string Marker = "NOAH1:";
    private const string HideOpen = "<size=0><alpha=#00>";
    private const string HideClose = "</alpha></size>";

    // Packet fields are short: they ride inside every radio line.
#pragma warning disable CS0649
    private sealed class Packet
    {
        [JsonProperty("s")] public string Speaker;
        [JsonProperty("c")] public string Colour;
        [JsonProperty("t")] public string Text;
        [JsonProperty("e")] public EncounterInfo Encounter;
        [JsonProperty("end")] public int EndId;
        [JsonProperty("x")] public bool Enemy;
    }

    private sealed class EncounterInfo
    {
        [JsonProperty("id")] public int Id;
        [JsonProperty("n")] public string Name;
        [JsonProperty("ti")] public string Title;
        [JsonProperty("u")] public string Unit;
        [JsonProperty("d")] public string Designation;
        [JsonProperty("c")] public string Colour;
        [JsonProperty("side")] public Side Side;
        [JsonProperty("sch")] public NamingScheme Scheme;
        [JsonProperty("card")] public CardStyle Card;
        [JsonProperty("shots")] public IntroShots Shots;
        [JsonProperty("shape")] public EmblemShape Shape;
        [JsonProperty("key")] public string Key;
        [JsonProperty("f")] public FormationKind Formation;
        [JsonProperty("tier")] public int Tier;
        [JsonProperty("m")] public List<MemberInfo> Members;
    }

    private sealed class MemberInfo
    {
        [JsonProperty("id")] public uint Id;
        [JsonProperty("cs")] public string Callsign;
        [JsonProperty("p")] public string Pilot;
        [JsonProperty("l")] public bool Lead;
    }
#pragma warning restore CS0649

    private static int _nextId = 1;

    internal static int NewEncounterId() => _nextId++;

    // ---- host side ------------------------------------------------------------------------------------

    /// <summary>The mission message for a radio line, carrying the encounter when <paramref name="announce"/> is set.</summary>
    internal static string Encode(string speaker, Color colour, string text, ActiveEncounter announce, int endId, bool enemy)
    {
        var packet = new Packet
        {
            Speaker = speaker,
            Colour = "#" + ColorUtility.ToHtmlStringRGB(colour),
            Text = text,
            Encounter = announce != null ? Describe(announce) : null,
            EndId = endId,
            Enemy = enemy,
        };
        string json = JsonConvert.SerializeObject(packet, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"{speaker}: {text}{HideOpen}{Marker}{data}{HideClose}";
    }

    private static EncounterInfo Describe(ActiveEncounter enc) => new()
    {
        Id = enc.NetId,
        Name = enc.SquadronName,
        Title = enc.Title,
        Unit = enc.Unit,
        Designation = enc.Designation,
        Colour = "#" + ColorUtility.ToHtmlStringRGB(enc.Colour),
        Side = enc.Side,
        Scheme = enc.Scheme,
        Card = enc.Card,
        Shots = enc.Shots,
        Shape = enc.Shape,
        Key = enc.Def is { Handmade: true } ? enc.Def.Key : null,
        Formation = enc.Formation,
        Tier = enc.Tier,
        Members = enc.Members.Select(m => new MemberInfo
        {
            Id = m.Aircraft != null ? m.Aircraft.persistentID.Id : 0,
            Callsign = m.Callsign,
            Pilot = m.PilotName,
            Lead = m.Lead,
        }).ToList(),
    };

    // ---- receiving side (host included) -------------------------------------------------------------

    internal static void Apply(Harmony harmony)
    {
        Patches.Patch(harmony, "ace radio and encounter sync",
            AccessTools.Method(typeof(MissionMessages), "ShowMessgeLocal"),
            prefix: nameof(ShowMessageLocalPrefix), owner: typeof(NetSync));
    }

    private static MethodInfo _isLocalFaction, _playSound;

    /// <summary>Takes our messages out of the feed and plays them properly; anything else goes through untouched.</summary>
    private static bool ShowMessageLocalPrefix(MissionMessages __instance, string message, bool playsound, FactionHQ filterFaction)
    {
        if (string.IsNullOrEmpty(message)) return true;
        int at = message.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return true;
        try
        {
            int end = message.IndexOf(HideClose, at, StringComparison.Ordinal);
            string data = message.Substring(at + Marker.Length, (end > at ? end : message.Length) - at - Marker.Length);
            var packet = JsonConvert.DeserializeObject<Packet>(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
            if (packet == null) return true;
            if (!IsLocalFaction(filterFaction)) return false; // not for this side: same as the game's own filter

            if (playsound) PlaySound(__instance);
            ColorUtility.TryParseHtmlString(packet.Colour ?? "", out var colour);
            Chatter.ShowLocal(packet.Speaker, colour.a > 0f ? colour : Color.white, packet.Text, packet.Enemy);

            // The host already has the real encounter; clients build their copy.
            if (!Session.Authoritative)
            {
                if (packet.Encounter != null) BeginMirror(packet.Encounter);
                if (packet.EndId != 0 && _mirror != null && _mirror.NetId == packet.EndId) EndMirror("ended by the host");
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Could not read an ace radio message, showing it as text: " + ex.Message);
            return true;
        }
    }

    private static bool IsLocalFaction(FactionHQ filter)
    {
        try
        {
            _isLocalFaction ??= AccessTools.Method(typeof(MissionMessages), "IsLocalFaction");
            if (_isLocalFaction != null) return (bool)_isLocalFaction.Invoke(null, new object[] { filter });
        }
        catch { /* fall through */ }
        return filter == null || Session.LocalHQ == filter;
    }

    private static void PlaySound(MissionMessages messages)
    {
        try
        {
            _playSound ??= AccessTools.Method(typeof(MissionMessages), "PlaySound");
            _playSound?.Invoke(messages, null);
        }
        catch { /* sound is optional */ }
    }

    // ---- the client's copy of the encounter ---------------------------------------------------------

    private static ActiveEncounter _mirror;
    private static float _mirrorStart;
    private const float ResolveGrace = 15f; // seconds to wait for an ace's aircraft to reach this client

    internal static ActiveEncounter Mirror => _mirror;

    private static void BeginMirror(EncounterInfo info)
    {
        if (_mirror != null) EndMirror("replaced");
        var def = (!string.IsNullOrEmpty(info.Key) ? Squadrons.Handmade.FirstOrDefault(d => d.Key == info.Key) : null) ?? new SquadronDef();
        ColorUtility.TryParseHtmlString(info.Colour ?? "", out var colour);
        if (colour.a <= 0f) colour = Color.white;

        var enc = new ActiveEncounter
        {
            NetId = info.Id,
            Def = def,
            Side = info.Side,
            TargetHQ = Session.LocalHQ,
            EnemyHQ = Factions.HQFor(info.Side) ?? Factions.Opponent(Session.LocalHQ),
            SquadronName = info.Name ?? "Aces",
            Title = info.Title ?? "",
            Unit = info.Unit ?? "",
            Designation = info.Designation ?? "",
            Colour = colour,
            Scheme = info.Scheme,
            Card = info.Card,
            Shots = info.Shots,
            Shape = info.Shape,
            Formation = info.Formation,
            Tier = info.Tier,
            StartTime = Time.time,
        };
        enc.Emblem = def.LoadedEmblem ?? Emblem.Generate(enc.SquadronName, enc.Colour, info.Shape, info.Side);
        foreach (var m in info.Members ?? new List<MemberInfo>())
            enc.Members.Add(new AceMember { NetId = m.Id, Callsign = m.Callsign, PilotName = m.Pilot, Lead = m.Lead, Aircraft = Resolve(m.Id) });
        if (enc.Members.Count > 0 && !enc.Members.Any(x => x.Lead)) enc.Members[0].Lead = true;

        _mirror = enc;
        _mirrorStart = Time.time;
        Plugin.Log.LogInfo($"Host launched {enc.SquadronName} ({info.Side}, {enc.Members.Count} aces) at this side; " +
                           $"{enc.Members.Count(x => x.Aircraft != null)} aircraft found so far.");
        if (Session.LocalAircraft != null)
        {
            Music.Begin(enc);
            VoiceLines.Prepare(enc);
            IntroDirector.Begin(enc);
        }
    }

    private static Aircraft Resolve(uint id)
    {
        if (id == 0) return null;
        try
        {
            return new PersistentID { Id = id }.TryGetUnit(out Unit unit) ? unit as Aircraft : null;
        }
        catch
        {
            return null;
        }
    }

    private static void EndMirror(string why)
    {
        if (_mirror == null) return;
        if (IntroDirector.Encounter == _mirror) IntroDirector.Abort();
        Music.End(_mirror, _mirror.AliveCount == 0 ? "defeated" : "withdraw");
        _mirror.Ended = true;
        Plugin.Log.LogInfo($"Encounter copy of {_mirror.SquadronName} closed ({why}).");
        _mirror = null;
    }

    /// <summary>Client upkeep: find aces' aircraft as they arrive and close the copy when they are all gone.</summary>
    internal static void Tick()
    {
        if (_mirror == null) return;
        if (Session.Authoritative || GameManager.gameState != GameState.Multiplayer)
        {
            EndMirror("left the game");
            return;
        }
        bool waiting = Time.time - _mirrorStart < ResolveGrace;
        foreach (var m in _mirror.Members)
        {
            if (m.Aircraft == null && m.NetId != 0) m.Aircraft = Resolve(m.NetId);
            m.Alive = m.Aircraft != null ? !m.Aircraft.disabled : waiting;
        }
        if (_mirror.AliveCount == 0) EndMirror("all aces gone");
    }

    internal static void Reset()
    {
        EndMirror("mission changed");
    }
}
