using System;
using System.Reflection;
using UnityEngine;

namespace CupRounds
{
    /// <summary>Small drawing helpers: PNGs sent by ROUNDS become textures, plus plain shapes for rings and pips.</summary>
    public static class Gfx
    {
        private static Texture2D disc, ring, white;
        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> pngs = new System.Collections.Generic.Dictionary<string, Texture2D>();
        private static readonly System.Collections.Generic.Dictionary<Texture2D, Sprite> sprites = new System.Collections.Generic.Dictionary<Texture2D, Sprite>();
        private static readonly System.Collections.Generic.Dictionary<Color, Sprite> tinted = new System.Collections.Generic.Dictionary<Color, Sprite>();

        /// <summary>A skin image from ROUNDS, decoded once (the blob looks and the death ghosts share them).</summary>
        public static Texture2D FromPng(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            Texture2D t;
            if (pngs.TryGetValue(b64, out t) && t != null) return t;
            try
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                t.LoadImage(Convert.FromBase64String(b64));
                t.filterMode = FilterMode.Bilinear;
                t.wrapMode = TextureWrapMode.Clamp;
                pngs[b64] = t;
                return t;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("bad PNG from ROUNDS: " + e.Message);
                return null;
            }
        }

        public static Sprite ToSprite(Texture2D t)
        {
            if (t == null) return null;
            Sprite s;
            if (sprites.TryGetValue(t, out s) && s != null) return s;
            s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
            sprites[t] = s;
            return s;
        }

        /// <summary>A disc in one colour (for UI Images whose colour the game animates itself), 64 px, made once per colour.</summary>
        public static Sprite TintedDisc(Color c)
        {
            Sprite s;
            if (tinted.TryGetValue(c, out s) && s != null) return s;
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = Mathf.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f)) / (n / 2f);
                    px[y * n + x] = new Color(c.r, c.g, c.b, c.a * Mathf.Clamp01((1f - r) * n / 2f));
                }
            t.SetPixels(px);
            t.Apply();
            t.filterMode = FilterMode.Bilinear;
            s = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            tinted[c] = s;
            return s;
        }

        /// <summary>A filled disc with a soft edge, 1 unit across.</summary>
        public static Sprite Disc()
        {
            if (disc == null) disc = Circle(256, 0f);
            return Sprite.Create(disc, new Rect(0, 0, 256, 256), new Vector2(0.5f, 0.5f), 256);
        }

        /// <summary>A filled disc the given number of world units across.</summary>
        public static Sprite Disc(float units)
        {
            if (disc == null) disc = Circle(256, 0f);
            return Sprite.Create(disc, new Rect(0, 0, 256, 256), new Vector2(0.5f, 0.5f), 256f / Mathf.Max(1f, units));
        }

        /// <summary>A ring, 1 unit across, the given fraction of the radius thick.</summary>
        public static Sprite Ring()
        {
            if (ring == null) ring = Circle(256, 0.86f);
            return Sprite.Create(ring, new Rect(0, 0, 256, 256), new Vector2(0.5f, 0.5f), 256);
        }

        public static Texture2D White
        {
            get
            {
                if (white == null)
                {
                    white = new Texture2D(1, 1);
                    white.SetPixel(0, 0, Color.white);
                    white.Apply();
                }
                return white;
            }
        }

        private static Texture2D Circle(int size, float inner)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r)) / r;
                    float a = Mathf.Clamp01((1f - d) * r / 1.5f);
                    if (inner > 0f) a *= Mathf.Clamp01((d - inner) * r / 1.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        public static Color Hex(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            Color c;
            if (ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out c)) return c;
            return fallback;
        }
    }

    /// <summary>Reflection on Cuphead's private fields (read from the player's own Assembly-CSharp at runtime).</summary>
    public static class Reflect
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Get(object o, string name)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = t.GetProperty(name, All);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            return null;
        }

        public static T Get<T>(object o, string name)
        {
            object v = Get(o, name);
            return v is T ? (T)v : default(T);
        }

        public static bool Set(object o, string name, object value)
        {
            if (o == null) return false;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All);
                if (f != null)
                {
                    if (f.FieldType.IsEnum && value is string) value = Enum.Parse(f.FieldType, (string)value);
                    f.SetValue(o, value);
                    return true;
                }
                PropertyInfo p = t.GetProperty(name, All);
                if (p != null)
                {
                    MethodInfo setter = p.GetSetMethod(true);
                    if (setter == null) continue;
                    if (p.PropertyType.IsEnum && value is string) value = Enum.Parse(p.PropertyType, (string)value);
                    setter.Invoke(o, new[] { value });
                    return true;
                }
            }
            return false;
        }

        public static object Call(object o, string name, params object[] args)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod(name, All);
                if (m != null) return m.Invoke(o, args);
            }
            return null;
        }

        /// <summary>Raises an event declared on o's class by invoking its backing delegate.</summary>
        public static void Raise(object o, string evt)
        {
            Delegate d = Get(o, evt) as Delegate;
            if (d != null) d.DynamicInvoke();
        }

        public static string EnumName(object o, string name)
        {
            object v = Get(o, name);
            return v == null ? "" : v.ToString();
        }
    }
}
