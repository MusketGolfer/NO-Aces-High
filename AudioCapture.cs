using System;
using System.IO;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Record mode (-noahAudio): the game's sound, rendered offline in step with the captured frames (Time.captureFramerate),
/// written to audio.wav beside each clip's frames, so ffmpeg can lay it under the video in sync. Music (the game's and
/// the aces') is held at zero while recording.
/// </summary>
internal static class AudioCapture
{
    internal static bool Requested => Environment.GetCommandLineArgs() is { } a && Array.Exists(a, x => x.Equals("-noahAudio", StringComparison.OrdinalIgnoreCase));

    private static FileStream _file;
    private static BinaryWriter _w;
    private static long _samples;
    private static int _channels, _rate;
    private static bool _on;
    private static int _frames;

    private static readonly AccessTools.FieldRef<MusicManager, AudioSource> Current = AccessTools.FieldRefAccess<MusicManager, AudioSource>("currentSource");
    private static readonly AccessTools.FieldRef<MusicManager, AudioSource> Fade = AccessTools.FieldRefAccess<MusicManager, AudioSource>("fadeSource");

    internal static void Begin(string wavPath)
    {
        if (!Requested) return;
        End();
        try
        {
            _rate = AudioSettings.outputSampleRate;
            _channels = AudioSettings.speakerMode == AudioSpeakerMode.Mono ? 1 : 2;
            _file = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
            _w = new BinaryWriter(_file);
            _w.Write(new byte[44]); // header, filled in at the end
            _samples = 0;
            _on = AudioRenderer.Start();
            if (!_on) Plugin.Log.LogWarning("Audio capture: the audio renderer would not start.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Audio capture: " + ex.Message); _on = false; }
    }

    /// <summary>After each captured frame: that frame's share of sound, as 16-bit PCM.</summary>
    internal static void Frame()
    {
        if (!_on) return;
        Mute();
        try
        {
            int n = AudioRenderer.GetSampleCountForCaptureFrame();
            if (n <= 0) return;
            using var buf = new NativeArray<float>(n * _channels, Allocator.Temp);
            AudioRenderer.Render(buf);
            for (int i = 0; i < buf.Length; i++)
                _w.Write((short)Mathf.Clamp(Mathf.RoundToInt(buf[i] * 32767f), -32768, 32767));
            _samples += n;
            if (++_frames % 30 == 0) { WriteHeader(); _w.Seek(0, SeekOrigin.End); } // a run cut short still leaves a playable file
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Audio capture stopped: " + ex.Message); End(); }
    }

    internal static void Mute()
    {
        try
        {
            var mm = MusicManager.i;
            if (mm == null) return;
            if (Current(mm) != null) Current(mm).volume = 0f;
            if (Fade(mm) != null) Fade(mm).volume = 0f;
        }
        catch { /* no music manager */ }
    }

    internal static void End()
    {
        if (_w == null) return;
        try
        {
            if (_on) AudioRenderer.Stop();
            WriteHeader();
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Audio capture: could not finish the file: " + ex.Message); }
        finally
        {
            _w.Dispose();
            _file?.Dispose();
            _w = null; _file = null; _on = false; _frames = 0;
        }
    }

    private static void WriteHeader()
    {
        long dataBytes = _samples * _channels * 2;
        _w.Seek(0, SeekOrigin.Begin);
        _w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        _w.Write((int)(36 + dataBytes));
        _w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        _w.Write(16); _w.Write((short)1); _w.Write((short)_channels); _w.Write(_rate);
        _w.Write(_rate * _channels * 2); _w.Write((short)(_channels * 2)); _w.Write((short)16);
        _w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        _w.Write((int)dataBytes);
        _w.Flush();
    }
}
