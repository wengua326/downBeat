using System;
using UnityEngine;

namespace RhythmDartMR.Audio
{
    public enum TransientType
    {
        KickDownbeat,
        SnareBackbeat,
        SynthOffbeat
    }

    /// <summary>
    /// Sample-accurate beat clock driven strictly by AudioSettings.dspTime.
    /// Escalates tempo (100 -> 110 -> 125 -> 140 BPM) cleanly on 4-beat bar boundaries.
    /// </summary>
    public class BeatConductor : MonoBehaviour
    {
        public static BeatConductor Instance { get; private set; }

        [Header("Tempo Escalation (Bar-Aligned)")]
        public float baseBpm = 85f;
        public float[] bpmStages = new float[] { 85f, 90f, 95f, 100f };
        public float[] stageTimeThresholds = new float[] { 0f, 30f, 60f, 90f };

        [Header("Runtime State")]
        [SerializeField] private float currentBpm = 85f;
        [SerializeField] private float pendingBpm = 85f;
        [SerializeField] private int currentStageIndex = 0;

        private double _anchorDspTime;
        private double _anchorBeat;
        private double _roundStageAnchorDspTime;
        private double _accumulatedRoundSeconds;
        private double _lastUpdateDspTime;
        private float _timeScale = 1f;
        private bool _isRunning;

        private int _lastWholeBeat = -1;
        private int _lastHalfBeat = -1;
        private int _lastScheduledTargetHalfBeat = -1;

        public float CurrentBpm => currentBpm;
        public float EffectiveBpm => currentBpm * _timeScale;
        public double SecondsPerBeat => 60.0 / EffectiveBpm;
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Continuous beat counter since round start (0.0, 1.0, 2.0, ...).
        /// Piece-wise continuous across bar-aligned BPM changes and recording Time.timeScale changes.
        /// </summary>
        public double CurrentBeat
        {
            get
            {
                if (!_isRunning) return 0.0;
                double elapsed = AudioSettings.dspTime - _anchorDspTime;
                return _anchorBeat + elapsed * (EffectiveBpm / 60.0);
            }
        }

        /// <summary>
        /// Fractional phase within the current beat [0.0, 1.0).
        /// </summary>
        public float BeatPhase
        {
            get
            {
                double b = CurrentBeat;
                double f = b - Math.Floor(b);
                return (float)f;
            }
        }

        /// <summary>
        /// Elapsed seconds in the round using the DSP clock scaled seamlessly with Time.timeScale.
        /// </summary>
        public float ElapsedRoundSeconds
        {
            get
            {
                if (!_isRunning) return 0f;
                return (float)(_accumulatedRoundSeconds + (AudioSettings.dspTime - _roundStageAnchorDspTime) * _timeScale);
            }
        }

        public event Action<int, TransientType> OnBeatReached;
        public event Action<double, TransientType> OnScheduleUpcomingTarget;
        public event Action<float> OnTempoChanged;

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

        public void StartConductor()
        {
            double now = AudioSettings.dspTime + 0.1;
            _timeScale = Mathf.Clamp(Time.timeScale, 0.05f, 4f);
            _accumulatedRoundSeconds = 0.0;
            _roundStageAnchorDspTime = now;
            _lastUpdateDspTime = now;
            _anchorDspTime = now;
            _anchorBeat = 0.0;
            currentStageIndex = 0;
            currentBpm = bpmStages != null && bpmStages.Length > 0 ? bpmStages[0] : baseBpm;
            pendingBpm = currentBpm;
            _lastWholeBeat = -1;
            _lastHalfBeat = -1;
            _lastScheduledTargetHalfBeat = 1;
            _isRunning = true;

            OnTempoChanged?.Invoke(currentBpm);
        }

        public void StopConductor()
        {
            _isRunning = false;
        }

        private void SyncTimeScale()
        {
            float targetScale = Mathf.Clamp(Time.timeScale, 0.05f, 4f);
            if (Mathf.Approximately(targetScale, _timeScale)) return;

            double nowDsp = AudioSettings.dspTime;
            if (nowDsp >= _anchorDspTime)
            {
                _anchorBeat += (nowDsp - _anchorDspTime) * (EffectiveBpm / 60.0);
                _anchorDspTime = nowDsp;
            }
            if (nowDsp >= _roundStageAnchorDspTime)
            {
                _accumulatedRoundSeconds += (nowDsp - _roundStageAnchorDspTime) * _timeScale;
                _roundStageAnchorDspTime = nowDsp;
            }
            _timeScale = targetScale;
        }

        private void Update()
        {
            if (!_isRunning) return;

            SyncTimeScale();

            double nowDsp = AudioSettings.dspTime;
            if (nowDsp < _anchorDspTime) return;

            double dspDelta = nowDsp > _lastUpdateDspTime ? (nowDsp - _lastUpdateDspTime) : 0.0;
            _lastUpdateDspTime = nowDsp;

            // Stop round tempo-escalation clock completely while waiting for player to choose a power card
            var session = RhythmDartMR.UI.GameSessionManager.Instance;
            if (session != null && session.IsPaused)
            {
                _roundStageAnchorDspTime += dspDelta;
            }

            float elapsedSec = ElapsedRoundSeconds;
            for (int i = stageTimeThresholds.Length - 1; i >= 0; i--)
            {
                if (elapsedSec >= stageTimeThresholds[i])
                {
                    if (i > currentStageIndex)
                    {
                        currentStageIndex = i;
                        pendingBpm = bpmStages[Mathf.Clamp(i, 0, bpmStages.Length - 1)];
                    }
                    break;
                }
            }

            double beat = CurrentBeat;
            int wholeBeat = (int)Math.Floor(beat);
            int halfBeat = (int)Math.Floor(beat * 2.0);

            if (wholeBeat > _lastWholeBeat)
            {
                _lastWholeBeat = wholeBeat;

                // Apply pending tempo change strictly on 4-beat bar boundaries
                if (wholeBeat > 0 && (wholeBeat % 4 == 0) && !Mathf.Approximately(pendingBpm, currentBpm))
                {
                    ApplyTempoChangeAtBeat(wholeBeat, pendingBpm);
                }

                int beatInBar = wholeBeat % 4;
                TransientType transient = (beatInBar == 0)
                    ? TransientType.KickDownbeat
                    : (beatInBar == 1 || beatInBar == 3)
                        ? TransientType.SnareBackbeat
                        : TransientType.SynthOffbeat;

                OnBeatReached?.Invoke(wholeBeat, transient);
            }

            if (halfBeat > _lastHalfBeat)
            {
                _lastHalfBeat = halfBeat;
                if (halfBeat % 2 == 1 && currentStageIndex >= 2)
                {
                    // At higher BPM stages (125 / 140 BPM), also fire 8th-note synth transients
                    OnBeatReached?.Invoke(wholeBeat, TransientType.SynthOffbeat);
                }
            }

            // Schedule at most one slow target circle every 4 beats (1 bar), looking ahead 6 beats (~4.2s)
            int lookAheadHalfBeat = halfBeat + 12;
            while (_lastScheduledTargetHalfBeat < lookAheadHalfBeat)
            {
                _lastScheduledTargetHalfBeat++;
                // Only schedule on 4-beat bar boundaries (every 8 half-beats) to reduce circle count
                if (_lastScheduledTargetHalfBeat % 8 == 0)
                {
                    double targetBeatNumber = _lastScheduledTargetHalfBeat * 0.5;
                    int barIndex = ((int)Math.Floor(targetBeatNumber / 4.0)) % 3;
                    TransientType tType = barIndex == 0
                        ? TransientType.KickDownbeat
                        : barIndex == 1
                            ? TransientType.SnareBackbeat
                            : TransientType.SynthOffbeat;

                    OnScheduleUpcomingTarget?.Invoke(targetBeatNumber, tType);
                }
            }
        }

        private void ApplyTempoChangeAtBeat(int barBeatIndex, float newBpm)
        {
            double exactBarDspTime = _anchorDspTime + (barBeatIndex - _anchorBeat) * (60.0 / EffectiveBpm);
            _anchorDspTime = exactBarDspTime;
            _anchorBeat = barBeatIndex;
            currentBpm = newBpm;
            OnTempoChanged?.Invoke(currentBpm);
        }

        public double BeatToDspTime(double beatNumber)
        {
            return _anchorDspTime + (beatNumber - _anchorBeat) * (60.0 / EffectiveBpm);
        }

        /// <summary>
        /// Returns signed time difference in milliseconds between a DSP timestamp and a target beat.
        /// Negative = early, Positive = late.
        /// </summary>
        public float GetOffsetMsFromBeat(double dspTimestamp, double targetBeat)
        {
            double targetDsp = BeatToDspTime(targetBeat);
            return (float)((dspTimestamp - targetDsp) * 1000.0);
        }

        /// <summary>
        /// Returns absolute offset in milliseconds to the nearest whole or half beat.
        /// </summary>
        public float GetOffsetMsToNearestBeat(double dspTimestamp, out double nearestBeat)
        {
            double beatAtTime = _anchorBeat + (dspTimestamp - _anchorDspTime) * (EffectiveBpm / 60.0);
            nearestBeat = Math.Round(beatAtTime);
            double nearestBeatDsp = BeatToDspTime(nearestBeat);
            return (float)((dspTimestamp - nearestBeatDsp) * 1000.0);
        }
    }
}
