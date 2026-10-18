using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Developer aid (--roundhead-dumpcards): writes every active card's effect parts to %LOCALAPPDATA%\CupRounds\cards-dump.json:
    /// the gun / stat / block values a card changes (compared with a fresh component), and the components of every prefab it
    /// spawns (on hit, on the bullet, on the player, on block), so each effect's mechanism can be traced.
    /// </summary>
    public static class CardAudit
    {
        private static readonly Dictionary<Type, Component> defaults = new Dictionary<Type, Component>();
        private static GameObject defaultsHost;
        private static readonly HashSet<string> skip = new HashSet<string>
        {
            "Transform", "RectTransform", "CanvasRenderer", "Canvas", "CanvasScaler", "GraphicRaycaster", "Image", "RawImage", "Text",
            "TextMeshProUGUI", "TextMeshPro", "Mask", "RectMask2D", "LayoutElement", "ContentSizeFitter", "HorizontalLayoutGroup",
            "VerticalLayoutGroup", "GridLayoutGroup", "CardVisuals", "CardInfoDisplayer", "CardAnimation", "ScaleShake", "CurveAnimation",
            "GeneralParticleSystem", "SpriteRenderer", "SpriteMask", "Animator", "ParticleSystemRenderer", "SortingGroup", "AudioSource",
            "PhotonView", "CanvasGroup", "Shadow", "Outline", "SetTextColor", "CardRarityColor", "CardThemeColor", "Button"
        };

        public static void Dump()
        {
            defaultsHost = new GameObject("audit defaults");
            defaultsHost.SetActive(false);
            var sb = new StringBuilder();
            sb.Append("[\n");
            bool first = true;
            foreach (var card in Cards.All())
            {
                if (card == null) continue;
                try
                {
                    var visited = new HashSet<int>();
                    var o = new Dictionary<string, object>
                    {
                        ["id"] = Cards.Id(card),
                        ["name"] = card.cardName,
                        ["rarity"] = card.rarity.ToString(),
                        ["description"] = card.cardDestription,
                        ["stats"] = Cards.StatLines(card),
                        ["mod"] = card.GetType().Assembly.GetName().Name,
                        ["components"] = Components(card.gameObject, 0, visited),
                    };
                    if (!first) sb.Append(",\n");
                    first = false;
                    Write(sb, o, 1);
                }
                catch (Exception e) { EnginePlugin.L.LogWarning("audit " + card.cardName + ": " + e.Message); }
            }
            sb.Append("\n]\n");
            string path = Path.Combine(Proto.DataDir(), "cards-dump.json");
            File.WriteAllText(path, sb.ToString());
            UnityEngine.Object.Destroy(defaultsHost);
            EnginePlugin.L.LogInfo("card audit written: " + path);
        }

        private static List<object> Components(GameObject go, int depth, HashSet<int> visited)
        {
            var list = new List<object>();
            if (go == null || depth > 6 || !visited.Add(go.GetInstanceID())) return list;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string tn = c.GetType().Name;
                if (skip.Contains(tn) || tn.StartsWith("TMP_") || tn.StartsWith("Cinemachine")) continue;
                var entry = new Dictionary<string, object> { ["type"] = tn, ["on"] = c.gameObject == go ? "" : c.transform.name };
                var fields = Fields(c, depth, visited);
                if (fields.Count > 0) entry["fields"] = fields;
                list.Add(entry);
            }
            return list;
        }

        private static Component Default(Type t)
        {
            Component d;
            if (defaults.TryGetValue(t, out d)) return d;
            try { d = defaultsHost.AddComponent(t); } catch { d = null; }
            defaults[t] = d;
            return d;
        }

        private static Dictionary<string, object> Fields(Component c, int depth, HashSet<int> visited)
        {
            var o = new Dictionary<string, object>();
            var def = typeof(MonoBehaviour).IsAssignableFrom(c.GetType()) ? Default(c.GetType()) : null;
            foreach (var f in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.IsNotSerialized) continue;
                if (!f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length == 0) continue;
                object v;
                try { v = f.GetValue(c); } catch { continue; }
                object dv = null;
                if (def != null) { try { dv = f.GetValue(def); } catch { } }
                var val = Value(v, dv, def != null, depth, visited);
                if (val != null) o[f.Name] = val;
            }
            return o;
        }

        /// <summary>A value worth reporting (differs from the default), or null.</summary>
        private static object Value(object v, object dv, bool haveDefault, int depth, HashSet<int> visited)
        {
            if (v == null) return null;
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string)
            {
                if (haveDefault && Equals(v, dv)) return null;
                if (!haveDefault && (v is bool b ? !b : (v is string s ? s.Length == 0 : (t.IsPrimitive && Convert.ToDouble(v) == 0.0)))) return null;
                return t.IsEnum ? v.ToString() : v;
            }
            if (v is Color || v is Vector2 || v is Vector3 || v is LayerMask)
            {
                if (haveDefault && Equals(v, dv)) return null;
                return v is LayerMask lm ? (object)lm.value : v.ToString();
            }
            if (v is GameObject g) return new Dictionary<string, object> { ["prefab"] = g.name, ["components"] = Components(g, depth + 1, visited) };
            if (v is Component comp && !(v is Transform)) return new Dictionary<string, object> { ["ref"] = comp.GetType().Name + " on " + comp.name, ["components"] = Components(comp.gameObject, depth + 1, visited) };
            if (v is UnityEngine.Object uo) return null; // sprites, materials, sounds
            if (v is IList list)
            {
                if (list.Count == 0) return null;
                var items = new List<object>();
                foreach (var item in list)
                {
                    var iv = Value(item, null, false, depth, visited);
                    if (iv == null && item != null && !item.GetType().IsPrimitive && !(item is UnityEngine.Object)) iv = Plain(item, depth, visited);
                    if (iv != null) items.Add(iv);
                }
                return items.Count > 0 ? items : null;
            }
            if (t.IsClass && t.GetCustomAttributes(typeof(SerializableAttribute), true).Length > 0 && depth <= 6) return Plain(v, depth, visited);
            return null;
        }

        private static object Plain(object v, int depth, HashSet<int> visited)
        {
            var o = new Dictionary<string, object> { ["class"] = v.GetType().Name };
            foreach (var f in v.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object fv;
                try { fv = f.GetValue(v); } catch { continue; }
                var val = Value(fv, null, false, depth, visited);
                if (val != null) o[f.Name] = val;
            }
            return o.Count > 1 ? o : null;
        }

        private static void Write(StringBuilder sb, object v, int indent)
        {
            string pad = new string(' ', indent * 2);
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", " ")).Append('"'); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case IDictionary<string, object> d:
                    sb.Append("{");
                    bool f1 = true;
                    foreach (var kv in d)
                    {
                        sb.Append(f1 ? "\n" : ",\n").Append(pad).Append("  ");
                        f1 = false;
                        Write(sb, kv.Key, 0);
                        sb.Append(": ");
                        Write(sb, kv.Value, indent + 1);
                    }
                    sb.Append("\n").Append(pad).Append("}");
                    break;
                case IEnumerable e when !(v is string):
                    sb.Append("[");
                    bool f2 = true;
                    foreach (var item in e)
                    {
                        sb.Append(f2 ? "" : ", ");
                        f2 = false;
                        Write(sb, item, indent + 1);
                    }
                    sb.Append("]");
                    break;
                default:
                    if (v is float fl) sb.Append(fl.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    else if (v is double db) sb.Append(db.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    else if (v.GetType().IsPrimitive) sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                    else Write(sb, v.ToString(), indent);
                    break;
            }
        }
    }
}
