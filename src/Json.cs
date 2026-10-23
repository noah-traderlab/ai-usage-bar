using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIUsageBar
{
    /// <summary>
    /// 依存ライブラリなしで使う小さなJSONパーサー／ライター。
    /// オブジェクトは Dictionary&lt;string, object&gt;、配列は List&lt;object&gt;、
    /// 整数は long、小数は double、文字列・bool・null はそのまま。
    /// </summary>
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            SkipWs(s, ref i);
            object v = ReadValue(s, ref i, 0);
            SkipWs(s, ref i);
            if (i != s.Length) throw new FormatException("JSONの末尾に余分なデータがあります");
            return v;
        }

        public static object TryParse(string s)
        {
            try { return Parse(s); } catch { return null; }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') i++; else break;
            }
        }

        static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > 256) throw new FormatException("JSONの入れ子が深すぎます");
            if (i >= s.Length) throw new FormatException("JSONが途中で終わっています");
            char c = s[i];
            switch (c)
            {
                case '{':
                {
                    i++;
                    var d = new Dictionary<string, object>();
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    while (true)
                    {
                        SkipWs(s, ref i);
                        if (i >= s.Length || s[i] != '"') throw new FormatException("キーが必要です");
                        string key = ReadString(s, ref i);
                        SkipWs(s, ref i);
                        if (i >= s.Length || s[i] != ':') throw new FormatException(": が必要です");
                        i++;
                        SkipWs(s, ref i);
                        d[key] = ReadValue(s, ref i, depth + 1);
                        SkipWs(s, ref i);
                        if (i >= s.Length) throw new FormatException("JSONが途中で終わっています");
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == '}') { i++; return d; }
                        throw new FormatException(", または } が必要です");
                    }
                }
                case '[':
                {
                    i++;
                    var list = new List<object>();
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    while (true)
                    {
                        SkipWs(s, ref i);
                        list.Add(ReadValue(s, ref i, depth + 1));
                        SkipWs(s, ref i);
                        if (i >= s.Length) throw new FormatException("JSONが途中で終わっています");
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == ']') { i++; return list; }
                        throw new FormatException(", または ] が必要です");
                    }
                }
                case '"':
                    return ReadString(s, ref i);
                case 't':
                    Expect(s, ref i, "true"); return true;
                case 'f':
                    Expect(s, ref i, "false"); return false;
                case 'n':
                    Expect(s, ref i, "null"); return null;
                default:
                    return ReadNumber(s, ref i);
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("不正なリテラルです");
            i += word.Length;
        }

        static string ReadString(string s, ref int i)
        {
            i++; // opening quote
            int start = i;
            // 高速パス：エスケープがない場合
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { string r = s.Substring(start, i - start); i++; return r; }
                if (c == '\\') break;
                i++;
            }
            var sb = new StringBuilder();
            sb.Append(s, start, i - start);
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("不正な\\uエスケープです");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("不正なエスケープです");
                }
            }
            throw new FormatException("文字列が閉じられていません");
        }

        static object ReadNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; i++; continue; }
                break;
            }
            if (i == start) throw new FormatException("不正な値です");
            string t = s.Substring(start, i - start);
            if (!isFloat)
            {
                long l;
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            }
            double d;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsInfinity(d) || double.IsNaN(d))
                throw new FormatException("数値が範囲外です");
            return d;
        }

        // ---------- 書き出し ----------

        public static string Serialize(object o, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, o, indent, 0);
            return sb.ToString();
        }

        static void Newline(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        static void Write(StringBuilder sb, object o, bool indent, int level)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte || o is uint || o is ulong)
            {
                sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return;
            }
            if (o is double || o is float || o is decimal)
            {
                double d = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Newline(sb, indent, level + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, level + 1);
                }
                if (!first) Newline(sb, indent, level);
                sb.Append('}');
                return;
            }
            var en = o as IEnumerable;
            if (en != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Newline(sb, indent, level + 1);
                    Write(sb, item, indent, level + 1);
                }
                if (!first) Newline(sb, indent, level);
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- 取り出しヘルパー ----------

        public static Dictionary<string, object> Obj(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static string Str(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        public static bool Has(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.ContainsKey(key) && d[key] != null;
        }

        public static long? Long(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            if (v is long) return (long)v;
            if (v is double)
            {
                double dv = (double)v;
                if (dv > long.MaxValue || dv < long.MinValue) return null;
                return (long)dv;
            }
            if (v is string)
            {
                long l;
                if (long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            }
            return null;
        }

        public static double? Num(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            if (v is long) return (long)v;
            if (v is double) return (double)v;
            if (v is string)
            {
                double x;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return x;
            }
            return null;
        }

        public static bool? Bool(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || !(v is bool)) return null;
            return (bool)v;
        }
    }
}
