using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Beastkeeper
{
    // A small JSON reader for the mod's own data files (walk frames, rider motion): objects become
    // Dictionary<string, object>, arrays List<object>, numbers double, text string, true/false bool, null null.
    // Unity's JsonUtility was tried first, but it could not read these files (numbers with an exponent, nested
    // arrays), and it says nothing when it fails.
    internal static class MiniJson
    {
        internal static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            return v;
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                i++; Skip(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    string k = Text(s, ref i);
                    Skip(s, ref i);
                    if (s[i] != ':') throw new FormatException("expected ':' at " + i);
                    i++;
                    d[k] = Value(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                List<object> l = new List<object>();
                i++; Skip(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return Text(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st) throw new FormatException("unexpected '" + c + "' at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Text(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected text at " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            i++;
            return sb.ToString();
        }

        // helpers to read the tree
        internal static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        internal static List<object> Arr(object o) { return o as List<object>; }
        internal static string Str(object o) { return o as string; }
        internal static float Num(object o, float fallback = 0f) { return o is double ? (float)(double)o : fallback; }

        internal static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        internal static float[] Floats(object o)
        {
            List<object> l = Arr(o);
            if (l == null) return new float[0];
            float[] r = new float[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Num(l[i]);
            return r;
        }

        internal static string[] Strings(object o)
        {
            List<object> l = Arr(o);
            if (l == null) return new string[0];
            string[] r = new string[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Str(l[i]) ?? "";
            return r;
        }
    }
}
