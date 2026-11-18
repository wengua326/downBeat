using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RhythmDartMR.Audio;

namespace RhythmDartMR.Gameplay
{
    public enum HitZoneType
    {
        InnerBullseye,
        OuterBullseye,
        Bullseye,
        Triple,
        Double,
        Single,
        Miss
    }

    public struct DartImpactEvaluation
    {
        public bool hitBoard;
        public HitZoneType zoneType;
        public int sectorNumber;
        public int sectorIndex;
        public int basePoints;
        public int circleBonusPoints;
        public bool matchedActiveBeatZone;
        public double targetBeat;
        public DrumVoiceType drumVoice;
        public ApproachCircleCue consumedCue;
    }

    /// <summary>
    /// Large Real-Image Traditional Dartboard (1.44m diameter) with Standard Dart Rules
    /// (Inner Bull 50, Outer Bull 25, Triple 3x, Double 2x, Single 1x) + 100 Bonus Marks when hitting the active circle.
    /// </summary>
    public class RhythmDartboard : MonoBehaviour
    {
        public static RhythmDartboard Instance { get; private set; }

        public static readonly Vector3 DefaultBoardPosition = new Vector3(-0.96f, 1.32f, 2.50f);
        public const float DefaultBoardScale = 0.52f;
        public float WorldBoardRadius => boardRadius * transform.lossyScale.y;

        // Clockwise from 12 o'clock (Standard Tournament Dartboard Order)
        public static readonly int[] SectorNumbers = new int[]
        {
            20, 1, 18, 4, 13, 6, 10, 15, 2, 17,
            3, 19, 7, 16, 8, 11, 14, 9, 12, 5
        };

        private static readonly string SimulatorRoomsDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            @"MetaXRSimulator\v207.1\assets\RKJSModules\public\Cosmo\Shared\assets\rooms");

        [Header("Large Board Dimensions (Meters) — Matched to Real Dartboard Image Proportions")]
        public float boardRadius = 0.72f;           // 1.44m total diameter
        public float innerBullRadius = 0.035f;      // Red Inner Bullseye = 50 pts (double)
        public float bullseyeRadius = 0.078f;       // Green Outer Bullseye = 25 pts (single)
        public float tripleInnerRadius = 0.298f;    // Inner edge of Triple ring (3x)
        public float tripleOuterRadius = 0.345f;    // Outer edge of Triple ring (3x)
        public float doubleInnerRadius = 0.505f;    // Inner edge of Double ring (2x)
        public float doubleOuterRadius = 0.552f;    // Outer edge of Double ring (2x)

        [Header("Art Style")]
        [Tooltip("Neon arcade look (glowing procedural board + neon dart). Untick for the realistic photo dartboard and brass dart.")]
        public bool neonArtStyle = true;

        [Header("Bonus Circle Spawning")]
        [Tooltip("Spawn each bonus circle on a random sector (never right next to the previous one) instead of a fixed pattern.")]
        public bool randomizeTargetPositions = true;
        [Tooltip("Beats for the approach ring to shrink onto the target (0.85x speed = ~17.6% longer than 5.625f).")]
        [Range(2f, 10f)] public float circleApproachBeats = 6.62f;
        [Tooltip("Extra beats the closed target stays hittable before it fades out (0 = destroy immediately on close).")]
        [Range(0f, 4f)] public float circleHoldBeats = 0f;
        private int _lastRandomSector = -10;

        [Header("Multi-Target Spawning")]
        [Tooltip("Circles allowed on the board at the start of a round.")]
        [Range(1, 6)] public int startMaxTargets = 1;
        [Tooltip("Circles allowed on the board once the ramp has finished.")]
        [Range(1, 6)] public int endMaxTargets = 1;
        [Tooltip("Seconds of play to go from start to end target count.")]
        public float targetRampSeconds = 90f;
        [Tooltip("Interval between normal circle spawns (1 per 3 seconds).")]
        public float minSpawnGapSeconds = 3.0f;

        [Header("Power Circles (hit one → choose a power-up)")]
        [Range(0f, 1f)] public float powerCircleChance = 1.0f;
        public float firstPowerCircleAfterSeconds = 30f;
        public float powerCircleCooldownSeconds = 30f;

        private float _nextNormalSpawnSessionTime = 0.5f;
        private float _nextPowerSpawnSessionTime = 30f;

        public static readonly Color SingleColor = new Color(0.30f, 0.88f, 1.00f);
        public static readonly Color DoubleColor = new Color(1.00f, 0.58f, 0.20f);
        public static readonly Color TripleColor = new Color(1.00f, 0.34f, 0.80f);
        public static readonly Color BullColor = new Color(1.00f, 0.84f, 0.30f);
        public static readonly Color PowerColor = new Color(0.70f, 0.50f, 1.00f);

        private readonly List<ApproachCircleCue> _activeCues = new List<ApproachCircleCue>();
        private readonly List<GameObject> _stuckDarts = new List<GameObject>();
        private Vector2 _lastSpawnLocalCenter = new Vector2(-999f, -999f);
        private Vector2 _lastHitLocalPos = new Vector2(-999f, -999f);
        private float _lastHitTime = -999f;
        private float _spawnDelayUntilTime = -999f;

        private static Material _sharedUrpMaterialTemplate;
        private Texture2D _boardTexture;
        private Renderer _envRoomRenderer;
        private bool _simulatorEnvHiddenByStudio = true;
        private string _currentEnvRoomKey = "";
        private float _nextEnvPollTime = 0f;
        private const int EnvLogMaxReadBytes = 131072;
        private long _envLogReadPos = -1;
        private byte[] _envLogBuffer;
        private readonly Dictionary<string, Texture2D> _loadedEnvTextures = new Dictionary<string, Texture2D>();

        public IReadOnlyList<ApproachCircleCue> ActiveCues => _activeCues;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Enforce calibrated real-image dartboard proportions
            boardRadius = 0.72f;
            innerBullRadius = 0.035f;
            bullseyeRadius = 0.078f;
            tripleInnerRadius = 0.298f;
            tripleOuterRadius = 0.345f;
            doubleInnerRadius = 0.505f;
            doubleOuterRadius = 0.552f;
            transform.position = DefaultBoardPosition;
            transform.localScale = Vector3.one * DefaultBoardScale;

            // Enforce calibrated single-target spawn rates & 0.85x slower shrink speed
            circleApproachBeats = 6.62f;
            circleHoldBeats = 0f;
            startMaxTargets = 1;
            endMaxTargets = 1;
            minSpawnGapSeconds = 3.0f;
            powerCircleChance = 1.0f;
            firstPowerCircleAfterSeconds = 30f;
            powerCircleCooldownSeconds = 30f;
            _nextNormalSpawnSessionTime = 0.5f;
            _nextPowerSpawnSessionTime = 30f;

            BuildRealDartboardVisuals();
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            transform.position = DefaultBoardPosition;
            transform.localScale = Vector3.one * DefaultBoardScale;
        }

        private void Update()
        {
            if (Instance == null) Instance = this;
            if ((transform.position - DefaultBoardPosition).sqrMagnitude > 0.000001f)
            {
                transform.position = DefaultBoardPosition;
            }
            if (Mathf.Abs(transform.localScale.x - DefaultBoardScale) > 0.0001f)
            {
                transform.localScale = Vector3.one * DefaultBoardScale;
            }

            for (int i = _activeCues.Count - 1; i >= 0; i--)
            {
                if (_activeCues[i] == null || _activeCues[i].IsConsumed)
                {
                    _activeCues.RemoveAt(i);
                }
            }

            UpdateSpawner();

            if (Application.isEditor && Time.time >= _nextEnvPollTime)
            {
                _nextEnvPollTime = Time.time + 1.0f;
                SyncSimulatorChosenEnvironment();
            }
        }

        public void SetOverdriveVisual(bool active)
        {
        }

        /// <summary>Toggles the Meta XR Simulator synthetic room backdrop visibility for Demo Recording Mode.</summary>
        public void SetSimulatorEnvironmentVisible(bool visible)
        {
            _simulatorEnvHiddenByStudio = !visible;
            if (_envRoomRenderer != null && _envRoomRenderer.gameObject != null &&
                _envRoomRenderer.gameObject.scene.isLoaded &&
                _envRoomRenderer.gameObject.activeSelf != visible)
            {
                _envRoomRenderer.gameObject.SetActive(visible);
            }
        }

        public void ClearAllCuesAndDarts()
        {
            for (int i = 0; i < _activeCues.Count; i++)
            {
                if (_activeCues[i] != null) Destroy(_activeCues[i].gameObject);
            }
            _activeCues.Clear();

            ClearStuckDarts();

            var session = UI.GameSessionManager.Instance;
            if ((BuffSystem.Instance == null || !BuffSystem.Instance.IsChoosing) &&
                (session == null || !session.IsRoundActive || session.ElapsedSessionSeconds < 0.5f))
            {
                _nextNormalSpawnSessionTime = 0.5f;
                _nextPowerSpawnSessionTime = firstPowerCircleAfterSeconds;
                _lastRandomSector = -10;
                _lastSpawnLocalCenter = new Vector2(-999f, -999f);
                _lastHitLocalPos = new Vector2(-999f, -999f);
                _lastHitTime = -999f;
                _spawnDelayUntilTime = -999f;
            }
        }

        private void ClearStuckDarts()
        {
            for (int i = 0; i < _stuckDarts.Count; i++)
            {
                if (_stuckDarts[i] != null) Destroy(_stuckDarts[i]);
            }
            _stuckDarts.Clear();
        }

        public void RegisterStuckDart(GameObject dartObj)
        {
            if (dartObj == null) return;
            _stuckDarts.Add(dartObj);
            Destroy(dartObj, 0.04f);
        }

        public Material CreateSafeMaterial(Color color, Texture2D tex = null, float emissionBoost = 0.45f)
        {
            return CreateSafeMaterialStatic(color, tex, emissionBoost);
        }

        public static Material CreateSafeMaterialStatic(Color color, Texture tex = null, float emissionBoost = 0.45f)
        {
            if (_sharedUrpMaterialTemplate == null)
            {
                var tempPrim = GameObject.CreatePrimitive(PrimitiveType.Quad);
                var rend = tempPrim.GetComponent<Renderer>();
                if (rend != null && rend.sharedMaterial != null)
                {
                    _sharedUrpMaterialTemplate = new Material(rend.sharedMaterial);
                }
                if (Application.isPlaying)
                    Destroy(tempPrim);
                else
                    DestroyImmediate(tempPrim);
            }

            var mat = _sharedUrpMaterialTemplate != null
                ? new Material(_sharedUrpMaterialTemplate)
                : new Material(Shader.Find("Sprites/Default"));

            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.0f);

            if (tex != null)
            {
                mat.mainTexture = tex;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }

            if (emissionBoost > 0f && mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                Color emCol = new Color(color.r * emissionBoost, color.g * emissionBoost, color.b * emissionBoost, 1f);
                mat.SetColor("_EmissionColor", emCol);
                if (tex != null && mat.HasProperty("_EmissionMap"))
                {
                    mat.SetTexture("_EmissionMap", tex);
                }
            }

            return mat;
        }

        // ------------------------------------------------------------------ multi-target spawner

        /// <summary>Strictly 1 active target on the board at any moment.</summary>
        public int MaxTargetsNow => 1;

        private void UpdateSpawner()
        {
            var session = UI.GameSessionManager.Instance;
            if (session != null && (!session.IsRoundActive || session.IsPaused)) return;
            var conductor = BeatConductor.Instance;
            if (conductor == null || !conductor.IsRunning) return;

            double beat = conductor.CurrentBeat;
            if (beat < 0.5) return;

            float elapsed = session != null ? session.ElapsedSessionSeconds : Time.timeSinceLevelLoad;
            if (elapsed < 0.5f && _nextPowerSpawnSessionTime > firstPowerCircleAfterSeconds + 1f)
            {
                _nextNormalSpawnSessionTime = 0.5f;
                _nextPowerSpawnSessionTime = firstPowerCircleAfterSeconds;
            }

            if (Time.time < _spawnDelayUntilTime) return;
            if (_activeCues.Count >= MaxTargetsNow) return;

            // 1. Purple power triangle: deterministically 1 per 30 seconds
            if (elapsed >= _nextPowerSpawnSessionTime && !HasActivePowerCue())
            {
                if (TrySpawnCue(beat, true))
                {
                    _nextPowerSpawnSessionTime = elapsed + powerCircleCooldownSeconds;
                    return;
                }
            }

            // 2. Normal circle: 1 per 3 seconds (accelerated when TargetRush / FASTER SPAWN buff is active)
            bool fastSpawn = BuffSystem.Instance != null && BuffSystem.Instance.IsTargetRushActive;
            float normalInterval = fastSpawn
                ? minSpawnGapSeconds * 0.5f
                : minSpawnGapSeconds;

            if (elapsed >= _nextNormalSpawnSessionTime && _activeCues.Count < MaxTargetsNow)
            {
                if (TrySpawnCue(beat, false))
                {
                    _nextNormalSpawnSessionTime = elapsed + normalInterval;
                }
            }
        }

        private bool TrySpawnCue(double spawnBeat, bool forcePower)
        {
            float sizeMul = BuffSystem.Instance != null ? BuffSystem.Instance.CircleSizeMultiplier : 1f;
            bool power = forcePower;

            for (int attempt = 0; attempt < 24; attempt++)
            {
                HitZoneType zoneType;
                int sectorIndex = PickRandomSector(_lastRandomSector);
                Vector3 center;
                float radius;
                Color color;

                float roll = Random.value;
                if (roll < 0.30f)
                {
                    zoneType = HitZoneType.Triple;
                    center = GetSectorLocalPosition(sectorIndex, (tripleInnerRadius + tripleOuterRadius) * 0.5f);
                    radius = 0.088f;
                    color = TripleColor;
                }
                else if (roll < 0.60f)
                {
                    zoneType = HitZoneType.Double;
                    center = GetSectorLocalPosition(sectorIndex, (doubleInnerRadius + doubleOuterRadius) * 0.5f);
                    radius = 0.092f;
                    color = DoubleColor;
                }
                else
                {
                    zoneType = HitZoneType.Single;
                    float outerSingleRad = Random.Range(tripleOuterRadius + 0.05f, doubleInnerRadius - 0.04f);
                    center = GetSectorLocalPosition(sectorIndex, outerSingleRad);
                    radius = 0.095f;
                    color = SingleColor;
                }

                radius *= sizeMul;
                if (!IsFreeSpot(center, radius)) continue;

                SpawnCueAt(spawnBeat, zoneType, sectorIndex, center, radius, power ? PowerColor : color, power);
                return true;
            }
            return false;
        }

        private void SpawnCueAt(double spawnBeat, HitZoneType zoneType, int sectorIndex, Vector3 localCenter,
            float radius, Color color, bool power)
        {
            for (int i = _activeCues.Count - 1; i >= 0; i--)
            {
                if (_activeCues[i] != null) Destroy(_activeCues[i].gameObject);
            }
            _activeCues.Clear();
            var leftoverCues = GetComponentsInChildren<ApproachCircleCue>(true);
            for (int i = 0; i < leftoverCues.Length; i++)
            {
                if (leftoverCues[i] != null) Destroy(leftoverCues[i].gameObject);
            }

            ClearStuckDarts();

            var cueObj = new GameObject(power ? "PowerCue" : $"Cue_{zoneType}");
            cueObj.transform.SetParent(transform, false);
            cueObj.transform.localPosition = new Vector3(localCenter.x, localCenter.y, -0.015f);
            cueObj.transform.localRotation = Quaternion.identity;

            float slow = BuffSystem.Instance != null ? BuffSystem.Instance.ApproachBeatsMultiplier : 1f;
            float baseApproach = power ? 5.294f : circleApproachBeats;
            ApproachCircleCue.ApproachBeats = baseApproach * slow;
            ApproachCircleCue.HoldBeats = 0f;

            var cue = cueObj.AddComponent<ApproachCircleCue>();
            cue.Initialize(spawnBeat, spawnBeat + ApproachCircleCue.ApproachBeats, radius, color, sectorIndex, zoneType, null, power);
            _activeCues.Add(cue);
            _lastRandomSector = sectorIndex;
            _lastSpawnLocalCenter = new Vector2(localCenter.x, localCenter.y);
        }

        /// <summary>
        /// Enforces strict opposite-half spawning (>= 110 deg angular separation, negative dot product,
        /// and >= 0.60m Euclidean separation) against previous spawn, active cues, and recent hit VFX.
        /// </summary>
        private bool IsFreeSpot(Vector3 localCenter, float radius)
        {
            Vector2 candidate = new Vector2(localCenter.x, localCenter.y);
            if (!IsOppositeSectorAndFarEnough(candidate, _lastSpawnLocalCenter)) return false;

            for (int i = 0; i < _activeCues.Count; i++)
            {
                var cue = _activeCues[i];
                if (cue == null) continue;
                Vector2 other = cue.transform.localPosition;
                if (!IsOppositeSectorAndFarEnough(candidate, other)) return false;
                float minDist = Mathf.Max(0.60f, (radius + cue.InnerRadius) * 1.25f + 0.03f);
                if (Vector2.Distance(candidate, other) < minDist) return false;
            }

            if (Time.time - _lastHitTime < 1.2f && _lastHitLocalPos.x > -100f)
            {
                if (_activeCues.Count == 0)
                {
                    if (!IsOppositeSectorAndFarEnough(candidate, _lastHitLocalPos)) return false;
                }
                else if (Vector2.Distance(candidate, _lastHitLocalPos) < 0.32f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsOppositeSectorAndFarEnough(Vector2 candidate, Vector2 reference)
        {
            if (reference.x < -100f) return true;
            if (reference.sqrMagnitude < 0.0001f)
            {
                return Vector2.Distance(candidate, reference) >= 0.30f;
            }
            if (candidate.sqrMagnitude < 0.0001f) return false;

            float candAngle = Mathf.Atan2(candidate.x, candidate.y) * Mathf.Rad2Deg;
            float refAngle = Mathf.Atan2(reference.x, reference.y) * Mathf.Rad2Deg;
            float angularSep = Mathf.Abs(Mathf.DeltaAngle(candAngle, refAngle));
            if (angularSep <= 120f) return false;

            if (Vector2.Dot(candidate.normalized, reference.normalized) >= 0f) return false;

            return Vector2.Distance(candidate, reference) >= 0.60f;
        }

        private bool HasActivePowerCue()
        {
            for (int i = 0; i < _activeCues.Count; i++)
            {
                if (_activeCues[i] != null && _activeCues[i].IsPowerCue) return true;
            }
            return false;
        }

        /// <summary>Picks a sector 7 to 13 steps (126-180 deg) away from <paramref name="avoidSector"/>.</summary>
        private static int PickRandomSector(int avoidSector)
        {
            if (avoidSector < 0)
            {
                return Random.Range(0, 20);
            }
            return (avoidSector + Random.Range(7, 14)) % 20;
        }

        public Vector3 GetSectorLocalPosition(int sectorIndex, float radius)
        {
            float angleDeg = sectorIndex * 18f;
            float angleRad = angleDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angleRad) * radius, Mathf.Cos(angleRad) * radius, 0f);
        }

        public Vector3 ApplyAimAssist(Vector3 rawWorldHitPoint, float assistRadius = 0.12f, float assistStrength = 0.35f)
        {
            float effectiveRadius = Mathf.Max(assistRadius, 0.30f);
            float effectiveStrength = Mathf.Max(assistStrength, 0.72f);

            ApproachCircleCue bestCue = null;
            float bestDist = effectiveRadius;

            for (int i = 0; i < _activeCues.Count; i++)
            {
                var cue = _activeCues[i];
                if (cue == null || cue.IsConsumed) continue;
                float d = Vector3.Distance(rawWorldHitPoint, cue.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestCue = cue;
                }
            }

            if (bestCue != null)
            {
                return Vector3.Lerp(rawWorldHitPoint, bestCue.transform.position, effectiveStrength);
            }

            return rawWorldHitPoint;
        }

        /// <summary>
        /// Evaluates a dart impact strictly following the Standard Dart Rules:
        /// - Inner Bullseye (Red Center): 50 points (counted as double)
        /// - Outer Bullseye (Green Ring): 25 points (counted as single)
        /// - Triple Ring: 3x Sector Number (e.g., Triple 20 = 60)
        /// - Double Ring: 2x Sector Number (e.g., Double 20 = 40)
        /// - Single Ring (Inner or Outer): 1x Sector Number (1-20)
        /// - Plus +100 Bonus Marks when hitting inside the active target circle!
        /// </summary>
        public DartImpactEvaluation EvaluateImpact(Vector3 worldHitPos, double impactDspTime)
        {
            Vector3 localPos = transform.InverseTransformPoint(worldHitPos);
            float radialDist = new Vector2(localPos.x, localPos.y).magnitude;

            DartImpactEvaluation eval = new DartImpactEvaluation();

            // Check active target hits first in board-local 2D coordinates (decoupled from exterior VFX placement)
            ApproachCircleCue matchedCue = null;
            float closestCueDist = float.MaxValue;

            for (int i = 0; i < _activeCues.Count; i++)
            {
                var cue = _activeCues[i];
                if (cue == null || cue.IsConsumed) continue;

                float distToCue = Vector2.Distance(
                    new Vector2(localPos.x, localPos.y),
                    new Vector2(cue.transform.localPosition.x, cue.transform.localPosition.y));

                float hitRadius = Mathf.Max(cue.InnerRadius * (cue.IsPowerCue ? 2.50f : 2.40f), 0.26f);
                if (distToCue <= hitRadius && distToCue < closestCueDist)
                {
                    closestCueDist = distToCue;
                    matchedCue = cue;
                }
            }

            if (radialDist > boardRadius * 1.15f && matchedCue == null)
            {
                eval.hitBoard = false;
                eval.zoneType = HitZoneType.Miss;
                eval.basePoints = 0;
                eval.circleBonusPoints = 0;
                return eval;
            }

            eval.hitBoard = true;
            _lastHitLocalPos = new Vector2(localPos.x, localPos.y);
            _lastHitTime = Time.time;
            _spawnDelayUntilTime = Mathf.Max(_spawnDelayUntilTime, Time.time + 0.18f);

            float angleDeg = Mathf.Atan2(localPos.x, localPos.y) * Mathf.Rad2Deg;
            if (angleDeg < 0f) angleDeg += 360f;
            int sectorIdx = Mathf.FloorToInt(((angleDeg + 9f) % 360f) / 18f);
            eval.sectorIndex = Mathf.Clamp(sectorIdx, 0, 19);
            eval.sectorNumber = SectorNumbers[eval.sectorIndex];

            // Standard Dartboard Scoring Rules
            if (radialDist <= innerBullRadius)
            {
                eval.zoneType = HitZoneType.InnerBullseye;
                eval.basePoints = 50; // Inner Bullseye = 50 points
                eval.drumVoice = DrumVoiceType.SubBassKick;
            }
            else if (radialDist <= bullseyeRadius)
            {
                eval.zoneType = HitZoneType.OuterBullseye;
                eval.basePoints = 25; // Outer Bullseye = 25 points
                eval.drumVoice = DrumVoiceType.SubBassKick;
            }
            else if (radialDist >= tripleInnerRadius && radialDist <= tripleOuterRadius)
            {
                eval.zoneType = HitZoneType.Triple;
                eval.basePoints = eval.sectorNumber * 3; // Triple Ring = 3x base number
                eval.drumVoice = DrumVoiceType.CrispSnare;
            }
            else if (radialDist >= doubleInnerRadius && radialDist <= doubleOuterRadius)
            {
                eval.zoneType = HitZoneType.Double;
                eval.basePoints = eval.sectorNumber * 2; // Double Ring = 2x base number
                eval.drumVoice = DrumVoiceType.CrispSnare;
            }
            else
            {
                // Inner/Outer Single or Outer Number Ring within board radius awards base sector points
                eval.zoneType = HitZoneType.Single;
                eval.basePoints = eval.sectorNumber;
                eval.drumVoice = DrumVoiceType.SynthStab;
            }

            if (matchedCue != null)
            {
                if (eval.basePoints <= 0 || radialDist > doubleOuterRadius)
                {
                    eval.sectorIndex = Mathf.Clamp(matchedCue.SectorIndex, 0, 19);
                    eval.sectorNumber = SectorNumbers[eval.sectorIndex];
                    eval.zoneType = matchedCue.ZoneType;
                    eval.basePoints = matchedCue.ZoneType switch
                    {
                        HitZoneType.InnerBullseye => 50,
                        HitZoneType.OuterBullseye => 25,
                        HitZoneType.Bullseye => 25,
                        HitZoneType.Triple => eval.sectorNumber * 3,
                        HitZoneType.Double => eval.sectorNumber * 2,
                        _ => eval.sectorNumber
                    };
                    eval.drumVoice = (matchedCue.ZoneType == HitZoneType.Triple || matchedCue.ZoneType == HitZoneType.Double)
                        ? DrumVoiceType.CrispSnare
                        : DrumVoiceType.SynthStab;
                }

                eval.matchedActiveBeatZone = true;
                eval.circleBonusPoints = 100; // +100 Bonus Mark for hitting the circle!
                eval.targetBeat = matchedCue.TargetImpactBeat;
                eval.consumedCue = matchedCue;
            }
            else
            {
                eval.matchedActiveBeatZone = false;
                eval.circleBonusPoints = 0;
                if (BeatConductor.Instance != null)
                {
                    BeatConductor.Instance.GetOffsetMsToNearestBeat(impactDspTime, out double nearestBeat);
                    eval.targetBeat = nearestBeat;
                }
            }

            return eval;
        }

        private void BuildRealDartboardVisuals()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            // 1. Dark wooden/matte cabinet backing disc behind the dartboard
            var rimDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rimDisc.name = "DartboardCabinetBacking";
            Destroy(rimDisc.GetComponent<Collider>());
            rimDisc.transform.SetParent(transform, false);
            rimDisc.transform.localPosition = new Vector3(0f, 0f, 0.024f);
            rimDisc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            rimDisc.transform.localScale = new Vector3(boardRadius * 2.04f, 0.020f, boardRadius * 2.04f);
            rimDisc.GetComponent<Renderer>().sharedMaterial = neonArtStyle
                ? CreateSafeMaterial(new Color(0.02f, 0.02f, 0.04f), null, 0f)
                : CreateSafeMaterial(new Color(0.07f, 0.07f, 0.07f));
            rimDisc.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 2. Board face: procedural neon board, or the REAL dartboard photo
            const float faceUvRadius = 0.492f;
            Material faceMat;
            if (neonArtStyle)
            {
                _boardTexture = RhythmDartMR.VFX.NeonArt.BoardTexture(boardRadius, innerBullRadius, bullseyeRadius,
                    tripleInnerRadius, tripleOuterRadius, doubleInnerRadius, doubleOuterRadius, faceUvRadius, SectorNumbers);
                faceMat = CreateSafeMaterial(new Color(0.35f, 0.35f, 0.35f), _boardTexture, 0f);
                RhythmDartMR.VFX.NeonArt.MakeSelfLit(faceMat, _boardTexture, Color.white, 0.55f);
            }
            else
            {
                _boardTexture = LoadRealDartboardTexture();
                faceMat = CreateSafeMaterial(Color.white, _boardTexture, 0.45f);
            }

            var faceObj = new GameObject("RealDartboardFace");
            faceObj.transform.SetParent(transform, false);
            faceObj.transform.localPosition = Vector3.zero;
            faceObj.transform.localRotation = Quaternion.identity;

            var mf = faceObj.AddComponent<MeshFilter>();
            mf.sharedMesh = CreateDiscMesh(boardRadius, 96, faceUvRadius);
            var mr = faceObj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = faceMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 3. Simulator "Choose environment" Room Backdrop (automatically syncs with whichever room you click
            // in Meta XR Simulator's "Choose environment" menu: GameRoom, Bedroom, LivingRoom, Office, Corridor, etc.)
            if (Application.isEditor)
            {
                var envQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                envQuad.name = "SimulatorEnvironmentRoomBackdrop";
                Destroy(envQuad.GetComponent<Collider>());
                envQuad.transform.SetParent(transform, false);
                envQuad.transform.localPosition = new Vector3(0f, 0.32f, 0.35f);
                envQuad.transform.localRotation = Quaternion.identity;
                envQuad.transform.localScale = new Vector3(12.0f, 7.2f, 1f);
                _envRoomRenderer = envQuad.GetComponent<Renderer>();
                ApplyEnvironmentRoomTexture("GameRoom");
                SyncSimulatorChosenEnvironment();
                envQuad.SetActive(!_simulatorEnvHiddenByStudio);
            }

            // 4. BoxCollider for raycasts
            var col = gameObject.GetComponent<BoxCollider>();
            if (col == null) col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(boardRadius * 2.2f, boardRadius * 2.2f, 0.05f);
            col.center = Vector3.zero;
        }

        private void SyncSimulatorChosenEnvironment()
        {
            if (_envRoomRenderer == null || _simulatorEnvHiddenByStudio) return;

            try
            {
                string logPath = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    @"MetaXRSimulator\Logs\MetaXRSimulator.log");

                var info = new FileInfo(logPath);
                if (!info.Exists) return;

                // Only read what was appended since the last poll (plus a small overlap), instead of
                // re-reading and decoding 128 KB every poll, which caused periodic frame hitches.
                long len = info.Length;
                if (len == _envLogReadPos) return;
                long start = (_envLogReadPos < 0 || len < _envLogReadPos)
                    ? len - EnvLogMaxReadBytes
                    : _envLogReadPos - 512;
                start = System.Math.Max(System.Math.Max(0L, start), len - EnvLogMaxReadBytes);
                _envLogReadPos = len;

                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int readBytes = (int)(len - start);
                    if (readBytes <= 0) return;
                    if (_envLogBuffer == null) _envLogBuffer = new byte[EnvLogMaxReadBytes];
                    fs.Seek(start, SeekOrigin.Begin);
                    int actual = fs.Read(_envLogBuffer, 0, readBytes);
                    string tail = System.Text.Encoding.UTF8.GetString(_envLogBuffer, 0, actual);

                    string marker = "Synthetic Environment Server with config:";
                    int idx = tail.LastIndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        string snippet = tail.Substring(idx, Mathf.Min(360, tail.Length - idx));
                        string detectedKey = DetectRoomKeyFromSnippet(snippet);
                        if (!string.IsNullOrEmpty(detectedKey) && detectedKey != _currentEnvRoomKey)
                        {
                            ApplyEnvironmentRoomTexture(detectedKey);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string DetectRoomKeyFromSnippet(string snippet)
        {
            if (snippet.IndexOf("Bedroom", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Bedroom";
            if (snippet.IndexOf("GameRoom", System.StringComparison.OrdinalIgnoreCase) >= 0) return "GameRoom";
            if (snippet.IndexOf("LivingRoom", System.StringComparison.OrdinalIgnoreCase) >= 0) return "LivingRoom";
            if (snippet.IndexOf("Office", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Office";
            if (snippet.IndexOf("XRoom2_3", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                snippet.IndexOf("Corridor", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Corridor";
            if (snippet.IndexOf("XRoom2_4", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                snippet.IndexOf("Furniture", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Furniture";
            if (snippet.IndexOf("XRoom5_1", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                snippet.IndexOf("HighCeiling", System.StringComparison.OrdinalIgnoreCase) >= 0) return "HighCeiling";
            if (snippet.IndexOf("XRoom5_6", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                snippet.IndexOf("LShape", System.StringComparison.OrdinalIgnoreCase) >= 0) return "LShape";
            return "GameRoom";
        }

        private void ApplyEnvironmentRoomTexture(string roomKey)
        {
            _currentEnvRoomKey = roomKey;
            if (!_loadedEnvTextures.TryGetValue(roomKey, out Texture2D tex) || tex == null)
            {
                string fileName = roomKey switch
                {
                    "Bedroom" => "bedroom.png",
                    "LivingRoom" => "living_room.png",
                    "Office" => "office_day.png",
                    "Corridor" => "corridor.png",
                    "Furniture" => "furniture_room.png",
                    "HighCeiling" => "high_ceiling_room.png",
                    "LShape" => "l_shape_room.png",
                    _ => "game_room.png"
                };

                string fullPath = Path.Combine(SimulatorRoomsDir, fileName);
                if (!File.Exists(fullPath))
                {
                    fullPath = Path.Combine(Application.dataPath, "Resources/RealRoomBg.png");
                }

                if (File.Exists(fullPath))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(fullPath);
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                        if (ImageConversion.LoadImage(tex, bytes))
                        {
                            tex.wrapMode = TextureWrapMode.Clamp;
                            tex.filterMode = FilterMode.Bilinear;
                            _loadedEnvTextures[roomKey] = tex;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (_envRoomRenderer != null && tex != null)
            {
                _envRoomRenderer.sharedMaterial = CreateSafeMaterialStatic(new Color(0.92f, 0.92f, 0.92f), tex, 0.58f);
            }
        }

        private Texture2D LoadRealDartboardTexture()
        {
            var resTex = Resources.Load<Texture2D>("RealDartboard");
            if (resTex != null) return resTex;

            string[] candidatePaths = new string[]
            {
                Path.Combine(Application.dataPath, "Resources/RealDartboard.jpg"),
                Path.Combine(Application.streamingAssetsPath, "RealDartboard.jpg")
            };

            foreach (string p in candidatePaths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(p);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                        if (ImageConversion.LoadImage(tex, bytes))
                        {
                            tex.wrapMode = TextureWrapMode.Clamp;
                            tex.filterMode = FilterMode.Trilinear;
                            return tex;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return GenerateFallbackBristleBoardTexture(1024);
        }

        private Texture2D GenerateFallbackBristleBoardTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color[] pixels = new Color[size * size];
            Color matteBlack = new Color(0.08f, 0.08f, 0.08f, 1f);
            Color creamIvory = new Color(0.91f, 0.87f, 0.76f, 1f);
            Color crimsonRed = new Color(0.86f, 0.14f, 0.14f, 1f);
            Color kellyGreen = new Color(0.08f, 0.56f, 0.23f, 1f);
            Color silverWire = new Color(0.78f, 0.80f, 0.82f, 1f);

            float invR = 1f / boardRadius;
            float rInnerBull = innerBullRadius * invR;
            float rOuterBull = bullseyeRadius * invR;
            float rTripleIn = tripleInnerRadius * invR;
            float rTripleOut = tripleOuterRadius * invR;
            float rDoubleIn = doubleInnerRadius * invR;
            float rDoubleOut = doubleOuterRadius * invR;
            float wireHalfWidth = 0.0042f;
            float half = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                float ny = (y - half) / half;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - half) / half;
                    float r = Mathf.Sqrt(nx * nx + ny * ny);
                    if (r > 1.0f)
                    {
                        pixels[y * size + x] = matteBlack;
                        continue;
                    }

                    float angleDeg = Mathf.Atan2(nx, ny) * Mathf.Rad2Deg;
                    if (angleDeg < 0f) angleDeg += 360f;
                    float shiftedAngle = (angleDeg + 9f) % 360f;
                    int sectorIdx = Mathf.FloorToInt(shiftedAngle / 18f);
                    float sectorLocalDeg = shiftedAngle - sectorIdx * 18f;
                    bool isEvenSector = (sectorIdx % 2 == 0);
                    Color c;

                    if (r <= rInnerBull) c = crimsonRed;
                    else if (r <= rOuterBull) c = kellyGreen;
                    else if (r > rDoubleOut) c = matteBlack;
                    else if ((r >= rTripleIn && r <= rTripleOut) || (r >= rDoubleIn && r <= rDoubleOut))
                        c = isEvenSector ? crimsonRed : kellyGreen;
                    else
                        c = isEvenSector ? matteBlack : creamIvory;

                    bool onRingWire =
                        Mathf.Abs(r - rInnerBull) <= wireHalfWidth ||
                        Mathf.Abs(r - rOuterBull) <= wireHalfWidth ||
                        Mathf.Abs(r - rTripleIn) <= wireHalfWidth ||
                        Mathf.Abs(r - rTripleOut) <= wireHalfWidth ||
                        Mathf.Abs(r - rDoubleIn) <= wireHalfWidth ||
                        Mathf.Abs(r - rDoubleOut) <= wireHalfWidth;

                    float arcDist = Mathf.Min(sectorLocalDeg, 18f - sectorLocalDeg) * Mathf.Deg2Rad * r;
                    bool onSpokeWire = (r >= rOuterBull && r <= rDoubleOut + 0.02f) && (arcDist <= wireHalfWidth * 0.95f);

                    if (onRingWire || onSpokeWire) c = silverWire;
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }

        private static Mesh CreateDiscMesh(float radius, int segments, float uvRadius)
        {
            Mesh m = new Mesh { name = "RealDartboardDiscMesh" };
            Vector3[] verts = new Vector3[segments + 1];
            Vector2[] uvs = new Vector2[segments + 1];
            int[] tris = new int[segments * 3];

            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);

            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                float sx = Mathf.Sin(a);
                float cy = Mathf.Cos(a);
                verts[i + 1] = new Vector3(sx * radius, cy * radius, 0f);
                uvs[i + 1] = new Vector2(0.5f + sx * uvRadius, 0.5f + cy * uvRadius);

                int next = (i + 1) % segments;
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = next + 1;
            }

            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
