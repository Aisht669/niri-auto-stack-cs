using System.Globalization;

namespace NiriAutoStack;

/// <summary>
/// 列高 / 剩余空间的计算。
///
/// niri 里每一列的高度都等于工作区高度 = 屏幕高 - struts - layer-shell 独占区。
/// 独占区(waybar 那种)IPC 不暴露,所以先用屏幕高估算,再用实际窗口反推校准。
///
/// 这里刻意不用正则:System.Text.RegularExpressions 一整个子系统进 AOT 二进制
/// 要占掉 400KB 出头,而读 niri 配置只需要匹配几个固定的关键字。
/// </summary>
internal sealed class Geometry
{
    private const double DefaultGaps = 16.0;

    private readonly double _gaps;
    private readonly double _strutTop;
    private readonly double _strutBottom;
    private readonly int? _screenHeight;

    public Geometry(int? screenHeight)
    {
        (_gaps, _strutTop, _strutBottom) = ReadLayoutConfig();
        _screenHeight = screenHeight;
    }

    public double Gaps => _gaps;

    /// <summary>按屏幕高估算列高(扣掉 struts 和上下 gap)。</summary>
    public double? EstimateColumnHeight()
    {
        if (_screenHeight is null)
        {
            return null;
        }

        return _screenHeight.Value - _strutTop - _strutBottom - _gaps * 2;
    }

    /// <summary>
    /// 精确列高。
    ///
    /// 一列里的窗口如果都是 auto 高度(填满整列),那它们的高度之和
    /// + 中间的 gap 恰好就是列高。找到这样的列就用它的实测值,否则用估算值。
    /// </summary>
    public double? ColumnHeight(IReadOnlyList<WindowInfo> windows)
    {
        var est = EstimateColumnHeight();
        if (est is null || windows.Count == 0)
        {
            return est;
        }

        var columns = new Dictionary<(ulong Ws, int Col), List<WindowInfo>>();
        foreach (var w in windows)
        {
            if (w.IsFloating || w.Column == 0)
            {
                continue;
            }

            var key = (w.WorkspaceId ?? 0, w.Column);
            if (!columns.TryGetValue(key, out var list))
            {
                list = [];
                columns[key] = list;
            }

            list.Add(w);
        }

        double? best = null;
        foreach (var tiles in columns.Values)
        {
            var total = OccupiedHeight(tiles);
            // 这列填满了(比估算值略小,因为 waybar 独占区没算进去)
            if (total >= est.Value - 60 && (best is null || total > best.Value))
            {
                best = total;
            }
        }

        return best ?? est;
    }

    /// <summary>一列里所有窗口已占用的高度(含窗口之间的 gap)。</summary>
    public double OccupiedHeight(IReadOnlyList<WindowInfo> tiles)
    {
        if (tiles.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var t in tiles)
        {
            total += t.Height;
        }

        return total + _gaps * (tiles.Count - 1);
    }

    /// <summary>从 niri 配置里读 gaps 和 struts。读不到就用默认值。</summary>
    private static (double Gaps, double Top, double Bottom) ReadLayoutConfig()
    {
        foreach (var path in ConfigPaths())
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            var gaps = DefaultGaps;
            var top = 0.0;
            var bottom = 0.0;

            var pos = 0;
            while (pos < text.Length)
            {
                var lineStart = pos;
                var lineEnd = text.IndexOf('\n', pos);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }

                var line = text.AsSpan(pos, lineEnd - pos).Trim().ToString();
                pos = lineEnd + 1;

                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("gaps", StringComparison.Ordinal))
                {
                    gaps = ReadDirectiveNumber(line, "gaps") ?? gaps;
                    continue;
                }

                if (line.StartsWith("struts", StringComparison.Ordinal))
                {
                    // '{' 就在 struts 那一行上,所以要从这一行的开头找。
                    var body = ExtractBraceBlock(text, lineStart);
                    if (body is not null)
                    {
                        top = ReadDirectiveNumber(body, "top") ?? top;
                        bottom = ReadDirectiveNumber(body, "bottom") ?? bottom;
                    }
                }
            }

            return (gaps, top, bottom);
        }

        return (DefaultGaps, 0, 0);
    }

    private static IEnumerable<string> ConfigPaths()
    {
        yield return Environment.GetEnvironmentVariable("NIRI_CONFIG") ?? string.Empty;

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        yield return Path.Combine(xdg, "niri", "config.kdl");
    }

    /// <summary>从 pos 起找第一个 { ... } 块的内容。</summary>
    private static string? ExtractBraceBlock(string text, int from)
    {
        var open = text.IndexOf('{', from);
        if (open < 0)
        {
            return null;
        }

        var close = text.IndexOf('}', open);
        if (close < 0)
        {
            return null;
        }

        return text.Substring(open + 1, close - open - 1);
    }

    /// <summary>在文本里找形如 "关键字 数字" 的行,取那个数字。</summary>
    private static double? ReadDirectiveNumber(string text, string keyword)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(keyword, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line.AsSpan(keyword.Length);
            if (rest.Length == 0 || char.IsWhiteSpace(rest[0]))
            {
                var value = ParseNumber(rest.ToString());
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static double? ParseNumber(string s)
    {
        s = s.TrimStart();
        var i = 0;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            i++;
        }

        var start = i;
        while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.'))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        return double.TryParse(
            s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
