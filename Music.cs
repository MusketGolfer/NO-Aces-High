using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NOAcesHigh;

/// <summary>
/// Ace music, played through the game's own MusicManager (so it follows the music volume and crossfades with the
/// game's tracks), for the side being hunted only.
///
/// Slots, in order:
/// - intro: as the encounter is called in. The intro cinematic / title card waits until it has played out (setting
///   MusicGatesIntro, capped by MaxMusicGateSeconds), so the build-up leads into the cutscene;
/// - cutscene: during the intro itself (optional; without it the intro track carries on);
/// - loop: the sortie, looping until the encounter ends;
/// - outro: when the squadron is beaten; escape: when it withdraws (falls back to outro).
/// Files (.ogg / .wav / .mp3) are found per slot in this order: the squadron's own "music" entries in squadron.json
/// (paths relative to its folder), then BepInEx/config/NOAcesHigh/Music/&lt;BDF|PALA&gt;/ for the aces' side, then
/// BepInEx/config/NOAcesHigh/Music/. A slot with no file is skipped.
/// </summary>
internal static class Music
{
    internal static readonly string[] Slots = { "intro", "cutscene", "loop", "outro", "escape" };
    private static readonly string[] Extensions = { ".ogg", ".wav", ".mp3" };
    private const float Priority = 50f;

    private enum State { Idle, Loading, Intro, Cutscene, Loop, Outro }

    private static State _state;
    private static ActiveEncounter _enc;
    /// <summary>
    /// What a slot plays: a streamed file and the stretch of it to play (End 0 = to the end of the file). Files are
    /// streamed from disk, never decoded whole, so a long soundtrack costs nothing to load; the game's music player
    /// is moved to Start after it starts the clip, and kept inside the section by Tick.
    /// </summary>
    private sealed class Slot
    {
        public AudioClip Clip;
        public float Start, End;
        public float Stop => End > 0f ? Mathf.Min(End, Clip.length) : Clip.length;
        public float Length => Mathf.Max(0.1f, Stop - Start);
        public string Name => Start > 0f || End > 0f ? $"{Clip.name} {Start:0.#}-{Stop:0.#}" : Clip.name;
    }

    private static readonly Dictionary<string, Slot> Clips = new();          // slot -> what it plays, for the current encounter
    private static readonly Dictionary<string, AudioClip> Cache = new();     // path -> streamed clip
    private static Slot _playing;                                            // the section the player should be in
    private static bool _playingLoops;
    private static float _sectionStartedAt;
    private static readonly HarmonyLib.AccessTools.FieldRef<MusicManager, AudioSource> CurrentSource =
        HarmonyLib.AccessTools.FieldRefAccess<MusicManager, AudioSource>("currentSource");
    private static readonly HarmonyLib.AccessTools.FieldRef<MusicManager, AudioSource> FadeSource =
        HarmonyLib.AccessTools.FieldRefAccess<MusicManager, AudioSource>("fadeSource");
    private static float _trackEnds;      // unscaled time the current one-shot track ends
    private static float _gateStart;
    private static bool _introStarted;

    internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "NOAcesHigh", "Music");

    /// <summary>Creates the music folders with a short README the first time.</summary>
    internal static void EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Folder, "BDF"));
            Directory.CreateDirectory(Path.Combine(Folder, "PALA"));
            string readme = Path.Combine(Folder, "README.txt");
            if (File.Exists(readme)) return;
            File.WriteAllText(readme,
                "NO Aces High music\r\n" +
                "==================\r\n\r\n" +
                "Drop tracks here named after their slot (.ogg, .wav or .mp3):\r\n\r\n" +
                "  intro     plays as an ace squadron is called in. The intro cutscene / title card waits for it to finish\r\n" +
                "            (7. HUD and Music > IntroWaitsForMusic, capped by MaxIntroMusicWait).\r\n" +
                "  cutscene  plays during the intro itself (optional; without it the intro track carries on).\r\n" +
                "  loop      the sortie: loops until the squadron is beaten or leaves.\r\n" +
                "  outro     the squadron is beaten.\r\n" +
                "  escape    the squadron withdrew (falls back to outro).\r\n\r\n" +
                "Tracks in BDF\\ or PALA\\ are used for that side's aces before the ones here.\r\n" +
                "A squadron can name its own in squadron.json:\r\n" +
                "  \"music\": { \"intro\": \"music/intro.ogg\", \"loop\": \"music/theme.ogg\", \"outro\": \"music/win.ogg\" }\r\n" +
                "(paths relative to the squadron's folder). Any slot without a file is skipped.\r\n" +
                "Music only plays for the side being hunted, at the game's music volume.\r\n");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Music: could not set up the music folder: " + ex.Message);
        }
    }

    internal static ActiveEncounter Current => _enc;

    /// <summary>The intro may start: no intro music, it has played out, the gate is off, or it has waited long enough.</summary>
    internal static bool IntroGateOpen
    {
        get
        {
            if (!Plugin.MusicEnabled.Value || !Plugin.MusicGatesIntro.Value || _enc == null) return true;
            if (Clock.Now - _gateStart > Plugin.MaxMusicGateSeconds.Value) return true;
            if (_state == State.Loading) return false;
            return _state != State.Intro || Clock.Now >= _trackEnds;
        }
    }

    /// <summary>The encounter has been called in for this side.</summary>
    internal static void Begin(ActiveEncounter enc)
    {
        if (!Plugin.MusicEnabled.Value || enc == null || AudioCapture.Requested) return; // trailer recording: no music
        Stop(fade: 1f);
        _enc = enc;
        _state = State.Loading;
        _gateStart = Clock.Now;
        _introStarted = false;
        Clips.Clear();
        Plugin.Instance.StartCoroutine(LoadAll(enc));
    }

    private static IEnumerator LoadAll(ActiveEncounter enc)
    {
        foreach (string slot in Slots)
        {
            var track = Find(enc, slot);
            if (track == null) continue;
            string path = track.Path;
            if (!Cache.TryGetValue(path, out var clip) || clip == null)
            {
                using var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, TypeOf(path));
                if (req.downloadHandler is DownloadHandlerAudioClip dh)
                {
                    // Streamed from disk: nothing is decoded up front, so even a long soundtrack loads without a hitch.
                    dh.streamAudio = true;
                }
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogWarning($"Music: could not load {path}: {req.error}");
                    continue;
                }
                clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null) continue;
                clip.name = Path.GetFileName(path);
                UnityEngine.Object.DontDestroyOnLoad(clip);
                Cache[path] = clip;
            }
            if (_enc != enc) yield break; // replaced while loading
            var s = new Slot { Clip = clip, Start = track.HasRange ? track.Start : 0f, End = track.HasRange ? track.End : 0f };
            if (s.Start >= clip.length - 0.1f) { Plugin.Log.LogWarning($"Music: {slot} starts at {s.Start:0} s, past the end of {clip.name} ({clip.length:0} s)."); continue; }
            Clips[slot] = s;
        }
        if (_enc != enc) yield break;
        Plugin.Log.LogInfo(Clips.Count == 0
            ? "Music: no ace music found (see 8. Music)."
            : "Music: " + string.Join(", ", Clips.Select(c => $"{c.Key}={c.Value.Name} ({c.Value.Length:0} s)")));

        if (Clips.TryGetValue("intro", out var intro)) PlayOnce(intro, State.Intro, fadeIn: 0.5f);
        else if (!IntroDirector.Active) PlayLoop();
        else _state = State.Intro; // nothing to play yet; the cutscene or loop takes over
    }

    /// <summary>The intro (cinematic or title card) has started.</summary>
    internal static void OnIntroStart()
    {
        if (_enc == null || _introStarted) return;
        _introStarted = true;
        if (Clips.TryGetValue("cutscene", out var clip)) PlayOnce(clip, State.Cutscene, fadeIn: 0.3f);
    }

    /// <summary>The encounter is over: outro (beaten) or escape (withdrew), else fade out.</summary>
    internal static void End(ActiveEncounter enc, string reason)
    {
        if (enc == null || enc != _enc) return;
        bool escaped = reason == "withdraw";
        Slot clip = null;
        if (escaped) Clips.TryGetValue("escape", out clip);
        if (clip == null) Clips.TryGetValue("outro", out clip);
        if (clip != null) PlayOnce(clip, State.Outro, fadeIn: 0.8f);
        else Stop(fade: 3f);
        _enc = null;
    }

    internal static void Stop(float fade = 1.5f)
    {
        if (_state is State.Intro or State.Cutscene or State.Loop or State.Outro)
        {
            try { MusicManager.i?.FadeOut(fade); } catch { /* no music manager */ }
        }
        _state = State.Idle;
        _enc = null;
        _playing = null;
    }

    /// <summary>The game music source playing our clip (the new one is the fade source until the crossfade ends).</summary>
    private static AudioSource OurSource()
    {
        try
        {
            var mm = MusicManager.i;
            if (mm == null || _playing == null) return null;
            foreach (var src in new[] { FadeSource(mm), CurrentSource(mm) })
                if (src != null && src.clip == _playing.Clip && src.isPlaying) return src;
        }
        catch { /* no music manager */ }
        return null;
    }

    /// <summary>Keeps the player inside the section: loops jump back to Start, one-shots fade at their end.</summary>
    private static void KeepInSection()
    {
        if (_playing == null) return;
        var src = OurSource();
        if (src == null) return;
        float t = src.time;
        // Just started (the game starts clips at 0): move to the section.
        if (Clock.Now - _sectionStartedAt < 0.5f && t < _playing.Start - 0.25f) { src.time = _playing.Start; return; }
        if (_playingLoops)
        {
            if (t >= _playing.Stop - 0.05f || t < _playing.Start - 0.5f) src.time = _playing.Start;
        }
        else if (_playing.End > 0f && t >= _playing.Stop - 0.8f && _state != State.Loop)
        {
            if (_state == State.Outro) { try { MusicManager.i?.FadeOut(0.8f); } catch { } _playing = null; }
        }
    }

    internal static void Tick()
    {
        if (_state == State.Idle || _state == State.Loading) return;
        KeepInSection();
        if (_state == State.Outro)
        {
            if (Clock.Now >= _trackEnds) _state = State.Idle;
            return;
        }
        if (_enc == null || _enc.Ended) return;
        // One-shot sections roll into the loop once they end and the intro is not holding them.
        bool sectionOver = Clock.Now >= _trackEnds - 0.5f;
        bool introBusy = IntroDirector.Active && IntroDirector.Encounter == _enc && _state == State.Cutscene;
        if ((_state == State.Intro || _state == State.Cutscene) && sectionOver && !introBusy) PlayLoop();
        // A cut section held by the intro past its end: fade it rather than run on into the rest of the file.
        else if ((_state == State.Intro || _state == State.Cutscene) && _playing is { End: > 0f } && Clock.Now >= _trackEnds + 0.5f)
        {
            try { MusicManager.i?.FadeOut(1.5f); } catch { }
            _playing = null;
        }
    }

    private static void PlayLoop()
    {
        if (Clips.TryGetValue("loop", out var loop))
        {
            CrossFade(loop, repeat: true, fadeIn: 1.5f);
            _state = State.Loop;
        }
        else _state = State.Loop; // nothing to loop: the last section just ends
    }

    private static void PlayOnce(Slot clip, State state, float fadeIn)
    {
        CrossFade(clip, repeat: false, fadeIn: fadeIn);
        _state = state;
        _trackEnds = Clock.Now + clip.Length;
    }

    private static void CrossFade(Slot slot, bool repeat, float fadeIn)
    {
        try
        {
            var mm = MusicManager.i;
            if (mm == null) return;
            // A section that does not run to the end loops by our hand (Tick), not the player's.
            mm.CrossFadeMusic(slot.Clip, 1.2f, fadeIn, repeat, allowReplay: true, replacePlaying: true, Priority);
            _playing = slot;
            _playingLoops = repeat;
            _sectionStartedAt = Clock.Now;
            var src = FadeSource(mm);
            if (src != null && src.clip == slot.Clip && slot.Start > 0f) src.time = slot.Start;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Music: could not play " + slot.Clip.name + ": " + ex.Message);
        }
    }

    // ---- files -----------------------------------------------------------------------------------------

    /// <summary>A file, and optionally the section of it a slot plays (seconds; End 0 = to the end).</summary>
    internal sealed class TrackRef
    {
        public string Path;
        public float Start, End;
        public bool HasRange;
    }

    /// <summary>
    /// Reads a squadron.json music value: "music/intro.ogg" (a file), "0:00-0:18" (a section of the squadron's
    /// "file"), or "music/ost.ogg@1:02-1:40" (a section of another file). Times are m:ss, m:ss.fff or plain seconds;
    /// an empty end ("2:45-") runs to the end of the file. Null if it cannot be read (error says why).
    /// </summary>
    internal static TrackRef Parse(string value, string sharedFile, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value)) { error = "empty"; return null; }
        string v = value.Trim();
        string file = v, range = null;
        int at = v.LastIndexOf('@');
        if (at >= 0) { file = v.Substring(0, at).Trim(); range = v.Substring(at + 1).Trim(); }
        else if (LooksLikeRange(v)) { file = sharedFile; range = v; }
        if (string.IsNullOrWhiteSpace(file)) { error = $"\"{value}\" is a time range, but there is no \"file\" for it to come from"; return null; }
        var t = new TrackRef { Path = file };
        if (range == null) return t;
        int dash = range.IndexOf('-');
        if (dash < 0 || !TryTime(range.Substring(0, dash), out t.Start) ||
            (range.Length > dash + 1 && !TryTime(range.Substring(dash + 1), out t.End)))
        {
            error = $"\"{range}\" is not a time range like 0:18-2:45";
            return null;
        }
        if (t.End > 0f && t.End <= t.Start) { error = $"\"{range}\" ends before it starts"; return null; }
        t.HasRange = true;
        return t;
    }

    private static bool LooksLikeRange(string v) => v.Contains("-") && v.All(c => char.IsDigit(c) || c == ':' || c == '.' || c == '-' || c == ' ');

    private static bool TryTime(string s, out float seconds)
    {
        seconds = 0f;
        s = s.Trim();
        if (s.Length == 0) return true;
        float total = 0f;
        foreach (var part in s.Split(':'))
        {
            if (!float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n) || n < 0f) return false;
            total = total * 60f + n;
        }
        seconds = total;
        return true;
    }

    private static TrackRef Find(ActiveEncounter enc, string slot)
    {
        var def = enc.Def;
        if (def?.Music != null && def.Music.TryGetValue(slot, out var own) && !string.IsNullOrWhiteSpace(own) && def.Folder != null)
        {
            def.Music.TryGetValue("file", out var shared);
            var t = Parse(own, shared, out string error);
            if (t == null) Plugin.Log.LogWarning($"Music: {def.Squadron}'s {slot}: {error}.");
            else
            {
                t.Path = System.IO.Path.Combine(def.Folder, t.Path);
                if (File.Exists(t.Path)) return t;
                Plugin.Log.LogWarning($"Music: {def.Squadron}'s {slot} track '{own}' not found.");
            }
        }
        string side = enc.Side switch { Side.BDF => "BDF", Side.PALA => "PALA", _ => null };
        if (side != null)
        {
            string p = InFolder(System.IO.Path.Combine(Folder, side), slot);
            if (p != null) return new TrackRef { Path = p };
        }
        string g = InFolder(Folder, slot);
        return g != null ? new TrackRef { Path = g } : null;
    }

    private static readonly Dictionary<string, AudioClip> Sections = new();

    /// <summary>A new clip holding just the slot's section of a loaded file (cached per file and range).</summary>
    internal static AudioClip Section(AudioClip whole, TrackRef t)
    {
        string key = $"{t.Path}@{t.Start}-{t.End}";
        if (Sections.TryGetValue(key, out var cached) && cached != null) return cached;
        try
        {
            int freq = whole.frequency, channels = whole.channels;
            int start = Mathf.Clamp(Mathf.RoundToInt(t.Start * freq), 0, whole.samples);
            int end = t.End > 0f ? Mathf.Clamp(Mathf.RoundToInt(t.End * freq), start, whole.samples) : whole.samples;
            int length = end - start;
            if (length <= freq / 10)
            {
                Plugin.Log.LogWarning($"Music: section {t.Start:0.##}-{t.End:0.##} s of {whole.name} ({whole.length:0} s long) is empty.");
                return null;
            }
            var data = new float[length * channels];
            if (!whole.GetData(data, start)) throw new Exception("the file could not be read back for cutting");
            var clip = AudioClip.Create($"{whole.name} {t.Start:0.##}-{(t.End > 0f ? t.End : whole.length):0.##}", length, channels, freq, false);
            clip.SetData(data, 0);
            UnityEngine.Object.DontDestroyOnLoad(clip);
            Sections[key] = clip;
            return clip;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Music: could not cut {t.Start:0.##}-{t.End:0.##} s out of {whole.name}: {ex.Message}");
            return null;
        }
    }

    private static string InFolder(string folder, string slot)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var ext in Extensions)
        {
            string p = Path.Combine(folder, slot + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    internal static AudioType TypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ogg" => AudioType.OGGVORBIS,
        ".wav" => AudioType.WAV,
        ".mp3" => AudioType.MPEG,
        _ => AudioType.UNKNOWN,
    };

    internal static string Describe() =>
        _state == State.Idle ? "music idle" : $"music {_state.ToString().ToLowerInvariant()}" + (Clips.Count > 0 ? $" ({string.Join(", ", Clips.Keys)})" : "") +
        (OurSource() is { } src ? $" at {src.time:0.0} s of {_playing?.Name}" : "");
}
