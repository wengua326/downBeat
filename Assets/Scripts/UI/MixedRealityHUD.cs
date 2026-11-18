using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using RhythmDartMR.Audio;
using RhythmDartMR.Gameplay;
using RhythmDartMR.VFX;

namespace RhythmDartMR.UI
{
    /// <summary>
    /// Modern world-space scoreboard above the dartboard (see <see cref="UiTheme"/> for the visual language).
    ///
    ///  ┌───────────────────────────────────────────────────────────────────────────┐
    ///  │  T I M E            │        S C O R E          │        D A R T S          │
    ///  │   1:42              │         12,480            │        14 / 20            │
    ///  │  ▬▬▬▬▬▬▬▬▬▬───      │   ■ ■ ■ ■ □ □ □ □          │   ||||||||||||||┆┆┆┆┆┆      │
    ///  │                     │   ×4 COMBO · HEATING UP   │   ● 92 BPM                │
    ///  └───────────────────────────────────────────────────────────────────────────┘
    ///      [2X OVERDRIVE 3 LEFT]  [XL BIG TARGETS 12s]        ← active power-ups
    ///
    /// Round end shows a results card (score, new-best badge, best / max combo / circles hit / accuracy).
    /// Performance: animated parts are on a nested canvas; pulses use CanvasRenderer tints (no mesh rebuild);
    /// strings are only rebuilt when the value they show changes.
    /// </summary>
    public class MixedRealityHUD : MonoBehaviour
    {
        private const float AboveBoard = 0.24f;
        private const float HudRootScale = 0.00100f;
        private const float TimeColX = -750f;
        private const float ScoreColX = 0f;
        private const float DartsColX = 750f;
        private const int MaxTicks = 40;
        private const int MaxBuffChips = 6;
        private const int ComboSegments = 8;

        private RectTransform _root;
        private Canvas _rootCanvas;
        private Canvas _dynCanvas;
        private Canvas _resultsCanvas;
        private CanvasGroup _mainGroup;

        // static layer
        private Image[] _brackets;

        // dynamic layer
        private Text _timeLabel, _timeValue, _timeFlash;
        private Image _timeFill;
        private Text _scoreValue;
        private Image _scoreGlow;
        private Image[] _comboSegs;
        private Text _comboText;
        private Text _dartsValue, _dartsFlash, _bpmText;
        private Image[] _ticks;
        private Image _beatDot;

        [System.Serializable]
        private sealed class BuffChip
        {
            public RectTransform Root;
            public Image Frame;
            public Image Bar;
            public Text Badge;
            public Text Name;
            public Text Remaining;
        }
        private BuffChip[] _chips;
        private readonly List<ActiveBuffView> _buffViews = new List<ActiveBuffView>();

        // results
        private GameObject _results;
        private CanvasGroup _resultsGroup;
        private Text _resultsReason, _resultsScore, _statBest, _statCombo, _statCircles, _statAccuracy, _resultsHint;
        private GameObject _newBestBadge;
        private float _resultsShownAt;
        private int _resultsFinalScore;
        private int _shownResultScore = int.MinValue;

        // state
        private float _displayedScore;
        private int _targetScore;
        private float _scorePunch;
        private int _shownScore = int.MinValue, _shownCombo = int.MinValue, _shownBpm = int.MinValue;
        private int _shownTimeSec = int.MinValue, _shownRemaining = int.MinValue, _shownMax = int.MinValue;
        private bool _shownPaused;
        private bool _wasRoundActive;
        private int _bestAtRoundStart;
        private float _timeFlashUntil, _dartsFlashUntil;
        private GameSessionManager _hookedSession;

        private void Start()
        {
            Build();
        }

        private void OnDestroy()
        {
            if (_hookedSession != null)
            {
                _hookedSession.OnBonusTimeAdded -= HandleBonusTime;
                _hookedSession.OnBonusDartsAdded -= HandleBonusDarts;
            }
        }

        // ===================================================================== build

        private void Build()
        {
            _shownScore = int.MinValue;
            _shownCombo = int.MinValue;
            _shownBpm = int.MinValue;
            _shownTimeSec = int.MinValue;
            _shownRemaining = int.MinValue;
            _shownMax = int.MinValue;
            _shownResultScore = int.MinValue;

            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);

            _root = UiTheme.CreateWorldCanvas("ModernScoreboard", transform, new Vector2(2360f, 940f), out _rootCanvas);
            _root.localScale = Vector3.one * HudRootScale;
            _root.rotation = Quaternion.Euler(12.5f, 0f, 0f);
            PlaceAboveBoard();

            // ---------------- static layer (main panel + outer neon border + 2.5D depth sub-cards)
            var main = UiTheme.Node(_root, "Main", Vector2.zero, new Vector2(2360f, 940f));
            _mainGroup = main.gameObject.AddComponent<CanvasGroup>();

            UiTheme.Panel(main, "OuterGlow", new Vector2(0f, 40f), new Vector2(2420f, 540f), UiTheme.Glow, new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, 0.18f));
            UiTheme.Panel(main, "Glass", new Vector2(0f, 40f), new Vector2(2260f, 440f), UiTheme.Rounded, UiTheme.Glass, 2.4f);
            UiTheme.Panel(main, "Frame", new Vector2(0f, 40f), new Vector2(2260f, 440f), UiTheme.NeonFrame, UiTheme.Accent, 2.4f);
            _brackets = UiTheme.CornerBrackets(main, new Vector2(0f, 40f), new Vector2(2260f, 440f), 84f, 6f, UiTheme.Accent2);

            // Inner sub-cards offset forward (-4 canvas units ≈ -0.0055m) for layered 2.5D spatial depth
            var subCards = UiTheme.Node(main, "SubCards", Vector2.zero, new Vector2(2360f, 940f));
            subCards.localPosition = new Vector3(0f, 0f, -4f);

            Color cyanSubBorder = new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, 0.48f);
            Color magentaSubBorder = new Color(UiTheme.Accent2.r, UiTheme.Accent2.g, UiTheme.Accent2.b, 0.48f);

            UiTheme.Panel(subCards, "TimeSubCard", new Vector2(TimeColX, 40f), new Vector2(660f, 370f), UiTheme.Rounded, UiTheme.SubCard, 1.8f);
            UiTheme.Panel(subCards, "TimeSubFrame", new Vector2(TimeColX, 40f), new Vector2(660f, 370f), UiTheme.NeonFrame, cyanSubBorder, 1.8f);

            UiTheme.Panel(subCards, "ScoreSubCard", new Vector2(ScoreColX, 40f), new Vector2(760f, 370f), UiTheme.Rounded, UiTheme.SubCard, 1.8f);
            UiTheme.Panel(subCards, "ScoreSubFrame", new Vector2(ScoreColX, 40f), new Vector2(760f, 370f), UiTheme.NeonFrame, magentaSubBorder, 1.8f);

            UiTheme.Panel(subCards, "DartsSubCard", new Vector2(DartsColX, 40f), new Vector2(660f, 370f), UiTheme.Rounded, UiTheme.SubCard, 1.8f);
            UiTheme.Panel(subCards, "DartsSubFrame", new Vector2(DartsColX, 40f), new Vector2(660f, 370f), UiTheme.NeonFrame, cyanSubBorder, 1.8f);

            var frontLabels = UiTheme.Node(main, "FrontLabels", Vector2.zero, new Vector2(2360f, 940f));
            frontLabels.localPosition = new Vector3(0f, 0f, -6.5f);
            _timeLabel = UiTheme.Label(frontLabels, "TimeLabel", new Vector2(TimeColX, 178f), new Vector2(560f, 56f), UiTheme.Tracked("TIME"), 44, UiTheme.Accent, display: true);
            UiTheme.Label(frontLabels, "ScoreLabel", new Vector2(ScoreColX, 178f), new Vector2(560f, 56f), UiTheme.Tracked("SCORE"), 44, UiTheme.Accent2, display: true);
            UiTheme.Label(frontLabels, "DartsLabel", new Vector2(DartsColX, 178f), new Vector2(560f, 56f), UiTheme.Tracked("DARTS"), 44, UiTheme.Accent, display: true);
            UiTheme.Panel(frontLabels, "TimeTrack", new Vector2(TimeColX, -62f), new Vector2(520f, 16f), UiTheme.Pill, new Color(0f, 0f, 0f, 0.55f), 0.25f);

            // ---------------- dynamic layer (nested canvas, -7 canvas units ≈ -0.0097m forward for 2.5D depth)
            var dyn = UiTheme.Node(main, "Dynamic", Vector2.zero, new Vector2(2360f, 940f));
            dyn.localPosition = new Vector3(0f, 0f, -7f);
            _dynCanvas = dyn.gameObject.AddComponent<Canvas>();
            if (Camera.main != null) _dynCanvas.worldCamera = Camera.main;

            // time
            _timeValue = UiTheme.Label(dyn, "TimeValue", new Vector2(TimeColX, 58f), new Vector2(600f, 180f), "2:00", 150, UiTheme.TextPrimary, display: true);
            _timeFill = UiTheme.Panel(dyn, "TimeFill", new Vector2(TimeColX, -62f), new Vector2(520f, 16f), UiTheme.CyanMagentaGradient, Color.white);
            _timeFill.type = Image.Type.Filled;
            _timeFill.fillMethod = Image.FillMethod.Horizontal;
            _timeFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _timeFlash = UiTheme.Label(dyn, "TimeFlash", new Vector2(TimeColX, -118f), new Vector2(520f, 70f), "", 62, UiTheme.Success, display: true);

            // score
            _scoreGlow = UiTheme.Panel(dyn, "ScoreGlow", new Vector2(ScoreColX, 62f), new Vector2(820f, 300f), UiTheme.Glow, new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, 0.16f));
            _scoreValue = UiTheme.Label(dyn, "ScoreValue", new Vector2(ScoreColX, 62f), new Vector2(740f, 200f), "0", 172, UiTheme.TextPrimary, display: true);
            _comboSegs = new Image[ComboSegments];
            for (int i = 0; i < ComboSegments; i++)
            {
                float x = (i - (ComboSegments - 1) * 0.5f) * 82f;
                _comboSegs[i] = UiTheme.Panel(dyn, $"Combo{i}", new Vector2(x, -58f), new Vector2(72f, 20f), UiTheme.Pill, Color.white, 0.25f);
            }
            _comboText = UiTheme.Label(dyn, "ComboText", new Vector2(ScoreColX, -118f), new Vector2(720f, 58f), "", 46, UiTheme.TextSecondary, display: true);

            // darts
            _dartsValue = UiTheme.Label(dyn, "DartsValue", new Vector2(DartsColX, 58f), new Vector2(600f, 180f), "20", 150, UiTheme.TextPrimary, display: true);
            _ticks = new Image[MaxTicks];
            for (int i = 0; i < MaxTicks; i++)
            {
                _ticks[i] = UiTheme.Panel(dyn, $"Tick{i}", new Vector2(DartsColX, -60f), new Vector2(8f, 36f), UiTheme.White, UiTheme.Accent);
                _ticks[i].gameObject.SetActive(false);
            }
            _beatDot = UiTheme.Panel(dyn, "BeatDot", new Vector2(DartsColX - 110f, -118f), new Vector2(42f, 42f), UiTheme.Glow, UiTheme.Accent2);
            _bpmText = UiTheme.Label(dyn, "Bpm", new Vector2(DartsColX + 40f, -118f), new Vector2(300f, 58f), "-- BPM", 44, UiTheme.TextSecondary, display: true, align: TextAnchor.MiddleLeft);
            _dartsFlash = UiTheme.Label(dyn, "DartsFlash", new Vector2(DartsColX + 240f, 58f), new Vector2(220f, 76f), "", 68, UiTheme.Success, display: true);

            // buff chips
            _chips = new BuffChip[MaxBuffChips];
            for (int i = 0; i < MaxBuffChips; i++) _chips[i] = BuildChip(dyn, i);

            BuildResults(_root);
            _results.SetActive(false);
        }

        private BuffChip BuildChip(Transform parent, int i)
        {
            var chip = new BuffChip();
            chip.Root = UiTheme.Node(parent, $"BuffChip{i}", new Vector2(0f, -238f), new Vector2(380f, 92f));
            chip.Root.localScale = Vector3.one * 1.2f;
            UiTheme.Panel(chip.Root, "Glass", Vector2.zero, new Vector2(380f, 92f), UiTheme.Rounded, UiTheme.Glass, 1.0f);
            chip.Frame = UiTheme.Panel(chip.Root, "Frame", Vector2.zero, new Vector2(380f, 92f), UiTheme.NeonFrame, Color.white, 1.0f);
            chip.Badge = UiTheme.Label(chip.Root, "Badge", new Vector2(-132f, 2f), new Vector2(104f, 68f), "", 48, Color.white, display: true);
            chip.Name = UiTheme.Label(chip.Root, "Name", new Vector2(48f, 16f), new Vector2(250f, 38f), "", 32, UiTheme.TextPrimary, display: true, align: TextAnchor.MiddleLeft);
            chip.Remaining = UiTheme.Label(chip.Root, "Remaining", new Vector2(48f, -16f), new Vector2(250f, 36f), "", 30, UiTheme.TextSecondary, display: true, align: TextAnchor.MiddleLeft);
            chip.Bar = UiTheme.Panel(chip.Root, "Bar", new Vector2(0f, -40f), new Vector2(340f, 8f), UiTheme.CyanMagentaGradient, Color.white);
            chip.Bar.type = Image.Type.Filled;
            chip.Bar.fillMethod = Image.FillMethod.Horizontal;
            chip.Bar.fillOrigin = (int)Image.OriginHorizontal.Left;
            chip.Root.gameObject.SetActive(false);
            return chip;
        }

        private void BuildResults(Transform parent)
        {
            var r = UiTheme.Node(parent, "Results", new Vector2(0f, -60f), new Vector2(1580f, 780f));
            r.localPosition = new Vector3(0f, -60f, -10f);
            r.localScale = Vector3.one * 1.16f;
            _results = r.gameObject;
            _resultsGroup = _results.AddComponent<CanvasGroup>();
            _resultsCanvas = _results.AddComponent<Canvas>();
            if (Camera.main != null) _resultsCanvas.worldCamera = Camera.main;

            UiTheme.Panel(r, "Glass", Vector2.zero, new Vector2(1580f, 780f), UiTheme.Rounded, UiTheme.Glass, 2.4f);
            UiTheme.Panel(r, "Frame", Vector2.zero, new Vector2(1580f, 780f), UiTheme.NeonFrame, new Color(UiTheme.Gold.r, UiTheme.Gold.g, UiTheme.Gold.b, 0.72f), 2.4f);
            UiTheme.CornerBrackets(r, Vector2.zero, new Vector2(1580f, 780f), 84f, 6f, UiTheme.Gold);

            _resultsReason = UiTheme.Label(r, "Reason", new Vector2(0f, 310f), new Vector2(1380f, 60f), "", 48, UiTheme.Gold, display: true);
            UiTheme.Label(r, "FinalLabel", new Vector2(0f, 244f), new Vector2(1380f, 52f), UiTheme.Tracked("FINAL SCORE"), 42, UiTheme.TextSecondary, display: true);
            UiTheme.Panel(r, "ScoreGlow", new Vector2(0f, 130f), new Vector2(1100f, 340f), UiTheme.Glow, new Color(UiTheme.Gold.r, UiTheme.Gold.g, UiTheme.Gold.b, 0.18f));
            _resultsScore = UiTheme.Label(r, "Score", new Vector2(0f, 130f), new Vector2(1380f, 230f), "0", 200, UiTheme.TextPrimary, display: true);

            var badge = UiTheme.Node(r, "NewBest", new Vector2(0f, 10f), new Vector2(440f, 66f));
            UiTheme.Panel(badge, "Pill", Vector2.zero, new Vector2(440f, 66f), UiTheme.Pill, UiTheme.Gold, 0.9f);
            UiTheme.Label(badge, "Text", Vector2.zero, new Vector2(440f, 66f), UiTheme.Tracked("NEW BEST"), 40, new Color(0.06f, 0.05f, 0.02f), display: true);
            _newBestBadge = badge.gameObject;

            UiTheme.Panel(r, "Divider", new Vector2(0f, -45f), new Vector2(1380f, 3f), null, UiTheme.Hairline);

            _statBest = Stat(r, -530f, "BEST");
            _statCombo = Stat(r, -176f, "MAX COMBO");
            _statCircles = Stat(r, 176f, "CIRCLES HIT");
            _statAccuracy = Stat(r, 530f, "ACCURACY");

            _resultsHint = UiTheme.Label(r, "Hint", new Vector2(0f, -310f), new Vector2(1480f, 64f), UiTheme.Tracked("USE THROW MOTION TO START NEW SESSION"), 44, UiTheme.Gold, display: true);
        }

        private static Text Stat(Transform parent, float x, string label)
        {
            var value = UiTheme.Label(parent, label + "Value", new Vector2(x, -125f), new Vector2(340f, 110f), "0", 92, UiTheme.TextPrimary, display: true);
            UiTheme.Label(parent, label + "Label", new Vector2(x, -198f), new Vector2(340f, 48f), UiTheme.Tracked(label), 32, UiTheme.TextSecondary, display: true);
            return value;
        }

        // ===================================================================== update

        private void Update()
        {
            if (_root == null || _chips == null || _brackets == null || _comboSegs == null || _ticks == null || _timeValue == null || _scoreValue == null || _dartsValue == null)
            {
                Build();
                if (_root == null) return;
            }
            if (Mathf.Abs(_root.localScale.x - HudRootScale) > 0.00001f)
            {
                _root.localScale = Vector3.one * HudRootScale;
            }
            SyncCanvasCamera();
            PlaceAboveBoard();
            HookSession();

            var score = ScoreAndComboManager.Instance;
            var session = GameSessionManager.Instance;
            var conductor = BeatConductor.Instance;

            int currentScore = score != null ? score.CurrentScore : 0;
            int combo = score != null ? score.CurrentCombo : 0;
            bool roundActive = session == null || session.IsRoundActive || session.IsCountingDown;
            bool paused = session != null && session.IsPaused;
            int remaining = session != null ? session.RemainingThrows : 20;
            int maxDarts = session != null ? session.maxThrowsPerSession : 20;

            // round transitions
            if (roundActive && !_wasRoundActive)
            {
                _bestAtRoundStart = score != null ? score.HighestPreviousSessionScore : 0;
                _results.SetActive(false);
                _mainGroup.alpha = 1f;
                _displayedScore = currentScore;
            }
            else if (!roundActive && _wasRoundActive)
            {
                ShowResults(score, session);
            }
            _wasRoundActive = roundActive;

            // beat
            bool running = conductor != null && conductor.IsRunning;
            float phase = running ? conductor.BeatPhase : Mathf.Repeat(Time.unscaledTime * 0.8f, 1f);
            float pulse = Mathf.Exp(-phase * 5f);
            Color tier = VfxResources.ComboColor(combo);

            // corner brackets pulse on the beat (tint only — no rebuild)
            float timeLeft = session != null ? session.TimeRemainingSeconds : 0f;
            bool finalStretch = roundActive && !paused && session != null && timeLeft <= 10f;
            Color bracket = finalStretch ? UiTheme.Danger : paused ? UiTheme.Gold : UiTheme.Accent2;
            bracket.a = 0.55f + 0.45f * pulse;
            for (int i = 0; i < _brackets.Length; i++) UiTheme.Tint(_brackets[i], bracket);

            UpdateTime(session, timeLeft, paused, finalStretch, pulse);
            UpdateScore(currentScore, tier);
            UpdateCombo(combo, tier, pulse);
            UpdateDarts(remaining, maxDarts, running ? conductor.CurrentBpm : 0f, pulse);
            UpdateBuffChips();
            UpdateFlashes();
            if (_results.activeSelf) AnimateResults();
        }

        private void UpdateTime(GameSessionManager session, float timeLeft, bool paused, bool finalStretch, float pulse)
        {
            int secs = Mathf.CeilToInt(timeLeft);
            if (secs != _shownTimeSec)
            {
                _shownTimeSec = secs;
                _timeValue.text = $"{secs / 60}:{secs % 60:00}";
            }
            if (paused != _shownPaused)
            {
                _shownPaused = paused;
                _timeLabel.text = UiTheme.Tracked(paused ? "PAUSED" : "TIME");
                _timeLabel.color = paused ? UiTheme.Gold : UiTheme.Accent;
            }

            float length = session != null ? Mathf.Max(1f, session.SessionLengthSeconds) : 1f;
            _timeFill.fillAmount = Mathf.Clamp01(timeLeft / length);
            Color fill = finalStretch ? Color.Lerp(UiTheme.Danger, Color.white, 0.25f * pulse) : timeLeft <= 30f ? UiTheme.Orange : Color.white;
            UiTheme.Tint(_timeFill, fill);
            UiTheme.Tint(_timeValue, finalStretch ? Color.Lerp(UiTheme.Danger, Color.white, 0.3f * pulse) : Color.white);
            _timeValue.rectTransform.localScale = Vector3.one * (finalStretch ? 1f + 0.06f * pulse : 1f);
        }

        private void UpdateScore(int currentScore, Color tier)
        {
            if (currentScore != _targetScore)
            {
                if (currentScore > _targetScore) _scorePunch = 1f;
                _targetScore = currentScore;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            _displayedScore = Mathf.MoveTowards(_displayedScore, _targetScore,
                Mathf.Max(60f, Mathf.Abs(_targetScore - _displayedScore) * 7f) * dt);
            int shown = Mathf.RoundToInt(_displayedScore);
            if (shown != _shownScore)
            {
                _shownScore = shown;
                _scoreValue.text = shown.ToString("N0");
            }
            _scorePunch = Mathf.MoveTowards(_scorePunch, 0f, dt * 4f);
            _scoreValue.rectTransform.localScale = Vector3.one * (1f + 0.08f * _scorePunch);
            Color glow = tier;
            glow.a = 0.6f + 1.2f * _scorePunch;
            UiTheme.Tint(_scoreGlow, glow);
        }

        private void UpdateCombo(int combo, Color tier, float pulse)
        {
            int lit = Mathf.Min(combo, ComboSegments);
            for (int i = 0; i < _comboSegs.Length; i++)
            {
                Color grad = Color.Lerp(UiTheme.Accent, UiTheme.Accent2, i / (float)(ComboSegments - 1));
                Color c = i < lit ? Color.Lerp(grad, Color.white, i == lit - 1 ? 0.35f * pulse : 0f) : new Color(1f, 1f, 1f, 0.14f);
                UiTheme.Tint(_comboSegs[i], c);
            }
            if (combo != _shownCombo)
            {
                _shownCombo = combo;
                string tierName = combo >= VfxResources.ComboTierFever ? "UNSTOPPABLE"
                    : combo >= VfxResources.ComboTierBlazing ? "IN THE ZONE"
                    : combo >= VfxResources.ComboTierHot ? "HEATING UP" : null;
                if (combo <= 0) _comboText.text = UiTheme.Tracked("NO COMBO");
                else if (tierName == null) _comboText.text = $"×{combo}  COMBO";
                else _comboText.text = $"×{combo}  COMBO  ·  <color=#{UiTheme.Hex(tier)}>{tierName}</color>";
                _comboText.color = combo > 0 ? UiTheme.TextPrimary : UiTheme.TextMuted;
            }
        }

        private void UpdateDarts(int remaining, int maxDarts, float bpm, float pulse)
        {
            if (remaining != _shownRemaining || maxDarts != _shownMax)
            {
                _shownRemaining = remaining;
                _shownMax = maxDarts;
                _dartsValue.text = $"{remaining}<size=86><color=#{UiTheme.Hex(UiTheme.TextSecondary)}>  / {maxDarts}</color></size>";
                _dartsValue.color = remaining <= 3 ? UiTheme.Orange : UiTheme.TextPrimary;

                int count = Mathf.Min(maxDarts, MaxTicks);
                float spacing = count > 24 ? 12f : 18f;
                for (int i = 0; i < MaxTicks; i++)
                {
                    bool exists = i < count;
                    _ticks[i].gameObject.SetActive(exists);
                    if (!exists) continue;
                    _ticks[i].rectTransform.anchoredPosition = new Vector2(DartsColX + (i - (count - 1) * 0.5f) * spacing, -60f);
                    _ticks[i].rectTransform.sizeDelta = new Vector2(spacing * 0.52f, 36f);
                    _ticks[i].color = i < remaining ? Color.Lerp(UiTheme.Accent, UiTheme.Accent2, i / Mathf.Max(1f, count - 1f)) : new Color(1f, 1f, 1f, 0.14f);
                }
            }

            int b = Mathf.RoundToInt(bpm);
            if (b != _shownBpm)
            {
                _shownBpm = b;
                _bpmText.text = b > 0 ? $"{b} BPM" : "-- BPM";
            }
            _beatDot.rectTransform.localScale = Vector3.one * (0.7f + 0.6f * pulse);
            UiTheme.Tint(_beatDot, new Color(1f, 1f, 1f, 0.5f + 0.5f * pulse));
        }

        private void UpdateBuffChips()
        {
            if (_chips == null) return;

            if (BuffSystem.Instance != null) BuffSystem.Instance.GetActiveBuffs(_buffViews);
            else _buffViews.Clear();

            int n = Mathf.Min(_buffViews.Count, _chips.Length);
            float spacing = 480f;
            for (int i = 0; i < _chips.Length; i++)
            {
                var chip = _chips[i];
                if (chip == null || chip.Root == null) continue;
                bool on = i < n;
                if (chip.Root.gameObject.activeSelf != on) chip.Root.gameObject.SetActive(on);
                if (!on) continue;

                var v = _buffViews[i];
                chip.Root.anchoredPosition = new Vector2((i - (n - 1) * 0.5f) * spacing, -238f);
                if (chip.Name.text != v.Name)
                {
                    chip.Name.text = v.Name;
                    chip.Badge.text = v.Badge;
                    chip.Badge.color = v.Color;
                    chip.Bar.color = Color.white;
                }
                if (chip.Remaining.text != v.Remaining) chip.Remaining.text = v.Remaining;
                if (Mathf.Abs(chip.Bar.fillAmount - v.Fraction) > 0.004f) chip.Bar.fillAmount = v.Fraction;
                Color frame = v.Color;
                frame.a = v.Fraction < 0.25f ? 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 8f)) : 0.85f;
                UiTheme.Tint(chip.Frame, frame);
            }
        }

        private void UpdateFlashes()
        {
            float t1 = Mathf.Clamp01((_timeFlashUntil - Time.unscaledTime) / 1.5f);
            _timeFlash.canvasRenderer.SetAlpha(t1);
            float t2 = Mathf.Clamp01((_dartsFlashUntil - Time.unscaledTime) / 1.5f);
            _dartsFlash.canvasRenderer.SetAlpha(t2);
        }

        // ===================================================================== results

        private void ShowResults(ScoreAndComboManager score, GameSessionManager session)
        {
            _results.SetActive(true);
            _resultsShownAt = Time.unscaledTime;
            _mainGroup.alpha = 0.2f;

            _resultsFinalScore = score != null ? score.CurrentScore : 0;
            string reason = session == null ? "ROUND COMPLETE"
                : session.LastEndReason == SessionEndReason.TimeUp ? "TIME'S UP"
                : session.LastEndReason == SessionEndReason.OutOfDarts ? "OUT OF DARTS"
                : "ROUND COMPLETE";
            _resultsReason.text = UiTheme.Tracked(reason);

            bool newBest = _resultsFinalScore > 0 && _resultsFinalScore > _bestAtRoundStart;
            _newBestBadge.SetActive(newBest);

            int best = Mathf.Max(_bestAtRoundStart, _resultsFinalScore);
            int darts = score != null ? score.DartsJudged : 0;
            int circles = score != null ? score.CirclesHit : 0;
            _statBest.text = best.ToString("N0");
            _statCombo.text = score != null ? $"×{score.MaxCombo}" : "×0";
            _statCircles.text = circles.ToString();
            _statAccuracy.text = darts > 0 ? $"{Mathf.RoundToInt(100f * circles / darts)}%" : "--";
            _resultsScore.text = "0";
            _shownResultScore = 0;
        }

        private void AnimateResults()
        {
            float t = Time.unscaledTime - _resultsShownAt;
            _resultsGroup.alpha = UiTheme.EaseOutCubic(t / 0.4f);
            _results.transform.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, UiTheme.EaseOutCubic(t / 0.4f));

            float count = UiTheme.EaseOutCubic((t - 0.3f) / 1.2f);
            int shown = Mathf.RoundToInt(_resultsFinalScore * count);
            if (shown != _shownResultScore)
            {
                _shownResultScore = shown;
                _resultsScore.text = shown.ToString("N0");
            }
            _resultsHint.canvasRenderer.SetAlpha(0.45f + 0.55f * (0.5f + 0.5f * Mathf.Sin(t * 3f)));
        }

        // ===================================================================== helpers

        private void HookSession()
        {
            var s = GameSessionManager.Instance;
            if (s == _hookedSession) return;
            if (_hookedSession != null)
            {
                _hookedSession.OnBonusTimeAdded -= HandleBonusTime;
                _hookedSession.OnBonusDartsAdded -= HandleBonusDarts;
            }
            _hookedSession = s;
            if (s != null)
            {
                s.OnBonusTimeAdded += HandleBonusTime;
                s.OnBonusDartsAdded += HandleBonusDarts;
            }
        }

        private void HandleBonusTime(float seconds)
        {
            _timeFlash.text = $"+{seconds:0}s";
            _timeFlashUntil = Time.unscaledTime + 1.8f;
        }

        private void HandleBonusDarts(int count)
        {
            _dartsFlash.text = $"+{count}";
            _dartsFlashUntil = Time.unscaledTime + 1.8f;
        }

        private void LateUpdate()
        {
            if (_root == null) return;
            SyncCanvasCamera();
            PlaceAboveBoard();
        }

        private void SyncCanvasCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_rootCanvas != null && _rootCanvas.worldCamera != cam) _rootCanvas.worldCamera = cam;
            if (_dynCanvas != null && _dynCanvas.worldCamera != cam) _dynCanvas.worldCamera = cam;
            if (_resultsCanvas != null && _resultsCanvas.worldCamera != cam) _resultsCanvas.worldCamera = cam;
        }

        private void PlaceAboveBoard()
        {
            if (_root == null) return;
            var board = RhythmDartboard.Instance != null ? RhythmDartboard.Instance : FindAnyObjectByType<RhythmDartboard>();
            Vector3 boardPos = board != null
                ? board.transform.position
                : RhythmDartboard.DefaultBoardPosition;
            Quaternion boardRot = board != null
                ? board.transform.rotation
                : Quaternion.identity;
            float r = board != null
                ? board.WorldBoardRadius
                : (0.72f * RhythmDartboard.DefaultBoardScale);
            Vector3 target = new Vector3(boardPos.x, boardPos.y + r + AboveBoard, boardPos.z);
            Quaternion targetRot = boardRot * Quaternion.Euler(12.5f, 0f, 0f);
            if ((_root.position - target).sqrMagnitude > 0.000001f) _root.position = target;
            if (Quaternion.Angle(_root.rotation, targetRot) > 0.01f) _root.rotation = targetRot;
        }
    }
}
