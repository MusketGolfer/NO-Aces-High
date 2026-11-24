using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Real (unscaled) time for the intro and music: slow motion does not stretch them. In normal play this is exactly
/// Time.unscaledTime / unscaledDeltaTime. While frames are being captured (Time.captureFramerate set, record mode)
/// it advances one capture frame per frame instead, so the intro and its music gate play at true speed in the footage
/// however long each frame takes to save.
/// </summary>
internal static class Clock
{
    private static float _now;
    private static int _frame = -1;

    internal static float Delta => Time.captureFramerate > 0 ? 1f / Time.captureFramerate : Time.unscaledDeltaTime;

    internal static float Now
    {
        get
        {
            Advance();
            return _now;
        }
    }

    private static void Advance()
    {
        if (_frame == Time.frameCount) return;
        if (_frame < 0) _now = Time.unscaledTime;
        else _now += Delta * Mathf.Max(1, Time.frameCount - _frame);
        _frame = Time.frameCount;
    }
}
