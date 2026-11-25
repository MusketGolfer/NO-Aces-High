using System.Linq;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Radio banter not tied to any action: every so often (a random gap between ChatterMinSeconds and ChatterMaxSeconds,
/// then a ChatterChance roll) one of the aces says something. On the way in the line comes from "chatterIngress",
/// once the fight is on from "chatterFight"; a squadron can also give a plain "chatter" list for both. Never during
/// the intro, and never within a few seconds of another radio line, so it does not talk over the action.
/// </summary>
internal static class AmbientChatter
{
    private const float QuietAfterOtherLines = 6f;
    private static ActiveEncounter _for;
    private static float _next;

    internal static void Reset() => _for = null;

    internal static void Tick(ActiveEncounter enc, Aircraft target)
    {
        if (!Plugin.ChatterEnabled.Value || enc == null || enc.Ended) return;
        if (IntroDirector.Active || IntroGate.Holding) return;
        float now = Time.time;
        float min = Mathf.Max(5f, Plugin.ChatterMinSeconds.Value), max = Mathf.Max(min, Plugin.ChatterMaxSeconds.Value);
        if (_for != enc) { _for = enc; _next = now + Random.Range(min * 0.4f, min); return; } // first line soon after the intro
        if (now < _next) return;
        _next = now + Random.Range(min, max);
        if (now - Chatter.LastLineAt < QuietAfterOtherLines) return;
        if (Random.value > Plugin.ChatterChance.Value) return;

        var alive = enc.Members.Where(m => m.Alive && m.Aircraft != null && !m.Aircraft.disabled).ToList();
        if (alive.Count == 0) return;
        var wingmen = alive.Where(m => !m.Lead).ToList();
        var speaker = wingmen.Count > 0 && Random.value < 0.6f ? wingmen[Random.Range(0, wingmen.Count)] : alive.FirstOrDefault(m => m.Lead) ?? alive[0];

        // On the way in: still in formation, or (a lone ace) well short of the target.
        bool ingress = enc.FormationHeld;
        if (!ingress && alive.Count == 1 && target != null)
            ingress = Vector3.Distance(alive[0].Aircraft.transform.position, target.transform.position) > 15000f;
        string phase = ingress ? "chatterIngress" : "chatterFight";
        string key = Own(enc, phase) ? phase : Own(enc, "chatter") ? "chatter" : phase;
        Chatter.Say(enc, key, speaker, 0f);
    }

    private static bool Own(ActiveEncounter enc, string key) =>
        enc.Def?.Lines != null && enc.Def.Lines.TryGetValue(key, out var l) && l is { Count: > 0 };
}
