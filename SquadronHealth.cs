using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOAcesHigh;

/// <summary>
/// Boss-style health bars along the bottom of the screen for the ace squadron hunting your side: one bar per
/// aircraft, the lead's under the squadron's name, the wingmen's after it (a four-ship shows four), in the game's
/// HUD "Danger" colour (Accessibility colour theme).
///
/// The game keeps no single health value for an aircraft. Each part has hit points out of 100 (what the game's own
/// damage display reads), and a detached part is gone, so an ace's health here is the mass-weighted condition of its
/// parts: losing a wing costs more than losing a flap. A destroyed ace, or one whose pilot is dead or out, reads
/// zero. Aces that left the fight (winchester, bingo) show dimmed. Built inside UIPlus's HUD canvas like the threat
/// gauge, with an IMGUI fallback when that HUD is not up.
/// </summary>
internal static class SquadronHealth
{
    private const float BarHeight = 8f, BarGap = 30f;

    private sealed class PartWeight
    {
        public UnitPart Part;
        public float Mass;
    }

    private static readonly Dictionary<Aircraft, List<PartWeight>> Parts = new();
    private static readonly Dictionary<Aircraft, float> Shown = new();

    // ---- health -------------------------------------------------------------------------------------------

    /// <summary>0..1: the aircraft's parts' condition (hit points / 100, detached = 0), weighted by part mass.</summary>
    internal static float Health(Aircraft a)
    {
        if (a == null || a.disabled) return 0f;
        try
        {
            if (a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null && (a.pilots[0].dead || a.pilots[0].ejected)) return 0f;
            if (!Parts.TryGetValue(a, out var list))
            {
                list = new List<PartWeight>();
                if (a.partLookup != null)
                    foreach (var p in a.partLookup)
                        if (p != null) list.Add(new PartWeight { Part = p, Mass = Mathf.Max(p.mass, 1f) });
                Parts[a] = list;
            }
            float total = 0f, left = 0f;
            foreach (var pw in list)
            {
                total += pw.Mass;
                if (pw.Part == null || pw.Part.IsDetached()) continue;
                left += pw.Mass * Mathf.Clamp01(pw.Part.hitPoints / 100f);
            }
            return total > 0f ? left / total : 1f;
        }
        catch { return 1f; }
    }

    private static Color Danger()
    {
        try
        {
            var theme = NuclearOption.UIStyleSystem.ThemeManager.Active?.ColorTheme;
            if (theme != null) return theme.Alert;
        }
        catch { /* no theme yet */ }
        return new Color(1f, 0.29f, 0.24f);
    }

    private struct Entry
    {
        public float Health;
        public bool Left;   // retreated from the fight
    }

    private static ActiveEncounter Current()
    {
        if (!Plugin.HealthBars.Value || !Plugin.Enabled.Value) return null;
        var hq = Session.LocalHQ;
        if (hq == null || Session.LocalAircraft == null) return null;
        if (IntroDirector.Playing && IntroDirector.CameraCut) return null;
        var enc = Encounters.ActiveAgainst(hq) ?? NetSync.Mirror;
        // The bars come up once the squadron has been introduced: not while its intro is queued, waiting for a
        // safe moment or playing (camera shots and title card). With intros off there is nothing to wait for.
        if (enc != null && IntroDirector.Active && IntroDirector.Encounter == enc) return null;
        return enc is { Ended: false } && enc.Members.Count > 0 ? enc : null;
    }

    private static List<Entry> Entries(ActiveEncounter enc)
    {
        var list = new List<Entry>();
        foreach (var m in enc.Members.OrderByDescending(m => m.Lead))
        {
            float target = m.Alive ? Health(m.Aircraft) : 0f;
            float shown = m.Aircraft != null && Shown.TryGetValue(m.Aircraft, out var s) ? s : target;
            shown = Mathf.MoveTowards(shown, target, Clock.Delta * 0.8f); // drains visibly rather than jumping
            if (m.Aircraft != null) Shown[m.Aircraft] = shown;
            list.Add(new Entry { Health = shown, Left = Encounters.IsRetreating(m.Aircraft) });
        }
        return list;
    }

    internal static void Reset()
    {
        Parts.Clear();
        Shown.Clear();
    }

    // ---- layout ------------------------------------------------------------------------------------------

    /// <summary>Bars per row: up to eight; past eight the rest stack in a second row on top.</summary>
    private const int PerRow = 8;
    private const float RowStep = BarHeight + 14f;

    private static int Columns(int count) => Mathf.Clamp(count, 1, PerRow);
    private static int Rows(int count) => Mathf.Max(1, (count + PerRow - 1) / PerRow);

    // ---- UIPlus-hosted version --------------------------------------------------------------------------

    private static GameObject _strip, _builtFor;
    private static TextMeshProUGUI _label, _shadow;
    private static RectTransform _labelBox;
    private static readonly List<(RectTransform fill, Image fillImage, Image track)> Bars = new(); // lead first
    private static int _builtCount = -1;

    internal static void Tick()
    {
        try
        {
            var root = UiPlusRoot();
            var enc = Current();
            if (root == null || !root.activeInHierarchy || HudLayoutBridge.Editing)
            {
                if (_strip != null) _strip.SetActive(false);
                return;
            }
            if (_strip == null || _builtFor != root) Build(root);
            if (_strip == null) return;
            _strip.SetActive(enc != null);
            if (enc == null) return;

            var entries = Entries(enc);
            if (_builtCount != entries.Count) BuildBars(entries.Count);
            Color danger = Danger();
            _label.text = enc.SquadronName.ToUpperInvariant();
            if (_shadow != null)
            {
                _shadow.text = _label.text;
                if (_shadow.font != _label.font) _shadow.font = _label.font;
            }
            _label.color = Color.white;
            var rt = (RectTransform)_strip.transform;
            var place = HudLayoutBridge.Health;
            float half = place.Width / (2f * Mathf.Max(0.05f, place.Scale)); // anchors span the unscaled width
            rt.anchorMin = new Vector2(place.X - half, place.Y);
            rt.anchorMax = new Vector2(place.X + half, place.Y);
            rt.localScale = new Vector3(place.Scale, place.Scale, 1f);
            rt.localRotation = Quaternion.Euler(0f, 0f, place.Rotation);
            for (int i = 0; i < entries.Count && i < Bars.Count; i++)
            {
                var (fill, fillImage, track) = Bars[i];
                float a = entries[i].Left ? 0.35f : 1f;
                fill.anchorMax = new Vector2(Mathf.Max(0.0001f, entries[i].Health), 1f);
                fillImage.color = new Color(danger.r, danger.g, danger.b, a);
                track.color = new Color(danger.r * 0.25f, danger.g * 0.25f, danger.b * 0.25f, 0.55f * a);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Squadron health bars failed, hiding them: " + ex.Message);
            if (_strip != null) UnityEngine.Object.Destroy(_strip);
            _strip = null;
        }
    }

    private static System.Reflection.FieldInfo _root, _font, _fontMaterial;
    private static bool _lookedUp;

    private static GameObject UiPlusRoot()
    {
        if (!_lookedUp)
        {
            _lookedUp = true;
            var hud = AccessTools.TypeByName("UIPlus.Helpers.ArcadeHud");
            if (hud != null)
            {
                _root = AccessTools.Field(hud, "_root");
                _font = AccessTools.Field(hud, "_font");
                _fontMaterial = AccessTools.Field(hud, "_fontMaterial");
            }
        }
        try { return _root?.GetValue(null) as GameObject; }
        catch { return null; }
    }

    private static void Build(GameObject root)
    {
        if (_strip != null) UnityEngine.Object.Destroy(_strip);
        Bars.Clear();
        _builtCount = -1;
        _builtFor = root;
        _strip = new GameObject("NOAcesHigh Squadron Health", typeof(RectTransform));
        var rt = (RectTransform)_strip.transform;
        rt.SetParent(root.transform, false);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(0f, 0f);

        // The squadron's name, above the top row of bars.
        var box = new GameObject("Name", typeof(RectTransform));
        _labelBox = (RectTransform)box.transform;
        _labelBox.SetParent(rt, false);
        _labelBox.anchorMin = _labelBox.anchorMax = new Vector2(0f, 0f);
        _labelBox.pivot = new Vector2(0f, 0f);
        _labelBox.sizeDelta = new Vector2(600f, 28f);
        var text = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        var trt = (RectTransform)text.transform;
        trt.SetParent(box.transform, false);
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = trt.offsetMax = Vector2.zero;
        // A black copy two units down-right: the name stays readable over a red damage wash or bright sky.
        var shadow = UnityEngine.Object.Instantiate(text, box.transform, false);
        shadow.name = "Shadow";
        var srt = (RectTransform)shadow.transform;
        srt.offsetMin = new Vector2(2f, -2f);
        srt.offsetMax = new Vector2(2f, -2f);
        _shadow = shadow.GetComponent<TextMeshProUGUI>();
        _shadow.fontSize = 20f;
        _shadow.alignment = TextAlignmentOptions.BottomLeft;
        _shadow.enableWordWrapping = false;
        _shadow.overflowMode = TextOverflowModes.Overflow;
        _shadow.color = new Color(0f, 0f, 0f, 0.8f);
        text.transform.SetAsLastSibling();
        _label = text.GetComponent<TextMeshProUGUI>();
        _label.fontSize = 20f;
        _label.alignment = TextAlignmentOptions.BottomLeft;
        _label.enableWordWrapping = false;
        _label.overflowMode = TextOverflowModes.Overflow;
        try
        {
            if (_font?.GetValue(null) is TMP_FontAsset font) _label.font = font;
            if (_fontMaterial?.GetValue(null) is Material mat) _label.fontSharedMaterial = mat;
        }
        catch { /* default TMP font */ }
    }

    /// <summary>A health bar along the bottom of a slot: dark backing, dim track, Danger-coloured fill.</summary>
    private static (RectTransform, Image, Image) CreateBar(Transform slot)
    {
        var back = new GameObject("Backing", typeof(RectTransform), typeof(Image));
        var brt = (RectTransform)back.transform;
        brt.SetParent(slot, false);
        brt.anchorMin = new Vector2(0f, 0f);
        brt.anchorMax = new Vector2(1f, 0f);
        brt.pivot = new Vector2(0.5f, 0f);
        brt.sizeDelta = new Vector2(4f, BarHeight + 4f);
        brt.anchoredPosition = Vector2.zero;
        back.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);

        var track = new GameObject("Track", typeof(RectTransform), typeof(Image));
        var trt = (RectTransform)track.transform;
        trt.SetParent(slot, false);
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0f);
        trt.pivot = new Vector2(0.5f, 0f);
        trt.sizeDelta = new Vector2(0f, BarHeight);
        trt.anchoredPosition = new Vector2(0f, 2f);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        var frt = (RectTransform)fill.transform;
        frt.SetParent(trt, false);
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = frt.offsetMax = Vector2.zero;
        return (frt, fill.GetComponent<Image>(), track.GetComponent<Image>());
    }

    /// <summary>One equal slot per aircraft: row 0 at the bottom (lead first), the ninth on up in a row above.</summary>
    private static void BuildBars(int count)
    {
        foreach (var (_, _, track) in Bars)
            if (track != null) UnityEngine.Object.Destroy(track.transform.parent.gameObject);
        Bars.Clear();
        _builtCount = count;
        int cols = Columns(count), rows = Rows(count);
        for (int i = 0; i < count; i++)
        {
            int row = i / PerRow, col = i % PerRow;
            var slot = new GameObject($"Ace {i + 1}", typeof(RectTransform));
            var srt = (RectTransform)slot.transform;
            srt.SetParent(_strip.transform, false);
            srt.anchorMin = new Vector2(col / (float)cols, 0f);
            srt.anchorMax = new Vector2((col + 1) / (float)cols, 0f);
            srt.pivot = new Vector2(0.5f, 0f);
            srt.offsetMin = new Vector2(col == 0 ? 0f : BarGap * 0.5f, row * RowStep);
            srt.offsetMax = new Vector2(col == cols - 1 ? 0f : -BarGap * 0.5f, row * RowStep + BarHeight + 4f);
            Bars.Add(CreateBar(slot.transform));
        }
        if (_labelBox != null) _labelBox.anchoredPosition = new Vector2(0f, (rows - 1) * RowStep + BarHeight + 8f);
    }

    // ---- fallback ----------------------------------------------------------------------------------------

    /// <summary>The same bars with IMGUI, for when the UIPlus HUD is not showing.</summary>
    internal static void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        var root = UiPlusRoot();
        if (root != null && root.activeInHierarchy) return;
        var enc = Current();
        if (enc == null) return;
        if (HudLayoutBridge.Editing) return; // the layout editor draws its own preview
        DrawImgui(enc.SquadronName, Entries(enc), HudLayoutBridge.Health);
    }

    /// <summary>A sample four-ship where the UIPlus layout editor shows the bars.</summary>
    internal static void DrawPreview(HudPlace place)
    {
        var sample = new List<Entry>
        {
            new() { Health = 1f }, new() { Health = 0.8f }, new() { Health = 0.55f }, new() { Health = 0.3f, Left = true },
        };
        DrawImgui("Ace Squadron", sample, place);
    }

    private static void DrawImgui(string squadron, List<Entry> entries, HudPlace place)
    {
        float scale = Screen.height / 1080f;
        Vector2 pivot = HudLayoutBridge.Pivot(place);
        float s = Mathf.Max(0.05f, place.Scale);
        float width = Screen.width * place.Width / s, left = pivot.x - width * 0.5f; // unscaled; the matrix scales it
        float baseY = pivot.y;
        var keepMatrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(-place.Rotation, pivot);
        GUIUtility.ScaleAroundPivot(new Vector2(s, s), pivot);
        float gap = BarGap * scale, barH = BarHeight * scale, rowStep = RowStep * scale;
        int cols = Columns(entries.Count), rows = Rows(entries.Count);
        float each = (width - gap * (cols - 1)) / cols; // all bars equal
        Color danger = Danger(), keep = GUI.color;

        float labelY = baseY - (rows - 1) * rowStep - barH - 30f * scale;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerLeft, fontSize = Mathf.RoundToInt(19f * scale), fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
        style.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(left + 2f * scale, labelY + 2f * scale, width, 24f * scale), squadron.ToUpperInvariant(), style);
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(left, labelY, width, 24f * scale), squadron.ToUpperInvariant(), style);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int row = i / PerRow, col = i % PerRow;
            float x = left + col * (each + gap);
            float y = baseY - barH - row * rowStep;
            float a = e.Left ? 0.35f : 1f;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(x - 2f * scale, y - 2f * scale, each + 4f * scale, barH + 4f * scale), Texture2D.whiteTexture);
            GUI.color = new Color(danger.r * 0.25f, danger.g * 0.25f, danger.b * 0.25f, 0.55f * a);
            GUI.DrawTexture(new Rect(x, y, each, barH), Texture2D.whiteTexture);
            GUI.color = new Color(danger.r, danger.g, danger.b, a);
            GUI.DrawTexture(new Rect(x, y, each * e.Health, barH), Texture2D.whiteTexture);
        }
        GUI.color = keep;
        GUI.matrix = keepMatrix;
    }
}
