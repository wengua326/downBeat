using System;
using UnityEngine;
using RhythmDartMR.Audio;
using RhythmDartMR.UI;

namespace RhythmDartMR.Gameplay
{
    public enum TimingGrade
    {
        Perfect,
        Great,
        Good,
        Miss
    }

    public struct HitJudgmentResult
    {
        public TimingGrade timingGrade;
        public float timingOffsetMs;
        public int standardPoints;
        public int bonusPoints;
        public int pointsAwarded;
        public int sectorNumber;
        public int currentCombo;
        public float comboMultiplier;
        public bool matchedActiveBeatZone;
        public HitZoneType zoneType;
        public DrumVoiceType drumVoice;
        public bool recordedToLoop;
        public float pointsMultiplier;   // Overdrive buff (1 or 2)
        public bool triggeredPowerUp;    // a POWER circle was hit → power-up selection opens
        public bool comboShielded;       // a miss was absorbed by Combo Shield
    }

    /// <summary>
    /// Scores dart impacts strictly using the Standard Dart Rules:
    /// - Numbers (1-20): Single = 1x, Double Ring = 2x, Triple Ring = 3x
    /// - Outer Bullseye (Green): 25 points
    /// - Inner Bullseye (Red): 50 points
    /// - Hitting inside the active Circle adds +100 Bonus Marks!
    /// Also tracks Highest Score across 10-throw sessions.
    /// </summary>
    public class ScoreAndComboManager : MonoBehaviour
    {
        public static ScoreAndComboManager Instance { get; private set; }

        private const string HighScorePlayerPrefsKey = "RhythmDartMR_HighestSessionScore";

        [Header("Timing Windows (Milliseconds)")]
        public float perfectWindowMs = 60f;
        public float greatWindowMs = 110f;
        public float goodWindowMs = 180f;

        [Header("Overdrive Power-Up Settings")]
        public int perfectStreakForOverdrive = 3;
        public double overdriveDurationBeats = 8.0;

        public int CurrentScore { get; private set; }
        public int HighestPreviousSessionScore { get; private set; }
        public int CurrentCombo { get; private set; }
        public int MaxCombo { get; private set; }
        public int PerfectStreak { get; private set; }
        public bool IsOverdriveReady { get; private set; }
        public bool IsOverdriveActive { get; private set; }
        private double _overdriveEndBeat;

        // Round stats for the results screen
        public int DartsJudged { get; private set; }
        public int CirclesHit { get; private set; }
        public int PowerUpsTaken { get; private set; }

        public float ComboMultiplier => 1f;

        public event Action<HitJudgmentResult, Vector3> OnDartJudged;
        public event Action<bool> OnOverdriveStateChanged;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            HighestPreviousSessionScore = PlayerPrefs.GetInt(HighScorePlayerPrefsKey, 0);
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
        }

        public void RecordCompletedSessionScore()
        {
            if (CurrentScore > HighestPreviousSessionScore)
            {
                HighestPreviousSessionScore = CurrentScore;
                PlayerPrefs.SetInt(HighScorePlayerPrefsKey, HighestPreviousSessionScore);
                PlayerPrefs.Save();
            }
        }

        public void ResetStats()
        {
            CurrentScore = 0;
            CurrentCombo = 0;
            MaxCombo = 0;
            PerfectStreak = 0;
            IsOverdriveReady = false;
            IsOverdriveActive = false;
            OnOverdriveStateChanged?.Invoke(false);
            DartsJudged = 0;
            CirclesHit = 0;
            PowerUpsTaken = 0;
            if (RhythmDartboard.Instance != null)
            {
                RhythmDartboard.Instance.SetOverdriveVisual(false);
            }
        }

        public bool TryActivateOverdrive()
        {
            return false;
        }

        public HitJudgmentResult EvaluateDartImpact(Vector3 worldHitPos, double impactDspTime)
        {
            HitJudgmentResult result = new HitJudgmentResult();

            if (RhythmDartboard.Instance == null)
            {
                return result;
            }

            // While the power-up screen is open, darts pick a card instead of scoring.
            var buffs = BuffSystem.Instance;
            if (buffs != null && buffs.IsChoosing)
            {
                buffs.HandleSelectionHit(worldHitPos);
                return result;
            }

            DartsJudged++;
            float multiplier = buffs != null ? buffs.PointsMultiplier : 1f;
            if (buffs != null) buffs.ConsumeDartCharge();

            DartImpactEvaluation boardEval = RhythmDartboard.Instance.EvaluateImpact(worldHitPos, impactDspTime);
            if (!boardEval.hitBoard)
            {
                bool shielded = CurrentCombo > 0 && buffs != null && buffs.TryConsumeComboShield();
                if (!shielded) CurrentCombo = 0;
                PerfectStreak = 0;
                result.timingGrade = TimingGrade.Miss;
                result.zoneType = HitZoneType.Miss;
                result.standardPoints = 0;
                result.bonusPoints = 0;
                result.pointsAwarded = 0;
                result.currentCombo = CurrentCombo;
                result.pointsMultiplier = multiplier;
                result.comboShielded = shielded;
                OnDartJudged?.Invoke(result, worldHitPos);
                if (GameSessionManager.Instance != null)
                {
                    GameSessionManager.Instance.NotifyDartScored();
                }
                return result;
            }

            // Standard dart score + circle bonus (x3 with Jackpot), all x2 with Overdrive
            int circleBonus = 100 * (buffs != null ? buffs.CircleBonusMultiplier : 1);
            int standardPts = Mathf.RoundToInt(boardEval.basePoints * multiplier);
            int bonusPts = boardEval.matchedActiveBeatZone ? Mathf.RoundToInt(circleBonus * multiplier) : 0;
            int totalPoints = standardPts + bonusPts;
            bool powerUp = boardEval.consumedCue != null && boardEval.consumedCue.IsPowerCue;
            if (boardEval.matchedActiveBeatZone) CirclesHit++;
            if (powerUp) PowerUpsTaken++;

            CurrentScore += totalPoints;

            TimingGrade grade = boardEval.matchedActiveBeatZone
                ? TimingGrade.Perfect
                : standardPts > 0
                    ? TimingGrade.Great
                    : TimingGrade.Miss;

            if (boardEval.consumedCue != null)
            {
                boardEval.consumedCue.ConsumeOnHit(grade);
            }

            bool comboShielded = false;
            if (totalPoints > 0)
            {
                CurrentCombo++;
                if (CurrentCombo > MaxCombo) MaxCombo = CurrentCombo;
            }
            else
            {
                comboShielded = CurrentCombo > 0 && buffs != null && buffs.TryConsumeComboShield();
                if (!comboShielded) CurrentCombo = 0;
            }

            bool recordLoop = boardEval.matchedActiveBeatZone;
            if (DrumPadAudioEngine.Instance != null && standardPts > 0)
            {
                DrumPadAudioEngine.Instance.TriggerDrumPadHit(
                    boardEval.drumVoice,
                    boardEval.sectorIndex,
                    recordLoop,
                    false);
            }

            result.timingGrade = grade;
            result.timingOffsetMs = 0f;
            result.standardPoints = standardPts;
            result.bonusPoints = bonusPts;
            result.pointsAwarded = totalPoints;
            result.sectorNumber = boardEval.sectorNumber;
            result.currentCombo = CurrentCombo;
            result.comboMultiplier = 1f;
            result.matchedActiveBeatZone = boardEval.matchedActiveBeatZone;
            result.zoneType = boardEval.zoneType;
            result.drumVoice = boardEval.drumVoice;
            result.recordedToLoop = recordLoop;
            result.pointsMultiplier = multiplier;
            result.triggeredPowerUp = powerUp;
            result.comboShielded = comboShielded;

            OnDartJudged?.Invoke(result, worldHitPos);

            // POWER circle → pause and offer 3 power-ups (after the hit effects have been triggered)
            if (powerUp && buffs != null)
            {
                buffs.OfferChoice();
            }

            if (GameSessionManager.Instance != null)
            {
                GameSessionManager.Instance.NotifyDartScored();
            }

            return result;
        }
    }
}
