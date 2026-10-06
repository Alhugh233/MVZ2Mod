# MVZ2 Replay（关卡回放录制 / 播放）

> 《Minecraft vs Zombies 2》0.7.0 的社区 mod。依赖 **DSHCore** 前置。
> 记录一局关卡的游玩过程并写出 `.mrep` 回放文件；也能把 `.mrep` 放回来重演，
> 并且用录制时采到的**状态指纹**自动核对「回放和实际游玩是否一致」。

- 插件 GUID：`qoznos.mvz2.replay`
- 当前版本：**0.1.0**
- 适配：MVZ2 **0.7.0 test-10**（Windows **x86** / IL2CPP / Unity 2022.3.62f3）

---

## 它记录什么

时间轴单位是游戏自己的**逻辑 tick**（`LevelEngine.GetLevelTime()`，30 tick/秒）。
因为游戏的加速/慢动作只改变「一帧跑几个 tick」，不改变每个 tick 的内容，
所以按 tick 记录天然就是「**不记录暂停/加速**」的。

`0.7.0 test-10` 的玩家输入汇聚成 5 个调用点，全部被挂钩（参数已被 test-10
统一成 `MVZ2.Level.Inputing.PointerInteractionArgs` 结构体，里面带 pointerId / button / interaction）：

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

`.mrep` 是 **gzip** 压缩的逐行 JSON（与游戏的 `.lvl` 一样用 gzip）：

```
第 1 行   header          {"magic":"mrep","ver":3,...}
第 2 行   keyframe        {"k":"keyframe","tick":0,"state":"<SerializableLevelController 的 BSON>"}
之后      ...             {"t":123,"k":"grid","lane":1,"col":3,"pid":-1,"btn":0,"ia":4,"sx":960,"sy":540,"wx":1.2,"wy":0.3,"wz":2.4}
          ...             {"t":30,"k":"fp","en":175,"w":1,"fl":0,"cl":false,"r":"..."}
```

配置项 `WritePlainText = true`（默认）时会额外写一份**未压缩**的 `.mrep.json`，方便直接查看。

### 事件类型

| `k` | 含义 | 额外字段 |
| --- | --- | --- |
| `grid` | 格子上的指针事件 | `lane` `col` |
| `entity` | 实体上的指针事件 | `id` `def` `lyr` `ex/ey/ez` |
| `blueprint` | 点选卡槽 | `idx` `cv` |
| `bpkey` | 选卡热键 | `idx` `cv` `key` |
| `lawn` | 草坪区域 | `area` |
| `tool` | 工具点击 | `name`（`pickaxe` / `starshard` / `trigger`） |
| `fp` | 状态指纹 | `en` `w` `fl` `cl` `ec` `eh` `ed` `r` `r0`..`r6` |

`t` = tick，`pid` = 指针 ID，`btn` = 鼠标键，`ia` = `PointerInteraction` 枚举值，
`s*` / `w*` = 指针位置（见上文）。

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

> `ed` 与 `r0`..`r6` 由 `FullFingerprint`（默认开）控制。关掉能省约 150 字节/条，但排障时就没东西了。
> 历史上还有一个「完整 RNG 状态（x/y/z/w）」的 `rng` 字段，已删除 —— 它是嵌套数组，
> 单行 JSON 解析器读不了，留着只会制造解析错误。

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

### 回放结束面板

回放播完（**播放完毕 / 关卡通关 / 关卡失败 / 手动结束**）时会弹出结束面板，三个按钮：

| 按钮 | 做什么 |
| --- | --- |
| `导出 .mrep` | 弹游戏**原生的「另存为」对话框**（与「切换存档 → 导出」同一个），把 `.mrep`（连同同名的 `.mrep.json`）写到玩家自己挑的位置；结果写回面板正文最后一行 |
| `重新播放` | 从头再播这一条（`ReplayPlayer.RestartCurrent()`）|
| `退出` | 结束播放，**直接落到回放页**（不是地图；关掉回放页再回主菜单）|

- **两种到达方式**：① 播完（含通关 / 失败 / 手动 `/replay stop`）；
  ② 回放里按 Esc →「结束播放」→ 确认（走 `ReplayPlayer.FinishFromMenu()`，
  和播完**同一条收尾路径**，并会先把 Esc 菜单收起来）。
- 面板开着的时候**输入是被拦的**（和播放中一样），免得顺手点到关卡里的东西。
- 导出结果为什么写在面板正文里而不另弹一个提示框：游戏的对话框是**排队显示**的，
  「面板 → 提示 → 再弹回面板」这种链式弹窗会踩到排队时序（实测出现「面板已经换了、
  旧的那个还挂在屏幕上」）。现在整条链上始终只有一个对话框。
- 「另存为」是**系统级阻塞对话框**（弹着的时候游戏主线程停住）—— 游戏自己的导入导出同理。

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
| `AutoRecord` | `false` | 进入关卡自动开始录制。默认**关** —— 平时玩关卡不会产生任何回放文件 |
| `AutoSave` | `false` | 录制**过程中**是否每 20 秒把回放写一次盘。默认**关** —— 只在录制**结束**时写一次（离开关卡 / 退出游戏 / 手动停止），也就是「关卡里不会自己冒出回放文件」。开着它每 20 秒就在回放目录落一次盘 |
| `OutputDir` | `save` | 输出目录。见下方「回放存在哪」 |
| `FingerprintInterval` | `150` | 每多少 tick 采一条指纹；`0` = 关闭 |
| `FullFingerprint` | `true` | 指纹里是否额外存 `ed`（实体定义直方图）与 `r0`..`r6`（7 条 RNG 流各自的哈希）|
| `WritePlainText` | `false` | 是否额外写未压缩的 `.mrep.json` |
| `DiscardEmpty` | `true` | 一条动作都没录到时不产生文件 |
| `PointerAction` | `true` | 是否把录到的鼠标动作也发给**输入层的全局回调**（右键取消手持物等）。见「它是怎么重演的」第 5 条 |
| `MaxReplays` | `50` | 最多保留多少个回放（每写出一份就清一次最旧的）。`0` = 不限制 |
| `Commands` | `false` | 是否注册 `/replay` 控制台指令。默认**关** —— 普通玩家走界面就行 |
| `ProbeEnabled` | `false` | 排障探针：把注入 `blueprint`/`grid` 时游戏认为的状态写进日志。**别在测一致性时开**（见下）|
| `ProbeMaxLines` | `600` | 探针输出上限 |

> 曾经的 `GuiEnabled` / `EndPanel` / `ExportDir` 已经删了：GUI 入口常驻（就当没这个开关），
> 结束面板也常驻，导出改成**原生「另存为」对话框**（目标由玩家自己挑，不再配目录）。
> 老 cfg 里残留的这几行会在下次启动后自动消失。

`[Visual]` 段（播放时的观感，见上方「假鼠标」）：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `FakeMouse` | `true` | 是否合成鼠标轨迹并画假鼠标 |
| `FakeMouseTicks` | `0` | `0` = 缓动占满整个区间（全程不停） |
| `FakeMouseEase` | `linear` | 缓动曲线 |
| `FakeMouseScale` | `1` | 假鼠标贴图的显示大小倍率 |
| `FakeMouseGameCursor` | `true` | 用游戏自己的光标贴图 |

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
┌───────────────── 回放 ─────────────────┐   ← 标题（页面壳的 Top/Title）
│ ┌────────────────────────────────────┐ │
│ │ 共 16 个 [导入][按时间][按关卡][按时长] │ │   ← 面板第一行：数量 + 导入 + 排序（并排）
│ ├────────────────────────────────────┤ │
│ │ 万圣夜 - 第2天  10-04 22:35  0:03   [播放] [删除] │
│ │ 妖怪之山 - 第10天 10-04 21:52 7:42   [播放] [删除] │
│ │ …（可滚动）                          │ │
│ └────────────────────────────────────┘ │   ← 面板 = 页面壳的 Center/List
└────────────────────────────────────────┘
                 [返回]                       ← 页面壳的 Bottom/ReturnButton
```

- 每一行：**关卡名 · 存档时间 · 时长 · 播放 · 删除**。
- **「导入」**（在排序按钮左边）：弹游戏**原生的「打开文件」对话框**（与「切换存档 → 导入」
  同一个），选中的 `.mrep`（连同同名 `.mrep.json`）会被复制进回放目录。
  同名已存在时自动退到 `名字 (2).mrep`，**不覆盖**本地那一条。
  导入前会先读一遍 header，选错文件当场说清楚，而不是往列表里塞一条「读不出来」。
- 「删除」会再弹一个原生对话框二次确认，确认后才真的删（`.mrep` 与 `.mrep.json` 一起删）。
- 数量上限是配置里的 `MaxReplays`（默认 20），**每次写出一份新回放**就清一次最旧的。

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
> 且 `LogOutput.log` 尾部是 `[GuiTest] 自动退出游戏`）。跑回放回归前先把它设回 `false`。

它做的事：等主菜单 → `DisplayPage(Addons)` → 等入口装上 → **调用一次入口的 `onClick`**
→ dump 布局矩形 + 截图 → 弹一次删除确认 → 截图 → 收起 → 截图 → 写报告。

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

1. **看播放小结**（控制台里就有，也在自测报告里）：
   - `实体不存在 / 实体 ID 错位` 不为 0 → 看下面第 3 步；
   - **`目标找不到的分类`** 是最先要看的东西：它按「交互类型 + 目标定义」聚合，能直接区分
     *无伤大雅的悬停漏掉*（`Enter/Exit/Stay` 打在一个已经消失的特效实体上）和
     *真的丢了一次点击*（`Down`/`Hold`/`Streak` 打在 `mvz2:redstone` 上 = 少收一份资源）；
   - `注入分类` 里某一类为 0 → 那类事件根本没发生（例如全部 `entity` 被跳过）；
   - `指纹核对 0 / N` → 从一开始就发散。
2. **看日志里的「载入后状态」**：
   - `载入后状态与关键帧一致 ✅` → 载入是准的，问题在后面的 tick；
   - `❌` 或只有一条 `载入后状态：t=… en=…` → 关键帧没还原到位（或文件太旧没存 tick=startTick 的指纹）。
3. **看日志里的「找不到实体 id=… 当前关卡里存在的 id：…」**：
   对比两边的 ID 集合就能看出错位多少。
4. 用 `PlayTickLimit` 把一次验证缩到几秒（例如 60），不用每次等完整局。

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
一次性覆盖所有手持物变化。但 0.7.0 test-9 实测**做不到**，而且原因很隐蔽：

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
