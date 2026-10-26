# BootTimer DEVLOG

## 2026-10-26 预载里程碑按 session 类别对齐真正消费者

### 发现(全部来自反编译引擎源码与对端源码)

- `preload-observed-sessions-idle` 是"首次排空"语义: first-wins, 只锁在观测到的**第一次**
  `outstanding == 0`. 引擎启动序列下这个点是 `IntroLogo` 会话完成
  (`NGame.cs:1011-1019` 串行 await `LoadLogoAnimation` / `LoadMainMenuEssentials`,
  `PreloadManager.cs:39-57`), 早于 logo 动画, 更早于 `NMainMenu._Ready`. 它和 FastBoot 的 32 项预热完成
  **没有可比性** —— open item 描述的现象就是这个, 不是观测错误.
- 引擎在菜单出现后会自己重开队列: `LaunchMainMenu` 末尾 `TaskHelper.RunSafely(LoadDeferredStartupAssetsAsync())`
  入队 `Common` 会话且不 await (`NGame.cs:1039-1041`, `PreloadManager.cs:60-64`); `LoadAssetSets` 只 await
  会话**创建** (`PreloadManager.cs:158-183`). 所以"引擎类排空"一次启动至少发生两次, 一次在菜单可见前, 一次在之后.
- FastBoot 是第三个生产者: 32 条单路径会话, 名字固定为 `RegentFXFastBoot optional <path>`
  (`MainFile.cs:177`, `:1112-1117`), 菜单就绪后才逐条提交 (`:953-975`, 同时最多一条在飞),
  终态是 `CompleteWarmUp(string)` 的 `COMPLETED (reason): warmed=..., alreadyCached=..., failed=..., notSubmitted=...`
  (`MainFile.cs:1233-1240`). BootTimer 之前只把 FastBoot 程序集记成 `fastboot_assembly=present|absent`, 没有绑定它的终态.
- `NAssetLoader._Process` 每帧只推进一个会话, 且刚出队那一帧不调 `Process()`
  (`NAssetLoader.cs:68-85`); 单路径会话要 3-4 帧才置完成, 32 条串行在 60fps 下 >= ~130 帧 (>= 2s),
  这段尾巴整体落在 `main-menu-visible` 之后.

### 变更(mod/BootTimerCode/MainFile.cs, 版本 0.2.0)

- session 分类: 名字前缀 `RegentFXFastBoot optional` => `optional`(`PreloadSubmitPatch.OptionalWarmUpNamePrefix`),
  其余 => `engine`. 第三个生产者的未知会话会落进 engine 类, 因此摘要公开分类依据:
  `classifier` + `engine_names="..."`(最多 8 个, 溢出计数 `engine_name_overflow`).
- 每类独立计数(不再只有一个总量): `engine_submitted/completed/failed/outstanding/idle_epochs/last_idle_tick`,
  `optional_*` 同构; 全部写在 `[BootTimer][SUMMARY] preload_contract` 一行, 并在 `semantics=` 里写明每条口径.
- 里程碑改名+收紧: `preload-observed-sessions-idle` -> `preload-engine-sessions-idle`(只对 engine 类, first-wins,
  note 明说 engine 类在菜单后会重开, 不是预热完成); span `preload-observed-sessions` -> `preload-engine-sessions`.
- 新里程碑 `warmup-consumer-terminal`: 反射绑定 `RegentFXFastBoot.RegentFXFastBootCode.MainFile.CompleteWarmUp(string)`
  (新 `FastBootBinder` + `FastBootHooks`, 独立尝试预算与终态标记, 签名/返回类型不符则保持 unobserved 并记录原因,
  绝不报 0). note 形如 `reason=the queue drained; warmed=32; already_cached=0; failed=0; pending=0`.
- `hooks` 行新增 `fastboot_binding` / `fastboot_binding_attempts` / `fastboot_terminal_hook`, 以及实时状态
  `fastboot_warmup="phase=<BindPhase> pending=<n> warmed=<n> terminal_hook=<...>"`.
- 绑定时机新增 initializer 一次(FastBoot 若已加载即可立即打桩), 加上原有的 `tryloadmod:*` 与 `main-menu-visible`.
- 未改: `preload-producer-closed-and-idle` 仍要求调用方显式 `MarkProducerClosed` 围栏, 语义与原因不变.

### 剩余

- 本轮未构建、未启动游戏(静态工作). 编译与实机验收由主会话在授权窗口执行.
- 旧留存日志不可重解: 它只有旧里程碑名, 只能当作"首次排空早于预热完成"的历史证据.
- 若玩家侧 FastBoot 改了 `CompleteWarmUp` 形状, `warmup-consumer-terminal` 会 unobserved 并给出原因, 属设计行为.

### 实机 smoke 清单(需授权; 主会话执行)

准备
- 测试副本: 建议 `E:/sts2smokeB`(A/B 用 `E:/sts2smokeA`), 主副本 `E:/Slay the Spire 2` 不动.
  当前 `E:/sts2smokeB/mods/` 里**没有** BootTimer 也没有 RegentFX/RegentFXFastBoot, 先复制进去.
- 需要的 mod: `BootTimer` 0.2.0(本次新构建, 从仓库构建输出取, 不要用旧 DLL), `RegentFXFastBoot`, `RegentFX`.
  可用的现成载荷目录: `G:/omp works/Sts/_runtime/sts2-test-client-B/mods/{RegentFXFastBoot,RegentFX}/`;
  BootTimer 的旧载荷同目录下有一份, **必须替换成新构建**.
- 日志: `%APPDATA%\SlayTheSpire2\logs\godot.log`(user:// 在 APPDATA, 不随副本目录走;
  每次启动轮转为 `godot<时间戳>.log`). 每次启动后先复制一份再继续, 用同一份日志内的
  `[Startup] Time to main menu` 与 mod 列表行确认是哪一次运行.

步骤 A(有预热, 应 EARLY-ARMED)
1. 清空/改名旧 `godot.log`, 启动副本, 停在主菜单等到日志出现 `COMPLETED (`, 然后进一次战斗, 退出.
2. 在**同一份**日志里按顺序核对:
   - `grep -n "milestone=preload-engine-sessions-idle"` 命中 1 次, 且它的 `t1` **小于**
     `[Startup] Time to main menu` 之后第一条 `[RegentFXFastBoot] MENU IDLE` 的时点.
   - `grep -c "\[RegentFXFastBoot\] WARMED"` = 32.
   - `grep -n "COMPLETED (the queue drained): warmed=32"` 命中 1 次.
   - `grep -n "milestone=warmup-consumer-terminal"` 命中 1 次, `t1` 必须 **大于** 第一条 milestone 的 `t1`,
     且大于最后一条 `preload-session ... class=optional` span 的 `t1`; `note="reason=the queue drained; warmed=32; ..."`.
   - 战斗后那次 `[BootTimer][SUMMARY]`(`trigger=first-combat-room-ready`)里:
     `preload_contract ... engine_submitted>=2 engine_outstanding=0 ...`
     (`IntroLogo` 只有没跳过 logo 时才有, 判定条件 `NGame.cs:1009-1019`; `Common` 在菜单之后入队),
     `optional_submitted=32 optional_completed=32 optional_failed=0 optional_outstanding=0`,
     且 `hooks ... fastboot_binding=bound ... fastboot_warmup="phase=Completed pending=0 warmed=32 ..."`.
   - `engine_names=` 必含 `MainMenuEssentials` 与 `Common`(没跳过 logo 时还含 `IntroLogo`).
3. 判据: 以上全中 => PASS. 任一条缺失/顺序反了 => FAIL, 记录缺失项与该日志的行号.

步骤 B(无预热, 对照)
1. 移除 `RegentFXFastBoot`(或把 RegentFX 的 PreloadEffects 设 false 使 LoadScenes 不被调用), 再启动一次.
2. 期望: `preload-engine-sessions-idle` 仍然出现(**证明它跟踪的是引擎类, 不是预热**);
   `optional_submitted=0`; `warmup-consumer-terminal observed=false reason="..."`(摘要行), 且没有 `WARMED`/`COMPLETED`.
3. 判据: A/B 两次 `preload-engine-sessions-idle` 都在菜单附近, 而只有 A 出现 `warmup-consumer-terminal` => PASS.

失败时最该看的三件事
- `hooks` 行里 `fastboot_binding` 的值(`bound` / `assembly not loaded yet` / `type ... not found` / `patch failed: ...`).
- 该次日志是否被轮转切断(SUMMARY 只有 4 次写, 战斗后那次才是完整对照).
- `preload_contract` 里 `reopen_count` / `submission_epochs`: 有 32 条可选会话时它们应显著大于 1.

## 2026-10-27 后台启动烟测（smokeA，AGENTS §2e-2）

- 副本 `E:/sts2smokeA`，`.tooling/sts2-autoslay-smoke.ps1 -Tag regentfx-20261027`；部署 RegentFX（取自 `E:/Slay the Spire 2/mods/RegentFX`，pck 60716376 B）+ 当前源码重建的 RegentFXFastBoot.dll（69632 B）+ BootTimer.dll（52736 B，v0.2.0）。8 个 mod 全加载。日志 `.tmp/smoke-regentfx-20261027/godot.log`。
- RegentFXFastBoot：32 条 `WARMED`，`COMPLETED (the queue drained): warmed=32, alreadyCached=0, failed=0, notSubmitted=0.`（:732），无异常。
- BootTimer 新里程碑：`preload-engine-sessions-idle` 在 18:13:09.982（:399），`main-menu-visible` 18:13:21.168（:424），`warmup-consumer-terminal` 18:13:30.270（:731，与 FastBoot COMPLETED 同一帧）。证实"引擎预载空闲"比"预热完成"早约 20 秒，两者确实是不同事件。
- 已知不足：`[SUMMARY]` 在主菜单出现时打印（:427-448），早于预热完成，所以 SUMMARY 里 `warmup-consumer-terminal observed=false`；实际里程碑之后才到。SUMMARY 时机需要推迟到 warm-up terminal 或加一次补打 [待修]。
- 未测：通知弹窗的人工点击/Esc/离开菜单路径（需要 §2e 窗口）。恢复：AppData 3311 个文件 mismatch=0。
- 修复（同日）：`FastBootHooks.TerminalPrefix` 在 latch 之后补写一次 `WriteSummary("warmup-consumer-terminal")`（`MaxSummaryWrites = 4` 内）。复测 `smoke-regentfx-20261027b`：:430 `trigger=main-menu-visible`（其中 terminal `observed=false`），:737 `trigger=warmup-consumer-terminal`，:746 `milestone=warmup-consumer-terminal kind=milestone`，:828 FastBoot `COMPLETED ... warmed=32`。0 错误构建；AppData mismatch=0。
