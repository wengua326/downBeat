using System;
using UnityEngine;
using RhythmDartMR.Audio;
using RhythmDartMR.Gameplay;
using RhythmDartMR.UI;
using Random = UnityEngine.Random;

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// Self-bootstrapping visual feedback layer (no scene setup needed):
    /// - Impact FX on every dart: flash, spark burst, shockwave ring, floating score popup.
    ///   Bigger for Triple / Double / Bull, huge gold star-burst for hitting the bonus circle (PERFECT).
    /// - Combo popups and milestone celebrations (HOT STREAK / ON FIRE / FEVER).
    /// - Neon beat rim around the dartboard that pulses on every beat (stronger on the downbeat), sends a
    ///   ripple outward each bar, changes colour with the combo tier, and sheds rainbow sparks in Fever.
    /// - "TEMPO UP!" announcement whenever the BeatConductor raises the BPM.
    /// </summary>
    public class ImpactVfxDirector : MonoBehaviour
    {
        public static ImpactVfxDirector Instance { get; private set; }

        private ScoreAndComboManager _hookedScore;
        private BeatConductor _hookedConductor;
        private BuffSystem _hookedBuffs;

        private RhythmDartboard _rimBoard;
        private LineRenderer _rim;
        private LineRenderer _halo;

        private int _lastWholeBeat = int.MinValue;
        private float _flash;
        private float _lastBpm = -1f;
        private bool _bullseyeAltSide;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            new GameObject("ImpactVfxDirector").AddComponent<ImpactVfxDirector>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
        }

        private void OnDestroy()
        {
            if (_hookedScore != null) _hookedScore.OnDartJudged -= HandleDartJudged;
            if (_hookedConductor != null) _hookedConductor.OnTempoChanged -= HandleTempoChanged;
            if (_hookedBuffs != null) _hookedBuffs.OnBuffChosen -= HandleBuffChosen;
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            HookEvents();
            EnsureRim();
            AnimateRim();
            MonitorRoundState();
            MonitorFrameRate();
        }

        // ------------------------------------------------------------------ auto quality

        private const float LowFpsFrameTime = 1f / 45f;   // below ~45 FPS ...
        private const float LowFpsSecondsToDowngrade = 2.5f; // ... sustained for this long → Low quality
        private const float StartupGraceSeconds = 4f;      // ignore loading hitches at startup
        private float _smoothedFrameTime = 1f / 72f;
        private float _slowSeconds;

        private void MonitorFrameRate()
        {
            if (!VfxSettings.AutoQuality || VfxSettings.LowQuality) return;
            if (Time.timeSinceLevelLoad < StartupGraceSeconds) return;

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            _smoothedFrameTime = Mathf.Lerp(_smoothedFrameTime, dt, 0.05f);
            _slowSeconds = _smoothedFrameTime > LowFpsFrameTime ? _slowSeconds + dt : 0f;

            if (_slowSeconds >= LowFpsSecondsToDowngrade)
            {
                VfxSettings.LowQuality = true;
                if (_halo != null) _halo.gameObject.SetActive(false);
                Debug.Log($"[RhythmDartMR] Average {1f / _smoothedFrameTime:0} FPS — switched VFX to Low quality " +
                          "(fewer particles, no halo). Set VfxSettings.AutoQuality = false to disable.");
            }
        }

        // ------------------------------------------------------------------ wiring

        private void HookEvents()
        {
            var score = ScoreAndComboManager.Instance;
            if (score != _hookedScore)
            {
                if (_hookedScore != null) _hookedScore.OnDartJudged -= HandleDartJudged;
                _hookedScore = score;
                if (score != null) score.OnDartJudged += HandleDartJudged;
            }

            var conductor = BeatConductor.Instance;
            if (conductor != _hookedConductor)
            {
                if (_hookedConductor != null) _hookedConductor.OnTempoChanged -= HandleTempoChanged;
                _hookedConductor = conductor;
                if (conductor != null)
                {
                    conductor.OnTempoChanged += HandleTempoChanged;
                    _lastBpm = conductor.CurrentBpm;
                }
            }

            var buffs = BuffSystem.Instance;
            if (buffs != _hookedBuffs)
            {
                if (_hookedBuffs != null) _hookedBuffs.OnBuffChosen -= HandleBuffChosen;
                _hookedBuffs = buffs;
                if (buffs != null) buffs.OnBuffChosen += HandleBuffChosen;
            }
        }

        // ------------------------------------------------------------------ beat rim

        private void EnsureRim()
        {
            var board = RhythmDartboard.Instance;
            if (board == null || (board == _rimBoard && _rim != null)) return;

            _rimBoard = board;
            float r = board.boardRadius;
            _halo = VfxFactory.CreateRing(board.transform, "VFX_BeatHalo", r * 1.035f, 0.04f, VfxResources.Cyan, 128, -0.005f);
            if (VfxSettings.LowQuality) _halo.gameObject.SetActive(false);
            _rim = VfxFactory.CreateRing(board.transform, "VFX_BeatRim", r * 0.995f, 0.010f, VfxResources.Cyan, 128, -0.006f);
        }

        private void AnimateRim()
        {
            var board = RhythmDartboard.Instance;
            if (board == null || _rim == null) return;

            var conductor = BeatConductor.Instance;
            bool running = conductor != null && conductor.IsRunning;
            bool roundActive = GameSessionManager.Instance == null || GameSessionManager.Instance.IsRoundActive;

            float pulse;
            if (running)
            {
                double beat = conductor.CurrentBeat;
                int whole = (int)Math.Floor(beat);
                float phase = (float)(beat - Math.Floor(beat));
                bool downbeat = whole >= 0 && whole % 4 == 0;
                pulse = Mathf.Exp(-phase * 5.5f) * (downbeat ? 1f : 0.6f);

                if (whole != _lastWholeBeat)
                {
                    _lastWholeBeat = whole;
                    bool pausedNow = GameSessionManager.Instance != null && GameSessionManager.Instance.IsPaused;
                    if (downbeat && roundActive && !pausedNow) SpawnDownbeatRipple(board);
                }
            }
            else
            {
                pulse = 0.25f + 0.2f * Mathf.Sin(Time.time * 2f); // idle "breathing"
            }

            _flash = Mathf.MoveTowards(_flash, 0f, Time.deltaTime * 2.5f);

            int combo = ScoreAndComboManager.Instance != null ? ScoreAndComboManager.Instance.CurrentCombo : 0;
            Color tier = VfxResources.ComboColor(combo);
            var session = GameSessionManager.Instance;
            bool paused = session != null && session.IsPaused;
            if (session != null && roundActive && !paused && session.TimeRemainingSeconds <= 10f)
            {
                tier = Color.Lerp(UiTheme.Danger, Color.white, 0.2f * pulse); // final stretch: red alert rim
            }
            float energy = Mathf.Clamp01(pulse + _flash);
            float dim = !roundActive ? 0.45f : paused ? 0.3f : 1f;

            Color rimCol = Color.Lerp(tier, Color.white, _flash * 0.7f);
            rimCol.a = (0.45f + 0.55f * energy) * dim;
            _rim.widthMultiplier = 0.008f + 0.012f * energy;
            VfxFactory.SetColor(_rim, rimCol);

            if (_halo != null && _halo.gameObject.activeSelf)
            {
                Color haloCol = tier;
                haloCol.a = (0.10f + 0.30f * energy) * dim;
                _halo.widthMultiplier = 0.03f + 0.06f * energy;
                VfxFactory.SetColor(_halo, haloCol);
            }

            if (roundActive && combo >= VfxResources.ComboTierFever) EmitFeverSparks(board);
        }

        private void SpawnDownbeatRipple(RhythmDartboard board)
        {
            int combo = ScoreAndComboManager.Instance != null ? ScoreAndComboManager.Instance.CurrentCombo : 0;
            Color c = VfxResources.ComboColor(combo);
            c.a = 0.55f;
            float r = board.WorldBoardRadius;
            VfxFactory.SpawnShockwave(BoardPoint(board, Vector2.zero, -0.004f), board.transform.rotation,
                r, r * 1.3f, 0.025f, c, 0.6f);
        }

        private void EmitFeverSparks(RhythmDartboard board)
        {
            if (Random.value > (VfxSettings.LowQuality ? 0.25f : 0.6f)) return;
            float r = board.boardRadius;
            float a = Random.value * Mathf.PI * 2f;
            Vector2 dir2 = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
            Vector3 pos = BoardPoint(board, dir2 * r, -0.01f);
            Vector3 vel = board.transform.TransformDirection(new Vector3(dir2.x, dir2.y, 0f)) * 0.35f
                          - board.transform.forward * 0.15f;
            Color c = Color.HSVToRGB(Mathf.Repeat(a / (Mathf.PI * 2f) + Time.time * 0.3f, 1f), 0.7f, 1f);
            VfxPools.Glow(pos, vel, c, Random.Range(0.015f, 0.03f), Random.Range(0.4f, 0.7f));
        }

        // ------------------------------------------------------------------ impacts

        private void HandleDartJudged(HitJudgmentResult res, Vector3 hitPos)
        {
            var board = RhythmDartboard.Instance;
            Quaternion rot = board != null ? board.transform.rotation : Quaternion.identity;
            Vector3 boardCenter = board != null ? board.transform.position : RhythmDartboard.DefaultBoardPosition;
            Vector3 boardForward = board != null ? board.transform.forward : Vector3.forward;
            Vector3 normal = -boardForward; // towards the player
            float outerRadius = board != null ? board.WorldBoardRadius : (0.72f * RhythmDartboard.DefaultBoardScale);

            // Project hit position onto the board plane to get 2D outward radial direction
            Vector3 inPlane = Vector3.ProjectOnPlane(hitPos - boardCenter, boardForward);
            Vector3 dir;
            if (inPlane.sqrMagnitude < 0.0016f)
            {
                // Dead-center bullseye: alternate left/right upward cleanly outside top quadrants
                _bullseyeAltSide = !_bullseyeAltSide;
                Vector3 localDir = _bullseyeAltSide ? new Vector3(-0.7071f, 0.7071f, 0f) : new Vector3(0.7071f, 0.7071f, 0f);
                dir = board != null ? board.transform.TransformDirection(localDir).normalized : localDir;
            }
            else
            {
                dir = inPlane.normalized;
            }

            // Redirect popups away from the bottom-right throwing hand zone to the top/left exterior rim
            if (dir.x > 0.10f && dir.y < 0.20f)
            {
                dir = board != null
                    ? board.transform.TransformDirection(new Vector3(-0.65f, 0.76f, 0f)).normalized
                    : new Vector3(-0.65f, 0.76f, 0f).normalized;
            }

            // Always project all hit VFX & floating score text 12cm outside the dartboard rim (0 overlap inside board)
            Vector3 exteriorSpawnPos = boardCenter + dir * (outerRadius + 0.12f);
            Vector3 p = exteriorSpawnPos + normal * 0.035f;
            Vector3 chipPos = exteriorSpawnPos + normal * 0.045f;

            bool miss = res.pointsAwarded <= 0;
            bool perfect = res.matchedActiveBeatZone;
            bool bigZone = res.zoneType == HitZoneType.Triple || res.zoneType == HitZoneType.Double ||
                           res.zoneType == HitZoneType.InnerBullseye || res.zoneType == HitZoneType.OuterBullseye;

            if (miss)
            {
                VfxFactory.RingSparks(p, rot, 0.035f, VfxResources.MissGrey, 10, 0.45f, 0.012f, 0.22f);
                VfxFactory.SpawnShockwave(p, rot, 0.040f, 0.095f, 0.008f, new Color(1f, 0.4f, 0.4f, 0.65f), 0.22f);
                if (res.comboShielded)
                    VfxFactory.SpawnScoreChip(chipPos, "SHIELD", "COMBO SAVED", UiTheme.Accent, 0.9f, 0.45f);
                return;
            }

            Color c = res.triggeredPowerUp ? UiTheme.Gold : perfect ? VfxResources.Gold : VfxResources.ComboColor(res.currentCombo);

            // 1. Exterior hollow radial shockwave + spark burst (kept outside 0.72m board perimeter, max 0.25s)
            VfxFactory.RingSparks(p, rot, 0.040f, c, perfect ? 36 : bigZone ? 24 : 14, perfect ? 1.5f : 1.0f, 0.014f, 0.22f);
            VfxFactory.SpawnShockwave(p, rot, 0.035f, 0.11f, 0.014f, c, 0.22f);

            if (perfect)
            {
                Color ring2 = Color.white;
                ring2.a = 0.85f;
                VfxFactory.SpawnShockwave(p, rot, 0.045f, 0.11f, 0.018f, ring2, 0.25f);
                VfxFactory.RadialStreaks(p, rot, VfxResources.Gold, 12, 2.0f, 0.018f, 0.25f);
                _flash = 1f;
            }
            else if (bigZone)
            {
                VfxFactory.RadialStreaks(p, rot, c, 8, 1.4f, 0.014f, 0.22f);
                _flash = Mathf.Max(_flash, 0.5f);
            }

            if (res.triggeredPowerUp && board != null)
            {
                Vector3 center = BoardPoint(board, Vector2.zero, -0.01f);
                VfxFactory.SpawnShockwave(center, rot, outerRadius + 0.04f, outerRadius * 1.45f, 0.04f, UiTheme.Violet, 0.25f);
                VfxFactory.RingSparks(center, rot, outerRadius + 0.04f, UiTheme.Gold, 60, 0.7f, 0.03f, 0.25f);
            }

            // 2. Score chip (starts 12cm outside rim and moves outward at 0.25m/s over 0.45s)
            string label = ZoneLabel(res) ?? (res.sectorNumber > 0 ? $"SINGLE {res.sectorNumber}" : "HIT");
            if (res.triggeredPowerUp) label = "POWER-UP";
            else if (perfect) label = $"PERFECT \u00B7 {label}";
            if (res.pointsMultiplier > 1.01f) label += " \u00B7 2X";
            Color accent = perfect || res.triggeredPowerUp ? UiTheme.Gold : Color.Lerp(c, Color.white, 0.2f);
            VfxFactory.SpawnScoreChip(chipPos, label, $"+{res.pointsAwarded:N0}", accent,
                perfect ? 1.20f : 1f, 0.45f);

            // 3. Combo milestones
            CelebrateMilestone(res.currentCombo, board);
        }

        private void CelebrateMilestone(int combo, RhythmDartboard board)
        {
            if (board == null) return;

            string headline = null;
            if (combo == VfxResources.ComboTierHot) headline = "HEATING UP";
            else if (combo == VfxResources.ComboTierBlazing) headline = "IN THE ZONE";
            else if (combo == VfxResources.ComboTierFever) headline = "UNSTOPPABLE";
            else if (combo > VfxResources.ComboTierFever && combo % 5 == 0) headline = $"{combo} IN A ROW";
            if (headline == null) return;

            float r = board.WorldBoardRadius;
            Color c = VfxResources.ComboColor(combo);
            Vector3 center = BoardPoint(board, Vector2.zero, -0.01f);
            VfxFactory.SpawnShockwave(center, board.transform.rotation, r * 0.98f, r * 1.6f, 0.05f, c, 0.8f);
            VfxFactory.RingSparks(center, board.transform.rotation, r, c, 48, 0.6f, 0.03f, 0.6f);
            SpawnBoardCallout(board, $"COMBO \u00D7{combo}", headline, c);
            _flash = 1f;
        }

        // ------------------------------------------------------------------ tempo / round / power-ups

        private void HandleTempoChanged(float bpm)
        {
            bool increased = _lastBpm > 0f && bpm > _lastBpm + 0.5f;
            _lastBpm = bpm;
            if (!increased) return;

            var board = RhythmDartboard.Instance;
            if (board == null) return;

            float r = board.WorldBoardRadius;
            Vector3 center = BoardPoint(board, Vector2.zero, -0.01f);
            Quaternion rot = board.transform.rotation;
            VfxFactory.SpawnShockwave(center, rot, r, r * 1.8f, 0.06f, VfxResources.Orange, 0.9f);
            VfxFactory.SpawnShockwave(center, rot, r * 0.9f, r * 1.4f, 0.03f, Color.white, 0.6f);
            VfxFactory.RingSparks(center, rot, r, VfxResources.Orange, 64, 0.8f, 0.03f, 0.7f);
            SpawnBoardCallout(board, "TEMPO UP", $"{bpm:0} BPM", UiTheme.Orange);
            _flash = 1f;
        }

        private void HandleBuffChosen(BuffDefinition def)
        {
            var board = RhythmDartboard.Instance;
            if (board == null) return;
            Color c = BuffSystem.RarityColor(def.Rarity);
            float r = board.WorldBoardRadius;
            Vector3 center = BoardPoint(board, Vector2.zero, -0.01f);
            VfxFactory.SpawnShockwave(center, board.transform.rotation, r * 0.9f, r * 1.7f, 0.05f, c, 0.8f);
            _flash = 1f;
        }

        private bool _wasRoundActive;
        private bool _finalStretchShown;

        private void MonitorRoundState()
        {
            var session = GameSessionManager.Instance;
            var board = RhythmDartboard.Instance;
            if (session == null || board == null) return;

            bool active = session.IsRoundActive;
            if (active && !_wasRoundActive)
            {
                _finalStretchShown = false;
                VfxFactory.SpawnShockwave(BoardPoint(board, Vector2.zero, -0.01f), board.transform.rotation,
                    0.05f, board.WorldBoardRadius * 1.4f, 0.04f, UiTheme.Accent, 0.7f);
                _flash = 1f;
            }
            else if (!active && _wasRoundActive)
            {
                string headline = session.LastEndReason == SessionEndReason.TimeUp ? "TIME'S UP"
                    : session.LastEndReason == SessionEndReason.OutOfDarts ? "OUT OF DARTS"
                    : "ROUND OVER";
                SpawnBoardCallout(board, "ROUND COMPLETE", headline, UiTheme.Gold);
            }
            _wasRoundActive = active;

            if (active && !session.IsPaused)
            {
                float left = session.TimeRemainingSeconds;
                if (left > 11f) _finalStretchShown = false; // bonus time can re-arm it
                if (!_finalStretchShown && left <= 10f && left > 0f)
                {
                    _finalStretchShown = true;
                    SpawnBoardCallout(board, "FINAL STRETCH", "10 SECONDS", UiTheme.Danger);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private void SpawnBoardCallout(RhythmDartboard board, string eyebrow, string headline, Color accent)
        {
            Vector3 boardPos = board != null ? board.transform.position : RhythmDartboard.DefaultBoardPosition;
            float worldR = board != null ? board.WorldBoardRadius : (0.72f * RhythmDartboard.DefaultBoardScale);
            Vector3 calloutPos = new Vector3(boardPos.x, boardPos.y + worldR + 0.13f, boardPos.z - 0.02f);
            VfxFactory.SpawnCallout(calloutPos, eyebrow, headline, accent, 0.68f, 1.5f);
        }

        private static string FormatClock(float seconds)
        {
            int s = Mathf.CeilToInt(seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        private static Vector3 BoardPoint(RhythmDartboard board, Vector2 local, float localZ)
        {
            return board.transform.TransformPoint(new Vector3(local.x, local.y, localZ));
        }

        private static string ZoneLabel(HitJudgmentResult r)
        {
            switch (r.zoneType)
            {
                case HitZoneType.InnerBullseye: return "BULLSEYE";
                case HitZoneType.OuterBullseye: return "BULL";
                case HitZoneType.Triple: return $"TRIPLE {r.sectorNumber}";
                case HitZoneType.Double: return $"DOUBLE {r.sectorNumber}";
                default: return null;
            }
        }
    }
}