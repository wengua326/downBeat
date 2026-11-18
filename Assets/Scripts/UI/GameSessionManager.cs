using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using RhythmDartMR.Audio;
using RhythmDartMR.Gameplay;
using RhythmDartMR.Input;

namespace RhythmDartMR.UI
{
    public enum SessionEndReason
    {
        None,
        TimeUp,
        OutOfDarts
    }

    /// <summary>
    /// Timed single-player session: the round ends when the clock runs out (default 3:00) or when all darts
    /// (default 20) have been thrown and have landed — whichever comes first. Power-ups can add time and darts.
    /// The clock is frozen while the power-up selection screen is open (<see cref="IsPaused"/>).
    /// </summary>
    public class GameSessionManager : MonoBehaviour
    {
        public static GameSessionManager Instance { get; private set; }

        [Header("Session Settings")]
        [Tooltip("Round length in seconds (power-ups can add more).")]
        public float sessionSeconds = 120f;
        [Tooltip("Darts per round (power-ups can add more).")]
        public int dartsPerSession = 20;
        public bool autoStartOnAwake = true;

        [Header("Legacy (kept for scene compatibility)")]
        public int maxThrowsPerSession = 20;
        public float roundDurationSeconds = 120f;

        public bool IsRoundActive { get; private set; }
        public bool IsCountingDown { get; private set; }
        public int RemainingThrows { get; private set; } = 20;
        public int ThrowsScoredCount { get; private set; } = 0;
        public SessionEndReason LastEndReason { get; private set; } = SessionEndReason.None;

        private float _sessionStartRealTime;
        private float _activeSeconds;   // game time that has elapsed (excludes pauses)
        private float _bonusSeconds;    // added by power-ups
        private int _dartsThrown;
        private int _dartsLanded;
        public float RoundEndRealTime { get; private set; } = -10f;

        /// <summary>True while the power-up selection screen is open: clock frozen, throws are free and pick a card.</summary>
        public bool IsPaused => BuffSystem.Instance != null && BuffSystem.Instance.IsChoosing;

        /// <summary>Game time used in the current session (pauses excluded; frozen after the round ends).</summary>
        public float ElapsedSessionSeconds => _activeSeconds;

        /// <summary>Total length of this round including bonus time.</summary>
        public float SessionLengthSeconds => sessionSeconds + _bonusSeconds;

        /// <summary>Seconds left on the clock.</summary>
        public float TimeRemainingSeconds => Mathf.Max(0f, SessionLengthSeconds - _activeSeconds);

        public float RemainingTimeSeconds => TimeRemainingSeconds;

        /// <summary>Raised when a power-up adds time (seconds) — used by the HUD for a "+15s" flash.</summary>
        public event System.Action<float> OnBonusTimeAdded;
        public event System.Action<int> OnBonusDartsAdded;

        private void Update()
        {
            if (!IsRoundActive || IsPaused) return;
            _activeSeconds += Time.deltaTime;
            if (TimeRemainingSeconds <= 0f)
            {
                EndRound(SessionEndReason.TimeUp);
            }
        }

        public void AddBonusTime(float seconds)
        {
            if (!IsRoundActive || seconds <= 0f) return;
            _bonusSeconds += seconds;
            OnBonusTimeAdded?.Invoke(seconds);
        }

        public void AddDarts(int count)
        {
            if (!IsRoundActive || count <= 0) return;
            RemainingThrows += count;
            maxThrowsPerSession += count;
            OnBonusDartsAdded?.Invoke(count);
        }

        /// <summary>Ends the round if every dart has been thrown and has landed (skipped while paused).</summary>
        public void CheckEndConditions()
        {
            if (!IsRoundActive || IsPaused) return;
            if (RemainingThrows <= 0 && _dartsLanded >= _dartsThrown)
            {
                EndRound(SessionEndReason.OutOfDarts);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeSceneBootstrap()
        {
            InstallSafeOvrLogCallback();
            DisableUnusedMrukGlobalContext();
            EnsureSimulatorSyntheticRoomRunning();
            EnsureRealImagesCopiedToResources();

            foreach (string oldName in new[] { "FirstShapes", "Floor" })
            {
                var oldObj = GameObject.Find(oldName);
                if (oldObj != null) Destroy(oldObj);
            }

            // Hide synthetic passthrough room only in Editor when the simulated backdrop is active
            var switcher = UnityEngine.Object.FindAnyObjectByType<RhythmDartMR.VFX.BackgroundVideoSwitcher>();
            bool usingVideoBackdrop = Application.isEditor &&
                                      (switcher != null
                                          ? switcher.IsVideoBackdropActive
                                          : (GameObject.Find("VideoBackground") != null ||
                                             File.Exists(Path.Combine(Application.dataPath, "2.mp4"))));

            var passthroughBlock = GameObject.Find("[BuildingBlock] Passthrough");
            if (passthroughBlock != null)
            {
                passthroughBlock.SetActive(true);
                var ptLayer = passthroughBlock.GetComponent<OVRPassthroughLayer>();
                if (ptLayer != null)
                {
                    ptLayer.hidden = usingVideoBackdrop;
                    ptLayer.enabled = !usingVideoBackdrop;
                    ptLayer.textureOpacity = 1f;
                }
            }

            var camRig = GameObject.Find("[BuildingBlock] Camera Rig");
            if (camRig != null)
            {
                var ovrManager = camRig.GetComponent<OVRManager>();
                if (ovrManager != null)
                {
                    ovrManager.isInsightPassthroughEnabled = true;
                }
                foreach (var cam in camRig.GetComponentsInChildren<Camera>(true))
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }
            }

            foreach (string handName in new[] { "[BuildingBlock] Hand Tracking Left", "[BuildingBlock] Hand Tracking Right" })
            {
                var handGo = GameObject.Find(handName);
                if (handGo == null) continue;
                foreach (var smr in handGo.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.enabled = false;
                foreach (var mr in handGo.GetComponentsInChildren<OVRMeshRenderer>(true)) mr.enabled = false;
                foreach (var sr in handGo.GetComponentsInChildren<OVRSkeletonRenderer>(true)) sr.enabled = false;
            }

            if (Instance != null || GameObject.Find("RhythmDartMR_Root") != null) return;

            var root = new GameObject("RhythmDartMR_Root");

            var conductorObj = new GameObject("BeatConductor");
            conductorObj.transform.SetParent(root.transform, false);
            conductorObj.AddComponent<BeatConductor>();

            var audioObj = new GameObject("DrumPadAudioEngine");
            audioObj.transform.SetParent(root.transform, false);
            audioObj.AddComponent<DrumPadAudioEngine>();

            var scoreObj = new GameObject("ScoreAndComboManager");
            scoreObj.transform.SetParent(root.transform, false);
            scoreObj.AddComponent<ScoreAndComboManager>();

            var sessionObj = new GameObject("GameSessionManager");
            sessionObj.transform.SetParent(root.transform, false);
            sessionObj.AddComponent<GameSessionManager>();

            var boardObj = new GameObject("RhythmDartboard");
            boardObj.transform.SetParent(root.transform, false);
            boardObj.transform.position = RhythmDartboard.DefaultBoardPosition;
            boardObj.transform.rotation = Quaternion.identity;
            boardObj.transform.localScale = Vector3.one * RhythmDartboard.DefaultBoardScale;
            boardObj.AddComponent<RhythmDartboard>();

            var handThrowObj = new GameObject("HandThrowController");
            handThrowObj.transform.SetParent(root.transform, false);
            handThrowObj.AddComponent<HandThrowController>();

            var hudObj = new GameObject("MixedRealityHUD");
            hudObj.transform.SetParent(root.transform, false);
            hudObj.AddComponent<MixedRealityHUD>();
        }

        public static void EnsureRealImagesCopiedToResources()
        {
            // Resources/RealDartboard.jpg and Resources/RoomBackdropStatic.jpg are packaged directly in Assets/Resources.
        }

        private static void EnsureSimulatorSyntheticRoomRunning()
        {
            if (!Application.isEditor) return;
            try
            {
                if (Process.GetProcessesByName("synth_env_server").Length > 0) return;

                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string exePath = Path.Combine(programFiles, @"MetaXRSimulator\v207.1\synthetic_env_server\synth_env_server.exe");
                if (File.Exists(exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "GameRoom",
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }
            }
            catch
            {
            }
        }

        public static void DisableUnusedMrukGlobalContext()
        {
            var mrukGlobal = GameObject.Find("MRUKGlobalContext");
            if (mrukGlobal != null && mrukGlobal.activeSelf)
            {
                mrukGlobal.SetActive(false);
            }
        }

        private void Awake()
        {
            InstallSafeOvrLogCallback();
            DisableUnusedMrukGlobalContext();
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            sessionSeconds = 120f;
            roundDurationSeconds = 120f;
            maxThrowsPerSession = dartsPerSession;
            RemainingThrows = maxThrowsPerSession;
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            InstallSafeOvrLogCallback();
            DisableUnusedMrukGlobalContext();
        }

        private void LateUpdate()
        {
            if (Time.frameCount <= 60)
            {
                InstallSafeOvrLogCallback();
            }
        }

        private void OnDisable()
        {
            InstallSafeOvrLogCallback();
        }

        private void OnApplicationQuit()
        {
            InstallSafeOvrLogCallback();
        }

        private void OnDestroy()
        {
            InstallSafeOvrLogCallback();
            if (Instance == this) Instance = null;
        }

#if UNITY_EDITOR
        private static readonly OVRPlugin.LogCallback2DelegateType SafeOvrLogDelegate = SafeOvrPluginLogCallback;

        [AOT.MonoPInvokeCallback(typeof(OVRPlugin.LogCallback2DelegateType))]
        private static void SafeOvrPluginLogCallback(OVRPlugin.LogLevel logLevel, IntPtr message, int size)
        {
            if (message == IntPtr.Zero || size <= 0) return;
            try
            {
                int copyLen = Mathf.Clamp(size, 0, 4096);
                byte[] buf = new byte[copyLen];
                Marshal.Copy(message, buf, 0, copyLen);
                int actualLen = 0;
                while (actualLen < copyLen && buf[actualLen] != 0) actualLen++;
                if (actualLen <= 0) return;

                string logString = System.Text.Encoding.UTF8.GetString(buf, 0, actualLen);
                if (logString.Contains("xrGetAudioInputDeviceGuidOculus") ||
                    logString.Contains("Failed to request exit session") ||
                    logString.Contains("xrDestroySession") ||
                    logString.Contains("xrRetrieveSpaceDiscoveryResultsMETA"))
                {
                    return;
                }

                if (logLevel <= OVRPlugin.LogLevel.Info)
                {
                    UnityEngine.Debug.Log("[OVRPlugin] " + logString);
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[OVRPlugin] " + logString);
                }
            }
            catch
            {
            }
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallSafeOvrLogCallback()
        {
#if UNITY_EDITOR
            try
            {
                OVRPlugin.SetLogCallback2(SafeOvrLogDelegate);
            }
            catch
            {
            }
#endif
        }

        private void Start()
        {
            if (autoStartOnAwake)
            {
                Invoke(nameof(StartNewRound), 0.4f);
            }
        }

        public bool TryConsumeThrowOpportunity()
        {
            if (!IsRoundActive) return false;
            if (IsPaused) return true; // power-up selection darts are free

            if (RemainingThrows <= 0) return false;

            RemainingThrows--;
            _dartsThrown++;
            return true;
        }

        public void NotifyDartScored()
        {
            if (!IsRoundActive) return;

            ThrowsScoredCount++;
            _dartsLanded++;
            CheckEndConditions();
        }

        public void StartNewRound()
        {
            if (IsCountingDown) return;

            // Before resetting score for the new session, ensure any completed score is saved as high score
            if (ScoreAndComboManager.Instance != null)
            {
                ScoreAndComboManager.Instance.RecordCompletedSessionScore();
                ScoreAndComboManager.Instance.ResetStats();
            }

            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.ClearAllCuesAndDarts();
            }

            if (BeatConductor.Instance != null)
            {
                BeatConductor.Instance.StopConductor();
            }

            if (DrumPadAudioEngine.Instance != null)
            {
                DrumPadAudioEngine.Instance.StopBackingMusic();
            }

            if (BuffSystem.Instance != null)
            {
                BuffSystem.Instance.ResetAll();
            }

            sessionSeconds = 120f;
            roundDurationSeconds = 120f;
            maxThrowsPerSession = dartsPerSession;
            RemainingThrows = maxThrowsPerSession;
            ThrowsScoredCount = 0;
            _dartsThrown = 0;
            _dartsLanded = 0;
            _activeSeconds = 0f;
            _bonusSeconds = 0f;
            LastEndReason = SessionEndReason.None;
            IsRoundActive = false;
            IsCountingDown = true;

            SessionCountdownOverlay.EnsureInstance().PlayCountdown(ActivateRoundAfterCountdown);
        }

        private void ActivateRoundAfterCountdown()
        {
            if (!IsCountingDown) return;

            IsCountingDown = false;
            _activeSeconds = 0f;
            _sessionStartRealTime = Time.time;
            IsRoundActive = true;

            if (DrumPadAudioEngine.Instance != null)
            {
                DrumPadAudioEngine.Instance.StartBackingMusic();
            }

            if (BeatConductor.Instance != null)
            {
                BeatConductor.Instance.StartConductor();
            }
        }

        public void EndRound()
        {
            EndRound(SessionEndReason.None);
        }

        public void EndRound(SessionEndReason reason)
        {
            if (!IsRoundActive && !IsCountingDown) return;
            LastEndReason = reason;
            IsRoundActive = false;
            IsCountingDown = false;
            RoundEndRealTime = Time.time;

            if (SessionCountdownOverlay.Instance != null)
            {
                SessionCountdownOverlay.Instance.StopImmediate();
            }

            if (BuffSystem.Instance != null)
            {
                BuffSystem.Instance.ResetAll();
            }

            if (ScoreAndComboManager.Instance != null)
            {
                ScoreAndComboManager.Instance.RecordCompletedSessionScore();
            }

            if (BeatConductor.Instance != null)
            {
                BeatConductor.Instance.StopConductor();
            }

            if (DrumPadAudioEngine.Instance != null)
            {
                DrumPadAudioEngine.Instance.StopBackingMusic();
            }

            HandThrowController.Instance?.OnRoundEnded();
        }
    }
}
