# niri-auto-stack-cs

**中文** | [English](#english)

让 niri 新开的 kitty 填进左边那一列的剩余空间,而不是永远新开一列。

---

# 中文

## 一句话

niri 的滚动平铺里,新窗口永远新开一列。这个守护进程把它改成:新开的 kitty 塞进左边那一列的剩余空间,塞不下才开新列。

## 为什么 niri 原生做不到

niri 官方 Design Principles 的第一条:

> Opening a new window should not affect the sizes of any existing windows.

源码 `src/layout/scrolling.rs` 的 `add_column()` 固定把新列插到 `active_column_idx + 1`。

所以这不是疏漏,是刻意的设计原则 —— 新窗口不该改变已有窗口的大小。想要「填进上一列」,只能自己发 `consume-or-expel-window-left`。

## 效果

| | 左边列还有空位 | 左边列已经满了 |
|---|---|---|
| 原生 niri | 新开一列 | 新开一列 |
| 装上之后 | **并入左边列,填进剩余空间** | 新开一列(和原生一致) |

## 判断逻辑

```
列高       = 一列能占的总高度(同一工作区所有列一样高)
上一列已用 = 该列各窗口高度之和 + 窗口之间的 gap
剩余空间   = 列高 - 上一列已用

if (剩余空间 >= 新窗口高度 + gap - 容差)
    → consume 进去,填进剩余空间
else
    → 不动,让它自己开新列
```

## 列高怎么算准

niri 里一列的高度 = 工作区高度 = 屏幕高 - struts - layer-shell 独占区。后两项 IPC 不暴露(waybar 的独占区完全拿不到),所以:

1. 用 niri 配置里的 `gaps` / `struts` 加屏幕逻辑高**估算**
2. 再从「已经填满的列」**反推校准** —— 填满列的窗口高度之和加中间的 gap,恰好就是列高

实测:1728 的逻辑屏高 → 估算 1718 → 实测 1695。差的 23px 是 waybar 的 layer-shell 独占区。

## 安装

### 依赖

- niri(需支持 `consume-or-expel-window-left` 和 IPC EventStream;本项目在 niri 26.04 上测试)
- .NET SDK 10
- 一个 C 链接器(gcc 或 clang)

### 编译

```bash
git clone https://github.com/Aisht669/niri-auto-stack-cs.git
cd niri-auto-stack-cs
./build.sh
```

`build.sh` 会用 NativeAOT 编译,然后把二进制装到 `~/.config/niri/sh/niri-auto-stack`。

> NixOS 上 gcc 不在默认 PATH 里,`build.sh` 已经自己补上了,不用手动配。
> 其他发行版如果链接器也找不到,可以手动 `export PATH=/path/to/gcc/bin:$PATH`。

### 接入 niri

`~/.config/niri/runs.kdl`:

```kdl
spawn-at-startup "~/.config/niri/sh/niri-auto-stack"
```

`spawn-at-startup` 只在 niri 启动时执行,配置重载不会重跑。改完记得重启 niri。

### (可选)让新 kitty 开出来就是小窗

归列要有意义,新窗口得比整列矮。`~/.config/niri/wins.kdl`:

```kdl
window-rule {
    match app-id="^kitty$"
    default-column-width { proportion 0.25; }
    default-window-height { proportion 0.25; }
}
```

## 工作原理

### IPC

niri 的 socket 路径在 `$NIRI_SOCKET`。请求是单行 JSON,响应是 `{"Ok":...}` 或 `{"Err":...}`。

| 请求 | 用途 |
|---|---|
| `{"EventStream":null}` | 订阅事件流,响应 `{"Ok":"Handled"}` 后逐行推 JSON |
| `{"Windows":null}` | 拿全部窗口(位置、尺寸、工作区) |
| `{"Outputs":null}` | 拿显示器逻辑尺寸 |
| `{"Action":{"ConsumeOrExpelWindowLeft":{"id":123}}}` | 把 123 号窗口挪进左边的列 |

**坑:事件流请求会独占那条连接**,之后在这条连接上发别的请求不会得到响应。查询必须另开一条连接。

### 事件

事件流开头会推一份全量快照(`WindowsChanged`),之后是增量的 `WindowOpenedOrChanged` / `WindowClosed` / `WindowLayoutsChanged`。

niri 的窗口 id 单调递增,所以「id 大于见过的最大值」就是新窗口,不用维护已见集合。

### 时序

新窗口出现 → 轮询 `Windows` 请求直到布局落定(高度连续两次采样一致)→ 算剩余空间 → 决定要不要 consume。

## 为什么手写 JSON 和配置解析

为了体积。`System.Text.RegularExpressions` 和 `System.Text.Json` 两个子系统加起来在 AOT 二进制里占 800KB 出头,而这个程序只需要读懂 niri IPC 那几种固定结构。于是:

- `MiniJson.cs` —— 约 180 行的 JSON 解析器,够用
- `Geometry.cs` —— 手写的配置解析,读 `gaps` / `struts` 两个关键字而已
- `NiriIpc.cs` —— 裸 `Socket` 按字节切行,不用 `StreamReader` / `StreamWriter` / `NetworkStream`

## 体积和内存

| | 磁盘体积 | 常驻内存 |
|---|---|---|
| Python 版(参考实现) | — | 32.8 MB(14.5 MB 解释器 + 18.3 MB `niri msg` 子进程) |
| C# AOT 首版 | 3,654,296 B | 7.0 MB |
| **C# AOT 裁剪后** | **1,690,088 B** | **4.8 MB** |

同机 hello-world AOT 是 1,040,800 B,所以离地板只差 650 KB,再压收益已经很薄。

编译期开关:

| 属性 | 作用 |
|---|---|
| `OptimizationPreference=Size` | ILC 的 `--Os`,不为吞吐做激进内联 |
| `IlcScanReflection=false` | 去掉 `--scanreflection`,完全不用反射 |
| `IlcResilient=false` | 去掉 `--resilient`,省掉未处理异常的兜底逻辑 |
| `IlcFoldIdenticalMethodBodies` | 相同方法体合并 |
| `TrimMode=full` | 裁掉没用到的 BCL |
| `InvariantGlobalization` | 不带 ICU |
| `StackTraceSupport` / `EventSourceSupport` = `false` | 关掉对应支持 |

注意:泛型实例化 + 类型元数据 + 冻结对象数据是**非线性**缩放的,删掉一个子系统省下的远超符号表里量到的。代码层砍依赖这一步(1.77 MB)比编译期开关(192 KB)有效得多。

## 可调参数

都在 `Program.cs` 顶部:

| 参数 | 默认 | 说明 |
|---|---|---|
| `TargetAppIds` | `{"kitty"}` | 哪些 app_id 要自动归列 |
| `Tolerance` | `8` | 像素容差,吸收 gaps / struts / waybar 独占区 / 取整误差 |
| `PollInterval` | `60ms` | 轮询布局落定的间隔 |
| `PollTimeout` | `2s` | 等布局落定的上限 |

`gaps` 和 `struts` 会自动从 niri 的 `config.kdl` 里读,不用配。

## 诊断

程序往 stdout 打日志,前缀 `auto-stack: `:

```
auto-stack: 监听中 gaps=6 估算列高=1718 目标=kitty
auto-stack: 发现新窗口 112 (kitty)
auto-stack: 112 并入左边列(剩余 1276 >= 需要 425, 列高 1695, 上一列已用 419)
auto-stack: 113 放不下(剩余 0 < 需要 425, 列高 1695, 上一列已用 1695)
```

niri 的 `spawn-at-startup` 会把输出接到 niri 自己的日志里。

## 已知限制

- consume 进去之后,niri 会重新分配这一列的高度:原本的窗口高度不变,新窗口拿走剩下的。
- **列宽会被列内最宽的窗口污染** —— niri 的 `Column::width()` 取列内最大值。
- 只处理新开的窗口,不会去动已经存在的布局。
- 新窗口如果自己就是满列高度,它那一列没有剩余空间,自然放不下。
- 默认只处理 kitty。

## 文件

| 文件 | 作用 |
|---|---|
| `Program.cs` | 主循环、决策逻辑 |
| `MiniJson.cs` | 手写 JSON 解析器 |
| `Niri.cs` | 窗口信息结构 `WindowInfo` |
| `NiriIpc.cs` | UNIX socket 客户端(事件流 + 查询两条连接) |
| `Geometry.cs` | 列高 / 剩余空间计算 + niri 配置解析 |
| `NiriAutoStack.csproj` | NativeAOT 与裁剪配置 |
| `build.sh` | 编译并安装 |

## 参考

- [niri 源码](https://github.com/YaLTeR/niri) —— `src/layout/scrolling.rs`、`src/ipc/server.rs`
- [niri IPC 协议](https://github.com/YaLTeR/niri/blob/main/niri-ipc/src/lib.rs)

## License

MIT

---

# English

## What it does

In niri's scrolling layout, a new window always opens a brand-new column. This daemon changes that: a newly opened kitty gets **consumed into the column on its left**, filling whatever space is left there. Only when that column is full does it open a new one.

## Why niri can't do this natively

The first entry in niri's official Design Principles:

> Opening a new window should not affect the sizes of any existing windows.

In the source, `add_column()` in `src/layout/scrolling.rs` always inserts the new column at `active_column_idx + 1`.

That's deliberate, not an oversight — a new window shouldn't resize existing ones. The only way to get "fill the previous column" is to send `consume-or-expel-window-left` yourself.

## Behaviour

| | Space left in the left column | Left column already full |
|---|---|---|
| Stock niri | Opens a new column | Opens a new column |
| With this daemon | **Consumed into the left column** | Opens a new column (same as stock) |

## The decision

```
column height   = total height available to one column (same for every column in a workspace)
left column used = sum of that column's window heights + the gaps between them
remaining       = column height - left column used

if (remaining >= new window height + gap - tolerance)
    → consume it in, filling the remaining space
else
    → leave it alone, let it open its own column
```

## Getting the column height right

A niri column is exactly as tall as the workspace: screen height minus struts minus the layer-shell exclusive zone. The IPC doesn't expose the last two (waybar's exclusive zone is completely invisible to clients), so:

1. **Estimate** from `gaps` / `struts` in niri's config plus the screen's logical height
2. **Calibrate** by back-computing from a column that's already full — the sum of its window heights plus the gaps between them *is* the column height

Measured on a 1728-logical-pixel screen: estimate 1718, actual 1695. The missing 23px is waybar's layer-shell exclusive zone.

## Installation

### Requirements

- niri (needs `consume-or-expel-window-left` and the IPC EventStream; tested on niri 26.04)
- .NET SDK 10
- A C linker (gcc or clang)

### Build

```bash
git clone https://github.com/Aisht669/niri-auto-stack-cs.git
cd niri-auto-stack-cs
./build.sh
```

`build.sh` compiles with NativeAOT and installs the binary to `~/.config/niri/sh/niri-auto-stack`.

> On NixOS, gcc isn't on the default PATH — `build.sh` adds it for you.
> On other distros, if the linker isn't found, `export PATH=/path/to/gcc/bin:$PATH`.

### Hook it into niri

`~/.config/niri/runs.kdl`:

```kdl
spawn-at-startup "~/.config/niri/sh/niri-auto-stack"
```

`spawn-at-startup` only runs when niri starts; reloading the config won't re-run it. Restart niri after changing it.

### (Optional) make new kitty windows small

Consuming only makes sense if the new window is shorter than a full column. `~/.config/niri/wins.kdl`:

```kdl
window-rule {
    match app-id="^kitty$"
    default-column-width { proportion 0.25; }
    default-window-height { proportion 0.25; }
}
```

## How it works

### IPC

niri's socket path is in `$NIRI_SOCKET`. Requests are a single line of JSON; responses are `{"Ok":...}` or `{"Err":...}`.

| Request | Purpose |
|---|---|
| `{"EventStream":null}` | Subscribe to events — replies `{"Ok":"Handled"}` then streams one JSON object per line |
| `{"Windows":null}` | All windows (position, size, workspace) |
| `{"Outputs":null}` | Display logical sizes |
| `{"Action":{"ConsumeOrExpelWindowLeft":{"id":123}}}` | Move window 123 into the column on its left |

**Gotcha:** the EventStream request takes over that connection — anything else you send on it gets no reply. Queries need a second connection.

### Events

The stream opens with a full snapshot (`WindowsChanged`), then sends incremental `WindowOpenedOrChanged` / `WindowClosed` / `WindowLayoutsChanged`.

niri's window ids increase monotonically, so "id greater than the highest we've seen" means "new window" — no seen-set to maintain.

### Timeline

A window appears → poll the `Windows` request until its layout settles (height identical across two consecutive samples) → compute remaining space → decide whether to consume.

## Why hand-written JSON and config parsing

Size. `System.Text.RegularExpressions` and `System.Text.Json` together account for 800KB+ of an AOT binary, and all this program needs to understand is niri's fixed IPC shapes. Hence:

- `MiniJson.cs` — a ~180-line JSON parser, which is all that's needed
- `Geometry.cs` — hand-rolled config parsing for the `gaps` and `struts` keys
- `NiriIpc.cs` — raw `Socket` reads that split on byte boundaries, no `StreamReader` / `StreamWriter` / `NetworkStream`

## Size and memory

| | On disk | Resident memory |
|---|---|---|
| Python version (reference impl) | — | 32.8 MB (14.5 MB interpreter + 18.3 MB `niri msg` child) |
| First C# AOT build | 3,654,296 B | 7.0 MB |
| **Trimmed C# AOT** | **1,690,088 B** | **4.8 MB** |

A hello-world AOT binary on the same machine is 1,040,800 B, so this sits just 650 KB above the floor — further shrinking has sharply diminishing returns.

Compiler switches:

| Property | Effect |
|---|---|
| `OptimizationPreference=Size` | ILC's `--Os`: no aggressive inlining for throughput |
| `IlcScanReflection=false` | Drops `--scanreflection`; this program uses no reflection |
| `IlcResilient=false` | Drops `--resilient` and its unhandled-exception fallback |
| `IlcFoldIdenticalMethodBodies` | Merges identical method bodies |
| `TrimMode=full` | Trims unused BCL |
| `InvariantGlobalization` | No ICU |
| `StackTraceSupport` / `EventSourceSupport` = `false` | Turns off the corresponding support |

Worth noting: generic instantiations, type metadata and frozen object data shrink **non-linearly**. Removing a whole subsystem saved far more than the symbol table suggested — the dependency cuts (1.77 MB) dwarfed the compiler switches (192 KB).

## Tuning

All at the top of `Program.cs`:

| Setting | Default | Meaning |
|---|---|---|
| `TargetAppIds` | `{"kitty"}` | Which `app_id`s get auto-stacked |
| `Tolerance` | `8` | Pixel slack for gaps / struts / waybar's exclusive zone / rounding |
| `PollInterval` | `60ms` | Layout-settling poll interval |
| `PollTimeout` | `2s` | Upper bound on waiting for the layout to settle |

`gaps` and `struts` are read automatically from niri's `config.kdl` — nothing to configure.

## Diagnostics

Logs go to stdout with an `auto-stack: ` prefix:

```
auto-stack: 监听中 gaps=6 估算列高=1718 目标=kitty
auto-stack: 发现新窗口 112 (kitty)
auto-stack: 112 并入左边列(剩余 1276 >= 需要 425, 列高 1695, 上一列已用 419)
auto-stack: 113 放不下(剩余 0 < 需要 425, 列高 1695, 上一列已用 1695)
```

niri wires `spawn-at-startup` output into its own log.

## Known limitations

- After consuming, niri redistributes the column's heights: existing windows keep their size and the newcomer takes what's left.
- **Column width gets polluted by the widest window in it** — niri's `Column::width()` takes the max.
- Only newly opened windows are handled; existing layouts are never rearranged.
- If a new window opens at full column height, its own column has no room left, so nothing gets consumed.
- Only kitty is handled by default.

## Files

| File | Role |
|---|---|
| `Program.cs` | Main loop and decision logic |
| `MiniJson.cs` | Hand-written JSON parser |
| `Niri.cs` | `WindowInfo` struct |
| `NiriIpc.cs` | UNIX socket client (separate event-stream and query connections) |
| `Geometry.cs` | Column-height / remaining-space maths + niri config parsing |
| `NiriAutoStack.csproj` | NativeAOT and trimming configuration |
| `build.sh` | Build and install |

## References

- [niri source](https://github.com/YaLTeR/niri) — `src/layout/scrolling.rs`, `src/ipc/server.rs`
- [niri IPC protocol](https://github.com/YaLTeR/niri/blob/main/niri-ipc/src/lib.rs)

## License

MIT
