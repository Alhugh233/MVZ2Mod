# MVZ2 回放 mod（`.mrep`）调研与设计

> 目标：记录并回放一局关卡游玩过程，文件为 `.mrep`，暂时通过控制台指令播放。
> 回放**不记录**暂停/加速等游戏外行为；播放时玩家**只允许**做暂停/加速等游戏外操作。
> 必须保证「回放结果」与「实际游玩结果」一致。
>
> 本文结论基于反编译源码 `_Scripts/0.7.0 in developing/`（与游戏 **0.7.0** 同日导出，2560 个 `.cs`）。
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

> **实现状态（2026-10-14）**：录制与**播放都已落地**（0.7.0 上播放的两处崩溃已修，
> 见 §4.4.15 / §4.4.16）。上面第 3 点里那个「统一漏斗」
> 在 interop 下**挂不了**（`IHeldItemTarget` 被生成成类，而具体目标是不声明它的值类型代理），
> 实际是挂 5 个调用点；第 6 点的输入拦截也改成了拦 8 个输入入口。
> 这些修正都写在 §4.4 与 §7（已重写为 "已实现" 版本）。
> **文件格式已规范化为协议 v10**：字段级规范见 [`PROTOCOL.md`](PROTOCOL.md)，
> 设计理由见 §5.1，使用者视角见 [`../README.md`](../README.md#文件格式)。

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

### 2.8 早期内测版 interop 实测修正（与上面反编译源码的差异）

生成早期内测版的 interop 后用 `tools/dump-interop-signature.ps1` 核对，发现三处关键差异
（**反编译镜像 `_Scripts/0.7.0 in developing` 与早期内测版二进制并不完全一致**）：

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

### 4.4.9 假鼠标的尺寸、锚点，以及为什么去掉了「接管系统光标」

- **尺寸**：`[Visual] FakeMouseScale`。画布是「屏幕空间-相机」，`sizeDelta` 最终要写
  `贴图尺寸 × (canvasRect.width / Screen.width) × scale`（前一段是 UI 单位↔像素换算，
  后一段是给玩家的手感微调）。这个比值**跟着窗口尺寸变**，所以每次比较 `Screen.*` 变了就重算
  （`FakePointer.ApplySize`）—— 只在建节点时算一次的话，改过窗口后光标会明显偏大/偏小。
- **锚点**：游戏把箭头的光点存在 `CursorData.hotspot`，而它是交给
  `Cursor.SetCursor(texture, hotspot, mode)` 的 —— **那个重载的热点以贴图左上角为原点**
  （实测游戏的箭头就是 `(0,0)`）。而 `RectTransform.pivot` 用的是**左下角**原点。
  两者差一个上下翻转：直接 `pivot = hotspot / texSize` 会把热点 `(0,0)` 变成**左下角**，
  于是光标被画到指针**上方**一整个贴图高度（看起来像「锚点在贴图正中」）。
  正确换算：`pivot = (hotspot.x / w, 1 - hotspot.y / h)`（`FakePointer.HotspotToPivot`）。
  自测会把热点与枢轴都打出来：**箭头热点 = `0,0` ⇒ 枢轴必须 = `0,1`**。
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

### 4.4.11 回放不许改动存档（金币走影子，其它一律拦）

**现象**：放一次回放，玩家的金币变了、统计数据（击杀/放置/通关次数）也涨了。

**根因**：MVZ2 的金币**不是关卡内货币**，而是全局存档里的钱：

```csharp
// MVZ2/Level/Components/MoneyComponent.cs
public void SetMoney(int value) => Global.Saves.SetMoney(value);
public int  GetMoney()          => Global.Saves.GetMoney();
```

而 `LevelController.ExitScene()` 里还有一句 `Saves.SaveToFile()`（退出关卡时保存游戏）。
统计那一批则是 `StatsGlobalCallbacks` 挂在 `POST_ENEMY_SPAWNED` / `POST_CONTRACTION_*` /
`POST_ENTITY_DEATH` / `POST_GAME_OVER` / `POST_LEVEL_CLEAR` / `POST_LEVEL_RESTART` 上的，
回放重演一局就一条条 `Global.Saves.AddStat(...)` 累加进存档。

> 通关那一条我们早就拦了（4.4.5 的 `EngineClearPrefix`），但那只拦住
> `LevelController.OnEngineClearCallback` 这**一个**入口；引擎层的
> `Triggers.RunCallback(POST_LEVEL_CLEAR, …)` 和整局里的每一次击杀照样在跑。

**做法**（`ReplaySaveGuard`）：

| 类别 | 处理 | 为什么 |
| --- | --- | --- |
| 金币 | **影子值**：`GetMoney` 返回影子的值，`SetMoney`/`AddMoney` 只改影子；第一次读时用真值播种 | 关卡要**读**钱来决定买不买得起 —— 只拦写入的话，花掉的钱不会减少 → 能买得起本买不起的东西 → 回放本身就跑偏了 |
| 统计 / 解锁 / 难度记录 / 上次选卡 / 上次地图 / 地图对话 / 关卡进度标记 | 前缀直接 `return false` | 这些在回放里没有任何「读回去」的需求，写进去只会污染存档 |

- 「上次选卡」（`LogicSaveExt.SetLastSelection`）值得单独点出来：它在**选卡阶段**就会被写
  （`LevelBlueprintChooseController`），也就是只要放一次回放，玩家自己存的选卡就被换掉了。
- 金币挂**两层**：`LogicSaveExt`（`Global.Saves.*` 的公开面）与 `LogicSaveData`（直接摸数据对象）。
  两者都很短，IL2CPP 有可能把里层内联进外层 —— 只挂一层的话，真被内联了那一层永远不生效，
  **而且不报任何错**。多前缀同时命中是安全的：金币每次都是「整体赋值给同一个影子」，
  外层拦下后里层根本不会跑。
- ⚠️ **Harmony 的前缀形参是按名字绑到原方法参数上的**。第一版把前缀写成
  `ExtSetMoneyPrefix(IGlobalSaveData save, int value)`，而原方法叫 `money` → 这两个补丁
  静默地没挂上（`GetMoney` 因为第一个参数恰好叫 `save` 所以过了）。现在失败会**点名**
  写进播放小结：`存档护栏 : 补丁 已挂 12 个，❌ 失败 2 个（LogicSaveExt.SetMoney、…）`。
- 补丁**延迟到第一次播放才装**（`ReplayHooks.EnsureSaveGuardHooks`）：金币那两处在
  `MVZ2Logic.*`，本仓库踩过「在 `Plugin.Load()` 阶段反射 `MVZ2Logic` 的类型会提前触发
  它的 `.cctor` → NRE → 被运行时永久缓存」的坑；等到真播放时游戏早已初始化完。

**验收**（自测 `SaveGuardCheck`，在「播放已结束、结束面板开着」那一刻做）：
直接对着存档读一遍 —— 影子 +1234 而真值不变；`SaveManager.SetStat` 写 +7 之后读回来还是原值。

### 4.4.12 换成别的窗口尺寸后，假鼠标必须还能对上操作

**现象**：调整窗口大小再放回放，假鼠标画的位置和实际操作的位置完全对不上（分辨率越偏离越明显）。

**根因**：`.mrep` 里的 `sx/sy` 是**录制时的屏幕像素**，而游戏把像素变世界坐标的公式是

```
LevelCamera（正交）: 世界偏移 = (像素 - 屏幕中心) × 2 × orthographicSize / 屏幕高
```

注意分母只有**屏幕高**：纵向可视范围恒为 `2*orthographicSize`，横向才是「纵向 × 宽高比」。
于是同一个像素在不同窗口尺寸下指向**不同的格子**。

而回放的操作本身是**语义化重演**（记的是 `lane`/`col`、实体 ID、卡槽号），跟像素无关 ——
所以「操作落在哪个格子」不随分辨率变，只有**跟着像素走的那两样**（假鼠标的位置、以及
`UpdateHeldItemPosition` 里给手持物用的指针位置）会跑掉。这也解释了为什么只有它们对不上。

**做法**（`ReplayViewport`）：header 记下录制时的窗口尺寸（`sw`/`sh`，ver 7），播放时换算：

```
k  = 现在屏幕高 / 录制时屏幕高
新 = 现在屏幕中心 + (旧 - 录制时屏幕中心) × k
```

- 两个分量用**同一个 `k`**：横向可视范围随宽高比一起变，所以横向不能用「新宽/旧宽」
  （代进去就知道：`世界X偏移 = (像素x - 屏宽/2) × 2 × ortho / 屏高`，只跟屏高有关）。
- 尺寸没变时 `k = 1`、中心相同 → **恒等**，所以旧行为一个比特都没变；旧文件（没有 `sw`/`sh`）
  同样恒等返回。
- 同一个换算用在四处：假鼠标路点、`args.position`、`raycast.screenPosition` / `eventData.position`、
  以及 `Global.Input` 的指针位置改写。
- 窗口在**播放中途**改了也照样有效；假鼠标的**大小**也要跟着重算
  （画布 UI 单位 ↔ 像素的比值同样依赖窗口尺寸，见 4.4.9），否则光标会明显偏大/偏小。
- ⚠️ **换算必须在「取值时」做，不能烘进路点表或缓存。** 第一版在 `FakePointer.Begin` 里
  就把路点换算好了（那时窗口尺寸已经知道），结果「开始播放**之后**再改窗口尺寸」依旧对不上 ——
  表里的值定死了。现在路点表里存的是**录制坐标系**的原值，读取时才换算。
  代价是每个「几何量」都有两个访问器：`ScreenPos`/`PointerScreen`（已换算）与
  `RawScreenPos`/`RawPointerScreen`（原值）—— **「临时换上假鼠标位置」那条通道两边必须同坐标系**，
  混着用就会重复换算、越偏越多。
- ⚠️ 推算时假设 `orthographicSize` 不变。这里可以放心：`LevelCamera.cameraSize` 随宽高比变，
  但代进去会发现相机位置里那个项**恰好抵消**（`camPos = ((0.5-anchorX)*leftspace, 0)`），
  所以窗口尺寸剩下的影响就只有 `2*ortho/屏高` 这一项。

**验收**（自测）：

1. `ReplayViewport.SelfCheck`（纯函数）：① 同尺寸恒等；② `1024x766 → 1920x1080` 下
   `(900,600) → (1507.1,846)`，且换算前后「相对屏幕中心 / 屏幕高」完全一致（= 指向同一个世界点）；
   ③ 屏幕中心恒映射到屏幕中心。
2. `ResizeProbe`（**真改分辨率**）：播放中途 `Screen.SetResolution` 到 800x480、等 45 帧，
   验证「假鼠标位置 == 按**当前**尺寸换算的结果」且「≠ 按录制时尺寸换算的结果」，
   然后还原。这是「烘焙 bug」的回归判定。
   > 自测注入的动作（数字键 / 工具按钮）**一个坐标都没有**，所以那条路径本来跑不到；
   > 自测会临时塞一条**假路点表**（`FakePointer.SelfTestSetTrack`，坐标是录制坐标系的）
   > 把换算链走通，报告里会标出来。用完立刻还原。
3. `FakePointer.SelfCheckPlacement`：节点上一次**真正摆放**时算出的世界坐标 == 那个屏幕点
   经相机反投影的结果（验 `PlaceAt` 的数学），且节点可见时那次摆放的屏幕点 == 当前 `ScreenPos`。
   > 为什么检「上一次摆放」而不是「当前位置」：手上有物品时游戏用**物品模型当指针**，
   > `UpdateVisual` 会跳过摆放（节点是隐藏的），那时 `rect.position` 还停在上一次的位置上。
   > 而且摆放发生在**逻辑 tick** 上，改完路点表得再等几个 tick 才能看到。

### 4.4.13 回放不受「交换触发」选项影响（蓝图上的即时触发标记）

**现象**：同一个回放，在「交换触发」开着/关着时效果不一样。

**根因**：那个开关是玩家选项（`swap_trigger`）、**关卡内随时可改**、**不在关卡关键帧里**，
而它参与决定手持蓝图上的 `instant_trigger`：

```csharp
// SelectBlueprintHeldItemBehaviour（拿卡槽 / 捡地上的卡 两条路都有）
bool swapped = Global.Saves.IsTriggerUnlocked() && Global.Options.IsTriggerSwapped();
bool triggerValue = canInstantTrigger && (holdingTrigger != swapped);
builder.SetInstantTrigger(pickData.triggerValue);   // 存进手持物
```

放下时 `BlueprintGlobalCallbacks` 读 `heldData.IsInstantTrigger()` 决定行为 ——
也就是说这个值直接进入仿真。

**做法**：录制时把**拿起蓝图那一刻**的 `instantTrigger` / `instantEvoke` 跟着事件一起记
（`itg` / `evk`，`ReplayRecorder.AppendHeldFlags`），回放时在同一个位置
**按回去**（`ReplayPlayer.ApplyHeldFlags` → `ReplayHeldFlags.Apply`）。

- 只有 `instantTrigger` 受该选项影响（`instantEvoke` 只看星之碎片）—— 两个都记只是为了完整性。
- 录制侧只对「手上确实是蓝图」的事件写这两个字段，类型白名单比对的是
  `NamespaceID.Path`（`blueprint` / `conveyor` / `blueprint_pickup`）——
  **不直接用 `LogicHeldTypes` / `VanillaHeldTypes`**：它们的静态字段初始化依赖
  `Global.BuiltinNamespace` / `VanillaMod.spaceName`，属于本仓库反复踩过的「碰一下就提前跑 cctor」那类。
- 两个字段**要么都在要么都不在**：只写一个就分不出「另一个是 false」与「这个事件没这个信息」。
- 写回用的是游戏自己的属性扩展（`LogicHeldItemProps.SetInstantTrigger`）。
  `IHeldItemData` 在 interop 里**不声明** `IHeldItemPropertyContainer` 的成员，
  所以要用指针手工包一层壳：`new IHeldItemPropertyContainer(data.Pointer)`。

**验收**：

1. `ReplayHeldFlags.SelfCheck`：现造一个 `HeldItemData`，验证 `SetInstantTrigger(true)` 之后
   `IsInstantTrigger()` 读回为 true、写 false 读回为 false、`instantEvoke` 同样能读写。
   > 为什么要单独验这个：属性是**注册过的键**才有意义的。键没注册时 `SetProperty` 只会默默失败，
   > 于是「把标记按回去」就成了**静默无效** —— 而它从游戏表现上完全看不出来。
2. 自测的动作日志会直接报「选卡之后手持是什么」：拿不到蓝图就不写 `itg`/`evk`，
   那是**录制内容如此**（能量不够 / 卡无效），不是功能失效。实测自测那条链
   （强制开局 + `ApplyChoose` + 数字键选卡）拿到的是 `mvz2:none`，所以那条文件里就没有这两个字段。

### 4.4.14 指针输入的**第三条**下游通路：逐 tick 轨迹 + 回放期独占输入层

前面 §4.4.1 讲了鼠标输入有两条下游通路（路由事件 / `POST_POINTER_ACTION` 全局回调）。
2026-10-09 处理「打砖块挡板跟着旁观者走、重装兵器不开火、梦魇的对话框变成让观众选」
这一批问题时，发现了**第三条**，而且是前两条都没覆盖的那种：

| | 通路 | 发生频率 | 典型受影响者 |
| --- | --- | --- | --- |
| ③ | 各行为**每帧直接问** `Global.Input.GetPointerScreenPosition()` / `InputHelper.IsPointerHolding(...)` | 每帧 × 每个调用点 | `BreakoutBoard`（打砖块）、`Snipenser`（重装兵器）、`MinecartRideable`（矿车）、`TeleporterTeamController` |

②③ 都**不是「按事件」发生的**，而原有的模型是「离散事件 + 按 tick 注入」，所以天然记不全；
更糟的是**回放期间没人挡 `InputManager.UpdatePointer()`**，它照旧每帧读旁观者的真鼠标。

#### 为什么是「每 tick 一次」而不是「每帧一次」

游戏主循环是「先跑逻辑 tick，再 `UpdateFrame`（输入在其内）」。所以
**「tick N 结束时鼠标在哪」正好就是 tick N+1 里那些行为会看到的值**。
而 `BreakoutBoard` 在鼠标模式下是**绝对赋值**（`position = ScreenToLawnPositionByY(screenPosition, 32)`）：
一帧里轮询多次只会互相覆盖，只有最后一次参与决策 ⇒ 每 tick 采一次与每帧采多次**等价**，
体积却小一个数量级（只在变化时写，一条 ~50 字节）。

> ⚠️ **时序修正（2026-10-09 晚，这条曾经差了一整个 tick，是本轮「所有回放都对不上」的主因）。**
>
> 最初的实现是「tick T 内部用『tick T−1 结束』的值」，依据是「采样发生在 `UpdateFrame` 里、
> 比逻辑 tick 晚」。**这是错的。** Unity 的 `Input` 在**一帧内是常量**：
>
> ```text
> 帧 F：Input 定成 I(F)
>   GameUpdater.Update()      → 跑 tick T（tick 内读 Input 看到的就是 I(F)）
>   我们的采样点（tick 后）    → sample@T = I(F)
>   InputManager.UpdatePointer→ 发 POST_POINTER_ACTION，带 I(F)
> ```
>
> ⇒ **tick T 内部读到的输入 == sample@T**，所以 tick T 必须用「第 T 条」采样。
>
> 差一个 tick 的后果极其隐蔽：`Snipenser` 的「按住才开火」晚一整 tick ⇒ 回放里**多发一颗子弹**
> （实测重装兵器 tick 300 时录制 `arrow=3`、回放 `arrow=4`）；`BreakoutBoard` 的挡板晚一帧 ⇒
> 珠子在另一个 tick 落空。两者都会让实体的 RNG 流错位，之后**指纹全线对不上**。
>
> 现在 `BeginTick(T)` 取的就是 sample@T，与 `EndTick(T)` 派发 `POST_POINTER_ACTION` 用的是
> 同一条采样，不会自相矛盾。守门员是 `ReplayPointerTrajectory.SelfCheck`（自测里断言）。

#### 轨迹**没覆盖到的地方绝不编数据**

轨迹是**按需**录制的，所以一个回放文件里的轨迹可能只覆盖**后半段** —— 例：2-11 的
`mvz2:crushing_walls` 要等 Boss 出场才生成，实测那份回放的采样从 tick **16801** 才开始
（全关长 18037）。

最初的实现用「**整条文件里有没有轨迹**」（`HasSamples`）来决定接管策略，于是：

- tick 1..16800：明明没有数据，却按「每 tick 统一派发」处理 ⇒ **凭空造出一整条录制时
  并不存在的 `POST_POINTER_ACTION` 流**，那一段越走越偏（实测首次指纹不符出现在 tick 1350）；
- 与此同时「按事件派发」那条老路径被**关掉**了，等于把两条路都走坏。

正确做法是用**逐 tick 的覆盖标记** `ReplayPointerTrajectory.TickCovered`：

| `TickCovered` | 接管策略 |
| --- | --- |
| `true` | 全部接管：按键相位用轨迹掩码、指针位置用轨迹、`POST_POINTER_ACTION` 由 `FirePhases` 每 tick 派发（不再走按事件那条） |
| `false` | **完全不接管**：按键/位置由游戏读实时输入，`POST_POINTER_ACTION` 走按事件的老路径（与改造之前的行为完全一致） |

#### 按需录制（省体积）+ 第三方注册口

轨迹不是每局都录：只有「这一关确实存在会读实时指针的内容」时才录。
判定由 **`ReplayCompat.RegisterPointerConsumer`** 提供，原版已内置注册 4 个：

| ID | 为什么 |
| --- | --- |
| `mvz2:breakout_board` | 挡板每 tick 挪到指针处（绝对赋值） |
| `mvz2:snipenser` | 炮口朝指针 + `IsPointerHolding(MOUSE,0)` 才开火 |
| `mvz2:minecart_rideable` | 目标点每 tick 由指针决定 |
| `mvz2:crushing_walls` | 每次按下扣 0.01 进度（不看点到什么） |

- 判定在关卡开始时求值一次、之后每 15 tick 复查；**一旦为真本关一直录**
  （否则「实体中途消失再出现」会留下一段没有轨迹的空洞）。
- 第三方 mod 在自己的 `Plugin.Load()` 里 `ReplayCompat.RegisterPointerConsumer("ns:entity", "说明")`
  或注册一个 `PointerNeedPredicate(LevelEngine)` 即可。
- 内置 ID **故意用字符串**而不是 `VanillaEffectID.*`：那些静态字段会去碰
  `VanillaMod.spaceName` / `Global.BuiltinNamespace`，属于本仓库反复踩过的「碰一下就提前跑 cctor」。
  `NamespaceID` 也是**懒解析**的（第一次求值时游戏早已初始化完）。

#### 回放期独占输入层

- 拦 `MVZ2.Inputs.InputManager.UpdatePointer()` 前缀 `return false` ——
  游戏自己不再派发任何 `POST_POINTER_ACTION`，**旁观者的鼠标彻底出局**。
  只掐 `UpdatePointer`、不动 `UpdateKeys`：暂停/加速/血条/Esc 照旧可用。
- 接管按键状态：`InputHelper.IsPointerOfPhase(int, PointerPhase)` 是
  `IsPointerDown/Holding/Up` 的最终漏斗（读的是 `Input.GetMouseButton*`），
  连同 6 个公开包装**每层都挂** —— 短方法可能被内联，只挂一层会「挂上了但没生效」。
  相位由掩码**边沿**推出：上升沿 `Press`、按住 `Hold`、下降沿 `Release`、否则 `None`。
- 由轨迹**每 tick** 派发 `POST_POINTER_ACTION`（3 个键各一条）。
  ⚠️ 同时**必须停掉**「路由事件顺带 `Fire` 一次」那条 —— 两边都发会让同一次按下被处理两遍
  （碾压墙一次点击扣两次进度）。旧文件（`ver <= 8`）没有轨迹，保留老路径。

#### 一个被删掉的错误短路

`ReplayPlayer.TryOverridePointerPosition` 里原来写着 `if (phase != Playing || injecting) return false;`
—— 也就是**注入期间故意不接管**指针位置。这条短路让「魔人经卷」（`sorcerersScroll` 把星之碎片的
held type 换成 `combat` ⇒ `CombatHeldItemBehaviour`）在回放里落点跟着旁观者的鼠标走：

```csharp
// Down：落点就是这里定下来的
SetDragStartPosition(data, Global.Input.GetPointerScreenPosition(pointer.type, pointer.button));
// Up：CastCombat 用 dragStartPosition 算 castPosition
```

改后：注入期间用**这个事件自己的位置**（`BuildArgs` 刚写好），其余时刻用**逐 tick 轨迹**的位置。

#### 选项对话框（梦魇的「选择命运」、上锁的箱子的「赎金」）

这类对话框是**阻塞式**的：调用方先 `level.PauseGame(100)` 再 `ShowDialog(...)`，
等真人点了才在回调里 `ResumeGameDelayed(100)`。回放里会变成「让观众选」，而且因为
逻辑 tick 被暂停，**回放会永久卡住**。

- 挂钩点选在 `CustomDialog`（`MainScene → MainSceneUI → CustomDialog` 是唯一漏斗），
  录制 `{tick, title, op[], selName}`，回放时显示 `DialogHoldSeconds`（默认 2 秒）后自动点掉。
- **不能只记下标**：上锁的箱子的选项列表是 `FillRansomOptionList` 按
  `level.GetMoney() >= cost` / `GetStarshardCount() >= cost` **动态生成**的，
  而金币是**全局存档**的钱、不在关卡关键帧里。所以：
  1. header 记 `mn`（开录时的金币），回放时用它给金币影子播种
     （`ReplaySaveGuard.SeedMoney`）—— 让选项列表的**生成条件**与录制时一致；
  2. 事件里记**所选那一项的文本**，回放时按文本在实时列表里定位；
  3. 文本找不到时**宁可不点**（交给玩家）**绝不按下标硬猜** ——
     箱子里「拒绝」和「付光所有钱」的后果差得很远。
- **点击走的不是回调**：那个 `Action<int>` 是原生委托，从托管侧读/调都不可靠。
  改成反射调 `CustomDialog.OnButtonRowItemClickCallback(row, column)` ——
  真按钮被点时走的就是它，后面的链路（`OnOptionSelect` → 展开 `ShowDialogTask` 的 lambda
  → `SetInteractable` / 关框 / 调用方回调 / `ResumeGameDelayed`）全部照原样发生。
- ⚠️ **两个 interop 坑（都是实测）**：
  1. **私有字段拿不到。** Il2CppInterop 把 `[SerializeField]` 私有字段生成成**属性**，
     `AccessTools.Field(typeof(CustomDialog), "countPerRow")` 返回 null。
     所以「选了第几项 / 该点第几项」全部改成按**选项文字**定位，
     行/列由 `ButtonRow.UpdateButtons(string[] texts)`（**公开**方法）记下的每行文字反查 ——
     连「每行几项」都不需要知道。
  2. **参数含托管委托时按签名找不到方法。**
     `AccessTools.Method(CustomDialog, "SetDialog", new[]{string,string,string[],Action<int>})` → null
     （interop 里那个委托不是 `System.Action<int>`）。
     改成按「名字 + 参数个数」找（`FindByArity`），前缀也不声明那个委托参数。
     > 这个坑**实测症状是「补丁安装 成功 35 个，失败 1 个」** —— 不报错，只是少挂一个。

**验收**（自测 `CompatCheck`，以及为什么它覆盖不到「效果」）：

1. 纯逻辑（不碰 UI，`ReplayPlayer.SelfCheckPointerPhases` / `ReplayDialogs.SelfCheck`）：
   掩码→相位的边沿推导 9 条；选项列表变化时的定位 5 条（含「录制选的项实时不存在 → 不猜」
   与「行文字 → 列号反查」）。这两条错了的表现极隐蔽（重装兵器不开火、箱子选错项）。
2. 管道：`ReplayHooks.PatchStatus` 必须**零失败**并把失败目标点名；
   自测用一个可开关的 `PointerNeedPredicate` 验证「关掉时这一关判定为不需要轨迹」
   与「打开后采样真的被写进文件」。
3. `Il2CppInterop` 不会为私有字段生成字段这一点，是靠自测报出的
   `找不到 CustomDialog.countPerRow` 才发现的 —— 又一次证明**补丁/反射必须自报状态**。
4. **覆盖不到**：打砖块/重装兵器/矿车/碾压墙/梦魇/上锁的箱子都在别的关卡，自测关
   （`mvz2:halloween_2`）里没有这些实体，所以「挡板真的跟着轨迹走」这类**效果**
   要靠另录一局亲自验；自测只能保证管道通、纯逻辑对。

### 4.4.15 0.7.0 的播放崩溃 —— **已解决**

0.7.0 上「开始播放」会让游戏**直接死掉**：

```
Fatal error. 0xC0000005
   at Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(...)
   at DynamicClass.DMD<MVZ2.Scenes.SceneLoadingManager::LoadSceneAsync>(...)
   at Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(...)
   at DynamicClass.DMD<MVZ2.Level.LevelManager::GotoLevelSceneAsync>(...)
   at MVZ2Logic.Games.IGlobalScene.GotoLevelCoroutine()
   at Replay.ReplayPlayer.DrivePhase(MVZ2.Scenes.MainSceneController)
   at Replay.ReplayPlayer.Tick(MVZ2.Scenes.MainSceneController)
   at Replay.ReplayPatches.SceneUpdatePostfix(MVZ2.Scenes.MainSceneController)
```

#### 定位手法：同步 flush 的文件诊断

BepInEx 的日志是**队列写盘**，硬崩会连尾巴一起丢掉（`LogOutput.log` 停在崩溃前若干行）。
所以临时加了一个「每次 `File.AppendAllText` 后立即 flush」的诊断文件，才拿到最后两条：

```
GotoScene: begin
guard: Data.GetMoney      ← 死在这里
```

一次对照实验就定案：**`ReplaySaveGuard.Begin()` 只要在「进关卡场景」这段时间里是 active 就必崩；
把它关掉，同一版本、同一条代码路径完全正常。**

**修法**：`Begin()` 与 `SeedMoney()` 从 `Start()` 推迟到 `WaitScene → Playing` 那一刻 ——
**护栏只在 `Phase.Playing` 期间 active，进关卡 / 切场景的整个过程都必须关着**；
`EnsureSaveGuardHooks()`（只装 Harmony 补丁）留在 `Start()`，实测无害。

#### 顺带改掉的设计：进关卡走游戏自己的指令

原来进关卡是自己调 `IGlobalScene.GotoLevelCoroutine()`（= `managed → il2cpp_runtime_invoke →
原生 async 场景加载` 这一跳）。既然游戏自己的 `/gotolevel` 在同一次会话里完全正常，
就把它当**唯一受支持的场景切换入口**：

| 当前状态 | 发的指令 | 游戏内部走的链路 |
| --- | --- | --- |
| 不在关卡里 | `/gotolevel <stage> <area>` | `GotoLevel.Coroutine` → `scene.GotoLevelCoroutine()` → `level.InitLevel()` |
| 已经在关卡里 | `/load` | `Load.Invoke` → `level.ReloadLevel(InLevel)` → `Dispose()` → `GotoLevelSceneAsync()` → `LevelManager.InitLevel()` |

两条路的整条场景加载链路都留在 il2cpp 原生侧，和我们无关了。

**排查过程中排除掉的假设**（都在 0.7.0 + be.788 x86 上实跑）：

| 假设                                                 | 实验                                                                                              | 结果           |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------------------- | -------------- |
| invoker thunk 缺失（release 不为反射可达的方法生成） | 读原生 `MethodInfo`：`invokerMethod=0x091DB3C0`（两个 `GotoLevelSceneAsync`）、其它方法也都有值    | ❌ 不成立       |
| 反射 `MethodBase.Invoke` 本身的问题                  | 换成直接调 `LevelManager.GotoLevelSceneAsync()`（C# 静态绑定）                                    | ❌ 一样崩       |
| 是本仓库其它 mod 引起的                              | 把 `LaboratoryMod` / `ModularCurse` / `Loadout` / `UiGallery` 的 DLL 全部移出 `plugins/`           | ❌ 一样崩       |
| 是 DSHCore 的 meta 注入补丁引起的                    | `MetaInjection.Apply()` 里临时直接 `return`（三个补丁全不装）                                     | ❌ 一样崩       |
| 是"经 interop 走 runtime_invoke"这件事本身           | 用挂 `LoadModResourcesInit` / `LoadMetaLists` / `GetSceneInstance` / `LoadSceneAsync` 的探针记录链路 | 链路能走到 `LoadSceneAsync` 内部才崩 |

**真正的反证（关键）**：游戏自己的 `/gotolevel` 指令在同一次会话、同一个主菜单状态下**成功** ——
`GotoLevel.Invoke` → `Global.Game.StartCoroutine(...)` → `scene.GotoLevelCoroutine()`，
最终落到**同一个** `SceneLoadingManager.LoadSceneAsync`。
所以「0.7.0 上这个调用不可用」不成立，差别只剩"谁在什么时候调用它"。
再加上「护栏一关就不崩」这次对照实验，答案就是**存档护栏的补丁在场景切换期间被调用了**。

### 4.4.16 关卡内重播：`/load` 的 async **续体不会恢复** → 改成轮询接管

换了「走游戏自己的指令」之后，**关卡外**那条路（`/gotolevel`）一次就通了；
但**关卡内**那条（`/load`，即结束面板的「重新播放」）会卡在 tick 1 不动。

逐个方法挂 prefix/postfix（`LevelController.ReloadLevel`、`LevelManager.GotoLevelSceneAsync`、
`SceneLoadingManager.LoadSceneAsync` / `UnloadSceneAsync`、`LevelController.Dispose`），
再每秒 dump 一次 `SceneManager` 的场景表，拿到这段确定的时序：

```
ReloadLevel 开始
LevelController.Dispose 开始
Scene.LoadSceneAsync 开始
ReloadLevel 同步段结束（Task 已返回）
未加载场景表：3 个  Main#-94/L/1  Level#-52760/L/1  Level#-79862/-/0     ← 新的还没激活
Scene.UnloadSceneAsync 开始
Scene.UnloadSceneAsync 同步段结束（Task 已返回）
下一帧场景表：2 个  Main#-94/L/1  Level#-79862/L/1                       ← 旧场景**确实卸掉了**
（此后 LevelManager.InitLevel 再也没有出现）
```

也就是：`LevelController.ReloadLevel()` 里 `await Scene.UnloadSceneAsync(旧场景)` 的续体**很久不恢复**，
于是它后面那句 `LevelManager.InitLevel(startAreaID, startStageID, exitTarget)` 在我们轮询接管之前一直不执行。
（在纯托管代码里看，这属于「Task 完成了但 continuation 迟迟不跑」；成因在 il2cpp/Addressables 内部，
我们既不能改也不该赌。）

> ⚠️ **后续修正（很重要）**：它**不是永远不恢复**，而是**恢复得很晚** ——
> 恢复后它并没有走 `LevelManager.InitLevel` 的托管桩（实测那个前缀整局只打过一次，在启动时），
> 而是**直接调进了 `LevelController.InitLevel` + `StartLevelIntro`**，
> 把关卡整个重建、把刚载好的关键帧冲掉。详见 §4.4.17 的结论与修复。

**所以改成不依赖它：`ReplaySceneEntry` 自己轮询。**

| 步骤 | 实现 |
| --- | --- |
| ① 发指令前记现场 | `Begin(area, stage)` 存下**旧控制器指针**与**旧关卡场景 handle** |
| ② 等旧场景卸完 | 每帧扫 `SceneManager`，`旧 handle` 还在 loaded 就继续等（这一刻正是原 `InitLevel` 前缀的时机） |
| ③ 等新控制器 | `MainManager.Instance.LevelManager.GetLevelController()` 的指针 ≠ 旧指针 |
| ④ 接管 | `ReplayPlayer.ApplyKeyframeTo(ctrl, area, stage, exitTarget)` → `LoadGame(关键帧)` |
| ⑤ 防迟到续体 | `LevelController.InitLevel` / `StartLevelIntro` 在「接管中/已接管」时 `return false`，
彻底挡住重建（`LevelManager.InitLevel` 的前缀只当另一道门） |

第 ⑤ 步同时还挡住了两件不该发生的事：它的 `SaveManager.SaveToFile()`（回放不许写存档）、
以及它发现 `.lvl` 存档后 `LoadLevel`（会从**玩家上次玩到一半**的状态续关，而不是录制时的状态）。

实测结果：record → `/load` → play 全流程跑通 ——
`轮询到新控制器 0x… → LoadGame 返回 True → 播放 tick 1..450 → 结束面板 → 回到回放页`，
补丁 44 个 0 失败。

> 附带结论：**Harmony 给 `async` 方法挂 prefix/postfix 是安全的**，
> 前缀在方法体开始前跑、后缀在"同步段返回 Task"时跑（不是在 Task 完成时），
> 这正是上面用来定位时序的手段。

### 4.4.17 仍未解决的保真问题：实体定义集合 + RNG 偏移（自测 2 项 FAIL）

自测的「录制 + 播放」里 `指纹核对 0/3`。**注意差的是哪几项**：

```
录制 t=150/300/450（三条完全相同）  en=50 w=0 fl=0 cl=false ec=6
                                   eh=48583e6f…  ed=[mvz2:fragment=3,mvz2:gargoyle_statue=3]
                                   r=e31d337f…  r0..r6 全同
回放 t=150                          en=50 w=0 fl=0 cl=false ec=6
                                   eh=ba499af6…  ed=[mvz2:leather_capped_zombie=2,mvz2:skeleton=1,mvz2:zombie=3]
                                   r=f572823f…
```

- 能量 / 波次 / 旗数 / 通关 / **实体总数**都一致，**只有「实体定义集合」和「RNG」不一致**。
- 录制侧三条指纹完全一样，而 `levelTime` 确实从 1 涨到 451（`LevelEngine.Update()` 末尾
  `AddLevelTime()`，说明 `LevelEngine.Update()` 跑了 451 次）⇒ 录制期间**关卡内容一点没动**。
- 关键背景：自测里关卡**自己永远不开始**（`IsGameStarted()` 在 5..35 秒一直是 false，
  卡在选卡面板），40 秒后靠 `BlueprintChoosePart.ApplyChoose()` +
  `LevelController.GameStartInstantTransition()`（私有，反射）强制开局；
  而 `GameStartInstantTransition()` = `SetCameraPosition + PRE_BATTLE + level.PrepareForBattle() + StartGame()`。

**下一步要查的**：同一份关键帧，为什么回放侧的波次/生成系统比录制侧先动
（录制侧 450 tick 一个实体都没变，回放侧 150 tick 就换成了 6 个敌人）。
重点怀疑 `LoadGame` → `InitLevelEngine` → `level.InitComponentsFromSerializable` 之后
「阶段定义的生成调度起点」与录制侧不等价。

---

**2026-10-14 更新：调试模式（§5.1 决策 5）一上线就把这条线索钉死了。**

开 `[Debug] Enabled=true` 录一份同样的小关（200 tick，逐 tick 指纹），回放时的日志：

```
轮询到新控制器 = 0x3A997380，开始载入关键帧
关键帧反序列化完成
关键帧里的实体 id：7,8,9,10,11,12
载入后状态与关键帧一致 ✅  t=1 en=50 w=0 fl=0 cl=False ec=6
                          eh=48583e6f1b9b37a3 r=fbf0118d00fef012
                          ed=[mvz2:fragment=3,mvz2:gargoyle_statue=3]
已在新控制器上恢复关键帧（不等游戏的 InitLevel）
关卡已按关键帧恢复（控制器 = 0x3A997380），开始按 tick 播放
── 紧接着 ──
首次状态不一致：tick 1  实体集合 48583e6f…→2c07c75c…
   实体构成 [mvz2:fragment=3,mvz2:gargoyle_statue=3]→[mvz2:leather_capped_zombie=2,mvz2:skeleton=1,mvz2:zombie=3]
   rx 首个不同（第 0 字符）：ac2c77e6 → 777fb5c2
   ei 首个不同（第 1 字符）：10:mvz2:fragment:0:780 → 1:mvz2:zombie:200:1066
   lk 首个不同（第 9 字符）：eid=13 → eid=7      ← currentEntityID
```

结论非常明确：

1. **关键帧本身是好的** —— `LoadGame` 之后用同一套采样器算出来的状态与录制时**逐字段一致**（✅）。
   所以问题不在序列化/反序列化。
2. **在「开始按 tick 播放」之后的几帧内，关卡被整个换掉了**：
   `currentEntityID` 从 **13 变成 7**、实体从「碎片 + 石像」变成「2 僵尸 + 1 骷髅 + 3 僵尸」。
3. `currentEntityID = 7` + 6 个敌人 = **一个刚 `InitLevel` 完、并且刚刚
   `CreatePreviewEnemies`（预览敌人，ids 1..6）的干净关卡**。
   反观录制侧：`currentEntityID = 13`（12 个 id 被分配过，前 6 个正是那批后来被移除的预览敌人）。

⇒ **回放期间有一次「关卡被重新 Init」**（或等价的一次全新开始流程）把我们的关键帧冲掉了，
而且它发生在 `ReplaySceneEntry` 接管**之后**。

下一步该查的（按成本排序）：

1. 把 `LevelController.InitLevel` / `LevelManager.InitLevel` / `StartLevelIntro` /
   `StartLevelIntroTransition` / `LevelController.Dispose` 的 prefix 都挂上无条件日志，
   看**是谁**在我们接管之后又跑了一遍（现在只挂了 `LevelManager.InitLevel` 的前缀，
   而它「被跳过时不留痕」—— 应该把它也记一行）。
2. 特别怀疑 `/load` 那条链的**迟到续体**：`LevelController.ReloadLevel()` 的
   `await GotoLevelSceneAsync()` 续体如果**最终恢复**了，它后面就是
   `LevelManager.InitLevel(...)` + `level.Triggers.RunCallback(POST_LEVEL_RESTART)`。
   前者会被我们的前缀挡掉，但**后者跑在旧控制器的 `level` 字段上**。
3. 也可能是 `PrepareForBattle` / 首次 `Update` 里某个「还没开始就补齐预览敌人」的分支 ——
   用调试模式把 `fw`（frame/update 计数）之类的标量也加进 `lk` 就能看出来。

> 这就是调试模式的第一个战果：**常规模式下这条线索要花好几轮实验才能拿到，
> 而它一次就把范围从「RNG/生成系统」缩到「关卡被重新初始化」。**

---

**2026-10-14 最终结论与修复（问题已解决）**

把 `LevelController` / `LevelManager` 的 `InitLevel`、`StartLevelIntro`、`ReloadLevel`、`Dispose`、
`GotoLevelSceneAsync`、`RemoveLevelState` 全部挂上「前缀日志 + 调用栈」，一次自测就拿到了答案：

```
[Replay] 轮询到新控制器 = 0x309F41C0，开始载入关键帧
[Replay] 关键帧已恢复，开始按 tick 注入（tick 1..210，事件 5 条）
[Replay] 已在新控制器上恢复关键帧（接管成功，控制器 = 0x309F41C0）
[DIAG] ★ LevelController.InitLevel 被调用 控制器=0x309F41C0 接管状态=Done
       at MVZ2.Level.LevelController.InitLevel      ← 上面只有 il2cpp 桥，**没有托管调用者**
[DIAG] LevelController.StartLevelIntro 被调用 控制器=0x309F41C0 接管状态=Done
```

而**整局** `LevelManager.InitLevel` 的前缀只打过一次（启动时那次 `接管状态=Idle`）。
⇒ 真相是：`LevelController.ReloadLevel()` 的 `await GotoLevelSceneAsync()` 续体**确实恢复了**，
但它那一跳**绕过了 `LevelManager.InitLevel` 的托管桩**、直奔 `LevelController.InitLevel`
（调用栈里连 `il2cpp_runtime_invoke` 都没有，说明是原生侧直接调用），
所以只拦 `LevelManager.InitLevel` 的结构完全拦不住——这解释了为什么当初得出
「续体永远不恢复」的错结论：我们是靠「`LevelManager.InitLevel` 再也没出现」推的，
而它本来就不会出现。

**修复：把闸门拦在「真正重建关卡」的那个方法上。**

| 补丁 | 行为 |
| --- | --- |
| `LevelController.InitLevel` 前缀 | `Waiting` → 就在这里 `ApplyKeyframeTo` 接管（此刻控制器刚 Awake、还没被 Init 过碰撞系统，是 `LoadGame` 唯一能成功的时机）；`Done` → 直接 `return false` |
| `LevelController.StartLevelIntro` 前缀 | 接管过的控制器上跳过（那是「新开一局」的流程：设起始镜头 + 开场对话 + `level.BeginLevel()`，与游戏自己「从关卡存档继续」的那套不等价） |
| `LevelManager.InitLevel` 前缀 | 保留（`/gotolevel` 那条路靠它；同时避开 `SaveToFile()` 与 `.lvl` 续关） |

好处是**不管谁调、从哪跳进来都不会再冲掉状态**，不再依赖「猜游戏内部哪条续体会不会跑」。

修复后自测（录制 + 播放，含结束面板的「重新播放」共两轮）：

```
指纹核对 1 / 1   ✅ 全部一致
[OK] 回放状态与录制时完全一致（指纹不一致 0 条）
停顿在 tick 210 / 210   注入事件 5 / 5
```

以前这里恒为 `指纹核对 0/1`（实体集合与 RNG 都对不上）。§4.4.17 的那条「保真问题」至此消失。

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

### 4.6 体验改进（2026-10-14，一轮 QoL）

以下都是「不改协议、不改录制/播放语义」的界面与目录层改进。逐条记下**为什么这么做**，
免得以后有人按「更简单」的方式改回去又踩同样的坑。

#### 4.6.1 关卡内减速按钮（`ReplaySlowDown.cs`）

- **倍率乘在 `LevelController.GetGameSpeed()` 的 postfix 上**，而不是用游戏自己的
  `AddSlowMotion(timeout, multiplier)`：后者**带超时**、`slowMotions` 是私有列表且只在
  timeout 到点时 `RemoveAll`，用它做「开关式减速」会让列表无限增长（每次开关都加一条）。
  postfix 里乘 `1 / fastForwardMultiplier` 还顺带满足「倍率跟随设置实时变化」。
- **互斥只拦 `LevelUIPreset.SetSpeedUp(bool)`**：加速有三条入口（按钮、热键、别的 mod），
  但它们最终都会走到 `LevelController.SwitchSpeedUp()` → `SetSpeedUp()`。
  只拦这一个**公开**方法就覆盖了全部入口，不必反射私有的 `speedUp` 字段。
  反过来「开启减速时关掉加速」直接 `speedUpButton.onClick.Invoke()` ——
  走游戏自己那条路。
- 按钮是**克隆加速按钮**：位置按锚点算到正下方，两个图标节点 `localScale.x` 取负做镜像，
  热键文字清空（本按钮不注册热键）。
- ⚠️ 坑：`MVZ2Logic` 的 `LogicOptionExt.GetFastForwardMultiplier(this IGlobalOptions)`
  **在 mod 侧编不过**（`CS1929`）—— interop 生成的 `OptionsManager` 没有声明实现那个接口。
  改成直接调接口上的同名方法 `opts.GetOptionFloat(LogicOptionItemID.fastForwardMultiplier)`。
- 按钮会随关卡重建（场景重载）一起消失，所以每帧有一个看门狗补建（与 `LevelHud` 同款）。

#### 4.6.2 回放目录索引（`ReplayIndex.cs`）

- 目录里多一份 `_replay_index.json`，**逐行扁平 JSON**（与 `.mrep` 同格式，直接复用
  `FlatJson`，不必为嵌套结构再写解析器）。
- 两个用途：① 缓存每条 header 快照 + `Size` + `LastWriteTimeUtc`（变了才重读；
  `.mrep` 是 gzip，每条都要解一次压缩头）；② 存**健康度**。
- 「健康度」没有别的自然归属：回放文件本身是玩家可自由拷贝/导出的只读产物，
  而「本机这一份库的播放记录」属于目录。索引**永远可以丢**（静默重建）。

#### 4.6.3 名字着色与播放前弹窗

- 三档：白（无事）/ 黄（**可能**播不好：环境不同、无环境记录、缺 `spec`、游戏版本不同、
  读不出来）/ 红（**上次播放真的出过不一致**）。
- 黄与红的弹窗**措辞必须不同**：黄说「可能出问题的**环境**」（逐条列），
  红说「上次出问题的**时间**」（第几 tick、共几处）。
- 写入规则（`ReplayPlayer.RecordHealth`）：有问题 → 红；没问题且跑到**总时长一半以上** → 白；
  跑得不够久 → **不动**（没看到的事不能当成「没问题」）。

#### 4.6.4 几个「看起来更简单但会错」的选择

| 选择 | 为什么不那么做 |
| --- | --- |
| 「时间」列显示文件创建时间，而不是 header 里的 `created` | 玩家关心「这条什么时候到我这里的」；导入进来的能差好几天。**导入后要显式 `SetCreationTime`** —— `File.Copy` 会把源的创建时间一起带过来 |
| 清理分三类、只删**第一类命中的** | 一次把失效+未命名都删掉会把明显不该删的一起删掉。而且提示必须说清清的是哪一类 |
| 「已重命名」用正则 `<关卡>_yyyyMMdd_HHmmss` 判定 | 没有别的可靠判据（文件里没有「名字是谁起的」这个信息），而录制器的自动命名格式是我们自己定的、稳定的 |
| 「更多」详情做成**克降 `CustomDialog` 的独立弹窗** | 第一版是「把列表收起来、在同一个面板里换内容」，版式要自己拼（容器/间距/字号全靠猜），结果很丑。改成弹窗后标题/正文/按钮的版式与字号全来自官方 prefab |
| 导出的实现独立成 `ReplayExporter` | 结束面板上的按钮已移除、功能移进【更多】，两处必须调同一份逻辑（原生「另存为」+ 扩展名补齐） |
| 删掉旧回放目录（`BepInEx/replays`）的迁移与搜索兜底 | 只有一个目录时的行为更好预测；旧文件的兼容成本一直大于收益 |
| **排序只重排行、不重建行** | 一行 6 个节点、58 行 = 348 次 `Instantiate`；排序时**行的内容没变**，只是顺序变了。重建的话点一下排序就卡一下（实测就是这个问题，一开始还以为是索引没生效 —— 日志里明明白白写着「索引复用 58 条，重读 0 条」） |
| 列表行颜色**黑（正常）/ 黄 / 红**，且**只改颜色** | 面板是浅色底，白字反而发虚；三档字号/字重/对齐完全一致，风险只靠颜色区分 |
| **`header.created` 写录制开始时刻**，不是写出时刻 | 回放是**离开关卡时**才写盘的，用 `DateTime.Now` 会让一局 16 分钟的回放 `created` 比文件名的戳晚 16 分钟（实测：文件名 `..._225128`、created `23:05:14`） |

---

## 5. `.mrep` 文件格式

> ⚠️ **本节保留的是早期草案（二进制 varint 版）**，用来说明当时的取舍。
> **实际实现用的是 gzip + 逐行 JSON。**
> **字段级规范见 [`PROTOCOL.md`](PROTOCOL.md)；面向使用者的说明见
> [`Replay/README.md` 的「文件格式」一节](README.md#文件格式)。**
> 改成 JSON 的理由：一局只有几百条事件，gzip 后两种编码都在 1~5 KB 量级，
> 而逐行 JSON 可流式写、可部分恢复、能直接读，排障价值大得多；
> 若将来事件量真的上去了，再换回二进制也不影响上层语义。
>
> 当前 `ver = 10`。版本历史见 PROTOCOL.md §13。**读侧一律对缺失字段宽容**，
> 所以旧文件仍然能播。§5.1 记录 v10 那一次规范化改动的**设计理由**。

早期草案 —— 整体 **gzip**（与 `.lvl` 一致，魔数 `1F 8B`），内部：

```
第 1 行   JSON header（可读，便于排障）
          {"magic":"mrep","ver":1,"game":"0.7.0","created":"...",
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

### 5.1 v10：把格式**规范化**成协议（设计理由）

到 v9 为止，这个文件格式是「一路加字段加出来的」：每次遇到「回放对不上」就加一个字段。
能跑，但有三件事说不清，而且越来越贵：

1. **读不了自己写的东西之外的东西。** 没有任何地方写清楚「哪些字段是必须的、缺了算什么、
   版本号更高怎么办」。于是「文件损坏」和「版本不匹配」被混成同一句报错，
   用户不知道该重录还是该升级 mod。
2. **「为什么这次没复现」没有起点。** 回放与实战不符时，第一件该查的往往是
   「当时的游戏版本 / 装了什么 mod」，而这些信息**根本没写进文件**。
   用户在不同版本的游戏上装着一堆 mod，拿一份旧回放回来放，只能看到「对不上」。
3. **调试需求与常规需求互相挤压。** 想定位「从第几个 tick 起不对」就必须逐 tick 采样、
   还要原始 RNG 与逐实体明细；但这些数据的体积是常规模式的 100 倍。
   没有开关的时候只能在「文件小但查不动」和「文件巨大但能查」之间二选一。

v10 一次性解决这三件事：

| 改动 | 解决的问题 |
| --- | --- |
| 独立出 **`PROTOCOL.md`** 规范；`spec`（规范版本）与 `ver`（格式版本）分开 | ① |
| 新增 **`env` 记录**：游戏 / 引擎 / 平台 / 全部已装 mod + 环境哈希 | ② |
| 新增 **[Debug] 调试模式**，默认关；文件里用 `debug`/`caps`/`samplers` 自述 | ③ |

**关键决策 1：环境只警告、不阻止播放。**
一开始想的是「环境不匹配就拒绝播放」。否掉的理由：环境不同的**原因**常常是无关紧要的
（换台机器、装了个不相关的插件），而拒绝播放会让用户**什么都做不了**。
改成「照样放 + 把差异完整报告出来」之后，用户既能看回放，又能立刻知道「这一次别指望完全复现」。
唯一的例外是**内容 mod 的关卡数据版本**——那一项错了关键帧根本载不进去，
所以单独点名（它属于「必然失败」，不是「略有偏差」）。

**关键决策 2：`fpt` 写「实际用的间隔」而不是「配置值」。**
如果只写配置，读者就得自己去推「调试模式会不会覆盖它、覆盖成多少」。
写实际值之后，文件对「自己是怎么录出来的」是完全自述的（协议自检也据此判断自述是否自洽）。

**关键决策 3：指纹的比对规则做成**通用**的，而不是逐个字段写比较代码。**
规则是：「凡是录制侧指纹里出现了的键，都要求回放侧逐字相等」。
这样加一种新指纹只要写一个**采样器**（录制侧与播放侧跑同一批），
比对、报告、协议自检全都不用改。这也是后来「第三方 mod 能自己加指纹」的前提。

**关键决策 4：明确写死「录制行为←配置，播放行为←文件」。**
之前散落各处：播放时有的地方读配置（`PointerAction`）、有的地方看文件（有没有 `ptr` 行），
结果是「同一个文件在不同配置下行为不同」——这对一个**录像格式**是致命的
（没法拿去给别人复现）。现在：
录制侧把配置**快照**一次并写进 header；播放侧的语义行为**只读文件**。
配置只剩「表现」（假鼠标、对话框停留秒数、探针）还没搬。
净效果：**同一个文件在任何机器、任何配置下播放，行为一致。**

**关键决策 5：调试模式是「写进文件」而不是「播放时现算」。**
一度想让调试模式成为一个「播放开关」：开着就用更多字段比对。
否掉的理由：那样差异报告就取决于**看的人**的配置 —— 用户来找你排障，
你让他开一堆开关再放一遍，等于把排障成本转嫁给他。
写进文件之后，他拿到的报告和你拿到的一样。

**关键决策 6：指纹字段用「短键 + 附加字段」而不是嵌套结构。**
`rx`（原始 RNG）、`ei`（实体明细）、`lk`（关卡标量）都是**字符串**，用分隔符编码。
这是被 §1.1 的「必须扁平」逼出来的，但结果是好的：
单遍解析器能读、人能读、`grep` 能用、加字段不用改解析器。

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
- [ ] 把 BepInEx（be.788 x86 IL2CPP）装进 `0.7.0`，`UpdateInteropAssemblies=true` 启动一次生成 interop。
- [ ] `.\tools\sync-runtime.ps1 -GameDir "E:\Game\PVZ\MVZ2\MVZ2 0.7.0"` 刷新仓库 `runtime/`。
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
| 版本漂移 | 游戏更新后 tick 语义/序列化变化 | `.mrep` header 写 `game` 版本（取 `Application.version`，不写死）+ mod 版本；不匹配拒绝播放 |
| interop 过期 | 0.7.0 尚未生成 interop | 阶段 0 必做（否则 native AV） |

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
7. 用 `tools/dump-interop-signature.ps1` 确认以下类型/方法在 0.7.0 interop 中可用：
   `MVZ2Logic.Level.LogicLevelExt`（扩展方法）、`MVZ2.Level.HeldItems.HeldItemComponent`、
   `MVZ2.Level.LevelController`（`StartGame`/`UpdateInput`/`SaveGame`/`LoadGame`）、
   `PVZEngine.Level.LevelEngine`（`Update`/`GetLevelTime`/`FindEntityByID`）、
   `MVZ2.Scene(s).GameUpdater`。

---

## 11. 0.7.0 正式版：播放闪退的根因（2026-10-16）

> 这一节是**结论性**的：症状、被证伪的猜想、以及最后真正改掉的那一处。
> 使用者视角的说明见 [`../README.md`](../README.md#070-正式版播放任意回放闪退的根因与修法)。

### 11.1 症状与崩溃点

首个正式版发布之后，**播放任何回放**都会在进关卡后几秒内让进程**直接消失**：

- BepInEx 日志里没有托管异常，`ErrorLog.log` 里是原生栈；
- Windows 事件：`Application Error id=1000`、`出错模块=GameAssembly.dll`、
  `异常代码=0xc0000005`、`错误偏移=0x0141b822`；
- `0x0141b822` = `System.RuntimeType.IsAssignableFrom` + `0x32`（用 `tools/rva-index.tsv`
  按 RVA 反查得到）。

用 `tools/dump-stack.ps1` 从两份崩溃转储里各自还原出的调用链一致：

```
Addressables ResourceManager.ProvideResource
  → CompactLocation.get_Dependencies
    → ResourceLocationMap.Locate
      → RuntimeType.IsAssignableFrom   ← ★ AV 在这里
```

注意这条链是**游戏的资源加载路径**，与回放本身的数据没有直接关系 ——
它只是「谁碰巧把坏参数传进原生 `IsAssignableFrom`」的那个位置。

### 11.2 二分：真正的原因是存档护栏

把 `[General] SaveGuard` 关掉（前缀直接 `return true`，什么都不拦）后**回放能完整播完**
（tick 88/226/376…，进程存活，无崩溃事件）。于是按「护栏的两半 × 两层」继续二分：

| 配置 | 结果 |
| --- | --- |
| `SaveGuard = false` | ✅ 正常播完 |
| `SaveGuard = true` + `SaveGuardBlockWrites = false`（只留 5 个金币补丁） | ❌ 同样 AV @ `0x0141b822` |
| `SaveGuard = true` + `SaveGuardBlockWrites = true` + `SaveGuardDataPatches = false`（只留 3 个 `LogicSaveExt` 补丁） | ✅ 正常播完，指纹 4/4 |

结论：**崩的是 `LogicSaveData.GetMoney / SetMoney`（直连数据对象那一层）**，
不是统计/解锁那一半，也不是金币影子算法本身。

而这正是 0.7.0 时期 `ReplayPlayer` 上面那条注释警告过的东西 ——
当时的现场记录是「`guard: Data.GetMoney`」之后再无输出。也就是说这个问题**从预发布版就存在**，
只是那时它表现为「进关卡场景期间崩」，被「延后到关卡加载完成才启用护栏」绕过去了；
正式版上它换了个位置（资源加载路径）再次发作。

### 11.3 修法与代价

- 新增 `[General] SaveGuardDataPatches`（默认 `false`）：不挂 `LogicSaveData` 那一层。
- 保留 `LogicSaveExt.GetMoney / SetMoney / AddMoney`（`Global.Saves.*` 的公开面）——
  游戏绝大多数地方走这条，所以**存档隔离仍然生效**（自测里
  「金币 真=133068 / 影子=130600 → +1234 → 真值仍=133068」仍然通过）。
- 代价：少数绕过公开面、直接摸 `LogicSaveData` 的写法拦不到。这是明确的取舍，
  不是遗漏 —— 挂上那一层的代价是**整个进程消失**。

### 11.4 被证伪的猜想（别再试）

| 猜想 | 实验 | 结果 |
| --- | --- | --- |
| 关键帧载入得太早，关卡资源目录还没就绪 | `KeyframeDelayFrames = 90` | ❌ 照崩 |
| 关键帧里的 `musicID` 触发了 `mainmusic` bundle 加载 | `RestoreMusic = false`（日志确认「关键帧里的音乐字段已剔除」） | ❌ 照崩 |
| 把 `Locate` 的类型过滤器 `System.Object` 改写成 `null` 就绕开了 | 补 `ResourceLocationMap.Locate` | ❌ 更糟：崩溃从「id=1000 + GameAssembly」变成「id=1023 + coreclr 内部错误」= **这个拦截点根本不能用** |
| 补 `Il2CppSystem.RuntimeType.IsAssignableFrom` 提前给出答案 | 挂上那个前缀（`TypeGuard`） | ❌ 同上，且更早更难查 —— 见 11.4.1 |

**判断崩溃类型的经验规则**（本仓库反复用到）：

- `Application Error id=1000` + `出错模块=GameAssembly.dll` + `0xc0000005`
  → 真正的**原生访问违例**（游戏或我们传给原生的参数）；
- 只有 `.NET Runtime id=1023`（coreclr internal error）
  → 崩在 **CoreCLR / 我们自己的补丁**里（Harmony 编译失败的 detour、把托管
  `System.Type` 加进补丁目标、给敏感的原生小函数挂前缀等等）。

#### 11.4.1 `TypeGuard` 自己就是一颗雷（2026-10-16 实测）

修法（11.3）落地后，用**诊断构建**（`-Diagnostics`，含 `TypeGuard`）再播一次
`compat1` 仍然崩，但事件类型变成了 **`id=1023`**（coreclr internal error）——
按上面的规则，这说明**崩的是补丁而不是游戏**。

对照实验：

| 构建 / 配置 | 结果 |
| --- | --- |
| 诊断构建 + `TypeGuard = true` | ❌ 进关卡后很快 `id=1023` |
| 诊断构建 + `TypeGuard = false` | ✅ 正常播完（无任何崩溃事件） |

也就是说 `ReplayTypeGuard` 给 `Il2CppSystem.RuntimeType.IsAssignableFrom` /
`Il2CppSystem.Type.IsAssignableFrom` 挂的那两个前缀，**挂上就等于把进程推向 coreclr 崩溃**。
它当初是为了「绕开崩溃」才写的，结果只是把「游戏自己的 AV」换成了「补丁自己的 AV」。

**处置**：`ReplayTypeGuard` / `ReplayAddressablesProbe` / `ReplayAddressablesFix` 三个文件
**已删除**（需要时从 git 历史取回）。它们共同的模式是「给游戏的热路径小函数挂前缀」，
在 0.7.0 上没有一个能安全存活。
`Diagnostics/` 现在只剩**不改行为**的东西：自测（`ReplaySelfTest`）、GUI 自测、
布局 dump，以及只做 `Marshal` 读的 `NativePeek`。

### 11.5 顺带的工程决定：诊断代码编译期隔离

排查过程里造出来的探针（Addressables 定位记录、`IsAssignableFrom` 守卫、
逐 tick 心跳、布局 dump、内置自测）**本身就会改变行为** —— 见 11.4 与 11.4.1。
所以它们不再靠「配置项默认关」来兜底，而是整份搬进 `Diagnostics/`，
由 MSBuild 属性 `Diagnostics` 控制是否编译（详见 [`../README.md`](../README.md#diagnostics-与--diagnostics)）。
发布 DLL 因此从 ~337 KB 降到 **263,680 字节**，且不再包含任何探针。

两点实现细节：

- 传送带守卫（`ReplayConveyorGuard`）也跟着搬进去了：它接管
  `ConveyorBlueprintController.UpdateFixed`（重写游戏逻辑，不是「只是看看」），
  而正式版的传送带闪退**还没修好**（已明确推迟），不该跟着发布包出去。
  它的开关配置项（`ConveyorGuard` / `ConveyorGuardLog`）同样在 `#if DIAGNOSTICS` 里，
  这样发布 cfg 里不会留下一个不起作用的开关。
- 但它的 `Beat(...)` 调用点**留在原处**（`ReplayPatches` 每帧 5 处、`ReplayPlayer` 6 处）：
  那是「崩溃前最后一条标记」，是定位原生 AV 时唯一能用的线索。
  发布构建编译 `Game/ReplayConveyorGuardStub.cs` 里的空实现 ——
  调用位置两个构建完全一致，不会因为要排查而改变代码结构。

### 11.6 验证记录（0.7.0 正式版，2026-10-16）

均为**诊断构建**（= 发布构建 + `Diagnostics/`）下的自动化播放。
判据三条：会不会闪退、有没有播完、指纹有没有全对。

| 用例 | 规模 / 来源 | 闪退 | 一致性 |
| --- | --- | --- | --- |
| `mvz2_palace_10_20261016_200337` | 710 tick / v10 / 0.7.0 录制 | ✅ 不崩 | ✅ **4/4 指纹一致**；点「重新播放」再走一遍仍 4/4 |
| `compat1` | 9441 tick / v9 / 0.7.0 录制 | ✅ 不崩（以前进关卡几秒就崩） | —（自测超时截断在 tick ≈8700） |
| `山4`（`mvz2:mountain_4`） | 14704 tick / v9 / 0.7.0 录制 | ✅ 不崩（以前几秒就崩） | ❌ 29/29 不一致，第 4414 tick game-over |
| 对照：诊断构建 + `TypeGuard = true` | — | ❌ 即刻 `id=1023` | —（见 11.4.1） |

**关于 `山4` 的不一致（必须记住的一条边界）**：它是 **0.7.0 录的 v9 文件** ——
没有环境哈希、没有 `samplers` 字段、**也没有逐 tick 指针轨迹**（`特性=vp`）。
而 `mountain_4` 含 `pistenser` 这类**由指针位置驱动仿真**的内容（炮口朝指针），
没有轨迹就只能靠离散事件「猜」指针位置 → 跑偏是必然的。

- 修复前这条回放**根本播不起来**（几秒内闪退），指纹**一条都没有**
  → 不存在「以前一致、现在不一致」的回归；
- 同版本录的 `palace_10` 依旧 4/4 → 本轮的改动没有破坏一致性；
- 想在 0.7.0 上得到可核对的一致性，**用 0.7.0 重新录**（新文件是 v10，带环境哈希 + 指针轨迹）。

> 注意：**自动验证只能用诊断构建跑**（自测代码不在发布构建里）。
> 两个构建的行为差别只有「不自测 = 不多跑那套状态机」与传送带守卫（见 11.5）；
> 被验证的这几关都不是传送带关，所以播放行为与发布构建一致。

### 11.7 0.7.1 兼容 + 四项正式版失效功能（2026-10-17）

#### 11.7.1 0.7.1 兼容性

- **做法**：把 0.7.0 的 BepInEx 运行环境搬到干净解压的 0.7.1 目录（不带 `interop` / `cache`），
  让 BepInEx 首次启动时按 0.7.1 的 `global-metadata` 重新生成 interop，
  再 `tools/sync-runtime.ps1 -GameDir "<0.7.1>"` 刷回仓库 `runtime/`。
- **结果**：`build.ps1 -Clean` 下 **6 个 mod 零代码改动全部编译通过**
  → 0.7.1 对这套 mod **没有 API 破坏**。
- **唯一行为差异**：0.7.1 起真的会进 `MainScenePageType.HealthWarning`（健康忠告页）。
  它是**阻塞式**的（要玩家点一下），自动化流程会一直卡在「等主菜单」。
  自测里对它的处理与 Titlescreen 一致：调游戏自己的 `DisplayMainmenu()` 跳过。

#### 11.7.2 减速按钮「丢失」

不是代码问题：用户 cfg 里 `[General] SlowDownButton = false`。
但自测当时会**无条件**断言「按钮建出来了」，于是配置关着时会产生 6 条假 FAIL。
现在这三条按 `SlowDownButtonOn` 跳过，并显式写一行「[跳过] …配置里就是关的」。
同时给 `Build()` 的三个 early-return 补了 `WarnOnce`，下次真是 prefab 变了能一眼看到。

#### 11.7.3 回放菜单文案替换失效

原来只挂 `UpdateRestartButton` / `UpdateLeaveLevelButton` 的后缀。那两个后缀只在
**原版刷新主页控件**那一瞬间生效，之后还有东西会重写按钮文字 ——
典型是 `TextMeshProUGUITranslator`（节点 enable / 切语言时用 `Translate()` 覆盖 `text`），
以及原版自己的 `UpdateMainPageButtonText`。

→ 新增 `ReplayPatches.EnforceMenuLabels()`：回放期间**每帧**把两个按钮的文字按一遍（幂等）。
只在 `optionsDialogController.IsOpen()` 为真时动手，菜单没开时只有一次原生判空。
菜单开着时游戏是暂停的、帧数很低，两次字符串赋值的开销可忽略。

#### 11.7.4 「一致性通过但名字仍标黄」

两个原因叠在一起：

1. **门槛太高**：原来要求「跑到总时长一半以上」才算验证通过。一条两万 tick 的回放要跑一万 tick，
   玩家看一眼就走了 → 永远达不到。（用户实测反馈「黄标没降级」。）
   → 降到 **20%**（`(lt - st) / 5`），仍能覆盖「关键帧载入 + 前几波」这段最容易翻车的地方。
2. **写错了键**（真凶）：`RecordHealth` 用 `CurrentName` = `Path.GetFileName(file.Path)`，
   是**带 `.mrep`** 的文件名；而索引列表的主键是**裸名字**。
   于是健康度被写到一条谁都读不到的记录上（实测索引里出现过
   `{"n":"mvz2_mountain_5_...mrep","f":null,...,"ve":"..."}` 这种脏条目）。
   → `ReplayIndex.UpdateHealth` 统一过 `NormalizeName`（去掉 `.mrep` / `.mrep.json`），
   并顺手清掉历史脏键。

配套新增 `ReplayIndexEntry.VerifiedEnv`（索引 `ve` 字段）：记下「在哪个环境哈希下验证的」。
`ReplayInfo.Risk` 里 **`VerifiedNow` 优先于所有静态推断**（环境不同 / 没环境记录 / 版本不同），
`CompatText` 也不再挂「录制环境与当前不同」那句。
环境一变哈希就变，黄标自动回来 —— 所以跨版本回放不会永远黄着。

#### 11.7.5 手持蓝图时假鼠标不跟随

**真凶是类型挂错了（`LevelUIPreset` 而不是 `LevelUI`）＋ 拦截点选得太靠前。**

- 原来挂 `LevelController.UpdateHeldItemPosition`，在**前缀**里临时替换指针值。
  但那个方法内部走的是 `Main.InputManager.GetPointerScreenPosition()` →
  `InputHelper.GetPointerPosition()` —— **只有一行的静态方法，IL2CPP 完全可以内联**。
  内联后前缀在那个调用点上不执行（其它消费者走别的调用点，所以「只有手持物不跟随」）。
- 改为挂**纯表现的终点** `LevelUI.SetHeldItemPosition(Vector2)`，用 `ref` 形参直接换世界坐标：
  游戏算完世界坐标后我们覆盖它，与谁算的、有没有内联都无关。
- `LevelUIPreset` 是**预制体/预设**类，`SetHeldItemPosition` 的实际声明类是
  **`LevelUI`**（dump 里按 `nearest class decl` 反查确认）。挂错类型时
  `AccessTools.Method` 返回 null，日志只说「找不到目标」，很容易漏看。
- 报告里新增 `手持tick=` / `手持跟随=`：前者 > 0 而后者 = 0 就说明这条通道又没生效。
- 顺带修掉补丁校验器的误报：`ref X` 形参接管值参数 `X` 是 Harmony 的标准用法，
  原来会误报「形参绑不上」。



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
