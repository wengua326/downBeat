using UnityEngine;

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// "Neon arcade" art style: a procedurally generated glowing dartboard face, a neon number ring,
    /// and emissive dart materials. Everything is self-lit (emission), so it looks the same in any room,
    /// pops against passthrough, and needs no real-time lights or shadows.
    /// </summary>
    public static class NeonArt
    {
        // Palette
        private static readonly Color BoardBg = new Color(0.015f, 0.018f, 0.045f);
        private static readonly Color SingleA = new Color(0.020f, 0.024f, 0.060f);
        private static readonly Color SingleB = new Color(0.055f, 0.030f, 0.095f);
        private static readonly Color RingA = new Color(0.00f, 0.42f, 0.62f);   // cyan segments
        private static readonly Color RingB = new Color(0.62f, 0.06f, 0.42f);   // magenta segments
        private static readonly Color OuterBull = new Color(0.00f, 0.62f, 0.80f);
        private static readonly Color InnerBull = new Color(1.00f, 0.22f, 0.58f);
        private static readonly Color WireGlow = new Color(0.55f, 0.90f, 1.00f);
        private static readonly Color EdgeGlow = new Color(1.00f, 0.30f, 0.85f);

        private static Texture2D _boardTex;

        /// <summary>
        /// Builds (once) the neon board face texture. Radii are in metres; <paramref name="uvRadius"/> is the
        /// UV radius the board disc mesh maps its outer edge to (0.5 = texture edge).
        /// </summary>
        public static Texture2D BoardTexture(float boardRadius, float innerBull, float outerBull,
            float tripleIn, float tripleOut, float doubleIn, float doubleOut, float uvRadius,
            int[] sectorNumbers, int size = 1024)
        {
            if (_boardTex != null) return _boardTex;

            // Sector numbers are drawn INTO the texture (glowing seven-segment digits), so they are
            // always exactly aligned with the board — no separate floating objects.
            float numberRadius = (doubleOut + boardRadius) * 0.5f / boardRadius;
            float numBandIn = doubleOut / boardRadius;
            const float digitH = 0.085f, digitW = 0.046f, digitGap = 0.024f;
            const float strokeW = 0.0062f, strokeHalo = 0.018f;

            float inv = 1f / boardRadius;
            float rIB = innerBull * inv, rOB = outerBull * inv;
            float rTI = tripleIn * inv, rTO = tripleOut * inv;
            float rDI = doubleIn * inv, rDO = doubleOut * inv;
            float[] wires = { rIB, rOB, rTI, rTO, rDI, rDO };

            const float wireW = 0.0045f;   // core half-width (fraction of board radius)
            const float haloW = 0.016f;    // soft glow falloff
            float edgeScale = 0.5f / uvRadius; // texture-space → board-space radius

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "NeonDartboard",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };
            var px = new Color32[size * size];
            float half = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                float ny = (y - half) / half;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - half) / half;
                    float r = Mathf.Sqrt(nx * nx + ny * ny) * edgeScale; // 1.0 = board edge

                    float angleDeg = Mathf.Atan2(nx, ny) * Mathf.Rad2Deg;
                    if (angleDeg < 0f) angleDeg += 360f;
                    float shifted = (angleDeg + 9f) % 360f;
                    int sector = Mathf.FloorToInt(shifted / 18f);
                    float local = shifted - sector * 18f;
                    bool even = (sector & 1) == 0;

                    // Base zone colour
                    Color c;
                    if (r <= rIB) c = InnerBull;
                    else if (r <= rOB) c = OuterBull;
                    else if (r > rDO) c = BoardBg;
                    else if ((r >= rTI && r <= rTO) || (r >= rDI && r <= rDO)) c = even ? RingB : RingA;
                    else c = even ? SingleA : SingleB;

                    // Subtle radial sheen inside the single beds so the board isn't flat black
                    if (r > rOB && r < rDO && !(r >= rTI && r <= rTO) && !(r >= rDI && r <= rDO))
                    {
                        float sheen = Mathf.Exp(-Mathf.Abs(r - (rOB + rTI) * 0.5f) * 9f) * 0.035f
                                    + Mathf.Exp(-Mathf.Abs(r - (rTO + rDI) * 0.5f) * 9f) * 0.035f;
                        c += new Color(sheen * 0.4f, sheen * 0.5f, sheen);
                    }

                    // Neon wires: ring wires + sector spokes
                    float d = float.MaxValue;
                    for (int w = 0; w < wires.Length; w++) d = Mathf.Min(d, Mathf.Abs(r - wires[w]));
                    if (r >= rOB && r <= rDO)
                    {
                        float arc = Mathf.Min(local, 18f - local) * Mathf.Deg2Rad * r;
                        d = Mathf.Min(d, arc);
                    }
                    float core = Mathf.Exp(-(d * d) / (wireW * wireW));
                    float halo = Mathf.Exp(-d / haloW) * 0.35f;
                    c += WireGlow * (core * 0.9f + halo);

                    // Glowing outer edge tube
                    float de = Mathf.Abs(r - 0.975f);
                    c += EdgeGlow * (Mathf.Exp(-(de * de) / (0.008f * 0.008f)) * 0.9f + Mathf.Exp(-de / 0.03f) * 0.25f);

                    // Sector number (only this pixel's own sector needs testing)
                    if (sectorNumbers != null && r > numBandIn && r < 0.97f)
                    {
                        float sa = sector * 18f * Mathf.Deg2Rad;
                        float lx = nx * edgeScale - Mathf.Sin(sa) * numberRadius;
                        float ly = ny * edgeScale - Mathf.Cos(sa) * numberRadius;
                        float dn = NumberDistance(sectorNumbers[sector], lx, ly, digitW, digitH, digitGap);
                        if (dn < strokeHalo * 4f)
                        {
                            Color numCol = even ? NumberPink : NumberCyan;
                            float nCore = Mathf.Exp(-(dn * dn) / (strokeW * strokeW));
                            float nHalo = Mathf.Exp(-dn / strokeHalo) * 0.4f;
                            c += numCol * (nCore * 1.1f + nHalo);
                        }
                    }

                    // Fade to transparent black outside the board
                    if (r > 1f) c = Color.black;

                    px[y * size + x] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
                }
            }

            tex.SetPixels32(px);
            tex.Apply(true, true);
            _boardTex = tex;
            return tex;
        }

        /// <summary>Makes a Lit material glow with its own texture at full strength (light-independent neon).</summary>
        public static void MakeSelfLit(Material mat, Texture emissionMap, Color emission, float smoothness)
        {
            if (mat == null) return;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission);
                if (emissionMap != null && mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", emissionMap);
            }
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        // ---------- Seven-segment digits (distance field) ----------

        private static readonly Color NumberPink = new Color(1.00f, 0.45f, 0.88f);
        private static readonly Color NumberCyan = new Color(0.45f, 0.90f, 1.00f);

        // Segment masks a,b,c,d,e,f,g (bit 0 = a) for digits 0-9
        private static readonly int[] DigitMasks =
        {
            0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110,
            0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111
        };

        /// <summary>Distance from (x, y) to the strokes of <paramref name="number"/> drawn centred on the origin.</summary>
        private static float NumberDistance(int number, float x, float y, float w, float h, float gap)
        {
            int tens = number / 10;
            int ones = number % 10;
            int count = tens > 0 ? 2 : 1;
            float total = count * w + (count - 1) * gap;
            float left = -total * 0.5f;

            float d = float.MaxValue;
            if (tens > 0)
            {
                d = Mathf.Min(d, DigitDistance(tens, x - left, y, w, h));
                left += w + gap;
            }
            d = Mathf.Min(d, DigitDistance(ones, x - left, y, w, h));
            return d;
        }

        private static float DigitDistance(int digit, float x, float y, float w, float h)
        {
            int mask = DigitMasks[Mathf.Clamp(digit, 0, 9)];
            float hh = h * 0.5f;
            float d = float.MaxValue;
            if ((mask & 1) != 0) d = Mathf.Min(d, SegDist(x, y, 0f, hh, w, hh));     // a top
            if ((mask & 2) != 0) d = Mathf.Min(d, SegDist(x, y, w, 0f, w, hh));      // b top-right
            if ((mask & 4) != 0) d = Mathf.Min(d, SegDist(x, y, w, -hh, w, 0f));     // c bottom-right
            if ((mask & 8) != 0) d = Mathf.Min(d, SegDist(x, y, 0f, -hh, w, -hh));   // d bottom
            if ((mask & 16) != 0) d = Mathf.Min(d, SegDist(x, y, 0f, -hh, 0f, 0f));  // e bottom-left
            if ((mask & 32) != 0) d = Mathf.Min(d, SegDist(x, y, 0f, 0f, 0f, hh));   // f top-left
            if ((mask & 64) != 0) d = Mathf.Min(d, SegDist(x, y, 0f, 0f, w, 0f));    // g middle
            return d;
        }

        private static float SegDist(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float wx = px - ax, wy = py - ay;
            float t = Mathf.Clamp01((wx * vx + wy * vy) / (vx * vx + vy * vy));
            float dx = wx - vx * t, dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }
}
