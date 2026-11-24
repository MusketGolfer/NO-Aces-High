using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// F1 menu list of the installed handmade squadrons: emblem, name, organisation, airframe and style, whether random
/// rolls may pick it, and a button to launch it now.
/// </summary>
internal static class SquadronMenu
{
    /// <summary>Result of the last spawn or end request, shown under the list and the debug tools.</summary>
    internal static string Message;

    private static readonly Dictionary<SquadronDef, Texture2D> Thumbs = new();
    private static GUIStyle _name, _small;

    internal static void Draw(ConfigEntryBase _)
    {
        _name ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = GUI.skin.label.fontSize + 2, wordWrap = false };
        _small ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 1), wordWrap = true, richText = true };

        GUILayout.BeginVertical();
        var list = Squadrons.Handmade;
        GUILayout.Label(list.Count == 0
            ? "No handmade squadrons installed. Put a folder with a squadron.json in BepInEx/config/NOAcesHigh/Squadrons."
            : $"{list.Count} handmade squadron(s). Ticked ones can turn up in random encounters (HandmadeSquadronChance).", _small);
        GUILayout.Label("How to make one: README.md in the squadron folder (Open folder); copy _Template to start.", _small);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Spawn random")) Message = DebugQueue.Queue("Spawning a random squadron", () => Message = Encounters.DebugSpawnNow());
        GUI.enabled = Encounters.HasActive;
        if (GUILayout.Button("End active encounter"))
        {
            Encounters.DebugEndEncounter();
            Message = "Encounter ended (" + Plugin.EndAction.Value.ToString().ToLowerInvariant() + ").";
        }
        GUI.enabled = true;
        if (GUILayout.Button("Reload files"))
        {
            Squadrons.Reload();
            Thumbs.Clear();
            Message = $"Reloaded: {Squadrons.Handmade.Count} squadron(s).";
        }
        if (GUILayout.Button("Open folder")) OpenFolder(Squadrons.UserFolder);
        if (GUILayout.Button("Music folder")) OpenFolder(Music.Folder);
        GUILayout.EndHorizontal();

        foreach (var failed in SquadronCheck.FailedFiles)
            GUILayout.Label("<color=#ff7a6a>Not loaded: " + failed + "</color>", _small);

        bool ready = AirframeChecklist.EncyclopediaReady();
        foreach (var def in list.ToList())
        {
            try
            {
                DrawRow(def, ready);
            }
            catch (Exception ex)
            {
                GUILayout.Label($"{def.Squadron ?? def.Key}: {ex.Message}", _small);
            }
        }

        if (!string.IsNullOrEmpty(Message)) GUILayout.Label(Message, _small);
        GUILayout.EndVertical();
    }

    private static void DrawRow(SquadronDef def, bool encyclopediaReady)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);

        var thumb = Thumb(def);
        if (thumb != null) GUILayout.Label(thumb, GUILayout.Width(56), GUILayout.Height(56));
        else GUILayout.Space(56);

        GUILayout.BeginVertical();
        string name = string.IsNullOrWhiteSpace(def.Squadron) ? $"({def.Key}: random name)" : def.Squadron;
        if (!string.IsNullOrWhiteSpace(def.Unit)) name += "  ·  " + def.Unit;
        Side side = Factions.Parse(def.Faction);
        name += side switch
        {
            Side.BDF => "   [BDF · Boscali Defense Force]",
            Side.PALA => "   [PALA · Primeva Armed Liberation Front]",
            _ => "   [either side]",
        };
        GUILayout.Label(name, _name);

        string org = FirstOf(def.Designation, def.Title);
        if (!string.IsNullOrEmpty(org)) GUILayout.Label(org, _small);

        string airframe;
        if (def.Aircraft is not { Count: > 0 }) airframe = "any allowed airframe";
        else if (!encyclopediaReady) airframe = string.Join(" / ", def.Aircraft);
        else
        {
            var found = AceSpawner.InstalledAirframe(def);
            airframe = found != null ? found.unitName : $"not installed ({string.Join(", ", def.Aircraft)})";
        }
        int members = def.Members?.Count ?? 0;
        string crew = members > 0 ? $"{Mathf.Min(members, AceSpawner.MaxMembers)} aces" : "2-4 aces";
        string tier = def.Tier.HasValue ? $"tier {Mathf.Clamp(def.Tier.Value, 1, 3)}" : "random tier";
        GUILayout.Label($"{airframe} · {crew} · {tier}", _small);

        string style = string.Join(" · ", new[]
        {
            Tag("naming", def.Naming), Tag("formation", def.Formation), Tag("shots", def.IntroShots), Tag("card", def.IntroStyle),
            def.LoadedEmblem != null ? (def.LoadedEmblem.Frames.Length > 1 ? $"animated emblem ({def.LoadedEmblem.Frames.Length} frames)" : "custom emblem") : null,
        }.Where(x => x != null));
        if (style.Length > 0) GUILayout.Label(style, _small);
        foreach (var problem in def.Problems ?? new List<string>())
            GUILayout.Label("<color=#ffc04d>â€¢ " + problem + "</color>", _small);
        GUILayout.EndVertical();

        GUILayout.FlexibleSpace();
        GUILayout.BeginVertical(GUILayout.Width(110));
        bool inRotation = Squadrons.IsInRotation(def);
        bool now = GUILayout.Toggle(inRotation, "In rotation");
        if (now != inRotation) Squadrons.SetInRotation(def, now);
        if (GUILayout.Button("Spawn")) Message = DebugQueue.Queue($"Spawning {def.Squadron ?? def.Key}", () => Message = Encounters.DebugSpawnNow(def));
        GUILayout.EndVertical();

        GUILayout.EndHorizontal();
    }

    /// <summary>The squadron's own emblem, else the generated one it would get (same name, colour and shape).</summary>
    private static Texture2D Thumb(SquadronDef def)
    {
        if (Thumbs.TryGetValue(def, out var cached)) return cached;
        Texture2D tex = def.LoadedEmblem?.FrameAt(0f);
        if (tex == null && !string.IsNullOrWhiteSpace(def.Squadron))
        {
            string firstColour = Variants.Entries(def.Color).FirstOrDefault();
            Color colour = !string.IsNullOrWhiteSpace(firstColour) && ColorUtility.TryParseHtmlString(firstColour, out var c) ? c : new Color(0.6f, 0.65f, 0.7f);
            EmblemShape shape = Variants.TryParse(def.EmblemShape, out EmblemShape s) && s != EmblemShape.Match ? s : EmblemShape.Match;
            if (shape == EmblemShape.Match)
            {
                NamingScheme scheme = Variants.TryParse(def.Naming, out NamingScheme n) ? n : NamingScheme.Classic;
                shape = Nomenclature.ShapeFor(scheme, Emblem.Seed(def.Squadron));
            }
            tex = Emblem.Generate(def.Squadron, colour, shape, Factions.Parse(def.Faction)).FrameAt(0f);
        }
        Thumbs[def] = tex;
        return tex;
    }

    private static string Tag(string label, string value) => string.IsNullOrWhiteSpace(value) ? null : $"{label}: {value}";

    private static string FirstOf(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Application.OpenURL("file:///" + folder.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            Message = "Could not open the folder: " + ex.Message;
        }
    }
}
