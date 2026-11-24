using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// The intro overlay, drawn with IMGUI. Driven by IntroDirector.Clock and the encounter's IntroPlan so it pauses,
/// skips and ends with the camera work.
/// Styles:
/// - PwBoss / Stamp / Typewriter: letterbox, centred emblem, name (+ unit), title, organisation and lead pilot.
/// - Caution (Project Wingman): faded emblem behind a thin box with a blinking "-- CAUTION --" and the unit line,
///   or "\\ CALLSIGN \\" when the intro is about the lead.
/// - Unit (Ace Combat 7): bottom-left emblem, big name, small unit number and the organisation underneath.
/// </summary>
internal static class TitleCard
{
    private static readonly Color CautionOrange = new(1f, 0.62f, 0.12f);
    private static readonly Color CaptionWhite = new(0.92f, 0.92f, 0.92f);

    private static ActiveEncounter _enc;
    private static bool _cinematic;
    private static IntroPlan _plan;
    private static CardStyle _style;
    private static GUIStyle _text;

    internal static void Show(ActiveEncounter enc, bool cinematic, IntroPlan plan)
    {
        _enc = enc;
        _cinematic = cinematic;
        _plan = plan;
        _style = plan.Card == CardStyle.Match ? CardStyle.PwBoss : plan.Card;
    }

    internal static void Hide() => _enc = null;

    /// <summary>Skipping jumps the clock to the wipe-out; nothing else to do here.</summary>
    internal static void Skip() { }

    internal static void OnGUI()
    {
        if (_enc == null || _plan == null) return;
        if (!IntroDirector.Playing || IntroDirector.Encounter != _enc)
        {
            _enc = null;
            return;
        }
        if (Event.current.type != EventType.Repaint) return;

        if (_text == null)
        {
            _text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, richText = false, clipping = TextClipping.Overflow };
            _text.normal.textColor = Color.white; // GUI.color does the tinting
        }
        GUI.depth = -1000;
        Matrix4x4 baseMatrix = GUI.matrix;
        Color baseColour = GUI.color;

        float t = IntroDirector.Clock;
        float outK = Ease(Mathf.Clamp01((t - _plan.CameraEnd) / IntroPlan.OutLength));
        float fade = 1f - outK;

        switch (_style)
        {
            case CardStyle.Caution:
                DrawCaution(t, fade);
                break;
            case CardStyle.Unit:
                DrawUnit(t, fade);
                break;
            default:
                DrawClassic(t, fade, outK);
                break;
        }

        GUI.matrix = baseMatrix;
        GUI.color = baseColour;
    }

    // ---- PwBoss / Stamp / Typewriter -----------------------------------------------------------------

    private static void DrawClassic(float t, float fade, float outK)
    {
        float W = Screen.width, H = Screen.height;
        Matrix4x4 baseMatrix = GUI.matrix;

        // Letterbox: closes as the intro starts, opens with the wipe-out.
        if (_cinematic)
        {
            float closeK = Ease(Mathf.Clamp01(t / 0.6f));
            float bar = H * 0.115f * closeK * fade;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, W, bar), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, H - bar, W, bar), Texture2D.whiteTexture);
        }

        float emblemAt = _plan.CardAt;
        float gap = Mathf.Min(2.4f, (_plan.CameraEnd - _plan.CardAt) * 0.55f);
        float nameAt = _style == CardStyle.Stamp ? emblemAt : emblemAt + gap;
        float titleAt = _style == CardStyle.Stamp ? emblemAt : nameAt + 0.35f;
        float pilotAt = _style == CardStyle.Stamp ? emblemAt : nameAt + 0.7f;

        float stampShake = 0f;
        if (_style == CardStyle.Stamp && t >= emblemAt) stampShake = Mathf.Clamp01(1f - (t - emblemAt) / 0.25f);

        float scaleAll = _cinematic ? 1f : 0.55f;
        float emblemSize = H * 0.30f * scaleAll;
        Vector2 centre = new(W * 0.5f, H * (_cinematic ? 0.36f : 0.21f));
        Vector2 shake = stampShake > 0f
            ? new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f)) * H * 0.012f * stampShake
            : Vector2.zero;
        float slideUp = -outK * H * 0.03f;

        // White flash on the emblem's arrival (pw-boss only, cinematic only).
        if (_cinematic && _style == CardStyle.PwBoss && t >= emblemAt)
        {
            float flash = 0.5f * (1f - Mathf.Clamp01((t - emblemAt) / 0.35f));
            if (flash > 0f)
            {
                GUI.color = new Color(1f, 1f, 1f, flash);
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            }
        }

        Texture2D tex = _enc.Emblem?.FrameAt(Mathf.Max(0f, t - emblemAt));
        if (tex != null && t >= emblemAt)
        {
            float local = t - emblemAt;
            float alpha, scale = 1f, rot = 0f;
            switch (_style)
            {
                case CardStyle.Stamp:
                    alpha = 1f;
                    break;
                case CardStyle.Typewriter:
                    alpha = Mathf.Clamp01(local / 0.6f);
                    break;
                default:
                    alpha = Mathf.Clamp01(local / 0.15f);
                    scale = Mathf.Lerp(1.45f, 1f, Ease(Mathf.Clamp01(local / 0.3f)));
                    rot = Mathf.Lerp(-10f, 0f, Ease(Mathf.Clamp01(local / 2f)));
                    break;
            }
            Vector2 c = centre + shake + new Vector2(0f, slideUp);
            GUIUtility.RotateAroundPivot(rot, c);
            GUIUtility.ScaleAroundPivot(Vector2.one * scale, c);
            DrawEmblem(tex, c, emblemSize, alpha * fade);
            GUI.matrix = baseMatrix;
        }

        float y = centre.y + emblemSize * 0.5f + H * 0.045f * scaleAll + slideUp;
        string lead = _enc.LeadMember?.Callsign ?? "";
        string pilot = _enc.LeadMember?.PilotName;
        string pilotLine = string.IsNullOrEmpty(pilot) ? lead : $"{lead}   ·   {pilot}";
        string name = _enc.SquadronName.ToUpperInvariant();
        if (!string.IsNullOrEmpty(_enc.Unit)) name += "  ·  " + _enc.Unit.ToUpperInvariant();

        ClassicLine(name, nameAt, t, new Vector2(W * 0.5f, y) + shake,
            Mathf.RoundToInt(H * 0.062f * scaleAll), FontStyle.Bold, _enc.Colour, fade);
        y += H * 0.058f * scaleAll;
        ClassicLine(_enc.Title, titleAt, t, new Vector2(W * 0.5f, y) + shake,
            Mathf.RoundToInt(H * 0.030f * scaleAll), FontStyle.BoldAndItalic, Color.white, fade);
        if (!string.IsNullOrEmpty(_enc.Designation))
        {
            y += H * 0.036f * scaleAll;
            ClassicLine(_enc.Designation.ToUpperInvariant(), titleAt, t, new Vector2(W * 0.5f, y) + shake,
                Mathf.RoundToInt(H * 0.019f * scaleAll), FontStyle.Normal, new Color(0.8f, 0.8f, 0.8f), fade);
        }
        y += H * 0.042f * scaleAll;
        ClassicLine(pilotLine, pilotAt, t, new Vector2(W * 0.5f, y) + shake,
            Mathf.RoundToInt(H * 0.026f * scaleAll), FontStyle.Normal, new Color(0.85f, 0.85f, 0.85f), fade);
    }

    private static void ClassicLine(string text, float at, float t, Vector2 centre, int fontSize, FontStyle fontStyle, Color colour, float fade)
    {
        if (string.IsNullOrEmpty(text) || t < at) return;
        float local = t - at;

        float alpha = 1f, scale = 1f;
        string shown = text;
        switch (_style)
        {
            case CardStyle.Typewriter:
                shown = Typed(text, local, 30f);
                break;
            case CardStyle.Stamp:
                break;
            default:
                alpha = Mathf.Clamp01(local / 0.12f);
                scale = Mathf.Lerp(1.35f, 1f, Ease(Mathf.Clamp01(local / 0.2f)));
                break;
        }
        if (shown.Length == 0) return;

        Matrix4x4 m = GUI.matrix;
        GUIUtility.ScaleAroundPivot(Vector2.one * scale, centre);
        // Typing is left-aligned inside the full line's box so it does not re-centre as it grows.
        Label(shown, text, centre, _style == CardStyle.Typewriter ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, true,
            fontSize, fontStyle, colour, alpha * fade);
        GUI.matrix = m;
    }

    // ---- Caution ---------------------------------------------------------------------------------------

    private static void DrawCaution(float t, float fade)
    {
        float local = t - _plan.CardAt;
        if (local < 0f) return;
        float W = Screen.width, H = Screen.height;
        float s = _cinematic ? 1f : 0.6f;
        Vector2 centre = new(W * 0.5f, H * (_cinematic ? 0.47f : 0.2f));

        // Big faded emblem behind the box.
        Texture2D tex = _enc.Emblem?.FrameAt(local);
        if (tex != null)
        {
            float k = Ease(Mathf.Clamp01(local / 0.6f));
            Matrix4x4 m = GUI.matrix;
            Vector2 ec = centre + new Vector2(0f, -H * 0.03f * s);
            GUIUtility.ScaleAroundPivot(Vector2.one * Mathf.Lerp(1.08f, 1f, k), ec);
            DrawEmblem(tex, ec, H * 0.56f * s, 0.55f * k * fade);
            GUI.matrix = m;
        }

        string header = "-- CAUTION --";
        string sub = _plan.LeadFocus
            ? $"\\\\ {(_enc.LeadMember?.Callsign ?? _enc.SquadronName).ToUpperInvariant()} \\\\"
            : (!string.IsNullOrEmpty(_enc.Designation)
                ? $"{_enc.Designation} - {_enc.SquadronName}"
                : $"{_enc.SquadronName} - {_enc.Title}").ToUpperInvariant();

        int headerSize = Mathf.RoundToInt(H * 0.058f * s), subSize = Mathf.RoundToInt(H * 0.024f * s);
        Vector2 headerDim = Measure(header, headerSize, FontStyle.Bold);
        Vector2 subDim = Measure(sub, subSize, FontStyle.Bold);
        float pad = H * 0.035f * s;
        float boxW = Mathf.Max(headerDim.x, subDim.x) + pad * 2f;
        float boxH = headerDim.y + subDim.y + pad * 0.6f;
        float line = Mathf.Max(1f, H * 0.0018f);

        // Box: top and bottom rules draw outward from the middle, then the sides drop in.
        float open = Ease(Mathf.Clamp01((local - 0.1f) / 0.25f));
        if (open > 0f)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.9f * fade);
            float w = boxW * open;
            float top = centre.y - boxH * 0.5f, bottom = centre.y + boxH * 0.5f;
            GUI.DrawTexture(new Rect(centre.x - w * 0.5f, top, w, line), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centre.x - w * 0.5f, bottom - line, w, line), Texture2D.whiteTexture);
            if (open >= 1f)
            {
                GUI.DrawTexture(new Rect(centre.x - boxW * 0.5f, top, line, boxH), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(centre.x + boxW * 0.5f - line, top, line, boxH), Texture2D.whiteTexture);
            }
        }

        // Header blinks in like a warning, then holds.
        float hLocal = local - 0.25f;
        if (hLocal >= 0f && (hLocal > 0.7f || Mathf.FloorToInt(hLocal * 10f) % 2 == 0))
            Label(header, null, new Vector2(centre.x, centre.y - boxH * 0.5f + pad * 0.3f + headerDim.y * 0.5f), TextAnchor.MiddleCenter, false,
                headerSize, FontStyle.Bold, CautionOrange, fade);

        string typed = Typed(sub, local - 0.45f, 70f);
        if (typed.Length > 0)
            Label(typed, sub, new Vector2(centre.x, centre.y + boxH * 0.5f - pad * 0.3f - subDim.y * 0.5f), TextAnchor.MiddleLeft, true,
                subSize, FontStyle.Bold, Color.white, fade);
    }

    // ---- Unit ------------------------------------------------------------------------------------------

    private static void DrawUnit(float t, float fade)
    {
        float local = t - _plan.CardAt;
        if (local < 0f) return;
        float W = Screen.width, H = Screen.height;
        float s = _cinematic ? 1f : 0.8f;
        float a = Ease(Mathf.Clamp01(local / 0.5f)) * fade;
        float slide = -(1f - Ease(Mathf.Clamp01(local / 0.6f))) * W * 0.015f;

        float x0 = W * 0.045f + slide;
        float yName = H * 0.645f;
        float yDesig = yName + H * 0.05f * s;

        // Emblem spans the name and organisation lines on the left.
        Texture2D tex = _enc.Emblem?.FrameAt(local);
        float emblemH = H * 0.118f * s, emblemW = 0f;
        if (tex != null)
        {
            float aspect = tex.height > 0 ? Mathf.Clamp((float)tex.width / tex.height, 0.5f, 1.3f) : 1f;
            emblemW = emblemH * aspect;
            DrawEmblem(tex, new Vector2(x0 + emblemW * 0.5f, (yName + yDesig) * 0.5f), emblemH, a, emblemW);
        }

        float xText = x0 + emblemW + H * 0.016f;
        string name = _enc.SquadronName.ToUpperInvariant();
        int nameSize = Mathf.RoundToInt(H * 0.068f * s);
        Vector2 nameDim = Label(name, null, new Vector2(xText, yName), TextAnchor.MiddleLeft, false, nameSize, FontStyle.Normal, CaptionWhite, a);

        if (!string.IsNullOrEmpty(_enc.Unit))
        {
            string unit = _enc.Unit.ToUpperInvariant();
            int unitSize = Mathf.RoundToInt(H * 0.032f * s);
            Vector2 unitDim = Measure(unit, unitSize, FontStyle.Bold);
            float baselineNudge = (nameDim.y - unitDim.y) * 0.3f;
            Label(unit, null, new Vector2(xText + nameDim.x + H * 0.012f, yName + baselineNudge), TextAnchor.MiddleLeft, false,
                unitSize, FontStyle.Bold, CaptionWhite, a);
        }

        string org = !string.IsNullOrEmpty(_enc.Designation) ? _enc.Designation : _enc.Title;
        float a2 = Ease(Mathf.Clamp01((local - 0.25f) / 0.5f)) * fade;
        if (!string.IsNullOrEmpty(org))
            Label(org.ToUpperInvariant(), null, new Vector2(xText + H * 0.003f, yDesig), TextAnchor.MiddleLeft, false,
                Mathf.RoundToInt(H * 0.019f * s), FontStyle.Bold, new Color(0.85f, 0.85f, 0.85f), a2);
    }

    // ---- drawing helpers -------------------------------------------------------------------------------

    private static void DrawEmblem(Texture2D tex, Vector2 centre, float size, float alpha, float width = 0f)
    {
        if (alpha <= 0f) return;
        float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
        float w = width > 0f ? width : size * Mathf.Min(aspect, 1.6f);
        float h = w / Mathf.Max(aspect, 0.01f);
        if (h > size) { h = size; w = h * aspect; }
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(new Rect(centre.x - w * 0.5f, centre.y - h * 0.5f, w, h), tex, ScaleMode.ScaleToFit, alphaBlend: true);
    }

    private static Vector2 Measure(string text, int fontSize, FontStyle fontStyle)
    {
        _text.fontSize = Mathf.Max(8, fontSize);
        _text.fontStyle = fontStyle;
        return _text.CalcSize(new GUIContent(text));
    }

    /// <summary>
    /// Draws a line with a soft shadow. The anchor is the line's centre (MiddleCenter) or left middle (MiddleLeft).
    /// With centreOnFull, the box is sized and centred for <paramref name="full"/> so a typing line stays put.
    /// </summary>
    private static Vector2 Label(string shown, string full, Vector2 anchor, TextAnchor align, bool centreOnFull,
        int fontSize, FontStyle fontStyle, Color colour, float alpha)
    {
        Vector2 size = Measure(full ?? shown, fontSize, fontStyle);
        float x = align == TextAnchor.MiddleLeft && !centreOnFull ? anchor.x : anchor.x - size.x * 0.5f;
        var rect = new Rect(x, anchor.y - size.y * 0.5f, size.x, size.y);
        _text.alignment = centreOnFull ? TextAnchor.MiddleLeft : (align == TextAnchor.MiddleLeft ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter);
        if (alpha <= 0f) return size;
        float shadow = Mathf.Max(1f, fontSize * 0.06f);
        GUI.color = new Color(0f, 0f, 0f, 0.7f * alpha);
        GUI.Label(new Rect(rect.x + shadow, rect.y + shadow, rect.width, rect.height), shown, _text);
        GUI.color = new Color(colour.r, colour.g, colour.b, alpha);
        GUI.Label(rect, shown, _text);
        return size;
    }

    private static string Typed(string text, float local, float charsPerSecond)
    {
        if (local <= 0f) return "";
        int chars = Mathf.Clamp(Mathf.FloorToInt(local * charsPerSecond), 0, text.Length);
        return text.Substring(0, chars);
    }

    private static float Ease(float x) => 1f - (1f - x) * (1f - x) * (1f - x);
}
