using System.Collections.Generic;
using UnityEngine;
using RhythmDartMR.Audio;
using RhythmDartMR.Gameplay;
using RhythmDartMR.UI;

namespace RhythmDartMR.Input
{
    /// <summary>
    /// Competition-Grade Hand Tracking & XR Simulator Aim and Throw Controller.
    /// Features Automatic Dual-Profile Calibration:
    /// 1. Simulator (2D Laptop Webcam) Profile (active automatically in Unity Editor):
    ///    - Combines 90ms sliding-window forward velocity WITH wrist pitch/angular snap detection so
    ///      natural dart throws trigger reliably despite 2D webcam Z-depth clamping.
    ///    - Tolerates 25-30 FPS webcam frame steps (0.55m jump threshold) without wiping throw history.
    ///    - Allows pinch-release even when 2D finger overlap lowers MediaPipe confidence.
    /// 2. Real Meta Quest Device Profile (active automatically on Quest Android builds or when forceQuestDeviceProfile is ticked):
    ///    - Uses true 3D stereo depth thresholds (0.72 m/s forward push or 0.48 m/s + wrist snap),
    ///      high-confidence gating, and 90Hz frame-jump filtering (0.20m).
    /// 3. 220ms Lookback Pre-Throw Static Aim Lock (both profiles):
    ///    - Freezes the aimpoint at the exact spot you were aiming BEFORE the throwing motion started,
    ///      hides the held dart immediately on throw, and keeps the aimpoint 100% static until the dart
    ///      lands and the score is recorded.
    /// </summary>
    public class HandThrowController : MonoBehaviour
    {
        public static HandThrowController Instance { get; private set; }

        [Header("Meta XR Hand References")]
        public OVRHand rightOvrHand;
        public OVRHand leftOvrHand;
        public Transform trackingSpace;
        public Transform rightHandAnchor;
        public Transform leftHandAnchor;
        public Transform centerEyeAnchor;

        [Header("Platform Profile Selection")]
        [Tooltip("Leave unticked for 2D Laptop Webcam in Meta XR Simulator. Automatically uses Real Quest 3D profile in Android builds.")]
        public bool forceQuestDeviceProfile = false;

        [Header("Simulator Mouse Aim & Click Input")]
        [Tooltip("Allows aiming with the mouse cursor and Left-Clicking to throw a dart (or pick a power card / restart a round) in the Unity Editor / Simulator.")]
        public bool enableSimulatorMouseInput = true;

        [Header("Aim Sensitivity (1.2x) & Smoothing")]
        public float aimSensitivity = 1.25f;
        [Range(4f, 30f)] public float aimSmoothingSpeed = 16f;
        public float aimAssistRadius = 0.13f;
        [Range(0f, 1f)] public float aimAssistStrength = 0.35f;

        [Header("Aim Gain (higher = crosshair travels further per cm of hand movement)")]
        public float handAimGain = 2.4f;
        public float rayAimWeight = 0.45f;
        [Tooltip("Smoothing when the hand is nearly still (lower = steadier crosshair).")]
        [Range(4f, 40f)] public float aimSmoothingMin = 10f;
        [Tooltip("Smoothing when the hand moves fast (higher = snappier crosshair).")]
        [Range(4f, 60f)] public float aimSmoothingMax = 38f;

        [Header("Real Meta Quest Device Throw Thresholds (3D Stereo Depth)")]
        public bool allowMotionThrow = true;
        [Tooltip("Disabled by default so uncurling fingers while pulling your hand backward never triggers a false throw.")]
        public bool allowPinchReleaseThrow = false;
        public float flickForwardSpeed = 0.72f;
        public float flickSustainSeconds = 0.030f;
        public float minPinchHoldSeconds = 0.06f;
        public float pinchReleaseDebounceSeconds = 0.035f;
        public float trackingJumpRejectMeters = 0.20f;

        [Header("Simulator 2D Webcam Throw Thresholds (Pure Forward Motion - Auto-Active in Editor)")]
        public float simWebcamForwardSpeed = 0.24f;
        public float simWebcamSustainSeconds = 0.020f;
        public float simWebcamJumpRejectMeters = 0.55f;
        [Tooltip("Lockout duration (seconds) after moving the hand backward so retracting/resetting the hand never triggers a throw.")]
        public float backwardRetractLockoutSeconds = 0.32f;

        [Header("Shared Pinch & Flight Tuning")]
        [Range(0.30f, 0.85f)] public float pinchGrabThreshold = 0.44f;
        [Range(0.15f, 0.50f)] public float pinchReleaseThreshold = 0.28f;
        public float throwCooldownSeconds = 0.45f;
        public double dartFlightBeats = 0.70;

        private struct HandHistorySample
        {
            public float time;
            public Vector3 handPos;
            public Quaternion handRot;
            public Vector3 aimWorldPos;
        }

        private readonly List<HandHistorySample> _history = new List<HandHistorySample>(64);

        private OVRPlugin.HandState _rightHandState;
        private OVRPlugin.HandState _leftHandState;
        private OVRPlugin.Hand _activeTrackedHand = OVRPlugin.Hand.None;

        private Vector3 _smoothedHandPos = new Vector3(0.12f, 1.15f, 0.38f);
        private Quaternion _smoothedHandRot = Quaternion.identity;
        private Vector3 _smoothedAimDir = Vector3.forward;
        private Vector3 _currentReticleWorldPos;

        // Static aimpoint lock from the start of throw motion until the score is recorded
        private bool _isAimStaticUntilScoreRecorded;
        private Vector3 _lockedAimWorldPos;
        private Vector3 _lockedLaunchHandPos;
        private float _aimLockSafetyTimer;
        private const float MaxAimLockDurationSeconds = 2.0f;

        private int _consecutiveTrackedFrames = 0;
        private const int WarmupFramesAfterTrackingRegained = 8;

        private bool _wasPinching;
        private float _pinchHoldTime;
        private float _lastThrowTime = -10f;

        // 70ms camera-presence check: ONLY cancels if the hand disappears from the camera lens (!isTracked)
        private bool _hasPendingThrow;
        private float _pendingThrowTimer;
        private const float StillInCameraConfirmationSeconds = 0.070f;

        private GameObject _aimDotObj;
        private LineRenderer _aimOuterRing;
        private LineRenderer _aimOutlineRing;
        private LineRenderer[] _aimTicks;
        private Material _aimDotMat;
        private GameObject _heldDartPreview;

        public Vector3 CurrentAimWorldPoint => _currentReticleWorldPos;

        // False-throw & backward-retraction protection state
        private float _pinchReleaseTimer;
        private float _flickTime;
        private float _backwardRetractLockoutTimer;
        private bool _handConfidenceHigh = true;
        private bool _sawHighConfidence;
        private Vector3 _lastRawHandPos;

        // Post-session confirmation: waits for explicit forward throw motion after 1.5s cooldown
        private bool _postSessionSawNeutral;
        private float _postSessionNeutralTimer;
        private float _postSessionThrowTimer;
        private float _gameOverCooldownUntil;

        // Simulator / Editor click-to-drag aim & unclick-to-release state
        private bool _isMouseDraggingAim;

        /// <summary>
        /// True when running in the Unity Editor (Meta XR Simulator 2D Webcam mode) unless forceQuestDeviceProfile is set.
        /// False in Android APK builds on a real Meta Quest headset.
        /// </summary>
        private bool IsSimulatorWebcamProfile => Application.isEditor && !forceQuestDeviceProfile;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            allowMotionThrow = true;
            allowPinchReleaseThrow = false;
            aimSensitivity = 1.25f;
            aimSmoothingSpeed = 16f;
            aimAssistStrength = 0.35f;
            pinchGrabThreshold = 0.44f;
            pinchReleaseThreshold = 0.28f;
            throwCooldownSeconds = 0.42f;
            dartFlightBeats = 0.70;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Clears active throw/pitch/pinch states and starts a 1.5s Game Over input cooldown
        /// so trailing throw follow-through never auto-restarts the next session.
        /// </summary>
        public void OnRoundEnded()
        {
            _hasPendingThrow = false;
            _pendingThrowTimer = 0f;
            _isAimStaticUntilScoreRecorded = false;
            _aimLockSafetyTimer = 0f;
            _wasPinching = false;
            _pinchHoldTime = 0f;
            _pinchReleaseTimer = 0f;
            _flickTime = 0f;
            _isMouseDraggingAim = false;
            _history.Clear();

            ResetPostSessionGestureState();
            _gameOverCooldownUntil = Time.time + 1.5f;

            if (_heldDartPreview != null && _heldDartPreview.activeSelf)
            {
                _heldDartPreview.SetActive(false);
            }
        }

        private void Start()
        {
            AutoResolveHandReferences();
            CreateLargeAimVisuals();
        }

        public void AutoResolveHandReferences()
        {
            if (trackingSpace == null)
            {
                var tsObj = GameObject.Find("TrackingSpace");
                if (tsObj != null) trackingSpace = tsObj.transform;
            }

            if (centerEyeAnchor == null)
            {
                var eyeObj = GameObject.Find("CenterEyeAnchor");
                if (eyeObj != null) centerEyeAnchor = eyeObj.transform;
            }

            if (rightHandAnchor == null)
            {
                var rhObj = GameObject.Find("RightHandAnchor");
                if (rhObj != null) rightHandAnchor = rhObj.transform;
            }

            if (leftHandAnchor == null)
            {
                var lhObj = GameObject.Find("LeftHandAnchor");
                if (lhObj != null) leftHandAnchor = lhObj.transform;
            }

            if (rightOvrHand == null || leftOvrHand == null)
            {
                var hands = FindObjectsByType<OVRHand>();
                foreach (var h in hands)
                {
                    string n = h.gameObject.name.ToLowerInvariant();
                    if (n.Contains("right") && rightOvrHand == null) rightOvrHand = h;
                    else if (n.Contains("left") && leftOvrHand == null) leftOvrHand = h;
                }
                if (rightOvrHand == null && hands.Length > 0)
                {
                    rightOvrHand = hands[0];
                }
            }
        }

        private void Update()
        {
            if (centerEyeAnchor == null || trackingSpace == null)
            {
                AutoResolveHandReferences();
            }

            float dt = Mathf.Max(Time.deltaTime, 0.001f);

            // Safety unlock if a dart flight was interrupted by round end
            if (_isAimStaticUntilScoreRecorded && !_hasPendingThrow)
            {
                _aimLockSafetyTimer += dt;
                if (_aimLockSafetyTimer >= MaxAimLockDurationSeconds)
                {
                    UnlockAimpointAfterScoreRecorded();
                }
            }

            bool isTracked = TryGetTrackedHandPose(
                out Vector3 rawHandPos,
                out Quaternion rawHandRot,
                out Vector3 rawAimDir,
                out float currentPinchStrength,
                out OVRPlugin.Hand detectedHand);

            TryPollSimulatorMouse(out Vector2 mouseScreenPos, out bool leftMouseHeld, out bool leftMouseReleased);
            bool simClickDragMode = enableSimulatorMouseInput && IsSimulatorWebcamProfile;

            if (!isTracked)
            {
                // Hand disappeared from camera lens: cancel any pending throw immediately so it NEVER auto-shoots!
                _consecutiveTrackedFrames = 0;
                _activeTrackedHand = OVRPlugin.Hand.None;
                if (!leftMouseHeld)
                {
                    _wasPinching = false;
                }
                _pinchHoldTime = 0f;
                _pinchReleaseTimer = 0f;
                _flickTime = 0f;
                _history.Clear();

                if (_hasPendingThrow)
                {
                    _hasPendingThrow = false;
                    _pendingThrowTimer = 0f;
                    _isAimStaticUntilScoreRecorded = false;
                }

                if (leftMouseHeld)
                {
                    _wasPinching = true;
                    TryUpdateAimFromMouse(mouseScreenPos, false);
                }
                else if (leftMouseReleased)
                {
                    _wasPinching = false;
                    TryUpdateAimFromMouse(mouseScreenPos, false);
                    if (GameSessionManager.Instance != null && !GameSessionManager.Instance.IsRoundActive)
                    {
                        ProcessPostSessionThrowToStart(Vector3.zero, 0f, 0f, dt, false, true);
                    }
                    else
                    {
                        TryTriggerMouseClickThrow();
                    }
                }
                else
                {
                    _wasPinching = false;
                    if (_heldDartPreview != null && _heldDartPreview.activeSelf)
                    {
                        _heldDartPreview.SetActive(false);
                    }

                    ProcessPostSessionThrowToStart(Vector3.zero, 0f, 0f, dt, false, false);
                }

                UpdateAimVisualsOnBoard();
                return;
            }

            bool handednessChanged = (_activeTrackedHand != OVRPlugin.Hand.None && _activeTrackedHand != detectedHand);
            _activeTrackedHand = detectedHand;

            // Profile-aware single-frame jump threshold:
            // - Simulator 2D Webcam (20-30 FPS): 0.55m so fast throws across webcam frames are NEVER wiped out.
            // - Real Meta Quest (72-90 Hz): 0.20m to reject tracking teleports.
            float activeJumpLimit = IsSimulatorWebcamProfile ? simWebcamJumpRejectMeters : trackingJumpRejectMeters;
            bool trackingJump = _consecutiveTrackedFrames > 0 &&
                                (rawHandPos - _lastRawHandPos).sqrMagnitude > activeJumpLimit * activeJumpLimit;
            _lastRawHandPos = rawHandPos;

            if (_consecutiveTrackedFrames == 0 || handednessChanged || trackingJump)
            {
                _consecutiveTrackedFrames = 1;
                _smoothedHandPos = rawHandPos;
                _smoothedHandRot = rawHandRot;
                _smoothedAimDir = rawAimDir.normalized;
                _wasPinching = leftMouseHeld;
                _pinchHoldTime = 0f;
                _pinchReleaseTimer = 0f;
                _flickTime = 0f;
                _history.Clear();
                RecordHistorySample(rawHandPos, rawHandRot, _currentReticleWorldPos);
                if (leftMouseHeld)
                {
                    TryUpdateAimFromMouse(mouseScreenPos, true);
                }
                else
                {
                    UpdateAimPointWithSensitivity(1f);
                }
                UpdateAimVisualsOnBoard();
                if (leftMouseReleased)
                {
                    _wasPinching = false;
                    TryUpdateAimFromMouse(mouseScreenPos, true);
                    if (GameSessionManager.Instance != null && !GameSessionManager.Instance.IsRoundActive)
                    {
                        ProcessPostSessionThrowToStart(Vector3.zero, 0f, 0f, dt, true, true);
                    }
                    else
                    {
                        TryTriggerMouseClickThrow();
                    }
                }
                return;
            }

            _consecutiveTrackedFrames++;

            // Hide the held dart in the player's hand as soon as a throw is triggered or while a dart is in flight,
            // so the dart NEVER stays in the hand following hand motion after being thrown!
            if (_heldDartPreview != null)
            {
                bool shouldShowHeldDart = !_isAimStaticUntilScoreRecorded &&
                                          !_hasPendingThrow &&
                                          (GameSessionManager.Instance == null ||
                                           (GameSessionManager.Instance.IsRoundActive &&
                                            (GameSessionManager.Instance.RemainingThrows > 0 || GameSessionManager.Instance.IsPaused)));
                if (_heldDartPreview.activeSelf != shouldShowHeldDart)
                {
                    _heldDartPreview.SetActive(shouldShowHeldDart);
                }
            }

            // Compute smooth 90ms sliding-window linear velocity (pure forward-motion based, no pitch/wrist-whip trigger)
            Vector3 windowVelocity = ComputeSlidingWindowVelocity(rawHandPos, rawHandRot, 0.09f, out _);
            SplitThrowVelocity(windowVelocity, rawHandPos, out float forwardSpeed, out float lateralSpeed);
            float totalSpeed = windowVelocity.magnitude;

            // Track instantaneous frame step along the hand-to-board axis BEFORE updating _smoothedHandPos
            float smoothing = Mathf.Lerp(aimSmoothingMin, aimSmoothingMax, Mathf.InverseLerp(0.05f, 0.6f, totalSpeed));
            float alpha = 1f - Mathf.Exp(-smoothing * dt);
            Vector3 nextSmoothedHandPos = Vector3.Lerp(_smoothedHandPos, rawHandPos, alpha);
            SplitThrowVelocity((nextSmoothedHandPos - _smoothedHandPos) / dt, nextSmoothedHandPos, out float frameForwardSpeed, out _);

            bool holdingSimClickOrPinch = leftMouseHeld ||
                                          (simClickDragMode && (_wasPinching ? currentPinchStrength >= pinchReleaseThreshold : currentPinchStrength >= pinchGrabThreshold));

            // BACKWARD RETRACTION LOCKOUT:
            // Whenever the hand moves backward away from the board (even while a dart is in flight!),
            // lock out throw detection for backwardRetractLockoutSeconds (0.32s) so pulling the hand back
            // or stopping a backward pull NEVER triggers a throw!
            if (forwardSpeed < -0.05f || frameForwardSpeed < -0.08f)
            {
                _backwardRetractLockoutTimer = Mathf.Max(_backwardRetractLockoutTimer, backwardRetractLockoutSeconds);
                _flickTime = 0f;
                if (!holdingSimClickOrPinch)
                {
                    _wasPinching = false;
                    _pinchHoldTime = 0f;
                    _pinchReleaseTimer = 0f;
                }
            }
            else if (_backwardRetractLockoutTimer > 0f)
            {
                _backwardRetractLockoutTimer = Mathf.Max(0f, _backwardRetractLockoutTimer - dt);
            }

            // Stabilize aimpoint ONLY when the player begins a true forward push (never when moving backward)
            float preThrowFreezeForward = IsSimulatorWebcamProfile ? 0.16f : 0.38f;
            bool startingThrowMotion = !holdingSimClickOrPinch &&
                                       (_backwardRetractLockoutTimer <= 0f) &&
                                       (forwardSpeed > preThrowFreezeForward && frameForwardSpeed > 0f && totalSpeed > 0.22f);

            float dynamicSensitivityScale = _isAimStaticUntilScoreRecorded
                ? 0f
                : (holdingSimClickOrPinch ? 1f : (startingThrowMotion ? 0.04f : 1f));

            _smoothedHandPos = nextSmoothedHandPos;
            _smoothedHandRot = Quaternion.Slerp(_smoothedHandRot, rawHandRot, alpha);
            _smoothedAimDir = Vector3.Slerp(_smoothedAimDir, rawAimDir.normalized, alpha).normalized;

            if (leftMouseHeld || leftMouseReleased)
            {
                TryUpdateAimFromMouse(mouseScreenPos, true);
            }
            else
            {
                UpdateAimPointWithSensitivity(dynamicSensitivityScale);
            }
            RecordHistorySample(_smoothedHandPos, _smoothedHandRot, _currentReticleWorldPos);

            if (leftMouseHeld)
            {
                _wasPinching = true;
                UpdateAimVisualsOnBoard();
                return;
            }

            if (leftMouseReleased)
            {
                _wasPinching = false;
                UpdateAimVisualsOnBoard();
                if (GameSessionManager.Instance != null && !GameSessionManager.Instance.IsRoundActive)
                {
                    ProcessPostSessionThrowToStart(windowVelocity, forwardSpeed, frameForwardSpeed, dt, true, true, currentPinchStrength);
                }
                else
                {
                    TryTriggerMouseClickThrow();
                }
                return;
            }

            UpdateAimVisualsOnBoard();

            // Wait for warm-up frames after hand first enters camera lens
            if (_consecutiveTrackedFrames < WarmupFramesAfterTrackingRegained)
            {
                _wasPinching = false;
                _pinchHoldTime = 0f;
                return;
            }

            // If a throw was triggered, wait 70ms ONLY to confirm the hand didn't vanish from the camera lens.
            // Do NOT cancel on follow-through motion so real throws ALWAYS fire!
            if (_hasPendingThrow)
            {
                _pendingThrowTimer -= dt;
                if (_pendingThrowTimer <= 0f)
                {
                    _hasPendingThrow = false;
                    _pendingThrowTimer = 0f;
                    _lastThrowTime = Time.time;
                    ThrowDartFromHand();
                }
                return;
            }

            // While the thrown dart is flying to the board, keep aimpoint static and wait for score to be recorded
            if (_isAimStaticUntilScoreRecorded)
            {
                return;
            }

            // Between sessions, wait after cooldown + neutral rest for an explicit forward throw motion (no pinch/pitch)
            if (GameSessionManager.Instance != null && !GameSessionManager.Instance.IsRoundActive)
            {
                ProcessPostSessionThrowToStart(windowVelocity, forwardSpeed, frameForwardSpeed, dt, true, false, currentPinchStrength);
                return;
            }

            ProcessTrackedHandGestures(windowVelocity, frameForwardSpeed, currentPinchStrength, dt);
        }

        private void ResetPostSessionGestureState()
        {
            _postSessionSawNeutral = false;
            _postSessionNeutralTimer = 0f;
            _postSessionThrowTimer = 0f;
        }

        private void ProcessPostSessionThrowToStart(Vector3 windowVelocity, float forwardSpeed, float frameForwardSpeed, float dt, bool isHandTracked, bool mouseClickStart = false, float pinchStrength = 0f)
        {
            var session = GameSessionManager.Instance;
            if (session == null || session.IsRoundActive || session.IsCountingDown)
            {
                ResetPostSessionGestureState();
                return;
            }

            // Enforce a 1.5s input cooldown upon entering Game Over so trailing throw motion never triggers auto-restart
            if (Time.time - session.RoundEndRealTime < 1.5f || Time.time < _gameOverCooldownUntil)
            {
                ResetPostSessionGestureState();
                _wasPinching = false;
                _pinchHoldTime = 0f;
                return;
            }

            bool keyboardStart = false;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame))
            {
                keyboardStart = true;
            }
#endif

            if (!mouseClickStart && isHandTracked && enableSimulatorMouseInput && IsSimulatorWebcamProfile)
            {
                bool isSimPinchingNow = _wasPinching
                    ? (pinchStrength >= pinchReleaseThreshold)
                    : (pinchStrength >= pinchGrabThreshold);
                if (isSimPinchingNow)
                {
                    _pinchHoldTime += dt;
                    _wasPinching = true;
                }
                else if (_wasPinching)
                {
                    _wasPinching = false;
                    bool releasedAfterPinch = _pinchHoldTime >= 0.03f;
                    _pinchHoldTime = 0f;
                    if (releasedAfterPinch)
                    {
                        mouseClickStart = true;
                    }
                }
            }

            if (keyboardStart || mouseClickStart)
            {
                ResetPostSessionGestureState();
                UnlockAimpointAfterScoreRecorded();
                session.StartNewRound();
                return;
            }

            if (!isHandTracked)
            {
                _postSessionNeutralTimer = 0f;
                _postSessionThrowTimer = 0f;
                return;
            }

            // Require the hand to rest in a neutral non-retracting state for at least 0.12s after cooldown
            if (!_postSessionSawNeutral)
            {
                _postSessionThrowTimer = 0f;
                bool isNeutralNow = (Mathf.Abs(forwardSpeed) < 0.12f) && (_backwardRetractLockoutTimer <= 0f);
                _postSessionNeutralTimer = isNeutralNow ? _postSessionNeutralTimer + dt : 0f;
                if (_postSessionNeutralTimer >= 0.12f)
                {
                    _postSessionSawNeutral = true;
                }
                return;
            }

            // Detect ONLY pure forward throw motion (never pinch or pitch alone)
            bool simWebcam = IsSimulatorWebcamProfile;
            float activeThreshold = simWebcam ? simWebcamForwardSpeed : flickForwardSpeed;
            float activeFrameFloor = simWebcam ? 0.04f : 0.10f;
            float activeLateralFactor = simWebcam ? 0.78f : 0.95f;
            float activeMinVy = simWebcam ? -0.42f : -0.60f;
            float activeSustain = simWebcam ? simWebcamSustainSeconds : flickSustainSeconds;

            SplitThrowVelocity(windowVelocity, _smoothedHandPos, out float forward, out float lateral);
            bool throwForwardNow = (_backwardRetractLockoutTimer <= 0f) &&
                                   (simWebcam || _handConfidenceHigh) &&
                                   IsHandInsideCameraSafeZone(_smoothedHandPos) &&
                                   (forward >= activeThreshold) &&
                                   (frameForwardSpeed > activeFrameFloor) &&
                                   (forward >= (simWebcam ? Mathf.Abs(windowVelocity.x) : lateral) * activeLateralFactor) &&
                                   (windowVelocity.y > activeMinVy);
            _postSessionThrowTimer = throwForwardNow ? _postSessionThrowTimer + dt : 0f;

            if (_postSessionThrowTimer >= activeSustain)
            {
                ResetPostSessionGestureState();
                UnlockAimpointAfterScoreRecorded();
                session.StartNewRound();
            }
        }

        /// <summary>
        /// Splits hand velocity into the component towards the dartboard and the sideways/vertical remainder.
        /// </summary>
        private void SplitThrowVelocity(Vector3 velocity, Vector3 handPos, out float forward, out float lateral)
        {
            Vector3 axis = Vector3.forward;
            if (RhythmDartboard.Instance != null)
            {
                Vector3 toBoard = RhythmDartboard.Instance.transform.position - handPos;
                if (toBoard.sqrMagnitude > 0.01f) axis = toBoard.normalized;
            }
            forward = Vector3.Dot(velocity, axis);
            lateral = (velocity - axis * forward).magnitude;
        }

        private void RecordHistorySample(Vector3 handPos, Quaternion handRot, Vector3 aimWorldPos)
        {
            float now = Time.time;
            _history.Add(new HandHistorySample
            {
                time = now,
                handPos = handPos,
                handRot = handRot,
                aimWorldPos = aimWorldPos
            });

            // Prune samples older than 0.45s
            while (_history.Count > 2 && (now - _history[0].time) > 0.45f)
            {
                _history.RemoveAt(0);
            }
        }

        private Vector3 ComputeSlidingWindowVelocity(
            Vector3 currentHandPos,
            Quaternion currentHandRot,
            float windowSeconds,
            out float angularSpeedDegPerSec)
        {
            angularSpeedDegPerSec = 0f;
            if (_history.Count == 0) return Vector3.zero;

            float now = Time.time;
            int bestIdx = 0;
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                bestIdx = i;
                if ((now - _history[i].time) >= windowSeconds)
                {
                    break;
                }
            }

            float elapsed = Mathf.Max(now - _history[bestIdx].time, 0.016f);
            float angleDeg = Quaternion.Angle(_history[bestIdx].handRot, currentHandRot);
            angularSpeedDegPerSec = angleDeg / elapsed;
            return (currentHandPos - _history[bestIdx].handPos) / elapsed;
        }

        /// <summary>
        /// Retrieves the steady aimpoint from ~220ms ago (right BEFORE the hand started the physical throw motion),
        /// so the throwing motion itself never pulls the aimpoint or dart off target.
        /// </summary>
        private Vector3 GetLookbackPreThrowAimPoint(float lookbackSeconds = 0.22f)
        {
            if (_history.Count == 0) return _currentReticleWorldPos;

            float targetTime = Time.time - lookbackSeconds;
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].time <= targetTime && _history[i].aimWorldPos.sqrMagnitude > 0.001f)
                {
                    return _history[i].aimWorldPos;
                }
            }

            return _history[0].aimWorldPos.sqrMagnitude > 0.001f
                ? _history[0].aimWorldPos
                : _currentReticleWorldPos;
        }

        private bool IsHandInsideCameraSafeZone(Vector3 worldHandPos)
        {
            Vector3 headPos = (centerEyeAnchor != null && centerEyeAnchor.position.sqrMagnitude > 0.001f)
                ? centerEyeAnchor.position
                : new Vector3(0f, 1.50f, 0f);
            Vector3 neutralHandCenter = headPos + new Vector3(0.0f, -0.06f, 0.36f);
            Vector3 delta = worldHandPos - neutralHandCenter;

            return Mathf.Abs(delta.x) <= 0.30f &&
                   delta.y >= -0.24f &&
                   delta.y <= 0.30f;
        }

        private bool TryGetTrackedHandPose(
            out Vector3 rawHandPos,
            out Quaternion rawHandRot,
            out Vector3 rawAimDir,
            out float pinchStrength,
            out OVRPlugin.Hand activeHand)
        {
            rawHandPos = _smoothedHandPos;
            rawHandRot = _smoothedHandRot;
            rawAimDir = _smoothedAimDir;
            pinchStrength = 0f;
            activeHand = OVRPlugin.Hand.None;

            bool gotRight = false;
            bool gotLeft = false;
            try
            {
                gotRight = OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandRight, ref _rightHandState);
                gotLeft = OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandLeft, ref _leftHandState);
            }
            catch
            {
            }

            bool rightTracked = gotRight && (_rightHandState.Status & OVRPlugin.HandStatus.HandTracked) != 0;
            bool leftTracked = gotLeft && (_leftHandState.Status & OVRPlugin.HandStatus.HandTracked) != 0;

            if (rightTracked || leftTracked)
            {
                bool useRight = rightTracked;
                OVRPlugin.HandState state = useRight ? _rightHandState : _leftHandState;
                bool high = state.HandConfidence == OVRPlugin.TrackingConfidence.High;
                if (high) _sawHighConfidence = true;
                _handConfidenceHigh = high || !_sawHighConfidence;
                activeHand = useRight ? OVRPlugin.Hand.HandRight : OVRPlugin.Hand.HandLeft;
                Transform fallbackAnchor = useRight ? rightHandAnchor : leftHandAnchor;

                Vector3 localPos = state.RootPose.Position.FromFlippedZVector3f();
                Quaternion localRot = state.RootPose.Orientation.FromFlippedZQuatf();

                if (localPos.sqrMagnitude < 0.0005f)
                {
                    Vector3 ptrLocal = state.PointerPose.Position.FromFlippedZVector3f();
                    if (ptrLocal.sqrMagnitude >= 0.0005f)
                    {
                        localPos = ptrLocal;
                        localRot = state.PointerPose.Orientation.FromFlippedZQuatf();
                    }
                }

                if (localPos.sqrMagnitude >= 0.0005f)
                {
                    rawHandPos = trackingSpace != null ? trackingSpace.TransformPoint(localPos) : localPos;
                    rawHandRot = trackingSpace != null ? (trackingSpace.rotation * localRot) : localRot;
                    rawAimDir = ComputeNaturalAimDirection(rawHandPos, rawHandRot * Vector3.forward);
                }
                else if (fallbackAnchor != null && fallbackAnchor.position.sqrMagnitude >= 0.0005f)
                {
                    rawHandPos = fallbackAnchor.position;
                    rawHandRot = fallbackAnchor.rotation;
                    rawAimDir = ComputeNaturalAimDirection(rawHandPos, fallbackAnchor.forward);
                }
                else
                {
                    return false;
                }

                if (state.PinchStrength != null && state.PinchStrength.Length > (int)OVRPlugin.HandFinger.Index)
                {
                    pinchStrength = state.PinchStrength[(int)OVRPlugin.HandFinger.Index];
                }
                if (((int)state.Pinches & (1 << (int)OVRPlugin.HandFinger.Index)) != 0)
                {
                    pinchStrength = Mathf.Max(pinchStrength, 0.85f);
                }

                pinchStrength = Mathf.Max(pinchStrength, GetControllerOrSimPinchStrength());
                return true;
            }

            bool handTrackingPluginActive = false;
            try
            {
                handTrackingPluginActive = OVRPlugin.GetHandTrackingEnabled();
            }
            catch
            {
            }

            if (handTrackingPluginActive)
            {
                return false;
            }

            // Simulator Controller Mode fallback
            if (rightHandAnchor != null && rightHandAnchor.position.sqrMagnitude > 0.001f)
            {
                _handConfidenceHigh = true;
                rawHandPos = rightHandAnchor.position;
                rawHandRot = rightHandAnchor.rotation;
                rawAimDir = ComputeNaturalAimDirection(rawHandPos, rightHandAnchor.forward);
                pinchStrength = GetControllerOrSimPinchStrength();
                activeHand = OVRPlugin.Hand.HandRight;
                return true;
            }

            return false;
        }

        private float GetControllerOrSimPinchStrength()
        {
            float strength = 0f;
            strength = Mathf.Max(strength, OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RHand));
            strength = Mathf.Max(strength, OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch));
            strength = Mathf.Max(strength, OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LHand));
            if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RHand) ||
                OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                strength = Mathf.Max(strength, 0.9f);
            }
            return strength;
        }

        private Vector3 ComputeNaturalAimDirection(Vector3 handPos, Vector3 anchorForward)
        {
            if (centerEyeAnchor != null)
            {
                Vector3 eyeRef = centerEyeAnchor.position + new Vector3(0.0f, -0.08f, 0f);
                Vector3 eyeToHand = handPos - eyeRef;
                if (eyeToHand.sqrMagnitude > 0.001f)
                {
                    Vector3 forwardDir = eyeToHand.normalized;
                    if (forwardDir.z < 0.25f)
                    {
                        forwardDir.z = 0.65f;
                        forwardDir.Normalize();
                    }
                    return Vector3.Lerp(forwardDir, anchorForward.z > 0.2f ? anchorForward : Vector3.forward, 0.25f).normalized;
                }
            }
            return anchorForward.sqrMagnitude > 0.001f ? anchorForward.normalized : Vector3.forward;
        }

        private void UpdateAimPointWithSensitivity(float dynamicSensitivityScale)
        {
            if (RhythmDartboard.Instance == null) return;

            // Keep aimpoint 100% STATIC from the moment a throw is triggered until the score is recorded!
            if (_isAimStaticUntilScoreRecorded || _hasPendingThrow)
            {
                _currentReticleWorldPos = _lockedAimWorldPos;
                return;
            }

            Transform boardTf = RhythmDartboard.Instance.transform;
            Plane boardPlane = new Plane(-boardTf.forward, boardTf.position);
            Ray aimRay = new Ray(_smoothedHandPos, _smoothedAimDir);

            Vector3 rawHitPoint = boardTf.position;
            if (boardPlane.Raycast(aimRay, out float enterDist) && enterDist > 0f)
            {
                rawHitPoint = aimRay.GetPoint(enterDist);
            }

            Vector3 headPos = (centerEyeAnchor != null && centerEyeAnchor.position.sqrMagnitude > 0.001f)
                ? centerEyeAnchor.position
                : new Vector3(0f, 1.50f, 0f);
            Vector3 neutralHandCenter = headPos + new Vector3(0.0f, -0.06f, 0.36f);
            Vector3 handDeltaFromNeutral = _smoothedHandPos - neutralHandCenter;

            Vector3 localRayHit = boardTf.InverseTransformPoint(rawHitPoint);
            Vector2 rayContribution = new Vector2(localRayHit.x, localRayHit.y) * rayAimWeight;
            Vector2 handContribution = new Vector2(handDeltaFromNeutral.x, handDeltaFromNeutral.y) * handAimGain;

            // Apply overall aim sensitivity
            Vector2 sensitiveXY = (handContribution + rayContribution) * aimSensitivity;
            bool choosingBuff = BuffSystem.Instance != null && BuffSystem.Instance.IsChoosing;

            Vector2 clampedXY = choosingBuff
                ? new Vector2(
                    Mathf.Clamp(sensitiveXY.x, -1.20f, 1.20f),
                    Mathf.Clamp(sensitiveXY.y, -0.80f, 0.80f))
                : Vector2.ClampMagnitude(
                    sensitiveXY,
                    RhythmDartboard.Instance.boardRadius * 0.96f);

            Vector3 clampedWorld = boardTf.TransformPoint(new Vector3(clampedXY.x, clampedXY.y, choosingBuff ? -0.060f : 0f));
            Vector3 targetReticlePos = choosingBuff
                ? clampedWorld
                : RhythmDartboard.Instance.ApplyAimAssist(clampedWorld, aimAssistRadius, aimAssistStrength);

            if (_currentReticleWorldPos.sqrMagnitude < 0.001f)
            {
                _currentReticleWorldPos = targetReticlePos;
            }
            else if (dynamicSensitivityScale < 0.99f)
            {
                // During the start of a throw, anchor to the pre-throw aimpoint (shorter lookback during card selection)
                Vector3 preThrowAnchor = GetLookbackPreThrowAimPoint(choosingBuff ? 0.10f : 0.22f);
                _currentReticleWorldPos = Vector3.Lerp(preThrowAnchor, targetReticlePos, dynamicSensitivityScale);
            }
            else
            {
                _currentReticleWorldPos = targetReticlePos;
            }

            if (_heldDartPreview != null && _heldDartPreview.activeSelf)
            {
                Vector3 toTarget = _currentReticleWorldPos - _smoothedHandPos;
                if (toTarget.sqrMagnitude > 0.001f)
                {
                    _heldDartPreview.transform.position = _smoothedHandPos + toTarget.normalized * 0.12f;
                    _heldDartPreview.transform.rotation = Quaternion.LookRotation(toTarget.normalized);
                }
            }
        }

        private void ProcessTrackedHandGestures(
            Vector3 windowVelocity,
            float frameForwardSpeed,
            float pinchStrength,
            float dt)
        {
            bool simWebcam = IsSimulatorWebcamProfile;

            // Simulator Point-and-Click / Trigger Hold-to-Drag Aim & Unclick-to-Release Dart
            if (enableSimulatorMouseInput && simWebcam)
            {
                bool isSimPinchingNow = _wasPinching
                    ? (pinchStrength >= pinchReleaseThreshold)
                    : (pinchStrength >= pinchGrabThreshold);

                if (isSimPinchingNow)
                {
                    _pinchHoldTime += dt;
                    _pinchReleaseTimer = 0f;
                    _wasPinching = true;
                    _flickTime = 0f;
                    return;
                }
                else if (_wasPinching)
                {
                    _wasPinching = false;
                    bool releasedAfterDrag = _pinchHoldTime >= 0.03f;
                    _pinchHoldTime = 0f;
                    _pinchReleaseTimer = 0f;
                    _flickTime = 0f;
                    if (releasedAfterDrag)
                    {
                        TryTriggerMouseClickThrow();
                        return;
                    }
                }
            }

            // Block all throw detection while the hand is outside the safe zone, low confidence on Quest,
            // OR within the backward-retraction lockout window after pulling the hand back!
            if ((!simWebcam && !_handConfidenceHigh) ||
                !IsHandInsideCameraSafeZone(_smoothedHandPos) ||
                _backwardRetractLockoutTimer > 0f)
            {
                _wasPinching = false;
                _pinchHoldTime = 0f;
                _pinchReleaseTimer = 0f;
                _flickTime = 0f;
                return;
            }

            SplitThrowVelocity(windowVelocity, _smoothedHandPos, out float forward, out float lateral);
            float absVx = Mathf.Abs(windowVelocity.x);
            float vy = windowVelocity.y;

            // 1. Optional Pinch-and-release throw (disabled by default so uncurling fingers when pulling back never fires)
            bool validPinchReleased = false;
            if (allowPinchReleaseThrow)
            {
                bool isPinchingNow = _wasPinching
                    ? (pinchStrength >= pinchReleaseThreshold)
                    : (pinchStrength >= pinchGrabThreshold);

                float activeMinPinchHold = simWebcam ? 0.035f : minPinchHoldSeconds;
                float activeReleaseDebounce = simWebcam ? 0.020f : pinchReleaseDebounceSeconds;

                if (isPinchingNow && forward >= -0.02f)
                {
                    _pinchHoldTime += dt;
                    _pinchReleaseTimer = 0f;
                    _wasPinching = true;
                }
                else if (_wasPinching)
                {
                    _pinchReleaseTimer += dt;
                    if (_pinchReleaseTimer >= activeReleaseDebounce)
                    {
                        validPinchReleased = (_pinchHoldTime >= activeMinPinchHold) && (forward >= 0.05f) && (frameForwardSpeed >= 0f);
                        _wasPinching = false;
                        _pinchHoldTime = 0f;
                        _pinchReleaseTimer = 0f;
                    }
                }
            }
            else
            {
                _wasPinching = false;
                _pinchHoldTime = 0f;
                _pinchReleaseTimer = 0f;
            }

            // 2. Pure Forward Motion Throw Detection (NO pitch / wrist-angle trigger)
            bool validThrowMotion = false;
            if (allowMotionThrow)
            {
                if (simWebcam)
                {
                    // Simulator 2D Webcam Profile:
                    // Strictly requires true forward velocity (>= 0.24 m/s) over the 90ms window AND positive
                    // instantaneous forward movement on the current frame (frameForwardSpeed > 0.04 m/s),
                    // sustained across consecutive frames so backward pulls or palm rotations NEVER trigger a throw.
                    bool webcamForwardNow = (forward >= simWebcamForwardSpeed) &&
                                            (frameForwardSpeed > 0.04f) &&
                                            (forward >= absVx * 0.78f) &&
                                            (vy > -0.42f);
                    _flickTime = webcamForwardNow ? _flickTime + dt : 0f;
                    validThrowMotion = _flickTime >= simWebcamSustainSeconds;
                }
                else
                {
                    // Real Meta Quest Device Profile (Pure 3D Stereo Forward Push):
                    bool questFlickNow = (forward >= flickForwardSpeed) &&
                                         (frameForwardSpeed > 0.10f) &&
                                         (forward >= lateral * 0.95f) &&
                                         (vy > -0.60f);
                    _flickTime = questFlickNow ? _flickTime + dt : 0f;
                    validThrowMotion = _flickTime >= flickSustainSeconds;
                }
            }

            if ((validPinchReleased || validThrowMotion) && (Time.time - _lastThrowTime >= throwCooldownSeconds))
            {
                _wasPinching = false;
                _pinchHoldTime = 0f;
                _pinchReleaseTimer = 0f;
                _flickTime = 0f;

                // Lock the aimpoint to where the player was aiming right BEFORE the throw motion!
                bool choosingBuffNow = BuffSystem.Instance != null && BuffSystem.Instance.IsChoosing;
                float lookbackSec = choosingBuffNow ? 0.10f : (validPinchReleased ? 0.14f : 0.22f);
                _lockedAimWorldPos = GetLookbackPreThrowAimPoint(lookbackSec);
                _lockedLaunchHandPos = _smoothedHandPos;
                _currentReticleWorldPos = _lockedAimWorldPos;
                _isAimStaticUntilScoreRecorded = true;
                _aimLockSafetyTimer = 0f;

                // Immediately hide the held dart in the hand so it NEVER follows hand motion after throwing!
                if (_heldDartPreview != null && _heldDartPreview.activeSelf)
                {
                    _heldDartPreview.SetActive(false);
                }

                // Wait 70ms ONLY to confirm the hand is still tracked in the camera lens (!isTracked cancels it)
                _hasPendingThrow = true;
                _pendingThrowTimer = validPinchReleased ? 0.025f : StillInCameraConfirmationSeconds;
            }
        }

        public void ThrowDartFromHand()
        {
            if (GameSessionManager.Instance != null)
            {
                if (!GameSessionManager.Instance.IsRoundActive)
                {
                    UnlockAimpointAfterScoreRecorded();
                    return;
                }

                if (!GameSessionManager.Instance.TryConsumeThrowOpportunity())
                {
                    UnlockAimpointAfterScoreRecorded();
                    return;
                }
            }

            if (_heldDartPreview != null && _heldDartPreview.activeSelf)
            {
                _heldDartPreview.SetActive(false);
            }

            GameObject dartObj = DartFlight.CreateTraditionalDartVisual();
            DartFlight flight = dartObj.AddComponent<DartFlight>();

            // Launch from the locked release hand position straight to the locked pre-throw aimpoint!
            Vector3 launchStartPos = _lockedLaunchHandPos.sqrMagnitude > 0.001f ? _lockedLaunchHandPos : _smoothedHandPos;
            Vector3 targetPoint = _isAimStaticUntilScoreRecorded ? _lockedAimWorldPos : _currentReticleWorldPos;
            flight.Launch(launchStartPos, targetPoint, dartFlightBeats, OnDartReachedBoard);
        }

        private void OnDartReachedBoard(Vector3 hitPos, double impactDspTime, GameObject dartObj)
        {
            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.RegisterStuckDart(dartObj);
            }

            if (ScoreAndComboManager.Instance != null)
            {
                ScoreAndComboManager.Instance.EvaluateDartImpact(hitPos, impactDspTime);
            }

            // Restore normal aimpoint sensitivity and re-enable the held dart right after score is recorded!
            UnlockAimpointAfterScoreRecorded();
        }

        private void UnlockAimpointAfterScoreRecorded()
        {
            _isAimStaticUntilScoreRecorded = false;
            _hasPendingThrow = false;
            _aimLockSafetyTimer = 0f;
            _flickTime = 0f;
            _wasPinching = false;
            _pinchHoldTime = 0f;
            _pinchReleaseTimer = 0f;
            _isMouseDraggingAim = false;
            _history.Clear();
            RecordHistorySample(_smoothedHandPos, _smoothedHandRot, _currentReticleWorldPos);
            _backwardRetractLockoutTimer = Mathf.Max(_backwardRetractLockoutTimer, 0.22f);
        }

        private void CreateLargeAimVisuals()
        {
            foreach (string oldChild in new[] { "HandAimRay", "HandAimReticleRing", "HeldHandDartPreview", "AimDot", "AimOuterRing", "AimOutlineRing" })
            {
                var found = transform.Find(oldChild);
                if (found != null) Destroy(found.gameObject);
            }

            // 1. Crisp high-contrast Aim Center Disc on the dartboard (2.4 cm diameter, HDR emissive)
            _aimDotObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _aimDotObj.name = "AimDot";
            Destroy(_aimDotObj.GetComponent<Collider>());
            _aimDotObj.transform.SetParent(transform, true);
            _aimDotObj.transform.localScale = new Vector3(0.024f, 0.024f, 0.004f);
            _aimDotMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(1f, 0.94f, 0.25f), null, 2.5f);
            var dotRend = _aimDotObj.GetComponent<Renderer>();
            dotRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dotRend.receiveShadows = false;
            dotRend.sharedMaterial = _aimDotMat;

            // 2. High-Contrast Outer Aim Crosshair Ring (8.4 cm diameter, HDR emissive + dark contrast backing)
            const int segs = 48;
            const float rad = 0.042f;
            _aimOuterRing = RhythmDartMR.VFX.VfxFactory.CreateRing(
                transform, "AimOuterRing", rad, 0.0085f, new Color(0.0f, 0.94f, 1.0f, 1f),
                segs, 0f, RhythmDartMR.VFX.VfxResources.HdrRingMaterial);
            RhythmDartMR.VFX.VfxFactory.SetHdrColor(_aimOuterRing, new Color(0.0f, 0.94f, 1.0f, 1f), 2.5f);

            _aimOutlineRing = RhythmDartMR.VFX.VfxFactory.CreateRing(
                _aimOuterRing.transform, "AimOutlineRing", rad, 0.0155f, RhythmDartMR.VFX.VfxResources.DarkOutline,
                segs, 0.001f, RhythmDartMR.VFX.VfxResources.OutlineRingMaterial);

            // 4 crisp crosshair tick marks around the reticle ring
            _aimTicks = new LineRenderer[4];
            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI * 0.5f;
                Vector3 dir = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                var tickGo = new GameObject($"AimTick{i}");
                tickGo.transform.SetParent(_aimOuterRing.transform, false);
                var lr = tickGo.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.loop = false;
                lr.positionCount = 2;
                lr.SetPosition(0, dir * 0.024f);
                lr.SetPosition(1, dir * 0.048f);
                lr.widthMultiplier = 0.006f;
                lr.alignment = LineAlignment.TransformZ;
                lr.textureMode = LineTextureMode.Stretch;
                lr.sharedMaterial = RhythmDartMR.VFX.VfxResources.HdrRingMaterial;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                RhythmDartMR.VFX.VfxFactory.SetHdrColor(lr, new Color(0.0f, 0.94f, 1.0f, 1f), 2.5f);
                _aimTicks[i] = lr;
            }

            // 3. Traditional 3D Dart held in the player's hand
            _heldDartPreview = DartFlight.CreateTraditionalDartVisual();
            _heldDartPreview.name = "HeldHandDartPreview";
            _heldDartPreview.transform.SetParent(transform, true);
        }

        private void UpdateAimVisualsOnBoard()
        {
            if (RhythmDartboard.Instance == null) return;

            // Sit cleanly in front of both the dartboard/timing rings (-0.018m) or power-up selection canvas (-0.072m)
            bool choosingBuff = BuffSystem.Instance != null && BuffSystem.Instance.IsChoosing;
            Vector3 boardNormalOffset = choosingBuff
                ? Vector3.zero
                : -RhythmDartboard.Instance.transform.forward * 0.018f;
            Vector3 cardSurfaceBias = choosingBuff
                ? -RhythmDartboard.Instance.transform.forward * 0.012f
                : Vector3.zero;
            Quaternion boardRot = RhythmDartboard.Instance.transform.rotation;

            if (_aimDotObj != null)
            {
                _aimDotObj.transform.position = _currentReticleWorldPos + boardNormalOffset * 1.05f + cardSurfaceBias * 1.15f;
                _aimDotObj.transform.rotation = boardRot;
            }

            if (_aimOuterRing != null)
            {
                _aimOuterRing.transform.position = _currentReticleWorldPos + boardNormalOffset + cardSurfaceBias;
                _aimOuterRing.transform.rotation = boardRot;

                // Emerald Green = locked static during dart flight; Electric Gold = pinched/cocked; Neon Cyan = aiming
                Color ringCol;
                if (_isAimStaticUntilScoreRecorded || _hasPendingThrow)
                    ringCol = RhythmDartMR.VFX.VfxResources.NeonEmerald;
                else if (_wasPinching)
                    ringCol = RhythmDartMR.VFX.VfxResources.ElectricGold;
                else
                    ringCol = new Color(0.00f, 0.94f, 1.00f);

                RhythmDartMR.VFX.VfxFactory.SetHdrColor(_aimOuterRing, ringCol, 2.5f);
                if (_aimTicks != null)
                {
                    for (int i = 0; i < _aimTicks.Length; i++)
                    {
                        RhythmDartMR.VFX.VfxFactory.SetHdrColor(_aimTicks[i], ringCol, 2.5f);
                    }
                }
            }
        }

        private bool TryPollSimulatorMouse(out Vector2 mouseScreenPos, out bool leftMouseHeld, out bool leftMouseReleased)
        {
            mouseScreenPos = Vector2.zero;
            leftMouseHeld = false;
            leftMouseReleased = false;

            if (!enableSimulatorMouseInput || Application.platform == RuntimePlatform.Android)
            {
                _isMouseDraggingAim = false;
                return false;
            }

            bool gotMouse = false;
            bool leftDown = false;
            bool leftHeld = false;
            bool leftUp = false;
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                mouseScreenPos = mouse.position.ReadValue();
                leftDown = mouse.leftButton.wasPressedThisFrame;
                leftHeld = mouse.leftButton.isPressed;
                leftUp = mouse.leftButton.wasReleasedThisFrame;
                gotMouse = true;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!gotMouse && UnityEngine.Input.mousePresent)
            {
                mouseScreenPos = UnityEngine.Input.mousePosition;
                leftDown = UnityEngine.Input.GetMouseButtonDown(0);
                leftHeld = UnityEngine.Input.GetMouseButton(0);
                leftUp = UnityEngine.Input.GetMouseButtonUp(0);
                gotMouse = true;
            }
#endif

            if (!gotMouse)
            {
                _isMouseDraggingAim = false;
                return false;
            }

            bool insideGameView = mouseScreenPos.x >= 0f && mouseScreenPos.x <= Screen.width &&
                                  mouseScreenPos.y >= 0f && mouseScreenPos.y <= Screen.height;

            if (leftDown && insideGameView)
            {
                _isMouseDraggingAim = true;
            }

            if (_isMouseDraggingAim)
            {
                if (leftUp || !leftHeld)
                {
                    _isMouseDraggingAim = false;
                    leftMouseHeld = false;
                    leftMouseReleased = true;
                    return true;
                }

                leftMouseHeld = true;
                leftMouseReleased = false;
                return true;
            }

            return insideGameView;
        }

        private void TryUpdateAimFromMouse(Vector2 mouseScreenPos, bool isHandTracked)
        {
            if (RhythmDartboard.Instance == null) return;

            if (_isAimStaticUntilScoreRecorded || _hasPendingThrow)
            {
                _currentReticleWorldPos = _lockedAimWorldPos;
                return;
            }

            Camera cam = Camera.main;
            if (cam == null && centerEyeAnchor != null)
            {
                cam = centerEyeAnchor.GetComponent<Camera>();
            }
            if (cam == null) return;

            Transform boardTf = RhythmDartboard.Instance.transform;
            bool choosingBuff = BuffSystem.Instance != null && BuffSystem.Instance.IsChoosing;
            Vector3 planeOrigin = choosingBuff
                ? boardTf.TransformPoint(new Vector3(0f, 0f, -0.060f))
                : boardTf.position;
            Plane boardPlane = new Plane(-boardTf.forward, planeOrigin);

            // Normalize against Game View Screen dimensions and cast through Mono view so XR eye render-target
            // sizes (cam.pixelWidth/Height) never offset or compress mouse aiming.
            float vx = Mathf.Clamp01(mouseScreenPos.x / Mathf.Max(1, Screen.width));
            float vy = Mathf.Clamp01(mouseScreenPos.y / Mathf.Max(1, Screen.height));
            Ray ray = cam.ViewportPointToRay(new Vector3(vx, vy, 0f), Camera.MonoOrStereoscopicEye.Mono);
            if (boardPlane.Raycast(ray, out float enterDist) && enterDist > 0f)
            {
                Vector3 rawHit = ray.GetPoint(enterDist);
                Vector3 localHit = boardTf.InverseTransformPoint(rawHit);

                Vector2 clampedXY = choosingBuff
                    ? new Vector2(
                        Mathf.Clamp(localHit.x, -1.50f, 1.50f),
                        Mathf.Clamp(localHit.y, -0.95f, 0.95f))
                    : Vector2.ClampMagnitude(
                        new Vector2(localHit.x, localHit.y),
                        RhythmDartboard.Instance.boardRadius * 0.96f);

                Vector3 clampedWorld = boardTf.TransformPoint(new Vector3(clampedXY.x, clampedXY.y, choosingBuff ? -0.060f : 0f));
                _currentReticleWorldPos = choosingBuff
                    ? clampedWorld
                    : RhythmDartboard.Instance.ApplyAimAssist(clampedWorld, aimAssistRadius, aimAssistStrength);
            }
            else if (_currentReticleWorldPos.sqrMagnitude < 0.001f)
            {
                _currentReticleWorldPos = planeOrigin;
            }

            if (!isHandTracked)
            {
                Transform camTf = cam.transform;
                _smoothedHandPos = camTf.position + camTf.right * 0.18f - camTf.up * 0.20f + camTf.forward * 0.38f;
                _smoothedHandRot = camTf.rotation;

                if (_heldDartPreview != null)
                {
                    bool shouldShowHeldDart = !_isAimStaticUntilScoreRecorded &&
                                              !_hasPendingThrow &&
                                              (GameSessionManager.Instance == null ||
                                               (GameSessionManager.Instance.IsRoundActive &&
                                                (GameSessionManager.Instance.RemainingThrows > 0 || GameSessionManager.Instance.IsPaused)));
                    if (_heldDartPreview.activeSelf != shouldShowHeldDart)
                    {
                        _heldDartPreview.SetActive(shouldShowHeldDart);
                    }

                    if (shouldShowHeldDart)
                    {
                        Vector3 toTarget = _currentReticleWorldPos - _smoothedHandPos;
                        if (toTarget.sqrMagnitude > 0.001f)
                        {
                            _heldDartPreview.transform.position = _smoothedHandPos + toTarget.normalized * 0.12f;
                            _heldDartPreview.transform.rotation = Quaternion.LookRotation(toTarget.normalized);
                        }
                    }
                }
            }
        }

        private void TryTriggerMouseClickThrow()
        {
            if (_isAimStaticUntilScoreRecorded || _hasPendingThrow) return;
            if (Time.time - _lastThrowTime < throwCooldownSeconds) return;
            if (GameSessionManager.Instance != null && !GameSessionManager.Instance.IsRoundActive) return;

            _wasPinching = false;
            _pinchHoldTime = 0f;
            _pinchReleaseTimer = 0f;
            _flickTime = 0f;

            _lockedAimWorldPos = _currentReticleWorldPos;
            _lockedLaunchHandPos = _smoothedHandPos;
            _currentReticleWorldPos = _lockedAimWorldPos;
            _isAimStaticUntilScoreRecorded = true;
            _aimLockSafetyTimer = 0f;
            _lastThrowTime = Time.time;

            if (_heldDartPreview != null && _heldDartPreview.activeSelf)
            {
                _heldDartPreview.SetActive(false);
            }

            ThrowDartFromHand();
        }
    }
}
