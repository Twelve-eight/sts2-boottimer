# BootTimer 接手入口

- 当前位置:`G:/omp works/Sts/sts2-boottimer`. 根入口:[START-HERE](../../START-HERE.md).
- 职责:观察启动,mod 加载,异步阶段和帧尾;不是优化其它 mod 的执行器.
- 当前源码:[MainFile.cs](mod/BootTimerCode/MainFile.cs). 历史建议原件:[archive](../../archive/astra-advice/sts2-boottimer/astra-advice.md).
- 留存日志第一次全局 preload-queue-drained 早于 FastBoot 自己的 32 项完成. 对齐具体 session 集合与真正消费者,不要把首次排空当所有可选工作结束.
- 本次 .godot 已移到 [.tmp 保留目录](../../.tmp/workspace-pre-move-generated/2026-09-16/sts2-boottimer/mod/.godot),不是当前可用构建.
- 先按 [BUILD-READY](../../docs/WORKSPACE-NEXT.md) 关闭 live-copy 并恢复目标构建. 没有授权不启动游戏或部署.
