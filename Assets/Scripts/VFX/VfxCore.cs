using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using RhythmDartMR.UI;

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// Global effect quality. <see cref="AutoQuality"/> lets <see cref="ImpactVfxDirector"/> drop to Low
    /// automatically if the frame rate stays below ~45 FPS (e.g. on a weaker laptop running the simulator).
    /// </summary>
    public static class VfxSettings
    {
        public static bool LowQuality = false;
        public static bool AutoQuality = true;

        /// <summary>Scales a particle count for the current quality level.</summary>
        public static int Scale(int count)
        {
            return LowQuality ? Mathf.Max(1, Mathf.RoundToInt(count * 0.4f)) : count;
        }
    }

    /// <summary>
    /// Shared procedural VFX resources: additive glow materials, soft textures, combo colour palette.
    /// Everything is generated at runtime so no asset files are required.
    /// </summary>
    public static class VfxResources
    {
        public const string ParticleShaderName = "Universal Render Pipeline/Particles/Unlit";

        // Combo tiers that drive trail / rim / impact colours.
        public const int ComboTierHot = 3;
        public const int ComboTierBlazing = 5;
        public const int ComboTierFever = 8;

        public static readonly Color Cyan = new Color(0.00f, 0.941f, 1.00f, 1f);
        public static readonly Color Magenta = new Color(1.00f, 0.00f, 0.498f, 1f);
        public static readonly Color Orange = new Color(1.00f, 0.55f, 0.12f, 1f);
        public static readonly Color Gold = new Color(1.00f, 0.86f, 0.25f, 1f);
        public static readonly Color ElectricGold = new Color(1.00f, 0.902f, 0.00f, 1f);
        public static readonly Color NeonEmerald = new Color(0.00f, 1.00f, 0.40f, 1f);
        public static readonly Color DarkOutline = new Color(0.00f, 0.00f, 0.00f, 0.88f);
        public static readonly Color MissGrey = new Color(0.70f, 0.70f, 0.78f, 1f);

        private static Material _lineMat;
        private static Material _hdrRingMat;
        private static Material _outlineRingMat;
        private static Material _centerDimmerMat;
        private static Material _shockwaveMat;
        private static Material _glowMat;
        private static Material _discMat;
        private static Texture2D _glowTex;
        private static Texture2D _lineTex;
        private static Texture2D _hdrLineTex;
        private static Texture2D _discTex;
        private static Texture2D _centerDimmerTex;
        private static Texture2D _triangleDimmerTex;
        private static Font _font;

        /// <summary>Additive material with a soft-edged strip texture (LineRenderer / TrailRenderer). Uses vertex colours.</summary>
        public static Material LineMaterial => _lineMat != null ? _lineMat : (_lineMat = CreateMaterial(true, LineTexture));

        /// <summary>Crisp SDF additive HDR material for timing and reticle rings.</summary>
        public static Material HdrRingMaterial
        {
            get
            {
                if (_hdrRingMat != null) return _hdrRingMat;
                _hdrRingMat = CreateMaterial(true, HdrLineTexture);
                _hdrRingMat.renderQueue = (int)RenderQueue.Transparent + 1;
                return _hdrRingMat;
            }
        }

        /// <summary>Crisp alpha-blended dark backing outline material for contrast behind HDR rings.</summary>
        public static Material OutlineRingMaterial
        {
            get
            {
                if (_outlineRingMat != null) return _outlineRingMat;
                _outlineRingMat = CreateMaterial(false, HdrLineTexture);
                _outlineRingMat.renderQueue = (int)RenderQueue.Transparent - 1;
                return _outlineRingMat;
            }
        }

        /// <summary>Subtle dark vignette material rendered inside the target area to dim busy board grid lines.</summary>
        public static Material CenterDimmerMaterial
        {
            get
            {
                if (_centerDimmerMat != null) return _centerDimmerMat;
                _centerDimmerMat = CreateMaterial(false, CenterDimmerTexture);
                _centerDimmerMat.renderQueue = (int)RenderQueue.Transparent - 2;
                return _centerDimmerMat;
            }
        }

        /// <summary>Additive shockwave ring material sorted in front of active target cues.</summary>
        public static Material ShockwaveMaterial
        {
            get
            {
                if (_shockwaveMat != null) return _shockwaveMat;
                _shockwaveMat = CreateMaterial(true, LineTexture);
                _shockwaveMat.renderQueue = (int)RenderQueue.Transparent + 5;
                return _shockwaveMat;
            }
        }

        /// <summary>Additive material with a radial glow texture (particles, flashes). Uses vertex colours.</summary>
        public static Material GlowMaterial
        {
            get
            {
                if (_glowMat != null) return _glowMat;
                _glowMat = CreateMaterial(true, GlowTexture);
                _glowMat.renderQueue = (int)RenderQueue.Transparent + 5;
                return _glowMat;
            }
        }

        /// <summary>Additive material with a filled, rim-lit disc texture (target fill).</summary>
        public static Material DiscMaterial => _discMat != null ? _discMat : (_discMat = CreateMaterial(true, DiscTexture));

        /// <summary>Alpha-blended soft dark halo (used behind targets for contrast). Tint via _BaseColor.</summary>
        public static Material ShadeMaterial => _shadeMat != null ? _shadeMat : (_shadeMat = CreateMaterial(false, GlowTexture));
        private static Material _shadeMat;

        public static Texture2D GlowTexture => _glowTex != null ? _glowTex : (_glowTex = BuildRadial(64, "VFX_Glow", r =>
        {
            if (r >= 1f) return 0f;
            float soft = Mathf.Pow(1f - r, 2.2f);
            float core = Mathf.Pow(1f - r, 9f) * 0.6f;
            return soft + core;
        }));

        public static Texture2D DiscTexture => _discTex != null ? _discTex : (_discTex = BuildRadial(128, "VFX_Disc", r =>
        {
            if (r >= 1f) return 0f;
            if (r > 0.86f) return 1f - (r - 0.86f) / 0.14f;
            float t = r / 0.86f;
            return 0.35f + 0.65f * t * t * t;
        }));

        public static Texture2D CenterDimmerTexture => _centerDimmerTex != null ? _centerDimmerTex : (_centerDimmerTex = BuildRadial(128, "VFX_CenterDimmer", r =>
        {
            if (r >= 1f) return 0f;
            return r > 0.88f ? 1f - (r - 0.88f) / 0.12f : 1f;
        }));

        public static Texture2D TriangleDimmerTexture => _triangleDimmerTex != null ? _triangleDimmerTex : (_triangleDimmerTex = BuildTriangleDimmer(128, "VFX_TriangleDimmer"));

        public static Texture2D LineTexture
        {
            get
            {
                if (_lineTex != null) return _lineTex;
                const int w = 4, h = 32;
                _lineTex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "VFX_SoftLine",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var px = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    float v = y / (float)(h - 1);
                    float a = Mathf.Pow(1f - Mathf.Abs(v * 2f - 1f), 1.4f);
                    for (int x = 0; x < w; x++) px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
                _lineTex.SetPixels32(px);
                _lineTex.Apply(false, true);
                return _lineTex;
            }
        }

        public static Texture2D HdrLineTexture
        {
            get
            {
                if (_hdrLineTex != null) return _hdrLineTex;
                const int w = 4, h = 64;
                _hdrLineTex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "VFX_CrispSdfLine",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var px = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    float v = y / (float)(h - 1);
                    float d = Mathf.Abs(v * 2f - 1f);
                    float a = 1f - Mathf.SmoothStep(0.55f, 0.98f, d);
                    byte ab = (byte)(Mathf.Clamp01(a) * 255f);
                    for (int x = 0; x < w; x++) px[y * w + x] = new Color32(255, 255, 255, ab);
                }
                _hdrLineTex.SetPixels32(px);
                _hdrLineTex.Apply(false, true);
                return _hdrLineTex;
            }
        }

        public static Font UiFont
        {
            get
            {
                if (_font != null) return _font;
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 64);
                return _font;
            }
        }

        public static Color ComboColor(int combo)
        {
            if (combo >= ComboTierFever) return Color.HSVToRGB(Mathf.Repeat(Time.time * 0.35f, 1f), 0.70f, 1f);
            if (combo >= ComboTierBlazing) return Orange;
            if (combo >= ComboTierHot) return Magenta;
            return Cyan;
        }

        public static Material CreateMaterial(bool additive, Texture tex)
        {
            Material m;
            Shader sh = Shader.Find(ParticleShaderName);
            if (sh != null)
            {
                m = new Material(sh);
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                if (m.HasProperty("_Blend")) m.SetFloat("_Blend", additive ? 2f : 0f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                if (m.HasProperty("_DstBlendAlpha"))
                    m.SetFloat("_DstBlendAlpha", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.DisableKeyword("_ALPHAMODULATE_ON");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                // Fallback: alpha-blended sprite shader (always available, uses vertex colours).
                Shader fallback = Shader.Find("Sprites/Default");
                if (fallback == null) fallback = Shader.Find("Unlit/Transparent");
                m = new Material(fallback);
            }

            if (tex != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            }
            SetColor(m, Color.white);
            return m;
        }

        public static void SetColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static Texture2D BuildRadial(int size, string name, System.Func<float, float> alphaOfRadius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                float dy = (y - half) / half;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float a = Mathf.Clamp01(alphaOfRadius(Mathf.Sqrt(dx * dx + dy * dy)));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D BuildTriangleDimmer(int size, string name)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            const float s = 0.96f;
            Vector2 v0 = new Vector2(0f, s);
            Vector2 v1 = new Vector2(-s * 0.8660254f, -s * 0.5f);
            Vector2 v2 = new Vector2(s * 0.8660254f, -s * 0.5f);
            const float aa = 0.07f;

            for (int y = 0; y < size; y++)
            {
                float dy = (y - half) / half;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    Vector2 p = new Vector2(dx, dy);
                    bool inside = Cross2D(p - v0, v1 - v0) <= 0f &&
                                  Cross2D(p - v1, v2 - v1) <= 0f &&
                                  Cross2D(p - v2, v0 - v2) <= 0f;
                    float dist = Mathf.Min(
                        DistToSegment2D(p, v0, v1),
                        Mathf.Min(DistToSegment2D(p, v1, v2), DistToSegment2D(p, v2, v0)));
                    float signedDist = inside ? -dist : dist;
                    float a = Mathf.Clamp01(0.5f - signedDist / (2f * aa));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static float DistToSegment2D(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = Vector2.Dot(ab, ab);
            if (lenSq <= 1e-6f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return Vector2.Distance(p, a + ab * t);
        }
    }

    /// <summary>
    /// Two long-lived world-space particle systems (stretched sparks + soft glows) that every effect
    /// emits into. Avoids creating a ParticleSystem per hit.
    /// </summary>
    public static class VfxPools
    {
        private static ParticleSystem _sparks;
        private static ParticleSystem _glows;

        private static ParticleSystem Sparks => _sparks != null ? _sparks : (_sparks = Build("VFX_SparkPool", true, 0.45f));
        private static ParticleSystem Glows => _glows != null ? _glows : (_glows = Build("VFX_GlowPool", false, 0f));

        public static void Spark(Vector3 pos, Vector3 velocity, Color color, float size, float lifetime)
        {
            Emit(Sparks, pos, velocity, color, size, lifetime);
        }

        public static void Glow(Vector3 pos, Vector3 velocity, Color color, float size, float lifetime)
        {
            Emit(Glows, pos, velocity, color, size, lifetime);
        }

        private static void Emit(ParticleSystem ps, Vector3 pos, Vector3 velocity, Color color, float size, float lifetime)
        {
            if (ps == null) return;
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = velocity,
                startColor = color,
                startSize = size,
                startLifetime = lifetime
            };
            ps.Emit(ep, 1);
        }

        private static ParticleSystem Build(string name, bool stretched, float gravity)
        {
            var go = new GameObject(name);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.duration = 5f;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = true;
            main.gravityModifier = gravity;
            main.maxParticles = 1500;
            main.startSpeed = 0f;
            main.startLifetime = 0.5f;
            main.startSize = 0.02f;

            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = false;

            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.45f), new GradientAlphaKey(0f, 1f) });
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(fade);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, stretched
                ? AnimationCurve.Linear(0f, 1f, 1f, 0f)
                : new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.3f)));

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = VfxResources.GlowMaterial;
            r.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretched)
            {
                r.velocityScale = 0.05f;
                r.lengthScale = 1.2f;
            }
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;

            ps.Play();
            return ps;
        }
    }

    /// <summary>
    /// One-call effect builders: shockwave rings, flashes, spark bursts, floating score popups.
    /// </summary>
    public static class VfxFactory
    {
        private static Transform _cachedEye;

        public static Vector3 ViewerPosition
        {
            get
            {
                if (Camera.main != null) return Camera.main.transform.position;
                if (_cachedEye == null)
                {
                    var eye = GameObject.Find("CenterEyeAnchor");
                    if (eye != null) _cachedEye = eye.transform;
                }
                return _cachedEye != null ? _cachedEye.position : new Vector3(0f, 1.5f, 0f);
            }
        }

        public static float EaseOutCubic(float t) { t = 1f - Mathf.Clamp01(t); return 1f - t * t * t; }

        public static float EaseOutBack(float t)
        {
            t = Mathf.Clamp01(t);
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        // ---------- Lines / rings ----------

        public static LineRenderer CreateRing(Transform parent, string name, float radius, float width, Color color,
            int segments = 64, float localZ = 0f, Material material = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = segments;
            lr.widthMultiplier = width;
            lr.alignment = LineAlignment.TransformZ;
            lr.textureMode = LineTextureMode.Stretch;
            lr.sharedMaterial = material != null ? material : VfxResources.LineMaterial;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            SetRingRadius(lr, radius, localZ);
            SetColor(lr, color);
            return lr;
        }

        public static void SetRingRadius(LineRenderer lr, float radius, float localZ)
        {
            if (lr == null) return;
            int n = lr.positionCount;
            float step = Mathf.PI * 2f / n;
            for (int i = 0; i < n; i++)
            {
                float a = i * step;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, localZ));
            }
        }

        public static LineRenderer CreateTriangleRing(Transform parent, string name, float radius, float width, Color color,
            int segmentsPerSide = 21, float localZ = 0f, Material material = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = Mathf.Max(3, segmentsPerSide) * 3;
            lr.numCornerVertices = 2;
            lr.widthMultiplier = width;
            lr.alignment = LineAlignment.TransformZ;
            lr.textureMode = LineTextureMode.Stretch;
            lr.sharedMaterial = material != null ? material : VfxResources.LineMaterial;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            SetTriangleRadius(lr, radius, localZ);
            SetColor(lr, color);
            return lr;
        }

        public static void SetTriangleRadius(LineRenderer lr, float radius, float localZ)
        {
            if (lr == null) return;
            int total = lr.positionCount;
            if (total < 3) return;
            int perSide = Mathf.Max(1, total / 3);
            float vertexR = radius * 1.18f;

            Vector2 v0 = new Vector2(0f, vertexR);
            Vector2 v1 = new Vector2(-vertexR * 0.8660254f, -vertexR * 0.5f);
            Vector2 v2 = new Vector2(vertexR * 0.8660254f, -vertexR * 0.5f);

            int idx = 0;
            for (int side = 0; side < 3; side++)
            {
                Vector2 a = side == 0 ? v0 : side == 1 ? v1 : v2;
                Vector2 b = side == 0 ? v1 : side == 1 ? v2 : v0;
                int count = side < 2 ? perSide : (total - idx);
                for (int i = 0; i < count && idx < total; i++)
                {
                    float t = i / (float)Mathf.Max(1, count);
                    Vector2 p = Vector2.Lerp(a, b, t);
                    lr.SetPosition(idx++, new Vector3(p.x, p.y, localZ));
                }
            }
        }

        public static void SetColor(LineRenderer lr, Color c)
        {
            if (lr == null) return;
            lr.startColor = c;
            lr.endColor = c;
        }

        public static void SetHdrColor(LineRenderer lr, Color c, float hdrIntensity = 2.5f)
        {
            if (lr == null) return;
            Color hdr = new Color(c.r * hdrIntensity, c.g * hdrIntensity, c.b * hdrIntensity, c.a);
            lr.startColor = hdr;
            lr.endColor = hdr;
        }

        // Pooled one-shot effects: recycled instead of created/destroyed per hit (no allocation spikes).
        private static readonly Stack<LineRenderer> RingPool = new Stack<LineRenderer>();
        private static readonly Stack<LineRenderer> TriangleRingPool = new Stack<LineRenderer>();
        private static readonly Stack<Renderer> FlashPool = new Stack<Renderer>();

        public static void SpawnTriangleShockwave(Vector3 center, Quaternion rotation, float startRadius, float endRadius,
            float width, Color color, float duration)
        {
            LineRenderer lr = null;
            while (TriangleRingPool.Count > 0 && lr == null) lr = TriangleRingPool.Pop();
            if (lr == null) lr = CreateTriangleRing(null, "VFX_TriangleShockwave", startRadius, width, color, 21, 0f, VfxResources.ShockwaveMaterial);
            lr.gameObject.SetActive(true);
            lr.transform.SetPositionAndRotation(center, rotation);

            TimedFx.Run(lr.gameObject, duration, t =>
            {
                SetTriangleRadius(lr, Mathf.Lerp(startRadius, endRadius, EaseOutCubic(t)), 0f);
                lr.widthMultiplier = Mathf.Lerp(width, width * 0.15f, t);
                Color c = color;
                c.a = color.a * (1f - t) * (1f - t);
                SetColor(lr, c);
            }, () =>
            {
                lr.gameObject.SetActive(false);
                TriangleRingPool.Push(lr);
            });
        }



        public static void SpawnShockwave(Vector3 center, Quaternion rotation, float startRadius, float endRadius,
            float width, Color color, float duration)
        {
            LineRenderer lr = null;
            while (RingPool.Count > 0 && lr == null) lr = RingPool.Pop();
            if (lr == null) lr = CreateRing(null, "VFX_Shockwave", startRadius, width, color, 72, 0f, VfxResources.ShockwaveMaterial);
            lr.gameObject.SetActive(true);
            lr.transform.SetPositionAndRotation(center, rotation);

            TimedFx.Run(lr.gameObject, duration, t =>
            {
                SetRingRadius(lr, Mathf.Lerp(startRadius, endRadius, EaseOutCubic(t)), 0f);
                lr.widthMultiplier = Mathf.Lerp(width, width * 0.15f, t);
                Color c = color;
                c.a = color.a * (1f - t) * (1f - t);
                SetColor(lr, c);
            }, () =>
            {
                lr.gameObject.SetActive(false);
                RingPool.Push(lr);
            });
        }

        // ---------- Flash quad ----------

        public static void SpawnFlash(Vector3 pos, Quaternion rotation, float size, Color color, float duration)
        {
            Renderer rend = null;
            while (FlashPool.Count > 0 && rend == null) rend = FlashPool.Pop();
            if (rend == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "VFX_Flash";
                var collider = go.GetComponent<Collider>();
                if (collider != null) Object.Destroy(collider);
                rend = go.GetComponent<Renderer>();
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
                rend.sharedMaterial = new Material(VfxResources.GlowMaterial); // one instance per pooled quad
            }

            GameObject quad = rend.gameObject;
            Material mat = rend.sharedMaterial;
            quad.SetActive(true);
            quad.transform.SetPositionAndRotation(pos, rotation);

            TimedFx.Run(quad, duration, t =>
            {
                quad.transform.localScale = Vector3.one * size * (0.45f + 0.9f * EaseOutCubic(t));
                Color c = color;
                c.a = color.a * (1f - t) * (1f - t);
                VfxResources.SetColor(mat, c);
            }, () =>
            {
                quad.SetActive(false);
                FlashPool.Push(rend);
            });
        }

        // ---------- Particle bursts ----------

        public static void SparkBurst(Vector3 pos, Vector3 normal, Color color, int count, float speed, float size, float lifetime)
        {
            count = VfxSettings.Scale(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = (Random.onUnitSphere + normal * 1.3f).normalized;
                Color c = Color.Lerp(color, Color.white, Random.value * 0.55f);
                VfxPools.Spark(pos, dir * speed * Random.Range(0.45f, 1f), c,
                    size * Random.Range(0.6f, 1f), lifetime * Random.Range(0.6f, 1f));
            }
        }

        /// <summary>Evenly spaced streaks shooting outward in the plane of <paramref name="rotation"/> (star burst).</summary>
        public static void RadialStreaks(Vector3 pos, Quaternion rotation, Color color, int count, float speed, float size, float lifetime)
        {
            Vector3 normal = rotation * Vector3.back;
            count = VfxSettings.Scale(count);
            float offset = Random.value * Mathf.PI * 2f;
            for (int i = 0; i < count; i++)
            {
                float a = offset + i * Mathf.PI * 2f / count;
                Vector3 dir = rotation * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                VfxPools.Spark(pos, dir * speed + normal * 0.25f, Color.Lerp(color, Color.white, 0.4f), size, lifetime);
            }
        }

        /// <summary>Sparks spread around a circle's perimeter, flying outward.</summary>
        public static void RingSparks(Vector3 center, Quaternion rotation, float radius, Color color, int count, float speed, float size, float lifetime)
        {
            Vector3 normal = rotation * Vector3.back;
            count = VfxSettings.Scale(count);
            for (int i = 0; i < count; i++)
            {
                float a = (i + Random.value * 0.5f) * Mathf.PI * 2f / count;
                Vector3 dir = rotation * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                VfxPools.Glow(center + dir * radius, dir * speed + normal * 0.08f, color, size, lifetime);
            }
        }

        // ---------- Floating text: modern score chips + call-out banners ----------

        private sealed class ChipHandle
        {
            public GameObject Root;
            public CanvasGroup Group;
            public Image Frame;
            public Image Accent;
            public Text Label;
            public Text Value;
        }

        private sealed class CalloutHandle
        {
            public GameObject Root;
            public CanvasGroup Group;
            public Text Eyebrow;
            public Text Headline;
            public Image LineL;
            public Image LineR;
            public Image Glow;
        }

        private static readonly Stack<ChipHandle> ChipPool = new Stack<ChipHandle>();
        private static readonly Stack<CalloutHandle> CalloutPool = new Stack<CalloutHandle>();

        private static void FaceViewer(Transform t)
        {
            Vector3 away = t.position - ViewerPosition;
            if (away.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        /// <summary>
        /// High-contrast world-space score popup that rises rapidly upward and outward from the impact point and vanishes cleanly.
        /// </summary>
        public static void SpawnScoreChip(Vector3 pos, string label, string value, Color accent, float scale = 1f, float duration = 0.45f)
        {
            ChipHandle h = null;
            while (ChipPool.Count > 0 && (h == null || h.Root == null)) h = ChipPool.Pop();
            if (h == null || h.Root == null) h = CreateChip();

            h.Root.SetActive(true);
            h.Root.transform.position = pos;
            h.Label.text = string.IsNullOrEmpty(label) ? "" : UiTheme.Tracked(label);
            h.Label.color = accent;
            h.Value.text = value;
            h.Accent.color = accent;
            if (h.Frame != null) h.Frame.color = accent;
            float baseScale = 0.00135f * scale;
            Vector3 start = pos;
            Transform tr = h.Root.transform;

            Vector3 dir = Vector3.up;
            if (RhythmDartMR.Gameplay.RhythmDartboard.Instance != null)
            {
                Vector3 boardPos = RhythmDartMR.Gameplay.RhythmDartboard.Instance.transform.position;
                Vector3 boardFwd = RhythmDartMR.Gameplay.RhythmDartboard.Instance.transform.forward;
                Vector3 inPlane = Vector3.ProjectOnPlane(pos - boardPos, boardFwd);
                if (inPlane.sqrMagnitude > 0.0004f)
                {
                    dir = inPlane.normalized;
                }
            }
            Vector3 velocity = dir * 0.25f;

            TimedFx.Run(h.Root, duration, t =>
            {
                float pop = EaseOutBack(t / 0.20f);
                tr.localScale = Vector3.one * baseScale * Mathf.Lerp(0.75f, 1.0f, pop);
                tr.position = start + velocity * (t * duration);
                FaceViewer(tr);
                h.Group.alpha = t < 0.25f ? Mathf.Clamp01(t / 0.10f) : Mathf.Clamp01(1f - (t - 0.25f) / 0.75f);
            }, () =>
            {
                h.Root.SetActive(false);
                ChipPool.Push(h);
            });
        }

        /// <summary>
        /// Clean banner for big moments: tracked eyebrow with hairlines growing out of it, large headline with soft bloom.
        /// </summary>
        public static void SpawnCallout(Vector3 pos, string eyebrow, string headline, Color accent, float scale = 1f, float duration = 1.6f)
        {
            CalloutHandle h = null;
            while (CalloutPool.Count > 0 && (h == null || h.Root == null)) h = CalloutPool.Pop();
            if (h == null || h.Root == null) h = CreateCallout();

            h.Root.SetActive(true);
            h.Root.transform.position = pos;
            h.Eyebrow.text = UiTheme.Tracked(eyebrow);
            h.Eyebrow.color = accent;
            h.Headline.text = headline;
            h.LineL.color = accent;
            h.LineR.color = accent;
            h.Glow.color = new Color(accent.r, accent.g, accent.b, 0.22f);
            float baseScale = 0.001f * scale;
            Vector3 start = pos;
            Transform tr = h.Root.transform;
            float eyebrowHalf = Mathf.Max(120f, eyebrow.Length * 16f);

            TimedFx.Run(h.Root, duration, t =>
            {
                float appear = UiTheme.EaseOutCubic(t / 0.18f);
                tr.localScale = Vector3.one * baseScale;
                tr.position = start + Vector3.up * (t > 0.7f ? 0.06f * (t - 0.7f) / 0.3f : 0f);
                tr.rotation = RhythmDartMR.Gameplay.RhythmDartboard.Instance != null
                    ? RhythmDartMR.Gameplay.RhythmDartboard.Instance.transform.rotation
                    : Quaternion.identity;
                h.Headline.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.12f, 1f, appear);
                float lineW = 240f * UiTheme.EaseOutCubic((t - 0.05f) / 0.3f);
                h.LineL.rectTransform.sizeDelta = new Vector2(lineW, 2f);
                h.LineR.rectTransform.sizeDelta = new Vector2(lineW, 2f);
                h.LineL.rectTransform.anchoredPosition = new Vector2(-eyebrowHalf - 20f - lineW * 0.5f, 78f);
                h.LineR.rectTransform.anchoredPosition = new Vector2(eyebrowHalf + 20f + lineW * 0.5f, 78f);
                h.Group.alpha = t < 0.7f ? appear : 1f - (t - 0.7f) / 0.3f;
            }, () =>
            {
                h.Root.SetActive(false);
                CalloutPool.Push(h);
            });
        }

        private static ChipHandle CreateChip()
        {
            var root = UiTheme.CreateWorldCanvas("VFX_ScoreChip", null, new Vector2(540f, 200f), out _);
            var group = root.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            UiTheme.Panel(root, "Glass", Vector2.zero, new Vector2(450f, 162f), UiTheme.Rounded, UiTheme.Glass, 1.2f);
            var frame = UiTheme.Panel(root, "Frame", Vector2.zero, new Vector2(450f, 162f), UiTheme.NeonFrame, UiTheme.Accent, 1.2f);
            var accent = UiTheme.Panel(root, "Accent", new Vector2(0f, -74f), new Vector2(180f, 8f), UiTheme.Pill, Color.white, 0.15f);
            var label = UiTheme.Label(root, "Label", new Vector2(0f, 38f), new Vector2(420f, 44f), "", 36, Color.white, display: true);
            var value = UiTheme.Label(root, "Value", new Vector2(0f, -16f), new Vector2(420f, 96f), "", 92, UiTheme.TextPrimary, display: true);
            root.gameObject.SetActive(false);

            return new ChipHandle { Root = root.gameObject, Group = group, Frame = frame, Accent = accent, Label = label, Value = value };
        }

        private static CalloutHandle CreateCallout()
        {
            var root = UiTheme.CreateWorldCanvas("VFX_Callout", null, new Vector2(1800f, 360f), out _);
            var group = root.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            var glow = UiTheme.Panel(root, "Glow", new Vector2(0f, -10f), new Vector2(1100f, 320f), UiTheme.Glow, Color.white);
            var lineL = UiTheme.Panel(root, "LineL", new Vector2(-300f, 78f), new Vector2(0f, 2f), null, Color.white);
            var lineR = UiTheme.Panel(root, "LineR", new Vector2(300f, 78f), new Vector2(0f, 2f), null, Color.white);
            var eyebrow = UiTheme.Label(root, "Eyebrow", new Vector2(0f, 78f), new Vector2(1400f, 50f), "", 32, Color.white);
            var headline = UiTheme.Label(root, "Headline", new Vector2(0f, -20f), new Vector2(1700f, 150f), "", 124, UiTheme.TextPrimary, display: true);
            root.gameObject.SetActive(false);

            return new CalloutHandle
            {
                Root = root.gameObject, Group = group, Eyebrow = eyebrow, Headline = headline,
                LineL = lineL, LineR = lineR, Glow = glow
            };
        }
    }
}
