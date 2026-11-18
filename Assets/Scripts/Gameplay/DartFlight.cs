using System;
using System.Collections.Generic;
using UnityEngine;
using RhythmDartMR.Audio;

namespace RhythmDartMR.Gameplay
{
    /// <summary>
    /// Deterministic, non-physics scripted dart flight along a quadratic Bezier arc,
    /// with a procedural 3D Traditional Dart model (black needle tip, knurled brass/gold barrel,
    /// tapered royal-blue shaft, and 4-vane curved royal-blue flight).
    /// </summary>
    public class DartFlight : MonoBehaviour
    {
        private Vector3 _startPos;
        private Vector3 _controlPos;
        private Vector3 _targetPos;
        private double _releaseBeat;
        private double _flightDurationBeats = 1.0;
        private bool _inFlight;
        private Action<Vector3, double, GameObject> _onImpactCallback;

        private static Mesh _cachedTipMesh;
        private static Mesh _cachedBarrelMesh;
        private static Mesh _cachedShaftMesh;
        private static Mesh _cachedFlightsMesh;
        private static Texture2D _cachedKnurlTexture;

        /// <summary>True while the dart is travelling; false once it has stuck into the board.</summary>
        public bool IsInFlight => _inFlight;

        public void Launch(
            Vector3 startPos,
            Vector3 targetPos,
            double flightDurationBeats,
            Action<Vector3, double, GameObject> onImpactCallback)
        {
            _startPos = startPos;
            _targetPos = targetPos;
            _flightDurationBeats = Math.Max(0.25, flightDurationBeats);
            _onImpactCallback = onImpactCallback;

            Vector3 mid = (_startPos + _targetPos) * 0.5f;
            _controlPos = mid + Vector3.up * 0.14f;

            _releaseBeat = BeatConductor.Instance != null && BeatConductor.Instance.IsRunning
                ? BeatConductor.Instance.CurrentBeat
                : AudioSettings.dspTime * (100.0 / 60.0);

            transform.position = _startPos;
            transform.rotation = Quaternion.LookRotation((_targetPos - _startPos).normalized);
            _inFlight = true;

            // Glowing combo-coloured trail + landing wobble (created after positioning so the trail starts at the hand)
            if (GetComponent<RhythmDartMR.VFX.DartTrailFX>() == null)
            {
                gameObject.AddComponent<RhythmDartMR.VFX.DartTrailFX>();
            }
        }

        private void Update()
        {
            if (!_inFlight) return;

            double currentBeat = BeatConductor.Instance != null && BeatConductor.Instance.IsRunning
                ? BeatConductor.Instance.CurrentBeat
                : AudioSettings.dspTime * (100.0 / 60.0);

            float u = (float)((currentBeat - _releaseBeat) / _flightDurationBeats);

            if (u >= 1.0f)
            {
                _inFlight = false;
                // Embed the tip slightly into the bristle board
                transform.position = _targetPos;
                if (RhythmDartboard.Instance != null)
                {
                    transform.rotation = Quaternion.LookRotation(
                        (RhythmDartboard.Instance.transform.forward + Vector3.down * 0.08f).normalized);
                }

                double impactDspTime = AudioSettings.dspTime;
                _onImpactCallback?.Invoke(_targetPos, impactDspTime, gameObject);
                return;
            }

            float t = Mathf.Clamp01(u);
            Vector3 pos = EvaluateQuadraticBezier(_startPos, _controlPos, _targetPos, t);
            Vector3 nextPos = EvaluateQuadraticBezier(_startPos, _controlPos, _targetPos, Mathf.Min(1f, t + 0.04f));
            Vector3 tangent = nextPos - pos;
            if (tangent.sqrMagnitude > 0.00001f)
            {
                transform.rotation = Quaternion.LookRotation(tangent.normalized);
            }
            transform.position = pos;
        }

        private static Vector3 EvaluateQuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
        {
            float oneMinusT = 1f - t;
            return oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2;
        }

        /// <summary>
        /// Builds the classic 3D Traditional Dart (black steel tip, knurled gold/brass barrel,
        /// tapered blue shaft, and 4-vane curved blue heart-shaped flights).
        /// Origin (0,0,0) is placed near the front tip so when stuck in the board, the dart body extends outward.
        /// </summary>
        public static GameObject CreateHolographicDartVisual(Color accentFlightColor)
        {
            return CreateTraditionalDartVisual();
        }

        public static GameObject CreateTraditionalDartVisual()
        {
            bool neon = RhythmDartboard.Instance == null || RhythmDartboard.Instance.neonArtStyle;
            return neon ? CreateNeonDartVisual() : CreateClassicDartVisual();
        }

        /// <summary>
        /// Neon arcade dart: chrome tip, dark gunmetal knurled barrel, glowing cyan shaft, glowing magenta flights.
        /// Same meshes as the classic dart; only the materials differ (self-lit, so it reads well over passthrough).
        /// </summary>
        public static GameObject CreateNeonDartVisual()
        {
            EnsureDartMeshesCreated();
            EnsureNeonMaterialsCreated();
            var root = new GameObject("NeonDart");
            CreateMeshPart(root.transform, "ChromeTip", _cachedTipMesh, _neonTipMat);
            CreateMeshPart(root.transform, "GunmetalBarrel", _cachedBarrelMesh, _neonBarrelMat);
            CreateMeshPart(root.transform, "NeonShaft", _cachedShaftMesh, _neonShaftMat);
            CreateMeshPart(root.transform, "NeonFlights", _cachedFlightsMesh, _neonFlightMat);
            return root;
        }

        // Shared by every neon dart (no per-throw material allocations)
        private static Material _neonTipMat, _neonBarrelMat, _neonShaftMat, _neonFlightMat;

        private static void EnsureNeonMaterialsCreated()
        {
            if (_neonTipMat != null && _neonBarrelMat != null && _neonShaftMat != null && _neonFlightMat != null) return;

            _neonTipMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(0.80f, 0.82f, 0.88f), null, 0f);
            if (_neonTipMat.HasProperty("_Metallic")) _neonTipMat.SetFloat("_Metallic", 0.9f);
            if (_neonTipMat.HasProperty("_Smoothness")) _neonTipMat.SetFloat("_Smoothness", 0.85f);

            _neonBarrelMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(0.22f, 0.24f, 0.30f), _cachedKnurlTexture, 0f);
            if (_neonBarrelMat.HasProperty("_Metallic")) _neonBarrelMat.SetFloat("_Metallic", 0.8f);
            if (_neonBarrelMat.HasProperty("_Smoothness")) _neonBarrelMat.SetFloat("_Smoothness", 0.75f);

            _neonShaftMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(0.30f, 0.90f, 1.00f), null, 1.1f);
            _neonFlightMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(1.00f, 0.25f, 0.80f), null, 1.0f);
        }

        public static GameObject CreateClassicDartVisual()
        {
            EnsureDartMeshesCreated();

            var root = new GameObject("TraditionalDart");

            // 1. Matte Black Pointed Tip (z = -0.032m to +0.008m, tip point at +0.008m)
            var tipMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(0.08f, 0.08f, 0.09f), null, 0.15f);
            if (tipMat.HasProperty("_Smoothness")) tipMat.SetFloat("_Smoothness", 0.45f);
            CreateMeshPart(root.transform, "BlackPointedTip", _cachedTipMesh, tipMat);

            // 2. Knurled Gold/Brass Barrel (z = -0.095m to -0.032m)
            var barrelMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(0.88f, 0.74f, 0.42f), _cachedKnurlTexture, 0.35f);
            if (barrelMat.HasProperty("_Metallic")) barrelMat.SetFloat("_Metallic", 0.75f);
            if (barrelMat.HasProperty("_Smoothness")) barrelMat.SetFloat("_Smoothness", 0.68f);
            CreateMeshPart(root.transform, "KnurledBrassBarrel", _cachedBarrelMesh, barrelMat);

            // 3. Tapered Royal Blue Shaft (z = -0.155m to -0.095m)
            Color royalBlue = new Color(0.14f, 0.30f, 0.76f);
            var shaftMat = RhythmDartboard.CreateSafeMaterialStatic(royalBlue, null, 0.35f);
            if (shaftMat.HasProperty("_Smoothness")) shaftMat.SetFloat("_Smoothness", 0.6f);
            CreateMeshPart(root.transform, "BlueTaperedShaft", _cachedShaftMesh, shaftMat);

            // 4. Curved 4-Vane Royal Blue Flights (z = -0.215m to -0.145m)
            var flightMat = RhythmDartboard.CreateSafeMaterialStatic(new Color(0.17f, 0.34f, 0.80f), null, 0.38f);
            if (flightMat.HasProperty("_Smoothness")) flightMat.SetFloat("_Smoothness", 0.35f);
            CreateMeshPart(root.transform, "BlueCurvedFlights", _cachedFlightsMesh, flightMat);

            return root;
        }

        private static void CreateMeshPart(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static void EnsureDartMeshesCreated()
        {
            if (_cachedTipMesh != null && _cachedBarrelMesh != null && _cachedShaftMesh != null && _cachedFlightsMesh != null)
                return;

            // Profile points: Vector2(zPosition, radius)
            // 1. Pointed Black Tip (from collar at z=-0.032 to sharp point at z=+0.006)
            _cachedTipMesh = BuildLatheMesh("DartTipMesh", new Vector2[]
            {
                new Vector2(-0.032f, 0.0038f),
                new Vector2(-0.025f, 0.0021f),
                new Vector2(-0.002f, 0.0014f),
                new Vector2( 0.006f, 0.0002f)
            }, 16);

            // 2. Knurled Brass Barrel (z = -0.095 to -0.032)
            _cachedBarrelMesh = BuildLatheMesh("DartBarrelMesh", new Vector2[]
            {
                new Vector2(-0.095f, 0.0046f),
                new Vector2(-0.091f, 0.0056f),
                new Vector2(-0.042f, 0.0058f),
                new Vector2(-0.035f, 0.0050f),
                new Vector2(-0.032f, 0.0038f)
            }, 20);

            // 3. Tapered Blue Shaft (z = -0.158 to -0.095)
            _cachedShaftMesh = BuildLatheMesh("DartShaftMesh", new Vector2[]
            {
                new Vector2(-0.158f, 0.0019f),
                new Vector2(-0.125f, 0.0028f),
                new Vector2(-0.098f, 0.0042f),
                new Vector2(-0.095f, 0.0045f)
            }, 16);

            // 4. Curved Heart-Shaped 4-Vane Flights (z = -0.215 to -0.145)
            _cachedFlightsMesh = BuildCurvedFlightMesh();

            // 5. Procedural Knurled Brass Grip Texture (5 knurled bands + smooth polished bands)
            _cachedKnurlTexture = BuildKnurledBrassTexture(128, 256);
        }

        private static Texture2D BuildKnurledBrassTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            Color brightBrass = new Color(0.95f, 0.82f, 0.48f, 1f);
            Color darkGroove = new Color(0.45f, 0.36f, 0.16f, 1f);

            for (int y = 0; y < height; y++)
            {
                float v = (float)y / height; // 0 = rear of barrel, 1 = front nose
                // 5 knurled grip rings between v = 0.10 and v = 0.82
                bool inGripZone = (v >= 0.10f && v <= 0.82f);
                float bandPhase = (v - 0.10f) / 0.144f;
                float bandFrac = bandPhase - Mathf.Floor(bandPhase);
                bool inKnurlBand = inGripZone && (bandFrac > 0.18f && bandFrac < 0.82f);
                bool inRingGroove = inGripZone && (bandFrac <= 0.08f || bandFrac >= 0.92f);

                for (int x = 0; x < width; x++)
                {
                    float u = (float)x / width;
                    Color c = brightBrass;

                    if (inRingGroove)
                    {
                        c = darkGroove;
                    }
                    else if (inKnurlBand)
                    {
                        // Diamond cross-hatch knurling pattern
                        float d1 = Mathf.Sin((u * 36f + v * 90f) * Mathf.PI * 2f);
                        float d2 = Mathf.Sin((u * 36f - v * 90f) * Mathf.PI * 2f);
                        float knurl = (d1 * d2) * 0.5f + 0.5f;
                        c = Color.Lerp(darkGroove, brightBrass, Mathf.Clamp01(knurl * 0.85f + 0.15f));
                    }

                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            return tex;
        }

        private static Mesh BuildLatheMesh(string name, Vector2[] zAndRadius, int radialSegments)
        {
            Mesh mesh = new Mesh { name = name };
            int rings = zAndRadius.Length;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            float zMin = zAndRadius[0].x;
            float zMax = zAndRadius[rings - 1].x;
            float zSpan = Mathf.Max(0.0001f, zMax - zMin);

            for (int r = 0; r < rings; r++)
            {
                float z = zAndRadius[r].x;
                float rad = zAndRadius[r].y;
                float v = (z - zMin) / zSpan;

                for (int s = 0; s <= radialSegments; s++)
                {
                    float u = (float)s / radialSegments;
                    float a = u * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, z));
                    uvs.Add(new Vector2(u, v));
                }
            }

            int stride = radialSegments + 1;
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < radialSegments; s++)
                {
                    int i0 = r * stride + s;
                    int i1 = i0 + 1;
                    int i2 = (r + 1) * stride + s;
                    int i3 = i2 + 1;

                    tris.Add(i0);
                    tris.Add(i2);
                    tris.Add(i1);

                    tris.Add(i1);
                    tris.Add(i2);
                    tris.Add(i3);
                }
            }

            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds the 4-fin curved teardrop/heart dart flight matching the reference image.
        /// </summary>
        private static Mesh BuildCurvedFlightMesh()
        {
            Mesh mesh = new Mesh { name = "DartCurvedFlightMesh" };
            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Half-wing silhouette profile (z from -0.145 front of flight to -0.215 tail of flight)
            Vector2[] profile = new Vector2[]
            {
                new Vector2(-0.145f, 0.0018f), // Front stem attachment
                new Vector2(-0.158f, 0.0095f), // Swept leading edge
                new Vector2(-0.174f, 0.0185f), // Widening curve
                new Vector2(-0.190f, 0.0235f), // Max width of heart lobe
                new Vector2(-0.203f, 0.0215f), // Upper lobe curve
                new Vector2(-0.212f, 0.0140f), // Rounded top shoulder
                new Vector2(-0.207f, 0.0020f)  // Center notch at tail
            };

            // Create 4 orthogonal fins (0, 90, 180, 270 degrees), double-sided so visible from all angles
            float[] angles = { 0f, 90f, 180f, 270f };
            foreach (float deg in angles)
            {
                Quaternion rot = Quaternion.Euler(0f, 0f, deg);
                int baseIdx = verts.Count;
                int n = profile.Length;

                for (int i = 0; i < n; i++)
                {
                    verts.Add(rot * new Vector3(0f, 0f, profile[i].x));
                    verts.Add(rot * new Vector3(profile[i].y, 0f, profile[i].x));
                }

                for (int i = 0; i < n - 1; i++)
                {
                    int c0 = baseIdx + i * 2;
                    int o0 = c0 + 1;
                    int c1 = baseIdx + (i + 1) * 2;
                    int o1 = c1 + 1;

                    // Front face
                    tris.Add(c0); tris.Add(o0); tris.Add(c1);
                    tris.Add(o0); tris.Add(o1); tris.Add(c1);

                    // Back face
                    tris.Add(c0); tris.Add(c1); tris.Add(o0);
                    tris.Add(o0); tris.Add(c1); tris.Add(o1);
                }
            }

            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void SetMatColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }
    }
}
