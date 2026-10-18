# MVZ2 Replay（关卡回放录制 / 播放）

> 《Minecraft vs Zombies 2》0.7.0 的社区 mod。依赖 **DSHCore** 前置。
> 记录一局关卡的游玩过程并写出 `.mrep` 回放文件；也能把 `.mrep` 放回来重演，
> 并且用录制时采到的**状态指纹**自动核对「回放和实际游玩是否一致」。

- 插件 GUID：`qoznos.mvz2.replay`
- 当前版本：**0.1.0**
- 适配：MVZ2 **0.7.1**（Windows **x86** / IL2CPP / Unity 2022.3.62f3）
  > 编译引用的 interop 是**版本专属**的（按 metadata token 解析），换游戏版本必须重跑
  > `tools/sync-runtime.ps1 -GameDir "<新目录>"` 并重新编译，否则启动即原生 AV。
  > 回放**文件**则跨版本可用：静态推断出来的「环境不同」不再一票否决（见下「黄标规则」）。

**相关文档**

| 文档 | 内容 |
| --- | --- |
| 本文 | 用法、配置、界面、排障 |
| [docs/PROTOCOL.md](./docs/PROTOCOL.md) | `.mrep` 文件的**字段级规范**（协议版本 / 环境记录 / 指纹 / 调试模式 / 兼容规则） |
| [docs/DESIGN.md](./docs/DESIGN.md) | 设计推导与踩坑记录（为什么这么做） |

---

## 目录结构

```
Replay/
├─ Replay.csproj / build.ps1 / Directory.Build.props   工程文件
├─ README.md / install-readme.txt                      文档（本 mod 没有需要部署的资源文件）
├─ docs/            PROTOCOL.md（文件格式规范）、DESIGN.md（设计推导）
├─ Core/            与游戏无关的纯逻辑：JSON、协议、文件读写/索引、环境记录、
│                   视口换算、采样器、日志、兼容策略、指针轨迹、对话框下标
├─ Game/            与游戏耦合的运行时：Plugin（BepInEx 入口）、配置、本地化、
│                   补丁与钩子、场景接管、播放器/录制器、存档护栏、假鼠标、HUD、指令
├─ Ui/              回放页与结束面板（DSHCore 的「插件页子页面」）
└─ Diagnostics/     ⚠️ 诊断与自测代码 —— **发布构建里整份不编译**：
                    内置自测（ReplaySelfTest / ReplayGuiTest）、布局 dump（ReplayTreeDump）、
                    只做 Marshal 读的 NativePeek、以及尚未定型的传送带守卫
                    （ReplayConveyorGuard，接管 `UpdateFixed`，正式版传送带闪退仍待修）
```

### `Diagnostics/` 与 `-Diagnostics`

`Diagnostics/` 下面全是**排查与自测用**的东西：内置自测（`ReplaySelfTest` /
`ReplayGuiTest`）、布局 dump、原生内存只读工具，以及**还没定型**的传送带守卫。

它们默认**不参与编译**，也**不参与发布**：

| 命令 | 效果 |
| --- | --- |
| `.\build.ps1`（或 `.\build.ps1 -Mod rp`） | `Diagnostics/**` 不编译，`DIAGNOSTICS` 未定义 → 发布用 DLL（263,680 字节） |
| `.\build.ps1 -Mod rp -Diagnostics` | 编进 `Diagnostics/**` 并定义 `DIAGNOSTICS` → 排查/自测用 DLL（327,680 字节） |

配套地，`ReplayConfig` 里的 `[SelfTest]` / `[GuiTest]` 以及传送带守卫的开关
都在 `#if DIAGNOSTICS` 里 —— 发布版本的 cfg 里**不会**出现这些段。
而 `ReplayPatches` / `ReplayPlayer` 里那些 `ReplayConveyorGuard.Beat(...)` 调用点
**保持原样**（它们记录了「崩溃前的最后一条标记」，是定位原生 AV 的唯一手段）：
发布构建编译的是 `Game/ReplayConveyorGuardStub.cs` 里的**空实现**，一个空调用。

跑自测：

```powershell
.\tools\run-replay-selftest.ps1 -Build -PlayFile <回放名>      # -Build 会先做诊断构建
```

**为什么要编译期隔离，而不是「配置项关掉就行」**：这些探针本身就会改变行为。
实测在 0.7.0 正式版上，只要给 `ResourceLocationMap.Locate`（或
`Il2CppSystem.RuntimeType.IsAssignableFrom`）挂上任何一个补丁，
游戏自己的访问违例就会变成 CoreCLR 内部错误（Windows 事件 id 从 1000 变 1023）。
留在发布 DLL 里就是一颗随时会响的雷。

---

## 0.7.0 正式版：播放任意回放闪退的根因与修法

**症状**：0.7.0 正式版上播放任何回放，进关卡后几秒内进程**直接消失**，托管侧什么都看不到
（BepInEx 日志里没有托管异常，只有 `ErrorLog.log` 里有原生栈）。

**崩溃点**：`GameAssembly.dll` 偏移 `0x0141b822` = `System.RuntimeType.IsAssignableFrom` +0x32，
异常码 `0xc0000005`，可达链路是
`Addressables ResourceManager.ProvideResource → CompactLocation.get_Dependencies → ResourceLocationMap.Locate`。

**根因**：不是游戏本身，也不是「回放文件的问题」，而是本 mod 的**存档护栏**
（`Game/ReplaySaveGuard.cs`）在 `LogicSaveData` 那一层拦金币：

| 护栏状态 | 结果 |
| --- | --- |
| `[General] SaveGuard = false`（完全不拦） | 回放正常播完，指纹 4/4 一致 |
| 只拦 `LogicSaveExt.*`（公开面，3 个补丁） | ✅ 回放正常播完，指纹 4/4 一致，存档隔离照样生效 |
| 再挂上 `LogicSaveData.GetMoney / SetMoney` | ❌ 进关卡后几秒 AV 在 `0x0141b822` |

于是修法是：**不再挂 `LogicSaveData` 那一层**（配置项 `[General] SaveGuardDataPatches`，
默认 `false`）。游戏绝大多数地方走的是公开面 `LogicSaveExt.*`，所以存档隔离仍然生效，
只是「少数绕过公开面直接摸 `LogicSaveData` 的写法」拦不到。

> 这一层当年是**为了防内联**才加的（两个方法都很短，IL2CPP 可能把里层内联进外层）。
> 现在已知它的代价远大于收益：挂在它上面会让游戏在资源加载路径上访问违例。
> 如果将来要靠它解决「钱没拦住」的问题，请先用 `-Diagnostics` 构建复现 —— 别再靠猜。

**顺带作废、并且已经删掉的两条路**（代码从 git 历史里取回）：

1. 补 `ResourceLocationMap.Locate`（把类型过滤器 `System.Object` 改写成 `null`）：
   挂了补丁之后崩溃从「事件 id 1000 + GameAssembly」变成「事件 id 1023 + coreclr」，
   也就是**这个拦截点根本不能用**。
2. 补 `Il2CppSystem.RuntimeType.IsAssignableFrom`：**同样会把 id=1000 变成 id=1023**。
   它当初就是为「绕开崩溃」写的，实测「挂上就等于把进程推向 coreclr 崩溃」——
   拿 `TypeGuard = false` 当对照，诊断构建下同一条回放能完整播完。

> 这两条共同的模式是「给游戏热路径上的小函数挂前缀」，在 0.7.0 上没有一个能安全存活。
> 所以 `ReplayTypeGuard` / `ReplayAddressablesProbe` / `ReplayAddressablesFix`
> 三个文件已**整份删除**（诊断构建也不再编入）。

**关键帧等待（`[General] KeyframeDelayFrames`）与音乐恢复（`[General] RestoreMusic`）**：
这两条都是排查过程中试过的**证伪项** —— 调大延迟、剔掉音乐字段都**不能**避免崩溃，
所以它们不是原因。（保留这两个开关是为了以后遇到别的问题时能对照。）

### 列表里名字的颜色（黄标规则）—— 2026-10-17 调整

| 颜色 | 含义 |
| --- | --- |
| **黑** | 没问题；**或者「在本环境里实测跑过一遍、指纹全对」**（见下） |
| **黄** | 静态推断可能播不好：录制环境与当前不同 / 文件没有环境记录 / 游戏版本不同 / 是调试文件 |
| **红** | 上次播放真的出过状态不一致（会记下第一次不一致的 tick） |

**黄标怎么降级**：一次播放只要

1. 跑过了总时长的 **20%**（原来是一半，实测太严 —— 一条两万 tick 的回放要跑一万 tick 才算数，
   玩家看一眼就走了，于是黄标永远降不下来），**且**
2. 一条指纹都没错，

就把「已在这个环境哈希下验证过」写进索引（`_replay_index.json` 的 `ve` 字段）。
之后这条的名字就是黑的，环境哈希变了才自动回到黄色。

> 0.7.1 修掉了这条路上的一个 bug：原来写健康度用的是**带 `.mrep` 的文件名**做键，
> 而列表用的主键是**裸名字** —— 写进去的标记谁都读不到，所以怎么看都不降级。
> 现在索引读写都统一过 `ReplayIndex.NormalizeName`（并且会顺手清掉历史脏键）。

### 0.7.1 兼容性（2026-10-17）

- 重新生成 interop（`tools/sync-runtime.ps1 -GameDir "<0.7.1>"`）后，**全部 mod 零改动编译通过**，
  说明 0.7.1 没有 API 破坏。
- 0.7.1 新增真正的**健康忠告页**（`MainScenePageType.HealthWarning`）：自动化测试会卡在那里，
  已在自测里用游戏自己的 `DisplayMainmenu()` 程序化跳过。
- 跨版本回放（0.7.0 / rc-1 录的）照常播放，指纹仍然全对。

**0.7.1 上的自动化自测（`mvz2_mountain_5_20261017_123529`，688 tick）**：整体 **PASS，失败项 0**。

| 检查项 | 结果 |
| --- | --- |
| 播放完整性 | ✅ 停在 688/688，`finished` |
| 指纹一致性 | ✅ 4/4 全对 |
| 补丁安装 | ✅ 42 个全挂上、0 失败、形参全部绑得上 |
| 存档护栏 | ✅ 12 个补丁，金币只走影子、其它写入被拦 |
| 减速按钮 | ✅ 已建立 / 位置正确 / 图标已镜像 / 加速倍率 10 ↔ 减速 0.1 |
| 假鼠标手持跟随 | ✅ `手持跟随=3248`（手持蓝图时跟随假鼠标） |

### 验证记录（0.7.0 正式版，2026-10-16）

> 均为诊断构建（= 发布构建 + 自测，见下一节）下的**自动化播放**。
> 判据三条：会不会闪退、有没有播完、指纹有没有全对。

| 用例 | 规模 / 来源 | 闪退 | 一致性 |
| --- | --- | --- | --- |
| `mvz2_palace_10_20261016_200337` | 710 tick / v10 / **0.7.0 录制** | ✅ 不崩 | ✅ **4/4 指纹一致**（点「重新播放」再走一遍也一样） |
| `compat1` | 9441 tick / v9 / 0.7.0 录制 | ✅ 不崩 | —（自测超时截断在 tick ≈8700） |
| `山4`（`mvz2:mountain_4`） | 14704 tick / v9 / 0.7.0 录制 | ✅ 不崩（以前几秒就崩） | ❌ 29/29 指纹不一致，且在第 4414 tick 就 game-over |

**关于 `山4` 的不一致**：这条是 **0.7.0 录的 v9 文件** —— 它没有环境哈希、没有 `samplers` 字段、
**也没有逐 tick 指针轨迹**（`特性=vp`）。而 `mountain_4` 里有 `pistenser`（炮口朝指针）这类
**由指针位置驱动仿真**的内容，没有轨迹就只能靠离散事件「猜」指针位置 → 必然跑偏。
这属于「旧格式 + 跨版本回放」的已知限制，不是本轮修的东西：

- 同一个文件在**修复前**根本播不起来（几秒内闪退），所以指纹数据**一条都没有**，
  不存在「以前一致、现在不一致」的回归；
- 作为对照，同版本录的 `palace_10` 现在依旧 **4/4 完全一致**。

> 想在 0.7.0 上得到可核对的一致性，**用 0.7.0 重新录一条**即可
> （新文件是 v10，带环境哈希与指针轨迹）。

---

## 它记录什么

时间轴单位是游戏自己的**逻辑 tick**（`LevelEngine.GetLevelTime()`，30 tick/秒）。
因为游戏的加速/慢动作只改变「一帧跑几个 tick」，不改变每个 tick 的内容，
所以按 tick 记录天然就是「**不记录暂停/加速**」的。

0.7.0 的玩家输入汇聚成 5 个调用点，全部被挂钩（参数是
`MVZ2.Level.Inputing.PointerInteractionArgs` 结构体，里面带 pointerId / button / interaction）：

| 调用点 | 对应操作 |
| --- | --- |
| `LevelController.UI_OnGridPointerInteractionCallback` | 在格子上放置/移动器械 |
| `LevelController.UI_OnEntityPointerInteractionCallback` | 工具（镐子/星之碎片/触发）与手持物对实体使用 |
| `LevelController.UI_OnBlueprintPointerInteractionCallback` | 点选卡槽 |
| `LevelController.OnBlueprintKey` | 数字键选卡 |
| `LevelController.UI_OnRaycastReceiverPointerInteractionCallback` | 草坪区域（点空白取消手持物等） |

另外单独记录**工具点击**（镐子 / 星之碎片 / 触发器），挂的是
`LevelController.ClickPickaxe / ClickStarshard / ClickTrigger`。
手持物状态变化的其余来源都已被上面的指针事件覆盖（详见「已知限制」）。

> 记的是「点击」本身而不是「点击之后手持物变成了什么」：后者看不出玩家点的是哪一把工具，
> 也分不清「本来就拿着这把工具 → 点一下取消」和「换了另一把工具」。记点击本身，
> 取消 / 被挡下 / 没解锁这些分支全部由游戏自己重新判定，无歧义。

悬浮类交互（`Hover`/`Enter`/`Stay`/`Exit`）会被丢弃 —— 它们对仿真没有影响，
而 `DoHeldItemPointerEvent` 对每一次鼠标移动都会调用，记下来会让「鼠标划一圈草坪」变成几百条事件。
`Hold`/`Streak` 必须保留：长按拾取、按住给推车蓄力都依赖它。

每次录制还会抓一份**起始关键帧**（`LevelController.SaveGame()` = 整关 `SerializableLevelController`），
它是回放的起点。因为关键帧已经包含了此前的一切，**关键帧抓取成功之前的事件会被丢弃**，
避免回放时重复执行。

每条指针事件还带**指针位置**（`sx` `sy` = 屏幕坐标，`wx` `wy` `wz` = 射线命中的世界坐标）。
这不是装饰：`HeldItemTarget*` 上的 `LocalPointerPosition` 会被
`ToEntityHeldItemBehaviour` / `EmptyHandEntityHeldItemBehaviour` 拿去找目标实体，
位置对不上就会打到另一个实体上。

同时每 N tick 采样一条**状态指纹**（能量 / 波次 / 旗数 / 是否通关 + 7 条 RNG 的 64 位哈希），
回放时逐条重算并比对，这就是「回放与实际游玩一致」的自动化证据。

> 为什么不用「种子 + 原始鼠标键盘」：见 `DESIGN.md` 第 3 节。
> 简言之，语义事件比逐帧轨迹小一个数量级，而且记录的是游戏解析后的真实目标，
> 不依赖帧率、射线与输入解码。

---

## 文件格式

> 📄 **完整规范见 [PROTOCOL.md](./PROTOCOL.md)**（字段表、兼容性规则、调试模式、录制/播放的策略分工）。
> 本节只讲「你会看到什么、怎么用它」。

`.mrep` 是 **gzip** 压缩的逐行 JSON（与游戏的 `.lvl` 一样用 gzip）：每一行都是一个**扁平**的
JSON 对象，第 1 行固定是 header。

```
第 1 行        header      {"magic":"mrep","spec":1,"ver":10,...}
接着           env         {"k":"env","t":"summary",...}                  ← 协议 v10：录制环境摘要
               env         {"k":"env","kind":"content","id":"mvz2_lab",...} ← 每个已装 mod 一行
               keyframe    {"k":"keyframe","tick":1,"state":"<SerializableLevelController 的 BSON>"}
之后           ...         {"t":123,"k":"grid","lane":1,"col":3,...}
               ...         {"t":30,"k":"fp","en":175,"w":1,"fl":0,"cl":false,"r":"..."}
               ...         {"t":812,"k":"ptr","sx":640,"sy":360,"mb":1}
               ...         {"t":1471,"k":"dlg","title":"...","op":"支付 100 金币\u0001拒绝","sel":1,...}
```

### header 里跟「能不能放」有关的字段

| 字段 | 含义 |
| --- | --- |
| `spec` | **规范版本**（当前 1）。只作人读信息 |
| `ver` | **格式版本**（当前 **10**）。读者只看它决定能不能播；旧版本照旧能读能放 |
| `game` / `unity` / `plat` | 录制时的游戏版本 / 引擎版本 / 平台位数 |
| `envHash` / `envCount` | 录制环境的哈希与条目数（明细在 `env` 行） |
| `stage` / `area` / `difficulty` / `seed` | 进关用的语义信息 |
| `tps` | 逻辑帧率（固定 30） |
| `startTick` / `lastTick` | 录制区间 |
| `hasKeyframe` | 有没有起始关键帧（没有就放不了） |
| `sw` / `sh` | **录制那一刻的窗口尺寸**（v7 起）。见「换了窗口尺寸也对得上」 |
| `mn` | **开录时的全局金币**（v9 起）。金币不在关卡关键帧里、但会参与关卡逻辑的分支（上锁的箱子的选项列表） |
| `fpt` | 录制时**实际使用**的指纹间隔（v10 起）。播放按它自检「文件自述是否自洽」 |
| `debug` | 是不是**调试模式**文件（v10 起）。见下文「调试模式」 |
| `caps` / `samplers` | 本文件用到了哪些可选特性 / 启用了哪些指纹采样器（v10 起） |

配置项 `WritePlainText`（默认**关**）打开时会额外写一份**未压缩**的 `.mrep.json`，方便直接查看。

### `env` 行：录制环境（协议 v10）

**一类两形**：一行摘要 + 每个环境条目一行。

```json
{"k":"env","t":"summary","game":"0.7.0","unity":"2022.3.62f3","plat":"x86","tps":30,"hash":"...","n":9}
{"k":"env","kind":"builtin","id":"mvz2","name":"Minecraft VS Zombies 2","ver":"5","dv":5}
{"k":"env","kind":"content","id":"mvz2_lab","name":"Laboratory","ver":"4","dv":4}
{"k":"env","kind":"plugin","id":"qoznos.mvz2.replay","name":"MVZ2 Replay","ver":"0.1.0"}
{"k":"env","kind":"runtime","id":"env:game","name":"游戏版本","ver":"0.7.0"}
{"k":"env","kind":"runtime","id":"env:arch","name":"进程位数","ver":"x86"}
```

- `kind` = `builtin`（本体）/ `content`（内容 mod）/ `plugin`（BepInEx 插件）/ `runtime`（运行环境）。
- `ver` 对 `content` 是**关卡数据版本**而不是 mod 版本 —— 因为游戏读关卡存档时比对的就是
  「(命名空间, 关卡数据版本)」这一对。
- 条目按 `kind|id|ver` 排序后哈希成 `envHash`（顺序无关，同一套环境在任何机器上得到同一个值）。

**播放时的用处**：环境不同**只警告不阻止播放**，但会把差异完整写进播放小结。
内容 mod 的**关卡数据版本**不同会单独点名 —— 那意味着关键帧**根本载不进去**，
属于「必然失败」而不是「略有偏差」。回放库列表里也会给这类文件打上「录制环境与当前不同」。

> 这也是排查「回放对不上」的第一件事：先看小结里的环境差异，再看指纹。

### 调试模式（**回放 mod 开发用**，默认关闭）

配置 `[Debug] Enabled = true` 打开。打开后：

- **每个 tick** 一条指纹（而不是每 `FingerprintInterval`）；
- 指纹里额外带：`rx`（7 条 RNG 流的**原始** `x,y,z,w`）、
  `ei`（**每个实体的明细**：id / 定义 / 血量 / 坐标 / 分层）、
  `lk`（关卡内部标量：`levelTime` / `currentEntityID` / `waveState` / 回收池长度…）；
- 文件会大 1-2 个数量级。

它回答的问题和常规模式完全不同：

| 常规模式能说 | 调试模式能说 |
| --- | --- |
| 「tick 150 起不一致了」 | 「tick 137 起不一致」 |
| 「RNG 跑偏了，先偏的是 spawn 流」 | 「spawn 流的第 3 次 `Next` 之后偏的」 |
| 「实体集合变了」 | 「第 4 个 zombie 的血量少了 1」 |

调试文件的差异报告会给出**第一个不同处的上下文片段**，而不是把几百字符整串贴出来。

> ⚠️ 调试字段是**写进文件**的，不是「播放时按你这边的配置现算」。
> 所以别人拿到你的调试文件、即使没开调试模式，也能看到完整差异（协议 §10.3）。

### 事件类型

| `k` | 含义 | 额外字段 |
| --- | --- | --- |
| `grid` | 格子上的指针事件 | `lane` `col` |
| `entity` | 实体上的指针事件 | `id` `def` `lyr` `ex/ey/ez` |
| `blueprint` | 点选卡槽 | `idx` `cv` |
| `bpkey` | 选卡热键 | `idx` `cv` `key` |
| `lawn` | 草坪区域 | `area` |
| `tool` | 工具点击 | `name`（`pickaxe` / `starshard` / `trigger`） |
| `fp` | 状态指纹 | `en` `w` `fl` `cl` `ec` `eh` `ed` `r` `r0`..`r6`（+ 调试的 `rx` `ei` `lk`） |
| `ptr` | **逐 tick 指针轨迹**（v9，**不是**动作事件）| `sx` `sy` `mb`（按键位掩码） |
| `dlg` | **选项对话框**（v9，**不是**动作事件）| `title` `desc` `op` `sel` `selName` |
| `env` | **录制环境**（v10，**不是**动作事件）| `t`/`kind` `id` `name` `ver` `dv` |

> `ptr` / `dlg` 两类**不进事件队列**（不参与「按 tick 派发」），它们在文件末尾单独排。
> 原因：`dlg` 真正决定「玩家选了哪一项」的时刻可能比它出现的 tick 晚很多，
> 混进事件行会破坏「事件行按 tick 升序」这个前提（注入游标会漏掉后面的行）。
> 所以 `dlg` 只在**对话框打开时**按当时的 tick 写一条，选择结果回填在 `selName` 里。

`t` = tick，`pid` = 指针 ID，`btn` = 鼠标键，`ia` = `PointerInteraction` 枚举值，
`s*` / `w*` = 指针位置（见上文）。

`blueprint` / `bpkey` / `entity` 三个事件在**拿起蓝图**时还会带上 `itg` / `evk`
（那一刻手持蓝图上的「立即触发 / 立即激发」标记，v8 起）。**两个字段要么都在、要么都不在**：
只写一个的话就分不出「另一个是 false」和「这个事件没有这个信息」。
为什么要记：见下方「不受『交换触发』选项影响」。

`entity` 事件的 `def` 是目标的**定义 ID**，`ex/ey/ez` 是录制时目标的坐标 —— 它们是
「ID 错位时把目标找回来」的依据，见下文「已知限制」。

指纹（`fp`）里的诊断字段：

| 字段 | 含义 | 用来发现什么 |
| --- | --- | --- |
| `ec` | 存活实体数 | 实体数量变了 |
| `eh` | 所有实体的 `(id, 定义ID)` 按 id 排序后的哈希 | **从哪一个 tick 开始「同一个 id 在两边不是同一个东西」** |
| `ed` | 实体定义数量直方图，如 `mvz2:redstone=3,mvz2:zombie=2` | **可读版**：少了哪一类 / 多了哪一类（比如「资源没生成」） |
| `r` | 7 条 RNG 流合并后的哈希 | 只要有任一条 RNG 跑偏就报 |
| `r0`..`r6` | 7 条 RNG 流**各自**的哈希，顺序 = `level` `entity` `effect` `round` `spawn` `conveyor` `misc` | **哪一条**流先跑偏 → 直接缩到子系统（出怪 / 特效 / 实体 / …） |
| `rx` | 【调试】7 条流的**原始** `x,y,z,w` | 在**第几次 `Next`** 之后就偏了 |
| `ei` | 【调试】每个实体的明细 | **哪个实体的哪个量**先变 |
| `lk` | 【调试】关卡内部标量 | 多/少生成过实体（`currentEntityID` 差 1）、波次状态机走到哪一步 |
| `x_*` | 第三方采样器注册的自定义字段 | 由第三方 mod 决定 |

> `ed` 与 `r0`..`r6` 由 `FullFingerprint`（默认开）控制。关掉能省约 150 字节/条，但排障时就没东西了。
> 历史上还有一个「完整 RNG 状态（x/y/z/w）」的 `rng` 字段，已删除 —— 它是嵌套数组，
> 单行 JSON 解析器读不了，留着只会制造解析错误（要原始值请用调试模式的 `rx`）。

**指纹的比对规则是通用的**：凡是录制侧指纹里出现了的键，都要求回放侧逐字相等；
录制侧没有的键不比（那是「这条指纹只记了一部分」）；回放侧算不出来的键
**既不算一致也不算不一致**，会单独计数并在小结里说明。
所以「给回放加一种新指纹」＝注册一个采样器，录制、播放、比对的代码一行都不用改。

> v1 文件里没有 `sx`/`wx`，工具事件写的是派生的 `set` / `reset`。两者都还能读、能放，
> 但工具那部分会退化成近似（少了「点的是哪把工具」这个信息）。**想要完整保真度就重新录一遍。**

---

## 播放

回放时会在难度文字下面多出一行 `REC 当前帧/总帧数`。

> 它是**难度文字的子节点**（锚在下边缘正中）。不放在难度文字的**兄弟**位置是因为
> 上面那层带 `LayoutGroup` —— 实测当兄弟节点时会被布局组排到难度文字的**右侧**，
> 而不是下方。

```
/replay play <名字>        播放指定回放（名字可以用 /replay list 看）
/replay playlast           播放最近写出的那一条
/replay stopplay           中止播放（保留当前关卡状态）
```

播放时：

- **玩家只能做关卡外操作**：暂停（空格）、加速（快捷键）、血条开关、Esc 选项菜单。
  其余输入（放器械、选卡、镐子/星之碎片/触发器、鼠标指针事件）全部被原地丢弃，
  所以你在回放里乱点也不会改变任何东西。
- **暂停/加速都能用，且不影响一致性**：时间轴是逻辑 tick，暂停时引擎不跑 tick，
  加速时一帧跑多个 tick，事件始终在**它本来的那一个 tick** 上派发。
- 回放期间**不会录制**（否则会把注入的动作又记一遍）。开始播放时如果正在录制，会先把它停掉写盘。
- 结尾会把小结打印到控制台：注入了多少事件、指纹核对的结果、有没有跳过/报错。

### 假鼠标（`[Visual]` 段）

回放里录下的只有**动作本身**（点格子 / 点实体 / 点卡槽 / 工具按钮），**没有鼠标轨迹**。
所以 `bpkey`（数字键选卡）与 `tool`（镐子/星之碎片/触发器）这一类动作根本没有坐标，
手持物会一直停在**上一次点击的位置**，直到下一次点击才瞬移过去 —— 观感很差。

这个 mod 现在会把离散的路点**合成一条连续轨迹**，并额外**画一个假鼠标**：

- **轨迹**：在 tick 空间插值，而且**缓动覆盖两个路点之间的整个区间**（默认，
  `FakeMouseTicks = 0`）—— 上一个动作刚做完就开始往下一个位置走，**全程不停**。
  在 tick 空间做的好处：**暂停时指针自动冻住，加速时自动跟着变快**，全都不用额外处理。
- **画出来的光标**：手持物品时物品模型**就是**指针（会把系统光标隐藏），
  但**什么都没拿的时候游戏不画任何指针** —— 那时光标是系统光标，停在玩家自己的鼠标位置，
  跟回放毫无关系。所以这里额外克隆一张「游戏原生的光标贴图」画在假鼠标位置上。

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `FakeMouse` | `true` | 总开关。关掉 = 回到旧行为（原地不动 + 瞬移） |
| `FakeMouseTicks` | `0` | `0` = 缓动占满整个区间（**全程不停**）；填正数 = 先停着、最后 N tick 冲过去 |
| `FakeMouseEase` | `linear` | `linear`（匀速，最符合「不静止」）/ `quadinout` / `quadout`（最像人手） / `cubicout` |
| `FakeMouseScale` | `1` | 贴图的显示大小倍率（1 = 和游戏原生硬件光标一样大）|
| `FakeMouseGameCursor` | `true` | 用游戏自己的光标贴图还是 mod 自绘箭头（外观退路） |

> ⚠️ **只走视觉通道，绝不碰仿真。** 指针位置有好几个消费者，其中三个**会改仿真**：
> `MinecartRideable`（矿车整车位置每 tick 由指针决定）、`Snipenser`（朝向 → 射击速度向量）、
> `BreakoutBoard`（走 `POST_POINTER_ACTION` 的 `screenPos`）。
> 所以假鼠标**不会**改写 `ReplayPlayer.pointerScreen` 本身，而是在
> `LevelController.UpdateHeldItemPosition` 这一层临时换上、**出了这个方法立刻还原**
> （那是个纯表现的方法：`ScreenToWorldPoint(指针)` → `SetHeldItemPosition`）。
> 于是 `Global.Input` / 矿车 / 喷瓜 / 打砖块看到的全是事件原值，指纹不可能受影响。

#### 两个花了不少时间才找到的坑

1. **约 5% 的事件记录的坐标是 `(0,0)`** —— 那是**屏幕左下角**，是「游戏构造这个事件时
   没填位置」的哨兵值（`mountain_10` 那条 4564 个事件里有 222 个：198 个 `entity` +
   24 个 `grid`，且 `wx/wy/wz` 也全是 0）。不滤掉的话，因为改成全程缓动了，
   假鼠标会**频繁**往左下角跑。现在建路点表时会跳过它们（小结里会报`滤掉哨兵坐标 N`）。
2. **不能直接拿按钮克隆体当容器用** —— 原版「文本按钮」的 `Selectable` 用的是
   **SpriteSwap 过渡**，它在 `OnEnable` / 状态变化时会把**按钮自己的 sprite**
   写到 `targetGraphic`（我们的 `Image`）上，于是光标被换成按钮那张 400x40 的贴图、
   渲染出来就是一块灰方块。`Destroy(Button)` 是**帧末**生效的，拦不住 `OnEnable`。
   修法：**先把 `transition` 设成 `None` 再销毁**，另外每帧比较一次、不对就按回去。
3. **锚点不在贴图左上角，而是整整差一个贴图高度** —— 游戏把箭头的光点存在 `CursorData.hotspot`
   里，而它是交给 `Cursor.SetCursor(texture, hotspot, mode)` 的：**那个重载的热点以贴图左上角为原点**。
   而 `RectTransform.pivot` 用的是**左下角**原点 —— 两者差一个上下翻转。
   第一版直接把它当枢轴用（`pivot = hotspot / texSize`），于是热点 `(0,0)` 变成了**左下角**，
   光标被画到指针**上方**一整个贴图高度，看起来就像「锚点在贴图正中」。
   修法：`pivot = (hotspot.x / w, 1 - hotspot.y / h)`（`FakePointer.HotspotToPivot`）。
   小结里会把热点与枢轴都打出来：**游戏的箭头热点就是 `0,0`，所以枢轴必须是 `0,1`**。
4. **换了窗口尺寸之后坐标就对不上了** —— 见下一节。

#### 换了窗口尺寸也对得上（`sw`/`sh` + `ReplayViewport`）

`*x`/`*y` 记的是**当时的屏幕像素**，而游戏把像素变成世界坐标的公式（`LevelCamera`，正交相机）是

```
世界偏移 = (像素 - 屏幕中心) × 2 × orthographicSize / 屏幕高
```

注意到分子里只有**屏幕高**：纵向可视范围恒为 `2*orthographicSize`，横向才是「纵向 × 宽高比」。
所以同一个像素在不同窗口尺寸下**指向不同的格子** —— 回放里的操作本身是语义化重演（记的是
`lane`/`col`、实体 ID），跟像素无关；而假鼠标（以及「手持物跟手」那条）走的是像素，
两者只在**录制时那个分辨率**下才重合。

修法：header 记下录制时的窗口尺寸，播放时把坐标换算回去（`ReplayViewport`）：

```
k  = 现在屏幕高 / 录制时屏幕高
新 = 现在屏幕中心 + (旧 - 录制时屏幕中心) × k
```

两个分量用同一个 `k`（横向可视范围跟着宽高比一起变，不能用「新宽/旧宽」）。
**尺寸相同则 `k = 1` 且中心相同 → 原样返回**，所以不换窗口这条路上它是恒等的，
不会影响任何既有行为；旧文件（没有 `sw`/`sh`）也直接恒等返回。
同一个换算同时用在：假鼠标路点、注入进游戏的 `args.position` / `raycast.screenPosition` /
`eventData.position`、以及给 `Global.Input` 报的指针位置。

> ⚠️ **换算必须放在「取值时」做，不能烘进路点表/缓存。**
> 第一版在 `FakePointer.Begin` 里就把路点换算好了（因为那时窗口尺寸已经知道），
> 结果「开始播放**之后**再改窗口尺寸」依旧对不上 —— 表里的值是定死的。
> 现在路点表里存的是**录制坐标系**的原值，`ScreenPos` / `RawScreenPos` / `PointerScreen`
> 都在读取时现算，所以**播放中途**改尺寸也立刻跟上。
> （也因此存在两套取值：`ScreenPos`/`PointerScreen` = 已换算；`RawScreenPos`/`RawPointerScreen`
> = 原值。「临时换上假鼠标位置」那条通道两边必须用同一套坐标系，不能用混了。）

假鼠标的**大小**也会跟着重算（画布 UI 单位 ↔ 像素的比值同样依赖窗口尺寸）。

这个修复有自动验证：自测会在**播放中途真去改一次分辨率**（1222x796 → 800x480），
验证「假鼠标位置 = 按**当前**尺寸换算的结果、且不是录制时尺寸的结果」，然后还原。
因为自测注入的动作（数字键 / 工具按钮）**一个坐标都没有**，那条路径本来跑不到，
所以自测会临时塞一条假路点把换算链走通（报告里会标出来）。

### 不受「交换触发」选项影响（`itg`/`evk`）

难度界面里那个「交换触发」开关（`swap_trigger`）**在关卡内随时可以改**，而它会改变仿真：

```csharp
// SelectBlueprintHeldItemBehaviour.GetBlueprintPickData
bool swapped = Global.Saves.IsTriggerUnlocked() && Global.Options.IsTriggerSwapped();
bool triggerValue = canInstantTrigger && (holdingTrigger != swapped);
...
builder.SetInstantTrigger(pickData.triggerValue);   // 存进手持物，放下时由 BlueprintGlobalCallbacks 读
```

问题在于它是**玩家选项**、**不在关卡关键帧里** —— 录的时候开着、放的时候关着（或反过来）
会让 `triggerValue` 整个反过来，于是回放里的器械行为就与录那时不同了。

所以录制时把**拿起蓝图那一刻**的 `instantTrigger` / `instantEvoke` 跟着事件一起记下来
（`itg` / `evk`），回放时在同一个位置**按回去**（`ReplayHeldFlags`）。
这样回放与那个选项完全无关。

- 只有 `instantTrigger` 受该选项影响（`instantEvoke` 只看星之碎片）—— 两个都记只是为了完整。
- 录制侧只对「手上确实是蓝图」的事件写入这两个字段（类型白名单比对 `NamespaceID.Path`：
  `blueprint` / `conveyor` / `blueprint_pickup`），所以选卡**没成功**（能量不够、卡无效）时
  不会写 —— 回放据此知道「这个事件不用管」。自测的报告里会直接把「手持是什么」打出来。
- 写回用的是游戏自己的属性扩展（`LogicHeldItemProps.SetInstantTrigger`）。
  ⚠️ 那种属性是**注册过的键**才有意义的：键没注册时 `SetProperty` 只会默默失败。
  所以自测会拿一个现造的手持物数据验一遍「写进去能读回来」。

### 回放不改动存档（金币 / 统计 / 解锁 …）

放一次回放**不能让玩家的存档发生变化**。这不是「锦上添花」—— MVZ2 的金币是**全局存档里的钱**
（`MoneyComponent` 直接读写 `Global.Saves`），退出关卡时还会 `Saves.SaveToFile()` 落盘；
统计（击杀数 / 放置数 / 通关次数…）则挂在 `POST_ENEMY_SPAWNED` / `POST_ENTITY_DEATH` /
`POST_LEVEL_CLEAR` 这些关卡回调上，重演时一条条累加。

| 类别 | 处理 | 为什么不能一律拦掉 |
| --- | --- | --- |
| 金币 | **影子值**：读改都走 mod 自己的一份副本，第一次读时用真值播种 | 关卡要**读**钱决定买不买得起，读到的必须还能随消费/拾取变化 |
| 统计 / 解锁 / 难度记录 / 关卡进度 / 上次选卡 / 上次地图 / 地图对话 | **直接拦掉** | 这些在回放里没有「读回去」的需求，写进去只会污染存档 |

- 玩家看到的金币照样会涨会跌（观感完全正常），只是那些变化全进了影子；
  播放结束时影子整个丢掉，真存档一个字节都没动过。
- 金币挂**两层**：`LogicSaveExt`（`Global.Saves.*` 的公开面）与 `LogicSaveData`（直接摸数据对象那条）。
  两者都是很短的方法，IL2CPP 可能把里层内联进外层，只挂一层的话真被内联了就永远不生效。
- 拦截点是在**第一次播放时**才装的（`ReplayHooks.EnsureSaveGuardHooks`）—— 金币那两处在
  `MVZ2Logic.*`，而这个仓库踩过「在 `Plugin.Load` 阶段反射 `MVZ2Logic` 的类型会提前触发
  它的 `.cctor`」的坑；等到要播放时游戏早已初始化完，怎么反射都安全。
- 播放小结里会写 `存档护栏 : 补丁 已挂 14 个，开（…）`。**「已挂」和「拦下 N 次」都要看**：
  补丁没挂上、或者一次都没命中，都会让存档静默地被改动，而这从游戏表现上完全看不出来。

> 通关那一条另有一套拦截（见下）—— 原版 `OnEngineClearCallback` 会
> `RemoveLevelState()`（**删掉该关卡的存档文件**）、解锁关卡、写难度记录，
> 所以「通关」这个入口是单独前缀拦掉的。

### 会读「实时指针」的关卡内容（打砖块 / 重装兵器 / 矿车 / 碾压墙 / 经卷）

有一类关卡内容**不看指针事件的参数**，而是每帧直接问「鼠标现在在哪、哪个键按着」。
它们和「放器械 / 选卡」走的是完全不同的通路（详见 `DESIGN.md` §4.4.14），
所以早期版本里会出这些问题：

| 内容 | 以前在回放里的表现 | 原因 |
| --- | --- | --- |
| 打砖块的挡板（2-6 / 小游戏）| 跟着**旁观者的鼠标**跑 | 挡板每 tick 被**绝对**设置到指针处，而回放没接管指针 |
| 重装兵器（6-6 / 小游戏）| 朝旁观者，且**永不开火** | 要 `IsPointerHolding(MOUSE,0)` 才射击，而按键状态没被接管 |
| 矿车 | 目标点只在「带坐标的事件」那一刻更新 | 同上 |
| 碾压墙（梦魇）| **只有草坪上的点击**能被重现 | 录制侧原本只记「路由事件」，点空白 / 点 UI 一条都不记 |
| 魔人经卷（制品，把手持物换成手枪）| 落点跟着旁观者的鼠标走 | `TryOverridePointerPosition` 里有条「注入期间不接管」的错误短路 |

现在：

- **逐 tick 记录指针轨迹**（`ptr` 行：`sx`/`sy`/`mb`），回放时按 tick 重放。
  只在变化时写，一条 ~50 字节；而且**只有这一关确实有上面的内容时才录**。
- 回放期间**掐掉游戏自己的鼠标轮询**（`InputManager.UpdatePointer`），
  并接管 `InputHelper` 的按键状态 ⇒ **旁观者的鼠标动不了回放里的任何东西**。
  暂停 / 加速 / 血条 / Esc 照旧可用。
- 存档（金币）也会影响关卡逻辑的**分支**：上锁的箱子按「钱够不够」决定要不要生成
  「付钱」那一项，所以 header 里记了开录时的金币（`mn`），回放时用它给金币影子播种。

> ⚠️ **旧回放（`ver <= 8`）没有轨迹数据**，上面这些内容会退回旧行为
> （挡板卡顿、重装兵器不开火）。要看这些关卡请**重新录一条**。

#### 两条踩过坑的规则（改这块之前务必先读）

**① `tick T` 用的一定是「第 T 条」采样，不是「第 T−1 条」。**

Unity 的 `Input` 在**一帧内是常量**，而逻辑 tick 与采样点在同一帧里：

```text
帧 F：Input 定成 I(F)
  GameUpdater.Update()      → 跑 tick T（tick 内读 Input 看到的就是 I(F)）
  采样点（tick 后）          → sample@T = I(F)
```

⇒ tick T 内部读到的输入 **==** `sample@T`。

差一整个 tick 的后果非常隐蔽：重装兵器的「按住才开火」晚一 tick ⇒ 回放里
**多发一颗子弹**（实测 tick 300 时录制 `arrow=3`、回放 `arrow=4`），
接着实体 RNG 整条流错位、**之后所有指纹都对不上**；打砖块的珠子也会在另一个
tick 落空。自动化守门员是 `ReplayPointerTrajectory.SelfCheck`（自测里断言）。

**② 轨迹没覆盖到的 tick 绝不接管。**

轨迹是**按需**录的，所以一条回放里的轨迹可能只覆盖**后半段**：2-11 的碾压墙要等
Boss 出场才生成，实测那份回放的采样从 tick **16801** 才开始（全关 18037）。

| 本 tick 有轨迹（`TickCovered`） | 接管策略 |
| --- | --- |
| 是 | 全部接管：按键相位、指针位置、`POST_POINTER_ACTION` 都来自轨迹（不再走「按事件」那条，否则同一次按下会被处理两遍） |
| 否 | **完全不接管**，退回「按事件」的老路径（与没装这个功能时**完全一致**） |

早期版本用的是「整条文件里有没有轨迹」（`HasSamples`），于是 2-11 的前 16800 个 tick
在明明没有数据的情况下**凭空造出了一整条录制时并不存在的指针动作流**，
同时把老路径关掉了 —— 两条路一起走坏，首次指纹不符出现在 tick 1350。

#### 第三方 mod：注册自己的「指针消费者」

如果你的内容也每帧读实时指针，在 `Plugin.Load()` 里注册一次即可：

```csharp
// 场上出现这个实体时，本关的指针轨迹开始被记录/重放
ReplayCompat.RegisterPointerConsumer("myns:my_contraption", "我的器械会读实时指针");

// 或者用自定义判定（每 0.5 秒被调一次，请保持轻量）
ReplayCompat.RegisterPointerConsumer(level => level.EntityExists(...), "我的关卡行为");
```

不注册的代价：这一关不会录轨迹，你的内容在回放里就会跟着**观众**的鼠标走。

### 回放结束面板

回放播完（**播放完毕 / 关卡通关 / 关卡失败 / 手动结束**）时会弹出结束面板，两个按钮：

| 按钮 | 做什么 |
| --- | --- |
| `重新播放` | 从头再播这一条（`ReplayPlayer.RestartCurrent()`）|
| `退出` | 结束播放，**直接落到回放页**（不是地图；关掉回放页再回主菜单）|

> 「导出 .mrep」**已按需求从这里移除**，功能在回放页的【更多】里 ——
> 在关卡里导出既看不到其它回放、也改不了名字。导出的实现是共用的
> （`Ui/ReplayExporter.cs`），两个界面调同一个。

- **两种到达方式**：① 播完（含通关 / 失败 / 手动 `/replay stop`）；
  ② 回放里按 Esc →「结束播放」→ 确认（走 `ReplayPlayer.FinishFromMenu()`，
  和播完**同一条收尾路径**，并会先把 Esc 菜单收起来）。
- 面板开着的时候**输入是被拦的**（和播放中一样），免得顺手点到关卡里的东西。
- 「另存为」是**系统级阻塞对话框**（弹着的时候游戏主线程停住）—— 游戏自己的导入导出同理。
- 回放页里的结果提示（导出 / 重命名 / 清理）走的是自己的 `Confirm` 浮层，
  **不是**游戏那个排队对话框 —— 那里不会掞到排队时序。

### 回放里的关卡菜单（Esc）

回放期间 Esc 出来的还是游戏原版那个选项菜单，但里面两个会**破坏回放语义**的按钮被换掉了：

| 原版 | 回放里 | 点了之后 |
| --- | --- | --- |
| 重新开始 | **重新播放** | 先弹「确认要重新播放吗？当前进度会从头开始。」→ 确认后**收起菜单**并从头再播一遍（`RestartCurrent()`）|
| 返回地图 | **结束播放** | 先弹「确认要结束播放吗？结束后会弹出回放结束面板，可以导出、重播或退出。」→ 确认后**收起菜单并弹出回放结束面板**（`FinishFromMenu()`），由面板上的「退出」再落到回放页 |

- 两个回调都**先收菜单再动手**：真正把那个框体收起来的是 `LevelController.ResumeGame` 里的
  `ui.SetOptionsDialogActive(false)` —— 光调 `optionsDialogController.Close()` 只清 context、
  不收框体，结果就是「结束面板叠在还开着的菜单上面」（玩家能看见底下那半透明的菜单）。
  也不能依赖 `Close()` 触发的那条 `ResumeGameDelayed` 协程：它要下一帧才跑，
  那时结束面板已经弹出来了，会被 `ResumeGame` 里的 `if (Main.Scene.HasDialog()) return false;` 挡住。

- 文案是挂在原版 `OptionsDialogController.UpdateRestartButton` / `UpdateLeaveLevelButton`
  **之后**改的 —— 原版每次刷新按钮（切页 / 改语言 / 重新打开菜单）都会走那里，
  我们跟在后面覆盖，就不会出现「先对后错」。
- 行为是拦 `OnMainPageButtonClickCallback`：回放期间这两个枚举值不再走原版的
  `RestartLevel`（**会清关卡存档**）与 `ExitLevel`（落点 = 地图 / 主菜单）。
- ⛔ 顺带把原版的**通关**流程也拦了：`OnEngineClearCallback` 会调 `RemoveLevelState()`
  （**删掉该关卡的存档文件**）、解锁关卡、写难度记录 —— 回放只是在重演一局，
  拿它去改玩家存档是错的。失败时的原版对话框同样拦掉（否则会和结束面板并排出现）。

#### 收尾落点：为什么不能只把页面切成插件页

「结束播放 / 退出」的落点是 `MainSceneController.GotoMapOrMainmenu`（游戏里唯一那个
「打完一局去哪」的决策点）。它被拦下来改成了：

1. `DisplayAddons(回主菜单)` —— 回放页的壳本来就是**插件页 Index 的克隆体**，背景用它才对味；
2. **同一帧**就把面板亮出来（`ReplayPage.PumpNow()`）。
   只挂个单、等下一帧的 `Tick()` 再开的话，中间那一帧会闪出插件页自己的索引。
3. 关掉回放页时不再亮出插件页索引，而是直接执行那个「回主菜单」。

> ⚠️ **以前这里是个黑屏 bug**：`DisplayAddons` 传的 `onReturn` 是 `null`，
> 于是插件页的「返回」按钮 `OnReturnClick?.Invoke()` 什么都不做、只把自己
> `Hide()` 掉 —— 页面全灭、没东西顶上来，就是黑屏。
> 玩家是从关卡**直接**落到回放页的，从没经过插件页索引，所以退出时该回主菜单。

> ⚠️ **改文案要拿到「关卡自己的那个」`OptionsDialogController`。**
> 场景里同时存在主菜单 / 地图各一套同名控制器，`UiFinder.FindOne<OptionsDialogController>()`
> 很容易命中它们 —— 那些的 `ui.Main` 挂在未激活的节点下，写进去的文案没人看得见
> （症状：菜单里还是「重新开始 / 返回主界面」，而且一个错都不报）。
> 正确来源是 `LevelController.optionsDialogController`。

### 它是怎么重演的

1. 先把当前关卡场景**整个换掉**（`GotoLevelSceneAsync`），然后用游戏自己的
   `LevelController.LoadGame` 把关键帧灌进**新换出来的那个控制器**。

   > ⚠️ `GotoLevelSceneAsync` 是**异步**的：它 `await` 完场景加载才会换掉
   > `LevelManager.controller`。所以等待必须等到「控制器**换了实例**」，
   > 不能只等「场上有控制器」—— 否则会打在旧控制器上，见下一条。

   > ⚠️ `LoadGame` 必须打在**刚 Awake 出来、还没 `InitLevel` 过**的控制器上。
   > 因为 `builtinCollisionSystem` 是控制器的字段、由 `level.Init()` 填满；
   > 在已经 Init 过的控制器上 `LoadGame` → `CreateFromSerializable` →
   > `ReadCollisionFromSerializable` 会 `entities.Add(id)` 撞键：
   > `ArgumentException: An item with the same key has already been added. Key: [7, BuiltinCollisionEntity]`
   > → `LoadGame` 返回 false。游戏自己走 `LevelManager.InitLevel`，
   > 有存档文件时**只**走 `LoadLevel`（不建新关卡），天然避开这个坑。
2. 替玩家点掉游戏弹出的「关卡已载入」对话框并解除暂停。
3. 在 `LevelEngine.Update` 的**前缀**里看 `GetLevelTime()`：等于下一条事件的 tick 就派发。
   录制时事件也是按这个时钟打戳的（`AddLevelTime()` 在 `Update` 末尾），两边语义天然对齐。
4. 派发方式是**反射调用游戏自己的那 5 个回调 + 3 个工具点击**（不是自己拼
   `DoHeldItemPointerEvent`：interop 把 `HeldItemTargetGrid` 这类目标生成成了
   不声明 `IHeldItemTarget` 的值类型代理，从 mod 侧没法合法装箱成那个接口）。
5. **除了上面那条「路由事件」，还要补一条「输入层的全局回调」。**
   游戏的鼠标输入有**两条互不重叠**的下游通路，只重放第一条会漏掉一整类效果：

   | 通路 | 走法 | 典型受影响者 |
   | --- | --- | --- |
   | ① 路由事件 | EventSystem → 格子/实体/草坪/卡片控制器 → 上面那 5 个回调 → `DoHeldItemPointerEvent` | 放器械、选卡、工具、拾取 |
   | ② 全局回调 | `InputManager.UpdatePointer()` 轮询真实鼠标 → `RunCallbackFiltered(LogicCallbacks.POST_POINTER_ACTION, …)` | **右键取消手持物**、碎石墙 / 打砖块这类「按下鼠标就有效果」的关卡内容 |

   ②由 `ReplayPointerAction` 负责：把录到的事件按相位发给同一条 `POST_POINTER_ACTION`
   （不自己重写取消逻辑，游戏改行为我们自动跟着变）。映射：
   `Down→Press`、`Hold/Streak/Drag→Hold`、`Up→Release`、`Stay/Enter/Exit→None`
   （「指针在但这几个键没按下」）。`[General] PointerAction = true` 控制开关。

   > 为什么要连 `Stay→None` 一起发：原件里输入层**每帧**都会为非按下的键发一次 `None`
   > （带着当时的屏幕位置）。`BreakoutBoard`（打砖块）**不筛相位**，鼠标模式下每帧
   > 用 `screenPos` 把挡板移到指针处 —— 只发 `Down/Hold/Up` 会让挡板卡顿。
   > 按 `Press` 筛选的（右键取消、碎石墙）不受影响。

   > 实测后果（很隐蔽）：录制里 `选卡9 → 右键取消 → 再选卡9 → 放下去`；
   > 回放里少了那次取消，**再选卡**就变成了**取消手持物**（点一张已手持的卡会先
   > `CancelHeldItem` 然后 return），于是那个器械永远放不下去。
6. 每遇到一条指纹就重算一遍并比对；不一致会列在控制台小结里。
7. **实体事件的目标要按「定义 + 网格分层 + 坐标」三级找回来**（不能用录到的 ID 直接当准）。

   ID 是关卡内自增的，只要录制与回放之间有**任何一处生成时机不同（实测差 1~2 个 tick 就够）**，
   后面的 ID 就整体错位：轻则 `FindEntityByID` 返回 null（事件被丢掉），
   重则返回**另外一个实体**（静默地把动作做到错的东西上，比丢掉更糟）。
   所以事件里额外记了目标的**定义 ID**、**网格分层**与**坐标**，回放时：

   | 级别 | 条件 | 什么时候才用得到 |
   | --- | --- | --- |
   | ① | ID 命中 **且** 定义一致 **且** 分层一致 | 99% 的情况 |
   | ② | 同定义 **且** 分层一致，取离记录坐标最近的 | ID 错位了，但东西还在 |
   | ③ | 同定义，取最近的 | 分层读不到（旧文件 / 非网格实体） |
   | ④ | 记录坐标附近的**任意**实体 | 连同定义的都没有 —— 只当锚点，游戏自己会就近选目标 |

   **为什么「分层」是必需的（镐子挖错株的根因）**：同一格最多能同时站 4 个可被镐子挖掉的
   东西 —— `承载位 / 保护位 / 主要位 / 工具位` —— 它们**坐标完全相同**（同一个格子），
   所以「同定义 + 离记录坐标最近」根本分不出是哪一个。而游戏的目标判定**从锚点身份出发**：

   ```csharp
   // VanillaEntityExt.FindPointerTargetEntity（实体路径 —— 电脑端走的就是这条）
   var protectTargets = entity.GetProtectingTargets();   // ← 只有锚点是「保护位」才有非空结果
   ...
   if (firstValidMainTarget != null && (!canUseOnProtector || pointerPositionY >= 0.5f))
       return firstValidMainTarget;
   return entity;                                        // 否则返回锚点自己
   ```

   锚点从「保护位」变成「主要位」，`GetProtectingTargets()` 就直接是空数组 → 镐子挖到另一株。
   加上分层这一维就能唯一确定同格里的哪一个。实测同一格里出现过的组合：

   ```
   (5,6) mvz2:onbashira#149[carrier,main] + mvz2:fragment#150[] + mvz2:ejector#151[protector] + mvz2:fragment#152[]
   ```

   （一个实体可以**同时在多层**上，所以 `lyr` 存的是分层键的**有序拼接**，如 `carrier,main`。）

   > ⚠️ **电脑端与移动端的判定不同，本 mod 只做电脑端。**
   > - **电脑端（鼠标）**：`LevelRaycaster` 用 `castRadius <= 0` 的**精确射线**，
   >   锚点 = 光标那个像素落在谁的碰撞体上（`ToEntityHeldItemBehaviour.IsValidFor`
   >   也只在 `pointer.type == MOUSE` 时接受 `HeldItemTargetEntity`）。鼠标有像素级精度，
   >   所以同格三层俱全时**蹭着保护位露在外面那条边**也能点中它。
   > - **移动端（触摸）**：`SetHeldItem(..., radius)` 给的是**胖手指圆投射**，
   >   锚点退化成**格子**，走 `VanillaGridExt.FindPointerTargetEntity(grid, pointerPosition, …)`——
   >   那条路径才是"指针高度 < 0.5 选保护位、否则选主要位"。
   >
   > ⇒ 别用「上下半格」解释电脑端的现象。

   各级命中次数会写进播放小结：`按定义+分层找回` / `按定义找回` / `改用记录坐标当锚点`。

少数手持物行为不看回调参数、直接问 `Global.Input.GetPointerScreenPosition()`（实时鼠标位置），
回放时这三个 `InputHelper` 方法会被接管成「回放到的那一刻的位置」。

---

## 指令

| 指令 | 作用 |
| --- | --- |
| `/replay rec [名字]` | 从当前 tick 开始录制（不给名字就自动生成） |
| `/replay stop` | 结束录制并写出 `.mrep`（正在播放时 = 中止播放） |
| `/replay play <名字>` | 播放回放 |
| `/replay playlast` | 播放最近写出的回放 |
| `/replay stopplay` | 中止播放 |
| `/replay list` | 列出输出目录里的回放 |
| `/replay status` | 查看录制 / 播放状态 |
| `/replay dir` | 查看输出目录 |

默认**不录制**：`AutoRecord` 现在默认 `false`，平时进关卡不会产生任何回放文件。
要录就把 `AutoRecord` 打开（进关卡自动开录，关卡结束 / 退出关卡 / 退出游戏时落盘），
或者干脆在回放页按「录制」/ 用 `/replay rec` 手动开录。
落盘只在录制**结束**时发生一次；想中途也能落盘（防崩溃丢档）再开 `AutoSave`（默认关）。

---

## 配置

`BepInEx/config/qoznos.mvz2.replay.cfg`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `AutoRecord` | `true` | 进入关卡自动开始录制。关掉的话用 `/replay rec` 手动开始（需要先打开 `Commands`）|
| `AutoSave` | `false` | 录制**过程中**是否每 20 秒把回放写一次盘。默认**关** —— 只在录制**结束**时写一次（离开关卡 / 退出游戏 / 手动停止），也就是「关卡里不会自己冒出回放文件」。开着它每 20 秒就在回放目录落一次盘 |
| `OutputDir` | `save` | 输出目录。见下方「回放存在哪」 |
| `FingerprintInterval` | `150` | 每多少 tick 采一条指纹；`0` = 关闭 |
| `PointerTrajectory` | `true` | 录/放**逐 tick 指针轨迹**，见「会读实时指针的关卡内容」 |
| `DialogAutoAnswer` | `true` | 回放里自动应答选项对话框（关掉就会停下来等真人点）|
| `DialogHoldSeconds` | `2` | 自动应答前先把对话框显示多少秒（`0` = 不等）|
| `FullFingerprint` | `true` | 指纹里是否额外存 `ed`（实体定义直方图）与 `r0`..`r6`（7 条 RNG 流各自的哈希）|
| `WritePlainText` | `false` | 是否额外写未压缩的 `.mrep.json` |
| `DiscardEmpty` | `true` | 一条动作都没录到时不产生文件 |
| `PointerAction` | `true` | 是否把录到的鼠标动作也发给**输入层的全局回调**（右键取消手持物等）。见「它是怎么重演的」第 5 条 |
| `MaxReplays` | `20` | 最多保留多少个回放（每写出一份就清一次最旧的）。`0` = 不限制。**只数自动命名的**：在回放页里改过名的回放不计入上限、也不会被自动清理 |
| `SlowDownButton` | `true` | 播放回放时在关卡里的加速按钮正下方放一个减速按钮（只在回放期间存在）。关掉它就完全不碰关卡 UI |
| `ConveyorGuard` | `true` | 传送带关卡的崩溃守卫（见下方「第 11 天这类关卡」）。关掉则改回游戏原实现 |
| `ConveyorGuardLog` | `false` | 守卫的逐步骤标记，写 `<游戏目录>\BepInEx\_replay_conveyor.txt`。**排查用**，平时关着 |
| `Commands` | `false` | 是否注册 `/replay` 控制台指令。默认**关** —— 普通玩家走界面就行 |
| `ProbeEnabled` | `false` | 排障探针：把注入 `blueprint`/`grid` 时游戏认为的状态写进日志。**别在测一致性时开**（见下）|
| `ProbeMaxLines` | `600` | 探针输出上限 |

> 曾经的 `GuiEnabled` / `EndPanel` / `ExportDir` 已经删了：GUI 入口常驻（就当没这个开关），
> 结束面板也常驻，导出改成**原生「另存为」对话框**（目标由玩家自己挑，不再配目录）。
> 老 cfg 里残留的这几行会在下次启动后自动消失。

**这些键在 MVZ2ModManager 的配置页里是什么控件，由声明决定**（不声明就只能手打字符串）：

| 键 | 声明 | 管理器里显示为 |
| --- | --- | --- |
| `FingerprintInterval` / `ProbeMaxLines` / `MaxReplays` / `FakeMouseTicks` | `AcceptableValueRange<int>` | **滑条**（带值读数）|
| `FakeMouseScale` | `AcceptableValueRange<float>` | **滑条** |
| `FakeMouseEase` | `AcceptableValueList<string>` | **下拉框** |
| `OutputDir` | 无（自由文本） | 文本框 —— 它要接受路径，**不能**给列表 |
| 其余布尔 / 自由文本 | 无 | 复选框 / 文本框 |

`[Visual]` 段（播放时的观感，见上方「假鼠标」）：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `FakeMouse` | `true` | 是否合成鼠标轨迹并画假鼠标 |
| `FakeMouseTicks` | `0` | `0` = 缓动占满整个区间（全程不停） |
| `FakeMouseEase` | `linear` | 缓动曲线 |
| `FakeMouseScale` | `1` | 假鼠标贴图的显示大小倍率 |
| `FakeMouseGameCursor` | `true` | 用游戏自己的光标贴图 |

`[Debug]` 段（**回放 mod 开发用**，默认全关；详见 [PROTOCOL.md](./PROTOCOL.md) §10）：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 调试模式总开关：逐 tick 采指纹 + 指纹字段尽量全。文件会大 1-2 个数量级 |
| `EveryTick` | `true` | 逐 tick 采样（而不是每 `FingerprintInterval`）|
| `RawRng` | `true` | 写 7 条 RNG 流的**原始** `x,y,z,w`（`rx`）|
| `EntityDetail` | `true` | 写**每个实体**的明细（`ei`：id / 定义 / 血量 / 坐标 / 分层）|
| `LevelScalars` | `true` | 写关卡内部标量（`lk`：`levelTime` / `currentEntityID` / `waveState` …）|
| `EntityLimit` | `0` | 实体明细条数上限（`0` = 不限）|

> **这些开关只影响「录出来的文件」**。播放时要不要核对那些字段，看的是文件 header 里的
> `debug` 标记 —— 所以把调试文件发给别人，对方即使没开调试模式也能看到完整差异。

### 回放存在哪

默认**跟着原版存档走**（按用户区分），不再是 `BepInEx/replays`：

```
%USERPROFILE%\AppData\LocalLow\Cuerzor\<游戏名>\userdata\user<N>\replays\
```

`OutputDir` 可以填这些**位置关键字**：

| 值 | 实际位置 |
| --- | --- |
| `save`（默认）| `<存档根>/user<N>/replays` |
| `savemod` | `<存档根>/user<N>/<空间名>/replays`（与 `user.dat`、`level/` 同级）|
| `saveroot` | `<存档根>/replays` |
| `game` | `<游戏根>/BepInEx/replays`（旧行为）|

也可以直接给路径：绝对路径原样用，相对路径以游戏根目录为基准。

> 换成 `save` 之后**旧文件会被自动搬过去**（只搬 `.mrep` / `.mrep.json`，不覆盖、不删）。
> 即使搬迁失败（占用等），查找与 `/replay list` 也会**兜底搜索旧位置**，老回放不会「消失」。
> 关键字路径**不做缓存** —— 玩家在游戏里换用户（user0 → user1）时会跟着走。

---

## 界面（插件页 → 回放）

**入口**：主菜单 → 更多选项 → **插件**。列表里的条目自上而下是
「资源包」「模组设置（DSHCore）」「回放」（原版那个横向列表容器被改成纵向，各占一行）。

---

## 多语言

`ReplayLocale.cs` 把回放页、结束面板、提示与确认弹窗、以及各类失败原因的文字全部登记进
DSHCore 的 `UiLocale`，游戏切到英文时自动显示英文。`共 {0} 个` 这种带占位符的用 `UiLocale.Format`：

```csharp
T.En("共 {0} 个", "{0} total");
SetCountText(UiLocale.Format("共 {0} 个", shown));
```

弹窗按钮标签一次取一组用 `UiLocale.GetAll(...)`（游戏原生的 `SetDialog` 不会自己过翻译）。

> ⚠️ **自测用的字符串故意没翻**：`ReplaySelfTest` 靠文本匹配读自己的报告，
> 比如结束面板的 `_info`。这类“只给机器看”的文字动了会直接把自测弄坏。

玩家可以在 `<游戏>\BepInEx\config\DSHCore\lang\<语言码>.ini` 里覆盖。详见 `DSHCore/UI_GUIDE.md` §11。

> 回放页现在是 **DSHCore 的 `AddonPage`** 的一个实现（`Ui/ReplayPage.cs`）：
> 入口按钮的安装与文案纠正、页面壳的克隆与修补、「藏起插件页索引 → 叠面板」、
> 离开插件页自动收起、心跳驱动，**全部由 DSHCore 负责**；
> 本 mod 只写「面板里放什么」。因此本 mod 也不再自己挂心跳 ——
> `AddonPages` 已经挂在 `MainSceneController.Update` 上了。

> **本 mod 也会出现在「插件页 → 模组设置（DSHCore）」的左栏里**：
> `Load()` 里的一句 `UiOptions.DeclareMod("replay", NAME, "关卡回放录制 / 播放（.mrep）", "QoZnoS", "回放")`。
> 本 mod 是**纯补丁 mod**（不注册 `ModInfo`、没有任何游戏内容、也没有设置项），
> 不主动介绍自己的话，那个列表里根本看不到它 —— 右栏会显示说明/作者，
> 并在下面写明「该 mod 没有声明设置项」。

**回放页**沿用插件页自己的版式（与自定义 / 资源包 / 对话档案同款）：
标题在**面板上方**、返回按钮在**面板下方**，两者都在面板外面；面板撑满它们之间的全部高度。

```
┌────────────────── 回放 ──────────────────┐   ← 标题（页面壳的 Top/Title）
│ ┌──────────────────────────────────────┐ │
│ │ 共 16 个 [导入][刷新][清理回放][排序：时间↓] │ │   ← 面板第一行：数量 + 操作
│ ├──────────────────────────────────────┤ │
│ │ 万圣夜 - 第2天  10-04 22:35  0:03  [播放] [更多] │
│ │ 妖怪之山 - 第10天 10-04 21:52 7:42  [播放] [更多] │
│ │ …（可滚动）                            │ │
│ └──────────────────────────────────────┘ │   ← 面板 = 页面壳的 Center/List
└──────────────────────────────────────────┘
                   [返回]                       ← 页面壳的 Bottom/ReturnButton
```

- 每一行：**回放名 · 添加时间 · 时长 · 播放 · 更多**。
  第一列显示的是 `ReplayInfo.DisplayName`：**改过名的显示玩家起的名字**，
  没改过（名字仍是 `mvz2_<关卡>_yyyyMMdd_HHmmss`）才退回显示关卡名。
  也就是「列表左边就是可用来找自己的那条回放的名字」。
- **「时间」是「添加进库的时间」**（文件创建时间），不是录制开始时刻 ——
  玩家关心的是这条什么时候到他这里的，而不是录制者按下开始那一刻（导入进来的能差好几天）。
- **「排序」只有一个按钮**，点一下按固定六档循环（`ReplayLibrary.SortCycle`）：
  `时间↑ → 时间↓ → 时长↑ → 时长↓ → 关卡↑ → 关卡↓ →（回到起点）`。
- 行尾是【播放】+【更多】。所有破坏性/低频操作都收进【更多】的**详情弹窗**
  （克降官方 `CustomDialog` 的壳，用 `SetDialog` 铺标题/正文/四个按钮，再往按钮前插一行输入框）：

```
╔════════════───── 回放详情 ─────═════════════╗
║ mvz2_mountain_11_20261014_225128             ║
║ 关卡：妖怪之山 - 第11天                          ║
║ 添加时间：2026-10-14 23:05:14                  ║
║ 录制时间：2026-10-14 22:51:28                  ║
║ 时长：16:23　大小：758.3 KB                     ║
║ 格式：规范 1／格式 10                           ║
║ 录制环境：0.7.0／与当前一致                  ║
║                                               ║
║ 新名字：[_______________________]              ║
║                                               ║
║ [重命名] [导出 .mrep] [删除] [返回]              ║
╚═══════════════════════════════════════════╝
```

> 结果提示统一走**原生 Toast**（`UiGame.Scene.ShowPopup`），不另弹对话框：
> 游戏那个 `CustomDialog` 是**单实例复用 + 排队显示**的，两个叠在一起既难看又会互相挤。
> 重命名 / 导出 / 导入 / 清理 / 删除的结果都是 Toast。
> 删除仍然弹二次确认（用的是本 mod 自己的 `Confirm` 浮层）。

- **「清理回放」**按固定优先级只删**一类**（`ReplayLibrary.Cleanup`），
  而且**删之前一定先弹确认框**，正文里必须写清「哪一类、几条」：
  1. **失效**（读不出来的）→ 确认「将清理 已失效、读不出头的回放 N 条。」
     → 删完 Toast「已清理 N 条失效回放」；
  2. 没有失效的 → 删**未命名**的（名字仍是录制器自动生成的 `<关卡>_yyyyMMdd_HHmmss`，
     也就是**玩家没改过名**的）→ 确认「将清理 未命名的回放 N 条（改过名的一条都不会动）。」
     → 删完 Toast「已清理 N 条未命名回放（改过名的一条都没动）」；
  3. 两种都没有 → 弹窗问「要删除全部 N 条回放吗？」。

  > ⚠️ **两条硬性约束，别再简化：**
  > 1. **「未命名」这一类动辄几十条**，而按钮只写着「清理」—— 玩家会以为顶多删掉几个坏文件。
  >    所以确认框**必须**给出条数，且不能把两类合并成一句「清理回放」。
  > 2. 用 `CleanupPreview`（只算不删）来算然后弹确认，**绝不能**拿 `Cleanup` 的返回值
  >    反推「删了什么」。`Cleanup` 是按优先级命中第一类就直接删的，
  >    曾经因此**一次点击静默删光 57 条回放**（目录里只剩那条改过名的），
  >    现场看起来就像「重命名之后其它回放全丢了」。
- **重命名过的不计入 `MaxReplays` 上限，也永不被自动清理**（`Prune()` 只数自动命名的）。
  这条写在【重命名】按钮的悬停提示里。
- **「导入」**：弹游戏**原生的「打开文件」对话框**（与「切换存档 → 导入」同一个），
  选中的 `.mrep`（连同同名 `.mrep.json`）会被复制进回放目录。
  同名已存在时自动退到 `名字 (2).mrep`，**不覆盖**本地那一条。
  导入前会先读一遍 header，选错文件当场说清楚，而不是往列表里塞一条「读不出来」。
  导入后会把文件创建时间设成当前时刻（否则列表里显示的是源文件那个旧日期）。
- **「导出 .mrep」只在【更多】里**（结束面板上那个按钮已按需求移除），
  弹的是同一个原生「另存为」对话框。实现见 `Ui/ReplayExporter.cs`，两个界面共用。
- 「删除」会再弹一个原生对话框二次确认，确认后才真的删（`.mrep` 与 `.mrep.json` 一起删）。

### 名字的颜色 = 播放风险（黑 / 黄 / 红）

- **黑** = 没什么要说的（本面板是浅色底，用黑字才是正常色）。
- **黄** = 「**可能**播不好」：读不出来 / 录制环境与当前不同 / 文件没有环境记录（旧版本录的）/
  缺规范版本号 / 录制时的游戏版本与当前不同 / 是调试模式文件。
- **红** = **上次播放真的出过不一致**（`ReplayPlayer` 收尾时写进目录索引）。

三档**只改颜色** —— 字号 / 字重 / 对齐三档完全一致（统一文字样式），
风险只靠颜色区分。

播放前会**先弹窗说清楚为什么**，而且两种颜色的措辞不同：

| 颜色 | 弹窗标题 | 正文说的事 |
| --- | --- | --- |
| 黄 | 可能不兼容的回放 | **可能出问题的环境**：逐条列出（环境不同 / 无记录 / 版本号 / 游戏版本） |
| 红 | 回放可能无法正确播放 | **上次出问题的时间**：「上次播放到第 N tick 时出现了 M 处不一致」 |

判定与记录规则（`ReplayPlayer.RecordHealth`）：出现任何问题 → 红；没问题且已经跑到
**总时长的一半以上** → 黑；跑得不够久（中途退出/被停）→ **不动**，保留上次判断
（没看到的事不能当成「没问题」）。

### 回放目录索引文件

目录里会有一份 `_replay_index.json`（逐行扁平 JSON，与 `.mrep` 同款格式）：

- **省掉重复读 header**：缓存每条的 header 快照 + 大小 + 修改时间，两者没变就直接复用
  （`.mrep` 是 gzip，每条都要解一次压缩头；50 条 ≈ 上百次 I/O + 解压）。
- **存健康度**（上一条的黄/红）与「上次出问题的 tick」。
- **可以随便删**：缺失 / 损坏 / 版本不符一律静默重建，只是慢一点。
- 索引文件不会被当成回放（扫描只认 `.mrep` / `.mrep.json` 后缀）。
- **日志里会报「索引复用 N 条，重读 header M 条」** —— 这是「索引到底有没有起作用」
  唯一能直接看到的证据（列表卡顿时先看它）。
- ⚠️ 但**索引只能省文件 I/O**。列表显示得慢还有两个别的原因，它们跟索引无关：
  ① 每行要克隆 6 个节点（1 容器 + 3 文本 + 2 按钮），58 行 = 348 次 `Instantiate`；
  ② 排序以前会把整表重建一遍（现在只调 `SetSiblingIndex`，不再重建）。
  要根治 ① 得做分页 / 虚拟列表。
- 另一处只干一次但也不便宜的开销：`CurrentEnvHash()` 要遍历游戏的 mod 列表 +
  BepInEx 插件表（每次游戏启动的**首次**列举回放会做一次），结果会缓存。

### 关卡内：减速按钮（**仅回放播放期间**）

- **只在回放播放时存在**。它是「看回放时的辅助工具」，不是通用 HUD 功能 ——
  正常游玩不会凭空多出一个按钮（而且正常游玩把它减到 1/3 速也没任何好处）。
- 位置在**加速按钮正下方**，图标是加速按钮的**镜像**（两个图标节点的 `localScale.x` 取负）。
- 倍率恒为 `1 / OptionsManager.GetOptionFloat(fastForwardMultiplier)`（即 **倒数**），
  **跟随加速倍率的实时变化**（玩家在选项里拖动快进倍率，这里立刻跟着变）。
- **与加速互斥**：开启减速会把加速关掉；按加速（按钮或热键）会把减速关掉。
- **不注册热键**（把克隆来的热键文字清空），**也没有悬停提示**。
- 实现要点见 `ReplaySlowDown.cs` 的类注释：
  ① 倍率乘在 `LevelController.GetGameSpeed()` 的 postfix 上（**不要**动游戏自己的
  `slowMotions` 列表 —— 那个带超时、没有移除接口，做开关会让它无限增长）；
  ② 互斥只拦 `LevelUIPreset.SetSpeedUp(bool)` 这一个**公开**方法
  （加速的入口太多，但它们最终都会走到这里）；开启减速时直接
  `speedUpButton.onClick.Invoke()`，走的正是游戏自己那条路，不需要反射私有字段。

实现要点（改这块代码前先看这几条，都是实测结论）：

| 事实 | 为什么重要 |
| --- | --- |
| 页面壳 = **插件页自己的 `AddonsUI.indexUI`**（`ShellSource = AddonsIndex`） | 它就是 `Top{Title} / Center{List} / Bottom{ReturnButton}` 那个版式，标题与返回天然在面板外、面板高度天然撑满 —— 一个坐标都不用算。**不要**改用 `CustomDialog` 当壳（那是“标题正文在框体内”的另一种版式） |
| 面板内**本来没有 LayoutGroup** | 原版靠锚点摆位。往里加行之前必须**新建**一个 `VerticalLayoutGroup`（`AddonShell.PreparePanel` 兼管“改”和“建”）。漏了这一步：数量行停在 0×0、列表铺满整个面板盖住边框 |
| 打开页面 = 藏 `indexUI` + 叠自己的壳 | 背景因此天然与插件页/资源包/对话档案一致，不用自己拼美术；离开插件页时自动收起。**这一整套现在在 `AddonPage` 里**，本 mod 不重复实现 |
| **必须摘掉克隆体上的 `TextMeshProUGUITranslator`** | 它的 `OnEnable` 会 `text = lang._p(Context, Key)`，即**节点一启用就把文案按翻译键写回去**（实测：`Build()` 里明明是「回放」，面板一 `SetActive(true)` 就变回「插件」）。入口按钮同理（变回「资源包」）。文案归本 mod 所有，摘掉是正解。`AddonPage` 已自动做 |
| 列表行要**显式给 `LayoutElement`** | 原版控件没有 `LayoutElement`，而外层布局组 `childControlWidth/Height = 1` —— 不给尺寸就是 0 宽 0 高（`UiKit.Fit` / `UiKit.Label` / `UiKit.TextButton`） |
| 动态加完行要 `LayoutRebuilder.ForceRebuildLayoutImmediate` | 布局组不会自己立刻重排，新行会一直停在 0×0（`UiKit.Rebuild`） |
| 克隆体里空 sprite 的 `Image` 要清 alpha | uGUI 在 `sprite == null` 时画的是**纯白矩形**（标题底图、视口遮罩图形都是这种）。`UiKit.NormalizeSubtree` |
| `Mask` 要换成 `RectMask2D`（并 `enabled = false`） | `Mask` 走模板缓冲，会让 TMP 子网格申请**共享**的 Stencil 材质实例，克隆体与模板共用后互相销毁 → 对方 `OnDisable` 时空指针。另：interop 组件上 `DestroyImmediate` 实测**不生效**（dump 里 Mask 与 RectMask2D 并存），必须先禁用。`UiKit.ReplaceMask` |
| 量矩形**必须用 `Il2CppStructArray<Vector3>`** | 传托管 `Vector3[]` 会被隐式转成一份**拷贝**，被调方写的是那份拷贝 —— 读回来全是 0（实测所有矩形都报成 0×0，而画面里明明有东西）。`UiKit.Describe` |
| 按钮回调要用 `UiClone.OnClick` | 克隆体不会复制原版**运行时**挂的 `AddListener`；且托管委托必须自己留住，否则 GC 一收就是野指针 |

---

## 自测（不需要人工操作）

```powershell
.\tools\run-replay-selftest.ps1                          # 录一条 + 回放它 + 核对指纹
.\tools\run-replay-selftest.ps1 -PlayFile <回放名>        # 只播放指定回放并出报告
.\tools\run-replay-selftest.ps1 -Off                      # 只关掉自测开关（不启动游戏）

# 把录制→回放的窗口拉长（默认只有 ~200 tick，什么都看不出来）
.\tools\run-replay-selftest.ps1 -Stage mvz2:halloween_1 -WarmupTicks 900 -CooldownTicks 600
```

> ⚠️ **自测开关是写进游戏配置文件**（`BepInEx\config\qoznos.mvz2.replay.cfg` 的 `[SelfTest]` 段）
> **的持久状态，不是命令行参数。** 所以脚本被 Ctrl+C / 被杀 / 中途出错时，
> `Enabled = true` 会**留在配置里** —— 之后每次启动游戏都会自动进关卡、注入输入、自动退出，
> 正常游玩就没法进行了。这种情况下用 `-Off` 清掉（它会先检查游戏没在运行，
> 避免 BepInEx 退出时把配置写回去）。
>
> 注意 `-Off` 与 `-NoLaunch` 不同：`-NoLaunch` 是**准备好配置但不启动游戏**（调试用），
> 它会**打开**自测；`-Off` 才是**关闭**。

它把 `[SelfTest] Enabled` 打开 → 启动游戏 → 等游戏**自己**跑完把报告写出来 → 关掉开关 → 打印报告。
全程不碰鼠标键盘、不抢前台。

**只播放模式**（`-PlayFile`，对应配置 `[SelfTest] PlayFile`）专门用来反复排查「回放复现不出来」：
它跳过录制，启动后直接播放你指定的那个 `.mrep`，播完（或中止/超时）把注入量、
指纹对比结果与不一致明细写进报告。与在游戏里手敲 `/replay play` 等价，但是全自动、可重复。

自测由 `MainSceneController.Update` 的心跳驱动，状态机是：

1. 等主菜单就绪；若停在标题界面，用 `DisplayMainmenu()` **程序化**跳过
   （与玩家点一下「点击以开始」等价，但不需要人去点）
2. 用游戏自己的调试指令 `/gotolevel <stage> <area>` 进关卡
   （和玩家在控制台里敲的完全一样）
3. 等录制手臂就绪；超时就重试进关，再不行就
   `ApplyChoose() + GameStartInstantTransition() ` 直接开局
   （用于「关卡存档首行校验不通过」这类走不下去的情况）

   > ⚠️ 如果该关卡**已有 `.lvl` 存档**（游戏退出关卡时会自动存），
   > `/gotolevel` 会走「继续游戏」路径：`LoadGame` → 弹「关卡已载入」→ `PauseGame()`。
   > **暂停时逻辑 tick 不前进**，而热身/冷却阶段是按 tick 计数的 —— 所以自测会在
   > 暂停超过 ~2 秒后**替玩家点「继续」**（只影响 `SelfTest.Enabled = true` 时，最多试 3 次）。
   > 注意这种情况下录制会从**存档那一 tick** 开始，而不是 tick 1。
4. 按 tick 间隔注入**真实输入入口**（反射调用私有的
   `LevelController.OnBlueprintKey` / `ClickPickaxe` / `ClickStarshard` / `ClickTrigger`）
   —— 会真正经过 Harmony 补丁，所以是端到端验证
5. 冷却若干 tick → 停止录制
6. 校验写出的 `.mrep`：gzip 解压 → header 字段 → 逐行 JSON 形状 → 事件/指纹计数
7. **播放刚刚录的这一条**，用录制时的指纹逐条核对回放是否复现出完全相同的状态
   （`[SelfTest] Playback = true`，默认开）
8. 报告写到 `<游戏目录>\BepInEx\_replay_selftest.txt`，然后（默认）自动退出游戏

> **卡住检测**：关卡在播放途中**结束**（失败/通关）时游戏会弹对话框并 `PauseGame()`，
> 而暂停时逻辑 tick 不前进 —— 播放会一直卡到超时（实测白卡了 300 秒，连小结都拿不到）。
> 所以第 7 步会盯着「tick 是否前进」：**20 秒不动就提前收尾**，
> 保证小结（尤其是「目标找不到的分类」）一定写得出来，`停下原因` 会是 `selftest-stalled`。

报告里每一项都标了 `OK`/`FAIL`，并附带事件/指纹样本，失败时给出诊断
（`ctrl` / `engine` / `started` / 当前页面 / 重试次数）。

自测相关配置（都在 `[SelfTest]` 段）：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 总开关（脚本会自己打开再关掉） |
| `Stage` / `Area` | `mvz2:halloween_2` / `mvz2:halloween` | 进哪个关卡 |
| `StartupTimeoutSec` | `300` | 等主菜单的上限（调试版冷启动很慢，别调小） |
| `LevelTimeoutSec` | `180` | 等关卡开始的上限 |
| `RetrySec` | `25` | 重试间隔 |
| `WarmupTicks` | `60` | 录制开始后先等多少 tick 再注入 |
| `Actions` | `bpkey0,bpkey1,pickaxe,starshard,trigger` | 注入哪些动作 |
| `ActionGapTicks` | `30` | 动作间隔 |
| `CooldownTicks` | `90` | 最后一个动作后再录多少 tick |
| `AutoQuit` | `true` | 报告写完后自动退出游戏 |
| `Playback` | `true` | 录完是否立刻播放一遍并用指纹核对一致性 |
| `PlaybackTimeoutSec` | `180` | 等回放播完的上限（只播放模式下由脚本按 `-TimeoutSec` 写入） |
| `PlayFile` | `(空)` | 填了它就**跳过录制**，直接播放这个回放 |
| `PlayTickLimit` | `0` | 只播放到第多少 tick 就收尾（`0` = 不限制）。排查分歧时把它压到几秒 |
| `PlayMenuTick` | `0` | 播放到第几 tick 时把 Esc 菜单打开一次、把里面所有文案写进报告并截图（`0` = 不测）。用来验收「重新播放 / 结束播放」|
| `PlayShotTick` | `0` | 播放到第几 tick 时抓两张相隔 15 tick 的截图（`0` = 不抓）。
  用来目视确认「假鼠标在动、位置/尺寸/层级都对」—— 指纹能证明状态一致，但证明不了画得对 |
| `DeleteLevelState` | `false` | 卡住时的救援开关，见下 |

> ⚠️ **`DeleteLevelState` 为什么值得开着（开发/自动化时）**：
> 关卡存档首行的 `identifiers` 记的是「每个 mod 的 `LevelDataVersion`」。重建过 mod、
> 换过 mod 组合之后，旧存档就对不上了；而游戏对此的反应是
> `ValidateGameStateHeader` 返回 false → `ShowLevelMismatchLoadingDialog` ——
> **日志里一行错都没有**，外面看到的只是 `ctrl=True engine=False` 一直卡到超时。
> 开着它：连续两次重试管不出引擎、就把该关卡的 `.lvl` 删掉再来一次（会丢该关卡进度）。

### GUI 自测（`[GuiTest]` 段）

专门验收「插件页 → 回放」那条路：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 总开关 |
| `HoldFrames` | `90` | 每一步之后等多少帧再截图/取样（`ScreenCapture` 帧末才写盘，等太短会拍到上一拍）|
| `AutoQuit` | `true` | 跑完自动退出游戏 |

> ⚠️ **不要和 `[SelfTest]` 同时开。** 两者是各自独立的开关、各跑一套状态机，
> 而 `[GuiTest]` 跑完会 `Application.Quit()` —— 它会把还在「等关卡」阶段的 `[SelfTest]`
> 直接掐死（症状：`_replay_selftest.txt` 永远停在 `等关卡 5s`、`结果: 进行中`，
> 且 `LogOutput.log` 尾部是 `[GuiTest] 自动退出游戏`）。
>
> **❗ 而且这个坑的现象很容易被误读：在回放播放中它看起来就是「回放播到一半闪退」。**
> 真实例子：一次只播放模式跑到 `播放tick=50` 就“闪退”，日志尾部是
> `[GuiTest] 完成 → [GuiTest] 自动退出游戏 → 播放停止：application-quit` ——
> 根因是上一次跑 GUI 自测把 `[GuiTest] Enabled = true` **留在了配置里**
> （GUI 脚本跑完不会把开关改回去），而 `run-replay-selftest.ps1` 以前只写 `[SelfTest]` 段。
>
> 现在三道保险：① `run-replay-selftest.ps1` 启动前会把 `[GuiTest] Enabled` 删掉；
> ② `run-replay-gui-test.ps1` 跑完把自己关掉；③ 代码里 `[GuiTest]` 完成时若
> `ReplayPlayer.IsActive` 就**不退出**（只记一行警告），宁可把游戏留着也不制造假崩溃。

它做的事：等主菜单 → `DisplayPage(Addons)` → 等入口装上 → **调用一次入口的 `onClick`**
→ dump 布局矩形 + 截图 → **把排序循环走一圈** → **打开【更多】详情**（含克隆来的
`TMP_InputField`）+ 截图 → **重命名**那条 → 从详情返回列表 → **跑一次清理**
→ 弹一次删除确认 → 截图 → 收起 → 截图 → 写报告。

> 布局 dump 里每一行单元格后面会附上**文字颜色 `rgb(r,g,b)`** ——
> 名字黄/白/红这种档位光看字符是看不出来的，不记下来就只能靠人眼盯截图。

不想手动改配置就一键跑：

```powershell
.\tools\run-replay-gui-test.ps1              # 自动写 [GuiTest] Enabled=true（并把 [SelfTest] 关掉）→ 启动 → 等报告
.\tools\run-replay-gui-test.ps1 -Off         # 只把开关关回去（不启动游戏）
```

- 报告：`<游戏目录>\BepInEx\_replay_gui.txt`（每步的屏幕矩形、入口文案、条目数）
- 截图：`<游戏目录>\BepInEx\_rp_shot0_page.png` / `_rp_shot1_confirm.png` / `_rp_shot2_closed.png`

---

## 构建

```powershell
.\build.ps1              # 构建 + 部署到测试游戏目录
.\build.ps1 -Launch      # 构建后启动游戏
```

在仓库根目录等价于 `.\build.ps1 -Mod rp`。

⚠️ interop 程序集与游戏二进制绑定：换游戏版本后必须重新生成 interop
（见仓库根 `readme.md` 与 `tools/setup-game-runtime.ps1`）。

---

## 排障工具

| 脚本 | 作用 |
| --- | --- |
| `tools\inspect-replay.ps1 -Name <回放名>` | 打印头部、关键帧大小、事件类型直方图与事件样本 |
| `tools\replay-stats.ps1 -Name <回放名>` | 按 `kind × PointerInteraction` 统计，并列出「位置全 0」的事件数 |
| `tools\run-replay-selftest.ps1 -PlayFile <回放名>` | 自动播放并生成一致性报告 |
| `tools\run-replay-gui-test.ps1` | 一键跑「插件页 → 回放页」的 GUI 自测（自动改配置、启动、等报告） |
| `tools\find-log.ps1` / `tools\show-log.ps1` | 看游戏日志（游戏开着时日志被占用，用这两个脚本） |

### 播放失败：`LoadGame 返回 false`

`LoadGame` 里所有失败路径都是「`catch` → `Debug.LogException` → `return false`」，
而 **`Debug.LogException` 只进 Unity 的 `Player.log`**
（`%USERPROFILE%\AppData\LocalLow\Cuerzor\MinecraftVSZombies2Test\Player.log`），
**不进 `BepInEx\LogOutput.log`**，也不进 `ErrorLog.log`。所以 BepInEx 日志里
「没有任何异常」≠ 真的没抛异常 —— 直接去 `Player.log` 里搜 `LogException`。

两种已见过的原因：

| `Player.log` 里的东西 | 原因 | 处理 |
| --- | --- | --- |
| `ArgumentException: An item with the same key has already been added. Key: [N, BuiltinCollisionEntity]` | 关键帧被 `LoadGame` 到了**已经 `InitLevel` 过**的旧控制器 | 等控制器换了实例再 LoadGame（已在代码里修掉） |
| `invalid IL` / `…` | 文件是用修改前的版本录的，关键帧落在 **tick 0** | 游戏载不了 tick 0 的存档，**重录一遍** |

### 关键帧**不能**在 tick 0

第 0 tick 之前有些部件（进度条 / 蓝图选择 / 工具 UI …）还没序列化完整，
把这种存档交给 `LoadGame` 会直接失败。所以录制側**拒收 `levelTime < 1` 的关键帧**，
等第一个 tick 跑完再抓（`startTick` 也在那时才定为 1）。
自测里 `/gotolevel` 后**立刻**开局的场景很容易撞到这个 —— 现在应该不会了。

### 排查「回放对不上」的顺序

0. **先看环境**。播放小结与回放库列表都会给「录制环境 vs 当前环境」的差异。
   换了游戏版本 / 装或卸了 mod，多半**根本不会复现** —— 这时候再往下查都是白费力气。
   其中「内容 mod 关卡数据版本不同」最严重：那意味着关键帧**载不进去**。
1. **还查不动就开调试模式**（`[Debug] Enabled = true`）录一小段，用 `-PlayFile` + `-TickLimit`
   回放它。逐 tick 指纹 + `rx`/`ei`/`lk` 三个字段会直接告诉你
   **从哪个 tick 起不同、是 RNG 先变还是实体先变、`currentEntityID` 差多少**。
   不一致报告会给出**首个不同处的片段**（例如 `10:mvz2:fragment:0:780 → 1:mvz2:zombie:200:1066`）。
   ⚠️ 只在排障时开，文件会大 1-2 个数量级。
2. **看播放小结**（控制台里就有，也在自测报告里）：
   - `实体不存在 / 实体 ID 错位` 不为 0 → 看下面第 4 步；
   - **`目标找不到的分类`** 是最先要看的东西：它按「交互类型 + 目标定义」聚合，能直接区分
     *无伤大雅的悬停漏掉*（`Enter/Exit/Stay` 打在一个已经消失的特效实体上）和
     *真的丢了一次点击*（`Down`/`Hold`/`Streak` 打在 `mvz2:redstone` 上 = 少收一份资源）；
   - `注入分类` 里某一类为 0 → 那类事件根本没发生（例如全部 `entity` 被跳过）；
   - `指纹核对 0 / N` → 从一开始就发散；
   - `⚠ 有 N 个指纹字段没法核对` → 那些字段本 mod 算不出来，**别把它读成「全都对上了」**。
3. **看日志里的「载入后状态」**：
   - `载入后状态与关键帧一致 ✅` → 载入是准的，问题在后面的 tick；
   - `❌` 或只有一条 `载入后状态：t=… en=…` → 关键帧没还原到位（或文件太旧没存 tick=startTick 的指纹）。
4. **看日志里的「找不到实体 id=… 当前关卡里存在的 id：…」**：
   对比两边的 ID 集合就能看出错位多少。
5. 用 `PlayTickLimit` 把一次验证缩到几秒（例如 60），不用每次等完整局。

### 某个动作在回放里**根本没发生**（例如「第一个器械没放下来」）

这类问题**不能**只看注入计数：`grid`/`blueprint` 事件注入后游戏完全可能什么都不做
（手持物不对 / 格子不可放 / 能量不够），而回放器从外部看不出区别。用探针：

```powershell
# 1. 打开探针（General 段）
#    ProbeEnabled = true
# 2. 只放到出问题的那一刻附近，快得多
.\tools\run-replay-selftest.ps1 -PlayFile <回放名> -TickLimit 760
```

日志里每个 `Down` 事件都会给出「注入前 / 注入后」的手持物：

```
[ReplayProbe] grid  Down  t=680 lane=5 col=1 能量=300 手持=[mvz2:blueprint #9=mvz2:maplewheel(cost 175)] 格子上=[(空)]
[ReplayProbe]      → 注入后 手持=[mvz2:none]      ← 放置成功（成功会清空手持物）
```

关键信号是「**放置成功后游戏会把手持物清空**」：

| 前 | 后 | 含义 |
| --- | --- | --- |
| 空 | 空 | 前面的**选卡**就没生效（问题在选择，不在放置）|
| 非空 | 非空（值不变）| 游戏**拒绝了这次放置**（格子 / 能量 / 地形）|
| 非空 | 空 | 放置成功 |

> ⚠️ **测一致性时不要开探针。** 实测开探针会把首次不一致从 tick 690 提前到 tick 30
> （丢一个 `floating_text`）—— 探针本身不改任何状态，只是让注入路径更慢，
> 说明有实体的寿命跟**真实帧时间**有关，而回放帧率取决于 mod 干了多少活。
> 探针只用来回答「机制」类问题（前后手持物是什么），那类结论不受影响。

---

## 已知限制（0.1.0）

### ✅ 0.7.0 上播放已修好（2026-10-14）

之前 0.7.0 上「开始播放」会让游戏 `Fatal error. 0xC0000005` 退出。两个根因都已定位并修掉：

1. **存档护栏在进关卡期间必须是关着的。** 护栏的补丁只要在「切场景」这段时间被调到就必崩
   （对照实验：关掉护栏、其它全不变 → 正常）。现在 `Begin()/SeedMoney()` 推迟到
   关卡已经就绪、进入 `Phase.Playing` 那一刻才做。
2. **进关卡改走游戏自己的指令**：关卡外 `/gotolevel <stage> <area>`、关卡内 `/load`。
   整条场景加载链路都留在 il2cpp 原生侧。关键帧的载入由 **`ReplaySceneEntry` 自己轮询**：
   等旧场景卸完 → 发现新控制器指针 → `LoadGame(关键帧)`。
3. **游戏会「迟到重建」关卡（已挡住）。** `/load` 那条链里
   `await UnloadSceneAsync(旧场景)` 的续体不是不恢复，而是**恢复得很晚**，
   而且恢复后**绕过了 `LevelManager.InitLevel` 的托管桩**、直奔
   `LevelController.InitLevel` + `StartLevelIntro`，把关卡整个重建、冲掉刚载好的关键帧
   （症状：`currentEntityID` 13 → 7、实体变成预览敌人）。
   现在这两个方法各挂一道前缀：接管中就先载关键帧 / 已接管就直接 `return false`。
   不管谁调、从哪跳进来都不会再冲掉状态。

实测：录制 → 关卡内 `/load` 重播 → 播放到末 tick → 结束面板 → 回「回放页」全流程跑通，
**指纹核对 1/1 ✅ 全部一致**，补丁 0 失败。
完整证据链见 [DESIGN.md](./DESIGN.md) §4.4.15 / §4.4.16 / §4.4.17。

### ✅ 保真度问题已解决（2026-10-14）

自测「录制 + 播放」以前是 `指纹核对 0/3`：能量 / 波次 / 旗数 / 实体总数都对得上，
差的只有**实体定义集合**与**RNG**。用调试模式（`[Debug] Enabled=true`）追下去，
把范围缩到了「**开始按 tick 播放后的几帧内，关卡被整个重建了**」——
也就是上面第 3 条。闸门一拦，现在就是：

```
指纹核对 1 / 1   ✅ 全部一致
[OK] 回放状态与录制时完全一致（指纹不一致 0 条）
停顿在 tick 210 / 210   注入事件 5 / 5
```

> 这条线索是调试模式（§「调试模式」）直接送出来的：常规模式下只能看到「RNG 对不上」，
> 逐-tick 的 `rx`/`ei`/`lk` 一开，一眼就看出是「关卡被换了一整份」。

### 其它

- 不记录暂停 / 加速 / 血条显示 / 控制台开关（这是设计要求，不是缺陷）。
- **v1 文件**（含指针位置之前录的）也能放，但工具事件只能按近似处理，位置一律当 0。
- `Global.Input` 的三个指针位置方法是**小函数**，IL2CPP 有可能内联掉导致前缀不生效。
  播放小结里会报「指针位置查询被改写 N 次」：一直是 0 就说明这个补丁没生效，
  影响范围只限于「剑的砍击方向」这类看实时鼠标的东西。
- 玩家人为按暂停时，暂停期间错过的指纹会被跳过（不参与比对），不会误报不一致。
- **真实鼠标仍会漏一点进仿真**：`InputManager.UpdatePointer()` 每帧都会用**实时鼠标位置**
  发 `POST_POINTER_ACTION`（未按下的键发 `None` 相位）。挂了这条回调、又**不筛相位**的内容
  （如打砖块）因此仍会读到你的真实鼠标 —— 虽然我们现在也把 `Stay→None` 补上了（挡板会跟着
  回放位置走），但原件那条实时轮询没法关掉，两者会叠加。按 `Press` 筛选的（右键取消、碎石墙）不受影响。
  回放期间**别碰鼠标**最保险。
- 探针（`ProbeEnabled`）会通过延长帧时间扰动仿真，**测一致性时必须关闭**（见上文）。

### ⚠️ 实体 ID 会错位（这是最大的一个坑）

实体 ID 是由关卡内一个自增计数器分配的。**计数器本身会被存进关键帧**，理论上应当完全对齐；
但只要录制与回放之间有**任何一处生成时机差一两个 tick**，后面的 ID 就整体错位，后果是：

```
FindEntityByID(录下的 id)
  → 找不到   ⇒ 这条事件被丢掉
  → 找到的是另一个实体 ⇒ 静默地把动作做到错的东西上（比丢掉更糟）
```

实测症状就是「**资源（阳光）收集全部没有被采集到**」：收集阳光靠的就是实体指针事件，
一旦找不到目标，阳光就不再进账 → 能量对不上 → 后面能种的植物也不一样 → 全线发散。

所以 `entity` 事件额外记了目标的**定义 ID** 与**坐标**，回放时：

1. 先按 ID 找，**定义也一致** → 直接用；
2. 否则按「同一种东西里、离记录坐标最近的那个」找（半径 = 半格；一个格子是 80 草坪单位）；
3. 否则按「**离记录坐标最近的任意实体**」当锚点（见下）；
4. 都不行 → **跳过**（宁可漏一个动作，也不要打错目标）。

播放小结会报「实体 ID 错位 N 次（按定义+坐标找回 M 次 / 真跳过 K 次）」——
只要 M/N 不为 0，就说明这次回放已经发生过错位，指纹很难再完全对上。

#### 为什么需要第 3 级（「镐子用了但大碗还在」）

很多手持物行为只把指针事件里的实体当作**命中测试的锚点**，真正的作用对象是它自己按
位置就近挑的：

```csharp
// ToEntityHeldItemBehaviour.OnPointerEventEntity
var targetEntity = entity.FindPointerTargetEntity(target.LocalPointerPosition.y,
                                                target.ScreenPosition, CanUseOnEntity);
```

实测案例（`mvz2_mountain_10`，tick 2899~2910）：录下的 `entity` 事件挂在鼠标底下的
**`mvz2:starshard`** 上，而真正被挖掉的是它**旁边**的 `mvz2:giant_bowl`：

| tick | 录制 | 回放（修之前）|
| --- | --- | --- |
| 2899 | 拿起镐子 | 拿起镐子 |
| 2906 | 在 starshard 上按下 → 就近挖掉 giant_bowl | starshard 不存在 → **整条事件被丢** |
| 2910 | `giant_bowl=4, starshard=1` | `giant_bowl=5`（没挖掉）|

因为「同定义」那一级会失败（场上根本没别的 starshard），而丢掉整条事件就等于丢掉那次挖除。
改用记录坐标当锚点后，游戏会自己从锚点就近再选目标，总比什么都不做更接近录制。
这类事件的计数会在小结里单独列出。

> **v2 早期录的文件没有 `def`/坐标**，那些文件里回放仍然只能认 ID，会重现上述症状。
> 想拿到完整保真度，请用当前版本重新录一遍。

### ⚠️ 开着「显示赞助者名字」时，回放几乎必然歪种（上游 bug，已在修）

游戏设置里的「显示赞助者名字」打开时，放下**熔炉 / 月光传感器 / 火药桶**
会额外生成一个 `mvz2:floating_text` 显示出资人名字：

```csharp
// SponsorsGlobalCallbacks
if (!Global.Options.ShowSponsorNames()) return;
var names = Global.Sponsors.GetSponsorPlanNames(…);   // ← 名单来自外部
if (names.Length <= 0) return;
… furnace.Spawn(VanillaEffectID.floatingText, …);
```

名单来自外部（不是关卡的种子），所以**两次会话能拿到的名字数量/内容可能不一样** →
`floating_text` 生成与否都不同 → **实体 ID 从开局就整体错位**，后面的动作全部对不上。

实测症状：首次指纹不一致稳定地出现在 **tick 30**（刚好是第一个熔炉放下的地方），
内容是「少一个 `mvz2:floating_text`」，而且 `entity` 那条 RNG 已经不同。

> 作者已经在修这个 bug（「赞助者名单导致歪种」），等新版打包。
> **在那之前，要测回放就先把「显示赞助者名字」关掉** —— 关掉后不会生成这些
> `floating_text`，这一路歪种就没了，回放才能真的复现。

### 为什么不挂 `HeldItemComponent`

最自然的做法是挂 `HeldItemComponent.SetHeldItem/ResetHeldItem/CancelHeldItem`，
一次性覆盖所有手持物变化。但 0.7.0 实测**做不到**，而且原因很隐蔽：

```
TargetInvocationException: Exception has been thrown by the target of an invocation.
 <- TypeInitializationException: The type initializer for 'Il2CppInterop.Runtime.Il2CppClassPointerStore`1' threw an exception.
 <- TypeInitializationException: The type initializer for 'MVZ2.Level.HeldItems.HeldItemComponent' threw an exception.
 <- SEHException: External component has thrown an exception.
```

`HeldItemComponent` 有一个静态字段：

```csharp
public static readonly NamespaceID componentID = new NamespaceID(Global.BuiltinNamespace, "heldItem");
```

`Global.BuiltinNamespace` 要等游戏初始化之后才可用，所以在 `Plugin.Load()`
阶段一碰到这个类型就 native 报错；而且 `TypeInitializationException` 会被运行时
**永久缓存**，连带给以后任何想用这个类型的代码下毒。

→ 所以本 mod **永远不主动触碰 `HeldItemComponent`**，改用上面的
5 个漏斗 + 3 个工具点击入口。覆盖面是完整的：

| 手持物变化的来源 | 被谁覆盖 |
| --- | --- |
| 选卡（点卡/热键） | 蓝图漏斗 / `OnBlueprintKey` |
| 落地拾取蓝图、对实体使用 | 实体漏斗 |
| 镐子 / 星之碎片 / 触发器 | `ClickPickaxe` / `ClickStarshard` / `ClickTrigger` |
| 点空白取消 | 草坪漏斗 |
| `ValidateHeldItem`、行为 `OnUpdate` 的自动重置 | **不需要记录** —— 由状态派生，回放会自动重现 |
| `StartGame` / `LoadGame` 里的 `ResetHeldItem` | **不需要记录** —— 发生在关键帧抓取之前，已包含在关键帧里 |

设计依据与后续计划见 `DESIGN.md`。
