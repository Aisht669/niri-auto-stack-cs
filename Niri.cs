namespace NiriAutoStack;

/// <summary>
/// 一个窗口里我们关心的那几个字段。
///
/// 原本是 System.Text.Json 的实体类,现在手写解析,字段就只留真正用到的:
/// id / app_id / workspace_id / is_floating / 布局位置 / 窗口高度。
/// </summary>
internal readonly struct WindowInfo
{
    public readonly ulong Id;
    public readonly string? AppId;
    public readonly ulong? WorkspaceId;
    public readonly bool IsFloating;

    /// <summary>所在列号(1-based),不在滚动布局里则为 0。</summary>
    public readonly int Column;

    /// <summary>列内行号(1-based)。</summary>
    public readonly int Row;

    /// <summary>窗口自身高度(逻辑像素)。</summary>
    public readonly int Height;

    public WindowInfo(
        ulong id,
        string? appId,
        ulong? workspaceId,
        bool isFloating,
        int column,
        int row,
        int height)
    {
        Id = id;
        AppId = appId;
        WorkspaceId = workspaceId;
        IsFloating = isFloating;
        Column = column;
        Row = row;
        Height = height;
    }

    public static WindowInfo? Parse(object? node)
    {
        var idNum = MiniJson.Number(MiniJson.Field(node, "id"));
        if (idNum is null)
        {
            return null;
        }

        var appId = MiniJson.Str(MiniJson.Field(node, "app_id"));

        var wsNum = MiniJson.Number(MiniJson.Field(node, "workspace_id"));
        ulong? workspaceId = wsNum is null ? null : (ulong)wsNum.Value;

        var floating = MiniJson.Bool(MiniJson.Field(node, "is_floating"));

        var layout = MiniJson.Field(node, "layout");

        var column = 0;
        var row = 0;
        var pos = MiniJson.Array(MiniJson.Field(layout, "pos_in_scrolling_layout"));
        if (pos is { Count: >= 2 })
        {
            column = (int)(MiniJson.Number(pos[0]) ?? 0);
            row = (int)(MiniJson.Number(pos[1]) ?? 0);
        }

        var height = 0;
        var size = MiniJson.Array(MiniJson.Field(layout, "window_size"));
        if (size is { Count: >= 2 })
        {
            height = (int)(MiniJson.Number(size[1]) ?? 0);
        }

        return new WindowInfo(
            (ulong)idNum.Value, appId, workspaceId, floating, column, row, height);
    }
}
