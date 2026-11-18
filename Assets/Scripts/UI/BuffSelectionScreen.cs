using System;
using UnityEngine;
using UnityEngine.UI;
using RhythmDartMR.Gameplay;
using RhythmDartMR.VFX;

namespace RhythmDartMR.UI
{
    /// <summary>
    /// "Choose a power-up" pause screen, placed directly over the dartboard so the existing aim + throw
    /// controls are used to pick: the card under the crosshair highlights, and the dart that lands on a
    /// card selects it. Includes an auto-pick countdown and 1/2/3 keys as a desk-testing fallback.
    /// </summary>
    public class BuffSelectionScreen : MonoBehaviour
    {
        private const float IntroSeconds = 0.45f;
        private const float OutroSeconds = 1.1f;
        private static readonly Vector2 CardSize = new Vector2(420f, 540f);
        private static readonly float[] CardX = { -500f, 0f, 500f };
        private const float CardY = -20f;

        private sealed class CardView
        {
            public BuffDefinition Def;
            public RectTransform Root;
            public CanvasGroup Group;
            public Image Frame;
            public Image Glow;
            public Text Hint;
            public float Hover;
        }

        private Transform _root;
        private RectTransform _canvasRect;
        private Material _dimMat;
        private Text _eyebrow;
        private Text _title;
        private Text _subtitle;
        private Image _timerFill;
        private Text _timerText;
        private CardView[] _cards;

        private Action<BuffDefinition> _onPicked;
        private Action _onClosed;
        private float _shownAt;
        private float _pickedAt = -1f;
        private int _picked = -1;
        private int _lastHoveredCard = -1;
        private float _lastHoveredAt = -10f;
        private Transform _aimDot;

        // ------------------------------------------------------------------ public API

        public void Show(Transform board, BuffDefinition[] offers, Action<BuffDefinition> onPicked, Action onClosed)
        {
            HideImmediate();
            _onPicked = onPicked;
            _onClosed = onClosed;
            _picked = -1;
            _pickedAt = -1f;
            _lastHoveredCard = -1;
            _lastHoveredAt = -10f;
            _shownAt = Time.unscaledTime;
            Build(board, offers);
        }

        public bool TryPickAt(Vector3 worldPos)
        {
            if (_cards == null || _picked >= 0 || Time.unscaledTime - _shownAt < IntroSeconds * 0.5f) return false;
            int idx = CardAt(worldPos, 1.35f);
            if (_canvasRect != null)
            {
                Vector3 local = _canvasRect.InverseTransformPoint(worldPos);
                if (_lastHoveredCard >= 0 && _lastHoveredCard < _cards.Length && _cards[_lastHoveredCard] != null &&
                    Time.unscaledTime - _lastHoveredAt <= 0.40f &&
                    Mathf.Abs(local.x - CardX[_lastHoveredCard]) <= CardSize.x * 0.85f)
                {
                    idx = _lastHoveredCard;
                }
                else if (idx < 0)
                {
                    float bestDx = float.MaxValue;
                    for (int i = 0; i < _cards.Length && i < CardX.Length; i++)
                    {
                        if (_cards[i] == null) continue;
                        float dx = Mathf.Abs(local.x - CardX[i]);
                        if (dx < bestDx)
                        {
                            bestDx = dx;
                            idx = i;
                        }
                    }
                }
            }
            if (idx < 0) return false;
            Pick(idx);
            return true;
        }

        public void HideImmediate()
        {
            if (_root != null) Destroy(_root.gameObject);
            if (_dimMat != null) Destroy(_dimMat);
            _root = null;
            _cards = null;
        }

        // ------------------------------------------------------------------ build

        private void Build(Transform board, BuffDefinition[] offers)
        {
            var rootGo = new GameObject("PowerUpSelection");
            _root = rootGo.transform;
            _root.SetParent(board, false);

            // Dimmer behind the cards: darkens the board (and passthrough) so the choice reads instantly.
            var dim = GameObject.CreatePrimitive(PrimitiveType.Quad);
            dim.name = "Dimmer";
            Destroy(dim.GetComponent<Collider>());
            dim.transform.SetParent(_root, false);
            dim.transform.localPosition = new Vector3(0f, 0.05f, -0.045f);
            dim.transform.localScale = new Vector3(5.8f, 4.0f, 1f);
            var dimRend = dim.GetComponent<Renderer>();
            dimRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _dimMat = VfxResources.CreateMaterial(false, null);
            VfxResources.SetColor(_dimMat, new Color(0.01f, 0.01f, 0.03f, 0f));
            dimRend.sharedMaterial = _dimMat;

            _canvasRect = UiTheme.CreateWorldCanvas("SelectionCanvas", _root, new Vector2(1760f, 1260f), out _);
            _canvasRect.localPosition = new Vector3(0f, 0f, -0.06f);
            _canvasRect.localRotation = Quaternion.identity;
            // Large World-Space selection dashboard (+40% larger scale for effortless readability & targeting)
            _canvasRect.localScale = Vector3.one * 0.00168f;

            // Header
            _eyebrow = UiTheme.Label(_canvasRect, "Eyebrow", new Vector2(0f, 510f), new Vector2(1200f, 50f),
                UiTheme.Tracked("POWER-UP"), 34, UiTheme.Gold);
            UiTheme.Panel(_canvasRect, "EyebrowLineL", new Vector2(-230f, 510f), new Vector2(160f, 2f), null, UiTheme.Gold * new Color(1, 1, 1, 0.6f));
            UiTheme.Panel(_canvasRect, "EyebrowLineR", new Vector2(230f, 510f), new Vector2(160f, 2f), null, UiTheme.Gold * new Color(1, 1, 1, 0.6f));
            _title = UiTheme.Label(_canvasRect, "Title", new Vector2(0f, 430f), new Vector2(1400f, 120f),
                "CHOOSE ONE", 104, UiTheme.TextPrimary, display: true);
            _subtitle = UiTheme.Label(_canvasRect, "Subtitle", new Vector2(0f, 348f), new Vector2(1580f, 56f),
                UiTheme.Tracked("AIM & USE THROW MOTION TO SELECT POWER CARD"), 38, UiTheme.Gold, display: true);

            // Cards
            _cards = new CardView[offers.Length];
            for (int i = 0; i < offers.Length && i < CardX.Length; i++)
            {
                _cards[i] = BuildCard(i, offers[i]);
            }

            // Timer paused notice (no time limit while choosing)
            UiTheme.Panel(_canvasRect, "TimerTrack", new Vector2(0f, -355f), new Vector2(820f, 14f), UiTheme.Pill, new Color(0f, 0f, 0f, 0.55f), 0.2f);
            _timerFill = UiTheme.Panel(_canvasRect, "TimerFill", new Vector2(0f, -355f), new Vector2(820f, 14f), UiTheme.CyanMagentaGradient, Color.white);
            _timerFill.type = Image.Type.Filled;
            _timerFill.fillMethod = Image.FillMethod.Horizontal;
            _timerFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _timerFill.fillAmount = 1f;
            _timerText = UiTheme.Label(_canvasRect, "TimerText", new Vector2(0f, -402f), new Vector2(1200f, 52f),
                UiTheme.Tracked("TIMER PAUSED · NO TIME LIMIT"), 40, UiTheme.TextSecondary, display: true);

            _aimDot = null;
            var dot = GameObject.Find("AimDot");
            if (dot != null) _aimDot = dot.transform;
        }

        private CardView BuildCard(int index, BuffDefinition def)
        {
            Color rarity = BuffSystem.RarityColor(def.Rarity);
            var card = new CardView { Def = def };

            card.Root = UiTheme.Node(_canvasRect, $"Card{index}", new Vector2(CardX[index], CardY), CardSize);
            card.Group = card.Root.gameObject.AddComponent<CanvasGroup>();
            card.Group.alpha = 0f;
            card.Group.interactable = false;
            card.Group.blocksRaycasts = false;

            // soft rarity glow behind the card
            card.Glow = UiTheme.Panel(card.Root, "Glow", Vector2.zero, CardSize * 1.45f, UiTheme.Glow, rarity * new Color(1, 1, 1, 0.22f));
            UiTheme.Panel(card.Root, "Glass", Vector2.zero, CardSize, UiTheme.Rounded, UiTheme.GlassRaised, 1.6f);
            card.Frame = UiTheme.Panel(card.Root, "Frame", Vector2.zero, CardSize, UiTheme.Frame, Color.white, 1.6f);
            UiTheme.Tint(card.Frame, rarity * new Color(1, 1, 1, 0.55f));

            // top accent
            UiTheme.Panel(card.Root, "Accent", new Vector2(0f, CardSize.y * 0.5f - 22f), new Vector2(120f, 6f), UiTheme.Pill, rarity, 0.1f);
            UiTheme.Label(card.Root, "Rarity", new Vector2(0f, 175f), new Vector2(340f, 40f),
                UiTheme.Tracked(BuffSystem.RarityName(def.Rarity)), 24, rarity);

            // badge
            UiTheme.Panel(card.Root, "BadgeGlow", new Vector2(0f, 65f), new Vector2(260f, 260f), UiTheme.Glow, rarity * new Color(1, 1, 1, 0.35f));
            UiTheme.Panel(card.Root, "BadgeRing", new Vector2(0f, 65f), new Vector2(170f, 170f), UiTheme.Ring, rarity);
            UiTheme.Label(card.Root, "Badge", new Vector2(0f, 65f), new Vector2(170f, 120f), def.Badge, 66, UiTheme.TextPrimary, display: true);

            // text
            UiTheme.Label(card.Root, "Name", new Vector2(0f, -65f), new Vector2(340f, 60f), def.Name, 44, UiTheme.TextPrimary, display: true);
            UiTheme.Paragraph(card.Root, "Description", new Vector2(0f, -128f), new Vector2(300f, 80f), def.Description, 27, UiTheme.TextSecondary);

            // detail pill
            UiTheme.Panel(card.Root, "DetailPill", new Vector2(0f, -200f), new Vector2(230f, 44f), UiTheme.PillFrame, rarity * new Color(1, 1, 1, 0.7f), 0.7f);
            UiTheme.Label(card.Root, "Detail", new Vector2(0f, -200f), new Vector2(230f, 44f), def.Detail, 22, rarity);

            // "use throw motion" prompt shown below each card
            card.Hint = UiTheme.Label(card.Root, "Hint", new Vector2(0f, -CardSize.y * 0.5f - 40f), new Vector2(360f, 44f),
                UiTheme.Tracked("THROW TO SELECT"), 28, rarity, display: true);
            card.Hint.canvasRenderer.SetAlpha(0.55f);

            return card;
        }

        // ------------------------------------------------------------------ update

        private void Update()
        {
            if (_root == null || _cards == null) return;
            float now = Time.unscaledTime;
            float since = now - _shownAt;

            // dimmer fade-in
            VfxResources.SetColor(_dimMat, new Color(0.01f, 0.01f, 0.03f, 0.78f * UiTheme.EaseOutCubic(since / IntroSeconds)));

            // header
            float headerIn = UiTheme.EaseOutCubic(since / IntroSeconds);
            _title.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.08f, 1f, headerIn);

            if (_picked < 0)
            {
                float promptPulse = 0.72f + 0.28f * (0.5f + 0.5f * Mathf.Sin(now * 4.5f));
                _subtitle.canvasRenderer.SetAlpha(headerIn * promptPulse);
                _subtitle.rectTransform.localScale = Vector3.one * (1f + 0.025f * Mathf.Sin(now * 4.5f));

                UpdateHover();
                AnimateCardsIn(since);

                _timerFill.fillAmount = 1f;
                PollKeyboard();
            }
            else
            {
                AnimateOutro(now - _pickedAt);
            }
        }

        private void AnimateCardsIn(float since)
        {
            for (int i = 0; i < _cards.Length; i++)
            {
                var c = _cards[i];
                if (c == null) continue;
                float t = UiTheme.EaseOutCubic((since - i * 0.08f) / IntroSeconds);
                c.Group.alpha = t;
                float hoverScale = 1f + 0.06f * c.Hover;
                c.Root.anchoredPosition = new Vector2(CardX[i], CardY - 70f * (1f - t) + 14f * c.Hover);
                c.Root.localScale = Vector3.one * Mathf.Lerp(0.92f, 1f, t) * hoverScale;

                Color rarity = BuffSystem.RarityColor(c.Def.Rarity);
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
                UiTheme.Tint(c.Frame, Color.Lerp(rarity * new Color(1, 1, 1, 0.5f), Color.Lerp(rarity, Color.white, 0.4f), c.Hover * (0.7f + 0.3f * pulse)));
                c.Glow.canvasRenderer.SetAlpha(0.6f + 1.4f * c.Hover);
                c.Hint.canvasRenderer.SetAlpha(0.55f + 0.45f * c.Hover);
            }
        }

        private void UpdateHover()
        {
            int hovered = -1;
            if (_aimDot != null && _aimDot.gameObject.activeInHierarchy) hovered = CardAt(_aimDot.position, 1.25f);
            if (hovered >= 0)
            {
                _lastHoveredCard = hovered;
                _lastHoveredAt = Time.unscaledTime;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            for (int i = 0; i < _cards.Length; i++)
            {
                if (_cards[i] == null) continue;
                float target = i == hovered ? 1f : 0f;
                _cards[i].Hover = Mathf.MoveTowards(_cards[i].Hover, target, dt * 7f);
            }
        }

        private void AnimateOutro(float t)
        {
            float k = Mathf.Clamp01(t / OutroSeconds);
            for (int i = 0; i < _cards.Length; i++)
            {
                var c = _cards[i];
                if (c == null) continue;
                if (i == _picked)
                {
                    float pop = k < 0.25f ? Mathf.Lerp(1.06f, 1.18f, k / 0.25f) : Mathf.Lerp(1.18f, 1.1f, (k - 0.25f) / 0.75f);
                    c.Root.localScale = Vector3.one * pop;
                    c.Group.alpha = k < 0.7f ? 1f : 1f - (k - 0.7f) / 0.3f;
                    UiTheme.Tint(c.Frame, Color.Lerp(Color.white, BuffSystem.RarityColor(c.Def.Rarity), k));
                    c.Glow.canvasRenderer.SetAlpha(2.5f * (1f - k));
                }
                else
                {
                    c.Group.alpha = Mathf.Max(0f, 1f - k * 4f);
                    c.Root.localScale = Vector3.one * Mathf.Lerp(1f, 0.9f, k * 3f);
                }
            }

            float fade = k < 0.7f ? 1f : 1f - (k - 0.7f) / 0.3f;
            VfxResources.SetColor(_dimMat, new Color(0.01f, 0.01f, 0.03f, 0.78f * fade));
            _title.canvasRenderer.SetAlpha(fade);
            _eyebrow.canvasRenderer.SetAlpha(fade);
            _subtitle.canvasRenderer.SetAlpha(fade);
            _timerText.canvasRenderer.SetAlpha(0f);
            _timerFill.canvasRenderer.SetAlpha(0f);

            if (k >= 1f)
            {
                var closed = _onClosed;
                HideImmediate();
                closed?.Invoke();
            }
        }

        // ------------------------------------------------------------------ picking

        private void Pick(int index)
        {
            if (_picked >= 0 || index < 0 || index >= _cards.Length || _cards[index] == null) return;
            _picked = index;
            _pickedAt = Time.unscaledTime;

            var def = _cards[index].Def;
            Color rarity = BuffSystem.RarityColor(def.Rarity);
            _eyebrow.text = UiTheme.Tracked("ACTIVATED");
            _eyebrow.color = rarity;
            _title.text = def.Name;
            _subtitle.text = def.Detail;

            Vector3 cardWorld = _cards[index].Root.position;
            Quaternion rot = _root.rotation;
            VfxFactory.SpawnShockwave(cardWorld, rot, 0.05f, 0.45f, 0.02f, rarity, 0.6f);
            VfxFactory.RingSparks(cardWorld, rot, 0.2f, rarity, 40, 0.7f, 0.025f, 0.6f);

            _onPicked?.Invoke(def);
        }

        private int CardAt(Vector3 worldPos, float tolerance)
        {
            if (_canvasRect == null || _cards == null) return -1;
            Vector3 local = _canvasRect.InverseTransformPoint(worldPos);
            int bestIdx = -1;
            float bestDx = float.MaxValue;
            for (int i = 0; i < _cards.Length && i < CardX.Length; i++)
            {
                if (_cards[i] == null) continue;
                float dx = Mathf.Abs(local.x - CardX[i]);
                float dy = Mathf.Abs(local.y - CardY);
                if (dx <= CardSize.x * 0.5f * tolerance &&
                    dy <= CardSize.y * 0.5f * tolerance &&
                    dx < bestDx)
                {
                    bestDx = dx;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private void PollKeyboard()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame) Pick(0);
            else if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame) Pick(1);
            else if (kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame) Pick(2);
#endif
        }

        private void OnDestroy()
        {
            HideImmediate();
        }
    }
}
