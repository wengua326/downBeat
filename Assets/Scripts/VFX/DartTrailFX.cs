using UnityEngine;
using UnityEngine.Rendering;
using RhythmDartMR.Gameplay;

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// Added automatically to every thrown dart by <see cref="DartFlight.Launch"/>.
    /// - Two-layer glowing trail (wide soft glow + bright white-hot core), coloured by the current combo tier
    ///   (rainbow during Fever).
    /// - Glowing head + sparkle dust shed along the flight path.
    /// - On landing: trail stops emitting and fades out, and the dart does a short "thunk" wobble.
    /// </summary>
    public class DartTrailFX : MonoBehaviour
    {
        // Local Z of the dart's flights (dart origin sits at the tip, body extends towards -Z).
        private const float TailLocalZ = -0.17f;
        private const float WobbleSeconds = 0.45f;

        private DartFlight _flight;
        private TrailRenderer _glowTrail;
        private TrailRenderer _coreTrail;
        private Color _color;
        private bool _fever;
        private bool _landed;
        private bool _wobbleDone;
        private float _landTime;
        private Quaternion _restRotation;

        private void Awake()
        {
            _flight = GetComponent<DartFlight>();

            int combo = ScoreAndComboManager.Instance != null ? ScoreAndComboManager.Instance.CurrentCombo : 0;
            _fever = combo >= VfxResources.ComboTierFever;
            _color = _fever ? VfxResources.Gold : VfxResources.ComboColor(combo);

            Gradient glow = _fever ? RainbowGradient(0.85f) : FadeGradient(_color, _color * 0.6f, 0.85f);
            Gradient core = FadeGradient(Color.white, _color, 1f);

            _glowTrail = CreateTrail("DartTrailGlow", 0.30f, 0.045f, glow);
            _coreTrail = CreateTrail("DartTrailCore", 0.14f, 0.010f, core);
        }

        private void Update()
        {
            if (!_landed)
            {
                if (_flight != null && !_flight.IsInFlight)
                {
                    Land();
                    return;
                }
                EmitFlightParticles();
                return;
            }

            if (_wobbleDone) return;
            float t = Time.time - _landTime;
            if (t >= WobbleSeconds)
            {
                transform.rotation = _restRotation;
                _wobbleDone = true;
                return;
            }
            float amp = 7f * Mathf.Exp(-t * 9f);
            float ang = Mathf.Sin(t * 55f) * amp;
            transform.rotation = _restRotation * Quaternion.Euler(ang, ang * 0.6f, 0f);
        }

        private void EmitFlightParticles()
        {
            Vector3 tip = transform.position;
            Vector3 tail = transform.TransformPoint(new Vector3(0f, 0f, TailLocalZ));
            Color c = _fever ? VfxResources.ComboColor(VfxResources.ComboTierFever) : _color;

            // Glowing "comet head" around the barrel
            VfxPools.Glow(Vector3.Lerp(tip, tail, 0.35f), Vector3.zero, Color.Lerp(c, Color.white, 0.5f), 0.07f, 0.06f);

            // Sparkle dust shed from the flights
            if (VfxSettings.LowQuality && Random.value < 0.6f) return;
            int n = Random.value < 0.5f ? 1 : 2;
            for (int i = 0; i < n; i++)
            {
                Vector3 jitter = Random.insideUnitSphere * 0.012f;
                VfxPools.Glow(tail + jitter, Random.insideUnitSphere * 0.08f, c, Random.Range(0.010f, 0.022f), Random.Range(0.25f, 0.45f));
            }
        }

        private void Land()
        {
            _landed = true;
            _landTime = Time.time;
            _restRotation = transform.rotation;

            foreach (var tr in new[] { _glowTrail, _coreTrail })
            {
                if (tr == null) continue;
                tr.emitting = false;
                Destroy(tr.gameObject, tr.time + 0.1f);
            }
        }

        private TrailRenderer CreateTrail(string objName, float time, float width, Gradient gradient)
        {
            var go = new GameObject(objName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, TailLocalZ);

            var tr = go.AddComponent<TrailRenderer>();
            tr.time = time;
            tr.minVertexDistance = 0.01f;
            tr.widthMultiplier = width;
            tr.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.25f, 0.8f), new Keyframe(1f, 0f));
            tr.colorGradient = gradient;
            tr.sharedMaterial = VfxResources.LineMaterial;
            tr.textureMode = LineTextureMode.Stretch;
            tr.alignment = LineAlignment.View;
            tr.numCapVertices = 3;
            tr.shadowCastingMode = ShadowCastingMode.Off;
            tr.receiveShadows = false;
            tr.Clear();
            return tr;
        }

        private static Gradient FadeGradient(Color head, Color tail, float headAlpha)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(head, 0f), new GradientColorKey(tail, 1f) },
                new[] { new GradientAlphaKey(headAlpha, 0f), new GradientAlphaKey(headAlpha * 0.55f, 0.35f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private static Gradient RainbowGradient(float headAlpha)
        {
            var colors = new GradientColorKey[6];
            float hueOffset = Mathf.Repeat(Time.time * 0.35f, 1f);
            for (int i = 0; i < colors.Length; i++)
            {
                float t = i / (float)(colors.Length - 1);
                colors[i] = new GradientColorKey(Color.HSVToRGB(Mathf.Repeat(hueOffset + t * 0.8f, 1f), 0.75f, 1f), t);
            }
            var g = new Gradient();
            g.SetKeys(colors, new[]
            {
                new GradientAlphaKey(headAlpha, 0f),
                new GradientAlphaKey(headAlpha * 0.6f, 0.4f),
                new GradientAlphaKey(0f, 1f)
            });
            return g;
        }
    }
}
