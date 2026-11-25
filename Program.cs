namespace NiriAutoStack;

/// <summary>
/// niri:新开的 kitty 自动并入左边那一列,填进该列的剩余空间,而不是新开一列。
///
/// 背景
/// ----
/// niri 有条铁律 —— 新窗口永远新开一列,绝不自动填进已有列。
/// 源码 src/layout/scrolling.rs 的 add_column() 固定插到 active_column_idx + 1,
/// 这不是疏漏,是设计原则(新窗口不该改变已有窗口的大小)。
///
/// 所以想让新 kitty 填进上一列的剩余空间,只能靠 consume 系列动作。
/// 本程序监听 IPC 事件流,收到新窗口信号后自动执行。
///
/// 判断逻辑(用户给定的伪代码)
/// --------------------------
///     列高      = 一列能占的总高度(所有工作区都一样)
///     上一列已用 = 该列各窗口高度之和 + 窗口之间的 gap
///     剩余空间  = 列高 - 上一列已用
///
///     if (剩余空间 >= 新窗口高度)
///         → consume 进去,填进剩余空间
///     else
///         → 不动,让它自己开新列
///
/// 实现要点
/// --------
///  · 直接连 niri 的 UNIX socket,不用 spawn `niri msg` 子进程。
///    事件流占一条连接,查询走另一条。
///  · 事件流只用来「发现新窗口」;判断所需的几何数据随时用 Windows 请求查,
///    一次 unix socket 往返约 0.1ms,比 fork 一个进程便宜三个数量级。
///  · niri 的窗口 id 单调递增,「id 大于见过的最大值」即新窗口。
///  · 为了二进制小,JSON 和 niri 配置的解析都是手写的,没用
///    System.Text.Json 和 System.Text.RegularExpressions(这两块加起来占 800KB)。
/// </summary>
internal static class Program
{
    /// <summary>只对这些 app_id 自动归列。</summary>
    private static readonly HashSet<string> TargetAppIds = new(StringComparer.Ordinal) { "kitty" };

    /// <summary>像素容差。吸收 gaps、struts、layer-shell 独占区(waybar)、取整误差。</summary>
    private const double Tolerance = 8.0;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2.0);

    private static void Main()
    {
        var socketPath = Environment.GetEnvironmentVariable("NIRI_SOCKET");
        if (string.IsNullOrEmpty(socketPath))
        {
            Console.Error.WriteLine("auto-stack: NIRI_SOCKET 没设置,退出");
            return;
        }

        while (true)
        {
            try
            {
                Run(socketPath);
            }
            catch (Exception ex)
            {
                Log($"连接断开({ex.GetType().Name}: {ex.Message}),3 秒后重连");
            }

            Thread.Sleep(3000);
        }
    }

    private static void Run(string socketPath)
    {
        using var events = new NiriIpc(socketPath);
        using var query = new NiriIpc(socketPath);

        var geom = new Geometry(QueryScreenHeight(query));
        var est = geom.EstimateColumnHeight();
        Log(
            $"监听中 gaps={geom.Gaps:F0} " +
            $"估算列高={(est is null ? "?" : est.Value.ToString("F0"))} " +
            $"目标={string.Join(",", TargetAppIds)}"
        );

        events.StartEventStream();

        // niri 的窗口 id 单调递增,所以「id 大于见过的最大值」就是新窗口。
        ulong maxSeenId = 0;
        foreach (var w in QueryWindows(query))
        {
            if (w.Id > maxSeenId)
            {
                maxSeenId = w.Id;
            }
        }

        while (true)
        {
            var line = events.ReadLine();
            if (line is null)
            {
                throw new IOException("事件流结束");
            }

            Dictionary<string, object?>? ev;
            try
            {
                ev = MiniJson.Parse(line) as Dictionary<string, object?>;
            }
            catch (FormatException)
            {
                continue;
            }

            if (ev is null)
            {
                continue;
            }

            // 事件流初始快照:带全部窗口。
            if (ev.TryGetValue("WindowsChanged", out var wc))
            {
                var wins = MiniJson.Array(MiniJson.Field(wc, "windows"));
                if (wins is not null)
                {
                    foreach (var w in wins)
                    {
                        var wi = WindowInfo.Parse(w);
                        if (wi is not null && wi.Value.Id > maxSeenId)
                        {
                            maxSeenId = wi.Value.Id;
                        }
                    }
                }

                continue;
            }

            if (!ev.TryGetValue("WindowOpenedOrChanged", out var oc))
            {
                continue;
            }

            var opened = WindowInfo.Parse(MiniJson.Field(oc, "window"));
            if (opened is null || opened.Value.Id <= maxSeenId)
            {
                continue;
            }

            maxSeenId = opened.Value.Id;

            // 等布局落定:窗口要进列并拿到最终尺寸。
            var settled = WaitForLayout(query, opened.Value.Id);
            if (settled is null)
            {
                Log($"{opened.Value.Id} 布局没落定,跳过");
                continue;
            }

            var (windows, target) = settled.Value;
            if (target.AppId is null || !TargetAppIds.Contains(target.AppId))
            {
                continue;
            }

            if (target.IsFloating)
            {
                continue;
            }

            Log($"发现新窗口 {target.Id} ({target.AppId})");
            Stack(query, geom, windows, target);
        }
    }

    /// <summary>
    /// 轮询等新窗口在布局里落定:要能拿到列位置,而且高度连续两次采样一致。
    /// </summary>
    private static (List<WindowInfo> Windows, WindowInfo Win)? WaitForLayout(NiriIpc query, ulong id)
    {
        var deadline = Environment.TickCount64 + (long)PollTimeout.TotalMilliseconds;
        var prevHeight = -1;
        List<WindowInfo>? last = null;

        while (Environment.TickCount64 < deadline)
        {
            var windows = QueryWindows(query);
            last = windows;

            var win = Find(windows, id);
            if (win is not null && win.Value.Column > 0)
            {
                if (win.Value.Height == prevHeight)
                {
                    return (windows, win.Value);
                }

                prevHeight = win.Value.Height;
            }

            Thread.Sleep(PollInterval);
        }

        // 超时也要尽力而为:最后一次拿到的状态总比没有强。
        if (last is not null)
        {
            var win = Find(last, id);
            if (win is not null && win.Value.Column > 0)
            {
                return (last, win.Value);
            }
        }

        return null;
    }

    /// <summary>核心:算剩余空间,够就 consume 进去。</summary>
    private static void Stack(NiriIpc query, Geometry geom, List<WindowInfo> windows, WindowInfo newWin)
    {
        var col = newWin.Column;
        if (col <= 1)
        {
            Log($"{newWin.Id} 在第一列,左边没有列");
            return;
        }

        var wsId = newWin.WorkspaceId;

        // 找紧邻左边的那个列。列号是「工作区内」的,所以按 workspace_id 分组。
        var prev = new List<WindowInfo>();
        foreach (var w in windows)
        {
            if (w.Id == newWin.Id || w.WorkspaceId != wsId || w.IsFloating || w.Column != col - 1)
            {
                continue;
            }

            prev.Add(w);
        }

        if (prev.Count == 0)
        {
            Log($"{newWin.Id} 左边没有列,不动");
            return;
        }

        prev.Sort((a, b) => a.Row.CompareTo(b.Row));

        // 列高用同一工作区的窗口来反推(不同工作区可能在不同显示器上)。
        var wsWindows = new List<WindowInfo>();
        foreach (var w in windows)
        {
            if (w.WorkspaceId == wsId)
            {
                wsWindows.Add(w);
            }
        }

        var colH = geom.ColumnHeight(wsWindows);
        if (colH is null)
        {
            Log($"{newWin.Id} 拿不到列高,不动");
            return;
        }

        var newH = newWin.Height;
        var occupied = geom.OccupiedHeight(prev);
        var remaining = colH.Value - occupied;
        var need = newH + geom.Gaps;

        if (remaining < need - Tolerance)
        {
            Log(
                $"{newWin.Id} 放不下(剩余 {remaining:F0} < 需要 {need:F0}, " +
                $"列高 {colH.Value:F0}, 上一列已用 {occupied:F0})"
            );
            return;
        }

        Log(
            $"{newWin.Id} 并入左边列(剩余 {remaining:F0} >= 需要 {need:F0}, " +
            $"列高 {colH.Value:F0}, 上一列已用 {occupied:F0})"
        );

        // consume-or-expel-window-left 直接指定窗口,不用先聚焦。
        // 语义是「把这个窗口挪进左边的列」—— 新窗口单独占一列,正是要并进去。
        var resp = query.Request(
            $"{{\"Action\":{{\"ConsumeOrExpelWindowLeft\":{{\"id\":{newWin.Id}}}}}}}"
        );

        if (resp.Contains("\"Err\"", StringComparison.Ordinal))
        {
            Log($"{newWin.Id} consume 失败: {resp}");
        }
    }

    private static WindowInfo? Find(List<WindowInfo> windows, ulong id)
    {
        foreach (var w in windows)
        {
            if (w.Id == id)
            {
                return w;
            }
        }

        return null;
    }

    private static List<WindowInfo> QueryWindows(NiriIpc query)
    {
        var resp = MiniJson.Parse(query.Request("{\"Windows\":null}")) as Dictionary<string, object?>;
        if (resp is null)
        {
            throw new IOException("niri 响应格式异常");
        }

        if (resp.TryGetValue("Err", out var err))
        {
            throw new IOException($"niri 返回错误: {MiniJson.Str(err)}");
        }

        // 注意:Ok 里的键是 niri 的 ResponsePayload 变体名,首字母大写("Windows")。
        var wins = MiniJson.Array(MiniJson.Field(MiniJson.Field(resp, "Ok"), "Windows"));
        var result = new List<WindowInfo>();
        if (wins is null)
        {
            return result;
        }

        foreach (var w in wins)
        {
            var wi = WindowInfo.Parse(w);
            if (wi is not null)
            {
                result.Add(wi.Value);
            }
        }

        return result;
    }

    /// <summary>屏幕逻辑高度。列高的估算基准。</summary>
    private static int? QueryScreenHeight(NiriIpc query)
    {
        var resp = MiniJson.Parse(query.Request("{\"Outputs\":null}")) as Dictionary<string, object?>;
        if (resp is null)
        {
            return null;
        }

        var outputs = MiniJson.Field(MiniJson.Field(resp, "Ok"), "Outputs")
            as Dictionary<string, object?>;

        if (outputs is null)
        {
            return null;
        }

        int? maxH = null;
        foreach (var kv in outputs)
        {
            var logical = MiniJson.Field(kv.Value, "logical");
            var h = MiniJson.Number(MiniJson.Field(logical, "height"));
            if (h is null)
            {
                continue;
            }

            var height = (int)h.Value;
            if (maxH is null || height > maxH)
            {
                maxH = height;
            }
        }

        return maxH;
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"auto-stack: {msg}");
        Console.Out.Flush();
    }
}
