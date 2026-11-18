using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace RhythmDartMR.UI
{
    /// <summary>
    /// Shared visual language for every piece of UI (HUD, popups, call-outs, power-up cards):
    /// a clean modern sans-serif (Bahnschrift on Windows / Roboto on Quest), dark glass panels with
    /// hairline borders, tracked-out small-caps labels and a restrained neon accent palette.
    /// All sprites are generated at runtime — no assets needed.
    /// </summary>
    public static class UiTheme
    {
        // ---------------------------------------------------------------- palette
        public static readonly Color Glass = new Color(0.043f, 0.059f, 0.098f, 0.94f);       // #0B0F19 @ 0.94
        public static readonly Color GlassRaised = new Color(0.086f, 0.122f, 0.188f, 0.88f); // #161F30 @ 0.88
        public static readonly Color SubCard = new Color(0.086f, 0.122f, 0.188f, 0.85f);     // #161F30 @ 0.85
        public static readonly Color Hairline = new Color(1f, 1f, 1f, 0.22f);
        public static readonly Color TextPrimary = new Color(0.98f, 0.99f, 1.00f, 1f);
        public static readonly Color TextSecondary = new Color(0.76f, 0.84f, 0.96f, 1f);
        public static readonly Color TextMuted = new Color(0.55f, 0.62f, 0.76f, 1f);
        public static readonly Color Accent = new Color(0.00f, 0.941f, 1.00f, 1f);           // #00F0FF neon cyan
        public static readonly Color Accent2 = new Color(1.00f, 0.00f, 0.498f, 1f);          // #FF007F neon magenta
        public static readonly Color Gold = new Color(1.00f, 0.80f, 0.32f, 1f);
        public static readonly Color Violet = new Color(0.68f, 0.48f, 1.00f, 1f);
        public static readonly Color Orange = new Color(1.00f, 0.58f, 0.20f, 1f);
        public static readonly Color Success = new Color(0.00f, 1.00f, 0.40f, 1f);
        public static readonly Color Danger = new Color(1.00f, 0.34f, 0.38f, 1f);

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        // ---------------------------------------------------------------- fonts
        private static Font _display;
        private static Font _body;
        private static HashSet<string> _installed;

        /// <summary>Headline / number font (bold).</summary>
        public static Font Display => _display != null ? _display : (_display = PickFont(new[]
        {
            "Bahnschrift Bold", "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Bold", "Segoe UI Semibold",
            "Roboto Bold", "Arial Bold", "Helvetica Neue", "Arial"
        }));

        /// <summary>Label / body font (bold for crisp VR readability).</summary>
        public static Font Body => _body != null ? _body : (_body = PickFont(new[]
        {
            "Bahnschrift SemiBold", "Bahnschrift Bold", "Bahnschrift", "Segoe UI Bold", "Segoe UI Semibold",
            "Roboto Bold", "Arial Bold", "Helvetica Neue", "Arial"
        }));

        private static Font PickFont(string[] preferences)
        {
            try
            {
                if (_installed == null)
                    _installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
                foreach (var name in preferences)
                {
                    if (!_installed.Contains(name)) continue;
                    var f = Font.CreateDynamicFontFromOSFont(name, 128);
                    if (f != null) return f;
                }
            }
            catch
            {
            }
            return VFX.VfxResources.UiFont;
        }

        /// <summary>Wide letter-spacing for small-caps labels ("SCORE" → "S C O R E" with thin spaces).</summary>
        public static string Tracked(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var chars = new char[s.Length * 2 - 1];
            for (int i = 0; i < s.Length; i++)
            {
                chars[i * 2] = s[i];
                if (i < s.Length - 1) chars[i * 2 + 1] = ' '; // thin space
            }
            return new string(chars);
        }

        // ---------------------------------------------------------------- sprites
        private static Sprite _rounded, _frame, _neonFrame, _pill, _pillFrame, _glow, _ring, _white, _cyanMagentaGradient;

        /// <summary>Filled rounded rectangle (9-sliced).</summary>
        public static Sprite Rounded => _rounded != null ? _rounded : (_rounded = BuildRounded(22f, false, "UI_Rounded"));
        /// <summary>1.5 px hairline rounded border (9-sliced).</summary>
        public static Sprite Frame => _frame != null ? _frame : (_frame = BuildRounded(22f, true, "UI_Frame"));
        /// <summary>Crisp glowing rounded neon border (9-sliced).</summary>
        public static Sprite NeonFrame => _neonFrame != null ? _neonFrame : (_neonFrame = BuildNeonFrame(42f, "UI_NeonFrame"));
        /// <summary>Fully rounded capsule (9-sliced).</summary>
        public static Sprite Pill => _pill != null ? _pill : (_pill = BuildRounded(31f, false, "UI_Pill"));
        public static Sprite PillFrame => _pillFrame != null ? _pillFrame : (_pillFrame = BuildRounded(31f, true, "UI_PillFrame"));
        /// <summary>Soft radial glow (bloom imitation behind numbers / badges).</summary>
        public static Sprite Glow => _glow != null ? _glow : (_glow = BuildRadial(128, r => r >= 1f ? 0f : Mathf.Pow(1f - r, 2.4f), "UI_Glow"));
        /// <summary>Thin anti-aliased ring.</summary>
        public static Sprite Ring => _ring != null ? _ring : (_ring = BuildRadial(256, RingAlpha, "UI_Ring"));
        /// <summary>Horizontal Neon Cyan (#00F0FF) to Neon Magenta (#FF007F) gradient for progress bars.</summary>
        public static Sprite CyanMagentaGradient => _cyanMagentaGradient != null ? _cyanMagentaGradient : (_cyanMagentaGradient = BuildHorizontalGradient(Accent, Accent2, "UI_CyanMagentaGradient"));
        /// <summary>Plain white (needed for Filled images).</summary>
        public static Sprite White
        {
            get
            {
                if (_white != null) return _white;
                var t = Texture2D.whiteTexture;
                _white = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
                return _white;
            }
        }

        private static float RingAlpha(float r)
        {
            const float aa = 1f / 127f;
            return Mathf.Clamp01((r - 0.90f) / aa) * Mathf.Clamp01((0.975f - r) / aa);
        }

        private static Sprite BuildRounded(float radius, bool borderOnly, string name)
        {
            const int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[s * s];
            float half = s * 0.5f;
            float inner = half - radius - 1f;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float qx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - inner, 0f);
                    float qy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - inner, 0f);
                    float d = Mathf.Sqrt(qx * qx + qy * qy) - radius; // < 0 inside
                    float a = borderOnly
                        ? Mathf.Clamp01(1.5f - Mathf.Abs(d + 1.2f))
                        : Mathf.Clamp01(0.5f - d);
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            float b = Mathf.Ceil(radius + 2f);
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        private static Sprite BuildNeonFrame(float radius, string name)
        {
            const int s = 128;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[s * s];
            float half = s * 0.5f;
            float inner = half - radius - 8f;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float qx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - inner, 0f);
                    float qy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - inner, 0f);
                    float d = Mathf.Sqrt(qx * qx + qy * qy) - radius;
                    float core = Mathf.Clamp01(1f - Mathf.Max(0f, Mathf.Abs(d) - 1.25f) / 1.2f);
                    float halo = Mathf.Exp(-(d * d) / (4.5f * 4.5f)) * 0.65f * Mathf.Clamp01((7.5f - d) / 1.5f);
                    float a = Mathf.Clamp01(Mathf.Max(core, halo));
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            float b = Mathf.Ceil(radius + 10f);
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        private static Sprite BuildHorizontalGradient(Color left, Color right, string name)
        {
            const int w = 128, h = 16;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[w * h];
            for (int x = 0; x < w; x++)
            {
                Color c = Color.Lerp(left, right, x / (float)(w - 1));
                Color32 c32 = c;
                for (int y = 0; y < h; y++)
                {
                    px[y * w + x] = c32;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite BuildRadial(int s, Func<float, float> alpha, string name)
        {
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear
            };
            var px = new Color32[s * s];
            float half = (s - 1) * 0.5f;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float dx = (x - half) / half, dy = (y - half) / half;
                    float a = Mathf.Clamp01(alpha(Mathf.Sqrt(dx * dx + dy * dy)));
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
        }

        // ---------------------------------------------------------------- builders

        /// <summary>World-space canvas where 1 unit = 1 mm.</summary>
        public static RectTransform CreateWorldCanvas(string name, Transform parent, Vector2 sizeMm, out Canvas canvas)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * 0.001f;
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            if (Camera.main != null) canvas.worldCamera = Camera.main;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = sizeMm;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 5f;
            return rt;
        }

        public static RectTransform Node(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Image. Sliced sprites use <paramref name="cornerScale"/> to size their rounded corners.</summary>
        public static Image Panel(Transform parent, string name, Vector2 pos, Vector2 size, Sprite sprite, Color color, float cornerScale = 1f)
        {
            var rt = Node(parent, name, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            if (sprite != null && sprite.border.sqrMagnitude > 0f)
            {
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 1f / Mathf.Max(0.01f, cornerScale);
            }
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static Text Label(Transform parent, string name, Vector2 pos, Vector2 size, string text, int fontSize,
            Color color, bool display = false, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var rt = Node(parent, name, pos, size);
            var txt = rt.gameObject.AddComponent<Text>();
            txt.font = display ? Display : Body;
            txt.fontSize = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = align;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.supportRichText = true;
            txt.raycastTarget = false;
            txt.color = color;
            txt.text = text;
            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
            return txt;
        }

        /// <summary>Paragraph text that wraps inside its rect.</summary>
        public static Text Paragraph(Transform parent, string name, Vector2 pos, Vector2 size, string text, int fontSize, Color color)
        {
            var t = Label(parent, name, pos, size, text, fontSize, color);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.lineSpacing = 1.1f;
            return t;
        }

        /// <summary>Four L-shaped corner brackets (modern HUD framing). Returns the 8 bar images.</summary>
        public static Image[] CornerBrackets(Transform parent, Vector2 center, Vector2 size, float arm, float thickness, Color color)
        {
            var list = new Image[8];
            float hx = size.x * 0.5f, hy = size.y * 0.5f;
            int i = 0;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    Vector2 corner = center + new Vector2(sx * hx, sy * hy);
                    list[i++] = Panel(parent, "BracketH", corner + new Vector2(-sx * arm * 0.5f, -sy * thickness * 0.5f),
                        new Vector2(arm, thickness), null, color);
                    list[i++] = Panel(parent, "BracketV", corner + new Vector2(-sx * thickness * 0.5f, -sy * arm * 0.5f),
                        new Vector2(thickness, arm), null, color);
                }
            }
            return list;
        }

        public static void Tint(Graphic g, Color c)
        {
            if (g != null) g.canvasRenderer.SetColor(c);
        }

        public static float EaseOutCubic(float t) { t = 1f - Mathf.Clamp01(t); return 1f - t * t * t; }
        public static float EaseInOut(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
    }
}
