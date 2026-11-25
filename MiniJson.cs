using System.Globalization;
using System.Text;

namespace NiriAutoStack;

/// <summary>
/// 极简 JSON 解析器。
///
/// 为什么要自己写:System.Text.Json 即使用了源码生成,底层那套转换器、
/// 转义、反射元数据的机器码仍有 300KB 出头。对这个程序来说,只需要读懂
/// niri IPC 那几种固定结构,一个几百行的手写解析器反而更省。
///
/// 解析结果:
///   对象 → Dictionary&lt;string, object?&gt;
///   数组 → List&lt;object?&gt;
///   字符串 → string
///   数字 → double
///   布尔 → bool
///   null → null
/// </summary>
internal static class MiniJson
{
    public static object? Parse(string text)
    {
        var p = 0;
        var value = ParseValue(text, ref p);
        return value;
    }

    /// <summary>取对象里的某个字段。</summary>
    public static object? Field(object? node, string name)
    {
        if (node is not Dictionary<string, object?> obj)
        {
            return null;
        }

        return obj.TryGetValue(name, out var v) ? v : null;
    }

    public static double? Number(object? node)
    {
        return node is double d ? d : null;
    }

    public static string? Str(object? node)
    {
        return node as string;
    }

    public static bool Bool(object? node)
    {
        return node is true;
    }

    public static List<object?>? Array(object? node)
    {
        return node as List<object?>;
    }

    private static object? ParseValue(string s, ref int p)
    {
        SkipWs(s, ref p);
        if (p >= s.Length)
        {
            throw new FormatException("JSON 提前结束");
        }

        switch (s[p])
        {
            case '{':
                return ParseObject(s, ref p);
            case '[':
                return ParseArray(s, ref p);
            case '"':
                return ParseString(s, ref p);
            case 't':
                Expect(s, ref p, "true");
                return true;
            case 'f':
                Expect(s, ref p, "false");
                return false;
            case 'n':
                Expect(s, ref p, "null");
                return null;
            default:
                return ParseNumber(s, ref p);
        }
    }

    private static Dictionary<string, object?> ParseObject(string s, ref int p)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        p++; // '{'
        SkipWs(s, ref p);

        if (p < s.Length && s[p] == '}')
        {
            p++;
            return result;
        }

        while (true)
        {
            SkipWs(s, ref p);
            var key = ParseString(s, ref p);
            SkipWs(s, ref p);
            if (p >= s.Length || s[p] != ':')
            {
                throw new FormatException("JSON 对象里缺 ':'");
            }

            p++;
            result[key] = ParseValue(s, ref p);
            SkipWs(s, ref p);

            if (p >= s.Length)
            {
                throw new FormatException("JSON 对象没闭合");
            }

            if (s[p] == ',')
            {
                p++;
                continue;
            }

            if (s[p] == '}')
            {
                p++;
                return result;
            }

            throw new FormatException("JSON 对象里有意外字符");
        }
    }

    private static List<object?> ParseArray(string s, ref int p)
    {
        var result = new List<object?>();
        p++; // '['
        SkipWs(s, ref p);

        if (p < s.Length && s[p] == ']')
        {
            p++;
            return result;
        }

        while (true)
        {
            result.Add(ParseValue(s, ref p));
            SkipWs(s, ref p);

            if (p >= s.Length)
            {
                throw new FormatException("JSON 数组没闭合");
            }

            if (s[p] == ',')
            {
                p++;
                continue;
            }

            if (s[p] == ']')
            {
                p++;
                return result;
            }

            throw new FormatException("JSON 数组里有意外字符");
        }
    }

    private static string ParseString(string s, ref int p)
    {
        if (p >= s.Length || s[p] != '"')
        {
            throw new FormatException("JSON 里期望一个字符串");
        }

        p++;
        var sb = new StringBuilder();

        while (true)
        {
            if (p >= s.Length)
            {
                throw new FormatException("JSON 字符串没闭合");
            }

            var c = s[p++];
            if (c == '"')
            {
                return sb.ToString();
            }

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (p >= s.Length)
            {
                throw new FormatException("JSON 转义符没闭合");
            }

            var e = s[p++];
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
                    if (p + 4 > s.Length)
                    {
                        throw new FormatException("JSON 的 \\u 转义不完整");
                    }

                    var hex = s.AsSpan(p, 4);
                    if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new FormatException("JSON 的 \\u 转义不是合法十六进制");
                    }

                    p += 4;
                    sb.Append((char)code);
                    break;
                default:
                    throw new FormatException("JSON 有未知的转义符");
            }
        }
    }

    private static double ParseNumber(string s, ref int p)
    {
        var start = p;
        if (p < s.Length && (s[p] == '-' || s[p] == '+'))
        {
            p++;
        }

        while (p < s.Length && ((s[p] >= '0' && s[p] <= '9') || s[p] == '.' || s[p] == 'e' || s[p] == 'E'
            || ((s[p] == '-' || s[p] == '+') && (s[p - 1] == 'e' || s[p - 1] == 'E'))))
        {
            p++;
        }

        var text = s.AsSpan(start, p - start);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"JSON 里的数字不合法: {text.ToString()}");
        }

        return value;
    }

    private static void Expect(string s, ref int p, string literal)
    {
        if (p + literal.Length > s.Length || string.CompareOrdinal(s, p, literal, 0, literal.Length) != 0)
        {
            throw new FormatException($"JSON 里期望 {literal}");
        }

        p += literal.Length;
    }

    private static void SkipWs(string s, ref int p)
    {
        while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r'))
        {
            p++;
        }
    }
}
