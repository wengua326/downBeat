using System.Collections.Generic;
using UnityEngine;

namespace RhythmDartMR.Audio
{
    public enum DrumVoiceType
    {
        SubBassKick,     // Bullseye: heavy sub-bass kick
        CrispSnare,      // Treble / Double multipliers: crisp high-frequency snare
        SynthStab        // Single segments: ambient synth stabs & melodic samples
    }

    /// <summary>
    /// Turns the dartboard into an expressive audio controller ("Darts as a Drum Pad").
    /// Synthesizes high-impact drum/synth samples and records on-beat throws into an
    /// evolving 8-step (1-bar) rhythm loop that scales with the track's BPM.
    /// </summary>
    public class DrumPadAudioEngine : MonoBehaviour
    {
        public static DrumPadAudioEngine Instance { get; private set; }

        [Header("Audio Sources")]
        public AudioSource backingLoopSource;
        public AudioSource oneShotPadSource;
        public AudioSource sequencedLoopSource;

        private AudioClip _backingBeatClip;
        private AudioClip _subBassKickClip;
        private AudioClip _crispSnareClip;
        private AudioClip[] _synthStabClips;
        private AudioClip _overdriveFanfareClip;

        private struct LoopEvent
        {
            public DrumVoiceType voice;
            public int sectorIndex;
            public float volume;
        }

        private const int LoopStepCount = 8; // 4 beats * 2 subdivisions (8th notes)
        private readonly List<LoopEvent>[] _loopSteps = new List<LoopEvent>[LoopStepCount];
        private int _lastPlayedHalfBeat = -1;
        private int _totalRecordedLayers = 0;

        // C Minor Pentatonic frequencies across 2 octaves for expressive Single Segment stabs
        private static readonly float[] PentatonicFreqs = new float[]
        {
            261.63f, // C4
            311.13f, // Eb4
            349.23f, // F4
            392.00f, // G4
            466.16f, // Bb4
            523.25f, // C5
            622.25f, // Eb5
            698.46f, // F5
            783.99f, // G5
            932.33f  // Bb5
        };

        public int ActiveLoopLayerCount => _totalRecordedLayers;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            for (int i = 0; i < LoopStepCount; i++)
            {
                _loopSteps[i] = new List<LoopEvent>();
            }

            EnsureAudioSources();
            SynthesizeAllClips();
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            for (int i = 0; i < LoopStepCount; i++)
            {
                if (_loopSteps[i] == null) _loopSteps[i] = new List<LoopEvent>();
            }
            if (BeatConductor.Instance != null)
            {
                BeatConductor.Instance.OnTempoChanged -= HandleTempoChanged;
                BeatConductor.Instance.OnTempoChanged += HandleTempoChanged;
            }
        }

        private void Start()
        {
            if (BeatConductor.Instance != null)
            {
                BeatConductor.Instance.OnTempoChanged -= HandleTempoChanged;
                BeatConductor.Instance.OnTempoChanged += HandleTempoChanged;
            }
        }

        private void OnDisable()
        {
            if (BeatConductor.Instance != null)
            {
                BeatConductor.Instance.OnTempoChanged -= HandleTempoChanged;
            }
        }

        private void EnsureAudioSources()
        {
            if (backingLoopSource == null)
            {
                var go = new GameObject("BackingLoopAudio");
                go.transform.SetParent(transform, false);
                backingLoopSource = go.AddComponent<AudioSource>();
                backingLoopSource.loop = true;
                backingLoopSource.playOnAwake = false;
                backingLoopSource.spatialBlend = 0f;
                backingLoopSource.volume = 0.55f;
            }

            if (oneShotPadSource == null)
            {
                var go = new GameObject("DrumPadOneShotAudio");
                go.transform.SetParent(transform, false);
                oneShotPadSource = go.AddComponent<AudioSource>();
                oneShotPadSource.loop = false;
                oneShotPadSource.playOnAwake = false;
                oneShotPadSource.spatialBlend = 0.25f;
                oneShotPadSource.volume = 0.95f;
            }

            if (sequencedLoopSource == null)
            {
                var go = new GameObject("EvolvingLoopAudio");
                go.transform.SetParent(transform, false);
                sequencedLoopSource = go.AddComponent<AudioSource>();
                sequencedLoopSource.loop = false;
                sequencedLoopSource.playOnAwake = false;
                sequencedLoopSource.spatialBlend = 0.15f;
                sequencedLoopSource.volume = 0.65f;
            }
        }

        public void StartBackingMusic()
        {
            ClearRecordedLoop();
            _lastPlayedHalfBeat = -1;
            backingLoopSource.clip = _backingBeatClip;
            backingLoopSource.pitch = 1f;
            backingLoopSource.Play();
        }

        public void StopBackingMusic()
        {
            if (backingLoopSource != null) backingLoopSource.Stop();
        }

        public void ClearRecordedLoop()
        {
            for (int i = 0; i < LoopStepCount; i++)
            {
                _loopSteps[i].Clear();
            }
            _totalRecordedLayers = 0;
        }

        private void HandleTempoChanged(float newBpm)
        {
            float baseBpm = BeatConductor.Instance != null ? BeatConductor.Instance.baseBpm : 100f;
            if (backingLoopSource != null)
            {
                backingLoopSource.pitch = (newBpm / Mathf.Max(1f, baseBpm)) * Mathf.Clamp(Time.timeScale, 0.1f, 4f);
            }
        }

        private void Update()
        {
            if (BeatConductor.Instance == null || !BeatConductor.Instance.IsRunning) return;

            if (backingLoopSource != null)
            {
                float baseBpm = Mathf.Max(1f, BeatConductor.Instance.baseBpm);
                float targetPitch = (BeatConductor.Instance.CurrentBpm / baseBpm) * Mathf.Clamp(Time.timeScale, 0.1f, 4f);
                if (!Mathf.Approximately(backingLoopSource.pitch, targetPitch))
                {
                    backingLoopSource.pitch = targetPitch;
                }
            }

            int currentHalfBeat = Mathf.FloorToInt((float)(BeatConductor.Instance.CurrentBeat * 2.0));
            if (currentHalfBeat > _lastPlayedHalfBeat && currentHalfBeat >= 0)
            {
                _lastPlayedHalfBeat = currentHalfBeat;
                int step = currentHalfBeat % LoopStepCount;
                var events = _loopSteps[step];
                for (int i = 0; i < events.Count; i++)
                {
                    PlayVoiceInternal(sequencedLoopSource, events[i].voice, events[i].sectorIndex, events[i].volume);
                }
            }
        }

        /// <summary>
        /// Fires the immediate drum pad sound for a dart hit and optionally records it into the evolving loop.
        /// </summary>
        public void TriggerDrumPadHit(DrumVoiceType voice, int sectorIndex, bool recordIntoLoop, bool isOverdrive)
        {
            float vol = isOverdrive ? 1.0f : 0.9f;
            PlayVoiceInternal(oneShotPadSource, voice, sectorIndex, vol);

            if (isOverdrive && voice == DrumVoiceType.SynthStab)
            {
                // Add harmonic fifth in Overdrive mode
                PlayVoiceInternal(oneShotPadSource, voice, (sectorIndex + 3) % 20, 0.6f);
            }

            if (recordIntoLoop && BeatConductor.Instance != null && BeatConductor.Instance.IsRunning)
            {
                int nearestHalfBeat = Mathf.RoundToInt((float)(BeatConductor.Instance.CurrentBeat * 2.0));
                int step = ((nearestHalfBeat % LoopStepCount) + LoopStepCount) % LoopStepCount;

                // Keep up to 3 layers per step so the loop stays punchy and clean
                if (_loopSteps[step].Count >= 3)
                {
                    _loopSteps[step].RemoveAt(0);
                }
                else
                {
                    _totalRecordedLayers++;
                }

                _loopSteps[step].Add(new LoopEvent
                {
                    voice = voice,
                    sectorIndex = sectorIndex,
                    volume = 0.5f
                });
            }
        }

        public void PlayOverdriveActivationSound()
        {
            if (oneShotPadSource != null && _overdriveFanfareClip != null)
            {
                oneShotPadSource.PlayOneShot(_overdriveFanfareClip, 0.9f);
            }
        }

        private void PlayVoiceInternal(AudioSource source, DrumVoiceType voice, int sectorIndex, float volume)
        {
            if (source == null) return;

            switch (voice)
            {
                case DrumVoiceType.SubBassKick:
                    source.PlayOneShot(_subBassKickClip, volume);
                    break;
                case DrumVoiceType.CrispSnare:
                    source.PlayOneShot(_crispSnareClip, volume);
                    break;
                case DrumVoiceType.SynthStab:
                    int noteIdx = Mathf.Abs(sectorIndex) % _synthStabClips.Length;
                    source.PlayOneShot(_synthStabClips[noteIdx], volume * 0.85f);
                    break;
            }
        }

        private void SynthesizeAllClips()
        {
            int sampleRate = 44100;
            _subBassKickClip = CreateSubBassKickClip(sampleRate);
            _crispSnareClip = CreateCrispSnareClip(sampleRate);

            _synthStabClips = new AudioClip[PentatonicFreqs.Length];
            for (int i = 0; i < PentatonicFreqs.Length; i++)
            {
                _synthStabClips[i] = CreateSynthStabClip(sampleRate, PentatonicFreqs[i], i);
            }

            _overdriveFanfareClip = CreateOverdriveChordClip(sampleRate);
            _backingBeatClip = CreateBaseBackingLoopClip(sampleRate, 100f);
        }

        private AudioClip CreateSubBassKickClip(int sampleRate)
        {
            int samples = (int)(sampleRate * 0.42f);
            float[] data = new float[samples];
            float phase = 0f;

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / sampleRate;
                // Exponential pitch drop from 135 Hz down to 42 Hz sub-bass
                float freq = 42f + 93f * Mathf.Exp(-t * 28f);
                phase += freq / sampleRate;
                float env = Mathf.Exp(-t * 6.5f);
                float click = Mathf.Exp(-t * 90f) * Mathf.Sin(t * 1800f);
                float sample = (Mathf.Sin(phase * Mathf.PI * 2f) * 0.9f + Mathf.Sin(phase * Mathf.PI * 4f) * 0.25f + click * 0.3f) * env;
                data[i] = Mathf.Clamp(sample, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create("BullseyeSubBassKick", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private AudioClip CreateCrispSnareClip(int sampleRate)
        {
            int samples = (int)(sampleRate * 0.28f);
            float[] data = new float[samples];
            System.Random rng = new System.Random(1337);
            float phase = 0f;

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / sampleRate;
                float bodyFreq = 185f + 120f * Mathf.Exp(-t * 35f);
                phase += bodyFreq / sampleRate;
                float body = Mathf.Sin(phase * Mathf.PI * 2f) * Mathf.Exp(-t * 18f);

                float noise = ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 14f);
                // High-frequency crisp clap transient
                float snap = Mathf.Sin(t * 3400f) * Mathf.Exp(-t * 45f) * 0.35f;
                data[i] = Mathf.Clamp(body * 0.55f + noise * 0.65f + snap, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create("TrebleCrispSnare", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private AudioClip CreateSynthStabClip(int sampleRate, float rootFreq, int index)
        {
            int samples = (int)(sampleRate * 0.36f);
            float[] data = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / sampleRate;
                float attack = Mathf.Clamp01(t / 0.012f);
                float decay = Mathf.Exp(-t * 7.5f);
                float env = attack * decay;

                // Detuned saw/triangle + fifth chord harmonic
                float w1 = Mathf.Sin(t * rootFreq * Mathf.PI * 2f);
                float w2 = Mathf.Sin(t * (rootFreq * 1.006f) * Mathf.PI * 2f);
                float fifth = Mathf.Sin(t * (rootFreq * 1.498f) * Mathf.PI * 2f) * 0.45f;
                float octave = Mathf.Sin(t * (rootFreq * 2.0f) * Mathf.PI * 2f) * 0.25f;

                data[i] = Mathf.Clamp((w1 + w2 + fifth + octave) * 0.42f * env, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create($"SingleSynthStab_{index}", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private AudioClip CreateOverdriveChordClip(int sampleRate)
        {
            int samples = (int)(sampleRate * 0.65f);
            float[] data = new float[samples];
            float[] chord = { 523.25f, 622.25f, 783.99f, 1046.50f }; // C minor9 power chord

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / sampleRate;
                float env = Mathf.Clamp01(t / 0.02f) * Mathf.Exp(-t * 3.8f);
                float sum = 0f;
                for (int c = 0; c < chord.Length; c++)
                {
                    sum += Mathf.Sin(t * chord[c] * Mathf.PI * 2f);
                }
                data[i] = Mathf.Clamp(sum * 0.25f * env, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create("OverdriveFanfare", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// Synthesizes a 4-bar (16-beat) 100 BPM backing groove so the player always has a clear rhythmic pulse.
        /// </summary>
        private AudioClip CreateBaseBackingLoopClip(int sampleRate, float bpm)
        {
            float secPerBeat = 60f / bpm; // 0.6s at 100 BPM
            int beats = 4;
            int totalSamples = Mathf.RoundToInt(sampleRate * secPerBeat * beats);
            float[] data = new float[totalSamples];
            System.Random rng = new System.Random(42);

            float[] bassNotes = { 65.41f, 65.41f, 77.78f, 58.27f }; // C2, C2, Eb2, Bb1

            for (int i = 0; i < totalSamples; i++)
            {
                float t = (float)i / sampleRate;
                float beatFloat = t / secPerBeat;
                int beatIdx = Mathf.FloorToInt(beatFloat) % beats;
                float beatFrac = beatFloat - Mathf.Floor(beatFloat);
                float beatTime = beatFrac * secPerBeat;

                // Four-on-the-floor pulse kick
                float kickFreq = 48f + 70f * Mathf.Exp(-beatTime * 30f);
                float kick = Mathf.Sin(beatTime * kickFreq * Mathf.PI * 2f) * Mathf.Exp(-beatTime * 9f) * 0.55f;

                // Snare/rim on beats 1 and 3 (0-indexed: 2nd and 4th beats)
                float snare = 0f;
                if (beatIdx == 1 || beatIdx == 3)
                {
                    float noise = ((float)rng.NextDouble() * 2f - 1f);
                    snare = (noise * 0.35f + Mathf.Sin(beatTime * 210f * Mathf.PI * 2f) * 0.25f) * Mathf.Exp(-beatTime * 16f);
                }

                // Closed hi-hat on 8th notes
                float eighthTime = (beatFrac % 0.5f) * secPerBeat;
                float hatNoise = ((float)rng.NextDouble() * 2f - 1f);
                float hat = hatNoise * Mathf.Exp(-eighthTime * 42f) * 0.16f;

                // Off-beat pumping sub-bass
                float offBeatPhase = Mathf.Clamp01((beatFrac - 0.35f) * 4f);
                float bassFreq = bassNotes[beatIdx];
                float bass = Mathf.Sin(t * bassFreq * Mathf.PI * 2f) * offBeatPhase * Mathf.Exp(-(1f - beatFrac) * 2.5f) * 0.32f;

                data[i] = Mathf.Clamp(kick + snare + hat + bass, -0.95f, 0.95f);
            }

            AudioClip clip = AudioClip.Create("BackingGroove_100BPM", totalSamples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
