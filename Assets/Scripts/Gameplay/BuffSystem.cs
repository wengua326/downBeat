using System;
using System.Collections.Generic;
using UnityEngine;
using RhythmDartMR.UI;
using Random = UnityEngine.Random;

namespace RhythmDartMR.Gameplay
{
    public enum BuffType
    {
        Overdrive,    // next N darts score x2
        Jackpot,      // circle bonus x3 for a while
        TimeWarp,     // +15 s on the clock
        Quiver,       // +5 darts
        TargetRush,   // +2 simultaneous circles for a while
        BigTargets,   // circles 45% larger for a while
        SlowMotion,   // circles shrink 60% slower for a while
        ComboShield   // next 2 misses don't break the combo
    }

    public enum BuffRarity
    {
        Common,
        Rare,
        Epic
    }

    public sealed class BuffDefinition
    {
        public BuffType Type;
        public string Name;
        public string Badge;       // short big text on the card ("2X", "+15", …)
        public string Description;
        public string Detail;      // duration / charges pill
        public BuffRarity Rarity;
        public float Weight;       // relative chance of being offered
    }

    /// <summary>Snapshot of an active buff for the HUD.</summary>
    public struct ActiveBuffView
    {
        public BuffType Type;
        public string Name;
        public string Badge;
        public Color Color;
        public float Fraction;     // 1 → 0 as it runs out
        public string Remaining;   // "12s" / "3 LEFT"
    }

    /// <summary>
    /// Power-up ("buff") system. Hitting a POWER circle pauses the round and offers 3 random buffs
    /// (<see cref="BuffSelectionScreen"/>); the player throws a dart at the card they want.
    /// Timed buffs only tick while the round is running (not while choosing).
    /// Self-bootstrapping: no scene setup required.
    /// </summary>
    public class BuffSystem : MonoBehaviour
    {
        public static BuffSystem Instance { get; private set; }

        public const int OverdriveDarts = 5;
        public const float JackpotSeconds = 15f;
        public const float TimeWarpSeconds = 15f;
        public const int QuiverDarts = 5;
        public const float TimedBuffSeconds = 20f;
        public const int ShieldCharges = 2;

        private static readonly BuffDefinition[] Definitions =
        {
            new BuffDefinition { Type = BuffType.Overdrive, Name = "OVERDRIVE", Badge = "2X", Rarity = BuffRarity.Epic, Weight = 0.8f,
                Description = "Every point counts double.", Detail = $"NEXT {OverdriveDarts} DARTS" },
            new BuffDefinition { Type = BuffType.Jackpot, Name = "JACKPOT", Badge = "3X", Rarity = BuffRarity.Epic, Weight = 0.7f,
                Description = "Circle bonus is worth +300 instead of +100.", Detail = $"{JackpotSeconds:0} SECONDS" },
            new BuffDefinition { Type = BuffType.TimeWarp, Name = "TIME WARP", Badge = "+15", Rarity = BuffRarity.Rare, Weight = 1.0f,
                Description = "Adds extra time to the clock.", Detail = $"+{TimeWarpSeconds:0} SECONDS" },
            new BuffDefinition { Type = BuffType.Quiver, Name = "QUIVER", Badge = "+5", Rarity = BuffRarity.Rare, Weight = 1.0f,
                Description = "Adds extra darts to your quiver.", Detail = $"+{QuiverDarts} DARTS" },
            new BuffDefinition { Type = BuffType.TargetRush, Name = "FASTER SPAWN", Badge = "FAST", Rarity = BuffRarity.Common, Weight = 1.2f,
                Description = "Circles respawn twice as fast (max 2 on board).", Detail = $"{TimedBuffSeconds:0} SECONDS" },
            new BuffDefinition { Type = BuffType.BigTargets, Name = "BIG TARGETS", Badge = "XL", Rarity = BuffRarity.Common, Weight = 1.2f,
                Description = "Circles are much larger and easier to hit.", Detail = $"{TimedBuffSeconds:0} SECONDS" },
            new BuffDefinition { Type = BuffType.SlowMotion, Name = "SLOW MOTION", Badge = "0.6X", Rarity = BuffRarity.Common, Weight = 1.2f,
                Description = "Circles shrink slower and stay longer.", Detail = $"{TimedBuffSeconds:0} SECONDS" },
            new BuffDefinition { Type = BuffType.ComboShield, Name = "COMBO SHIELD", Badge = "x2", Rarity = BuffRarity.Common, Weight = 1.0f,
                Description = "Your next misses won't break the combo.", Detail = $"{ShieldCharges} CHARGES" },
        };

        public static BuffDefinition Get(BuffType type)
        {
            foreach (var d in Definitions) if (d.Type == type) return d;
            return Definitions[0];
        }

        public static Color RarityColor(BuffRarity rarity)
        {
            switch (rarity)
            {
                case BuffRarity.Epic: return UiTheme.Gold;
                case BuffRarity.Rare: return UiTheme.Violet;
                default: return UiTheme.Accent;
            }
        }

        public static string RarityName(BuffRarity rarity)
        {
            switch (rarity)
            {
                case BuffRarity.Epic: return "EPIC";
                case BuffRarity.Rare: return "RARE";
                default: return "COMMON";
            }
        }

        // ------------------------------------------------------------------ state

        public bool IsChoosing { get; private set; }

        /// <summary>Raised the moment the player picks a buff (before the selection screen closes).</summary>
        public event Action<BuffDefinition> OnBuffChosen;
        public event Action OnChoiceOffered;

        private int _overdriveDarts;
        private int _shieldCharges;
        private float _jackpotTime;
        private float _rushTime;
        private float _bigTime;
        private float _slowTime;

        private BuffSelectionScreen _screen;

        // ------------------------------------------------------------------ effects (read by gameplay)

        public float PointsMultiplier => _overdriveDarts > 0 ? 2f : 1f;
        public int CircleBonusMultiplier => _jackpotTime > 0f ? 3 : 1;
        public float CircleSizeMultiplier => _bigTime > 0f ? 1.45f : 1f;
        public float ApproachBeatsMultiplier => _slowTime > 0f ? 1.6f : 1f;
        public int ExtraTargets => 0;
        public bool IsTargetRushActive => _rushTime > 0f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            new GameObject("BuffSystem").AddComponent<BuffSystem>();
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
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            var session = GameSessionManager.Instance;
            bool ticking = !IsChoosing && (session == null || session.IsRoundActive);
            if (!ticking) return;

            float dt = Time.deltaTime;
            _jackpotTime = Mathf.Max(0f, _jackpotTime - dt);
            _rushTime = Mathf.Max(0f, _rushTime - dt);
            _bigTime = Mathf.Max(0f, _bigTime - dt);
            _slowTime = Mathf.Max(0f, _slowTime - dt);
        }

        /// <summary>Call once per judged dart (consumes an Overdrive charge).</summary>
        public void ConsumeDartCharge()
        {
            if (_overdriveDarts > 0) _overdriveDarts--;
        }

        /// <summary>True (and consumes a charge) if a miss should NOT break the combo.</summary>
        public bool TryConsumeComboShield()
        {
            if (_shieldCharges <= 0) return false;
            _shieldCharges--;
            return true;
        }

        public void ResetAll()
        {
            _overdriveDarts = 0;
            _shieldCharges = 0;
            _jackpotTime = _rushTime = _bigTime = _slowTime = 0f;
            if (IsChoosing)
            {
                IsChoosing = false;
                if (_screen != null) _screen.HideImmediate();
            }
        }

        // ------------------------------------------------------------------ selection flow

        /// <summary>Pauses the round and shows 3 random buffs to choose from.</summary>
        public void OfferChoice()
        {
            if (IsChoosing) return;
            var session = GameSessionManager.Instance;
            if (session != null && !session.IsRoundActive) return;

            var board = RhythmDartboard.Instance;
            if (board == null) return;

            IsChoosing = true;
            board.ClearAllCuesAndDarts();

            if (_screen == null)
            {
                _screen = new GameObject("BuffSelectionScreen").AddComponent<BuffSelectionScreen>();
            }
            _screen.Show(board.transform, PickThree(), HandlePicked, HandleScreenClosed);
            OnChoiceOffered?.Invoke();
        }

        /// <summary>Routes a dart that landed while choosing to the card it hit. Returns true if a card was picked.</summary>
        public bool HandleSelectionHit(Vector3 worldPos)
        {
            return IsChoosing && _screen != null && _screen.TryPickAt(worldPos);
        }

        private void HandlePicked(BuffDefinition def)
        {
            switch (def.Type)
            {
                case BuffType.Overdrive: _overdriveDarts += OverdriveDarts; break;
                case BuffType.Jackpot: _jackpotTime += JackpotSeconds; break;
                case BuffType.TimeWarp:
                    if (GameSessionManager.Instance != null) GameSessionManager.Instance.AddBonusTime(TimeWarpSeconds);
                    break;
                case BuffType.Quiver:
                    if (GameSessionManager.Instance != null) GameSessionManager.Instance.AddDarts(QuiverDarts);
                    break;
                case BuffType.TargetRush: _rushTime += TimedBuffSeconds; break;
                case BuffType.BigTargets: _bigTime += TimedBuffSeconds; break;
                case BuffType.SlowMotion: _slowTime += TimedBuffSeconds; break;
                case BuffType.ComboShield: _shieldCharges += ShieldCharges; break;
            }
            OnBuffChosen?.Invoke(def);
        }

        private void HandleScreenClosed()
        {
            IsChoosing = false;
            if (RhythmDartboard.Instance != null) RhythmDartboard.Instance.ClearAllCuesAndDarts();
            if (GameSessionManager.Instance != null) GameSessionManager.Instance.CheckEndConditions();
        }

        private static BuffDefinition[] PickThree()
        {
            var pool = new List<BuffDefinition>(Definitions);
            var result = new BuffDefinition[3];
            for (int i = 0; i < result.Length; i++)
            {
                float total = 0f;
                foreach (var d in pool) total += d.Weight;
                float roll = Random.value * total;
                int chosen = pool.Count - 1;
                for (int j = 0; j < pool.Count; j++)
                {
                    roll -= pool[j].Weight;
                    if (roll <= 0f)
                    {
                        chosen = j;
                        break;
                    }
                }
                result[i] = pool[chosen];
                pool.RemoveAt(chosen);
            }
            return result;
        }

        // ------------------------------------------------------------------ HUD

        /// <summary>Fills <paramref name="into"/> with the currently active buffs (no allocations if the list is reused).</summary>
        public void GetActiveBuffs(List<ActiveBuffView> into)
        {
            into.Clear();
            if (_overdriveDarts > 0) into.Add(View(BuffType.Overdrive, _overdriveDarts / (float)OverdriveDarts, $"{_overdriveDarts} LEFT"));
            if (_jackpotTime > 0f) into.Add(View(BuffType.Jackpot, _jackpotTime / JackpotSeconds, $"{Mathf.CeilToInt(_jackpotTime)}s"));
            if (_rushTime > 0f) into.Add(View(BuffType.TargetRush, _rushTime / TimedBuffSeconds, $"{Mathf.CeilToInt(_rushTime)}s"));
            if (_bigTime > 0f) into.Add(View(BuffType.BigTargets, _bigTime / TimedBuffSeconds, $"{Mathf.CeilToInt(_bigTime)}s"));
            if (_slowTime > 0f) into.Add(View(BuffType.SlowMotion, _slowTime / TimedBuffSeconds, $"{Mathf.CeilToInt(_slowTime)}s"));
            if (_shieldCharges > 0) into.Add(View(BuffType.ComboShield, _shieldCharges / (float)ShieldCharges, $"{_shieldCharges} LEFT"));
        }

        private static ActiveBuffView View(BuffType type, float fraction, string remaining)
        {
            var def = Get(type);
            return new ActiveBuffView
            {
                Type = type,
                Name = def.Name,
                Badge = def.Badge,
                Color = RarityColor(def.Rarity),
                Fraction = Mathf.Clamp01(fraction),
                Remaining = remaining
            };
        }
    }
}
