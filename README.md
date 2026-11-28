# BootTimer（启动耗时与加载里程碑诊断工具）

纯只读观测型诊断模组：使用单调时钟（`Stopwatch`）在 `godot.log` 中记录《杀戮尖塔 2》各模组加载耗时、本地化合并、引擎预热会话与主菜单可见里程碑，用于排查启动卡顿；不修改任何游戏玩法、逻辑或存档。

> **English**: Diagnostic-only observer that logs monotonic boot milestones and per-mod load timings (`TryLoadMod`, localization, preload sessions, `main-menu-visible`, `sts-performance-v1`) to `godot.log` without mutating gameplay or saves.

- **Current Version**: `0.2.1`（rebuilt & verified against Slay the Spire 2 **`v0.114.0`**, `min_game_version: 0.113.0`）
- **Steam Workshop**: [3846650308](https://steamcommunity.com/sharedfiles/filedetails/?id=3846650308)
- **Runtime Dependencies**: 无（不依赖 BaseLib）

## 特性与开关 / Features & Toggle

- 零玩法改动，不注册任何卡牌、遗物、药水、事件、角色或设置界面。
- 默认开启；仅当环境变量 `BOOTTIMER_DIAG` 设为 `0`（精确匹配）或 `false`（忽略大小写）时完全关闭（不安装任何补丁）。
- 诊断完成后直接禁用、取消订阅或移出 `mods/` 即可。

## 安装 / Installation

- **创意工坊**：订阅 [BootTimer](https://steamcommunity.com/sharedfiles/filedetails/?id=3846650308)。
- **手动安装**：从本仓库 [Releases](https://github.com/Twelve-eight/sts2-boottimer/releases) 下载压缩包，解压至 `<Slay the Spire 2>/mods/`，得到 `mods/BootTimer/{BootTimer.dll,BootTimer.json}`。

## 从源码构建 / Building from source

```bash
dotnet build mod/BootTimer.csproj -c Release \
  -p:Sts2Path="<path-to-Slay-the-Spire-2>" \
  -p:CopyToModsFolderOnBuild=false
```

使用指南与事件字段说明见 [START-HERE.md](START-HERE.md)。
