using UnityEngine;
using UnityEngine.UI;
using RhythmDartMR.Audio;
using RhythmDartMR.UI;
using RhythmDartMR.VFX;

namespace RhythmDartMR.Gameplay
{
    /// <summary>
    /// Beat-synced target circle. Hitting inside it awards the circle bonus on top of the dart score.
    ///
    /// Built for readability on a busy board / passthrough:
    /// - dark contrast halo behind the target, glowing fill, crisp white core ring + soft neon glow ring
    /// - bright approach ring that shrinks in BEATS (closes exactly on a beat, speeds up with the tempo)
    /// - rotating lock-on brackets that tighten as the ring closes and throb on every beat once closed
    /// - pulsing centre dot
    /// POWER circles add a spinning gold/violet hexagon, orbiting sparks and a "POWER-UP" tag; hitting
    /// one opens the power-up selection.
    /// </summary>
    public class ApproachCircleCue : MonoBehaviour
    {
        /// <summary>Beats from spawn until the approach ring closes (set by RhythmDartboard before each spawn).</summary>
        public static float ApproachBeats = 6.62f;

        /// <summary>Extra beats the closed target stays hittable before fading out (0 = destroy immediately).</summary>
        public static float HoldBeats = 0f;

        public double SpawnBeat { get; private set; }
        public double TargetImpactBeat { get; private set; }
        public float InnerRadius { get; private set; }
        public Color ZoneColor { get; private set; }
        public int SectorIndex { get; private set; }
        public HitZoneType ZoneType { get; private set; }
        public bool IsConsumed { get; private set; }
        public bool IsPowerCue { get; private set; }

        private const int CircleSegments = 64;
        private const float StartScaleMultiplier = 2.3f;
        private const float PopSeconds = 0.22f;
        private const float ExitSeconds = 0.05f;
        private const float FallbackBpm = 85f;

        private enum CueState { Approaching, Expiring, Consumed }

        private LineRenderer _glowRing;
        private LineRenderer _coreOutlineRing;
        private LineRenderer _coreRing;
        private LineRenderer _approachOutlineRing;
        private LineRenderer _approachRing;
        private LineRenderer[] _arcs;
        private LineRenderer _hex;
        private Transform _bracketRoot;
        private Transform _hexRoot;
        private Transform _centerDimmer;
        private Transform _dot;
        private Transform _dotOutline;
        private Material _centerDimmerMat;
        private Material _dotMat;
        private Material _dotOutlineMat;

        private CueState _state = CueState.Approaching;
        private float _spawnRealTime;
        private float _exitStartTime;
        private double _closeBeat;
        private double _holdBeats;
        private float _lastProgress;

        public void Initialize(
            double spawnBeat,
            double targetImpactBeat,
            float innerRadius,
            Color zoneColor,
            int sectorIndex,
            HitZoneType zoneType,
            Shader unlitShader,
            bool isPower = false)
        {
            SpawnBeat = spawnBeat;
            TargetImpactBeat = targetImpactBeat;
            InnerRadius = innerRadius;
            ZoneColor = zoneColor;
            SectorIndex = sectorIndex;
            ZoneType = zoneType;
            IsPowerCue = isPower;
            IsConsumed = false;
            _state = CueState.Approaching;
            _spawnRealTime = Time.time;
            _holdBeats = HoldBeats;

            // Use exact unrounded beat duration to preserve smooth 0.85x shrink speed and impact sync.
            _closeBeat = spawnBeat + ApproachBeats;
            TargetImpactBeat = _closeBeat;

            BuildVisuals();

            transform.localScale = Vector3.one * 0.01f;
        }

        public void ConsumeOnHit(TimingGrade grade)
        {
            if (IsConsumed) return;
            IsConsumed = true;
            _state = CueState.Consumed;
            Destroy(gameObject);
        }

        /// <summary>Destroy target immediately when cleared (e.g. for the power-up screen).</summary>
        public void Dismiss()
        {
            if (IsConsumed) return;
            IsConsumed = true;
            _state = CueState.Expiring;
            Destroy(gameObject);
        }

        // ------------------------------------------------------------------ update

        private void Update()
        {
            float now = Time.time;
            if (_state != CueState.Approaching)
            {
                AnimateExit(now);
                return;
            }

            double beat = CurrentBeat(out float beatPhase);
            float progress = Mathf.Clamp01((float)((beat - SpawnBeat) / (_closeBeat - SpawnBeat)));
            _lastProgress = progress;
            bool closed = progress >= 1f;
            bool inPerfectWindow = progress >= 0.82f;
            float windowBlend = inPerfectWindow ? Mathf.Clamp01((progress - 0.82f) / 0.18f) : 0f;
            float beatPulse = Mathf.Exp(-beatPhase * 6f);
            float perfectPulse = inPerfectWindow ? (0.5f + 0.5f * Mathf.Sin(now * 18f)) : 0f;

            // Pop-in, then clearly pulse when inside the release timing window
            float pop = VfxFactory.EaseOutBack((now - _spawnRealTime) / PopSeconds);
            float timingScale = inPerfectWindow ? (1f + 0.08f * windowBlend * Mathf.Max(beatPulse, perfectPulse)) : 1f;
            transform.localScale = Vector3.one * Mathf.Max(0.01f, pop) * timingScale;

            // 1. Shrinking Outer Approach Shape: Electric Gold (#FFE600) -> Neon Emerald (#00FF66) / HDR White (#FFFFFF)
            float approachRadius = closed
                ? InnerRadius * (1f + 0.05f * beatPulse)
                : Mathf.Lerp(InnerRadius * StartScaleMultiplier, InnerRadius, progress);
            if (IsPowerCue)
            {
                VfxFactory.SetTriangleRadius(_approachOutlineRing, approachRadius, -0.0015f);
                VfxFactory.SetTriangleRadius(_approachRing, approachRadius, -0.0020f);
            }
            else
            {
                VfxFactory.SetRingRadius(_approachOutlineRing, approachRadius, -0.0015f);
                VfxFactory.SetRingRadius(_approachRing, approachRadius, -0.0020f);
            }

            float approachWidth = Mathf.Lerp(0.034f, 0.046f, progress) + (inPerfectWindow ? 0.006f * perfectPulse : 0f);
            _approachRing.widthMultiplier = approachWidth;
            _approachOutlineRing.widthMultiplier = approachWidth + 0.020f;

            Color perfectCol = Color.Lerp(VfxResources.NeonEmerald, Color.white, 0.45f * perfectPulse);
            Color approachStart = IsPowerCue ? Color.Lerp(UiTheme.Violet, UiTheme.Gold, 0.35f) : VfxResources.ElectricGold;
            Color approachCol = inPerfectWindow
                ? Color.Lerp(approachStart, perfectCol, windowBlend)
                : approachStart;
            float approachAlpha = Mathf.Clamp01(progress / 0.08f);
            approachCol.a = approachAlpha;
            VfxFactory.SetHdrColor(_approachRing, approachCol, inPerfectWindow ? 3.2f + 0.4f * perfectPulse : 3.0f);

            Color outlineCol = VfxResources.DarkOutline;
            outlineCol.a *= approachAlpha;
            VfxFactory.SetColor(_approachOutlineRing, outlineCol);

            // 2. Static Inner Target Reference Shape: White/Cyan (or Violet for Power Triangle) -> Neon Emerald / HDR White on lock-on
            Color staticBase = IsPowerCue ? new Color(0.84f, 0.48f, 1.00f, 0.98f) : new Color(0.82f, 0.98f, 1.00f, 0.96f);
            Color coreCol = inPerfectWindow ? Color.Lerp(staticBase, perfectCol, windowBlend) : staticBase;
            float coreWidth = 0.018f + (inPerfectWindow ? 0.006f * windowBlend : 0f);
            _coreRing.widthMultiplier = coreWidth;
            _coreOutlineRing.widthMultiplier = coreWidth + 0.016f;
            VfxFactory.SetHdrColor(_coreRing, coreCol, inPerfectWindow ? 3.0f : 2.5f);
            VfxFactory.SetColor(_coreOutlineRing, VfxResources.DarkOutline);

            Color glowCol = inPerfectWindow ? VfxResources.NeonEmerald : (IsPowerCue ? UiTheme.Violet : VfxResources.ElectricGold);
            glowCol.a = inPerfectWindow ? (0.32f + 0.28f * perfectPulse) : 0.16f;
            _glowRing.widthMultiplier = 0.036f + (inPerfectWindow ? 0.020f * perfectPulse : 0f);
            VfxFactory.SetColor(_glowRing, glowCol);

            // 3. Lock-on brackets tightened around target (power triangle keeps chevron vertices aligned)
            if (IsPowerCue)
            {
                _bracketRoot.localRotation = Quaternion.identity;
            }
            else
            {
                _bracketRoot.Rotate(0f, 0f, 37.4f * Time.deltaTime, Space.Self);
            }
            float bracketScale = Mathf.Lerp(1.38f, 1.12f, progress) * (1f - 0.04f * (closed ? beatPulse : 0f));
            _bracketRoot.localScale = Vector3.one * bracketScale;
            Color bc = IsPowerCue ? Color.Lerp(UiTheme.Gold, Color.white, perfectPulse) : coreCol;
            bc.a = inPerfectWindow ? 0.65f + 0.35f * perfectPulse : 0.45f + 0.25f * progress;
            for (int i = 0; i < _arcs.Length; i++) VfxFactory.SetColor(_arcs[i], bc);

            // 4. Tiny high-contrast center dot
            float dotScale = InnerRadius * (0.16f + (inPerfectWindow ? 0.06f * perfectPulse : 0f));
            _dot.localScale = Vector3.one * dotScale;
            _dotOutline.localScale = Vector3.one * (dotScale * 1.65f);
            Color dc = inPerfectWindow ? perfectCol : Color.white;
            dc.a = 0.92f;
            VfxResources.SetColor(_dotMat, dc);
            VfxResources.SetColor(_dotOutlineMat, VfxResources.DarkOutline);

            if (IsPowerCue) AnimatePower(beatPulse);

            if (progress >= 1f || beat >= _closeBeat + _holdBeats)
            {
                IsConsumed = true;
                Destroy(gameObject);
                return;
            }
        }

        private void AnimatePower(float beatPulse)
        {
            _hexRoot.localRotation = Quaternion.identity;
            float k = 0.5f + 0.5f * Mathf.Sin(Time.time * 4f);
            Color hc = Color.Lerp(UiTheme.Violet, UiTheme.Gold, k);
            hc.a = 0.85f + 0.15f * beatPulse;
            VfxFactory.SetHdrColor(_hex, hc, 2.6f);
            _hex.widthMultiplier = 0.014f + 0.006f * beatPulse;

            // sparkles tracing along the equilateral triangle perimeter
            if (Random.value < 0.7f)
            {
                float u = Mathf.Repeat(Time.time * 0.45f + Random.value * 0.08f, 1f) * 3f;
                int side = Mathf.Clamp((int)u, 0, 2);
                float segT = u - side;
                float vertexR = InnerRadius * 1.42f * 1.18f * transform.localScale.x;
                Vector2 v0 = new Vector2(0f, vertexR);
                Vector2 v1 = new Vector2(-vertexR * 0.8660254f, -vertexR * 0.5f);
                Vector2 v2 = new Vector2(vertexR * 0.8660254f, -vertexR * 0.5f);
                Vector2 aPt = side == 0 ? v0 : side == 1 ? v1 : v2;
                Vector2 bPt = side == 0 ? v1 : side == 1 ? v2 : v0;
                Vector2 pt = Vector2.Lerp(aPt, bPt, segT);
                Vector3 local = new Vector3(pt.x, pt.y, -0.005f);
                Vector3 pos = transform.parent != null ? transform.parent.TransformPoint(transform.localPosition + local) : transform.position + local;
                VfxPools.Glow(pos, Random.insideUnitSphere * 0.03f, Color.Lerp(UiTheme.Violet, UiTheme.Gold, Random.value),
                    Random.Range(0.012f, 0.024f), Random.Range(0.3f, 0.5f));
            }
        }

        private void BeginExit(CueState state)
        {
            _state = state;
            _exitStartTime = Time.time;
        }

        private void AnimateExit(float now)
        {
            float t = Mathf.Clamp01((now - _exitStartTime) / ExitSeconds);
            if (t >= 1f)
            {
                Destroy(gameObject);
                return;
            }

            bool hit = _state == CueState.Consumed;
            transform.localScale = Vector3.one * (hit ? 1f + 0.75f * VfxFactory.EaseOutCubic(t) : 1f - 0.55f * t);

            Color baseCol = hit ? VfxResources.NeonEmerald : VfxResources.ElectricGold;
            float a = (1f - t) * (1f - t);

            Color ring = baseCol; ring.a = a;
            VfxFactory.SetHdrColor(_coreRing, ring, 2.2f);
            Color outCol = VfxResources.DarkOutline; outCol.a *= a;
            VfxFactory.SetColor(_coreOutlineRing, outCol);
            Color g = baseCol; g.a = a * 0.35f;
            VfxFactory.SetColor(_glowRing, g);
            Color appr = baseCol; appr.a = hit ? a : a * (1f - _lastProgress);
            VfxFactory.SetHdrColor(_approachRing, appr, 2.2f);
            Color apprOut = VfxResources.DarkOutline; apprOut.a *= appr.a;
            VfxFactory.SetColor(_approachOutlineRing, apprOut);
            for (int i = 0; i < _arcs.Length; i++) VfxFactory.SetColor(_arcs[i], ring);
            if (_hex != null) VfxFactory.SetColor(_hex, ring);

            Color dot = baseCol; dot.a = a;
            VfxResources.SetColor(_dotMat, dot);
            VfxResources.SetColor(_dotOutlineMat, outCol);
            if (_centerDimmerMat != null) VfxResources.SetColor(_centerDimmerMat, new Color(0f, 0f, 0f, 0.45f * a));
        }

        private double CurrentBeat(out float phase)
        {
            var conductor = BeatConductor.Instance;
            if (conductor != null && conductor.IsRunning)
            {
                phase = conductor.BeatPhase;
                return conductor.CurrentBeat;
            }
            double b = SpawnBeat + (Time.time - _spawnRealTime) * (FallbackBpm / 60f);
            phase = (float)(b - System.Math.Floor(b));
            return b;
        }

        // ------------------------------------------------------------------ build

        private void BuildVisuals()
        {
            float r = InnerRadius;

            // Target center dimmer vignette inside static target area to isolate ring from board lines
            _centerDimmerMat = VfxResources.CreateMaterial(false,
                IsPowerCue ? VfxResources.TriangleDimmerTexture : VfxResources.CenterDimmerTexture);
            _centerDimmerMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 2;
            VfxResources.SetColor(_centerDimmerMat, new Color(0f, 0f, 0f, 0.45f));
            _centerDimmer = CreateDiscQuad("CenterDimmer", IsPowerCue ? r * 2.40f : r * 2.04f, 0.0020f, _centerDimmerMat);

            // Dual Reference Shape + Bold 2x Line Width + High-Contrast #000000 Backing Outlines
            if (IsPowerCue)
            {
                Color powerCoreCol = new Color(0.84f, 0.48f, 1.00f, 0.98f);
                Color powerApprCol = Color.Lerp(UiTheme.Violet, UiTheme.Gold, 0.35f);
                _coreOutlineRing = VfxFactory.CreateTriangleRing(transform, "CoreOutlineRing", r, 0.034f, VfxResources.DarkOutline, 21, 0.0010f, VfxResources.OutlineRingMaterial);
                _glowRing = VfxFactory.CreateTriangleRing(transform, "GlowRing", r, 0.036f, UiTheme.Violet, 21, 0.0005f, VfxResources.LineMaterial);
                _coreRing = VfxFactory.CreateTriangleRing(transform, "CoreRing", r, 0.018f, powerCoreCol, 21, 0.0000f, VfxResources.HdrRingMaterial);

                _approachOutlineRing = VfxFactory.CreateTriangleRing(transform, "ApproachOutlineRing", r * StartScaleMultiplier, 0.054f, VfxResources.DarkOutline, 21, -0.0015f, VfxResources.OutlineRingMaterial);
                _approachRing = VfxFactory.CreateTriangleRing(transform, "ApproachRing", r * StartScaleMultiplier, 0.034f, powerApprCol, 21, -0.0020f, VfxResources.HdrRingMaterial);
            }
            else
            {
                _coreOutlineRing = VfxFactory.CreateRing(transform, "CoreOutlineRing", r, 0.034f, VfxResources.DarkOutline, CircleSegments, 0.0010f, VfxResources.OutlineRingMaterial);
                _glowRing = VfxFactory.CreateRing(transform, "GlowRing", r, 0.036f, VfxResources.ElectricGold, CircleSegments, 0.0005f, VfxResources.LineMaterial);
                _coreRing = VfxFactory.CreateRing(transform, "CoreRing", r, 0.018f, new Color(0.82f, 0.98f, 1.00f, 0.96f), CircleSegments, 0.0000f, VfxResources.HdrRingMaterial);

                _approachOutlineRing = VfxFactory.CreateRing(transform, "ApproachOutlineRing", r * StartScaleMultiplier, 0.054f, VfxResources.DarkOutline, CircleSegments, -0.0015f, VfxResources.OutlineRingMaterial);
                _approachRing = VfxFactory.CreateRing(transform, "ApproachRing", r * StartScaleMultiplier, 0.034f, VfxResources.ElectricGold, CircleSegments, -0.0020f, VfxResources.HdrRingMaterial);
            }

            // Lock-on brackets (3 corner chevrons for triangle PowerCue; 4 arcs for normal circle)
            _bracketRoot = new GameObject("Brackets").transform;
            _bracketRoot.SetParent(transform, false);
            _bracketRoot.localPosition = new Vector3(0f, 0f, -0.0025f);
            if (IsPowerCue)
            {
                _arcs = new LineRenderer[3];
                for (int i = 0; i < 3; i++)
                {
                    _arcs[i] = CreateCornerChevron(_bracketRoot, $"Chevron{i}", r, i, 0.012f);
                }
            }
            else
            {
                const int arcCount = 4;
                const float sweep = 32f;
                _arcs = new LineRenderer[arcCount];
                for (int i = 0; i < arcCount; i++)
                {
                    _arcs[i] = CreateArc(_bracketRoot, $"Arc{i}", r, i * 360f / arcCount - sweep * 0.5f, sweep, 0.012f);
                }
            }

            // Tiny crisp center reference dot + dark backing outline
            _dotOutlineMat = VfxResources.CreateMaterial(false, VfxResources.DiscTexture);
            _dotOutline = CreateDiscQuad("CenterDotOutline", r * 0.26f, -0.0005f, _dotOutlineMat);

            _dotMat = VfxResources.CreateMaterial(true, VfxResources.DiscTexture);
            _dot = CreateDiscQuad("CenterDot", r * 0.16f, -0.0010f, _dotMat);

            if (IsPowerCue) BuildPowerExtras(r);
        }

        private Transform CreateDiscQuad(string name, float size, float localZ, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localPosition = new Vector3(0f, 0f, localZ);
            q.transform.localRotation = Quaternion.identity;
            q.transform.localScale = Vector3.one * size;
            var rend = q.GetComponent<Renderer>();
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            rend.sharedMaterial = mat;
            return q.transform;
        }

        private void BuildPowerExtras(float r)
        {
            _hexRoot = new GameObject("PowerTriangleRoot").transform;
            _hexRoot.SetParent(transform, false);
            _hexRoot.localPosition = new Vector3(0f, 0f, -0.003f);
            _hex = VfxFactory.CreateTriangleRing(_hexRoot, "PowerTriangle", r * 1.42f, 0.014f, UiTheme.Gold, 21, 0f, VfxResources.HdrRingMaterial);
        }

        private static LineRenderer CreateCornerChevron(Transform parent, string name, float radius, int vertexIndex, float width)
        {
            float vertexR = radius * 1.18f;
            Vector2 v0 = new Vector2(0f, vertexR);
            Vector2 v1 = new Vector2(-vertexR * 0.8660254f, -vertexR * 0.5f);
            Vector2 v2 = new Vector2(vertexR * 0.8660254f, -vertexR * 0.5f);

            Vector2 v = vertexIndex == 0 ? v0 : vertexIndex == 1 ? v1 : v2;
            Vector2 prev = vertexIndex == 0 ? v2 : vertexIndex == 1 ? v0 : v1;
            Vector2 next = vertexIndex == 0 ? v1 : vertexIndex == 1 ? v2 : v0;

            float armLen = radius * 0.34f;
            Vector2 p0 = v + (prev - v).normalized * armLen;
            Vector2 p1 = v;
            Vector2 p2 = v + (next - v).normalized * armLen;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            lr.positionCount = 3;
            lr.numCornerVertices = 2;
            lr.SetPosition(0, new Vector3(p0.x, p0.y, 0f));
            lr.SetPosition(1, new Vector3(p1.x, p1.y, 0f));
            lr.SetPosition(2, new Vector3(p2.x, p2.y, 0f));
            lr.widthMultiplier = width;
            lr.numCapVertices = 2;
            lr.alignment = LineAlignment.TransformZ;
            lr.textureMode = LineTextureMode.Stretch;
            lr.sharedMaterial = VfxResources.HdrRingMaterial;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private static LineRenderer CreateArc(Transform parent, string name, float radius, float startDeg, float sweepDeg, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            const int points = 9;
            lr.positionCount = points;
            for (int i = 0; i < points; i++)
            {
                float a = (startDeg + sweepDeg * i / (points - 1)) * Mathf.Deg2Rad;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
            }
            lr.widthMultiplier = width;
            lr.numCapVertices = 2;
            lr.alignment = LineAlignment.TransformZ;
            lr.textureMode = LineTextureMode.Stretch;
            lr.sharedMaterial = VfxResources.HdrRingMaterial;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private void OnDestroy()
        {
            if (_centerDimmerMat != null) Destroy(_centerDimmerMat);
            if (_dotMat != null) Destroy(_dotMat);
            if (_dotOutlineMat != null) Destroy(_dotOutlineMat);
        }
    }
}
