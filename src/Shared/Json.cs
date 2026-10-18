using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CupRounds
{
    /// <summary>
    /// Minimal JSON for the protocol classes (public fields of string, int, float, bool, nested classes and arrays).
    /// Unity's JsonUtility skips nested classes from a mod's own assembly, so both games use this instead.
    /// </summary>
    public static class Json
    {
        public static string Write(object o)
        {
            var sb = new StringBuilder();
            WriteValue(sb, o);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            Type t = o.GetType();
            if (o is string s) { WriteString(sb, s); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte) { sb.Append(System.Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is float f) { sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is double d) { sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (t.IsEnum) { WriteString(sb, o.ToString()); return; }
            if (o is IList list)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, list[i]);
                }
                sb.Append(']');
                return;
            }
            sb.Append('{');
            bool first = true;
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, fi.Name);
                sb.Append(':');
                WriteValue(sb, fi.GetValue(o));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static T Read<T>(string json) where T : new()
        {
            int i = 0;
            object v = Parse(json, ref i);
            return (T)Convert(v, typeof(T));
        }

        private static object Convert(object v, Type t)
        {
            if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t == typeof(string)) return v is string ? v : v.ToString();
            if (t == typeof(int)) return v is double d1 ? (int)d1 : 0;
            if (t == typeof(long)) return v is double d2 ? (long)d2 : 0L;
            if (t == typeof(float)) return v is double d3 ? (float)d3 : 0f;
            if (t == typeof(double)) return v is double d4 ? d4 : 0d;
            if (t == typeof(bool)) return v is bool b && b;
            if (t.IsEnum) return v is string es ? Enum.Parse(t, es) : Activator.CreateInstance(t);
            if (t.IsArray)
            {
                var src = v as List<object> ?? new List<object>();
                Type et = t.GetElementType();
                Array arr = Array.CreateInstance(et, src.Count);
                for (int k = 0; k < src.Count; k++) arr.SetValue(Convert(src[k], et), k);
                return arr;
            }
            var dict = v as Dictionary<string, object>;
            object o = Activator.CreateInstance(t);
            if (dict == null) return o;
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object fv;
                if (dict.TryGetValue(fi.Name, out fv)) fi.SetValue(o, Convert(fv, fi.FieldType));
            }
            return o;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Parse(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>();
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (i < s.Length)
                {
                    Skip(s, ref i);
                    string key = ParseString(s, ref i);
                    Skip(s, ref i);
                    i++; // ':'
                    d[key] = Parse(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    i++; // '}'
                    break;
                }
                return d;
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (i < s.Length)
                {
                    l.Add(Parse(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    i++; // ']'
                    break;
                }
                return l;
            }
            if (c == '"') return ParseString(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double num;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out num);
            return num;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
