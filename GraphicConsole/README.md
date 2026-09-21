# MVZ2 图形化控制台（Graphic Console）

用可视化面板替代原版调试控制台的输入窗口。测试工具，**最大兼容性优先**。

- 适配：MVZ2 0.7.0 test-4 / Windows x86 / IL2CPP metadata v31.1 / BepInEx be.788
- 前置：无硬依赖（对 DSHCore 是**软依赖**，只是为了加载顺序与共用日志查看器）
- 需求来源：仓库根的 `Loadout.txt`

---

## 1. 兼容性设计（为什么这样写）

这个 mod 的第一目标是「装上去不会把别的 mod 搞坏」，所以做了几个刻意的取舍：

### 1.1 零注入

**不注入任何 il2cpp 类型、不注册任何 Definition、不注册自己的 ModInfo。**

这一步直接绕开了 metadata v31 下注入类的全部已知坑（`AI_GUIDE.md` §5）：
字段不被 GC 追踪、`Class::Init` 重置 vtable 槽、官方 LINQ 遍历注入 def 崩、
注入类实现接口抛 `MethodAccessException`、抽象方法槽 `EntryPointNotFoundException`……

代价是功能形态受限（不能加新 buff / 新指令 / 新关卡），但本项目不需要这些。

连「每帧心跳」都不自己注入 `MonoBehaviour`（那也要 `ClassInjector.RegisterTypeInIl2Cpp`），
而是挂在官方本来就每帧跑的 `MainSceneController.Update` 上（见 `Patches/PanelTickPatch.cs`）。

### 1.2 全部复用原版资产

| 需求 | 复用的原版资产 |
| --- | --- |
| 无限能量 / 无限星之碎片 / 禁用冷却 / 禁用失败 | 原版 `/cheat` 用的那四个**关卡级 debug buff**（`VanillaBuffID.Level.debugXxx`） |
| 忽略大波事件 / 自动收集 | 原版关卡属性（`LogicLevelProps`） |
| 后台暂停 | 原版**全局**选项 `pause_on_focus_lost`（设置界面里同一个开关） |
| 面板暂停（速度=0） | postfix 原版 `LevelController.GetGameSpeed()` 返回 0 |
| 旗数 / 波数 / 能量上限 / 星之碎片上限 / 出怪三参数 | 原版关卡属性（**都写 level 实例，不写 StageDefinition**） |
| 隐藏 HUD 九项 | 前五项走官方 `LevelEngine` 扩展（`SetEnergyActive` / `SetBlueprintsActive` / `SetPickaxeActive` / `SetStarshardActive` / `SetTriggerActive`）；后四项（关卡进度条 / 菜单按钮 / 加速按钮 / 金币数量）原版**没给接口**，只能改 `LevelUIPreset` 与 `level.LevelProgressVisible`（见 `Game/LevelHud.cs`） |
| 重载 | 官方 `LevelController.ReloadLevel()`（+ 先 `LevelManager.SaveLevel()`） |
| 面板的对话框、遮罩、标题、按钮、输入框 | 克隆场景里现成的 `MVZ2.UI.InputNameDialog` |
| 复选框 / 输入框 / 按钮 | 克隆 `MVZ2.UI.OptionWidgets` 与 `InputNameDialog` 里的现成控件 |
| 下拉格的**外观** | 同上（但不用 TMP 自带弹窗，见下） |
| 下拉格的**弹窗** | **自绘**（`Ui/PickerOverlay.cs`）：克隆原版按钮+文本的一层浮层，选项多了分页。原因见 §3 第一条 |
| 控件的**悬停说明** | **原版同一套悬停组件**：给目标挂 `TooltipAnchor` + `TooltipHandler`，鼠标进入时调 `MainSceneController.ShowTooltip(...)` —— 设置界面 / 图鉴 / 制品按钮用的就是这条链（见 `Ui/PanelTooltips.cs`） |
| 选卡界面的「全部蓝图 / 原版蓝图」切换按钮 | 克隆选卡面板自己的「取消」按钮（`blueprintChoosePanel.cancelButton`），摆在它右边一格 |
| 独立文本 | 克隆对话框的标题节点（继承 TMP 字体资产，自己 `AddComponent<Text>` 拿不到字体） |

### 1.3 只写 level、绝不写全局

- 关卡内面板**只写 level 级属性**。`StageDefinition` / `AreaDefinition` 是全局共享对象，
  往它们身上写会污染所有使用该关卡的场合（其它存档、无尽模式、街机）——
  原版恰好给「出怪三参数」提供了 `SetSpawnPointXxx(this StageDefinition, float)` 这种危险重载，
  本 mod 一律不用，改走 `level.SetProperty`（level 的回退链是「自身 → Stage → Area」，写自身即可盖住）。
- **绝不碰全局 options**。`LogicOptionExt.SetDifficulty` 会写进全局配置文件、跨用户跨存档生效，
  所以「难度」走的是自己换难度 buff 的路线（用原版的 `GetBuffID` / `GetIZombieBuffID`）。
- 关卡属性会随 `.lvl` 存读 —— 这一点是**有意接受**的（前作存档修改器就是靠改 lvl 实现的）。
  **「重载」的语义是「先保存当前关卡 → 改背景/关卡 → 从原入口重进」**，所以进度保留：
  `LevelManager.SaveLevel()`（写进**当前 `startStageID` 对应的那个 .lvl**）→
  `controller.SetStartStage(新 area/stage)` → 官方 `controller.ReloadLevel()`
  （它内部就是 `SaveToFile()` → `Dispose()` → `GotoLevelSceneAsync()` → `InitLevel(startAreaID, startStageID)`）。
  ⚠️ 存档文件名就是 **stageID**（`<用户存档>/<stage.Space>/level/<stage.Path>.lvl`），
  所以换到**不同关卡 id** 时状态存档对不上、只能从头开始；日志里会写明。
  以前那种「自己 `Dispose()` + `InitLevel`」的写法会让 `LevelManager.InitLevel` 里
  `HasLevelState(stageID)` 判定为 false，表现就是「点重载进了一个全新关卡」。

### 1.4 兼容性等级

有些功能天生要「接管」别的方法，撞上别的 mod 的补丁就难查，所以做了分级
（配置在 `BepInEx/config/qoznos.mvz2.graphicconsole.cfg` 的 `[Compat]` 段）：

| 等级 | 含义 |
| --- | --- |
| `Safe` | 只用原版已有的属性与接口，**不挂任何会改行为的补丁**。面板照常能用，但打开时关卡不暂停、选卡列表保持原样。**和别的 mod 冲突时先降到这一档。** |
| `Standard`（默认） | 额外挂「面板暂停（速度=0）」与「选卡界面显示所有实体蓝图」。**注意：早前版本的「接管原版控制台状态」已经删掉了**（它会连带禁掉菜单暂停与专注丢失暂停，见 §1.5） |
| `Full` | 全部，含需要**替换**官方行为的危险功能（目前只有预留的传送带卡池接管）。 |

每个危险功能还能单独 `On` / `Off`（`Auto` = 跟随等级）。
补丁**始终会挂上**，是否生效在**运行时**判断 —— 所以改等级立刻生效，不用重启，
也不会出现「补丁没挂 → 行为空洞」。

### 1.5 补丁清单

除心跳外都是**只读 / 只追加**性质：

| 补丁 | 类型 | 作用 |
| --- | --- | --- |
| `LevelController.GetGameSpeed` | postfix，只在面板打开时把返回值改成 0 | 「面板暂停」：速度倍率为 0 等效于逻辑冻结，但**不**走 `IsConsoleActive()` 那条路。走进阶路（老版本的 `DebugConsoleController.IsActive`）会连带禁掉菜单暂停与专注丢失暂停 |
| `AlmanacManager.GetOrderedContraptionsByAlmanac` | postfix，只往列表追加 | 「选卡界面显示所有实体蓝图」。该函数**全工程只有一个调用点**（选卡界面），所以作用域被精确限制在这里，不会牵连随机卡池。补全范围 = 内容库里 **`seed` 定义组**里的全部蓝图（实测 360 项），官方 80 项 → 追加 292 项 = **372 项**；末尾的追加段可以整段切掉（`AllBlueprintsPatch.ToggleShowAll()`），所以选卡面板能一键切回原版范围 |
| `MainSceneUI.SetDebugIconActive` | postfix，只是把原图标藏起来并用我们的副本代替 | 左上角那个半透明蜘蛛图标改为**面板入口**（点击开关面板）。官方这个方法每帧被 `MainSceneController.Update` 调用，挂它的 postfix 就等于免费拿到官方的显示条件 |
| `DebugManager.FillSuggestions` | **void** prefix，只往列表追加裸词 | 给 `/gc` 加 Tab 补全（`dump` / `open` / `close` / `reload`）。⚠️ 必须是 void：写 `return false` 会把官方补全全吞掉 |
| `MainSceneController.Update` | postfix，纯追加 | 面板心跳（热键、状态同步、拖动、HUD 压制） |

另外 `/gc` 走 `DebugManager.ExecuteCommand` 的 **prefix**，遵守仓库约定
「不是自己的指令一律 `return true`」—— 与 DSHCore 的 `/dshlog`、ModularCurse 的 `/curse` 三方共存不互吞。

---

## 2. 用法

```
蜘蛛图标  左上角半透明图标 → 点击开关面板（原调试图标的副本；唯一入口）
/        原版控制台（保留，未被动过）

/gc           状态
/gc dump      素材解析 + 面板构建报告 + **运行时布局 dump** + 把选择浮层开一次（排障用，需先打开面板）
/gc open|close
/gc reload    强制重新解析 UI 素材

tab          /gc 后按 Tab 可补全子指令

⚠️ 默认**不注册任何热键**（配置 `General.PanelHotkey` 留空）。
   想要热键就填一个 KeyCode 名（如 `KeyCode.F2`），填了就由本 mod 接管该键。
面板顶部是页签行（按住可拖动面板），底部是「关闭 / 原版控制台」。
面板**没有全屏遮罩**，所以面板开着也能正常点游戏。
```

### 关卡内 · 页面1「基础」

```
[x]上帝模式   [x]无限能量   [x]无限星之碎片 [x]禁用冷却
[x]后台暂停   [x]忽略大波事件 [x]自动收集      [x]隐藏HUD
背景:<>       关卡:<>       难度:<>          音乐:<>        ← 选项是手写的章节/关卡名
旗数:__       波数:__       能量上限:__      星之碎片上限:__
出怪点数:__   出怪倍数:__   出怪幂数:__      {重载}
```

- 下拉格（`<>`）点击后弹的是**自绘的选择浮层**（3 列 × 11 行一页 + 上/下页 + 取消），
  当前项高亮；120 首音乐就是 4 页。下拉格是**鼠标位置轮询**触发的，不依赖 `Button`（见 §4 坑 13）。
- 「后台暂停 / 隐藏HUD / 重载 / 背景 / 关卡」的说明写在**鼠标悬停提示**里，不占面板空间；
  悬停提示就是原版那一套（设置界面里同名效果），见 §3。
- 「后台暂停」= 原版设置里那个 `pause_on_focus_lost`（勾上时失焦自动暂停），
  同时接管面板自己的暂停（面板打开时把游戏速度压到 0）；因此面板右下的暂停按钮已移除。
- 「隐藏HUD」九项：蓝图（含传送带）、能量、星之碎片、铁镐、驱动、关卡进度条、菜单按钮、
  加速按钮，以及**拾取金币时弹出的金币数量**；取消勾选按隐藏前的实际状态恢复。
- 选卡界面（关卡内开打前的选卡）在「✕ 取消」右边多一个**「全部蓝图 / 原版蓝图」切换按钮**：
  默认「全部蓝图」（凡在内容库里注册了 `seed` 定义的实体都会列出：官方 80 项 → 372 项），
  点一下切回原版清单，再点又切回来。

### 关卡外 · 页面1「进度」

```
绿宝石:__                    | 已解锁条目（列表：**全部条目按 id 排序**，已解锁的带 ✔）
{解锁全部关卡}                | 解锁条目:<>（只列未解锁的）  {添加} {删除}
{解锁全部成就}
{移除商店已购买项}
```

- 右侧列表外面套了一层**内容框**（克隆原版 `form_hollow` 九宫格图，`raycastTarget=false`），
  列表本体再内缩 8px，所以滚动内容不会贴着框线。

### 即时生效 vs 需要重载（源码核实过）

| 项目 | 是否即时 | 依据 |
| --- | --- | --- |
| 四个作弊开关 / 忽略大波 / 自动收集 / 隐藏HUD | ✅ | 每帧或事件触发时读属性 |
| 后台暂停 | ✅ | 写的是全局选项，原版每帧就读它 |
| 旗数 / 波数 | ✅ | `CurrentFlag` / `CurrentWave` 是 public setter |
| 能量上限 / 星之碎片上限 | ✅ | `UpdateInLevelUI` 每帧刷新 |
| 音乐 | ✅ | `level.SetMusicID` + 立即 `controller.CurrentMusic = id`（后者才是真正开始播的那个） |
| 难度 | ✅ | 本 mod 自己换难度 buff |
| 出怪点数 / 倍数 / 幂数 | ✅ | `CalculateSpawnPoints` **每波重读** |
| **背景 / 关卡** | ❌ **必须重载** | Area 决定地格布局，只在 `LevelEngine.Init` 时建立 |

---

## 3. 已知边界

- **❌ 用不了 TMP 自带的下拉弹窗（已改用自绘浮层）**。实测在本环境里，TMP 生成的
  `Dropdown List` 节点**一个像素都画不出来**，但它的 `GraphicRaycaster` 照常工作 ——
  也就是「点开看不到 UI，乱点却能改值」。现场证据：节点 active、`Image.enabled=1`、
  颜色 alpha=1、`CanvasGroup.alpha=1`、`Canvas overrideSorting=1 order=30000 layer=Dialog`、
  屏幕矩形正确，而「展开前/后」截图**像素差分≈0**；且把它的画布 renderMode/worldCamera/
  planeDistance 对齐根画布（主 UI 画布是 Screen Space - Camera + UICamera）、关掉 overrideSorting、
  甚至把整个弹窗节点挪到面板 Content 末尾 —— 全部无效。
  所以下拉格的弹窗改成自绘（`Ui/PickerOverlay.cs`），见 §4 文件结构。
  顺带一个更基本的坑：下拉格**根本拿不到点击事件** —— `control.AddComponent<UnityEngine.UI.Button>()`
  在本环境**返回 null（且不报错）**，于是没有任何东西监听点击（原版 UI 类型不用 AddComponent 挂组件，
  自己造组件这条路走不通）。所以下拉格改成**鼠标位置轮询**（见 `UiFactory.TickDropdowns()`）：
  每帧算鼠标在不在格子矩形里，在就弹浮层，悬停自己画一层淡色。
- **鼠标悬停提示用的是原版组件**（`TooltipAnchor` + `TooltipHandler` + `MainSceneController.ShowTooltip`，
  与设置界面 / 图鉴 / 制品按钮同源），外观、跟随、淡入淡出全由官方代码负责，我们只负责写文本
  （`Ui/PanelTooltips.cs`；文本更新走 `SetText`，不重建组件）。
- **「背景 / 关卡」改了要点「重载」才生效**，重载会从原入口重进关卡但**保留进度**（见 §1.3）；
  换到**不同关卡 id** 时没有可复用的状态存档，只能从头开始。
- **选择浮层是分页而不是滚动**：一页 3×11 = 33 项。选项超过 33 项时分页（音乐 120 项 = 4 页）。
  之所以不用 `ScrollRect` + `Mask`，是因为那套同样没在本环境验证过（见第一条的教训）。
- **章节名是手写的**：`AreaMeta` / `MapMeta` 的元数据里根本没有名字字段，所以
  `Game/NameResolver.cs` 里内置了 `day→序章、halloween→万圣夜、dream→梦境世界、castle→辉针城、
  mausoleum→梦殿大祀庙、mausoleum_minigame→解密模式、ship→圣辇船、palace→地灵殿、mountain→妖怪之山`
  （游戏调色板里没有 `puzzle`、解密模式就用 `mausoleum_minigame`）。不认识的不猜，直接显示 id。
- **面板拖动**整段换算都在**屏幕空间**做：按下时记住鼠标屏幕点与面板中心屏幕点，
  拖动时 `新中心 = 起点中心 + (鼠标 - 起点鼠标)`，夹取范围也在屏幕坐标上判，
  最后才用一次 `ScreenPointToLocalPointInRectangle` 把结果写回 `anchoredPosition`。
  为什么不在父节点局部空间算：`anchoredPosition` 是**父节点空间**的单位，而祖先链上还叠了
  面板自身的 0.55 缩放与 canvas 的 `scaleFactor`（1.33 / 1.72）——
  早前按「屏幕像素 / 对话框局部单位」换算，实测偏差 1.82 倍（横向拖不到头、纵向能拖出屏）。
  范围限制：**纵向必须完整留在屏幕内**（比屏幕还高时居中），**横向可以拖到只剩 10px 可见**。
- **进入 / 退出关卡时面板会自动关闭**（`ConsolePanel.HandleLevelTransition()` 监听
  `LevelFacade.SwitchingLevel` 与「是否在关卡内」的翻转），免得把关卡内的页面带到关卡外；
  同时清掉下拉浮层与「全部蓝图」按钮状态（`HandleSceneChange()`）。
- **选卡清单的「全部蓝图 / 原版蓝图」切换是运行时重建列表**：写回
  `blueprintChoosePanel.choosingBlueprints` 再调 `RefreshChooseBlueprintPanelElements()`。
  不用原版的 `Refresh(IEnumerable<NamespaceID>)` 是因为从托管代码递一个 `List<T>` 给
  il2cpp 的泛型 `IEnumerable<T>` 没有隐式转换（§4 坑 14）。
- **出怪三参数受原版硬上限 500 限制**：`LogicLevelExt.CalculateSpawnPoints` 里
  `maxPoints = 500` 是写死的，所以公式 `ceil(min(pow(base, power) * mul + add, 500))` 调大会被顶住。
- **「解锁全部成就」实际会解锁全部已注册的解锁条件**（连带关卡与商店）。
  原版成就是**条件驱动**的（`AchievementMeta.Unlock` 是条件列表），没有「按成就解锁」的入口。
- **解锁 / 绿宝石 / 商店操作直接写用户存档，不可撤销**（原版没有「反解锁成就」的 API）。
- 关卡外面板的「已解锁条目」是一个**自建的单选滚动列表**（`Ui/GcListView.cs`：
  `RectMask2D` + `ScrollRect` + `VerticalLayoutGroup`，行是克隆的原版按钮节点），
  带选中高亮、自动滚到可见区、选中时启用「添加 / 删除」按钮。
- 面板**不响应 Escape**（避免与原版的暂停输入路径打架），关面板请用底部「关闭」按钮或图标。
- 面板尺寸是「设计尺寸 × 统一缩放」两层结构（`Ui/GcLayout.cs`）：设计 1000×660、
  全局 `Scale = 0.55`。实测在 1063×711 窗口下占 731×483 px、在 1379×813 下占 946×624 px
  （canvas `scaleFactor` 1.33 / 1.72）—— 所以**任何换算都不准写死屏幕尺寸或缩放比**，
  要用就问 `Screen.width/height` 与 `canvas.scaleFactor`。想整体放大/缩小**只改 `Scale`**。

---

## 4. 文件结构

```
Plugin.cs                     入口：配置 + 挂补丁
Compat/CompatConfig.cs        兼容性等级 + 单项开关（BepInEx config）
Patches/
  GameSpeedPatch.cs           面板暂停：GetGameSpeed postfix 返回 0
  AllBlueprintsPatch.cs       选卡界面显示所有实体蓝图
  GraphicConsoleIcon.cs       蜘蛛图标 → 面板入口（含图标点击回调）
  PanelTickPatch.cs           每帧心跳
Commands/PanelCommand.cs      /gc + Tab 补全
Diagnostics/
  PanelLayoutDump.cs          把运行时布局打进日志（含屏幕坐标）；选择浮层探测入口
  SelfTest.cs                 自测脚手架（自动开面板 / 自动进关卡 / 分阶段截图）
  DiagShots.cs                ScreenCapture 截图（排障时把真实画面落盘）
Ui/
  GcLayout.cs                 全部尺寸常量（改这个就能整体调大小）
  UiFinder.cs                 在活动场景里找组件（含未激活对象）
  UiSources.cs                克隆源解析与「模板架」缓存（按场景句柄）
  UiFactory.cs                克隆 / 布局 / 控件工厂 / 文本样式 / 委托防 GC
  GcListView.cs               自建单选滚动列表
  PickerOverlay.cs            自绘选择浮层（下拉格的弹窗，分页）
  PanelTooltips.cs            原版悬停提示接入（TooltipAnchor + TooltipHandler + ShowTooltip）
  PanelShell.cs               面板壳：页签行 + 内容区 + 底部按钮 + 屏幕空间拖动 + 浮层
  PanelPage.cs                IPanelPage + 轮询式 UI↔状态同步基类
  ConsolePanel.cs             总管：热键、场景/关卡切换、页面装配
Game/
  LevelFacade.cs              关卡内 API 薄封装（只写 level）
  LevelHud.cs                 隐藏 HUD 九项：五项走官方 LevelEngine 扩展，
                              进度条/菜单/加速/金币改 LevelUIPreset + level.LevelProgressVisible
  BlueprintListToggle.cs      选卡界面「全部蓝图 / 原版蓝图」按钮（克隆「取消」按钮）
  PanelPause.cs               后台暂停（全局选项）+ 面板暂停（速度=0）的单一开关
  NameResolver.cs             NamespaceID → 玩家看得懂的名字（带缓存；章节名是内置表）
  ProgressOps.cs              关卡外 API 薄封装（用户存档）
Panels/
  LevelBasicPage.cs           关卡内 页面1 基础
  GlobalProgressPage.cs       关卡外 页面1 进度
```

### 几个实现要点

- **UI 全靠克隆现场节点**：UI prefab 在 Addressables bundle 里，mod 侧没有可靠的按名加载入口；
  只有布局用的容器（`RectTransform` / `GridLayoutGroup` / `LayoutElement` / `ContentSizeFitter`）
  是现造的 —— 这些不带资源引用。**不要用现造的 `Image` 冒充原版图**：原版那堆图都挂着
  `ImageResolver`，会在 `OnEnable` 把 sprite 设回去。
- **控件同步走轮询，不走事件**：克隆控件的 `OnXxx` 是 `Il2CppSystem.Action` 属性，
  要 `DelegateSupport.ConvertDelegate` 造委托并自己持有防 GC，每次重建都要重挂。
  轮询（60fps）体感一样即时，还能处理「游戏状态被别的途径改了」的情况。
  区分「UI 变了」与「游戏状态变了」靠记录 `lastWritten`（见 `PanelPage.cs`）。
- **委托防 GC**：`IL2CPP` 侧只存裸函数指针，托管委托被回收后回调就指向已释放内存。
  按钮与对话框确认/取消的委托登记在 `UiFactory.delegateKeepAlive`（只增不减，所以数量必须少）。
- **`PanelShell` 的尺寸全部显式设定**，不依赖 prefab 原始数值，也不依赖「等一次布局才知道
  `rect.width`」—— 这样页面可以按算好的格子尺寸直接摆放。

---

## 5. 构建

```powershell
# 只构建本 mod（有游戏目录则自动部署）
.\GraphicConsole\build.ps1

# 从仓库根构建（可用 -Mod gc 只构建本 mod）
.\build.ps1 -Mod gc
.\build.ps1 -Mod gc -Pack          # 额外打包到 dist\
```

> ⚠️ 含中文的 `.ps1` **必须带 UTF-8 BOM**，否则 PowerShell 5.1 按 GBK 读会解析失败。

---

## 6. 排障：自测 + 布局 dump

面板的视觉问题（文字挤压、控件出屏、下拉框弹窗跑到看不见的地方）**从代码静态看看不出来**，
必须看运行时真实的 RectTransform。所以内建了一套自测：

```ini
# BepInEx/config/qoznos.mvz2.graphicconsole.cfg
[General]
# 空 = 关闭（默认）。menu / level / all
SelfTest = all
```

开启后（**仅排障用，平时留空**）：
1. 主界面等 4 秒 → 自动开面板 → 等布局结算 → 把布局 dump 进 `LogOutput.log`；
2. **分阶段截图**（`ScreenCapture.CaptureScreenshot`，落到 `<游戏目录>/BepInEx/_gc_shot*.png`）：
   `a` 关闭态 → 打开自绘选择浮层 → `b` 展开态。
   两张图做**像素差分**就能客观回答「这个东西到底画出来了没有」——
   当初 TMP 自带弹窗就是靠这个被证实“一个像素都没画”（差分≈0）；
3. 自动进第一关（与主菜单「开始游戏」同一条官方路线）；
4. 在关卡里再来一遍 1～3；
5. 打一行 `[SelfTest] DONE` 收尾（供脚本等待）。

布局 dump 每行以 `[GcDump]` 开头，包含：名字 / 显隐 / 锚点 / 轴心 / 位置 / 设计尺寸 /
**屏幕坐标与屏幕尺寸** / 图形状态（`Image.enabled`、色、sprite、`Canvas.sortingOrder`、`Mask`）/ 
控件类型与状态 / 布局组参数。

### 已经踩实、别再踩的坑

1. **Harmony 补丁里绝不能用 `async`/`await`**。`await Task.Delay(...)` 的续体会被丢到
   **线程池线程**（那一刻 `SynchronizationContext.Current` 为 null）→ 续体里调 `Global.Level`
   从非主线程进 il2cpp → `Fatal error 0xC0000005` 直接崩游戏（ErrorLog 里能看到
   `AsyncMethodBuilderCore.Start`/`ExecutionContextCallback`）。
   → 延时/轮询一律用**帧计数状态机**；关卡切换 Task 用每帧 `IsCompleted` 轮询
   （官方 `MainmenuController.GotoDebugStage` 就是这个套路）。
   Task 的 interop 类型是 `Il2CppSystem.Threading.Tasks.Task`。
2. **面板不能挂在「模板架」下面**。模板架是 `SetActive(false)` 的，挂进去整支面板 inactive
   → LayoutGroup 一次都不跑，子节点全停在默认 `100x100 @ 0,0`，屏幕上也看不到。
   要挂模板架的**父节点**（`UiSources.HostParent`）。
3. **`built` 标志要早于 `ShowPage()`**：`ShowPage` 开头有 `if (!IsBuilt) return;`。
   另外 `Build()` 不要自己 `SetActive(true)`，否则 `Open()` 会误判「已开着」而跳过页面装配。
4. **复选框模板要取 `LabeledToggle` 自己**，不能取 `OptionWidgets.Toggle.transform.parent`
   （那是四合一容器，克隆体没有 `UpdateWidgets` 控制器 → 每个格子里冒出原版「语言」下拉框）。
   复选框的文字在模板内部，必须自己 `SetLabelText`。
5. **TMP 的自动缩放会把字号算乱**：原版文本开着 `enableAutoSizing`，设的 `fontSize`
   会被按容器重算（实测同批标签有人 36px、有人 8px）。统一走
   `UiFactory.StyleLabel(...)`（第一件事就是关自动缩放）。
6. **`NewRect` 造的容器是默认 100×100 锚在中心** —— 列表根不 `Stretch` 就只有 100×100。
7. dump 里的「屏幕尺寸」必须走「局部角点 → 世界 → 屏幕」，直接乘 `canvas.scaleFactor`
   会漏掉祖先的 `localScale`（面板的 0.55 缩放）。
8. **克隆出来的 `Image` 可能 `enabled=0`**。面板的框体美术（`Dialog/Background`）实测克隆体
   的 `Image.enabled` 是 0 —— 节点在、sprite 对、就是不画。所以 `PanelShell.Build()` 里
   强制把它打开并打一条 warning（如果哪天官方把背景图改成默认开，这条日志就是提示）。
9. **面板拖动不能在自己的 `localScale` 上除**。`anchoredPosition` 是**父节点空间**的，
   与面板自身的 0.55 缩放无关；早前用 `scale * canvas.scaleFactor` 换算导致动得快 1/0.55 ≈ 1.82 倍。
   正确做法是 `ScreenPointToLocalPointInRectangle(父节点, ...)` 取两次采样做差。
10. **TMP 下拉弹窗在本环境渲染不出来**（见 §3 第一条）。如果以后要重新启用它，先用自测截图差分
    验证，不要相信“节点存在 + 尺寸对”就等于“看得见”。
11. **等级切换/重载不能用 `await`、也不能自己拼 `Dispose` + `InitLevel`**：前者会跑到线程池线程
    （坑 1），后者会让状态存档对不上（`HasLevelState` 看的是 stageID）。
12. **首次刷新必须强制读一次状态**。进度页早前用「两边数量都变了才重载」，结果首次刷新
    两边都是 0 → 不重载 → 列表空、下拉框还留着模板自带的「Option A」。
    现在用 `entriesLoaded` 标志强制第一次先读。
13. **原版 UI 类型上 `AddComponent<T>()` 会返回 null（且不抛异常）**。
    实测 `someUIObject.AddComponent<UnityEngine.UI.Button>()` → `null`，于是「克隆个格子 + 挂个 Button
    接点击」这条路完全不通（日志里只能看到自己印的「结果=null」）。
    原版自己的组件（`TooltipAnchor` / `TooltipHandler`）能挂，是因为它们本来就是场景里现成的
    il2cpp 组件类型。**结论：要交互就克隆一个已带该组件的原版节点，或者自己轮询鼠标位置。**
14. **il2cpp 泛型集合没有托管隐式转换**。`part.Refresh(IEnumerable<NamespaceID>)` 无法从托管
    `List<NamespaceID>` 调用（而且 il2cpp 的泛型 `IEnumerator<T>` 上没有 `MoveNext`，
    要自己转 `Il2CppSystem.Collections.IEnumerator`）。要重建选卡清单就写属性
    `choosingBlueprints` + 调 `RefreshChooseBlueprintPanelElements()`。
15. **克隆带点击的按钮后必须 `onClick.RemoveAllListeners()`**。`Instantiate` 会连
    **持久化监听器**一起复制 —— 直接克隆选卡面板的「取消」按钮，点它会真的取消选卡。
16. **`ContentProviderHelper.GetSeedDefinition(content, id)` 是「取不到就新建」**，不是查询。
    用它枚举「所有实体蓝图」会把 341 个实体（含粒子 / 特效）全部变成蓝图。
    要真正的蓝图清单就遍历定义组：`content.GetDefinitions<SeedDefinition>("seed")`（实测 360 项）。
17. **关卡进度条的可见性源头是 `level.LevelProgressVisible`**，`LevelUIPreset.SetProgressBarVisible`
    只是它的执行手（`LevelController_ProgressBar.cs` 里 `Awake_ProgressBar()` 先关，
    之后只在 UI 初始化 / 读档的 `RefreshUIAtLevelStart()` 里按属性值设一次）。
    所以隐藏进度条要**两个都压**，并且还原时按「意图值 + 实际可见」两个快照分别还原
    （否则会把关卡本来就没显示的进度条强行打开）。
18. **截图不一定拿得到真实帧**。`ScreenCapture.CaptureScreenshot` 偶发落下一张接近空白/
    黑屏的图（同一轮里 a/b 两张差分 100% 就是这种）。判图之前先看「两张是否都是正常画面」，
    或者把关键结论落到**日志**（布局 / 状态 dump）上，不要只靠截图。
