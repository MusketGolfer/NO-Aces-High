using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>Where one of our HUD items sits: centre (or bottom centre) as a screen fraction, size, turn, and width.</summary>
internal struct HudPlace
{
    public float X, Y;      // 0..1 across and up the screen
    public float Scale;     // 1 = as designed (for 1080p)
    public float Rotation;  // degrees, anticlockwise
    public float Width;     // health bars only: fraction of the screen width
}

/// <summary>
/// Puts the ace threat gauge and the squadron health bars into UIPlus's "Customize HUD Layout" screen. UIPlus has no
/// API for other mods' elements (its editor works on fixed internal tables), so this joins it from outside: while the
/// editor is open our two items are drawn as extra boxes with sample contents, take mouse input before the editor does
/// when the pointer is on them, and use the same handles (drag to move, corner square to size, the turn handle above to
/// rotate) plus a small settings window. Apply saves them to this mod's config with the rest of the layout, Cancel
/// throws the changes away, Reset puts them back to the defaults. Nothing is written into UIPlus's own settings.
/// </summary>
internal static class HudLayoutBridge
{
    private enum Item { None, Threat, Health }
    private enum Grab { None, Move, Size, Turn }

    private static PropertyInfo _isOpen;
    private static FieldInfo _focus, _pressed, _window;
    private static MethodInfo _buttonAt;
    private static bool _failed;

    internal static bool Editing { get; private set; }
    private static HudPlace _threat, _health;
    private static Item _selected;
    private static Grab _grab;
    private static Vector2 _grabStart, _grabPivot;
    private static HudPlace _grabFrom;
    private static float _grabRef;
    private static Rect _panel;
    private static bool _panelPlaced;
    private const int PanelId = 0x4E4F4148;

    private static readonly Color Idle = new(0.4f, 1f, 0.5f, 0.9f);
    private static readonly Color Gold = new(1f, 0.85f, 0.3f, 1f);

    internal static HudPlace Threat => Editing ? _threat : Saved(Item.Threat);
    internal static HudPlace Health => Editing ? _health : Saved(Item.Health);

    internal static void Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("UIPlus.Helpers.HudLayoutEditor");
        if (t == null)
        {
            Plugin.Log.LogInfo("UIPlus's HUD layout editor was not found; the threat gauge and health bars are placed from this mod's config only.");
            return;
        }
        _isOpen = AccessTools.Property(t, "IsOpen");
        _focus = AccessTools.Field(t, "_focus");
        _pressed = AccessTools.Field(t, "_pressedButton");
        _window = AccessTools.Field(t, "_window");
        _buttonAt = AccessTools.Method(t, "ButtonAt");
        if (_isOpen == null)
        {
            Plugin.Log.LogWarning("UIPlus's HUD layout editor has changed; the threat gauge and health bars are not in it. Place them from this mod's config.");
            return;
        }
        Patches.Patch(harmony, "UIPlus layout editor: our items take the mouse", AccessTools.Method(t, "Update"), prefix: nameof(UpdatePrefix), owner: typeof(HudLayoutBridge));
        Patches.Patch(harmony, "UIPlus layout editor: draw our items", AccessTools.Method(t, "Draw"), postfix: nameof(DrawPostfix), owner: typeof(HudLayoutBridge));
        Patches.Patch(harmony, "UIPlus layout editor: Apply saves ours", AccessTools.Method(t, "Apply"), postfix: nameof(ApplyPostfix), owner: typeof(HudLayoutBridge));
        Patches.Patch(harmony, "UIPlus layout editor: closing", AccessTools.Method(t, "Close"), postfix: nameof(ClosePostfix), owner: typeof(HudLayoutBridge));
    }

    // ---- places ------------------------------------------------------------------------------------------

    private static HudPlace Saved(Item it) => it == Item.Threat
        ? new HudPlace { X = Plugin.ThreatBarX.Value, Y = Plugin.ThreatBarY.Value, Scale = Plugin.ThreatBarScale.Value, Rotation = Plugin.ThreatBarRotation.Value, Width = 0f }
        : new HudPlace { X = Plugin.HealthBarsX.Value, Y = Plugin.HealthBarsY.Value, Scale = Plugin.HealthBarsScale.Value, Rotation = Plugin.HealthBarsRotation.Value, Width = Plugin.HealthBarsWidth.Value };

    private static float Def(ConfigEntry<float> e) => (float)((ConfigEntryBase)e).DefaultValue;

    private static HudPlace Default(Item it) => it == Item.Threat
        ? new HudPlace { X = Def(Plugin.ThreatBarX), Y = Def(Plugin.ThreatBarY), Scale = Def(Plugin.ThreatBarScale), Rotation = Def(Plugin.ThreatBarRotation), Width = 0f }
        : new HudPlace { X = Def(Plugin.HealthBarsX), Y = Def(Plugin.HealthBarsY), Scale = Def(Plugin.HealthBarsScale), Rotation = Def(Plugin.HealthBarsRotation), Width = Def(Plugin.HealthBarsWidth) };

    private static HudPlace Place(Item it) => it == Item.Threat ? _threat : _health;

    private static void SetPlace(Item it, HudPlace p)
    {
        if (it == Item.Threat) _threat = p;
        else if (it == Item.Health) _health = p;
    }

    private static bool Shown(Item it) => it == Item.Threat ? Plugin.ShowThreatBar.Value : Plugin.HealthBars.Value;

    private static string Name(Item it) => it == Item.Threat ? "ACE THREAT GAUGE" : "SQUADRON HEALTH BARS";

    // ---- geometry (IMGUI screen pixels, y down) -------------------------------------------------------------

    /// <summary>The item's pivot on screen: the gauge's centre, the health strip's bottom centre.</summary>
    internal static Vector2 Pivot(HudPlace p) => new(Screen.width * p.X, Screen.height * (1f - p.Y));

    private const float HealthHeight = 46f; // one row of bars and the name above it, 1080p units

    /// <summary>The item's rectangle around its pivot, before rotation.</summary>
    private static Rect Local(Item it)
    {
        var p = Place(it);
        float s = Screen.height / 1080f * p.Scale;
        if (it == Item.Threat) return new Rect(-140f * s, -19f * s, 280f * s, 38f * s);
        float w = p.Width * Screen.width;
        return new Rect(-w * 0.5f, -HealthHeight * s, w, HealthHeight * s);
    }

    /// <summary>Turns a vector by degrees in y-down screen space (positive = clockwise on screen).</summary>
    private static Vector2 Turn(Vector2 v, float deg)
    {
        float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    private static Vector2 ToScreen(Item it, Vector2 local) => Pivot(Place(it)) + Turn(local, -Place(it).Rotation);
    private static Vector2 ToLocal(Item it, Vector2 screen) => Turn(screen - Pivot(Place(it)), Place(it).Rotation);

    private static bool Hit(Item it, Vector2 m)
    {
        var r = Local(it);
        return new Rect(r.x - 6f, r.y - 6f, r.width + 12f, r.height + 12f).Contains(ToLocal(it, m));
    }

    // The gauge sizes from its bottom-right corner (like UIPlus's items); the health strip from its top-right corner,
    // since it grows up from the bottom: drag right for wider, up for thicker.
    private static Vector2 SizeHandle(Item it)
    {
        var r = Local(it);
        return ToScreen(it, it == Item.Threat ? new Vector2(r.xMax, r.yMax) : new Vector2(r.xMax, r.yMin));
    }

    private static Vector2 TurnHandle(Item it)
    {
        var r = Local(it);
        return ToScreen(it, new Vector2(r.xMax, r.yMin - 25f));
    }

    private static bool Near(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= 12f * 12f;

    private static Vector2 Mouse()
    {
        Vector2 m = Input.mousePosition;
        m.y = Screen.height - m.y;
        return m;
    }

    // ---- editor hooks -----------------------------------------------------------------------------------------

    private static bool EditorOpen()
    {
        try { return _isOpen != null && (bool)_isOpen.GetValue(null); }
        catch { return false; }
    }

    private static void SyncOpen()
    {
        bool open = EditorOpen();
        if (open && !Editing)
        {
            Editing = true;
            _threat = Saved(Item.Threat);
            _health = Saved(Item.Health);
            _selected = Item.None;
            _grab = Grab.None;
        }
        else if (!open && Editing) Editing = false;
    }

    private static int ButtonAt(Vector2 m)
    {
        try { return _buttonAt != null ? (int)_buttonAt.Invoke(null, new object[] { m }) : -1; }
        catch { return -1; }
    }

    private static bool UpdatePrefix()
    {
        if (_failed) return true;
        try
        {
            SyncOpen();
            if (!Editing || !Application.isFocused) return true;
            var m = Mouse();

            // UIPlus's Reset button puts ours back to the defaults too.
            if (Input.GetMouseButtonUp(0) && _pressed != null && (int)_pressed.GetValue(null) == 2 && ButtonAt(m) == 2)
            {
                _threat = Default(Item.Threat);
                _health = Default(Item.Health);
            }

            if (_grab != Grab.None)
            {
                if (!Input.GetMouseButton(0)) _grab = Grab.None;
                else Drag(m);
                return false;
            }
            if (_selected != Item.None && _panel.Contains(m)) return false; // our settings window has the mouse
            if (!Input.GetMouseButtonDown(0)) return true;
            if (_window != null && ((Rect)_window.GetValue(null)).Contains(m)) return true;
            if (ButtonAt(m) >= 0) return true;

            if (_selected != Item.None)
            {
                if (Near(m, SizeHandle(_selected))) { StartGrab(Grab.Size, m); return false; }
                if (Near(m, TurnHandle(_selected))) { StartGrab(Grab.Turn, m); return false; }
            }
            foreach (var it in new[] { Item.Threat, Item.Health })
            {
                if (!Shown(it) || !Hit(it, m)) continue;
                _selected = it;
                try { _focus?.SetValue(null, -1); } catch { /* their selection stays */ }
                StartGrab(Grab.Move, m);
                return false;
            }
            _selected = Item.None; // a click elsewhere belongs to UIPlus
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.Log.LogWarning("Threat gauge / health bars in the UIPlus layout editor failed; leaving the editor alone: " + ex.Message);
            return true;
        }
    }

    private static void StartGrab(Grab g, Vector2 m)
    {
        _grab = g;
        _grabStart = m;
        _grabFrom = Place(_selected);
        _grabPivot = Pivot(_grabFrom);
        var d = m - _grabPivot;
        _grabRef = g == Grab.Size ? Mathf.Max(4f, d.magnitude) : Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
    }

    private static void Drag(Vector2 m)
    {
        var p = _grabFrom;
        var d = m - _grabPivot;
        switch (_grab)
        {
            case Grab.Move:
            {
                var delta = m - _grabStart;
                p.X = Mathf.Clamp01(_grabFrom.X + delta.x / Screen.width);
                p.Y = Mathf.Clamp01(_grabFrom.Y - delta.y / Screen.height);
                var def = Default(_selected);
                if (Mathf.Abs(p.X - def.X) < 0.01f && Mathf.Abs(p.Y - def.Y) < 0.015f) { p.X = def.X; p.Y = def.Y; }
                break;
            }
            case Grab.Size when _selected == Item.Health:
            {
                var dl = Turn(m - _grabStart, _grabFrom.Rotation); // the drag in the strip's own axes
                p.Width = Mathf.Clamp(_grabFrom.Width + 2f * dl.x / Screen.width, 0.1f, 1f);
                float h0 = HealthHeight * Screen.height / 1080f * _grabFrom.Scale;
                p.Scale = Mathf.Clamp(_grabFrom.Scale * (h0 - dl.y) / h0, 0.3f, 3f);
                break;
            }
            case Grab.Size:
                p.Scale = Mathf.Clamp(_grabFrom.Scale * d.magnitude / _grabRef, 0.3f, 3f);
                break;
            case Grab.Turn:
            {
                float turned = Mathf.DeltaAngle(_grabRef, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                float rot = Mathf.DeltaAngle(0f, _grabFrom.Rotation - turned);
                p.Rotation = Mathf.Abs(rot) < 3f ? 0f : Mathf.Round(rot);
                break;
            }
        }
        SetPlace(_selected, p);
    }

    private static void ApplyPostfix()
    {
        if (!Editing) return;
        try
        {
            Plugin.ThreatBarX.Value = _threat.X;
            Plugin.ThreatBarY.Value = _threat.Y;
            Plugin.ThreatBarScale.Value = _threat.Scale;
            Plugin.ThreatBarRotation.Value = _threat.Rotation;
            Plugin.HealthBarsX.Value = _health.X;
            Plugin.HealthBarsY.Value = _health.Y;
            Plugin.HealthBarsScale.Value = _health.Scale;
            Plugin.HealthBarsRotation.Value = _health.Rotation;
            Plugin.HealthBarsWidth.Value = _health.Width;
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Could not save the threat gauge / health bar layout: " + ex.Message); }
    }

    private static void ClosePostfix()
    {
        if (EditorOpen()) return;
        Editing = false;
        _selected = Item.None;
        _grab = Grab.None;
    }

    // ---- record-mode test hooks ------------------------------------------------------------------------------

    /// <summary>Record mode: opens UIPlus's editor (as from the pause menu).</summary>
    internal static string TestOpen()
    {
        var t = AccessTools.TypeByName("UIPlus.Helpers.HudLayoutEditor");
        if (t == null) return "no UIPlus editor";
        CursorManager.SetFlag(CursorFlags.GameMenu, true);
        AccessTools.Method(t, "RequestOpen")?.Invoke(null, null);
        return "requested";
    }

    /// <summary>Record mode: selects one of ours and moves it, as a drag would.</summary>
    internal static void TestEdit(bool threat, float x, float y, float scale, float rotation, float width)
    {
        SyncOpen();
        _selected = threat ? Item.Threat : Item.Health;
        SetPlace(_selected, new HudPlace { X = x, Y = y, Scale = scale, Rotation = rotation, Width = width });
    }

    /// <summary>Record mode: the hit test and handles for a point, to check them against the drawing.</summary>
    internal static string TestProbe()
    {
        if (_selected == Item.None) return "nothing selected";
        var size = SizeHandle(_selected); var turn = TurnHandle(_selected);
        var centre = ToScreen(_selected, Local(_selected).center);
        return $"{_selected}: centre {centre} hit {Hit(_selected, centre)}, size handle {size}, turn handle {turn}, far point hit {Hit(_selected, centre + new Vector2(0f, 300f))}";
    }

    /// <summary>Record mode: Apply and close.</summary>
    internal static void TestApplyAndClose()
    {
        ApplyPostfix();
        var t = AccessTools.TypeByName("UIPlus.Helpers.HudLayoutEditor");
        AccessTools.Method(t, "Close")?.Invoke(null, new object[] { false });
        CursorManager.SetFlag(CursorFlags.GameMenu, false);
    }

    // ---- drawing --------------------------------------------------------------------------------------------

    private static void DrawPostfix()
    {
        if (_failed) return;
        try
        {
            SyncOpen();
            if (!Editing) return;
            var keepM = GUI.matrix;
            var keepC = GUI.color;
            GUI.matrix = Matrix4x4.identity;
            try
            {
                if (Event.current.type == EventType.Repaint)
                {
                    if (Shown(Item.Threat)) { ThreatBar.DrawPreview(_threat); Outline(Item.Threat); }
                    if (Shown(Item.Health)) { SquadronHealth.DrawPreview(_health); Outline(Item.Health); }
                    if (_selected != Item.None)
                    {
                        var size = SizeHandle(_selected);
                        Fill(new Rect(size.x - 9f, size.y - 9f, 18f, 18f), Gold);
                        var turn = TurnHandle(_selected);
                        GUI.Box(new Rect(turn.x - 9f, turn.y - 9f, 18f, 18f), "↻");
                    }
                }
                if (_selected != Item.None)
                {
                    if (!_panelPlaced) { _panel = new Rect(Screen.width - 336f, 80f, 320f, 10f); _panelPlaced = true; }
                    _panel = GUILayout.Window(PanelId, _panel, DrawPanel, Name(_selected), GUILayout.Width(320f));
                    _panel.x = Mathf.Clamp(_panel.x, 0f, Mathf.Max(0f, Screen.width - _panel.width));
                    _panel.y = Mathf.Clamp(_panel.y, 0f, Mathf.Max(0f, Screen.height - 40f));
                }
            }
            finally
            {
                GUI.matrix = keepM;
                GUI.color = keepC;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.Log.LogWarning("Drawing the threat gauge / health bars in the UIPlus layout editor failed: " + ex.Message);
        }
    }

    private static void Outline(Item it)
    {
        var p = Place(it);
        var pivot = Pivot(p);
        var r = Local(it);
        r.position += pivot;
        var keep = GUI.matrix;
        GUIUtility.RotateAroundPivot(-p.Rotation, pivot);
        var c = _selected == it ? Gold : Idle;
        Fill(new Rect(r.x, r.y, r.width, 2f), c);
        Fill(new Rect(r.x, r.yMax - 2f, r.width, 2f), c);
        Fill(new Rect(r.x, r.y, 2f, r.height), c);
        Fill(new Rect(r.xMax - 2f, r.y, 2f, r.height), c);
        GUI.Label(new Rect(r.x + 4f, r.y - 20f, 240f, 20f), Name(it));
        GUI.matrix = keep;
    }

    private static void Fill(Rect r, Color c)
    {
        var keep = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = keep;
    }

    private static void DrawPanel(int id)
    {
        var it = _selected;
        if (it == Item.None) return;
        var p = Place(it);
        GUILayout.Label($"Size  {p.Scale:0.00}x");
        p.Scale = GUILayout.HorizontalSlider(p.Scale, 0.3f, 3f);
        GUILayout.Label($"Rotation  {p.Rotation:0}°");
        p.Rotation = Mathf.Round(GUILayout.HorizontalSlider(p.Rotation, -180f, 180f));
        if (it == Item.Health)
        {
            GUILayout.Label($"Width  {p.Width * 100f:0}% of the screen");
            p.Width = GUILayout.HorizontalSlider(p.Width, 0.1f, 1f);
        }
        var def = Default(it);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset size")) { p.Scale = def.Scale; p.Width = def.Width; }
        if (GUILayout.Button("Reset rotation")) p.Rotation = def.Rotation;
        if (GUILayout.Button("Reset position")) { p.X = def.X; p.Y = def.Y; }
        GUILayout.EndHorizontal();
        GUILayout.Label("From NO Aces High. Drag the box to move it, the gold corner to size it, ↻ to turn it. Saved with Apply.");
        SetPlace(it, p);
        GUI.DragWindow();
    }
}
