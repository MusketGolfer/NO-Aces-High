using System.Collections.Generic;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// The payoff when a squadron falls: its emblem stamped DESTROYED, its name, how long it took, how many of its aces
/// were yours, and any medal just earned. Shown for about 8 seconds to the side that beat it, in the title card's
/// style (white text with a soft shadow), over normal play.
/// </summary>
internal static class SquadronDown
{
    private const float Length = 8f;
    private static ActiveEncounter _enc;
    private static float _start;
    private static string _time, _aces;
    private static List<string> _medals = new();
    private static GUIStyle _text;

    private static readonly Color Stamp = new(0.86f, 0.22f, 0.16f);
    private static readonly Color Gold = new(1f, 0.82f, 0.35f);

    /// <param name="mine">aces the local player shot down in this fight; below 0 if not known (a multiplayer client)</param>
    internal static void Show(ActiveEncounter enc, float seconds, int mine, int total, List<string> medals)
    {
        if (enc == null || !Plugin.ShowSquadronDown.Value) return;
        _enc = enc;
        _start = Time.unscaledTime + 1.5f; // after the last ace's explosion has registered
        _time = seconds > 0f ? $"TIME  {Stats.Mmss(seconds)}" : null;
        _aces = mine >= 0 ? $"ACES DOWNED BY YOU  {mine} / {total}" : null;
        _medals = medals ?? new List<string>();
    }

    internal static void Reset() => _enc = null;

    internal static void OnGUI()
    {
        if (_enc == null || Event.current.type != EventType.Repaint) return;
        float t = Time.unscaledTime - _start;
        if (t < 0f) return;
        if (t > Length) { _enc = null; return; }
        if (IntroDirector.Playing) return;

        _text ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow, wordWrap = false };
        _text.normal.textColor = Color.white;
        float a = Mathf.Clamp01(t / 0.4f) * Mathf.Clamp01((Length - t) / 0.8f);
        float W = Screen.width, H = Screen.height;
        var keepMatrix = GUI.matrix;
        var keepColour = GUI.color;
        GUI.depth = -1000;

        float cx = W * 0.5f, top = H * 0.2f;
        float size = H * 0.16f;
        var emblem = _enc.Emblem?.FrameAt(0f);
        if (emblem != null)
        {
            GUI.color = new Color(0.75f, 0.75f, 0.75f, a * 0.9f); // greyed: it is over
            GUI.DrawTexture(new Rect(cx - size * 0.5f, top, size, size), emblem, ScaleMode.ScaleToFit, true);
        }

        // The stamp lands a moment later, slightly too big, and settles.
        float st = Mathf.Clamp01((t - 0.35f) / 0.25f);
        if (st > 0f)
        {
            float scale = Mathf.Lerp(1.6f, 1f, st);
            var pivot = new Vector2(cx, top + size * 0.55f);
            GUIUtility.RotateAroundPivot(-12f, pivot);
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), pivot);
            int fs = Mathf.RoundToInt(H * 0.05f);
            Vector2 dim = Measure("DESTROYED", fs, FontStyle.Bold);
            var box = new Rect(pivot.x - dim.x * 0.5f - fs * 0.35f, pivot.y - dim.y * 0.5f - fs * 0.1f, dim.x + fs * 0.7f, dim.y + fs * 0.2f);
            float line = Mathf.Max(2f, fs * 0.08f);
            GUI.color = new Color(Stamp.r, Stamp.g, Stamp.b, a * st * 0.95f);
            GUI.DrawTexture(new Rect(box.x, box.y, box.width, line), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x, box.yMax - line, box.width, line), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x, box.y, line, box.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.xMax - line, box.y, line, box.height), Texture2D.whiteTexture);
            Label("DESTROYED", pivot, fs, FontStyle.Bold, Stamp, a * st);
            GUI.matrix = keepMatrix;
        }

        float y = top + size + H * 0.045f;
        Label(_enc.SquadronName.ToUpperInvariant(), new Vector2(cx, y), Mathf.RoundToInt(H * 0.05f), FontStyle.Normal, Color.white, a);
        y += H * 0.045f;
        string sub = !string.IsNullOrEmpty(_enc.Unit) ? _enc.Unit : _enc.Title;
        if (!string.IsNullOrEmpty(sub)) { Label(sub.ToUpperInvariant(), new Vector2(cx, y), Mathf.RoundToInt(H * 0.02f), FontStyle.Bold, new Color(0.85f, 0.85f, 0.85f), a); y += H * 0.04f; }
        float a2 = a * Mathf.Clamp01((t - 0.9f) / 0.4f);
        if (_time != null) { Label(_time, new Vector2(cx, y), Mathf.RoundToInt(H * 0.022f), FontStyle.Bold, Color.white, a2); y += H * 0.032f; }
        if (_aces != null) { Label(_aces, new Vector2(cx, y), Mathf.RoundToInt(H * 0.022f), FontStyle.Bold, Color.white, a2); y += H * 0.04f; }
        float a3 = a * Mathf.Clamp01((t - 1.6f) / 0.4f);
        foreach (var m in _medals)
        {
            Label($"★  MEDAL  {m.ToUpperInvariant()}", new Vector2(cx, y), Mathf.RoundToInt(H * 0.024f), FontStyle.Bold, Gold, a3);
            y += H * 0.034f;
        }
        GUI.matrix = keepMatrix;
        GUI.color = keepColour;
    }

    private static Vector2 Measure(string text, int size, FontStyle style)
    {
        _text.fontSize = Mathf.Max(8, size);
        _text.fontStyle = style;
        return _text.CalcSize(new GUIContent(text));
    }

    private static void Label(string text, Vector2 centre, int size, FontStyle style, Color colour, float alpha)
    {
        if (alpha <= 0f) return;
        Vector2 dim = Measure(text, size, style);
        var rect = new Rect(centre.x - dim.x * 0.5f, centre.y - dim.y * 0.5f, dim.x, dim.y);
        float shadow = Mathf.Max(1f, size * 0.06f);
        GUI.color = new Color(0f, 0f, 0f, 0.7f * alpha);
        GUI.Label(new Rect(rect.x + shadow, rect.y + shadow, rect.width, rect.height), text, _text);
        GUI.color = new Color(colour.r, colour.g, colour.b, alpha);
        GUI.Label(rect, text, _text);
    }
}
