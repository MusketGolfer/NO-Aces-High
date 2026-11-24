using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Radio lines. Shown as UIPlus subtitles when its arcade HUD is showing messages, otherwise in the game's message feed.
/// UIPlus has no public API, so its subtitle queue is found by reflection and the feed is the fallback.
/// </summary>
internal static class Chatter
{
    private sealed class Pending
    {
        public float At;
        public string Speaker;
        public Color Colour;
        public string Text;
        public FactionHQ Audience; // who hears it: the hunted side, or the aces' own side for "allied" lines
        public ActiveEncounter Announce; // multiplayer: this line also tells clients about the encounter
        public int EndId;                // multiplayer: this line closes the clients' copy of that encounter
        public bool Enemy;               // an ace talking: the hunted side sees it with red chevrons
        public float Waited;             // seconds held back for a recorded line still playing
    }

    private static readonly List<Pending> Queue = new();
    private static readonly Color AwacsColour = new(0.55f, 0.8f, 1f);
    private static readonly Color EnemyColour = new(1f, 0.45f, 0.4f);

    private static bool _uiPlusTried;
    private static MethodInfo _queueSubtitle;
    private static PropertyInfo _messagesActive;

    internal static void Clear() => Queue.Clear();

    /// <summary>
    /// Queues a line by key. Lines written "AWACS: text" or "Command: text" (any speaker before ": " when the key
    /// is awacs/allied/defeated) are controller lines; the rest are spoken by <paramref name="speaker"/>.
    /// "allied" lines go to the aces' own side; everything else to the side they are hunting (who hear the aces'
    /// radio). Placeholders: {squadron} {title} {designation} {unit} {callsign} {pilot} {target}, and the sides:
    /// {faction} / {factionLong} / {country} for the aces, {foe} / {foeLong} for the hunted side.
    /// </summary>
    internal static void Say(ActiveEncounter enc, string key, AceMember speaker, float delay, string subjectCallsign = null,
        bool announce = false, bool closes = false)
    {
        FactionHQ audience = key == "allied" ? enc.EnemyHQ : enc.TargetHQ;
        string line = RandomNames.Line(enc.Def, key, enc.Side);
        if (string.IsNullOrEmpty(line)) return;
        line = line.Replace("{squadron}", enc.SquadronName)
                   .Replace("{title}", enc.Title)
                   .Replace("{designation}", string.IsNullOrEmpty(enc.Designation) ? enc.SquadronName : enc.Designation)
                   .Replace("{unit}", enc.Unit ?? "")
                   .Replace("{callsign}", subjectCallsign ?? speaker?.Callsign ?? "")
                   .Replace("{pilot}", speaker?.PilotName ?? "")
                   .Replace("{target}", TargetName(enc))
                   .Replace("{factionLong}", Factions.Long(enc.EnemyHQ))
                   .Replace("{faction}", Factions.Short(enc.EnemyHQ))
                   .Replace("{country}", Factions.Country(enc.EnemyHQ))
                   .Replace("{foeLong}", Factions.Long(enc.TargetHQ))
                   .Replace("{foe}", Factions.Short(enc.TargetHQ));

        string who;
        Color colour;
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        bool controller = key is "awacs" or "allied" or "defeated";
        bool aceIsController = false;
        if (colon > 0 && colon <= 24 && (controller || line.StartsWith("AWACS", StringComparison.OrdinalIgnoreCase)))
        {
            who = line.Substring(0, colon);
            line = line.Substring(colon + 2);
            colour = AwacsColour;
            aceIsController = true;
        }
        else
        {
            who = speaker != null ? speaker.Callsign : enc.SquadronName;
            colour = enc.Colour.a > 0f ? enc.Colour : EnemyColour;
        }

        Queue.Add(new Pending
        {
            At = Time.time + delay, Speaker = who, Colour = colour, Text = line, Audience = audience,
            Announce = announce ? enc : null, EndId = closes ? enc.NetId : 0, Enemy = !aceIsController,
        });
    }

    private static string TargetName(ActiveEncounter enc)
    {
        string name = Session.PlayerName(enc.TargetPlayer);
        return string.IsNullOrEmpty(name) ? "the target" : name;
    }

    internal static void Tick()
    {
        for (int i = Queue.Count - 1; i >= 0; i--)
        {
            if (Time.time < Queue[i].At) continue;
            var p = Queue[i];
            // A recorded line waits for the one playing (up to 6 s), so voices do not talk over each other.
            if (VoiceLines.Busy && p.Waited < 6f && VoiceLines.Has(p.Text)) { p.Waited += Time.deltaTime; continue; }
            Queue.RemoveAt(i);
            Show(p);
        }
    }

    /// <summary>
    /// Single player: shown here if the local pilot is on the audience side.
    /// Multiplayer rules: sent as the game's own mission message, filtered to the audience side, with the line and
    /// (on the first one) the encounter hidden inside for modded clients (NetSync). Every player with the mod,
    /// the host included, plays it as a subtitle; anyone else sees the plain line in the feed.
    /// (Simulated multiplayer goes the same way and sends nothing.)
    /// </summary>
    private static void Show(Pending p)
    {
        Plugin.Log.LogInfo($"[radio -> {Factions.Short(p.Audience)}] {p.Speaker}: {p.Text}");
        if (Session.MultiplayerRules)
        {
            try
            {
                if (NetworkSceneSingleton<MissionMessages>.i != null)
                {
                    string message = NetSync.Encode(p.Speaker, p.Colour, p.Text, p.Announce, p.EndId, p.Enemy);
                    MissionMessages.ShowMessage(message, true, p.Audience, Session.InMultiplayer && Session.IsServer);
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Mission message failed, showing locally: " + ex.Message);
            }
        }
        if (p.Audience != null && Session.LocalHQ != p.Audience) return; // not for this side
        ShowLocal(p.Speaker, p.Colour, p.Text, p.Enemy);
    }

    /// <summary>
    /// On this screen: a UIPlus subtitle, else the game's message feed. Enemy lines (an ace talking to the side it
    /// hunts) get red chevrons instead of UIPlus's blue, so the player can tell at a glance who is speaking.
    /// </summary>
    internal static void ShowLocal(string speaker, Color colour, string text, bool enemy = false)
    {
        if (enemy)
        {
            EnemyLines.Add(text);
            if (EnemyLines.Count > 16) EnemyLines.RemoveAt(0);
        }
        VoiceLines.TryPlay(null, text);
        if (!Plugin.RadioSubtitles.Value) return;
        if (TryUiPlus(speaker, colour, text)) return;
        var ui = SceneSingleton<MessageUI>.i;
        if (ui != null) ui.GameMessage(enemy ? $"<color=#{EnemyChevronHex}><noparse>>></noparse></color> {speaker}: {text} <color=#{EnemyChevronHex}><noparse><<</noparse></color>" : $"{speaker}: {text}");
    }

    // ---- red chevrons ---------------------------------------------------------------------------------

    private const string EnemyChevronHex = "FF4A3D";
    private static readonly List<string> EnemyLines = new();
    private static FieldInfo _subtitleLine, _subtitleBody;
    private static PropertyInfo _tmpText;

    /// <summary>
    /// UIPlus draws every subtitle as "&lt;&lt; body &gt;&gt;" with its blue chevron colour, in the private
    /// ArcadeHud.ShowSubtitleText. After it runs, enemy lines get the chevron colour swapped for red.
    /// </summary>
    internal static void ApplyUiPlusPatch(HarmonyLib.Harmony harmony)
    {
        var hud = HarmonyLib.AccessTools.TypeByName("UIPlus.Helpers.ArcadeHud");
        Patches.Patch(harmony, "red chevrons on enemy subtitles",
            hud != null ? HarmonyLib.AccessTools.Method(hud, "ShowSubtitleText") : null,
            postfix: nameof(ShowSubtitleTextPostfix), owner: typeof(Chatter));
        if (hud != null) _subtitleLine = HarmonyLib.AccessTools.Field(hud, "_line");
    }

    private static void ShowSubtitleTextPostfix(object s)
    {
        try
        {
            if (s == null || EnemyLines.Count == 0 || _subtitleLine == null) return;
            _subtitleBody ??= HarmonyLib.AccessTools.Field(s.GetType(), "Body");
            if (_subtitleBody?.GetValue(s) is not string body || !EnemyLines.Contains(body)) return;
            object line = _subtitleLine.GetValue(null);
            if (line == null) return;
            _tmpText ??= line.GetType().GetProperty("text");
            if (_tmpText?.GetValue(line) is not string shown) return;
            // UIPlus writes its chevron colour as a <color=#RRGGBB> tag; any such tag here is a chevron.
            string recoloured = System.Text.RegularExpressions.Regex.Replace(shown, "<color=#[0-9A-Fa-f]{6}>", $"<color=#{EnemyChevronHex}>");
            if (recoloured != shown) _tmpText.SetValue(line, recoloured);
        }
        catch
        {
            // cosmetic only
        }
    }

    private static bool TryUiPlus(string speaker, Color colour, string text)
    {
        if (!_uiPlusTried)
        {
            _uiPlusTried = true;
            try
            {
                Type hud = Type.GetType("UIPlus.Helpers.ArcadeHud, UIPlus");
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                _queueSubtitle = hud?.GetMethod("QueueSubtitle", flags, null,
                    new[] { typeof(string), typeof(Color), typeof(string), typeof(bool) }, null);
                _messagesActive = hud?.GetProperty("MessagesActive", flags);
                Plugin.Log.LogInfo(_queueSubtitle != null
                    ? "Radio lines go through UIPlus subtitles."
                    : "UIPlus subtitle queue not found; radio lines use the game's message feed.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("UIPlus lookup failed: " + ex.Message);
            }
        }
        if (_queueSubtitle == null) return false;
        try
        {
            if (_messagesActive != null && !(bool)_messagesActive.GetValue(null)) return false;
            // mission: true. UIPlus drops chat-priority lines while a mission subtitle is up and lets each new one
            // replace the queued one; mission-priority lines queue (up to 4) and pre-empt chat and Yappinator voice
            // lines. They render identically, speaker and colour included.
            _queueSubtitle.Invoke(null, new object[] { speaker, colour, text, true });
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("UIPlus subtitle failed, falling back to the message feed: " + ex.Message);
            _queueSubtitle = null;
            return false;
        }
    }

    private static bool TryParseColour(string hex, out Color colour)
    {
        colour = default;
        return !string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex, out colour);
    }
}
