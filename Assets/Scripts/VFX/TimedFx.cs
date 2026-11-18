using System;
using System.Collections.Generic;
using UnityEngine;

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// Tiny tween helper: calls <c>tick(t)</c> every frame with t going 0 → 1 over <c>duration</c>.
    /// When finished it either calls <c>onComplete</c> (used to return pooled objects) or, if none was
    /// given, destroys its GameObject together with any objects registered via <see cref="Own"/>.
    /// Calling <see cref="Run"/> again on the same GameObject restarts the existing component (no new allocation).
    /// </summary>
    public sealed class TimedFx : MonoBehaviour
    {
        private float _elapsed;
        private float _duration;
        private Action<float> _tick;
        private Action _onComplete;
        private List<UnityEngine.Object> _owned;

        public static TimedFx Run(GameObject go, float duration, Action<float> tick, Action onComplete = null)
        {
            var fx = go.GetComponent<TimedFx>();
            if (fx == null) fx = go.AddComponent<TimedFx>();
            fx._elapsed = 0f;
            fx._duration = Mathf.Max(0.01f, duration);
            fx._tick = tick;
            fx._onComplete = onComplete;
            fx.enabled = true;
            tick?.Invoke(0f);
            return fx;
        }

        /// <summary>Destroy this object (e.g. a runtime Material instance) together with the effect.</summary>
        public TimedFx Own(UnityEngine.Object obj)
        {
            if (_owned == null) _owned = new List<UnityEngine.Object>();
            _owned.Add(obj);
            return this;
        }

        private void Update()
        {
            _elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            float t = Mathf.Clamp01(_elapsed / _duration);
            _tick?.Invoke(t);
            if (t < 1f) return;

            if (_onComplete != null)
            {
                var done = _onComplete;
                _onComplete = null;
                _tick = null;
                enabled = false;
                done();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (_owned == null) return;
            foreach (var o in _owned)
            {
                if (o != null) Destroy(o);
            }
        }
    }
}
