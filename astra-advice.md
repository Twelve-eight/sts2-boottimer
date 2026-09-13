## 第二轮复审 (2026-09-13)

当前隔离构建 exit 0, 1 warning/0 error. 本轮没有重启游戏或运行新的计时会话; 保留 BootTimer 作为观测工具, 不将其他项目的当前 live log 误当作本项目性能复验. 任何启动数字仍须注明计时器加载点,冷/暖缓存和完整 mod 顺序.

# Astra advice - BootTimer

日期: 2026-09-12. 主会话单线. 本轮隔离构建 exit 0, 1 STS002 warning, 0 error; 已有真实游戏日志确实包含 START/END/主菜单时间戳. 没有本轮新游戏启动.

## 当前判断

这是范围小且实际有用的诊断工具. 它帮助本轮证明 RegentFX 先卡 4.176 秒, FastBoot 之后才初始化. 不要把它扩成另一个全能性能框架, 也不要把它说成启动修复.

## P2 BOOT-1: 计时覆盖取决于工具自身的加载顺序

MainFile.Initialize 后才 patch ModManager.TryLoadMod. 排在它前面的模组不会有 START/END. 目前历史日志 BootTimer 排第 0, 但 manifest 没有通用地保证所有用户环境都如此.

建议每次报告写明 BootTimer 自身初始化时点/加载位置, 未覆盖前置开销列为未覆盖. 如要测完整开机, 使用引擎现有 startup 总计时和外部进程启动时间作参考, 不伪造缺失的前半段.

## P2 BOOT-2: UTC 时钟适合关联日志, 不适合唯一耗时基准

MainFile.Mark 使用 DateTimeOffset.UtcNow HH:mm:ss.fff, 无日期. 跨午夜/系统时钟校正会令简单相减出错.

建议保留 UTC 时间关联, 添加 Stopwatch 单调 elapsed tick 或按 invocation 的 duration. 只有 START 没 END 的异常/失败加载要显式显示未完成, 不从下一个模组开始时间推断.

## P3 BOOT-3: 恢复能力不足

检查时 `sts2-boottimer` 不是 git 仓库, 没有本目录 snapshot hook, 没有项目 DEVELOP/DEVLOG. 本轮仅写本建议, 未擅自初始化/发布远程仓库.

如果继续保留该工具作为长期验证基础, 为它建立独立可恢复源码和使用约定; 如果只是一次诊断产物, 明确归档位置/用途. 不将它混入 Spire1 产品源码或发布包来蹭备份.

## 最小验收

- 一个真正新进程, 启动到主菜单, 显示 BootTimer 自身挂载时间.
- 每个被覆盖加载尝试的 START/END 对应, disabled duplicate 也标清.
- 计时与原始 log 行可回溯, 日期/冷暖缓存/mod 集合明确.
- 多次初始化不重复打补丁/重复记录, 无配置写入/网络副作用.

不要为 STS002 warning 编造本地化内容. 本项目没有用户设置/卡牌文案, 应先判断是否应启用该分析器.

证据: `../astra-advice-evidence/2026-09-12/build-results.json`, `historical-startup-evidence.txt`, `repository-recovery.json`.

## 附录: 把观测范围写进性能结论

通用流程见 [总建议附录](../astra-advice.md).

- 每次先问测量从何时才开始. BootTimer 之前的开销不在 START/END 里, 不能按未出现推成耗时为 0.
- 日志时间用于跨源关联, 单调计时用于算时长. 缺 END, 时钟回拨, 跨午夜应标不完整, 不拿下一条日志猜补.
- 只有请求进入/方法返回, 未必等于所有异步工作完成. 计时对象的完成定义必须与被测 API 的 Task/回调语义一致.
- 观测工具本身也可能改变时序/开销. 比较前后运行时注明工具版本/加载位置和缓存条件, 不把采样差异误判成产品改进.

复验只需证明记录覆盖了声称覆盖的阶段, 未覆盖部分诚实保留. 不为诊断器追求漂亮的完整数字而制造不存在的证据.
