using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NOAcesHigh;

/// <summary>
/// Recorded radio lines. A squadron.json "voice" block works like "music": each line key (intro, break, kill...)
/// names audio for the text lines of the same key, in the same order, as a file ("lines/break1.ogg"), a section of the
/// block's shared "file" ("0:04-0:07"), or a section of another file ("lines/other.ogg@0:04-0:07"):
///
///   "voice": { "file": "lines/sol.ogg", "intro": ["0:00-0:03"], "break": ["0:03-0:05", "lines/break2.ogg"] }
///
/// Paths are relative to the squadron folder, then to its "lines" folder. The clips are loaded when the squadron
/// launches (on clients too, if they have the same squadron installed). A line is played when its subtitle shows,
/// found by its text (placeholders such as {target} match anything), through a radio filter at the VoiceVolume
/// setting; one at a time, so a line due while another plays waits for it (up to a few seconds).
/// </summary>
internal static class VoiceLines
{
    private sealed class Entry
    {
        public string Key;
        public Regex Text;           // null: a voice with no text line, played by key
        public Music.TrackRef Track;
        public AudioClip Clip;
    }

    private static readonly List<Entry> Entries = new();
    private static readonly Dictionary<string, AudioClip> Cache = new(); // path -> whole file
    private static ActiveEncounter _enc;
    private static AudioSource _source;
    private static AudioLowPassFilter _low;
    private static AudioHighPassFilter _high;
    private static AudioDistortionFilter _crunch;

    internal static float PlayingUntil { get; private set; }
    internal static bool Busy => Time.unscaledTime < PlayingUntil;

    /// <summary>Reads a squadron's voice block into (key, index, track) triples; errors go to <paramref name="problems"/>.</summary>
    internal static List<(string key, int index, Music.TrackRef track)> Read(SquadronDef def, List<string> problems = null)
    {
        var list = new List<(string, int, Music.TrackRef)>();
        if (def?.Voice == null) return list;
        string shared = def.Voice.Value<string>("file");
        foreach (var prop in def.Voice.Properties())
        {
            if (prop.Name == "file") continue;
            var values = prop.Value.Type == JTokenType.Array ? prop.Value.Select(v => v.Type == JTokenType.Null ? null : v.ToString()).ToList()
                : new List<string> { prop.Value.ToString() };
            for (int i = 0; i < values.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i])) continue; // a gap: that text line has no voice
                var t = Music.Parse(values[i], shared, out string error);
                if (t == null) { problems?.Add($"\"voice\" \"{prop.Name}\" #{i + 1}: {error}."); continue; }
                string path = Resolve(def.Folder, t.Path);
                if (path == null) { problems?.Add($"\"voice\" \"{prop.Name}\" #{i + 1}: file \"{t.Path}\" not found (looked in the squadron folder and its \"lines\" folder)."); continue; }
                t.Path = path;
                list.Add((prop.Name, i, t));
            }
        }
        return list;
    }

    private static string Resolve(string folder, string file)
    {
        if (folder == null || string.IsNullOrWhiteSpace(file)) return null;
        foreach (var p in new[] { Path.Combine(folder, file), Path.Combine(folder, "lines", file) })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Loads the voice clips for an encounter the local player will hear.</summary>
    internal static void Prepare(ActiveEncounter enc)
    {
        Entries.Clear();
        _enc = enc;
        if (enc?.Def?.Voice == null || !Plugin.VoiceEnabled.Value) return;
        var def = enc.Def;
        foreach (var (key, index, track) in Read(def))
        {
            Regex text = null;
            if (def.Lines != null && def.Lines.TryGetValue(key, out var lines) && index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
                text = Pattern(lines[index]);
            Entries.Add(new Entry { Key = key, Text = text, Track = track });
        }
        if (Entries.Count > 0) Plugin.Instance.StartCoroutine(Load(enc));
    }

    /// <summary>The line as written, with its {placeholders} matching anything and an optional "Speaker: " in front.</summary>
    private static Regex Pattern(string line)
    {
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon <= 24) line = line.Substring(colon + 2); // controller lines show without the speaker
        string escaped = Regex.Escape(line.Trim());
        escaped = Regex.Replace(escaped, @"\\\{[A-Za-z]+}", ".*");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IEnumerator Load(ActiveEncounter enc)
    {
        int loaded = 0;
        foreach (var e in Entries.ToList())
        {
            if (_enc != enc) yield break;
            if (!Cache.TryGetValue(e.Track.Path, out var whole) || whole == null)
            {
                using var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(e.Track.Path).AbsoluteUri, Music.TypeOf(e.Track.Path));
                if (req.downloadHandler is DownloadHandlerAudioClip dh) dh.streamAudio = true; // streamed: no decode hitch
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Plugin.Log.LogWarning($"Voice: could not load {e.Track.Path}: {req.error}"); continue; }
                whole = DownloadHandlerAudioClip.GetContent(req);
                if (whole == null) continue;
                whole.name = Path.GetFileName(e.Track.Path);
                UnityEngine.Object.DontDestroyOnLoad(whole);
                Cache[e.Track.Path] = whole;
            }
            e.Clip = whole;
            if (e.Track.HasRange && e.Track.Start >= whole.length - 0.05f) { Plugin.Log.LogWarning($"Voice: {e.Key} starts past the end of {whole.name}."); e.Clip = null; continue; }
            loaded++;
        }
        if (_enc == enc) Plugin.Log.LogInfo($"Voice: {loaded} recorded line(s) ready for {enc.SquadronName}.");
    }

    internal static void Reset()
    {
        Entries.Clear();
        _enc = null;
        PlayingUntil = 0f;
        if (_source != null) _source.Stop();
    }

    /// <summary>Whether a line with this text has a recording loaded (so the radio can wait for the one playing).</summary>
    internal static bool Has(string text) => Find(null, text) != null;

    /// <summary>Plays the recording for a shown line, if there is one. A key-only voice (no text) is played by key.</summary>
    internal static bool TryPlay(string key, string text)
    {
        if (!Plugin.VoiceEnabled.Value || Entries.Count == 0) return false;
        var e = Find(key, text);
        if (e?.Clip == null) return false;
        float start = e.Track.HasRange ? e.Track.Start : 0f;
        float stop = e.Track.HasRange && e.Track.End > 0f ? Mathf.Min(e.Track.End, e.Clip.length) : e.Clip.length;
        Play(e.Clip, start, stop);
        Plugin.Log.LogInfo($"Voice: playing {e.Clip.name} {start:0.#}-{stop:0.#} s for \"{text}\".");
        return true;
    }

    private static Entry Find(string key, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            string t = text.Trim();
            var byText = Entries.Where(e => e.Clip != null && e.Text != null && e.Text.IsMatch(t)).ToList();
            if (byText.Count > 0) return byText[UnityEngine.Random.Range(0, byText.Count)];
        }
        if (key == null) return null;
        var byKey = Entries.Where(e => e.Clip != null && e.Text == null && e.Key == key).ToList();
        return byKey.Count > 0 ? byKey[UnityEngine.Random.Range(0, byKey.Count)] : null;
    }

    /// <summary>Stops a section at its end (the clip is the whole streamed file).</summary>
    internal static void Tick()
    {
        if (_source != null && _source.isPlaying && Time.unscaledTime >= PlayingUntil - 0.15f) _source.Stop();
    }

    private static void Play(AudioClip clip, float start, float stop)
    {
        if (_source == null)
        {
            var go = new GameObject("NOAcesHigh Radio");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.ignoreListenerPause = true;
            _high = go.AddComponent<AudioHighPassFilter>();
            _low = go.AddComponent<AudioLowPassFilter>();
            _crunch = go.AddComponent<AudioDistortionFilter>();
        }
        bool radio = Plugin.VoiceRadioFilter.Value;
        _high.enabled = _low.enabled = _crunch.enabled = radio;
        _high.cutoffFrequency = 320f;
        _low.cutoffFrequency = 3800f;
        _crunch.distortionLevel = 0.25f;
        _source.volume = Plugin.VoiceVolume.Value;
        _source.Stop();
        _source.clip = clip;
        _source.Play();
        _source.time = start;
        PlayingUntil = Time.unscaledTime + (stop - start) + 0.15f;
    }
}
