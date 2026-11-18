using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using RhythmDartMR.Gameplay;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// Full-view looping 16:9 Unlit MP4 background switcher attached to the "VideoBackground" 3D Quad.
    /// Defaults to playing Assets/2.mp4 in a seamless muted loop, with Assets/5.mp4 pre-linked on standby
    /// (press key '5' to switch to 5.mp4, or '2' to switch back to 2.mp4, or use the Inspector context menu).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(900)]
    public class BackgroundVideoSwitcher : MonoBehaviour
    {
        public static BackgroundVideoSwitcher Instance { get; private set; }

        [Header("Static Image Backdrop")]
        [Tooltip("When enabled, displays staticImageBackdrop (or Resources/RoomBackdropStatic) instead of looping MP4 video.")]
        public bool useStaticImageBackdrop = true;

        [Tooltip("Optional Texture2D override. If empty, loads Resources/RoomBackdropStatic automatically.")]
        public Texture2D staticImageBackdrop;

        [Header("Video Clips (2.mp4 Active Default, 5.mp4 Standby)")]
        [Tooltip("Active looping default backdrop clip (Assets/2.mp4).")]
        public VideoClip clip2;

        [Tooltip("Standby looping backdrop clip (Assets/5.mp4).")]
        public VideoClip clip5;

        [Header("Placement & 16:9 Framing")]
        [Tooltip("Offset relative to the Dartboard (3.5m behind the board along board forward).")]
        public Vector3 offsetBehindBoard = new Vector3(0f, 0f, 3.5f);

        [Tooltip("Default 16:9 scale for the VideoBackground Quad.")]
        public Vector3 backdropScale = new Vector3(6.8f, 3.825f, 1f);

        [Tooltip("Expands the 16:9 quad proportionally if the active VR camera FOV requires more coverage to avoid black borders.")]
        public bool ensureFullScreenCoverage = true;

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Material _unlitMaterial;
        private VideoPlayer _videoPlayer;
        private RenderTexture _renderTexture;
        private bool _backdropVisible = true;
        private int _activeClipSlot = 2;

        public bool IsVideoBackdropActive => isActiveAndEnabled && _backdropVisible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureInstance()
        {
            if (Instance != null) return;
            var existing = FindAnyObjectByType<BackgroundVideoSwitcher>();
            if (existing != null)
            {
                Instance = existing;
                return;
            }

            var existingObj = GameObject.Find("VideoBackground");
            if (existingObj == null)
            {
                existingObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
                existingObj.name = "VideoBackground";
            }
            Instance = existingObj.AddComponent<BackgroundVideoSwitcher>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

#if UNITY_EDITOR
            AutoAssignClipsInEditor();
#endif
            EnsureBackdropComponents();
            HideSyntheticSimulatorEnvironment();
            UpdatePlacement();
            if (useStaticImageBackdrop)
            {
                PlayStaticImage();
            }
            else
            {
                PlayClip2();
            }
        }

        private void Start()
        {
            HideSyntheticSimulatorEnvironment();
            UpdatePlacement();
            if (!useStaticImageBackdrop && _videoPlayer != null && !_videoPlayer.isPlaying)
            {
                _videoPlayer.Play();
            }
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            HideSyntheticSimulatorEnvironment();
        }

        private void Reset()
        {
            offsetBehindBoard = new Vector3(0f, 0f, 3.5f);
            backdropScale = new Vector3(6.8f, 3.825f, 1f);
            ensureFullScreenCoverage = true;
#if UNITY_EDITOR
            AutoAssignClipsInEditor();
            EnsureBackdropComponents();
            UpdatePlacement();
#endif
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            AutoAssignClipsInEditor();
#endif
            if (_meshRenderer != null)
            {
                UpdatePlacement();
            }
        }

        private void Update()
        {
            UpdatePlacement();

            // Key '2' switches back to 2.mp4 (ignore during Power-Card selection where 1/2/3 pick cards)
            bool choosingCard = BuffSystem.Instance != null && BuffSystem.Instance.IsChoosing;
            if (!choosingCard && WasDigit2PressedThisFrame())
            {
                PlayClip2();
            }
            else if (WasDigit5PressedThisFrame())
            {
                PlayClip5();
            }
        }

        private void OnDestroy()
        {
            if (_videoPlayer != null) _videoPlayer.Stop();
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                DestroyImmediateFallback(_renderTexture);
                _renderTexture = null;
            }
            if (_unlitMaterial != null) DestroyImmediateFallback(_unlitMaterial);
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ Public Switch API

        [ContextMenu("Show Static Room Image")]
        public void PlayStaticImage()
        {
            useStaticImageBackdrop = true;
            _activeClipSlot = 0;
            EnsureBackdropComponents();

            Texture2D tex = staticImageBackdrop != null
                ? staticImageBackdrop
                : Resources.Load<Texture2D>("RoomBackdropStatic");

            if (_videoPlayer != null)
            {
                _videoPlayer.playOnAwake = false;
                if (_videoPlayer.isPlaying) _videoPlayer.Stop();
            }

            if (tex != null)
            {
                AssignTextureToMaterial(tex);
            }
            else
            {
                PlayClip2();
            }
        }

        [ContextMenu("Play 2.mp4 (Active Default)")]
        public void PlayClip2()
        {
            useStaticImageBackdrop = false;
            _activeClipSlot = 2;
            PlaySlot(clip2, "2.mp4");
        }

        [ContextMenu("Play 5.mp4 (Standby)")]
        public void PlayClip5()
        {
            useStaticImageBackdrop = false;
            _activeClipSlot = 5;
            PlaySlot(clip5, "5.mp4", "5mp4.mp4");
        }

        [ContextMenu("Switch / Toggle Video (2.mp4 <-> 5.mp4)")]
        public void ToggleClip()
        {
            if (_activeClipSlot == 2) PlayClip5();
            else PlayClip2();
        }

        public void SetBackdropVisible(bool visible)
        {
            _backdropVisible = visible;
            if (_meshRenderer != null)
            {
                _meshRenderer.enabled = visible;
            }
            if (_videoPlayer != null)
            {
                if (visible && !_videoPlayer.isPlaying) _videoPlayer.Play();
                else if (!visible && _videoPlayer.isPlaying) _videoPlayer.Pause();
            }
        }

        public void HideSyntheticSimulatorEnvironment()
        {
            if (!Application.isPlaying || !_backdropVisible) return;

            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.SetSimulatorEnvironmentVisible(false);
            }

            var ptLayers = FindObjectsByType<OVRPassthroughLayer>(FindObjectsInactive.Include);
            for (int i = 0; i < ptLayers.Length; i++)
            {
                if (ptLayers[i] == null) continue;
                ptLayers[i].hidden = true;
                ptLayers[i].enabled = false;
            }
        }

        // ------------------------------------------------------------------ Internal Setup

        public void EnsureBackdropComponents()
        {
            var col = GetComponent<Collider>();
            if (col != null) DestroyImmediateFallback(col);

            _meshFilter = GetComponent<MeshFilter>();
            if (_meshFilter == null) _meshFilter = gameObject.AddComponent<MeshFilter>();
            if (_meshFilter.sharedMesh == null)
            {
                var tempQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _meshFilter.sharedMesh = tempQuad.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediateFallback(tempQuad);
            }

            _meshRenderer = GetComponent<MeshRenderer>();
            if (_meshRenderer == null) _meshRenderer = gameObject.AddComponent<MeshRenderer>();
            _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.enabled = _backdropVisible;

            if (_unlitMaterial == null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Texture");
                if (sh == null) sh = Shader.Find("Sprites/Default");

                _unlitMaterial = new Material(sh) { name = "VideoBackground_UnlitMat" };
                _unlitMaterial.color = Color.white;
                if (_unlitMaterial.HasProperty("_BaseColor")) _unlitMaterial.SetColor("_BaseColor", Color.white);
                if (_unlitMaterial.HasProperty("_Color")) _unlitMaterial.SetColor("_Color", Color.white);
                if (_unlitMaterial.HasProperty("_Surface")) _unlitMaterial.SetFloat("_Surface", 0f);
                if (_unlitMaterial.HasProperty("_Cull")) _unlitMaterial.SetFloat("_Cull", 2f);
            }
            _meshRenderer.sharedMaterial = _unlitMaterial;

            _videoPlayer = GetComponent<VideoPlayer>();
            if (_videoPlayer == null) _videoPlayer = gameObject.AddComponent<VideoPlayer>();
            _videoPlayer.playOnAwake = true;
            _videoPlayer.isLooping = true;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
            _videoPlayer.skipOnDrop = true;
            _videoPlayer.waitForFirstFrame = true;
            _videoPlayer.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;

            if (Application.isPlaying)
            {
                EnsureRenderTexture(1920, 1080);
                _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                _videoPlayer.targetTexture = _renderTexture;
                AssignTextureToMaterial(_renderTexture);
            }
            else if (clip2 != null)
            {
                _videoPlayer.source = VideoSource.VideoClip;
                _videoPlayer.clip = clip2;
            }
        }

        private void PlaySlot(VideoClip clip, params string[] fallbackAssetFileNames)
        {
            EnsureBackdropComponents();
            if (_videoPlayer == null) return;

            _videoPlayer.playOnAwake = true;
            _videoPlayer.isLooping = true;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.None;

            if (clip != null)
            {
                int w = clip.width > 0 ? (int)clip.width : 1920;
                int h = clip.height > 0 ? (int)clip.height : 1080;
                if (Application.isPlaying) EnsureRenderTexture(w, h);
                _videoPlayer.source = VideoSource.VideoClip;
                _videoPlayer.clip = clip;
                if (Application.isPlaying && _backdropVisible)
                {
                    _videoPlayer.Play();
                }
                return;
            }

            foreach (string fileName in fallbackAssetFileNames)
            {
                string fullPath = Path.Combine(Application.dataPath, fileName);
                if (File.Exists(fullPath))
                {
                    _videoPlayer.source = VideoSource.Url;
                    _videoPlayer.url = fullPath;
                    if (Application.isPlaying && _backdropVisible)
                    {
                        _videoPlayer.Play();
                    }
                    return;
                }
            }
        }

        private void EnsureRenderTexture(int width, int height)
        {
            width = Mathf.Clamp(width, 256, 3840);
            height = Mathf.Clamp(height, 256, 2160);
            if (_renderTexture != null && _renderTexture.width == width && _renderTexture.height == height)
            {
                return;
            }

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                DestroyImmediateFallback(_renderTexture);
            }

            _renderTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "VideoBackground_RT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _renderTexture.Create();
            if (_videoPlayer != null) _videoPlayer.targetTexture = _renderTexture;
            AssignTextureToMaterial(_renderTexture);
        }

        private void AssignTextureToMaterial(Texture tex)
        {
            if (_unlitMaterial == null || tex == null) return;
            _unlitMaterial.mainTexture = tex;
            if (_unlitMaterial.HasProperty("_BaseMap")) _unlitMaterial.SetTexture("_BaseMap", tex);
            if (_unlitMaterial.HasProperty("_MainTex")) _unlitMaterial.SetTexture("_MainTex", tex);
        }

        public void UpdatePlacement()
        {
            var board = RhythmDartboard.Instance != null ? RhythmDartboard.Instance : FindAnyObjectByType<RhythmDartboard>();
            Vector3 boardPos = board != null ? board.transform.position : RhythmDartboard.DefaultBoardPosition;
            Quaternion boardRot = board != null ? board.transform.rotation : Quaternion.identity;

            Vector3 worldPos = boardPos + (boardRot * offsetBehindBoard);

            float targetWidth = Mathf.Max(0.1f, backdropScale.x);
            float targetHeight = Mathf.Max(0.1f, backdropScale.y);
            float aspect = targetWidth / targetHeight;

            if (ensureFullScreenCoverage)
            {
                // Anchor 16:9 background quad to fixed world horizon and depth so board placement never shifts video framing
                worldPos.x = offsetBehindBoard.x;
                worldPos.y = 1.24f + offsetBehindBoard.y;
                worldPos.z = 5.80f;
            }

            if (Application.isPlaying && ensureFullScreenCoverage)
            {
                Camera cam = Camera.main;
                if (cam != null && !cam.orthographic)
                {
                    Vector3 boardForward = boardRot * Vector3.forward;
                    float distFromCam = Mathf.Abs(Vector3.Dot(worldPos - cam.transform.position, boardForward));
                    if (distFromCam > 0.5f)
                    {
                        float frustumHeight = 2f * distFromCam * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                        float frustumWidth = frustumHeight * Mathf.Max(1f, cam.aspect);
                        float minHeight = Mathf.Max(frustumHeight, frustumWidth / aspect) * 1.16f;
                        if (minHeight > targetHeight)
                        {
                            targetHeight = minHeight;
                            targetWidth = targetHeight * aspect;
                        }
                    }

                    // Keep vertical center smoothly aligned between fixed horizon and camera eye height
                    worldPos.y = Mathf.Lerp(1.24f, cam.transform.position.y, 0.45f) + offsetBehindBoard.y;
                }
            }

            transform.SetPositionAndRotation(worldPos, boardRot);
            transform.localScale = new Vector3(targetWidth, targetHeight, 1f);
        }

#if UNITY_EDITOR
        public void AutoAssignClipsInEditor()
        {
            if (clip2 == null)
            {
                clip2 = FindVideoClipAsset("Assets/2.mp4", "2");
            }
            if (clip5 == null)
            {
                clip5 = FindVideoClipAsset("Assets/5.mp4", "5") ?? FindVideoClipAsset("Assets/5mp4.mp4", "5mp4");
            }
        }

        private static VideoClip FindVideoClipAsset(string exactPath, string nameHint)
        {
            var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(exactPath);
            if (clip != null) return clip;

            string[] guids = AssetDatabase.FindAssets($"t:VideoClip {nameHint}");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path).Equals(nameHint, System.StringComparison.OrdinalIgnoreCase))
                {
                    return AssetDatabase.LoadAssetAtPath<VideoClip>(path);
                }
            }
            return null;
        }
#endif

        private static bool WasDigit2PressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame)) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha2) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad2)) return true;
#endif
            return false;
        }

        private static bool WasDigit5PressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && (kb.digit5Key.wasPressedThisFrame || kb.numpad5Key.wasPressedThisFrame)) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha5) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad5)) return true;
#endif
            return false;
        }

        private static void DestroyImmediateFallback(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
