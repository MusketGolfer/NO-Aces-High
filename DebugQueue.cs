using System;
using BepInEx.Bootstrap;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Testing buttons that start something to watch (spawn a squadron, replay the intro, force a manoeuvre) close the
/// settings window (F1, BepInEx ConfigurationManager) and fire 2 s later, so you are back in the cockpit when it starts.
/// </summary>
internal static class DebugQueue
{
    private const float Delay = 2f;
    private static Action _pending;
    private static string _label;
    private static float _at;

    internal static string Queue(string label, Action action)
    {
        _pending = action;
        _label = label;
        _at = Time.unscaledTime + Delay;
        CloseSettings();
        return $"{label} in {Delay:0} s...";
    }

    internal static void Cancel() => _pending = null;

    internal static void Tick()
    {
        if (_pending == null || Time.unscaledTime < _at) return;
        var run = _pending;
        _pending = null;
        try { run(); }
        catch (Exception ex) { SquadronMenu.Message = $"{_label} failed: {ex.Message}"; Plugin.Log.LogWarning(SquadronMenu.Message); }
    }

    private static void CloseSettings()
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out var info) || info.Instance == null) return;
            var prop = info.Instance.GetType().GetProperty("DisplayingWindow");
            if (prop != null && prop.CanWrite) prop.SetValue(info.Instance, false);
        }
        catch { /* the window stays open; the delay still applies */ }
    }
}
