using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOAcesHigh;

/// <summary>
/// A small threat gauge on the HUD for your side: how close the enemy is to sending aces, then who is coming.
/// Built inside UIPlus's own HUD canvas ("UI Plus HUD") so it shows and hides with that HUD and takes its colour and
/// font; when the UIPlus HUD is not up it falls back to a plain IMGUI bar at the same place.
///
/// States: building (fill = threat / ThreatToUnlock, HUD colour shading to amber), high (unlocked, rolls running:
/// pulsing amber), inbound / engaged (a squadron hunting your side: red, survivors / members), quiet (no more
/// encounters this mission). On a multiplayer client the threat numbers live on the host, so only the inbound and
/// engaged states show there.
/// </summary>
internal static class ThreatBar
{
    private const float Width = 280f, Height = 38f;
    private static readonly Color Amber = new(1f, 0.72f, 0.15f);
    private static readonly Color Red = new(1f, 0.29f, 0.24f);

    private static FieldInfo _root, _green, _font, _fontMaterial;
    private static bool _lookedUp;

    private static GameObject _bar;
    private static RectTransform _fill;
    private static Image _fillImage;
    private static TextMeshProUGUI _label;
    private static GameObject _builtFor;

    private struct Reading
    {
        public bool Show;
        public float Fill;
        public string Text;
        public Color Colour;
        public bool Pulse;
    }

    private static Reading Read()
    {
        var r = new Reading();
        if (!Plugin.ShowThreatBar.Value || !Plugin.Enabled.Value) return r;
        var hq = Session.LocalHQ;
        if (hq == null || Session.LocalAircraft == null) return r;

        // An encounter hunting this side (the host's real one, or a client's copy).
        ActiveEncounter enc = Encounters.ActiveAgainst(hq) ?? NetSync.Mirror;
        if (enc is { Ended: false } && enc.Members.Count > 0)
        {
            int alive = enc.AliveCount;
            r.Show = true;
            r.Fill = alive / (float)enc.Members.Count;
            bool engaged = !enc.FormationHeld;
            r.Text = $"{(engaged ? "ACES ENGAGED" : "ACES INBOUND")}  ·  {enc.SquadronName.ToUpperInvariant()}  {alive}/{enc.Members.Count}";
            r.Colour = Red;
            r.Pulse = !engaged;
            return r;
        }
        if (!Session.Authoritative) return r; // clients do not know the threat numbers

        float unlock = Mathf.Max(1f, Plugin.UnlockThreat.Value);
        float threat = Encounters.ThreatOf(hq);
        r.Show = true;
        if (Encounters.EncountersLeft <= 0)
        {
            r.Fill = 0f;
            r.Text = "ACE THREAT  ·  QUIET";
            r.Colour = HudColour() * new Color(1f, 1f, 1f, 0.6f);
            return r;
        }
        r.Fill = Mathf.Clamp01(threat / unlock);
        if (threat >= unlock)
        {
            r.Text = Encounters.CoolingDown ? "ACE THREAT  ·  HIGH" : "ACE THREAT  ·  HIGH  ·  ACES MAY LAUNCH";
            r.Colour = Amber;
            r.Pulse = true;
        }
        else
        {
            r.Text = $"ACE THREAT  {Mathf.RoundToInt(r.Fill * 100f)}%";
            r.Colour = Color.Lerp(HudColour(), Amber, Mathf.InverseLerp(0.5f, 1f, r.Fill));
        }
        return r;
    }

    // ---- UIPlus-hosted version --------------------------------------------------------------------------

    internal static void Tick()
    {
        try
        {
            var root = UiPlusRoot();
            var reading = Read();
            if (root == null || !root.activeInHierarchy || HudLayoutBridge.Editing)
            {
                if (_bar != null) _bar.SetActive(false);
                return;
            }
            if (_bar == null || _builtFor != root) Build(root);
            if (_bar == null) return;
            _bar.SetActive(reading.Show);
            if (!reading.Show) return;

            var rt = (RectTransform)_bar.transform;
            var place = HudLayoutBridge.Threat;
            rt.anchorMin = rt.anchorMax = new Vector2(place.X, place.Y);
            rt.localScale = new Vector3(place.Scale, place.Scale, 1f);
            rt.localRotation = Quaternion.Euler(0f, 0f, place.Rotation);
            float pulse = reading.Pulse ? 0.65f + 0.35f * Mathf.Sin(Clock.Now * 6f) : 1f;
            Color c = reading.Colour;
            _fill.anchorMax = new Vector2(Mathf.Max(0.001f, reading.Fill), 1f);
            _fillImage.color = new Color(c.r, c.g, c.b, 0.85f * pulse);
            _label.text = reading.Text;
            _label.color = new Color(c.r, c.g, c.b, pulse);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Threat bar failed, hiding it: " + ex.Message);
            if (_bar != null) UnityEngine.Object.Destroy(_bar);
            _bar = null;
        }
    }

    private static void LookUp()
    {
        if (_lookedUp) return;
        _lookedUp = true;
        var hud = AccessTools.TypeByName("UIPlus.Helpers.ArcadeHud");
        if (hud == null) return;
        _root = AccessTools.Field(hud, "_root");
        _green = AccessTools.Field(hud, "Green");
        _font = AccessTools.Field(hud, "_font");
        _fontMaterial = AccessTools.Field(hud, "_fontMaterial");
    }

    private static GameObject UiPlusRoot()
    {
        LookUp();
        try { return _root?.GetValue(null) as GameObject; }
        catch { return null; }
    }

    private static Color HudColour()
    {
        try { if (_green?.GetValue(null) is Color c) return c; }
        catch { /* default */ }
        return new Color(0.4f, 1f, 0.5f);
    }

    private static void Build(GameObject root)
    {
        if (_bar != null) UnityEngine.Object.Destroy(_bar);
        _builtFor = root;
        _bar = new GameObject("NOAcesHigh Threat", typeof(RectTransform));
        var rt = (RectTransform)_bar.transform;
        rt.SetParent(root.transform, false);
        rt.sizeDelta = new Vector2(Width, Height);
        rt.pivot = new Vector2(0.5f, 0.5f);

        // Track: a thin dark strip along the bottom with a thin outline.
        var track = new GameObject("Track", typeof(RectTransform), typeof(Image));
        var trt = (RectTransform)track.transform;
        trt.SetParent(rt, false);
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0.3f);
        trt.offsetMin = trt.offsetMax = Vector2.zero;
        track.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        _fill = (RectTransform)fill.transform;
        _fill.SetParent(trt, false);
        _fill.anchorMin = Vector2.zero;
        _fill.anchorMax = new Vector2(0f, 1f);
        _fill.offsetMin = new Vector2(1f, 1f);
        _fill.offsetMax = new Vector2(-1f, -1f);
        _fillImage = fill.GetComponent<Image>();

        var text = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        var lrt = (RectTransform)text.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = new Vector2(0f, 0.36f);
        lrt.anchorMax = new Vector2(1f, 1f);
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        _label = text.GetComponent<TextMeshProUGUI>();
        _label.fontSize = 15f;
        _label.alignment = TextAlignmentOptions.Center;
        _label.enableWordWrapping = false;
        _label.overflowMode = TextOverflowModes.Overflow;
        try
        {
            if (_font?.GetValue(null) is TMP_FontAsset font) _label.font = font;
            if (_fontMaterial?.GetValue(null) is Material mat) _label.fontSharedMaterial = mat;
        }
        catch { /* default TMP font */ }
    }

    // ---- fallback --------------------------------------------------------------------------------------

    /// <summary>The same gauge with IMGUI, for when the UIPlus HUD is not showing (another HUD style, or no UIPlus HUD yet).</summary>
    internal static void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        var root = UiPlusRoot();
        if (root != null && root.activeInHierarchy) return;
        if (IntroDirector.Playing && IntroDirector.CameraCut) return;
        var r = Read();
        if (!r.Show) return;

        if (HudLayoutBridge.Editing) return; // the layout editor draws its own preview
        DrawImgui(r, HudLayoutBridge.Threat);
    }

    /// <summary>A sample gauge where the UIPlus layout editor shows it.</summary>
    internal static void DrawPreview(HudPlace place)
    {
        DrawImgui(new Reading { Show = true, Fill = 0.62f, Text = "ACE THREAT  62%", Colour = HudColour() }, place);
    }

    private static void DrawImgui(Reading r, HudPlace place)
    {
        float scale = Screen.height / 1080f;
        float w = Width * scale, h = Height * scale;
        Vector2 pivot = HudLayoutBridge.Pivot(place);
        float x = pivot.x - w * 0.5f;
        float y = pivot.y - h * 0.5f;
        var keepMatrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(-place.Rotation, pivot);
        GUIUtility.ScaleAroundPivot(new Vector2(place.Scale, place.Scale), pivot);
        float pulse = r.Pulse ? 0.65f + 0.35f * Mathf.Sin(Clock.Now * 6f) : 1f;
        Color baseColour = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        var track = new Rect(x, y + h * 0.7f, w, h * 0.3f);
        GUI.DrawTexture(track, Texture2D.whiteTexture);
        GUI.color = new Color(r.Colour.r, r.Colour.g, r.Colour.b, 0.85f * pulse);
        GUI.DrawTexture(new Rect(track.x + 1f, track.y + 1f, Mathf.Max(0f, (track.width - 2f) * r.Fill), track.height - 2f), Texture2D.whiteTexture);
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(14f * scale), fontStyle = FontStyle.Bold };
        style.normal.textColor = Color.white;
        GUI.color = new Color(r.Colour.r, r.Colour.g, r.Colour.b, pulse);
        GUI.Label(new Rect(x, y, w, h * 0.66f), r.Text, style);
        GUI.color = baseColour;
        GUI.matrix = keepMatrix;
    }
}
