using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using RhythmDartMR.Gameplay;

namespace RhythmDartMR.UI
{
    /// <summary>
    /// Pre-session "3 — 2 — 1 — GO" world-space countdown overlay.
    /// Features 2048x1600 SDF-rendered metallic sprites with trilinear mipmapping, multi-layer radial glow
    /// + expanding beat shockwave rings, procedural text fallback, and continuous buttery-smooth curves
    /// driven by clamped unscaled delta time.
    /// </summary>
    public class SessionCountdownOverlay : MonoBehaviour
    {
        public static SessionCountdownOverlay Instance { get; private set; }

        private const float Step3Duration = 0.88f;
        private const float Step2Duration = 0.88f;
        private const float Step1Duration = 0.86f;
        private const float StepGoDuration = 0.85f;

        private RectTransform _canvasRect;
        private Image _glowImage;
        private RectTransform _glowRect;
        private Image _ringImage;
        private RectTransform _ringRect;
        private Image _displayImage;
        private RectTransform _imageRect;
        private Text _fallbackText;

        private Sprite _sprite3;
        private Sprite _sprite2;
        private Sprite _sprite1;
        private Sprite _spriteGo;

        private bool _isPlaying;
        private float _elapsed;
        private bool _goCallbackInvoked;
        private Action _onGoTriggered;
        private Action _onCountdownFinished;

        public bool IsPlaying => _isPlaying;

        public static SessionCountdownOverlay EnsureInstance()
        {
            if (Instance != null) return Instance;
            var existing = FindAnyObjectByType<SessionCountdownOverlay>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }
            var go = new GameObject("SessionCountdownOverlay");
            Instance = go.AddComponent<SessionCountdownOverlay>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            LoadSprites();
            BuildCanvas();
            SetVisible(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void PlayCountdown(Action onGoTriggered, Action onCountdownFinished = null)
        {
            LoadSprites();
            if (_canvasRect == null) BuildCanvas();

            _onGoTriggered = onGoTriggered;
            _onCountdownFinished = onCountdownFinished;
            _elapsed = 0f;
            _goCallbackInvoked = false;
            _isPlaying = true;

            PositionInFrontOfBoard();
            SetVisible(true);
            UpdateAnimation(0f);
        }

        public void StopImmediate()
        {
            _isPlaying = false;
            _onGoTriggered = null;
            _onCountdownFinished = null;
            SetVisible(false);
        }

        private void Update()
        {
            if (!_isPlaying) return;

            PositionInFrontOfBoard();
            _elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            UpdateAnimation(_elapsed);
        }

        private void UpdateAnimation(float t)
        {
            float t1End = Step3Duration;
            float t2End = t1End + Step2Duration;
            float t3End = t2End + Step1Duration;
            float tGoEnd = t3End + StepGoDuration;

            if (t < t1End)
            {
                EvaluateDigitStep(_sprite3, "3", t, Step3Duration);
            }
            else if (t < t2End)
            {
                EvaluateDigitStep(_sprite2, "2", t - t1End, Step2Duration);
            }
            else if (t < t3End)
            {
                EvaluateDigitStep(_sprite1, "1", t - t2End, Step1Duration);
            }
            else if (t < tGoEnd)
            {
                float goTime = t - t3End;
                // Trigger GO callback right as GO pops into view so the 3:00 session starts on GO
                if (!_goCallbackInvoked && goTime >= 0.08f)
                {
                    _goCallbackInvoked = true;
                    _onGoTriggered?.Invoke();
                }
                EvaluateGoStep(_spriteGo, goTime);
            }
            else
            {
                if (!_goCallbackInvoked)
                {
                    _goCallbackInvoked = true;
                    _onGoTriggered?.Invoke();
                }
                _isPlaying = false;
                SetVisible(false);
                var done = _onCountdownFinished;
                _onCountdownFinished = null;
                done?.Invoke();
            }
        }

        /// <summary>
        /// Continuous buttery-smooth motion for "3", "2", "1":
        /// - 0.000s -> 0.180s: Smooth cubic ease-out pop-in (scale 0.48 -> 1.00, alpha 0 -> 1)
        /// - 0.180s -> (dur - 0.180s): Gentle continuous forward drift (1.00 -> 1.06, never static)
        /// - Final 0.180s: Smooth fade-out with slight zoom (1.06 -> 1.14, alpha 1 -> 0)
        /// Accompanied by a synced beat shockwave ring + soft cyan radial aura.
        /// </summary>
        private void EvaluateDigitStep(Sprite sprite, string fallbackLabel, float stepTime, float duration)
        {
            SetGlyphVisual(sprite, fallbackLabel);

            const float popInTime = 0.18f;
            const float fadeOutTime = 0.18f;
            float holdDuration = Mathf.Max(0.01f, duration - popInTime - fadeOutTime);
            float fadeStartTime = popInTime + holdDuration;
            float stepFrac = Mathf.Clamp01(stepTime / duration);

            float scale;
            float alpha;

            if (stepTime < popInTime)
            {
                float u = EaseOutCubic(stepTime / popInTime);
                scale = Mathf.Lerp(0.48f, 1.00f, u);
                alpha = SmoothStep(stepTime / (popInTime * 0.85f));
            }
            else if (stepTime < fadeStartTime)
            {
                float u = (stepTime - popInTime) / holdDuration;
                scale = Mathf.Lerp(1.00f, 1.06f, u);
                alpha = 1.00f;
            }
            else
            {
                float u = SmoothStep((stepTime - fadeStartTime) / fadeOutTime);
                scale = Mathf.Lerp(1.06f, 1.14f, u);
                alpha = 1.00f - u;
            }

            // Soft cyan radial glow pulsing in sync with the digit
            float glowAlpha = alpha * (0.34f + 0.12f * Mathf.Sin(stepFrac * Mathf.PI));
            float glowScale = scale * 1.18f;

            // Beat shockwave ring smoothly expanding outward from 0.75x -> 1.75x and fading out
            float ringT = EaseOutCubic(stepFrac);
            float ringScale = Mathf.Lerp(0.75f, 1.75f, ringT);
            float ringAlpha = Mathf.Pow(1f - stepFrac, 1.4f) * 0.72f * Mathf.Clamp01(stepTime / 0.06f);

            ApplyLayers(scale, alpha, glowScale, UiTheme.Accent, glowAlpha, ringScale, UiTheme.Accent, ringAlpha);
        }

        /// <summary>
        /// Continuous explosive zoom-in + shockwave animation for "GO":
        /// - 0.000s -> 0.165s: Snappy ease-out pop-in (scale 0.42 -> 1.00, alpha 0 -> 1)
        /// - 0.165s -> 0.520s: Continuous anticipation swell (scale 1.00 -> 1.08, alpha 1)
        /// - 0.520s -> 0.850s: Smooth exponential zoom toward the player (1.08 -> 2.55) while dissolving (alpha 1 -> 0)
        /// </summary>
        private void EvaluateGoStep(Sprite sprite, float stepTime)
        {
            SetGlyphVisual(sprite, "GO");

            const float popInTime = 0.165f;
            const float holdEndTime = 0.520f;
            const float zoomOutDuration = StepGoDuration - holdEndTime; // 0.330s
            float stepFrac = Mathf.Clamp01(stepTime / StepGoDuration);

            float scale;
            float alpha;

            if (stepTime < popInTime)
            {
                float u = EaseOutCubic(stepTime / popInTime);
                scale = Mathf.Lerp(0.42f, 1.00f, u);
                alpha = SmoothStep(stepTime / (popInTime * 0.8f));
            }
            else if (stepTime < holdEndTime)
            {
                float u = (stepTime - popInTime) / (holdEndTime - popInTime);
                scale = Mathf.Lerp(1.00f, 1.08f, EaseInOutQuad(u));
                alpha = 1.00f;
            }
            else
            {
                float u = Mathf.Clamp01((stepTime - holdEndTime) / zoomOutDuration);
                float zoomCurve = u * (0.28f + 0.72f * u);
                scale = Mathf.Lerp(1.08f, 2.55f, zoomCurve);
                alpha = Mathf.Pow(1.0f - SmoothStep(u), 1.15f);
            }

            float glowScale = scale * 1.25f;
            float glowAlpha = alpha * 0.46f;

            float ringT = EaseOutCubic(stepFrac);
            float ringScale = Mathf.Lerp(0.85f, 2.85f, ringT);
            float ringAlpha = Mathf.Pow(1f - stepFrac, 1.2f) * 0.85f * Mathf.Clamp01(stepTime / 0.05f);

            ApplyLayers(scale, alpha, glowScale, UiTheme.Gold, glowAlpha, ringScale, UiTheme.Gold, ringAlpha);
        }

        private void SetGlyphVisual(Sprite sprite, string fallbackLabel)
        {
            bool hasSprite = sprite != null && sprite != UiTheme.White;
            if (_displayImage != null)
            {
                if (_displayImage.sprite != sprite) _displayImage.sprite = sprite;
                if (_displayImage.enabled != hasSprite) _displayImage.enabled = hasSprite;
            }
            if (_fallbackText != null)
            {
                if (_fallbackText.enabled == hasSprite) _fallbackText.enabled = !hasSprite;
                if (!hasSprite && _fallbackText.text != fallbackLabel) _fallbackText.text = fallbackLabel;
            }
        }

        private void ApplyLayers(
            float scale, float alpha,
            float glowScale, Color glowColor, float glowAlpha,
            float ringScale, Color ringColor, float ringAlpha)
        {
            float clampedA = Mathf.Clamp01(alpha);

            if (_imageRect != null)
            {
                _imageRect.localScale = new Vector3(scale, scale, 1f);
            }
            if (_displayImage != null && _displayImage.enabled)
            {
                _displayImage.color = new Color(1f, 1f, 1f, clampedA);
            }
            if (_fallbackText != null && _fallbackText.enabled)
            {
                _fallbackText.color = new Color(UiTheme.TextPrimary.r, UiTheme.TextPrimary.g, UiTheme.TextPrimary.b, clampedA);
            }
            if (_glowRect != null && _glowImage != null)
            {
                _glowRect.localScale = new Vector3(glowScale, glowScale, 1f);
                _glowImage.color = new Color(glowColor.r, glowColor.g, glowColor.b, Mathf.Clamp01(glowAlpha));
            }
            if (_ringRect != null && _ringImage != null)
            {
                _ringRect.localScale = new Vector3(ringScale, ringScale, 1f);
                _ringImage.color = new Color(ringColor.r, ringColor.g, ringColor.b, Mathf.Clamp01(ringAlpha));
            }
        }

        private void BuildCanvas()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            _canvasRect = UiTheme.CreateWorldCanvas("CountdownCanvas", transform, new Vector2(1600f, 1400f), out Canvas canvas);
            canvas.sortingOrder = 50;
            var scaler = _canvasRect.GetComponent<CanvasScaler>();
            if (scaler != null) scaler.dynamicPixelsPerUnit = 6f;

            _glowImage = UiTheme.Panel(_canvasRect, "CountdownGlow", Vector2.zero, new Vector2(1080f, 960f), UiTheme.Glow, new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, 0f));
            _glowRect = _glowImage.rectTransform;

            _ringImage = UiTheme.Panel(_canvasRect, "CountdownRing", Vector2.zero, new Vector2(820f, 820f), UiTheme.Ring, new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, 0f));
            _ringRect = _ringImage.rectTransform;

            _imageRect = UiTheme.Node(_canvasRect, "CountdownGlyph", Vector2.zero, new Vector2(980f, 766f));
            _displayImage = _imageRect.gameObject.AddComponent<Image>();
            _displayImage.preserveAspect = true;
            _displayImage.raycastTarget = false;
            _displayImage.color = new Color(1f, 1f, 1f, 0f);

            _fallbackText = UiTheme.Label(_imageRect, "CountdownFallbackText", Vector2.zero, new Vector2(980f, 766f), "", 540, UiTheme.TextPrimary, display: true);
            _fallbackText.fontStyle = FontStyle.Bold;
            _fallbackText.enabled = false;
        }

        private void PositionInFrontOfBoard()
        {
            if (_canvasRect == null) return;

            Vector3 boardPos = RhythmDartboard.Instance != null
                ? RhythmDartboard.Instance.transform.position
                : RhythmDartboard.DefaultBoardPosition;
            Quaternion boardRot = RhythmDartboard.Instance != null
                ? RhythmDartboard.Instance.transform.rotation
                : Quaternion.identity;

            // Place slightly in front of the dartboard and match its 0.62x depth scale
            _canvasRect.position = boardPos + (boardRot * new Vector3(0f, 0.02f, -0.22f));
            _canvasRect.rotation = boardRot;
            _canvasRect.localScale = Vector3.one * 0.00075f;
        }

        private void SetVisible(bool visible)
        {
            if (_canvasRect != null && _canvasRect.gameObject.activeSelf != visible)
            {
                _canvasRect.gameObject.SetActive(visible);
            }
        }

        private void LoadSprites()
        {
            if (_sprite3 == null) _sprite3 = LoadCountdownSprite("Countdown_3");
            if (_sprite2 == null) _sprite2 = LoadCountdownSprite("Countdown_2");
            if (_sprite1 == null) _sprite1 = LoadCountdownSprite("Countdown_1");
            if (_spriteGo == null) _spriteGo = LoadCountdownSprite("Countdown_GO");
        }

        private static Sprite LoadCountdownSprite(string fileName)
        {
            string diskPath = Path.Combine(Application.dataPath, "Resources", "Countdown", fileName + ".png");
            Texture2D tex = null;

            // Prefer loading fresh PNG from disk with trilinear mipmaps for aliasing-free scaling across 0.4x -> 2.6x
            if (File.Exists(diskPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(diskPath);
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                    {
                        name = fileName,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 4
                    };
                    if (!tex.LoadImage(bytes, false))
                    {
                        Destroy(tex);
                        tex = null;
                    }
                }
                catch
                {
                    if (tex != null) Destroy(tex);
                    tex = null;
                }
            }

            if (tex == null)
            {
                string resourcePath = "Countdown/" + fileName;
                Sprite sp = Resources.Load<Sprite>(resourcePath);
                if (sp != null) return sp;
                tex = Resources.Load<Texture2D>(resourcePath);
            }

            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
                return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }

            return UiTheme.White;
        }

        private static float EaseOutCubic(float t)
        {
            t = 1f - Mathf.Clamp01(t);
            return 1f - t * t * t;
        }

        private static float EaseInOutQuad(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) * 0.5f;
        }

        private static float SmoothStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
