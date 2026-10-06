# MVZ2 回放 mod（`.mrep`）调研与设计

> 目标：记录并回放一局关卡游玩过程，文件为 `.mrep`，暂时通过控制台指令播放。
> 回放**不记录**暂停/加速等游戏外行为；播放时玩家**只允许**做暂停/加速等游戏外操作。
> 必须保证「回放结果」与「实际游玩结果」一致。
>
> 本文结论基于反编译源码 `_Scripts/0.7.0 in developing/`（与游戏 **0.7.0 test-9** 同日导出，2560 个 `.cs`）。
> 文中所有路径均相对该目录。

---

## 0. 结论速览（TL;DR）

1. **游戏逻辑是「固定 30 TPS 的纯 tick 模拟」**，仿真是 `(状态, tick)` 的函数，不读 `deltaTime`。
2. **游戏内所有随机数都源自关卡种子**（自实现 XORShift128，7 条主 RNG + 每实体派生 RNG），
   且 **RNG 状态与整关状态本来就可序列化**（`.lvl` 存档就是这么做的）。
   → **「种子 + 输入」在原理上成立**，并且可以用引擎自带能力做强校验。
3. **玩家对仿真的一切影响只经由一个漏斗**：
   `LogicLevelExt.DoHeldItemPointerEvent(LevelEngine, IHeldItemTarget, PointerInteractionData)`，
   外加**手持物状态变化**（`HeldItemComponent.SetHeldItem / ResetHeldItem / CancelHeldItem`）。
   这个漏斗的参数天然可序列化（格子行列 / 实体 ID / 蓝图槽号 / 草坪区域）。
4. **比「原始鼠标键盘」更省内存与存储的做法 = 语义事件日志**：
   只记录上述漏斗的调用（+少量手持物状态变化），每条约 10~20 字节，
   一局通常几百条 → **几 KB 级**（gzip 后 ~1 KB 级），而原始输入要按帧记录鼠标轨迹（数十 KB/分钟）。
   同时它更**健壮**：记录的是「游戏解析后真正执行的目标」，回放不依赖输入解码/帧率/射线。
5. **一致性用「起始关键帧 + 动作日志 + 周期性指纹校验」保证**，
   必要时在检查点存一份完整 `SerializableLevel` 作为「重同步点」（正常不需要）。
6. 播放时：禁用局内实时输入（只放行暂停/加速/HP 条），按 tick 注入记录的动作。

> **实现状态（2026-10-04）**：录制与**播放都已落地**。上面第 3 点里那个「统一漏斗」
> 在 interop 下**挂不了**（`IHeldItemTarget` 被生成成类，而具体目标是不声明它的值类型代理），
> 实际是挂 5 个调用点；第 6 点的输入拦截也改成了拦 8 个输入入口。
> 这些修正都写在 §4.4 与 §7（已重写为 "已实现" 版本）；实际格式见
> [`Replay/README.md`](README.md#文件格式)（v2）。

---

## 1. 目标与约束

| 项 | 内容 |
| --- | --- |
| 记录范围 | 一局关卡（含关卡开始前的选卡/制品/难度/诅咒的结果） |
| 不记录 | 暂停、加速、慢动作、HP 条显示、控制台开关等「游戏外」行为 |
| 播放 | 指令触发；播放期间只允许暂停/加速 |
| 文件 | `.mrep` |
| 硬约束 | 回放必须与实际游玩**一致** |

---

## 2. 游戏架构关键事实（代码依据）

### 2.1 主循环：固定步长累加器

`MVZ2/Scene/GameUpdater.cs`

```csharp
private void Update() {
    var deltaTime = Time.deltaTime;
    timeModular += deltaTime;
    var fixedInterval = 1 / (float)logicTicksPerSeconds;     // 30 TPS
    if (timeModular > fixedInterval) {
        int updateTimes = Mathf.Min((int)(timeModular / fixedInterval), maxUpdateTimePerFrame); // maxUpdateTimePerFrame=1
        for (int i = 0; i < updateTimes; i++) {
            if (level.Exists()) level.UpdateLogic();          // ← 逻辑推进
            main.UpdateManagerFixed();
        }
        timeModular %= fixedInterval;
    }
    var updateDeltaTime = Mathf.Min(fixedInterval * maxUpdateTimePerFrame, deltaTime);
    if (level.Exists()) { Physics2D.SyncTransforms(); level.UpdateFrame(updateDeltaTime); } // ← 表现 + 输入
}
```

要点：
- **逻辑与表现分离**：`UpdateLogic()` 是逻辑；`UpdateFrame(deltaTime)` 只做动画/UI/输入轮询。
- `UpdateLogic()` → `LevelController.UpdateLogic`（`MVZ2/Level/LevelController/LevelController_Update.cs`）
  → `UpdateLogicRunning()`：按 `GetGameSpeed()` 计算本帧跑几个 `UpdateLogicOnce()`
  （加速 = 一帧跑多个 tick；`gameRunTimeModular` 处理 1.5 倍这类小数）。
- **加速/慢动作只改变「一帧跑几个 tick」，不改变每个 tick 的内容**。
  → 这正是「回放不记录暂停/加速」在架构上成立的原因：回放以 **tick** 为单位，与实时无关。
- `UpdateLogicOnce()`：`level.Update()` → 实体 `UpdateFixed()` → 格子 `UpdateFixed()` → 各 part `UpdateLogic()`。

### 2.2 纯 tick 逻辑 + `levelTime`

- `Engine/Level/Level/LevelEngine.cs` 的 `Update()` **不接收 deltaTime**，末尾 `AddLevelTime()`。
- `Engine/Level/Level/LevelEngine_Progress.cs`：
  ```csharp
  public long GetLevelTime() => levelTime;
  private void AddLevelTime() { levelTime++; }
  ```
  → **`levelTime` 就是天然的逻辑 tick 序号**，可作为所有记录/回放的时间轴。
- `Engine/Level/Timing/Ticks.cs`：`defaultTPS = 30`（`Ticks.SetTPS` 可改，但正常恒 30）。

### 2.3 随机数：全部源自种子

- `Engine/Level/Level/LevelEngine_Random.cs`：
  ```csharp
  public void InitRandom(int seed) {
      Seed = seed == 0 ? Guid.NewGuid().GetHashCode() : seed;
      levelRandom = new RandomGenerator(Seed);
      entityRandom = CreateRNG(); effectRandom = CreateRNG(); roundRandom = CreateRNG();
      spawnRandom = CreateRNG();  conveyorRandom = CreateRNG(); miscRandom = CreateRNG();
  }
  public RandomGenerator CreateRNG() => new RandomGenerator(levelRandom.Next());
  public int NewEntitySeed() => entityRandom.Next();
  ```
- 自实现 `PVZEngine.Tools.Random.XORShift128`（`Engine/Tools/Random/XORShift.cs`）：
  状态就是 4 个 `uint`，`ToSerializable()` → `SerializableRNG{x,y,z,w}`。
- **每个实体自带 RNG**：`Engine/Level/Entities/Entity.cs`
  `public Entity(..., int seed) { RNG = new RandomGenerator(seed); DropRNG = new RandomGenerator(RNG.Next()); }`
  spawn 时传入的 `seed` 来自 `level.NewEntitySeed()`（`LevelEngine_Entities.cs`）。
- **制品 RNG**：`MVZ2/Level/LevelController/LevelController_Gameflow.cs`
  `level.SetArtifactRNG(level.CreateRNG())`（存在 `LogicLevelProps.ARTIFACT_RNG` 属性里，随关卡序列化）。

**结论：关卡内一切随机都可由「种子」复现。** 这是「种子 + 输入」方案的基础。

### 2.4 非确定性来源清单（必须逐个排除/规避）

| 来源 | 位置 | 是否影响仿真 | 处理 |
| --- | --- | --- | --- |
| `Guid.NewGuid()` 造的 `LevelController.rng` | `LevelController_Gameflow.cs:36` | **否**（只选哭泣音效/暂停图：`LevelController_Cry.cs`、`LevelController_Dialogs.cs`） | 忽略或可选改种子 |
| `UnityEngine.Random` | `Logic/Shake.cs`、`Logic/Model/Elements/Light/LightController.cs`、`MVZ2/Metas/SoundMeta.cs`、`CollectBehaviour_Value.cs`、`CrushingWalls.cs` | **否**（抖动/音高/闪光） | 忽略 |
| `UnityEngine.Random` | `Logic/Level/LogicLevelExt.cs` 的 `CreatePreviewEnemies` | **边界**：只影响「预备敌人」的初始位置；`StartGame()` 会 `RemovePreviewEnemies()`；但它会消耗关卡 RNG | 见 4.4「起始关键帧」 |
| `Guid.NewGuid()` 给模型 | `Logic/Model/Model.cs` | **否**（视觉） | 忽略 |
| `DateTime.Now` | `MVZ2Logger.cs`、截图 | **否** | 忽略 |
| Dictionary 迭代顺序 | `LevelEngine.levelObjectReferences` 等 | **理论风险**（同操作序列下稳定，跨机器另说） | 校验点兜底 |
| 浮点运算 | 全局 | 同机同二进制稳定 | 校验点兜底 |

> 注意：源码里**逻辑层**（`Logic/`、`Vanilla/`）几乎不使用 `UnityEngine.Random`；用到的都是表现层。
> 这是本方案可行的重要前提。

### 2.5 整关状态本来就可序列化

- `Engine/Level/Level/LevelEngine_Serialize.cs`：`ToSerializable()` 产出 `SerializableLevel`，
  内含 seed、**7 条 RNG 状态**、属性、格子、种子包、传送带、**所有实体**、能量、进度、波次……
- `MVZ2/Level/LevelController/SerializableLevelController.cs`：整关存盘（含 `rng`、`level`、实体控制器、UI、格子、音频等）。
- 存档路径/格式：`MVZ2/Level/LevelManager.cs` 的 `SaveLevel/LoadLevel`
  （`GetLevelStatePath` → `userdata/user<N>/<mod>/level/<stage>.lvl`，**两行 BSON**：header + content）。
- `Logic/Serialization/SerializeHelper.cs`：**gzip**（魔数 `1F 8B`），`SerializeHelper.init()` 注册了所有 BSON 序列化器。
- 实测 `.lvl` 体积：小关 4~15 KB，大关（Puzzle/Endless 满场）200~275 KB。

→ **回放可以复用这套「序列化 / gzip / BSON」基础设施**，不必自造。

### 2.6 玩家输入的汇聚点（最关键）

玩家对仿真的影响，最终都落到下面这些调用：

**(a) 手持物指针事件 —— 唯一漏斗**

`Logic/Level/LogicLevelExt_HeldItem.cs`：

```csharp
public static void DoHeldItemPointerEvent(this LevelEngine level, IHeldItemTarget target, PointerInteractionData pointerParams)
{
    var data = level.GetHeldItemData();
    var heldItemDef = level.GetHeldItemDefinition();
    if (data == null || heldItemDef == null) return;
    heldItemDef.DoPointerEvent(target, data, pointerParams);   // ← 各 HeldItemBehaviour 在这里执行
    // ... 触发 POST_HELD_ITEM_EVENT 回调
}
```

`IHeldItemTarget` 的四种变体（`Logic/HeldItems/IHeldItemTarget.cs`）**都可紧凑表示**：

| 变体 | 关键字段 | 出现场景 |
| --- | --- | --- |
| `HeldItemTargetGrid` | `LawnGrid Target`（行列）、`LocalPointerPosition`、`ScreenPosition` | 放置/移动器械 |
| `HeldItemTargetEntity` | `Entity Target`（ID）、`LocalPointerPosition`、`ScreenPosition` | 工具对实体使用 |
| `HeldItemTargetBlueprint` | `int Index`、`bool IsConveyor` | 选卡（点卡 / 热键） |
| `HeldItemTargetLawn` | `LawnArea Area` | 草坪区域交互（取消等） |

`PointerInteractionData`（`Logic/Inputs/PointerPhase.cs`）= `{ PointerData{int button,int type}, PointerInteraction interaction }`，
`PointerInteraction` 是枚举（`Hover/Enter/Stay/Exit/Down/Hold/Up/Click/BeginDrag/Drag/EndDrag/Drop/Streak/Release/Key`）。

调用来源（`MVZ2/` 侧）：
- 格子：`LevelController_Grids.cs` 的 `UI_OnGridPointerInteractionCallback(lane,column,eventData,interaction)`
  → 由 `GridView.holdStreakHandler` / `GridController` / `LaneController` / `GridLayoutController` 事件链上来。
- 草坪：`LevelController_UI.cs` 的 `UI_OnRaycastReceiverPointerInteractionCallback(LawnArea,...)`。
- 实体：`EntityController` 的 pointer 事件 → `HeldItemTargetEntity`。
- 蓝图热键：`LevelController_Input.cs` 的 `OnBlueprintKey(i, conveyor, key)` → **也走该漏斗**。
- 蓝图点击：`BlueprintController/BlueprintArray/Conveyor` → `Blueprint` 的 `OnPointerInteraction`。

**(b) 手持物状态变化（漏斗之外）**

`MVZ2/Level/HeldItems/HeldItemComponent.cs`：`SetHeldItem(IHeldItemBuilder)` / `ResetHeldItem()` / `CancelHeldItem()`。

- 有些调用**经过漏斗**（选中一张卡 → `SelectBlueprintHeldItemBehaviour` 里 `SetHeldItem`）。
- 有些**绕过漏斗**：`LevelController_Tools.cs` 的 `ClickPickaxe/ClickStarshard/ClickTrigger`
  直接 `level.SetHeldItem(LogicHeldTypes.pickaxe)`。
→ 记录时需要一个 **reentrancy 标志** 区分「漏斗内的 SetHeldItem」和「独立的 SetHeldItem」，避免重复记录。

**(c) 连续交互（Hold/Streak）——按 tick 触发，确定**

- `GridView.UpdateFixed()` / `EntityController` 的 `UpdateFixed()` 会调
  `LevelPointerInteractionHandler.UpdateHoldAndStreak()`（`MVZ2/Level/Input/LevelPointerInteractionHandler.cs`）。
- `UpdateFixed` 属于 **`UpdateLogicOnce()` 的 tick 循环**（见 2.1），
  → `Hold`/`Streak` 是**每 tick 触发一次**，与帧率无关（只要「按住」状态一致）。
- 使用 `Hold`/`Streak` 的行为：`PickupHeldItemBehaviour`（拾取）、
  `TriggerCartHeldItemBehaviour`（按住给推车蓄力——**按住 tick 数影响结果**）、
  `EmptyHandEntityHeldItemBehaviour`（空手长按）。
  → 回放必须复现「按住的目标与起止 tick」。

**(d) 需要排除的「游戏外」输入**

`LevelController_Input.cs` 的 `UpdateKeys()`：
- `Space` → 暂停（**不记录**）
- `Escape` → 选项（**不记录**）
- `HotKeys.fastForward` → 加速（**不记录**）
- `HotKeys.hpBars` → 血条（**不记录**，纯显示）
- 其余（蓝图热键、镐子/星之碎片/触发器热键）→ **记录**（它们改变仿真/手持物）。

### 2.7 关卡起始流程与「选卡结果」

- `LevelManager.InitLevel(areaID, stageID)`（`MVZ2/Level/LevelManager.cs`）：
  有存档则 `LoadLevel`，否则 `controller.InitLevel(Main.Game, areaID, stageID)`（**seed 参数缺省 0 → 随机**）。

> ⚠️ **有存档时，`LoadLevel` 可能「什么都不做而且不报错」。**
> 它先 `controller.ValidateGameStateHeader(header)`，比对的是存档首行的
> `identifiers`（= 每个 mod 的 `Namespace` + `LevelDataVersion`，见
> `LevelManager.GetLevelStateIdentifierList()`）与当前实际加载的 mod 集合；
> 不等就 `ShowLevelMismatchLoadingDialog` 然后直接 `return` —— **引擎永远不会被建出来**，
> 日志里一行错都没有。外面看到的症状就是 `ctrl != null && engine == null` 一直挂着。
> 重建过 mod、换过 mod 组合之后，旧关卡存档就会变成这样（只能删掉那个 `.lvl`）。
> 自测里的 `[SelfTest] DeleteLevelState` 就是为这种情况准备的。

- `LevelController_Gameflow.cs`：`InitLevel(game, area, stage, seed)` → `level.Init(area, stage, seed)` →
  `StartLevelIntro(delay)`（**用 `CoroutineManager.DelaySeconds` 的实时延迟 + 开场对话**）→ `level.BeginLevel()` → `StartGame()`。
- `StartGame()`（同文件）：
  ```csharp
  BlueprintChoosePart.ApplyChoose();   // 选卡 → 蓝图
  UpdateDifficulty();
  ... 各 part PostLevelStart；level.Start();
  ```
- `LevelBlueprintChooseController.ApplyChoose()`（`MVZ2/Level/ControllerPart/LevelBlueprintChooseController.cs`）：
  ```csharp
  Level.SetupBattleBlueprints(chosenBlueprintControllers.Select(e => e.ToChooseItem()).ToArray());
  Level.ReplaceArtifacts(chosenArtifacts.Select(i => i?.id).ToArray());
  ApplyCurses();   // 逐条 Level.AddBuff(buffId)
  ```
  → **选卡结果 = 每个槽的 `BlueprintChooseItem{ id, isCommandBlock, innate }` + 制品 ID 列表 + 启用的诅咒 ID 集合**。

**结论：关卡开始前玩家的选择不是种子能推出来的，必须单独记录。**

### 2.8 test-9 interop 实测修正（与上面反编译源码的差异）

生成 test-9 的 interop 后用 `tools/dump-interop-signature.ps1` 核对，发现三处关键差异
（**反编译镜像 `_Scripts/0.7.0 in developing` 与 test-9 二进制并不完全一致**）：

1. **输入参数被统一成 `MVZ2.Level.Inputing.PointerInteractionArgs`**（一个含引用字段的结构体，
   Il2CppInterop 因此把它生成成 `Il2CppSystem.ValueType` 的派生**类**）：
   `int pointerId` / `PointerInteraction interaction` / `Vector2 position` /
   `PointerEventData.InputButton button` / `Vector2 delta` / `RaycastResult pointerCurrentRaycast` /
   `PointerEventData eventData`。
   → 5 个调用点的签名相应变成 `(int lane, int column, PointerInteractionArgs args)` 之类。
2. **`IHeldItemTarget` 被生成成类，具体目标（`HeldItemTargetGrid` 等）是值类型类且不声明该接口**，
   于是「在漏斗里判断 target 的具体类型」恒为假 → 必须挂 5 个调用点（见 2.6）。
3. **`HeldItemComponent` 在 `Plugin.Load()` 阶段碰不得**：它的静态字段
   `componentID = new NamespaceID(Global.BuiltinNamespace, "heldItem")` 依赖游戏初始化，
   提前触碰会 native 崩溃，且 `TypeInitializationException` 会被运行时**永久缓存**。
   → 工具路径改挂 `LevelController.ClickPickaxe / ClickStarshard / ClickTrigger`。

教训：**改测试版之前先用 `dump-interop-signature.ps1` 把要 patch 的方法列出来**，
比照源码猜签名快得多（本仓库已经踩过两次）。

---

## 3. 方案对比

| 方案 | 记录内容 | 体积（一局） | 一致性 | 实现难度 | 备注 |
| --- | --- | --- | --- | --- | --- |
| **A. 原始输入回放** | 每帧鼠标位置/按键 + 种子 | 数十 KB/分钟 | 受帧率/输入轮询时序影响，脆弱 | 高（要拦 Unity `Input` 与 EventSystem） | 不推荐 |
| **B. 语义事件回放（推荐）** | 起始状态 + 漏斗事件 + 手持物变化 | **几 KB**（gzip ~1 KB） | 强（记录解析后的目标） | 中 | 本文推荐 |
| **C. 全量快照回放** | 每 N tick 一份 `SerializableLevelController` | 每秒 5~275 KB | 无需确定性 | 低（复用存档） | 体积爆炸 |
| **B+ 校验增强** | B + 周期性状态指纹（+可选重同步关键帧） | B + 每检查点 ~40 B | 强 + 可验证/可恢复 | 中 | 推荐 |

> 「比种子+原始输入更省」的结论来自 **B**：把「输入」从**每帧的原始鼠标/键盘**压缩成
> **每次真正生效的语义动作**。放置一个器械 = 一条约 12 字节的记录，
> 而不是「第 137 帧鼠标 (x,y) 左键按下、第 141 帧抬起」。

---

## 4. 推荐方案（B+）

### 4.1 `.mrep` 内容

```
[起始状态]  关卡启动所需的完整信息（三选一，见 4.2）
[动作日志]  按 tick 排序的语义动作流
[校验点]    每 N tick 一条状态指纹（可选，但强烈建议）
[尾信息]    结束原因（通关/失败/中止）、tick 总数
```

### 4.2 起始状态：两种取法

**取法 1（推荐，最稳）：完整起始关键帧**
- 在「开始记录」的那一刻抓一份 `SerializableLevelController`（复用 `LevelController.SaveGame()`），
  gzip 存进 `.mrep`。
- 播放时用与存档加载相同的路径恢复（`LevelController.LoadGame`），再喂动作。
- 优点：**消除全部「起始确定性」隐患**（预敌人消耗 RNG、开场对话时序、选卡应用顺序等一律不用管）。
- 体积：关卡早期实体很少，实测**几 KB~数十 KB**（越小越接近「刚 StartGame」）。

**取法 2（更小，需自证确定性）：种子 + 配置描述块**
- 记录：`area` / `stage` / `difficulty` / `seed` / 选卡结果 / 制品 ID / 诅咒 ID / 星之碎片槽数。
- 播放时 `InitLevel` 传入记录的 seed，并在 `StartGame` 前注入选卡/制品/诅咒，再 `StartGame`。
- 优点：几百字节。
- 风险：需要断言「同 seed + 同注入 ⇒ 起始状态完全一致」，用 4.4 的**起始指纹**校验。

> 建议：**默认用取法 1**；等实现稳定、且起始指纹长期零误差后，再提供「压缩模式」用取法 2。

### 4.3 记录挂钩点（Harmony）

| 目的 | 目标方法 | 类型 | 记录内容 |
| --- | --- | --- | --- |
| 动作漏斗 | `LogicLevelExt.DoHeldItemPointerEvent(LevelEngine, IHeldItemTarget, PointerInteractionData)` | Postfix | tick、target 描述、pointerParams |
| 手持物变化（独立于漏斗的） | `HeldItemComponent.SetHeldItem / ResetHeldItem / CancelHeldItem` | Postfix | 仅当**不在**漏斗内（reentrancy 标志为假） |
| 起始状态 | `LevelController.StartGame()` 或 `LevelEngine.Update()` 首个 tick | Postfix | 抓关键帧/起始描述块 |
| 结束 | `LevelController.Dispose / OnEngineClear / OnEngineGameOver` | Postfix | 收尾、写文件 |

**过滤策略（控制体积）**：
- `DoHeldItemPointerEvent` 每帧都会因 hover 被调用（`Hover/Enter/Stay/Exit`）。
  在桌面端，蓝图的放置判定是 `IsInvalidReleaseAction`（`Logic/Inputs/PointerPhase.cs`）：
  **鼠标只认 `Down`，触摸只认 `Release`**；`IsInvalidClickButton` 只认左键。
  但 **`Hold`/`Streak` 会影响拾取/推车蓄力**，必须保留。
- 因此**建议记录集合** = `{Down, Up, Click, Release, Key, Drop, EndDrag, Hold, Streak}`（丢弃 `Hover/Enter/Stay/Exit`）。
- 对连续的 `Hold/Streak` 做 **RLE**：只存「目标 + 起始 tick + 持续 tick 数」，回放时按 tick 展开。
- 可选再加一层**指纹过滤**：对一个互动量很小的原语（如「当前实体数 + currentEntityID + 能量 + 手持物类型」）
  取前后指纹，没变化就丢弃该事件——可进一步砍掉无副作用的调用。

### 4.4 回放挂钩点（**已按此实现**，代码见 `ReplayPlayer.cs` / `ReplayPatches.cs`）

| 目的 | 最终做法 | 与初稿的差异 |
| --- | --- | --- |
| 重建起始状态 | 重新加载一次关卡场景（`MainSceneController.GotoLevelSceneAsync`，private 反射调，**不 await**）→ 轮询 `LevelManager.GetLevelController()`，**必须等到控制器换了实例**（异步：await 完场景加载才会 `SetLevelController`；只看「非 null」会拿到旧控制器）→ 再等 3 帧 → `ctrl.SetActive(true)` + `SetStartStage` → `ctrl.LoadGame(FromBson<SerializableLevelController>(关键帧), Main.Game, area, stage)` → `ResumeFromLoadedDialog(ctrl)` 解除暂停 | 不走 `InitLevel`（那条路会发现 `.lvl` 关卡存档并走 `LoadLevel`，从**上一次玩到一半的状态**续关） |

> ⚠️ **`LoadGame` 只能打在「刚 Awake 出来、还没 `InitLevel` 过」的控制器上。**
> `builtinCollisionSystem` 是控制器字段、由 `level.Init()` 填满；打在已经 Init 过的控制器上时
> `CreateFromSerializable → ReadCollisionFromSerializable` 会 `entities.Add(id)` 撞键：
>
> ```
> ArgumentException: An item with the same key has already been added.
>   Key: [7, PVZEngine.Collisions.BuiltinCollisionEntity]
> ```
>
> → `LoadGame` 返回 false。**游戏自己不会踩这个坑**：`LevelManager.InitLevel` 在有 `.lvl` 存档时
> 只走 `LoadLevel`，根本不建新关卡。
>
> ⚠️ 这类异常 **`Debug.LogException` 只写 Unity 的 `Player.log`**
> （`%USERPROFILE%\AppData\LocalLow\Cuerzor\MinecraftVSZombies2Test\Player.log`，带 PDB 行号），
> **不写 `BepInEx\LogOutput.log`，也不写 `ErrorLog.log`（那个文件是 0 字节）**。
> 所以「BepInEx 日志里没异常」不等于没抛异常。
| 禁用局内实时输入 | **不碰 `UpdateInput`**：在 5 个指针回调 + `OnBlueprintKey` + `ClickPickaxe/ClickStarshard/ClickTrigger` 这 8 个入口挂**前缀 `return false`** | 比拦 `UpdateInput` 更精确：键盘热键最终也落到这 8 个方法上，一处拦就全拦住了；暂停 / 加速 / 血条 / Esc 完全不经过它们，天然放行 |
| 按 tick 注入动作 | `LevelEngine.Update()` **前缀**：`while (events[cursor].tick <= level.GetLevelTime())` 派发 | 与初稿一致 |
| 重建 target / 派发 | **反射调用游戏自己的那 5 个私有回调 + 3 个工具点击**，参数 `PointerInteractionArgs` 全部字段填满 | 初稿打算自己 `new HeldItemTargetGrid(...)` 再调 `DoHeldItemPointerEvent` —— **做不到**：interop 把 `HeldItemTarget*` 生成成不声明 `IHeldItemTarget` 的值类型代理，没法装箱成那个接口 |
| 指针位置 | v2 起记录 `sx/sy`（屏幕）+ `wx/wy/wz`（射线世界坐标），回放时同时填 `args.position` / `args.pointerCurrentRaycast` / `args.eventData` | 初稿漏了这一步；`LocalPointerPosition` 真的会影响仿真（`ToEntityHeldItemBehaviour` 用它挑目标实体） |
| 实时鼠标位置 | 另挂 `MVZ2Logic.Inputs.InputHelper.GetPointerPosition(int,int)` / `(int)` / `TryGetPointerPosition` 前缀，报告回放位置 | 少数行为（剑的砍击）不看回调参数而是 `Global.Input.GetPointerScreenPosition()` |
| REC 标签 | 克隆 `LevelUIPreset.difficultyText`（`DSHCore.Ui.UiClone`），挪到难度文字正下方 | 见 §7 |

> ⚠️ **注入必须走 Harmony 前缀可达的方法**。Harmony 对 interop 代理类型的补丁是打在
> **原生代码**上的，所以 `il2cpp_runtime_invoke`（= `MethodInfo.Invoke`）进来的调用
> **一样会经过前缀** —— 这既是「能拦住玩家输入」的原因，也是「必须用一个 re-entrancy
> 开关区分『玩家点的』和『我们注入的』」的原因。

> **顺序细节**：实际游玩中 `GameUpdater.Update()` 先跑逻辑 tick、再 `UpdateFrame`（输入在其内发生）。
> 所以某次点击发生在「tick N 之后、tick N+1 之前」，记录时 `levelTime == N`。
> 回放在 `LevelEngine.Update` 前缀里对 `levelTime == N` 派发，时序完全一致。

### 4.4.1 鼠标输入有**两条**下游通路，只重放一条会漏掉一整类效果

这是 2026-10-04 实测定位到的**架构性缺口**（症状：「第一个攻击性器械没放下来」）。

| | 通路 | 走法 | 典型受影响者 |
| --- | --- | --- | --- |
| ① | **路由事件** | Unity EventSystem → `GridController` / `EntityController` / `LawnRaycastReceiver` / `RuntimeBlueprintController` → `LevelController` 的 5 个 `UI_On*PointerInteractionCallback` → `level.DoHeldItemPointerEvent` | 放器械、选卡、工具、拾取 |
| ② | **全局回调** | `InputManager.UpdatePointer()` 每帧轮询真实鼠标 3 个键 → `FlushPointerCaches()` → `Main.Game.RunCallbackFiltered(LogicCallbacks.POST_POINTER_ACTION, param, param.phase)` | **右键取消手持物**（`RightMouseCancelHeldItemBehaviour`）、`CrushingWalls` / `BreakoutBoard` 这类「按下鼠标就有效果」的关卡内容 |

初版只做了 ①（`ReplayPatches` 的 5 个前缀后缀），②**完全没有覆盖** —— 而且因为
回放把玩家输入拦掉了，②甚至连「玩家偶然按到」都不会有。

**实测的连锁反应**（`mountain_10`，tick 556~680）：

```
录制：556 选卡9 → 591 右键取消 → 662 再选卡9 → 680 放下去 ✅
回放：556 选卡9 → 591 右键无效 → 662 变成「取消」 → 680 空手放不下 ❌
```

真正的机制是 `SelectBlueprintHeldItemBehaviour.PickupBlueprint`：

```csharp
if (!pickData.IsEnchanted() && level.IsHoldingExclusiveItem())
{ level.CancelHeldItem(); return; }        // 点一张「已手持」的卡 = 先取消，然后 return
```

少了一次右键取消，662 那次点击就从「选卡」变成了「取消手持物」。

**修法（`Replay/ReplayPointerAction.cs`）**：反射调
`Main.Game.RunCallbackFiltered(LogicCallbacks.POST_POINTER_ACTION, param, param.phase)`，
与 `InputManager` 完全同一条路径 —— 不自己重写取消逻辑，游戏改行为我们自动跟着变。
映射：`ia=4/5/6`（Down/Hold/Up）→ `PointerPhase.Press/Hold/Release`；`type/button`
用 `InputHelper.GetPointerType/GetPointerButton(pointerId)` 反推。

> **教训**：判断「输入是否录全了」不能只看「我补了几个入口」，而要顺着**数据的下游**走一遍。
> 这里的破绽是：`POST_POINTER_ACTION` 的触发点在输入层、不在 `LevelController` 里，
> 所以「把 `LevelController` 的指针回调全补上」看起来完备，实际漏了一半。
>
> **映射细节**：`Down→Press`、`Hold/Streak/Drag→Hold`、`Up→Release`、
> `Stay/Enter/Exit→None`。最后那一组是必须的：原件里输入层**每帧**都为未按下的键发
> `None`，而 `BreakoutBoard`（打砖块）不筛相位、鼠标模式下每帧用 `screenPos` 移动挡板。

### 4.4.2 实体事件的「锚点」语义 —— 为什么不能解析失败就丢

`UI_OnEntityPointerInteractionCallback` 收到的实体**不一定是被作用的那个**。
`ToEntityHeldItemBehaviour.OnPointerEventEntity` 只把它当命中测试的起点：

```csharp
var targetEntity = entity.FindPointerTargetEntity(target.LocalPointerPosition.y,
                                                  target.ScreenPosition, CanUseOnEntity);
entity.Level.ResetHeldItem();
if (targetEntity != null) UseOnEntity(targetEntity);
```

实测（`mvz2_mountain_10`，tick 2899~2910）：录下的事件挂在鼠标底下的 `mvz2:starshard` 上，
真正被镐子挖掉的是旁边的 `mvz2:giant_bowl`。所以「按同定义 + 坐标」这级会失败
（场上根本没有别的 starshard），而**丢掉整条事件就等于丢掉那次挖除** ——
症状是「用了镐子但大碗还在」，随后能量、波次全线对不上。

⇒ `ResolveEntity` 增加第 3 级：**离记录坐标最近的任意实体**当锚点，让游戏自己再从锚点
就近选目标。这比「什么都不做」更接近录制，且会在小结里单独计数。

> 更彻底的做法是录制时就把**游戏自己解析出的目标实体**记下来，但那需要按行为类型分别处理
> （每个 behaviour 的 `CanUseOnEntity` 不同），代价远大于收益。

**2026-10-04 补：光有「定义 + 坐标」还不够 —— 同一格里可以有 4 个一样的东西。**

镐子「挖错株」的根因是**同格多器械**：一个格子最多能同时站 4 个可被镐子挖掉的东西
（`承载位 carrier` / `保护位 protector` / `主要位 main` / `工具位 tool`），
它们**坐标完全相同**（`AreaMeta.GridWidth` 一个格子只有 80 草坪单位），
于是「同定义 + 离记录坐标最近」在它们之间**根本分不出是哪一个**。

而游戏的目标判定**从锚点身份出发**（`VanillaEntityExt.FindPointerTargetEntity`）：

```csharp
var protectTargets = entity.GetProtectingTargets();   // ← 只有锚点是「保护位」才有非空结果
...
if (firstValidMainTarget != null && (!canUseOnProtector || pointerPositionY >= 0.5f))
    return firstValidMainTarget;
return entity;                                        // 否则返回锚点自己
```

锚点从「保护位」变成「主要位」，`GetProtectingTargets()` 就直接是空数组 → 挖到另一株。

> ⚠️ **电脑端与移动端的判定不同，本 mod 只做电脑端。**
> - **电脑端（鼠标）**：`LevelRaycaster` 的 `castRadius <= 0` → **精确射线**
>   （`Physics2D.GetRayIntersectionAll`），锚点 = 光标那个像素落在谁的碰撞体上；
>   且 `ToEntityHeldItemBehaviour.IsValidFor` 只在 `pointer.type == MOUSE` 时接受
>   `HeldItemTargetEntity` —— **电脑端走实体路径**。鼠标有像素级精度，所以同格三层俱全时
>   **蹭着保护位露在外面的那条边**也能点中它。
> - **移动端（触摸）**：`SetHeldItem(..., radius)` 给的是**胖手指圆投射**
>   （`Physics2D.CircleCastAll`），锚点退化成**格子**，走
>   `VanillaGridExt.FindPointerTargetEntity(grid, pointerPosition, ...)`——
>   那条路径才是"指针高度 < 0.5 选保护位、否则选主要位"，并把 `rangeMin/rangeMax`
>   交给高亮（只亮可用的半格）。
>
> ⇒ **不要用"上下半格"解释电脑端现象**。电脑端要还原的只有两件事：
> 当时点中的是哪一个实体（本 mod 用 定义 + 分层 + 坐标 定位）、当时的指针位置
> （`wx/wy/wz`，供 `LocalPointerPosition.y` 与那几个直接问 `Global.Input` 的行为使用）。

⇒ 事件里再记一维 **`lyr`（网格分层键）**，解析顺序变成
**① ID + 定义 + 分层 → ② 定义 + 分层 → ③ 定义 → ④ 记录坐标附近的任意实体**。

- 分层键由 `ReplayRecorder.LayerKeyOf` 生成：拿 `PointToGrid(位置)` 得到格子，
  再依次问 `VanillaGridLayers` 的 `carrier/protector/main/tool`，
  **一个实体可以同时在多层**，所以键是**有序拼接**（实测见过 `carrier,main`）。
- 非网格实体（僵尸、拾取物、特效）返回 `""`；旧文件没有这个字段则视为 `null` = **不做判别**。
- 各级命中次数进播放小结：`按定义+分层找回` / `按定义找回` / `改用记录坐标当锚点`。

> ⚠️ 因此**旧录像（ver ≤ 5）享受不到这一维** —— 它们只能走 ③/④。
> 要验证镐子场景需要一条**新录的**回放。

### 4.4.3 上游的「赞助者名单」歪种（阻塞端到端验证）

`SponsorsGlobalCallbacks` 在「显示赞助者名字」打开时，会在放下熔炉 / 月光传感器 /
火药桶时 `furnace.Spawn(VanillaEffectID.floatingText, …)`，名字取自
`Global.Sponsors.GetSponsorPlanNames(…)` —— **来自外部而非关卡种子**。
两次会话拿到的名字数量可能不同 ⇒ `floating_text` 生成与否不同 ⇒ 实体 ID 开局整体错位。

实测：首次指纹不一致稳定出现在 **tick 30**（第一个熔炉放下处），内容是少一个
`mvz2:floating_text`，且 `entity` 流 RNG 已不同。

⇒ 作者已在修；**在那之前，要验证回放一致性必须先关掉「显示赞助者名字」。**

### 4.4.4 播放时的「假鼠标」：为什么只能走视觉通道

**问题**：录下来的只有动作本身，**没有任何鼠标轨迹**；而 `bpkey`（数字键选卡）与
`tool`（镐子/星之碎片/触发器）这两类事件**根本没有坐标**（`ReplayRecorder` 那两条
不调 `AppendPos`），悬浮类事件又被当噪音整个丢掉。于是 `pointerScreen` 只在
「带坐标的事件」派发的那一瞬间才变 —— 手持物停在原地、下一次点击时瞬移过去。

**解法**：把离散路点在 **tick 空间**上插值成一条连续轨迹
（暂停时自动冻住、加速时自动变快，都不用额外处理），行程压缩在
「上一个路点 → 下一个路点」这段区间的最后 N 个 tick 里，缓动到站。

**硬约束 —— 只换视觉，不换逻辑**。指针位置有 7 个消费者，其中 3 个**会改仿真**：

| 消费者 | 性质 |
| --- | --- |
| `LevelController.UpdateHeldItemPosition` | 纯表现（手持物世界坐标） |
| `EntityController.IsHPBarHovered` | 纯表现（血条悬停） |
| `MinecartRideable.Update` | **矿车整车位置每 tick 由指针决定** |
| `Snipenser.UpdateAI` | **朝向 → `ShootProjectile` 的速度向量** |
| `BreakoutBoard` | **走 `POST_POINTER_ACTION` 的 `screenPos`** |
| `CombatHeldItemBehaviour` / `SwordHeldItemBehaviour` | 在事件体内读，用于挥砍/拖拽方向 |

⇒ 所以**绝不能**把平滑值写进 `ReplayPlayer.pointerScreen`。做法是给
`LevelController.UpdateHeldItemPosition` 挂上「前缀换值 + 后缀立刻还原」，
让这个方法内部看到假鼠标的位置（游戏自己会 `ScreenToWorldPoint` +
`SetHeldItemPosition`），方法之外的一切代码看到的仍是事件原值。

**另外还要画一个光标**：手持物品时物品模型**就是**指针（`HeldItemCursorSource` 把系统光标
设成 `CursorType.Empty`），但什么都没拿的时候游戏不画任何指针 —— 那时光标是系统光标，
停在玩家自己的鼠标位置上。所以额外克隆一张**游戏原生的光标贴图**
（`CursorManager.cursorDatas` 里 `CursorType.Arrow` 那张，就是游戏给硬件光标用的贴图）
画在假鼠标的位置上；同时把系统光标强制保持成系统默认样式，两者才好区分。

### 4.4.5 回放收尾：为什么必须拦掉原版的通关 / 失败流程

回放重演的是**一局已经打完的关卡**，所以它会再次「通关」或「失败」。这两条路原版都会
去动**玩家存档**，直接放过去是真事故：

| 原版行为 | 为什么不能放 |
| --- | --- |
| `LevelController.OnEngineClearCallback` → `RemoveLevelState()` | **删掉该关卡的存档文件**（`level/*.lvl`）。看个回放就把自己没打完的进度删了 |
| 同上 → 解锁关卡 + 写难度记录 | 玩家可能根本没通过这一关 / 没打过这个难度 |
| 同上 → 通关对话 + `StartExitLevelTransition` 退场动画 | 和我们的结束面板并排出现，而且落点还会跑去地图 |
| `ShowGameOverDialog()` | 同样会和结束面板重复 |

所以回放期间这两个方法**前缀拦掉**，改由 `ReplayPlayer.NotifyLevelCleared()` /
`NotifyGameOver()` 走 `FinishPlayback(reason)`（弹结束面板）。

> ⚠️ **没拦住的部分（已知）：** 通关时 `LevelEngine.Clear()` 还会自己跑
> `Triggers.RunCallback(POST_LEVEL_CLEAR, …)`，全局统计 / 金钱那一批回调**照旧会执行**。
> 那是在引擎层，不在我们能干净拦下的位置。

**落点**：`ExitLevel()` 按 `LevelExitTarget` 决定去哪，默认 0 = `Scene.GotoMapOrMainmenu()`。
「结束播放」「退出」要回的是**回放页**，所以拦 `MainSceneController.GotoMapOrMainmenu`，
在 `ReplayPlayer.WantsReplayPage` 为真时改成 `DisplayAddons(null)`（插件页），
再由回放页自己的 `RequestOpen()` / `TryOpenPending()` 把面板打开。

### 4.4.6 回放里的关卡菜单：文案挂钩点的选择

「重新开始 → 重新播放」「返回地图 → 结束播放」只改**文案 + 点击行为**两处：

- **文案**：挂 `OptionsDialogController.UpdateRestartButton` / `UpdateLeaveLevelButton`
  **后缀**。原版每次刷新菜单（切页 / 改语言 / 重新打开）都会先跑这两个方法，
  跟在后面覆盖就不会出现「先对后错」。比在 `ShowOptionsDialog` 里写一次稳。
- **行为**：前缀拦 `OnMainPageButtonClickCallback(ButtonType)`，把那两个枚举值换成
  我们自己的确认框 + `RestartCurrent()` / `FinishFromMenu()`。

#### 4.4.6.1 两个踩过的坑（都有自测）

**① 菜单的「重新播放」点了没反应。** 原来回调里直接调 `ReplayPlayer.Start(name)`，
而 `Start()` 开头第一句是

```csharp
if (IsActive) return "[replay] 已经在播放了…";
```

回放**播到一半**时 `phase == Playing` ⇒ `IsActive` 为真 ⇒ 这条 `return` 直接命中，
函数返回一句提示字符串（没人读），菜单按钮就此静默失效。
修法：`RestartCurrent()` 里**先 `Stop("restart")` 再 `Start(name)`** ——
先收尾（停 HUD、收假鼠标、卸钩子、清 `bound`），把 `IsActive` 落到 false，再重开。

**② 菜单的「结束播放」应该弹结束面板，而不是直接退关卡。** 原来它走
`EndToReplayPage()`（结束 + 回放页），于是「播到一半想结束」和「播完了」是两套完全不同的收尾。
现在统一成 `FinishFromMenu()` → `FinishPlayback("manual-end")`：
和「自然播完 / 通关 / 失败」走**同一条收尾路径**，弹出同一个结束面板，
由面板上的「退出」再去 `EndToReplayPage()`。面板里能导出、重播，比直接退出去合理。

> ⛔ 两个回调都必须**先 `CloseLevelOptionsMenu()` 再动手**。真正把菜单框体收起来的是
> `LevelController.ResumeGame` 里的 `ui.SetOptionsDialogActive(false)` ——
> 光调 `optionsDialogController.Close()` 只清 context、**不收框体**，
> 结果是「结束面板叠在还开着的菜单上面」（玩家能看见底下那半透明的菜单）。
> 也不能指望 `Close()` 触发的那条 `ResumeGameDelayed` 协程：它要下一帧才跑，
> 那时结束面板已经弹出来了，会被 `ResumeGame` 第一段的
> `if (Main.Scene.HasDialog()) return false;` 挡掉，菜单就永远留在屏幕上了。

> ⛔ **必须用「关卡自己的那个」控制器。** 场景里主菜单（`Mainmenu/Canvas/OptionsDialog`）
> 和地图（`Map/Canvas/OptionsDialog`）各有一套同类型的 `OptionsDialogController`，
> `UiFinder.FindOne<OptionsDialogController>()` 会挑到它们；它们的 `ui.Main` 挂在
> 未激活节点下，写进去的文案**永远看不见，而且不报任何错**。
> 唯一正确来源是 `LevelController.optionsDialogController`（放在 Harmony 参数里当
> `__instance` 传进来最省事）。

### 4.4.7 收尾落点与「回放页」的边界（以及那个黑屏 bug）

「结束播放 / 退出」的落点挂在 `MainSceneController.GotoMapOrMainmenu` 上（游戏里唯一
「打完一局去哪」的决策点，改它就不用往 `ExitLevel` 的清理流程里插脚）。

**曾经的写法**：`DisplayAddons(null)` + 挂一个「待会儿开面板」的单，等 `Tick()` 下一轮
（≥20 帧）满足条件再开。两个问题：

1. **会闪一帧插件页** —— `DisplayAddons` 是同步切页（`DisplayPage` 立刻
   `SetActive`），而面板要等关卡场景卸完 + 20 帧节流。玩家看到的是
   「先跳出插件页的资源包/语言按钮，再变成回放页」。
   修法：在**同一帧**里 `DisplayAddons(...)` → `ReplayPage.PumpNow()`（内部把
   `Tick()` 的节流绕过去，也跳过「关卡已卸完」那条检查 —— 面板挂在主场景的
   `GlobalUICanvas/Dialogs`，关卡是**另加载的场景**，卸它动不到我们）。
2. **点插件的「返回」必黑屏** —— 传的 `onReturn` 是 `null`，而
   `MainScenePage.Return()` 是 `OnReturnClick?.Invoke(); Hide();`：回调查空、
   页面照样 `SetActive(false)`，于是**全灭且没有东西顶上来**。
   修法：把 `onReturn` 接到 `DisplayMainmenuToBasement`，并给 `ReplayPage` 一个
   「关掉面板之后去哪」（`SetReturnTo`）—— 从关卡直接落过来的那次，关面板就直接回主菜单，
   而不是把插件页的索引亮出来（玩家压根没经过那一页）。
   正常从插件页点「回放」进入时 `SetReturnTo(null)`，行为不变。

### 4.4.8 导入 / 导出走原生文件对话框（以及一个 interop 陷阱）

「切换存档 → 导入 / 导出」用的就是 `StandaloneFileBrowser`（游戏层的薄封装是
`MVZ2.IO.FileHelper`）。所以这里也用它：

| 动作 | 调用 |
| --- | --- |
| 导出（另存为）| `SFB.StandaloneFileBrowser.SaveFilePanel(title, dir, defaultName, "mrep")` → 返回路径 |
| 导入（打开）| `SFB.StandaloneFileBrowser.OpenFilePanel(title, dir, "mrep", false)` → 返回 `Il2CppStringArray` |

> ⚠️ **不要直接用 `FileHelper.SaveExternalFile` / `OpenExternalFile`**：它们是
> `async Task<string>`，在 interop 里变成 `Il2CppSystem.Threading.Tasks.Task<string>`
> —— 这个类型**不派生自** `System.Threading.Tasks.Task`（base 是 `Il2CppSystem.Object`），
> 既没有 `GetAwaiter()` 也没有 `ContinueWith()`，**await 不了、`.Result` 也没有**。
> 而 Windows 下这些包装本来就只是「同步调 SFB + 回调」，直接调 SFB 既等价又同步。
> （签名用 `tools/dump-interop-signature.ps1` 核过。）

**导出结果不另弹提示框**：游戏的 `CustomDialog` 是**单实例复用 + 排队显示**的
（`MainSceneUI.dialog` 就一个），链式弹窗（面板 → 提示 → 再弹回面板）会踩到时序 ——
实测出现「面板的逻辑已经换到第三个了、屏幕上挂着的还是第一个」。
现在整条链上始终**只有一个**对话框：导出结果直接拼进面板正文的最后一行再重弹同一个面板。

### 4.4.9 假鼠标的尺寸，以及为什么去掉了「接管系统光标」

- **尺寸**：`[Visual] FakeMouseScale`。画布是「屏幕空间-相机」，`sizeDelta` 最终要写
  `贴图尺寸 × (canvasRect.width / Screen.width) × scale`（前一段是 UI 单位↔像素换算，
  后一段是给玩家的手感微调）。
- **去掉了 `FakeMouseSystemCursor`**：那套代码是在 `CursorManager.UpdateCursor` 后面
  强制 `Cursor.visible = true` + `SetCursor(null, ...)`，把游戏的光标决定当场撤销。
  实际用起来没有价值（假鼠标是**画在画布上**的，和硬件光标互不影响），
  却引入了「每 tick 兜底检查 + 退出时还原」这一堆状态。现在整段删掉，
  回放期间系统光标完全交给游戏自己管。

### 4.4.10 「关卡里自动写出回放」为什么必须是可关的（默认关）

录制过程中 `ReplayRecorder.NotifyTick` 每 tick 都会被调。它原来会在 `tick` 走过
`AutoSaveTicks = 600`（= 20 秒）的整数倍时无条件 `Write("autosave")` ——
**每 20 秒往回放目录落一次盘**。两个后果：

1. 玩家只是正常玩关卡，回放目录里就不断冒出新文件（如果 `AutoRecord` 也开着），
   外加触发 `ReplayLibrary.Prune()` 去删「最旧的」（可能删掉玩家想留的那条）。
2. `Write()` 会**整体重写** `name + ".mrep"` 与（若开了 `WritePlainText`）`.mrep.json`，
   20 秒一次、每次都是整段 GZip，白烧磁盘。

现在两处都默认关：

- `[General] AutoRecord = false` —— 进关卡**不**自动开录（要录就回放页按「录制」/ `/replay rec`
  / 手动把配置打开）。`ReplayRecorder.Finish()` 在 `!armed` 时直接返回，
  所以没开录就必然不会有文件。
- `[General] AutoSave = false` —— `NotifyTick` 里那次落盘改成
  `if (ReplayConfig.GetAutoSave() && tick >= nextAutoSaveTick)`。
  关着时文件**只在录制结束**时落盘一次（离开关卡 / 退出游戏 / 手动停止）。
  开着才回到「每 20 秒兜底」的旧行为。

> 结论：默认配置下「关卡里不会自己冒出回放文件」。这条有硬性验收 ——
> 自测跑完只允许出现 **1 条** `[Replay] 已写出回放` 日志（就是手动停止那一次）。

### 4.5 一致性保障：指纹校验（这是「必须确保一致」的抓手）

在每个检查点（如每 30 tick / 每秒）记录一条**状态指纹**：

```
tick | levelTime | currentEntityID | entityCount | energy | currentWave | 7×RNG(x,y,z,w) | 可选: FNV/xxHash(SerializableLevel)
```

- **记录时**：每检查点算一次，写进 `.mrep`。
- **回放时**：同 tick 算一次，逐条比对。
  - 全部命中 → **证明回放与实际一致**（在记录的那些维度上）。
  - 出现偏差 → 记日志 + 中止或（若有关键帧）重同步。
- 成本：每检查点约 40 B（7 条 RNG 各 16 B 可只在「RNG 敏感」调试模式存；常规只存哈希）。

**附加用途**：
- 开发期「同一局录两次、比对指纹」即可验证 mod 自身不引入不确定性。
- 未来做 GUI 时，「回放进度条/跳转」用关键帧 + 重放 tick 实现。

### 4.5.1 实测的**头号**一致性风险：实体 ID 错位

指纹也确实抓到了问题，但真正的坑不在「随机数」，而在**实体 ID**：

```
原版 tick 15 已经存在实体 8,9,10,11,12
回放 tick 15 只存在实体 8,9,10,11     ← 少了 12，到 tick 20 才出现
```

实体 ID 由 `LevelEngine.currentEntityID` 自增分配。它**确实被序列化**（`SerializableLevel.currentEntityID`，
见 `LevelEngine_Entities.WriteEntitiesToSerializable` / `CreateEntitiesFromSerializable`），
所以「关键帧里已有的实体 + 之后按同一顺序生成」在理论上完全对齐。
但只要有**任何一处生成时机差一两个 tick**（实测就有），后面的 ID 就整体错位，于是：

| 情况 | 后果 |
| --- | --- |
| `FindEntityByID(id)` 返回空 | 这条事件被丢弃 |
| 返回的是**另一个实体** | 静默地把动作做到错的东西上（比丢弃更糟） |

**最典型的症状是「阳光收集全部丢失」**：收集阳光走的就是实体指针事件，
一旦目标找不到，阳光就不进账 → 能量对不上 → 可种的植物不同 → 全线发散。
（实测：原版 tick 30 能量 75、回放 0；原版 tick 60 能量 150、回放 25。）

**缓解（已实现）**：`entity` 事件额外记录目标的**定义 ID**（`def`）与**记时坐标**（`ex/ey/ez`）。
回放时按「ID → 同定义 + 最近坐标 → 放弃」三级解析；
匹配半径取**半格**（一个格子 = `AreaMeta.GridWidth` 默认 **80** 草坪单位）。
播放小结会报「实体 ID 错位 N 次（找回 M / 真跳过 K）」。

> 实体枚举用引擎自己的 `LevelEngine.GetEntities()`（传空过滤表 = 全部），
> 不必猜 ID 上界。指纹里也加了 `ec`（实体数）与 `eh`（所有实体 (id,定义ID) 的哈希），
> 用来把错位**定位到具体哪一个 tick**。

**2026-10-04 更新（修掉加载路径后）**：

上面那个「打在旧控制器上」的 bug 在很长一段时间里**掩盖了其他所有现象**：
`LoadGame` 一直返回 false，于是「首次不一致 tick 60 / 4320 两次不同」这类观察
其实都是在讨论一个根本没载入成功的回放。修掉之后：

- 自测（`mvz2:halloween_2`，强制开局，关键帧 tick=1）**指纹 3/3 全部一致**，
  「同一文件两次跑出不同结果」的现象也随之消失。
- 用户的实际回放（`mvz2:halloween_1`，ver=3 旧文件）仍然在 tick 60 开始发散，
  但**不一致项只剩 RNG**，`ec`/`eh`（实体集合）完全一致 —— 与「实体 ID 错位」
  的早期判断不同，实体其实是齐的。真正丢的是 **283 个实体事件找不到目标**
  （其中真跳过 20 个），加上 tick 570 起能量开始差（250→225）—— 就是「资源没收到」。
- 为了下一步定位，指纹升到 **v5**：新增 `ed`（实体定义数量直方图，可直接看出
  「少了哪一类实体」）与 `r0`..`r6`（7 条 RNG 流各自的哈希，可点名是
  `level`/`entity`/`effect`/`round`/`spawn`/`conveyor`/`misc` 哪一条先跑偏）。
- 已知 RNG 分流的一个可疑点：`effectRandom` / `miscRandom` 的消费者里有没有
  **跟着渲染帧而不是逻辑 tick 跑**的（那样的消耗次数依赖帧率，录制与回放天然不同，
  且同一个文件两次跑也会不同）。

**尚未解决**：那 283 个找不到目标的实体事件到底是「目标已被提前销毁」还是
「ID 分配确实早了一两 tick」，以及 tick 31..60 之间第一次 RNG 跑偏的具体原因。
需要一条 **v5 的新录像**（旧 ver=3 文件没有 tick=startTick 的指纹，无法校验加载保真度）。

---

## 5. `.mrep` 文件格式

> ⚠️ **本节保留的是早期草案（二进制 varint 版）**，用来说明当时的取舍。
> **实际实现用的是 gzip + 逐行 JSON**，字段定义见
> [`Replay/README.md` 的「文件格式」一节](README.md#文件格式)。
> 改成 JSON 的理由：一局只有几百条事件，gzip 后两种编码都在 1~5 KB 量级，
> 而逐行 JSON 可流式写、可部分恢复、能直接读，排障价值大得多；
> 若将来事件量真的上去了，再换回二进制也不影响上层语义。
>
> 当前 `ver = 6`：v5 加了 `ec/eh/ed` 与 `r0..r6`（指纹），v6 给 `entity` 事件加了
> `lyr`（网格分层键，见 §4.4.2）。**读侧一律对缺失字段宽容**（`lyr` 缺失 = 不做分层判别），
> 所以旧文件仍然能播。

早期草案 —— 整体 **gzip**（与 `.lvl` 一致，魔数 `1F 8B`），内部：

```
第 1 行   JSON header（可读，便于排障）
          {"magic":"mrep","ver":1,"game":"0.7.0 test-10","created":"...",
           "area":"mvz2:...","stage":"mvz2:...","seed":123456,"tickCount":18000}
第 2 段   起始状态（取法1: SerializableLevel 的 BSON；取法2: 配置描述块 JSON）
第 3 段   动作流（二进制，varint 编码）
第 4 段   校验点（二进制）
```

**动作记录（二进制）**：

```
varint tickDelta       // 相对上一条的 tick 增量（第一条相对 tick 0）
u8     kind            // 1=HeldItemPointerEvent  2=SetHeldItem  3=ResetHeldItem  4=CancelHeldItem
--- kind=1 时 ---
u8     targetKind      // 0=Grid 1=Entity 2=Blueprint 3=Lawn
  0: varint lane, varint column [, f32 localY] [, f32 screenX,screenY]
  1: varint entityId   [, f32 localY] [, f32 screenX,screenY]
  2: varint index, u8 conveyor
  3: varint area
u8     pointerType     // PointerTypes
u8     button
u8     interaction     // PointerInteraction
--- 连续 Hold/Streak 用 RLE：u8 kind=5，后接 varint repeatTicks ---
--- kind=2 时 ---
string type, varint priority（或存 NamespaceID）
```

- `LocalPointerPosition.y` / `ScreenPosition`：仅对**会用到它们的行为**（工具指向实体：`ToEntityHeldItemBehaviour`、`EmptyHandEntityHeldItemBehaviour`）需要；普通放置可省略。
- 字符串（NamespaceID）用「长度 + UTF8」或索引表压缩。

> 说明：也可以用「第 1 行 JSON + 第 2 行 BSON」直接照抄 `LevelManager.SaveLevel` 的两行格式，
> 再用 `SerializeHelper.OpenCompressedRead/Write` 读写——**零新增序列化代码**，只是动作流要自己写。

---

## 6. 指令接口（暂定）

沿用本仓库已有做法（`ModularCurse/CurseCommand.cs`）：在
`MVZ2.Debugs.DebugManager.ExecuteCommand(string,int)` 上加 Harmony 前缀，
用官方 `CommandUtility.SplitCommand` 解析，命中 `/replay` 就自行处理。

| 指令 | 作用 |
| --- | --- |
| `/replay rec <name>` | 从当前 tick 开始录制（抓起始关键帧 + 开始收动作） |
| `/replay stop` | 结束录制并写 `BepInEx/replays/<name>.mrep`；**正在播放时等价于中止播放** |
| `/replay play <name>` | 播放：重建起始状态 → 禁用局内输入 → 按 tick 注入（**已实现**）|
| `/replay playlast` | 播放本次启动最近写出的那一条（**已实现**）|
| `/replay stopplay` | 中止播放，保留当前关卡状态（**已实现**）|
| `/replay list` | 列出输出目录里的回放（**已实现**）|
| `/replay verify <name>` | 静默回放并比对指纹，输出一致性报告（目前的等价物：`[SelfTest] Playback=true`） |
| `/replay info` | 当前状态（录制中/播放中/tick/事件数）（**已实现**，即 `/replay status`）|

> 播放的暂停 / 加速**不做成指令**：直接用游戏自己的 Space 与加速快捷键就行，
> 播放器不需要知道（时间轴是 tick，暂停时引擎不跑 tick，加速时一帧跑多个 tick，
> 事件永远落在它本来的那一个 tick 上）。

---

## 7. 播放时的限制（对应「只允许暂停/加速」）—— **已实现**

- 拦 **8 个输入入口**的前缀（`return false`）：5 个指针回调 +
  `OnBlueprintKey` + `ClickPickaxe` / `ClickStarshard` / `ClickTrigger`。
  这些是**玩家输入影响仿真的全部入口**，键盘热键最终也落到同样的方法上，所以一处拦就全拦住。
- **不碰** `UpdateInput` / `SetRaycastDisabled`：拦入口比拦「键盘读取 + UI 射线」更精确，
  也不会误伤暂停（Space）、加速（快捷键）、血条开关、Esc 选项菜单 —— 这些都不经过那 8 个方法。
- 唯一需要额外注意的是**区分「玩家点的」和「我们注入的」**：注入就是反射调用这 8 个方法本身，
  而 Harmony 的前缀对 `il2cpp_runtime_invoke` 进来的调用一样生效，
  所以用 `ReplayPlayer.ShouldBlockInput == IsPlaying && !injecting` 这个开关放行自己的注入。
- 悬浮类交互（Hover/Enter/Stay/Exit）录制时就已丢弃，回放时不重现 —— 它们对仿真无影响。
- `LevelBlueprintChooseController.UpdateFrame` 里的「右键卸下蓝图」等也要屏蔽。
- 暂停/加速本身**不进回放状态**（`isPaused`/`speedUp` 不属于 `SerializableLevel`），
  所以玩家在播放时随便按空格/加速都不会破坏一致性。

---

## 8. 实施计划（分阶段）

**阶段 0：环境**
- [ ] 把 BepInEx（be.788 x86 IL2CPP）装进 `test-10`，`UpdateInteropAssemblies=true` 启动一次生成 interop。
- [ ] `.\tools\sync-runtime.ps1 -GameDir "E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-10"` 刷新仓库 `runtime/`。
- [ ] 用 `tools/dump-interop-signature.ps1` 核对关键类型/方法是否齐全（见第 10 节）。

**阶段 1：脚手架**
- [ ] `Replay/` 建 mod 骨架（`Plugin.cs` / `Directory.Build.props` / `Replay.csproj` / `build.ps1` / `README.md`），
      依赖 DSHCore，命名空间 `replay`，命令 `/replay`。
- [ ] 实现 `DoHeldItemPointerEvent` 的记录（先只记到日志，验证「漏斗是否覆盖一切」）。

**阶段 2：记录/回放核心（先不带校验）**
- [ ] `.mrep` 读写（起始关键帧 gzip + 动作流二进制）。
- [ ] 回放：`LoadGame` 关键帧 + 禁输入 + 按 tick 注入。
- [ ] 用「同一局：正常玩一遍 → 录 → 回放」肉眼/日志比对。

**阶段 3：一致性**
- [ ] 指纹校验点；`/replay verify`。
- [ ] 修掉所有偏差来源；补 `SetHeldItem`/`ResetHeldItem` 与 Hold/Streak RLE。

**阶段 4：体验**
- [ ] `/replay info`、播放结束提示、非法/版本不符文件的友好报错。
- [ ] （后续）GUI：回放列表/进度条/跳转（关键帧 + 重放 tick）。

**阶段 5：收尾**
- [ ] 打包/安装说明、README、DSHCore 的 AI_GUIDE 追加一节。

---

## 9. 风险与缓解

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 漏斗不完整 | 有输入绕过 `DoHeldItemPointerEvent` 改了仿真 | 已定位到「手持物状态」这条支路；实现时用「全输入穷举 + 指纹差异」找漏网 |
| Hold/Streak 计数 | 推车蓄力等依赖按住 tick 数 | 按 tick 记录 + RLE；回放逐 tick 展开 |
| 起始确定性 | 预敌人/开场对话可能消耗关卡 RNG | **优先用完整起始关键帧**（取法 1） |
| Dictionary 顺序 | 迭代顺序理论可变 | 指纹校验点兜底；偏差时记录重同步关键帧 |
| 版本漂移 | 游戏更新后 tick 语义/序列化变化 | `.mrep` header 写 `game` 版本 + mod 版本；不匹配拒绝播放 |
| interop 过期 | test-10 尚未生成 interop | 阶段 0 必做（否则 native AV） |

---

## 10. 待验证清单（实现前/中确认）

1. `LogicLevelExt.DoHeldItemPointerEvent` 是否覆盖**全部**放置/工具/选卡动作；
   `HeldItemComponent.SetHeldItem` 是否只多出「工具热键/按钮」这一支。
2. `Hold/Streak` 是否严格按 tick 触发（本报告结论：是，源自 `UpdateFixed`），
   以及是否存在「按住期间移动指针切换目标」的边界。
3. `HeldItemTargetGrid.ScreenPosition` / `LocalPointerPosition` 在哪些行为里被真正使用
   （已知：`ToEntityHeldItemBehaviour`、`EmptyHandEntityHeldItemBehaviour`）。
4. 起始关键帧在不同关卡下的体积（是否可接受）。
5. `level.FindEntityByID` 能否在回放时稳定还原所有 `HeldItemTargetEntity`（实体 ID 一致性）。
6. 播放时 `LoadGame` 的副作用（`PauseGame` / 「载入完成」弹窗 / `levelLoaded=true`）如何优雅跳过。
7. 用 `tools/dump-interop-signature.ps1` 确认以下类型/方法在 test-10 interop 中可用：
   `MVZ2Logic.Level.LogicLevelExt`（扩展方法）、`MVZ2.Level.HeldItems.HeldItemComponent`、
   `MVZ2.Level.LevelController`（`StartGame`/`UpdateInput`/`SaveGame`/`LoadGame`）、
   `PVZEngine.Level.LevelEngine`（`Update`/`GetLevelTime`/`FindEntityByID`）、
   `MVZ2.Scene(s).GameUpdater`。

---

## 附录 A：常用类型/方法速查

| 用途 | 位置 |
| --- | --- |
| 逻辑 tick 序号 | `LevelEngine.GetLevelTime()`（`Engine/Level/Level/LevelEngine_Progress.cs`） |
| 关卡 RNG 状态 | `LevelEngine.ToSerializable()` 的 7 个 `SerializableRNG` |
| 动作漏斗 | `LogicLevelExt.DoHeldItemPointerEvent`（`Logic/Level/LogicLevelExt_HeldItem.cs`） |
| 手持物 | `HeldItemComponent`（`MVZ2/Level/HeldItems/HeldItemComponent.cs`） |
| 输入类型/交互 | `Logic/Inputs/PointerPhase.cs`（`PointerData`/`PointerInteractionData`/`PointerInteraction`） |
| target 变体 | `Logic/HeldItems/IHeldItemTarget.cs` |
| 存读档格式 | `LevelManager.SaveLevel/LoadLevel`；`Logic/Serialization/SerializeHelper.cs`（gzip） |
| 主循环 | `MVZ2/Scene/GameUpdater.cs`；`LevelController_Update.cs` |
| 指令拦截 | `ModularCurse/CurseCommand.cs`（范例） |
