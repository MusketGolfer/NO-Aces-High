using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NOAcesHigh;

/// <summary>
/// Plays an ace squadron's intro: waits until the player is safe, cuts to a short shot list on the ace lead in
/// slow motion while the title card plays, then hands the camera back. Times are real (unscaled) seconds.
/// Falls back to the title card alone when the camera cut is off or it never becomes safe to cut away.
/// </summary>
internal static class IntroDirector
{
    // The shot list and card timing (real seconds from the start of the intro) come from IntroPlan.
    private enum Phase { Idle, Waiting, Playing }

    private static Phase _phase;
    private static ActiveEncounter _enc;
    private static float _waitStart;
    private static float _clock;          // real seconds into the intro, frozen while paused
    private static bool _cameraCut;       // false = title card only
    private static bool _ownsTime;
    private static float _setScale = 1f;
    private static CameraBaseState _previousState;
    private static readonly IntroCameraState CameraState = new();
    private static bool _linePlayed;

    private static IntroPlan _plan;

    // Pre-warm: a hidden camera renders the opening shot for a moment before the cut, so the terrain, models,
    // textures and shaders out there are loaded when the real camera arrives.
    private const float PrewarmSeconds = 1.2f;
    private static IntroPlan _primedPlan;
    private static float _primeUntil = -1f;
    private static Camera _prewarm;
    private static RenderTexture _prewarmTarget;
    private static Texture2D _black;

    private static Func<bool> _weaponCameraShowing;
    private static bool _arcadeTried;

    internal static bool Active => _phase != Phase.Idle;
    internal static bool Playing => _phase == Phase.Playing;
    internal static float Clock => _clock;
    internal static bool CameraCut => _cameraCut;
    internal static ActiveEncounter Encounter => _enc;
    internal static IntroPlan Plan => _plan;

    /// <summary>Queues the intro for a new encounter. Returns false if intros are off (caller plays the line itself).</summary>
    internal static bool Begin(ActiveEncounter enc)
    {
        if (!Plugin.PlayIntro.Value) return false;
        Abort();
        _enc = enc;
        _phase = Phase.Waiting;
        _waitStart = NOAcesHigh.Clock.Now;
        IntroGate.Reset();
        _linePlayed = false;
        return true;
    }

    /// <summary>Stops at once and gives the camera and game speed back.</summary>
    internal static void Abort()
    {
        if (_phase == Phase.Idle) return;
        // No intro line here: an abort means the fight ended, the mission changed or the intro broke.
        RestoreCamera();
        ReleaseTime();
        StopPrewarm();
        _primedPlan = null;
        _phase = Phase.Idle;
        _enc = null;
        TitleCard.Hide();
    }

    // ---- Pre-warm and the dip to black ---------------------------------------------------------------

    private static bool StartPrewarm()
    {
        try
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null || cam.mainCamera == null) return false;
            _primedPlan = IntroPlan.For(_enc, true);
            var go = new GameObject("NOAH intro prewarm");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _prewarm = go.AddComponent<Camera>();
            _prewarm.CopyFrom(cam.mainCamera);
            _prewarmTarget = new RenderTexture(480, 270, 24);
            _prewarm.targetTexture = _prewarmTarget;
            _prewarm.depth = cam.mainCamera.depth - 10f;
            _primeUntil = NOAcesHigh.Clock.Now + PrewarmSeconds;
            ShotCamera.Reset();
            UpdatePrewarm();
            Plugin.Log.LogInfo($"Intro: pre-warming the opening shot for {PrewarmSeconds:0.0} s.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Intro pre-warm failed, cutting straight away: " + ex.Message);
            StopPrewarm();
            return false;
        }
    }

    private static void UpdatePrewarm()
    {
        if (_prewarm == null || _primedPlan == null || _enc == null) return;
        if (!ShotCamera.Place(_primedPlan, _enc, _primedPlan.CutIn + 0.05f, 1f, out var frame)) return;
        _prewarm.transform.position = frame.Position;
        Vector3 dir = frame.LookAt - frame.Position;
        if (dir.sqrMagnitude > 1e-4f) _prewarm.transform.rotation = Quaternion.LookRotation(dir.normalized, frame.Up);
        _prewarm.fieldOfView = frame.Fov;
    }

    private static void StopPrewarm()
    {
        _primeUntil = -1f;
        if (_prewarm != null) UnityEngine.Object.Destroy(_prewarm.gameObject);
        _prewarm = null;
        if (_prewarmTarget != null) { _prewarmTarget.Release(); UnityEngine.Object.Destroy(_prewarmTarget); }
        _prewarmTarget = null;
        ShotCamera.Reset();
    }

    /// <summary>A brief dip to black around the cut to the aces and the cut back (hides late pop-in).</summary>
    internal static void OnGUI()
    {
        if (!Plugin.IntroFadeCuts.Value || _phase != Phase.Playing || !_cameraCut || _plan == null) return;
        float a = Mathf.Max(Dip(_clock, _plan.CutIn), Dip(_clock, _plan.CameraEnd));
        if (a <= 0.001f) return;
        if (_black == null)
        {
            _black = new Texture2D(1, 1);
            _black.SetPixel(0, 0, Color.black);
            _black.Apply();
        }
        var keep = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, a);
        GUI.depth = -1000;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _black);
        GUI.color = keep;
    }

    /// <summary>0.12 s down to black before a jump, 0.35 s back up after it.</summary>
    private static float Dip(float t, float at)
    {
        if (t < at - 0.12f || t > at + 0.35f) return 0f;
        return t < at ? Mathf.InverseLerp(at - 0.12f, at, t) : 1f - Mathf.InverseLerp(at, at + 0.35f, t);
    }

    internal static void Tick()
    {
        if (_phase == Phase.Idle) return;
        if (_enc == null || _enc.Ended || _enc.AliveCount == 0)
        {
            Abort();
            return;
        }

        if (_phase == Phase.Waiting)
        {
            // The intro music plays out first; the safety wait only starts counting once it has.
            if (!Music.IntroGateOpen)
            {
                _waitStart = NOAcesHigh.Clock.Now;
                return;
            }
            // Multiplayer rules: a title card only, at once, unless the experimental cinematic is on.
            if (!WantCameraCut)
            {
                Start(cut: false);
                return;
            }
            bool waitedOut = NOAcesHigh.Clock.Now - _waitStart > Plugin.IntroMaxWait.Value;
            if (_primeUntil >= 0f)
            {
                // Pre-warming: keep the hidden camera on the opening shot, then cut (if it is still safe).
                if (!SafeToCutAway(out string late))
                {
                    StopPrewarm();
                    if (waitedOut) { Plugin.Log.LogInfo($"Intro: not safe to cut away ({late}); showing the title card only."); Start(cut: false); }
                    return;
                }
                UpdatePrewarm();
                if (NOAcesHigh.Clock.Now >= _primeUntil)
                {
                    StopPrewarm();
                    Start(cut: true);
                }
                return;
            }
            if (SafeToCutAway(out string why))
            {
                if (Plugin.IntroPrewarm.Value && StartPrewarm()) return;
                Start(cut: true);
            }
            else if (waitedOut)
            {
                Plugin.Log.LogInfo($"Intro: not safe to cut away for {Plugin.IntroMaxWait.Value:0} s ({why}); showing the title card only.");
                Start(cut: false);
            }
            return;
        }

        // Playing. Hold the clock while the game is paused (a multiplayer game never pauses, menu or not).
        if (!ClockHeld()) _clock += NOAcesHigh.Clock.Delta;

        if (_clock > 0.3f && SkipPressed())
        {
            Plugin.Log.LogInfo("Intro skipped.");
            _clock = Mathf.Max(_clock, _plan.CameraEnd);
            TitleCard.Skip();
        }

        if (_cameraCut)
        {
            if (_clock >= _plan.CutIn && _clock < _plan.CameraEnd && DangerDuringCut(out string danger))
            {
                // Something came up while the camera was away: straight back to the pilot, card off.
                Plugin.Log.LogInfo($"Intro cut short: {danger}.");
                _cameraCut = false;
                RestoreCamera();
                ReleaseTime();
                _clock = Mathf.Max(_clock, _plan.CameraEnd);
            }
            else if (_clock >= _plan.CutIn && _clock < _plan.CameraEnd)
            {
                if (EnsureCameraCut()) KeepSlowMotion();
                else
                {
                    // The camera went somewhere else (player died, view changed): finish as a title card.
                    _cameraCut = false;
                    ReleaseTime();
                }
            }
            else if (_clock >= _plan.CameraEnd)
            {
                RestoreCamera();
                ReleaseTime();
            }
        }

        if (!_linePlayed && _clock >= _plan.CameraEnd + 0.2f) PlayIntroLine();

        if (_clock >= _plan.Total)
        {
            RestoreCamera();
            ReleaseTime();
            _phase = Phase.Idle;
            _enc = null;
        }
    }

    private static void Start(bool cut)
    {
        _phase = Phase.Playing;
        _clock = 0f;
        _cameraCut = cut;
        _plan = cut && _primedPlan != null ? _primedPlan : IntroPlan.For(_enc, cut);
        _primedPlan = null;
        ShotCamera.Reset();
        Music.OnIntroStart();
        TitleCard.Show(_enc, cut, _plan);
        Plugin.Log.LogInfo($"Intro for {_enc.SquadronName}: {(cut ? $"cinematic, {_plan.Shots} shots" : "title card only")}, {_plan.Card} card" +
                           (Session.MultiplayerRules ? $", multiplayer rules ({(SlowMotionAllowed && cut ? "experimental slow motion" : "no slow motion")})." : "."));
    }

    /// <summary>Camera cut wanted: on in single player; in multiplayer only with the experimental setting.</summary>
    private static bool WantCameraCut =>
        Plugin.IntroCameraCut.Value && (!Session.MultiplayerRules || Plugin.MultiplayerCinematic.Value);

    /// <summary>Slow motion: always in single player; in multiplayer only with the experimental toggle.</summary>
    private static bool SlowMotionAllowed => !Session.MultiplayerRules || Plugin.MultiplayerSlowMotion.Value;

    private static bool ClockHeld() =>
        Time.timeScale == 0f || (!Session.InMultiplayer && GameplayUI.GameIsPaused);

    /// <summary>Live checks while the camera is away (they matter most in multiplayer, where nothing is slowed down).</summary>
    private static bool DangerDuringCut(out string why)
    {
        why = null;
        var player = Session.LocalAircraft;
        if (player == null) { why = "no player aircraft"; return true; }
        var mw = player.GetMissileWarningSystem();
        if (mw != null && mw.IsWarning()) { why = "missile inbound"; return true; }
        if (player.radarAlt < 300f) { why = "below 300 m"; return true; }
        return false;
    }

    private static void PlayIntroLine()
    {
        _linePlayed = true;
        // In multiplayer the host sends the opening line to everyone on a fixed delay instead.
        if (!Session.MultiplayerRules) Chatter.Say(_enc, "intro", _enc.LeadMember, 0f);
    }

    // ---- Safety ------------------------------------------------------------------------------------

    private static bool SafeToCutAway(out string why)
    {
        why = null;
        if (!GameManager.GetLocalAircraft(out var player) || player == null || player.disabled) { why = "no player aircraft"; return false; }
        if (GamePaused()) { why = "paused"; return false; }
        if (Mathf.Abs(Time.timeScale - 1f) > 0.01f) { why = "game speed already changed"; return false; }
        var mw = player.GetMissileWarningSystem();
        if (mw != null && mw.IsWarning()) { why = "missile inbound"; return false; }
        if (player.radarAlt < 300f) { why = "too low"; return false; }
        if (player.gearDeployed) { why = "gear down"; return false; }
        if (player.speed > 30f && player.rb != null && Vector3.Angle(player.transform.forward, player.rb.velocity) > 15f) { why = "pulling hard"; return false; }
        if (WeaponCameraShowing()) { why = "ArcadeCam weapon view"; return false; }
        var cam = SceneSingleton<CameraStateManager>.i;
        if (cam == null || cam.currentState == null) { why = "no camera"; return false; }
        if (LeadAircraft() == null) { why = "no ace to film"; return false; }
        return true;
    }

    private static bool GamePaused() => GameplayUI.GameIsPaused || Time.timeScale == 0f;

    private static bool SkipPressed()
    {
        try
        {
            var input = GameManager.playerInput;
            return input != null && (input.GetButtonDown("Fire") || input.GetButtonDown("Center"));
        }
        catch
        {
            return false;
        }
    }

    private static bool WeaponCameraShowing()
    {
        if (!_arcadeTried)
        {
            _arcadeTried = true;
            try
            {
                var prop = Type.GetType("ArcadeCam.ArcadeCamApi, ArcadeCam")?.GetProperty("WeaponCameraShowing", BindingFlags.Static | BindingFlags.Public);
                if (prop != null) _weaponCameraShowing = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), prop.GetGetMethod());
            }
            catch { /* ArcadeCam API changed: just do not wait on it */ }
        }
        try { return _weaponCameraShowing != null && _weaponCameraShowing(); }
        catch { return false; }
    }

    // ---- Time --------------------------------------------------------------------------------------

    /// <summary>Slows the game only if nothing else has changed its speed, and never fights the pause menu.</summary>
    private static void KeepSlowMotion()
    {
        float slow = Mathf.Clamp(Plugin.IntroSlowMotion.Value, 0.05f, 1f);
        if (GamePaused() || !SlowMotionAllowed) return;

        if (!_ownsTime)
        {
            if (Mathf.Abs(Time.timeScale - 1f) > 0.01f) return;
            _ownsTime = true;
            _setScale = slow;
            Time.timeScale = slow;
        }
        else if (Mathf.Abs(Time.timeScale - _setScale) > 0.001f)
        {
            // Something else (pause/unpause) reset it; put ours back.
            Time.timeScale = _setScale;
        }
    }

    private static void ReleaseTime()
    {
        if (!_ownsTime) return;
        _ownsTime = false;
        if (!GameplayUI.GameIsPaused && Mathf.Abs(Time.timeScale - _setScale) < 0.001f) Time.timeScale = 1f;
    }

    // ---- Camera ------------------------------------------------------------------------------------

    private static bool EnsureCameraCut()
    {
        var cam = SceneSingleton<CameraStateManager>.i;
        if (cam == null) return false;
        if (cam.currentState == CameraState) return true;
        if (_previousState != null) return false; // someone switched away from us mid-intro: give up the cut
        if (!GameManager.GetLocalAircraft(out var player) || player == null || player.disabled) return false;
        _previousState = cam.currentState;
        cam.SwitchState(CameraState);
        return true;
    }

    private static void RestoreCamera()
    {
        var cam = SceneSingleton<CameraStateManager>.i;
        if (cam != null && cam.currentState == CameraState)
        {
            var back = _previousState ?? (CameraBaseState)cam.chaseState;
            try { cam.SwitchState(back); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Intro: could not restore the previous view, using chase: " + ex.Message);
                cam.SwitchState(cam.chaseState);
            }
        }
        _previousState = null;
    }

    private static Aircraft LeadAircraft()
    {
        if (_enc == null) return null;
        var lead = _enc.LeadMember;
        if (lead is { Alive: true } && lead.Aircraft != null && !lead.Aircraft.disabled) return lead.Aircraft;
        return _enc.Members.FirstOrDefault(m => m.Alive && m.Aircraft != null && !m.Aircraft.disabled)?.Aircraft;
    }

    /// <summary>Called by the camera state every LateUpdate while the cut is showing.</summary>
    internal static void UpdateCamera(CameraStateManager cam)
    {
        if (_plan == null || _enc == null) return;
        // Shots that park the camera ahead of the flight need to know how fast it will really move.
        float slow = SlowMotionAllowed ? Mathf.Clamp(Plugin.IntroSlowMotion.Value, 0.05f, 1f) : 1f;
        if (!ShotCamera.Place(_plan, _enc, _clock, slow, out var frame)) return;

        cam.cameraPivot.localPosition = frame.Position.ToGlobalPosition().AsVector3();
        cam.transform.localPosition = Vector3.zero;
        Vector3 dir = frame.LookAt - cam.transform.position;
        if (dir.sqrMagnitude > 1e-4f) cam.transform.rotation = Quaternion.LookRotation(dir.normalized, frame.Up);
        cam.mainCamera.fieldOfView = frame.Fov;
        cam.cameraVelocity = Vector3.zero;
    }

    /// <summary>
    /// A camera state that only places the camera where the director says. The game keeps following the
    /// player's aircraft (following the ace would be dropped by FollowCheck when it is not tracked).
    /// </summary>
    private sealed class IntroCameraState : CameraBaseState
    {
        private float _fov;

        public override void EnterState(CameraStateManager cam)
        {
            _fov = cam.mainCamera.fieldOfView;
            cam.cameraPivot.SetParent(Datum.origin);
            cam.transform.SetParent(cam.cameraPivot);
            cam.transform.localPosition = Vector3.zero;
            cam.cameraVelocity = Vector3.zero;
            cam.mainCamera.nearClipPlane = 1f;
            FlightHud.EnableCanvas(enable: false);
            UpdateCamera(cam);
        }

        public override void LeaveState(CameraStateManager cam)
        {
            cam.mainCamera.fieldOfView = cam.desiredFOV > 0f ? cam.desiredFOV : _fov;
        }

        public override void UpdateState(CameraStateManager cam)
        {
            try
            {
                UpdateCamera(cam);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Intro camera failed, ending the cut: " + ex.Message);
                _cameraCut = false;
                RestoreCamera();
                ReleaseTime();
            }
        }

        public override void FixedUpdateState(CameraStateManager cam) { }
    }
}
