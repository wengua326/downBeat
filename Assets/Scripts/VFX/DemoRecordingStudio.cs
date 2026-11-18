using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using RhythmDartMR.Gameplay;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RhythmDartMR.VFX
{
    public enum BackgroundMode
    {
        DefaultRoom = 0,
        CustomVideoOrImageBackdrop = 1,
        PureBlackForCompositing = 2
    }

    /// <summary>
    /// Non-destructive Demo Recording Mode controller for capturing 1080p Mixed Reality demo videos
    /// in the Meta XR Simulator without a physical headset:
    /// - Task 1: Switch between DefaultRoom, a custom World-Space Texture2D/Sprite/VideoClip MR backdrop
    ///           positioned 4m behind the dartboard, or PureBlackForCompositing (#000000 Alpha 0).
    /// - Task 2: Optional subtle procedural "Natural Head Breathing" sway applied during LateUpdate and
    ///           stripped right after render so hand-tracking raycasts and dart trajectories stay untouched.
    /// - Task 3: Smooth recording time-scale toggle (F9: 1.0x <-> 0.8x) and background mode cycle (F10).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class DemoRecordingStudio : MonoBehaviour
    {
        public static DemoRecordingStudio Instance { get; private set; }

        [Header("Task 1: Mixed Reality Background Switcher")]
        [Tooltip("DefaultRoom = Simulator synthetic room; CustomVideoOrImageBackdrop = real room photo/video behind dartboard; PureBlackForCompositing = solid #000000 Alpha 0.")]
        public BackgroundMode backgroundMode = BackgroundMode.DefaultRoom;

        [Tooltip("Drag your real-world room photo (Texture2D) here when using CustomVideoOrImageBackdrop.")]
        public Texture2D customRoomTexture;

        [Tooltip("Optional Sprite backdrop (used if Custom Room Video and Custom Room Texture are empty).")]
        public Sprite customRoomSprite;

        [Tooltip("Optional looping MP4/MOV VideoClip backdrop (takes priority over Texture2D/Sprite when assigned).")]
        public VideoClip customRoomVideo;

        [Tooltip("Distance in meters behind the dartboard where the custom backdrop plane is placed.")]
        [Range(1.5f, 12f)] public float backdropDistanceBehindBoard = 4.0f;

        [Tooltip("World-space width & height (meters) of the custom 16:9 backdrop plane.")]
        public Vector2 backdropSizeMeters = new Vector2(14.4f, 8.1f);

        [Tooltip("Unlit brightness multiplier applied to the custom room backdrop.")]
        [Range(0.2f, 1.5f)] public float backdropBrightness = 0.96f;

        [Tooltip("Mute audio from the custom room VideoClip so only game music & drum hits record cleanly.")]
        public bool muteBackdropVideoAudio = true;

        [Header("Task 2: Natural Head Breathing Sway")]
        [Tooltip("Adds a barely perceptible breathing/head drift during rendering without affecting hand raycasts or throws.")]
        public bool EnableNaturalHeadSway = true;

        [Tooltip("Position sway amplitude in meters (X = lateral drift, Y = vertical breathing).")]
        public Vector2 swayPositionAmplitude = new Vector2(0.003f, 0.004f);

        [Tooltip("Rotation sway amplitude in degrees (pitch & roll).")]
        [Range(0f, 1.5f)] public float swayRotationAmplitudeDegrees = 0.25f;

        [Tooltip("Slow natural breathing / drift frequency in Hz.")]
        [Range(0.05f, 2f)] public float swayFrequencyHz = 0.4f;

        [Header("Task 3: Demo Recording Shortcuts")]
        public KeyCode toggleTimeScaleKey = KeyCode.F9;
        public KeyCode cycleBackgroundModeKey = KeyCode.F10;
        [Range(0.25f, 1.0f)] public float slowRecordingTimeScale = 0.8f;

        private struct CachedCameraClearState
        {
            public CameraClearFlags clearFlags;
            public Color backgroundColor;
        }

        private readonly Dictionary<Camera, CachedCameraClearState> _originalCameraClearStates =
            new Dictionary<Camera, CachedCameraClearState>();
        private readonly List<Camera> _cachedSceneCameras = new List<Camera>();

        private Transform _cameraRigTransform;
        private Transform _swayedTransform;
        private Vector3 _savedLocalPos;
        private Quaternion _savedLocalRot;
        private Vector3 _appliedPosDelta;
        private Quaternion _appliedRotDelta = Quaternion.identity;
        private Vector3 _lastWrittenLocalPos;
        private Quaternion _lastWrittenLocalRot = Quaternion.identity;
        private bool _hasAppliedOffset;

        private GameObject _customBackdropQuad;
        private Renderer _customBackdropRenderer;
        private Material _customBackdropMat;
        private VideoPlayer _backdropVideoPlayer;
        private RenderTexture _backdropVideoRt;

        private BackgroundMode _appliedMode = (BackgroundMode)(-1);
        private Texture2D _appliedTexture;
        private Sprite _appliedSprite;
        private VideoClip _appliedVideo;
        private float _appliedBrightness = -1f;
        private float _appliedDistance = -1f;
        private Vector2 _appliedSize = Vector2.zero;
        private bool _appliedMuteVideo;
        private float _nextSceneRescanTime;

        private float _baseFixedDeltaTime = 1f / 72f;
        private bool _isSlowTimeScale;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureInstance()
        {
            if (Instance != null) return;
            var existing = FindAnyObjectByType<DemoRecordingStudio>();
            if (existing != null)
            {
                Instance = existing;
                return;
            }
            var go = new GameObject("DemoRecordingStudio");
            Instance = go.AddComponent<DemoRecordingStudio>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _baseFixedDeltaTime = Mathf.Clamp(Time.fixedDeltaTime, 0.004f, 0.05f);
        }

        private void OnEnable()
        {
            RenderPipelineManager.endContextRendering += HandleEndContextRendering;
            Camera.onPostRender += HandleCameraPostRender;
        }

        private void Start()
        {
            CacheCameraRigAndCameras();
            ApplyBackgroundMode(forceRebuild: true);
        }

        private void OnValidate()
        {
            if (!Application.isPlaying) return;
            ApplyBackgroundMode(forceRebuild: false);
        }

        private void Update()
        {
            // Defensive cleanup in case render pipeline callback didn't fire (e.g. minimized window)
            RemoveCameraSwayOffset();

            HandleShortcuts();

            // Re-apply if Inspector values changed live or if the board/cameras spawned late
            bool needsUpdate =
                _appliedMode != backgroundMode ||
                _appliedTexture != customRoomTexture ||
                _appliedSprite != customRoomSprite ||
                _appliedVideo != customRoomVideo ||
                !Mathf.Approximately(_appliedBrightness, backdropBrightness) ||
                !Mathf.Approximately(_appliedDistance, backdropDistanceBehindBoard) ||
                (_appliedSize - backdropSizeMeters).sqrMagnitude > 0.0001f ||
                _appliedMuteVideo != muteBackdropVideoAudio;

            if (needsUpdate)
            {
                ApplyBackgroundMode(forceRebuild: false);
            }
            else if (backgroundMode == BackgroundMode.CustomVideoOrImageBackdrop && _customBackdropQuad != null)
            {
                UpdateBackdropPlacement();
            }

            if (Time.unscaledTime >= _nextSceneRescanTime)
            {
                _nextSceneRescanTime = Time.unscaledTime + 1.5f;
                EnforceCameraAndPassthroughState();
            }
        }

        private void LateUpdate()
        {
            RemoveCameraSwayOffset();
            if (!EnableNaturalHeadSway) return;

            Transform rig = ResolveCameraRigTransform();
            if (rig == null) return;

            float t = Time.unscaledTime * Mathf.Max(0.01f, swayFrequencyHz);
            float breatheSine = Mathf.Sin(t * Mathf.PI * 2f);
            float lateralSine = Mathf.Sin(t * Mathf.PI * 1.37f + 0.8f);
            float perlinX = (Mathf.PerlinNoise(t * 0.85f + 11.3f, 2.7f) - 0.5f) * 2f;
            float perlinY = (Mathf.PerlinNoise(4.1f, t * 0.85f + 23.9f) - 0.5f) * 2f;
            float perlinPitch = (Mathf.PerlinNoise(t * 0.72f + 37.5f, 9.2f) - 0.5f) * 2f;
            float perlinRoll = (Mathf.PerlinNoise(14.8f, t * 0.72f + 51.1f) - 0.5f) * 2f;

            float offsetX = (0.55f * lateralSine + 0.45f * perlinX) * swayPositionAmplitude.x;
            float offsetY = (0.65f * breatheSine + 0.35f * perlinY) * swayPositionAmplitude.y;
            float pitchDeg = (0.60f * breatheSine + 0.40f * perlinPitch) * swayRotationAmplitudeDegrees;
            float rollDeg = (0.55f * lateralSine + 0.45f * perlinRoll) * swayRotationAmplitudeDegrees;

            _swayedTransform = rig;
            _savedLocalPos = rig.localPosition;
            _savedLocalRot = rig.localRotation;
            _appliedPosDelta = new Vector3(offsetX, offsetY, 0f);
            _appliedRotDelta = Quaternion.Euler(pitchDeg, 0f, rollDeg);

            rig.localPosition = _savedLocalPos + _appliedPosDelta;
            rig.localRotation = _savedLocalRot * _appliedRotDelta;
            _lastWrittenLocalPos = rig.localPosition;
            _lastWrittenLocalRot = rig.localRotation;
            _hasAppliedOffset = true;
        }

        private void OnDisable()
        {
            RenderPipelineManager.endContextRendering -= HandleEndContextRendering;
            Camera.onPostRender -= HandleCameraPostRender;
            RemoveCameraSwayOffset();
            RestoreTimeScale();
        }

        private void OnDestroy()
        {
            RemoveCameraSwayOffset();
            if (gameObject.scene.isLoaded)
            {
                RestoreOriginalSceneState();
            }

            if (_backdropVideoPlayer != null) _backdropVideoPlayer.Stop();
            if (_backdropVideoRt != null)
            {
                _backdropVideoRt.Release();
                Destroy(_backdropVideoRt);
                _backdropVideoRt = null;
            }
            if (_customBackdropMat != null) Destroy(_customBackdropMat);
            if (_customBackdropQuad != null) Destroy(_customBackdropQuad);
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ Shortcuts

        private void HandleShortcuts()
        {
            if (WasKeyPressedThisFrame(toggleTimeScaleKey))
            {
                _isSlowTimeScale = !_isSlowTimeScale;
                float targetScale = _isSlowTimeScale ? Mathf.Clamp(slowRecordingTimeScale, 0.2f, 1.0f) : 1.0f;
                Time.timeScale = targetScale;
                Time.fixedDeltaTime = _baseFixedDeltaTime * targetScale;
                Debug.Log($"[DemoRecordingStudio] Time.timeScale set to {targetScale:0.00}x (press {toggleTimeScaleKey} to toggle).");
            }

            if (WasKeyPressedThisFrame(cycleBackgroundModeKey))
            {
                int next = ((int)backgroundMode + 1) % 3;
                backgroundMode = (BackgroundMode)next;
                ApplyBackgroundMode(forceRebuild: false);
                Debug.Log($"[DemoRecordingStudio] BackgroundMode switched to {backgroundMode} (press {cycleBackgroundModeKey} to cycle).");
            }
        }

        private void RestoreTimeScale()
        {
            if (_isSlowTimeScale || !Mathf.Approximately(Time.timeScale, 1.0f))
            {
                _isSlowTimeScale = false;
                Time.timeScale = 1.0f;
                Time.fixedDeltaTime = _baseFixedDeltaTime;
            }
        }

        private static bool WasKeyPressedThisFrame(KeyCode keyCode)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (TryGetInputSystemKey(keyCode, out Key key) && kb[key].wasPressedThisFrame)
                {
                    return true;
                }
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            return UnityEngine.Input.GetKeyDown(keyCode);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static bool TryGetInputSystemKey(KeyCode keyCode, out Key key)
        {
            switch (keyCode)
            {
                case KeyCode.F1: key = Key.F1; return true;
                case KeyCode.F2: key = Key.F2; return true;
                case KeyCode.F3: key = Key.F3; return true;
                case KeyCode.F4: key = Key.F4; return true;
                case KeyCode.F5: key = Key.F5; return true;
                case KeyCode.F6: key = Key.F6; return true;
                case KeyCode.F7: key = Key.F7; return true;
                case KeyCode.F8: key = Key.F8; return true;
                case KeyCode.F9: key = Key.F9; return true;
                case KeyCode.F10: key = Key.F10; return true;
                case KeyCode.F11: key = Key.F11; return true;
                case KeyCode.F12: key = Key.F12; return true;
                default: return Enum.TryParse(keyCode.ToString(), true, out key);
            }
        }
#endif

        // ------------------------------------------------------------------ Camera Sway Isolation

        private void HandleEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            RemoveCameraSwayOffset();
        }

        private void HandleCameraPostRender(Camera cam)
        {
            RemoveCameraSwayOffset();
        }

        private void RemoveCameraSwayOffset()
        {
            if (!_hasAppliedOffset || _swayedTransform == null) return;

            if ((_swayedTransform.localPosition - _lastWrittenLocalPos).sqrMagnitude < 1e-10f)
            {
                _swayedTransform.localPosition = _savedLocalPos;
            }
            else
            {
                _swayedTransform.localPosition -= _appliedPosDelta;
            }

            if (Mathf.Abs(Quaternion.Dot(_swayedTransform.localRotation, _lastWrittenLocalRot)) > 0.999999f)
            {
                _swayedTransform.localRotation = _savedLocalRot;
            }
            else
            {
                _swayedTransform.localRotation *= Quaternion.Inverse(_appliedRotDelta);
            }

            _hasAppliedOffset = false;
        }

        private Transform ResolveCameraRigTransform()
        {
            if (_cameraRigTransform != null) return _cameraRigTransform;
            var rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null)
            {
                _cameraRigTransform = rig.transform;
                return _cameraRigTransform;
            }
            var rigObj = GameObject.Find("[BuildingBlock] Camera Rig");
            if (rigObj != null)
            {
                _cameraRigTransform = rigObj.transform;
                return _cameraRigTransform;
            }
            if (Camera.main != null)
            {
                _cameraRigTransform = Camera.main.transform.parent != null ? Camera.main.transform.parent : Camera.main.transform;
            }
            return _cameraRigTransform;
        }

        // ------------------------------------------------------------------ Background Switcher

        public void ApplyBackgroundMode(bool forceRebuild = false)
        {
            _appliedMode = backgroundMode;
            _appliedTexture = customRoomTexture;
            _appliedSprite = customRoomSprite;
            _appliedVideo = customRoomVideo;
            _appliedBrightness = backdropBrightness;
            _appliedDistance = backdropDistanceBehindBoard;
            _appliedSize = backdropSizeMeters;
            _appliedMuteVideo = muteBackdropVideoAudio;

            CacheCameraRigAndCameras();

            bool showSyntheticRoom = backgroundMode == BackgroundMode.DefaultRoom;
            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.SetSimulatorEnvironmentVisible(showSyntheticRoom);
            }

            EnforceCameraAndPassthroughState();

            if (backgroundMode == BackgroundMode.CustomVideoOrImageBackdrop)
            {
                EnsureBackdropQuad();
                UpdateBackdropPlacement();
                ApplyBackdropMedia(forceRebuild);
                _customBackdropQuad.SetActive(true);
            }
            else
            {
                if (_backdropVideoPlayer != null && _backdropVideoPlayer.isPlaying)
                {
                    _backdropVideoPlayer.Pause();
                }
                if (_customBackdropQuad != null)
                {
                    _customBackdropQuad.SetActive(false);
                }
            }
        }

        private void CacheCameraRigAndCameras()
        {
            _cachedSceneCameras.Clear();
            var cams = FindObjectsByType<Camera>(FindObjectsInactive.Include);
            for (int i = 0; i < cams.Length; i++)
            {
                Camera cam = cams[i];
                if (cam == null) continue;
                _cachedSceneCameras.Add(cam);
                if (!_originalCameraClearStates.ContainsKey(cam))
                {
                    _originalCameraClearStates[cam] = new CachedCameraClearState
                    {
                        clearFlags = cam.clearFlags,
                        backgroundColor = cam.backgroundColor
                    };
                }
            }
        }

        private void EnforceCameraAndPassthroughState()
        {
            var videoBg = BackgroundVideoSwitcher.Instance != null
                ? BackgroundVideoSwitcher.Instance
                : FindAnyObjectByType<BackgroundVideoSwitcher>();
            if (videoBg != null)
            {
                bool useVideoBg = backgroundMode == BackgroundMode.DefaultRoom;
                videoBg.SetBackdropVisible(useVideoBg);
            }

            bool hasVideoBackdrop = videoBg != null && videoBg.IsVideoBackdropActive;
            bool showSyntheticRoom = !hasVideoBackdrop && backgroundMode == BackgroundMode.DefaultRoom;
            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.SetSimulatorEnvironmentVisible(showSyntheticRoom);
            }

            // Hide Meta XR Passthrough / Simulator Synthetic Environment layers when video/custom backdrop or pure black alpha is active
            var passthroughLayers = FindObjectsByType<OVRPassthroughLayer>(FindObjectsInactive.Include);
            for (int i = 0; i < passthroughLayers.Length; i++)
            {
                var layer = passthroughLayers[i];
                if (layer == null) continue;
                layer.hidden = !showSyntheticRoom;
                layer.enabled = showSyntheticRoom;
            }

            for (int i = 0; i < _cachedSceneCameras.Count; i++)
            {
                Camera cam = _cachedSceneCameras[i];
                if (cam == null) continue;

                if (hasVideoBackdrop ||
                    backgroundMode == BackgroundMode.PureBlackForCompositing ||
                    backgroundMode == BackgroundMode.CustomVideoOrImageBackdrop)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }
                else if (_originalCameraClearStates.TryGetValue(cam, out var orig))
                {
                    cam.clearFlags = orig.clearFlags;
                    cam.backgroundColor = orig.backgroundColor;
                }
            }
        }

        private void RestoreOriginalSceneState()
        {
            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.SetSimulatorEnvironmentVisible(true);
            }

            var passthroughLayers = FindObjectsByType<OVRPassthroughLayer>(FindObjectsInactive.Include);
            for (int i = 0; i < passthroughLayers.Length; i++)
            {
                var layer = passthroughLayers[i];
                if (layer == null) continue;
                layer.hidden = false;
                layer.enabled = true;
            }

            foreach (var kvp in _originalCameraClearStates)
            {
                if (kvp.Key == null) continue;
                kvp.Key.clearFlags = kvp.Value.clearFlags;
                kvp.Key.backgroundColor = kvp.Value.backgroundColor;
            }
        }

        private void EnsureBackdropQuad()
        {
            if (_customBackdropQuad != null) return;

            _customBackdropQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _customBackdropQuad.name = "DemoMRCustomBackdrop";
            var col = _customBackdropQuad.GetComponent<Collider>();
            if (col != null) Destroy(col);

            _customBackdropRenderer = _customBackdropQuad.GetComponent<Renderer>();
            _customBackdropRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _customBackdropRenderer.receiveShadows = false;

            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Texture");
            if (sh == null) sh = Shader.Find("Sprites/Default");

            _customBackdropMat = new Material(sh);
            if (_customBackdropMat.HasProperty("_Surface")) _customBackdropMat.SetFloat("_Surface", 0f);
            _customBackdropRenderer.sharedMaterial = _customBackdropMat;
        }

        private void UpdateBackdropPlacement()
        {
            if (_customBackdropQuad == null) return;

            Transform boardTf = RhythmDartboard.Instance != null ? RhythmDartboard.Instance.transform : null;
            Vector3 boardPos = boardTf != null ? boardTf.position : RhythmDartboard.DefaultBoardPosition;
            Vector3 boardForward = boardTf != null ? boardTf.forward : Vector3.forward;
            Quaternion boardRot = boardTf != null ? boardTf.rotation : Quaternion.identity;

            // Place 4m behind the dartboard along boardForward (board faces -boardForward toward player)
            _customBackdropQuad.transform.position = boardPos + boardForward * backdropDistanceBehindBoard + new Vector3(0f, 0.30f, 0f);
            _customBackdropQuad.transform.rotation = boardRot;
            _customBackdropQuad.transform.localScale = new Vector3(backdropSizeMeters.x, backdropSizeMeters.y, 1f);
        }

        private void ApplyBackdropMedia(bool forceRebuild)
        {
            if (_customBackdropMat == null) return;

            Color tint = new Color(backdropBrightness, backdropBrightness, backdropBrightness, 1f);
            _customBackdropMat.color = tint;
            if (_customBackdropMat.HasProperty("_BaseColor")) _customBackdropMat.SetColor("_BaseColor", tint);
            if (_customBackdropMat.HasProperty("_Color")) _customBackdropMat.SetColor("_Color", tint);

            Vector2 uvScale = Vector2.one;
            Vector2 uvOffset = Vector2.zero;

            if (customRoomVideo != null)
            {
                EnsureVideoPlayerConfigured(customRoomVideo, forceRebuild);
                AssignBackdropTexture(_backdropVideoRt, uvScale, uvOffset);
                if (!_backdropVideoPlayer.isPlaying) _backdropVideoPlayer.Play();
                return;
            }

            if (_backdropVideoPlayer != null && _backdropVideoPlayer.isPlaying)
            {
                _backdropVideoPlayer.Stop();
            }

            Texture tex = customRoomTexture;
            if (tex == null && customRoomSprite != null && customRoomSprite.texture != null)
            {
                tex = customRoomSprite.texture;
                Rect r = customRoomSprite.textureRect;
                uvScale = new Vector2(r.width / tex.width, r.height / tex.height);
                uvOffset = new Vector2(r.x / tex.width, r.y / tex.height);
            }
            if (tex == null)
            {
                tex = Resources.Load<Texture2D>("RealRoomBg");
            }

            AssignBackdropTexture(tex, uvScale, uvOffset);
        }

        private void AssignBackdropTexture(Texture tex, Vector2 uvScale, Vector2 uvOffset)
        {
            if (_customBackdropMat == null) return;

            _customBackdropMat.mainTexture = tex;
            _customBackdropMat.mainTextureScale = uvScale;
            _customBackdropMat.mainTextureOffset = uvOffset;

            if (_customBackdropMat.HasProperty("_BaseMap"))
            {
                _customBackdropMat.SetTexture("_BaseMap", tex);
                _customBackdropMat.SetTextureScale("_BaseMap", uvScale);
                _customBackdropMat.SetTextureOffset("_BaseMap", uvOffset);
            }
            if (_customBackdropMat.HasProperty("_MainTex"))
            {
                _customBackdropMat.SetTexture("_MainTex", tex);
                _customBackdropMat.SetTextureScale("_MainTex", uvScale);
                _customBackdropMat.SetTextureOffset("_MainTex", uvOffset);
            }
        }

        private void EnsureVideoPlayerConfigured(VideoClip clip, bool forceRebuild)
        {
            if (_backdropVideoPlayer == null)
            {
                _backdropVideoPlayer = _customBackdropQuad.AddComponent<VideoPlayer>();
                _backdropVideoPlayer.playOnAwake = false;
            }

            int width = clip.width > 0 ? (int)clip.width : 1920;
            int height = clip.height > 0 ? (int)clip.height : 1080;
            if (forceRebuild || _backdropVideoRt == null || _backdropVideoRt.width != width || _backdropVideoRt.height != height)
            {
                if (_backdropVideoRt != null)
                {
                    _backdropVideoRt.Release();
                    Destroy(_backdropVideoRt);
                }
                _backdropVideoRt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "DemoBackdropVideoRT"
                };
                _backdropVideoRt.Create();
            }

            _backdropVideoPlayer.source = VideoSource.VideoClip;
            _backdropVideoPlayer.clip = clip;
            _backdropVideoPlayer.isLooping = true;
            _backdropVideoPlayer.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            _backdropVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
            _backdropVideoPlayer.targetTexture = _backdropVideoRt;
            _backdropVideoPlayer.audioOutputMode = muteBackdropVideoAudio
                ? VideoAudioOutputMode.None
                : VideoAudioOutputMode.Direct;
        }
    }
}
