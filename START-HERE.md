# BootTimer 接手入口

- 当前位置:`G:/omp works/Sts/sts2-boottimer`. 根入口:[START-HERE](../../START-HERE.md).
- 职责:观察启动,mod 加载,异步阶段和帧尾;不是优化其它 mod 的执行器.
- 当前源码:[MainFile.cs](mod/BootTimerCode/MainFile.cs). 历史建议原件:[archive](../../archive/astra-advice/sts2-boottimer/astra-advice.md).
- 已闭环 (2026-10-26): 原 open item "留存日志第一次 preload-queue-drained 早于 FastBoot 的 32 项完成" 不是缺陷,是两个不同里程碑被当成同一个. 现在按 session 类别分开计量: `preload-engine-sessions-idle` 只覆盖引擎 (PreloadManager) 类,且引擎在菜单出现后会自己重开队列 (`Common` 会话, `NGame.cs:1039-1041`),所以它**永不代表**预热完成;预热完成由 `warmup-consumer-terminal` 承担 —— 反射绑定 RegentFXFastBoot `MainFile.CompleteWarmUp(string)` (`MainFile.cs:1233-1240`),note 带 `reason/warmed/already_cached/failed/pending`. 摘要 `preload_contract` 行给 `engine_*` / `optional_*` 分项与 `classifier`, `hooks` 行给 `fastboot_warmup="phase=... pending=... warmed=..."`. 分类依据与取舍见 [DEVLOG.md](DEVLOG.md) 2026-10-26 条.
- 本次 .godot 已移到 [.tmp 保留目录](../../.tmp/workspace-pre-move-generated/2026-09-16/sts2-boottimer/mod/.godot),不是当前可用构建.
- 先按 [BUILD-READY](../../docs/project/WORKSPACE-NEXT.md) 关闭 live-copy 并恢复目标构建. 没有授权不启动游戏或部署.
