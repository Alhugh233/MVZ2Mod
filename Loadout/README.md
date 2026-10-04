# MVZ2 Loadout（图形化控制台面板）

用可视化面板替代原版调试控制台的输入窗口。测试工具，**最大兼容性优先**。

- 适配：MVZ2 0.7.0 test-4 / Windows x86 / IL2CPP metadata v31.1 / BepInEx be.788
- 前置：**硬依赖 [DSHCore](../DSHCore/README.md)**（全部 UI 都建立在 `DSHCore.Ui` 之上）
- 需求来源：仓库根的 `Loadout.txt`（符号约定：`[]`复选框 `__`输入框 `<>`带补齐的输入框 `{}`按钮 `|...|`内容列表）
- 设计论证：[`DSHCore/UI_GUIDE.md`](../DSHCore/UI_GUIDE.md)（怎么做）、[`DSHCore/UI_RESEARCH.md`](../DSHCore/UI_RESEARCH.md)（为什么）

---

## 1. 设计取向

| 取向 | 含义 |
| --- | --- |
| **零注入** | 不注入 il2cpp 类型、不注册任何 Definition、不注册自己的 ModInfo。全部功能落在「读写原版已有属性 / 调用原版已有接口 / 克隆原版已有 UI」上 → 直接绕开 metadata v31 下注入类的全部已知坑 |
| **UI 只用 DSHCore.Ui** | 面板、行、控件、列表、提示**全部是原版 prefab 的克隆体**。本 mod **不算坐标、不定尺寸、不自绘控件**（唯一的尺寸是对话框宽度那一个比例，见 §2） |
| **补丁只读 / 只追加** | 见 §4。没有一条补丁替换官方行为 |
| **只写 level，绝不写全局** | 关卡内面板只改 level 级属性（与「重载」天然可逆）；「后台暂停」是唯一写全局选项的项，因为它就是原版设置界面里那个开关 |

需求 ↔ 复用的原版资产：

| 需求 | 复用的原版资产 |
| --- | --- |
| 无限能量 / 无限星之碎片 / 禁用冷却 / 上帝模式 | 原版 `/cheat` 用的四个**关卡级 debug buff**（`VanillaBuffID.Level.debugXxx`） |
| 忽略大波事件 / 自动收集 | 原版关卡属性（`LogicLevelProps`） |
| 后台暂停 | 原版**全局**选项 `pause_on_focus_lost`（与设置界面同一个开关） |
| 面板暂停（速度=0） | postfix 原版 `LevelController.GetGameSpeed()` 返回 0 |
| 旗数 / 波数 / 能量上限 / 星之碎片上限 / 出怪三参数 | 原版关卡属性（**都写 level 实例，不写 StageDefinition**） |
| 隐藏 HUD 九项 | 前五项走官方 `LevelEngine` 扩展；后四项（进度条 / 菜单按钮 / 加速按钮 / 金币数量）改 `LevelUIPreset` + `level.LevelProgressVisible` |
| 重载 | 官方 `LevelController.ReloadLevel()`（+ 先 `LevelManager.SaveLevel()`） |
| 面板框体 / 标题 / 正文 / 按钮 / 页签 | 克隆官方 `CustomDialog`（+ 它的 `ButtonRow` 做页签） |
| 复选框 / 滑条 / 单按钮 | 克隆官方 `OptionWidgets`（四合一设置项行），用 `UiOptionRow` 指定类型 |
| 输入框 | 克隆「输入名字」对话框的 `TMP_InputField` |
| 补齐弹窗 | 克隆**原版控制台**的 `DebugConsole.prefab/Bottom/AutoComplete` |
| 可滚动列表 | 克隆官方 `Scroll View`（`UiList`），行是官方 `ButtonRow` |
| 悬停提示 | 官方 `TooltipAnchor` + `TooltipHandler` + `MainSceneController.ShowTooltip`（设置界面/图鉴/制品按钮同一套） |

---

## 2. 面板是怎么搭的（DSHCore.Ui 的用法）

```
UiSkin.Clone(Dialog)                       ← 面板壳 = 官方"标题+正文+若干按钮"对话框
  Root        (VerticalLayoutGroup)        ← 内容宿主：页面往这里插行
    TitleArea                              ← 拖动把手（按住标题行/页签行可拖）
    Tabs        (ButtonRow 克隆)            ← 页签：当前页按钮置灰
    Desc                                   ← 页说明（字号降到 14）
    行1..行n    (UiGridRow / UiList)        ← 页面自己插的内容
    Buttons     (官方按钮区)                ← 只有「关闭」
```

- **唯一的尺寸**：`Root.sizeDelta.x = 画布宽 × 0.94`。原因：原版对话框宽度固定 **400 单位**
  （`ContentSizeFitter` 只做 VerticalFit），而画布约 **800 单位**宽，4 列网格放不下。
  框体美术是拉伸锚点，标题/正文/按钮都会自动跟随。宽度按画布折算，不写死像素
  （与 `UiList.SuggestHeight` 同思路）。
- **网格行**：一行 = 一个横向 `LayoutGroup` 容器 + 若干**等分单元格**。
  容器素材优先用**原版设置面板里那个「一行三个滑条」的容器**
  （`OptionsDialog/Dialog/MainElements/Sliders/Sliders1`），拿不到才退回
  `UiHost.CreateNode + AddComponent<HorizontalLayoutGroup>`（抓到哪条会打进日志）。
  单元格的尺寸契约是 `LayoutElement { preferredWidth = 0, flexibleWidth = 1 }` —— 父布局组按 flex 等分。
  纵向容器用同一支里的 `MainElements/Sliders`。
- **单元格**：
  - 复选框/滑条/按钮 → `UiOptionRow`（官方四合一设置项行，自带标签与提示锚点）；
  - `<>` / 数字 → **标签（官方正文素材）+ 输入框**，装在同一个横向容器里。
    标签不设 `LayoutElement`（TMP 自己实现 `ILayoutElement`，preferred 宽度就是文字宽度），
    输入框吃剩余宽度。
- **对齐判据**：4 列会把原版控件挤窄（它们本来是按整行宽度设计的），所以自测里有一条硬判据：
  **每个单元格「最长标签需要的宽度 vs 格子宽」**（见 §10）。溢出了 dump 会直接标 `**挤（会溢出）**`。

---

## 3. 补齐输入框（`<>` 的替代实现）

**为什么不用下拉框**：原版 `TMP_Dropdown` 的展开列表在本游戏里**一个像素都画不出来**
（DSHCore 实测过并声明不提供替代实现）。所以「从一组里选一个」改成
**输入框 + 原版控制台那种候选弹窗**，这也正好符合需求里「`<>` 带补齐的输入框」这个符号约定。

| 项 | 做法 |
| --- | --- |
| 弹窗素材 | 克隆原版控制台的 `DebugConsole.prefab/Bottom/AutoComplete`（`ElementList` 候选项 = 图标+文字+选中态，外加上下翻页箭头）。**它是 `ElementList`，不是 TMP 下拉框**，所以没有"画不出来"的问题 |
| 弹窗位置 | **浮层**：挂在对话框根节点（它没有布局组）下、标成 `ignoreLayout`，位置按「浮层的左上角对齐输入框的左下角」算 —— 所以它不会参与面板的纵向排版、**不会把面板顶高**。因为和输入框同属一个父节点，拖面板时浮层跟着走 |
| 弹窗尺寸 | 高 = **固定 6 行**（需求）+ 上下箭头 + 原版弹窗自己的内边距/间距；宽 = 最长候选文字（实测）+ 图标宽 + 内边距，上限不超过面板宽度。实例：`255x215`、屏 `[119,79 → 441,350]`，输入框屏 `[119,352 → 262,403]` —— 左边缘对齐、顶边贴在输入框下边 2px 处 |
| ⚠ 弹窗不吃尺寸 | 那个面板的根节点带 **`LayoutElement.ignoreLayout = true`**（原版手动定位的浮层）→ 搬进我们的布局组后**布局组根本不给它尺寸**，父子全被算成 0，候选文字被自动换行断成一条竖线。`UiContainer.Fit` / `FillParent` 现在都会显式关掉它；dump 里会打 `⚠不参与布局` |
| 候选行尺寸 | 原版候选项是给**短单词**设计的（模板行 82x20、文字节点写死 72）→ 补成「行宽弹性 + 行高取素材自己的高」，行内文字 `flexibleWidth=1` 且 `enableWordWrapping=false`（超出用省略号） |
| 框里显示什么 | **只显示名字**（没名字才显示 id）。带 id 的完整文本（`名字  (id)`）只在候选列表里出现 —— 4 格一行时每格只有 163，而「序章  (mvz2:prologue)」需要 199，放进去会被裁。输入匹配仍然三路都认 |
| 每页条数 | **固定 6 行**（需求）。外框高度就按 6 行算，末页不足也照旧（不改高度、不跳） |
| 匹配 | **id / 当前语言名 / 英文名** 三路；**前缀优先、包含兜底、大小写不敏感**；空输入 = 列全部 → §3.1 |
| 键位 | `↑↓` 选择（翻页自动跟随）、`Tab`/`Enter` 补全并提交、`Esc` 还原成编辑前的值、**鼠标点候选行也能补全** |
| 候选行对齐 | 原版控制台是**居中**的；需求要左对齐 → `PatchItems` 里显式改 `alignment = TextAlignmentOptions.Left`（日志会打 `对齐=Left`） |
| 翻页 | **逐页走、不循环**：页 = ⌊起点 / 每页数⌋，夹在 `[0, 页数-1]`。早期是「起点 += 页 × 每页数」+“越界就绕到另一端”，于是一圈一页地乱跳；而且那时箭头在 `onClick` 与矩形命中里各做一次，一次点击真的会翻**两页**。现在只留箭头的 `onClick`（还能自动尊重“不可用”状态） |
| 鼠标与焦点 | 浮层的关/不关**只看鼠标位置**：点在浮层内或本格内不关，点别处立刻关。刻意与“输入框失焦”脱钩 —— 点箭头/候选项都会让框失焦，用失焦当判据要么误关、要么一旦被跳过就再也关不掉（两条都实测报过）。自测里有 `UiCompleter.DebugCloseSimulation()` 盯它 |
| 候选行不吃鼠标 | 候选行里的 `Toggle` 会把点击自己翻一次，与我们的命中处理合成“一次点击选两遍”（两次提交/两次应用）→ 给行加 `CanvasGroup.blocksRaycasts=false`，点击全靠矩形命中 |
| 提交 | 失焦或回车后把文本解析成 id；**解析不出来就回退原值**（不写坏数据）；**值没变则什么都不做** —— 点一下输入框再点别处也会走提交，对音乐这种有副作用的项是灾难（音乐会被从头重播） |

### 3.1 名称索引（中英文 + id）

- 名称一律走游戏自己的本地化访问器（`ResourceManager.GetEntityName` / `GetBlueprintName` /
  `GetArtifactName` / `GetMusicName` / `LevelManager.GetStageName` / `IGlobalGame.GetDifficultyName`）。
- **英文名**：游戏的语言包是 gettext 结构 —— PO 里的 msgid 就是**源语言（zh-Hans）的文本**，
  所以 `LanguageManager.GetLocalizedStringParticular(context, 中文名, "en-US")` 就是英文名。
  游戏跑在中文（源语言，默认）时恒成立；跑在英文时英文名与当前语言名相同 ——
  此时「按 id」与「按当前语言名」匹配仍然可用。
- **背景（Area）没有名字**：原版 `AreaMeta` / `MapMeta` 根本没有 name 字段，也没有本地化访问器，
  所以背景只有内置章节表（`day/halloween/dream/...`），**英文名一律拿不到**。
  这不是漏做，是原版数据里就没有。
- 索引按类别惰性建立并缓存；语言切换会自动重建。
- **空索引不缓存**：解锁条件来自资源侧，在标题界面建页时可能还没加载完（拿到 0 项）。
  把 0 项的结果缓存住的话，进关后再也不会重建（实测就是这样一直 0 项）——
  现在空结果不入缓存，进度页在索引为空时自愈重取。同样地，**补齐字段也不绑死建页时的索引实例**，
  而是在用的时候按类别重新取（否则某个格子会永远是 0 个候选）。

---

## 4. 兼容性等级与补丁清单

配置在 `BepInEx/config/qoznos.mvz2.loadout.cfg`：

| 等级 | 含义 |
| --- | --- |
| `Safe` | 只用原版已有的属性与接口，**不挂任何会改行为的补丁**。面板照常能用，但打开时关卡不暂停、选卡列表保持原样、图标不接管。**和别的 mod 冲突时先降到这一档。** |
| `Standard`（默认） | 额外：① 面板打开时速度=0；② 选卡界面显示所有实体蓝图（只追加）；③ 左上角蜘蛛图标作为面板入口 |
| `Full` | 全部，含需要**替换**官方行为的危险功能（目前只有预留的传送带卡池接管） |

每个危险功能还能单独 `Auto` / `On` / `Off`。补丁**始终会挂上**，是否生效在**运行时**判断 ——
所以改等级立刻生效、不用重启，也不会出现「补丁没挂 → 行为空洞」。

| 补丁 | 类型 | 作用 |
| --- | --- | --- |
| `LevelController.GetGameSpeed` | postfix，只在面板打开时把返回值改成 0 | 「面板暂停」。⚠️ 不走 `DebugConsoleController.IsActive` 那条路 —— 那会让 `IsGameRunning()` 变 false，连带禁掉菜单暂停、专注丢失暂停与键盘/草坪输入 |
| `AlmanacManager.GetOrderedContraptionsByAlmanac` | postfix，只往列表追加 | 「选卡界面显示所有实体蓝图」。该函数**全工程只有一个调用点**（选卡界面），所以作用域被精确限制在这里，不牵连随机卡池。补全范围 = 内容库 `seed` 定义组里的全部蓝图，末尾那段可整段切掉，所以选卡界面上能一键切回原版范围 |
| `MainSceneUI.SetDebugIconActive` | postfix，把原图标藏起来、用我们的副本替代 | 左上角图标改为面板入口。官方**每帧**都在调它，所以免费拿到官方的显示条件 |
| `DebugManager.FillSuggestions` | **void** prefix，只往列表追加裸词 | `/loadout` 的 Tab 补全。⚠️ 必须 void：返回 false 会把官方补全全吞掉且不报错 |
| `MainSceneController.Update` | postfix，纯追加 | 面板心跳（热键 / 拖动 / HUD 压制 / 补齐键盘 / 关卡切换自动关面板 / 自测状态机） |

`/loadout` 走 `DebugManager.ExecuteCommand` 的 **prefix**，遵守仓库约定
「不是自己的指令一律 `return true`」—— 与 DSHCore 的 `/dshlog`、ModularCurse 的 `/curse` 三方共存不互吞。

---

## 5. 用法

```
左上角蜘蛛图标   点击开关面板（原版调试图标的副本；唯一图形入口）
/loadout         状态
/loadout open | close
/loadout page <序号|名字>
/loadout dump    逐页装配 + 运行时布局 dump + 状态报告（排障主力）
/loadout report  只看状态（素材来源 / 名称索引 / 各子系统）
/loadout archive 转储游戏「对话档案」面板的结构（进度页的搜索框/列表就是克隆它的）
/loadout reload  强制重新解析 UI 素材并清空名称索引（切场景后素材失效时用）

tab              /loadout 后按 Tab 可补全子指令

⚠️ 默认**不注册任何热键**（配置 General.PanelHotkey 留空）。
   想要热键就填一个 KeyCode 名（如 F5），填了就由本 mod 接管该键。
   原版控制台仍然是「/」键 —— 本 mod 没有动它。
面板没有全屏遮罩（对话框壳自带的 **Blocker 已被关掉**），所以面板开着也能正常点游戏。
**拖动**：面板可交互控件（按钮 / 开关 / 输入框 / 列表滚动区）之外的**任何位置**都能按住拖。
进出关卡会自动关闭面板。
```

### 关卡内 · 页1「基础」（5 行 × 4 列 = 需求里的 5*4 网格）

```
[x]上帝模式  [x]无限能量   [x]无限星之碎片 [x]禁用冷却
[x]后台暂停  [x]忽略大波事件 [x]自动收集     [x]隐藏HUD
背景:<>      关卡:<>       难度:<>         音乐:<>
旗数:__      波数:__       能量上限:__     星之碎片上限:__
出怪点数:__  出怪倍数:__   出怪幂数:__     {重载}
```

- **4 列等宽**是算出来的（行宽 - 内边距 - 格间距 ÷ 4），不是靠 `flexibleWidth` 让布局组自己分：
  布局组分配剩余空间时是按「各自的最小宽度」占位，而格子的最小宽度 = 里面标签的最小宽度，
  结果就是「旗数」那格 145、「星之碎片上限」那格 198（实测）—— 四列错位。
- **列内对齐**：单元格的左右内边距清零，所以标签左边缘 = 格子左边缘 = 同列原版开关的左边缘；
  输入框右边缘 = 格子右边缘 = 同列开关的右边缘。
- **字号**：标签取原版开关行自己的字号（`UiKit.ControlFontSize`，实测 16），
  不是面板自定义的 14 —— “同列文字字号一致”只能是我们向原版控件对齐。
- `<>` 是**补齐输入框**：点进去就列候选，`↑↓` 选、`Tab`/`Enter` 补全；直接敲 id 也行。
- 「后台暂停 / 隐藏HUD / 重载 / 背景 / 关卡 / 难度 / 各数值」的说明写在**悬停提示**里（原版那一套），不占面板空间。
- **即时生效 vs 需要重载**：需要重载的是 **背景 / 关卡 / 难度**（选了之后「重载」按钮文字**变黄**、并带上一个 `*`）；
  其余全部即时。选完不重载也不会“变回去”—— 待重载的格子会保留你选的值，重开面板/翻页时才重新读游戏里的值。
- **「重载」到底做了什么**（这不是简单地重进一下）：
  - 关卡状态的存读路径是 `<用户存档>/<命名空间>/level/<关卡>.lvl`，而文件**内部**还写着它自己的
    `stageDefinitionID` / `areaDefinitionID`（`LevelEngine.CreateFromSerializable` 用的就是文件里那两个值）。
    所以只改「背景」时命中旧存档、里面的 area 还是旧的 → **背景换不掉**；只改「关卡」时文件名对不上 → 一切从头。
  - 现在的做法：先 `SaveLevel()`，再把这份状态**复制到目标关卡的路径**并改写文件内部的
    关卡/背景（需要时连 `difficulty`）id，最后才 `SetStartStage + ReloadLevel`。
    也就是「让游戏把当前关卡的存档当成目标关卡来解析」（前作存档修改器干的事）。
    原关卡的存档不会被破坏；目标存档已存在时先备份成 `*.lvl.bak`。
  - **难度**是唯一会写全局 options 的项，而且非写不可：官方的难度来源就是那个设置
    （`LevelController.UpdateDifficulty()` 每次开局都会用它覆写 `level.Difficulty`），
    只写 level 属性会在下一次开局被改回去（实测症状：改了立刻变回来）。
    写入只发生在你**明确点「重载」**时（与官方设置界面里那个难度按钮同一条路）。

### 关卡外 · 页1「进度」

```
绿宝石:__          | 搜索框（实时过滤；敲 [x] / [ ] 只看已解锁 / 未解锁）
{解锁全部关卡}      | |解锁条目列表（可多选，行文本 [x] mvz2:xxx = 已解锁）|
{解锁全部成就}      | {解锁} {锁定}
{移除商店已购买项}  |
```

- **列表与搜索框都是克隆游戏「对话档案」索引页左侧那一块**（`ArchiveProbe` → `UiArchiveList` / `UiSearchBox`）：
  外观、滚动、多选交互全部是原版素材与行为（`ArchiveTagItem` = `Toggle` + 文字）。见 §6。
- **条目 = 全部解锁条件的定义侧**（实测 155 条），不是“已解锁的那些” ——
  行首的 `[x]` / `[ ]` 才是解锁状态，列表必须同时包含两类，否则“锁定”无从下手。
- 「解锁 / 锁定」作用于**勾选**的条目（没勾选时两个按钮都置灰）；勾选 = 选中，不表示解锁状态。
- **勾选只能由我们改**：行里的 `Toggle` 是原版控件、点它会自己翻一次，与我们的命中处理合成“一次点击翻两遍”
  —— 表现就是「点了条目该行不变绿」（数据其实已经勾上），而两个按钮于是对不上。
  现在给行加了 `CanvasGroup.blocksRaycasts = false`：只挡射线（不改颜色/透明度），
  点击完全由 `UiArchiveList.Tick` 的矩形命中处理，所见即所得。
- ⚠️ 这些操作**直接写用户存档，不可撤销**。原版成就是条件驱动的（`AchievementMeta.Unlock` 是一串条件），
  没有「按成就解锁」的入口，所以「解锁全部成就」实际做的是**解锁全部已注册的解锁条件**（含关卡与商店）。
- 左侧三个批量按钮**不再逐条重算**：官方的 `SaveManager.Unlock/Relock` 内部每次都会调
  `EvaluateUnlocks`（遍历所有实体/制品/商店/诅咒/成就定义的全量重算），155 条就是重算 155 遍 ——
  这是之前“点一下就卡死”的根因。现在批内直写存档数据，批末只做一次重算 + 一次落盘（见 §9 的 `BeginBatch/EndBatch`）。
- ⚠️ 批末借来跑重算的那个 id **必须是“本来就已解锁”的**：早期实现记的是“批内第一个写过的 id”，
  对解锁批无害，但对**锁定批**就等于把刚锁掉的第一条又 `Unlock` 回去 ——
  实测症状就是「锁定按钮对勾选的第一条不生效，而解锁按钮看起来正常」。自测里有一条专门盯它
  （`ProgressOps.DebugBatchLockTest`）。

### 关卡内 · 页2「蓝图」（**只在自选卡模式**出现）

```
|玩家携带的蓝图，横排（左→右），可点选|         ← 整行宽的内容框（灰底 + 滚轮横滚）
费用:__  冷却(秒):__  {冷却预设：短}      {重置冷却}
血量:__  [x]模仿者  [x]逆阵营放置  [x]白天不睡觉
[x]无条件放置  {调整选卡}  {清空卡槽}  {传送带模式}
```

- 四行都是 **4 格**（5×4 网格对齐）。本来第二行的「卡槽数」被需求去掉了：
  卡槽数现在由「调整选卡」的卡片张数决定（<c>ApplyPicked</c> 里 <c>SetSlotCount</c>）
  以及「清空卡槽」负责，单独一个输入框是冗余的。
- 第一行是**卡组镜像**：一个灰色的**内容框**（`BlueprintStrip`），里面从左到右把玩家携带的蓝图排出来。
  - 框体 = 克隆原版可滚动容器（`UiSkinPart.ScrollView`，灰底 + 矩形裁剪视口 + `ScrollRect`）；
    **垫了一层半透明深色底**（`DSH_Fill`）—— 原版那层背景贴图是 `form_hollow`（一个空框），
    不一定有填充，需求要的是「一个灰色底的矩形」。
  - 卡片 = 克隆原版「已选蓝图」条（`ChosenBlueprints`）当**卡片工厂**，卡面是原版 `Blueprint` 节点、
    数据走 `Main.ResourceManager.GetBlueprintViewData(id, IsEndless, commandBlock)` ——
    **和关卡里蓝图掉落物是同一个入口**，所以底框/图标/费用/充能圈全都一致。
  - **左对齐**（不是居中）：第 i 张卡的左边缘 = `2 + i × 48`（内边距 2，卡片与边框贴合）；纵向居中。
    框体高 76 = 卡片 72 + 上下各 2。
  - ⚠️ 建卡时**绝不能写 `SetRecharge(1)`**：那层 `Recharge` 的贴图是 `card_curtain`（alpha 0.5，
    `fillAmount` = 充能量），`fill=1` 会把卡面整张盖住 —— 看上去就是「卡永远是灰的/洗白的」。
    未选中的卡一律 `SetRecharge(0)`（帷幕收起），与原版未选中的卡逐层一致。
  - **鼠标滚轮横滚**：把 `ScrollRect` 的 `horizontal` 打开、`vertical` 关掉（Unity 在「只横」时
    会把滚轮的 y 分量映射成横向），灵敏度 `-40`；框体上留了一层透明 `Image` 接射线。
    ⚠️ **灵敏度是负数**（不是 40）：那个分支算的是 `velocity.x = scrollDelta.y * sensitivity`，
    所以符号决定滚轮往前推时卡是往左还是往右走。觉得方向反了就改这一个符号。
    内容比视口窄时原版的 `Clamped` 会把它拴在左边 —— 正好就是左对齐。
    没有卡槽时**框体自己就是那个空框**（不画「深色空卡」），空卡槽也不画。
  - **悬停提示**（`BlueprintTooltip`）：鼠标停在卡上显示「名字 + 说明」，**两页的第一行 + 选卡浮层的
    网格三处都铺**。原版那套提示是 `LevelBlueprintChooseController_TooltipSources` **按序号**注册的，
    克隆出来的卡拿不到；所以自己挂一个 `TooltipHandler`（`UiTooltip.Attach`）。
    ⚠️ **说明取不到就退化成显示蓝图 id**（`mvz2:iron_helmetted_zombie`）：原版 `GetBlueprintTooltip`
    对「没定义提示」的蓝图返回空串，需求要求这时候显示 id —— 这条对**原版蓝图也一样生效**。
    自测里 `蓝图提示：铺了 N 次  最近=…` 就是它的计数（实测 411 次）。
  - ⚠️ **点选必须走 `IPanelPage.Frame()`（每帧）而不是 `Tick()`（30 帧一次）**：
    `Input.GetMouseButtonDown(0)` 只在**一帧**为真，而 `Tick` 是 30 帧一次的轮询 ——
    放里面就会变成「要连点几十下才中一次」（实测症状：镜像框里的卡得点好几次才选中）。
    自测里会打 `点击通路：Frame（每帧）=N  Tick（30 帧一次）=M`，两者一比就看出来了。
- 点条上的某张卡 = **单选**（再点同一张取消）：点中哪张就只灰哪张（原版 `SetDisabled`，
  与原版选卡面板里「已选的卡」同一种表现），下方的字段随即**填上它的当前值**。
  **选中集只在本页有效**（需求指定不持久化）；一个都没选时下面的字段作用在**全部卡**上
  （测试工具里这比“什么都不做”有用）。
- 「费用 / 冷却(秒) / 血量」作用于选中的卡。
  - **冷却值 = 上限值**：写 `RECHARGE_SPEED`，再 `SetStartRecharge(false)` + `FullRecharge()`
    （需求 1：不管初始值、把当前值顶到上限）。
  - 折算公式：`秒 = 上限tick / (30 × 关卡速度 × 卡速度)`；面板显示与输入的都是**折算后**的秒数，
    所以「输入秒数 = 应用秒数 × 修正系数」（修正系数写在悬停提示里，见需求 4）。
  - 输入值恰好等于某个预设时**直接切预设**；否则挂到「短」（225t）上，
    把基础速度写成 `225 / (30 × 关卡速度 × 秒数 × 卡速度)`。预设的顺序是 无 / 短 / 长 / 很长，
    匹配不上时显示「未定义」。
  - 关卡冷却速度异常（≥1000，例如开了「禁用冷却」）时，这一格**置灰**，悬停提示换成说明（需求 3）。
- {调整选卡} 打开的是**选卡浮层**（`BlueprintPickWindow`）：
  整体**克隆原版命令方块选卡面板**（`CommandBlockPanelStandalone`，800x600），于是
  五行八列的内容框、翻页按钮、`1/10` 页码全是原版的。可见框体就是在面板里居中的那条竖列
  （原版 `BlueprintListStandalone` 400x470），内部三段固定：**标题行 88 + 网格块 362 + 下排 58**；
  尺寸账是 `88 + 362 + 58 = 508`，所以面板高度从 470 **长到 508**（网格 `386x362` **一点不动**：
  它里面 `384x360` 的 8列x48 × 5行x72 是**零余量**的，压网格会让卡片溢出）。
  宽度保持**原版的 400**，里面那两样（都是 386）左右各留 7 的边距。三段各自的归属：
  - **标题行（400x88）** = **镜像框**（386x76，`BlueprintStrip`，与第一行同一个实现）
    **纵向上居在标题行里** → 距框体下边各 6（实测：标题行 屏 y[596,709]、镜像框 屏 y[604,701]）。
    原版这里只有一个标题，`HideStrayTexts()` 把标题（`选择蓝图复制`）和自带按钮（`返回`）藏掉之后
    就是一块空白，镜像框正好摆进去。**宽度与下面的选卡网格块同为 386** —— 实测两者屏幕矩形都是
    `x[265,759]`，左右完全对齐。点里面的卡 = 把那张放回去。
  - **网格块（416 里居中的 386x362）** = 五行八列蓝图，**始终不置灰**：置灰（`disabled`）与
    洗白（`recharge=1` 帷幕）都是「不能选 / 已经用完」的语义，而这里每一张都还能再选一张；
    已选过的也不另做标记（保持与原版选卡界面视觉一致）。
  - **下排（416x58）** = 翻页区（左：翻页按钮；右：页码，都是从框体左边算）
    ＋ **确认 / 取消**（各 95x40）。两个按钮**均分了原版 `CancelButton` 的 200 宽位置**
    （从框体左边算 `[100,195]` 与 `[205,300]`，原版那个按钮已被 `HideStrayTexts` 藏掉），
    所以它们与翻页按钮**同一行**、也不与页码重叠（实测：翻页 `[271,323]`、确认 `[374,495]`、
    取消 `[529,650]`、页码文字 ≈`[663,766]`）。
- **飞入动画：已去掉**（`BlueprintFlyer.cs` 已删）。原来点网格里的一张会飞进镜像框，
  但玩家视角**看不到**（浮层是满屏遮罩、动画在遮罩下播，且落地前的卡本来就在框外），
  留着只会让「点了以后卡怎么还没多」变成疑点 —— 现在点一下就**立刻**进框。
  两条当时踩出来的教训仍然有效，改动这块前先看：
  - ⚠️ **`MovingBlueprint.SetBlueprint` 会把传进去的卡收进自己的节点**：直接交网格里那张，
    网格上对应的卡当场消失（实测症状：点一下网格，那张蓝图就从选卡框里没了）。
    将来要恢复动画，只能交**副本**。
  - ⚠️ **落点必须先在可见区内**：镜像框宽 386 只装得下 8 张卡（386 / 48 ≈ 8.04），
    卡一满、新加的那张就落到框外了。实测 10 张时第 10 格的屏幕矩形是 `x[754,815]`，
    而框体右边界只到 **759** —— 被视口裁掉，看起来就是「点了没反应 / 数量没变」。
    所以**每次刷新完都要 `ScrollToEnd()`**（内容比视口窄时它自己会归位到 0），
    这样玩家看到的永远是「最新加的那张」。
- 选卡**允许同一种蓝图带多张**：网格里点一下就是「再加一张」，要减少就在已选框里把那一张点掉
  （那里是按张数算的）。
- 在后面的页选了卡**不会跳回第一页**（`RefreshGrid(resetPage)` 只在打开时为 true）。
- 按钮文案**每次打开都重铺**（`ApplyButtonLabels`），而不是只在建的时候写一次。
- {传送带模式} 把这一关切成传送带（`LevelEngine.SetConveyorMode(true)`），
  并**立刻**把当前页换到传送带那一套（`LoadoutPanel.SyncPageSet()`；页名两套一样，所以停在「蓝图」页）。
  除了按钮回调里这一句，`LoadoutPanel.Tick` 里**每帧也调一次** `SyncPageSet()` —— 集合没变时它第一行就返回，
  但它堵住了「面板开着的时候页集合变了、页签却没换」这条缝（自测里有一条专门盯它：
  面板开着、只改游戏模式、不手动同步 → 判据 `立即换页=OK`）。

### 关卡内 · 页2「蓝图」（**只在传送带模式**出现）

```
|卡池里的蓝图，横排（左→右），可点选|          ← 整行宽的内容框
权重:__  数量限制:__  传送带速度:__  当前概率:1000/6000 = 16.7%
血量:__  [x]模仿者  [x]逆阵营放置  [x]白天不睡觉
[x]无条件放置  {清空卡槽}  {调整卡池}  {自选卡模式}
```

- 四行都是 **4 格**。原来的「当前总权重」并进了「当前概率」那一格：需求要的是
  **显示计算公式**（`权重 / 总权重 = 概率`），所以那一格是只读的，文本就是 `1000/6000 = 16.7%`。
- 第一行/第三行与蓝图页完全同构（同一个 `BlueprintStrip`、同一套单选与标记）。

- **权重 = `ConveyorPoolEntry.Count`（初始份数），数量限制 = `MinCount`（补货下限）** ——
  原版抽卡就是按「剩余份数」加权的（`DrawConveyorSeed` 里 `weights[i] = 剩余份数`），所以份数即权重。
  当前概率 = 该卡权重 ÷ 总权重。
- 关卡没有卡池时**就地填一份**：拿当前卡槽里的卡，权重全 1000、下限 1（需求原文）。
- {调整卡池} 复用**选蓝图那套选择窗口**（需求 10）：已选 = 现有卡池，确定后「保留仍在选中的项
  （权重/下限不动）＋ 新勾的项按 1000/1 追加」。
- 第一行与蓝图页一样是**内容框**（`BlueprintStrip`），同样是**单选置灰**；
  {自选卡模式} 切回自选卡时也会**立刻**把页面换回那一套（同 `SyncPageSet()`）。
- 《模仿者》与《逆阵营放置》是**按蓝图 id** 记的标记，不是记在某一格上 ——
  因为传送带的卡是中途从卡池里抽出来的（`ConveyorSeedPack`），根本没有“那一格”可挂。
  模仿者标记由面板每帧同步到卡槽里的 SeedPack 上（`BlueprintOps.TickFlags`），
  逆阵营标记由 `ReverseFactionPatch` 在生成时读。

> ⚠️ **写卡池这一步有个 interop 坑，务必照现在的写法来。**
> `LogicStageProps.CONVEYOR_POOL` 的属性类型在 interop 里是
> `Il2CppReferenceArray<IConveyorPoolEntry>`（元素是**接口**），而生成器把 il2cpp 接口**生成成了类**
> （`Il2CppObjectBase` 子类 + 指针构造器），代理类型的 .NET 接口列表因此是空的 ——
> 「具体类型 → 接口」编译期 CS0029、运行期 `Unable to cast object of type
> 'MVZ2.Metas.ConveyorPoolEntry' to type '...IConveyorPoolEntry'`。
> 正确做法：在 il2cpp 里 `new ConveyorPoolEntry(...)`，再用指针包一层接口视图
> `new IConveyorPoolEntry(entry.Pointer)`（与 `new ITooltipTarget(obj.Pointer)` 同一套路）。
> 自测里有一条会**读回关卡属性**核对（`关卡属性读回=6 项/总权重=6000`），别把这条判据删了。

#### 本轮新增：**语义已确认，全部接线**

这些格子早先只做布局；语义确认后现在都是真功能（自测里各有判据，见下表右列）。

> **作用域规则（两页共用）**：除**「无条件放置」是关卡级全局**以外，
> 其余每一项都**只对镜像框里当前选中的那一张**生效（`Targets()` 只返回选中项）。
> 一个都没选时字段显示 `-` 并**置灰**，悬停提示换成一句说明。
> 第一行那个镜像框就是「选中」的唯一入口（单选置灰，再点同一张取消）。
> 实测：`卡槽=6 镜像格=6 选中=无 作用目标=0` ←未选中；点第 2 格 → `选中=槽1 作用目标=1`。

| 项 | 实现（已接线） | 自测判据 |
| --- | --- | --- |
| 血量 `__` | 按**这一张卡**记（`hp:s:2=10000` / `hp:i:mvz2:xxx=10000`）。`BlueprintSpawnPatch` 在生成漏斗 `LevelEngine.SpawnSourced` 上做 **postfix**，命中就把 `MAX_HEALTH` 写到该值（`Entity.Health` 跟着顶到新上限）。输入 0 / 负数 = 恢复原版。显示值仍是蓝图定义的 `EngineEntityProps.MAX_HEALTH`（实测发射器 = 300） | `放置探针`：A（标过的槽）12345 / B（没标的槽，**同一张蓝图**）300 / C（不带作用域 = 自然生成）300 |
| 白天不睡觉 `[]` | 同一个 postfix 里做：生成时若命中，就把 `VanillaBuffID.Contraption.nocturnal` **摘掉**（`new IBuffTarget(entity.Pointer)` → `HasBuff` / `RemoveBuffs`），夜里不打药白天直接干活 | 放置探针里一并打 `睡眠buff=0/1` |
| 无条件放置 `[]` | **关卡级全局**（没有键）。`BlueprintPlacementPatch` 在 `MVZ2Logic.Grids.LogicGridExt.CanPlaceBlueprint` 上做 **prefix**，直接 `error=null; __result=true`。**只短路这一个判断**，费用与冷却仍然照扣（后续流程一点没动） | `[UncondPlace] 已挂放置检查补丁`；面板里等级不够时**置灰** |
| 重置冷却 `{ }` | 从 **SeedDefinition** 把 `RECHARGE_ID` / `RECHARGE_SPEED` 写回卡上，再 `SetStartRecharge(true)` + `ResetRecharge()` —— 就是官方 `LevelEngine.ResetAllRechargeProgress` 那一对，所以「预设复位」和「当前值重设」一次到位 | 面板显示值随之回到原版值 |
| 清空卡槽 `{ }` | `level.ClearSeedPacks()`：**卡槽数不变、每格置空**（不是 `RemoveSeedPackAt`，那个会把格子也删掉、卡槽数跟着少） | `卡槽=6` 不变，各格为空 |

##### 标记按「**哪一张卡**」记，而且只对该卡放出来的实体生效

键有两种（`BlueprintKeys`）：自选卡模式下是**卡槽下标** `s:2`；传送带模式（卡是中途从卡池抽出来的，
没有「那一格」）是**卡池条目** `i:mvz2:xxx`。

> **为什么不能按「蓝图 id」一把抓**（早期版本就是这么写的，一次踩了两个坑）：
> ① **同一张蓝图带三张时只能共享一个值** —— 而需求要的是「三个血量不同的僵尸蓝图」；
> ② 命中表是「蓝图 id → 种子实体 id」，于是**自然生成**的同种实体（出怪刷出来的僵尸）也被改了血量。
> 根因：放置与自然生成**在生成漏斗里长得一模一样**（都是
> `SpawnSourced(def, pos, source: null, ...)`），在那儿根本分不出「是不是这张卡放出来的」。

分辨的唯一时机是**放置调用本身**（`PlacementScope`）：官方放置链是
`LogicGridExt.UseEntityBlueprint(grid, seedPack, data)` → `PlaceEntityBlueprint` →
`PlacementDefinition.PlaceEntity` → `grid.SpawnPlacedEntity` → `LevelEngine.SpawnSourced`。
所以在 `UseEntityBlueprint` 上开一个**作用域**（prefix 记下这张卡的键 + 它该放出来的实体 id，
finalizer 清掉），生成漏斗只在这个作用域里认账：
- **根生成**（`source == null`）且实体 id 对得上 → 就是这张卡放出来的，应用标记；
- 对不上的（模仿者的**命令方块**）→ 只打个「标签」，等它变换成真器械时凭父实体 ID 接回来；
- **子生成**在作用域里（升级替换、一次放置出多个）也会继承；
- 作用域外、也没标签的 → 一律不管（**自然生成走这条**）。

两个坑：① 作用域收尾必须用 **finalizer**（postfix 在异常时不跑，作用域会一直挂着）；
② 「标签」是**一次性**的，而血量与逆阵营是**两个**独立的 postfix —— 都要来问一次，
所以解析结果按实体 ID 缓存（`PlacementScope.Resolve`），否则第二个补丁问不到。

##### 兼容性等级：既在配置文件里，也是「更多选项 → 游戏」里的一行设置

`Compat.CompatLevel`（Safe / Standard / Full）现在同时是**原版设置界面**里的一行下拉框：

- 用 `DSHCore.Ui.UiOptions` 的标准三步（声明 → `Attach` → `ApplyProperties`），
  文案/分类在 `assets/Content/metas/options.xml`（分类 `gameplay` = 「游戏」一栏，`order=900` 排在后面）；
- 设置界面与配置文件是**同一份数据**（getter/setter 就写那个配置项），所以谁改都立刻生效；
- ⚠️ 这一行要求 **Loadout 真的注册一个 `Mod`**（`LoadoutMod`）—— 没有 `Mod` 实例就没有
  `mod.AddDefinition`。这是本 mod 里**唯一**一处注册进游戏的地方（代价：mod 列表里多一项）。
  ⚠️ 只注册 ModInfo、**不覆写 `CreateSaveData` / `LoadSaveData`** 会在读档时抛
  `EntryPointNotFoundException: Attempting to call abstract method 'Mod::CreateSaveData'`
  （而且是在 `SaveUserData` 链中间抛，会打断读档）—— 必须覆写并返回一份空 `LogicSaveData`。
- **等级不够的功能在面板里置灰**（悬停提示会说去哪儿调）：
  逆阵营 / 无条件放置 / 传送带模式 = Full；血量 / 白天不睡觉 = Standard。
- 页面的慢速轮询（`Tick`，30 帧一次）会**整页重铺**一次 —— 所以等级在设置界面被改之后，
  置灰状态会自己跟上（实测：改等级当帧还是灰的，重铺一次后变可点）。

##### 「全部蓝图」默认关闭，而且只在原版 debug 存档里出现

- `showAll` **默认 false**（= 与原版一致）；`AllBlueprintsPatch.IsAllowed()` = `LevelFacade.IsDebugSave()`
  （游戏自己的 `DebugManager.CanUseDebugFeatures()`）。非 debug 存档即使把状态打开也**不会追加**。
- 切换按钮克隆选卡面板的「X（取消全选）」（实测就是 `CancelButton`，40x40，图标 sprite = `close`），
  摆在它**右侧一格**（位置由原按钮的 rect 推出来，不写死数字）；**只在 debug 存档里创建**。
- 图标：需求要「显示全部时 `-`、不显示时 `+`」。实测那个按钮**没有文字节点**（只有图标），
  所以做法是：把克隆体子节点上的图标**关掉**（那是「X」，语义冲突），自己叠一个占满按钮的 TMP 文字
  （`-` / `+`，字号 26）。自测判据：`切换前=原版 → 切换后=全部  图标=-`。

##### 传送带模式是真的挂行为，不只是切个标志

`ConveyorRig`：切到传送带时，除了 `SetConveyorMode(true)`，还要**真有一个行为在抽卡**。
`StageDefinition.AddBehaviour` 是 `protected`、而且**没有移除 API**，所以：
- 关卡自定义里**已经带** `ConveyorStageBehaviour` → 我们**不重复加**，只是不驱动（`自驱动`）；
- 没有 → 我们 `new ConveyorStageBehaviour(stageDef)` 自己建一个、`Start(level)`，
  之后**每帧手动 `Update(level)`**（这样才不依赖 `AddBehaviour`）。
- ⚠️ 这个 Update 必须挂在 `LoadoutPanel.Tick()` 里、**在 `if (!IsOpen) return;` 之外** ——
  面板关着的时候传送带也得继续抽卡。
- 切回自选卡 / 换关：丢掉我们那个（`Disable` / `Reset`），**没有**从 stage 上摘除的办法
  （所以只在「本来没有」时才加）。
- 自测判据：`[Conveyor] 已为本关加上传送带行为（每 120 帧从卡池抽一张）` +
  `传送带行为：我们加的（在驱动）`。

### 关卡内 · 页3「制品」

```
|制品列表（带滚轮，一行 4 个）|  制品:{框}（点一下开选择窗口）
                                  添加数量:__  {添加}
                                  计数:__      {删除}
                                  [x]失效      {清空}
```

- 列表**克隆关卡 HUD 的制品条目**（`LevelUIPreset` 下的 `ArtifactItemUI`），
  所以图标 / 计数 / 灰化 / 发光都是原版那一套。**不限制数量**：槽位是数组，排不下不归本面板管。
- ⚠️ **不要用 `LevelUIPreset.GetArtifactAt` / `LevelUIBlueprintChoose.GetArtifactSlotAt`**：
  它们内部是 il2cpp 泛型方法 `getElement<T>`，interop 上走 `MethodInfoStoreGeneric`，实测返回 null。
  改成 `GetComponentsInChildren<ArtifactItemUI>(true)` / `GetComponentsInChildren<ArtifactSlot>(true)`。
- 制品框（右侧那个「点一下挑制品」的框）同样克隆原版：优先选卡面板里的**空制品框**
  （`ArtifactSlot`），拿不到就退化成 HUD 的 `ArtifactItemUI`。
  ⚠️ `ArtifactSlot.OnClick` 在 interop 里是 `Il2CppSystem.Action<T>`，C# 不允许 `+=` 一个 lambda，
  所以点击不看那个事件，由 `ArtifactPage.Tick` 自己做矩形命中。
- 「添加」会**按需扩槽**（一次一个，槽位数组跟着长）；「删除」会释放光环
  （`aura.PostRemove()`）再 `SetArtifact(null)` 并把后面的整体左移、末尾收缩。
  官方代码在删除时并**不**调 `PostRemove`，我们补上了，否则光环会残留在场上。
- 列表排序 = **槽位顺序**（需求 12）。计数 `-1` = 不显示（原版约定）。

### 后续页

场地 / 实体 / 出怪 / 设置 —— 见 `Loadout.txt`。加一页 = 写一个 `IPanelPage` +
在 `PanelPages` 里登记一行（传送带那套是 `ConveyorPages`，与自选卡那套并存）。

---

## 6. 内容列表

需求要求「内容列表每一种都需要单独设计」。目前实现了**进度页**那套（用户定稿的形态）：

- **整套克隆游戏自己的「对话档案」（`MVZ2.Archives.ArchiveUI`）索引页左侧那一块**：
  搜索框 = `IndexArchivePage.searchInputField`，列表 = `Center/Filter/Contents/Tags/Scroll View`
  （里面那层 `Content` 上挂着 `ElementList`，它的模板就是自己的子节点 `TagItem`）。
  克隆时 Unity 会把内部引用重定向到克隆体，所以列表能自己造行。
- 克隆后要做的两件结构修补（见 `UiArchiveList.Sanitize`）：
  ① 视口 `Mask` 换 `RectMask2D`（`Mask` 走模板缓冲，会让 TMP 子网格与模板共用 Stencil 材质，
  任一边销毁就连累另一边抛空指针）；② `localScale` 归 1（档案页在**另一个 Canvas** 下，世界缩放会跟着过来）。
- 行数据用原版入口 `ArchiveTagItem.UpdateTag(ArchiveTagViewData{ name, value })`；
  **勾选用自己实测命中**（指针在哪一行就改哪一行）——不用行自己的 `Toggle` 事件：
  那要靠 `ElementList.indexOf` 把“组件”换回“数据”，行池化复用/下标一有偏差就会出现
  “点第一项没反应 / 锁错一条”（实测报过）。
- **背景变成纯白**：视口上那张图原本是 `Mask` 的遮罩素材（`showMaskGraphic=false` 时靠改材质画成透明），
  换成 `RectMask2D` 之后它变回**普通 Image** → 画成一整块纯白。修法：把它的 alpha 清 0
  （保留 `raycastTarget`，列表靠它接滚动）。**只藏这一张**：行的 `Checkmark` / `Hover`
  也是无 sprite 的图，但那是勾选与悬停的正经视觉，不能一起藏。
- **过滤由我们自己实现**（原版把过滤写在 `ArchiveController` 里，控件不管）：对行文本做
  **大小写不敏感的包含匹配**。行文本形如 `[x] mvz2:xxx`，所以敲 `[x]` / `[ ]`
  就自然筛出已解锁 / 未解锁 —— 不需要给这两个符号写特例。
- 列表高度用 `UiList.SuggestHeight(0.4f)`（按画布高度折算），不用写死的像素。

其余列表（蓝图横向列表、制品、出怪、buff…）留待设计补充。

---

## 7. 已知边界

- **面板没有标题与描述行**（按需求删掉了）：对话框壳自带的那两行被留空 + `SetActive(false)`，
  布局组会直接忽略未激活子节点，所以既不多占高度也不用改对话框逻辑。拖动把手因此只剩**页签行**。
- **下拉框组件不可用**（`TMP_Dropdown` 展开列表不画），所以面板里**没有下拉框** —— 一律用补齐输入框。
- **背景没有本地化名称**，只有内置章节表；英文名拿不到（见 §3.1）。
- **补全范围受原版硬上限 500 限制**：`LogicLevelExt.CalculateSpawnPoints` 里 `maxPoints = 500` 是写死的。
- **面板宽度是一个比例**（画布宽 × 0.94）。4 列网格下每个单元格约 1/4 宽度 —— 标签特别长时会被挤
  （判据见 §10，真溢出了 dump 会标出来）。
- **拖动限制**：纵向必须完整留在屏幕内（比屏幕还高时居中），横向至少留 10px 可见。
- **进 / 出关卡会自动关面板**（关卡内 / 关卡外是两套页集合，把页面跨场景带过去没有意义）。
- 面板**不响应 Escape**（避免与原版暂停输入路径打架）；补齐输入框聚焦时 Escape 是"还原当前值"。
- 补齐输入框用的是「输入名字」对话框的 TMP 输入框（IME 走 TMP 自己的处理）。
- **慢速同步只在“读到有效值”时写回**：`UiCompleter.SyncTo` 遇到 `null` 直接返回 ——
  游戏关卡的 `Difficulty` 在某些时刻会读到 null，那不是“值被清空”而是“现在读不到”，
  照直写会把已经显示出来的正确值抹掉（难度格实测就这样变空过）。
- **解锁条件列表只在资源侧加载完之后才有 155 条**：标题界面初开面板时可能是 0 条，
  页面会每帧自愈重取（`ProgressPage.Tick` → `RefreshSource`），不用重开面板。
- **档案页的列表行高是原版设计值（25）**，没有跟着面板尺寸缩放；行内文字字号用原版自己的 16。
  若中文输入法在游戏里表现异常，改用克隆控制台自己的 `DebugConsoleInputField`
  （它激活时强制 `imeCompositionMode = On`）：`UiKit.Input` 里换素材即可。

---

## 8. 文件结构

```
Plugin.cs                     入口：配置 + 挂补丁（DSHCore 硬依赖）
LoadoutMod.cs                 Mod 逻辑体：**只为「兼容性等级」那一行原生设置项而存在**
Options/CompatOptions.cs      兼容性等级 → 原版设置项（UiOptions 声明/登记/写属性 + 渲染判据自测）
assets/Content/metas/options.xml   那一行的文案 / 分类（gameplay）/ 顺序
Compat/CompatConfig.cs        兼容性等级 + 单项开关（BepInEx config，与设置界面同一份数据）
Ui/
  LoadoutPanel.cs             面板壳：Dialog 克隆 + 宽度 + 页签 + 拖动 + 自动关面板 + 每帧心跳
  BlueprintStrip.cs           镜像框/卡池条（克隆原版已选蓝图条 + 横向滚轮）
  BlueprintPickWindow.cs      选蓝图浮层（克隆原版命令方块选卡面板）
  BlueprintTooltip.cs         给克隆出来的卡铺原版悬停提示（没定义就显示 id）
  UiGrid.cs                   容器/单元格工具箱：UiContainer（横/纵容器素材）、UiField、UiGridRow、UiKit
  UiCompleter.cs              补齐输入框（原版控制台 AutoComplete 弹窗 + 匹配 + 键位）
  NameIndex.cs                id / 当前语言名 / 英文名 的索引与模糊匹配
Pages/
  PanelPages.cs               页集合：关卡内 / 关卡外各一套
  LevelBasicPage.cs           关卡内 页1「基础」（5×4 网格）
  BlueprintPage.cs            关卡内 页2「蓝图」（自选卡）
  ConveyorPage.cs             关卡内 页2「蓝图」（传送带）
  ProgressPage.cs             关卡外 页1「进度」（左列 + 列表）
Patches/
  GameSpeedPatch.cs           面板暂停：GetGameSpeed postfix 返回 0
  AllBlueprintsPatch.cs       选卡界面显示所有实体蓝图（只追加；默认关 + 只在 debug 存档）
  BlueprintToggleButton.cs    选卡界面「X」右侧的 -/+ 切换按钮（只在 debug 存档出现）
  BlueprintSpawnPatch.cs      生成漏斗 postfix：血量 / 白天不睡觉（按卡）
  ReverseFactionPatch.cs      生成漏斗 postfix：逆阵营放置（按卡）
  BlueprintPlacementPatch.cs  放置检查 prefix：无条件放置
  DebugIconEntry.cs           左上角图标 → 面板入口
  PanelTickPatch.cs           每帧心跳
Commands/LoadoutCommand.cs    /loadout（含 Tab 补全）
Diagnostics/
  PanelDump.cs                布局 dump（屏幕矩形 / 字号 / **单元格宽度 vs 标签需要宽度**）
  SelfTest.cs                 自测状态机（帧计数，绝不用 async/await）
  DiagShots.cs                ScreenCapture 截图（异步，落盘到游戏目录）
Game/                         与 UI 无关的薄封装
  LevelFacade.cs              关卡内 API（只写 level）+ 关卡切换/重载 + IsDebugSave()
  BlueprintOps.cs             卡槽 / 卡池 / 冷却 / 血量 / 标记的读写
  BlueprintFlags.cs           标记的持久化（关卡存档旁边的 .loadoutflags，键 = 「哪一张卡」）
  BlueprintKeys.cs            「哪一张卡」的键：s:槽位 / i:蓝图 id
  BlueprintMarks.cs           生成期用的标记速查表（标记 → 该应用哪些改动）
  PlacementScope.cs           **放置作用域**：这次生成是哪张卡放出来的（标记只对该卡生效）
  ConveyorRig.cs              给关卡加/去传送带行为（自己每帧驱动）
  LevelHud.cs                 隐藏 HUD 九项（含还原快照）
  PanelPause.cs               「后台暂停」= 全局选项；面板打开时速度 0
  NameResolver.cs             NamespaceID → 玩家看得懂的名字（含内置章节表）
  ProgressOps.cs              关卡外 API（用户存档）
```

**代码量对比**：上一版自建 UI 框架约 200KB（自绘浮层 + 自算布局 + 自建列表 + 轮询同步），
本版 UI 层约 60KB 且不再有任何坐标/尺寸常量 —— 这正是 DSHCore.Ui 该起的作用。

---

## 9. 构建

```powershell
# 只构建本 mod（有游戏目录则自动部署）
.\Loadout\build.ps1

# 从仓库根构建（-Mod ld 只构建本 mod；-Pack 额外打包到 dist\）
.\build.ps1 -Mod ld
.\build.ps1 -Mod ld -Pack
```

> ⚠️ 含中文的 `.ps1` **必须带 UTF-8 BOM**，否则 PowerShell 5.1 按 GBK 读会解析失败。

---

## 10. 排障：自测 + 布局 dump

面板的视觉问题（标签被挤、内容把框体顶出屏幕、补齐弹窗没画出来）**从代码静态看不出来**，
必须看运行时真实的 RectTransform 与画面：

```ini
# BepInEx/config/qoznos.mvz2.loadout.cfg
[General]
# 空 = 关闭（默认）。menu / level / all
SelfTest = all
```

打开后（**仅排障用，平时留空**）：
1. 等主菜单 → 自动开面板 → 等布局结算 → 把布局 dump 与状态报告写进 `LogOutput.log`；
2. 把第一个补齐字段**聚焦并展开候选**，再截图（「节点存在 + 尺寸对」≠「看得见」）；
3. 自动进第一关（与主菜单「开始游戏」同一条官方路线），在关卡里再来一遍；
4. 打一行 `[SelfTest] DONE` 收尾（供脚本等待）。

一条命令跑完并落盘报告：

```powershell
powershell -File tools\run-loadout-selftest.ps1
# → <游戏目录>\BepInEx\_loadout_selftest.txt（自测行 + 判据行 + ErrorLog + 截图清单）
# → <游戏目录>\BepInEx\_ld_shot<N>_<tag>.png
```

**判据（dump 里直接给结论，不靠眼看）**：
- `判据 面板完整在屏内：OK` —— 面板矩形必须落在 `[0, Screen.height]` 内；
- 每个单元格一行 `格子宽=N 最长标签需要=M → 放得下 / **挤（会溢出）**`；
- `补齐字段状态` —— 每个补齐字段的弹窗是否建成、候选数、当前选中、每页条数；
- `可滚动容器：视口 WxH / 内容高度 / 行数（可滚动）`。

截图的像素差用 `tools/img-diff.ps1`；**必须配"关着"的对照图并限制区域**
（UI 之外的画面自己会动，全屏差分会被动画淹没）。

---

## 11. 踩坑清单（本轮新增，别再踩）

### 布局契约（这一轮踩出来的）

- **行容器的宽度契约必须是「填满父容器」**：把宽度整个忽略（preferred/flexible 都 -1）时，
  节点会直接拿自己布局组算出的**内容宽度**，嵌在一列里的行因此会溢出面板
  （实测解锁/锁定那行宽 732，而它所在的列只有 346 → 锁定按钮跑到框外）。
  `UiContainer.FitRow` 现在给的是 `preferredWidth = 0 + flexibleWidth = 1`。
- **等分列要按“父容器的实际宽度”算，而且必须等布局结算之后**：本行可能嵌在某一列里，
  用面板宽度算会溢出；而刚建出来的节点宽度还是 0 → 先 `ForceRebuildLayoutImmediate`，再等分，再重建一次
  （`LoadoutPanel.RegisterEqualizeRow` + `SettleLayout`）。
- **`RectTransform.position = 世界点` 在这个环境里不可靠**：用它摆浮层实测把框放到屏幕外
  一万多像素处。改用「屏幕点 → `ScreenPointToLocalPointInRectangle` → 减去父矩形左下角」。

### 面板级

- **对话框壳自带的全屏 `Blocker` 会吃掉面板以外的所有输入**：需求要的是非模态，
  所以在构建时找到名字带 blocker 且高度超过半屏的节点 `SetActive(false)`（关节点不销毁）。
- **拖动判定反过来做**：早期只认标题/页签行，需求是“所有可交互控件之外均可拖” →
  判据 = 指针在面板身上 **且** 不落在任何 `Selectable`（按钮/开关/输入框）或 `ScrollRect`（列表滚动区）上。
- 诊断“谁在挡输入”就用 `EventSystem.RaycastAll` 把最上面几层打出来（`/loadout hit`）；
  看自己面板的层级永远看不出别人的东西盖在上面。

### 排版契约（这几个最先踩，单独列）

- **原版控件没有 `LayoutElement`**（`OptionWidgets` / `LabeledToggle` 就是），而
  `LayoutUtility.GetPreferredHeight` 找不到 `ILayoutElement` 时返回 **0**。原版自己不受影响
  （它的面板是**固定高度**，外层布局组有富余空间可分），但在我们这种「高度由内容撑」的对话框里
  → 整个行会被算成 **0 高、5 行叠在一起**（实测）。所以：
  单元格显式给 `preferredHeight`，值取**素材自己 RectTransform 的高度**（`UiKit.RowHeight`）；
  行/列容器给 `preferredHeight = -1`（忽略）→ 由自己布局组的**内容高度**决定。
- **`ContentSizeFitter` 会盖掉父布局组给的尺寸**：控制台的补齐面板自带它，塞进对话框后
  被撑成 40x1872（屏 y 跑到 3055）→ 必须 `UiContainer.DisableSizeFitters` + 自己套定高外框。
- **隐藏要藏在"外框"上**：只关内层的话，外框那段预留高度会变成一片空白（布局组会忽略不活跃子节点）。
- **`Object.Destroy` 帧末才生效**：`清空 → 立刻重建 → 当帧读布局` 是自测里的常规动作，
  用 `Destroy` 会读到还在场上的旧行（实测内容行数 15，应该是 10）→ 用 `DestroyImmediate`。
- **dump 前必须 `LayoutRebuilder.ForceRebuildLayoutImmediate`**：否则新加的子节点还没参与排版，
  读到的是一堆 0 矩形，会把人带到完全错误的结论上（第一轮就是这么误判"行宽 0"的）。
- **同格里的标签要给 `minWidth = preferredWidth = 实测文字宽`**：只给 `flexibleWidth = 0` 时，
  输入框的 preferred 更大就会把标签压到小于文字宽（实测「背景」18 / 需要 28，文字压到输入框上）。

### 克隆原版蓝图条/选卡面板（这几个坑是第三轮才踩出来的）

- **卡片的轴心是 (0,1)（左上角）不是中心**：把卡片放在局部 `(x, y)` 时它占
  `[x, x+48] × [y-72, y]`。所以「左对齐」= 第 i 张左边缘 `内边距 + i×格宽`，
  「纵向居中」= 上边缘 `格高/2`（与框高无关）。一开始按「中心对齐」算，实测卡片整体偏下去半个卡片高。
- **不要用 `ClassicBlueprintSet.offset` / `GetBlueprintLocalPosition(i)` 排卡片**：它自己还要再叠
  一层「自动居中」，实测把整组推到左边。做法：克隆体只当**卡片工厂**（`_set.enabled = false`，
  它每帧都会把卡片搬回去），卡片位置完全自己算。
- **`ElementList._listRoot` 指向克隆体之外的节点**（Unity 不会在克隆时重映射它）：
  `CreateBlueprint/InsertBlueprint` 会把卡片挂到**关卡 UI** 上、跑到屏幕外。
  必须自己指定卡片父节点（这里是框体的 `Viewport/Content`）。
- **克隆出来的条上挂着 LayoutGroup**：往它下面塞节点会被当成「一格」重排
  （实测锚点被改成 (0,1)、位置被挪到右上角）→ 自己加的容器和条本身都要
  `LayoutElement.ignoreLayout = true`。注意 `UiContainer.FitCell` 会把 `ignoreLayout` 设回 false，
  所以「`AddCell` 之后再 `FillRow()`」。
- **横滚条**：`UiSkinPart.ScrollView` 的克隆体（原版设置界面那个可滚动容器）把
  `horizontal = true / vertical = false` 就是「滚轮横滚」—— Unity 的 `ScrollRect` 在「只横」时
  会把滚轮的 y 分量映射成横向。灵敏度默认 1（一格滚轮走 1 像素）要自己调大。
  另外**得给框体自己留一层能接射线的图形**（`Image` + alpha 0，或原版背景图本身就带 raycastTarget），
  否则滚轮落不到 `ScrollRect` 上。
- **原版背景贴图 `form_hollow` 是「空框」**，中间未必有填充 → 要「灰色底的矩形」就自己垫一层
  半透明深色 `Image`（`sprite = null` = 纯色矩形），放在 `SetSiblingIndex(0)`：
  背景贴图是实的话它被盖住、什么也不影响。
- **布局组不会自己重算**：刚 `SetParent` 进内容容器的行，当帧宽高还是 0（实测整行 0×0、
  卡片全挤在左上角）。所以 `ShowPage` 里 `page.Build()` 之后必须主动
  `LoadoutPanel.SettleLayout()`（内部 = `ForceRebuildLayoutImmediate` + 各蓝图条按真实宽度摆一次）。
- **别把「必须立刻生效」的尺寸结算只放在页面 `Tick` 里**：页面 Tick 是**节流**的
  （`_tick % 30`），「建好 → 立刻 dump」的自动化会读到没结算的状态。
- **浮层挂在面板对话框上 → 面板没打开时它整块都是隐藏的**：`activeSelf` 还是 1，
  但 `activeInHierarchy` 是 0。自测里「直接打开浮层」之前必须先 `LoadoutPanel.Open()`，
  否则 dump 出来的东西全是「没有按钮 / 没有文字」的假象（这一轮就被它骗了两个回合）。
- **按钮要自己建，不要用克隆面板自带的那个**：它是面板外的共享节点（搬过来会改到游戏自己的
  选卡界面），而且文案归游戏的语言 / 刷新逻辑管 —— 第二次打开会把我们写上去的字改回去
  （实测表现：两个按钮变成「取消 / 返回」）。用 DSHCore 的 `UiOptionRow`（按钮模式）
  自己建两个，**每次 `Open()` 都重铺一遍文案**。
- **不要“按组件强行打开”按钮行里的节点**：`UiOptionRow` / 原版 `OptionWidgets` 的 `SetKind`
  已经把那一路用不到的控件关掉了，强行全开会把下拉框 / 滑杆 / 复选框一起放出来
  （实测行里同时出现 `LabeledDropdown + LabeledToggle + TextSlider + TextButton`，
  而且读到的第一个文本是下拉框的「语言」）。按钮的可见性归原版组件管，别插手。
- **原版 `ButtonRow`（页签那种）的条目未必会被打开**：它里头的 `ElementList` 造的条目要等
  它自己的一次 `Update`；实测在「建完立刻 `SetActive(false)`」的第一个浮层里那两个按钮
  一直没打开（`activeSelf` 全 false）。要建按钮就用 `UiOptionRow` 或 `UiSkinPart.Button`。

### 本轮（去动画 / 加提示 / 接线 / 传送带行为）

- **「每帧要做的事」和「每 30 帧要做的事」必须分开**：`Input.GetMouseButtonDown(0)` 只在**一帧**为真，
  放在节流的 `Tick()`（`_tick % 30`）里就会变成「得连点几十下才中一次」。
  `IPanelPage` 因此多了个 **`Frame()`（空实现，每帧调、不节流）**，点击判定一律放那儿。
  自测会打 `点击通路：Frame（每帧）=30  Tick（30 帧一次）=1` —— 两个数一比就知道放对没放对。
- **自测要盯「会自己消失的东西」**：面板高度判据原来量的是 `LoadoutPanel.DialogRoot`（**满屏**，
  两页都是 768）→ 判据**恒真、什么也没验**。改成量 `LoadoutPanel.Content` 才有意义
  （实测两页都是 443）。**写判据时先问一句「这个量在最坏情况下会不会变」**。
- **切模式高度不一致的元凶是「0 高的行」**：`LayoutGroup` 给每个子节点都算间距，
  所以一行内容再少、哪怕高度 0，也照样贡献一份 spacing —— 传送带页曾因此比蓝图页多一截。
  定位办法：把两页的**行数 / 每行高度**逐行 dump 出来比（`DebugRects` 会打「标题行 / 网格块 / 下排」）。
  修法是让两页**行数完全一致**（本轮直接把传送带页多出来的那行删了，把警告塞进概率格）。
- **兼容功能默认是「静默失效」**：等级不够时功能一点声音都没有（既不报错也不置灰）。
  默认等级是 **Standard**，而 `MinLevel` 给「需要 Full」的那些（传送带接管 / 逆阵营 / 无条件放置）
  默认就是**关着**。写需求时要么把等级调上去，要么把 `MinLevel` 降下来 —— 别指望玩家自己去看配置。
  等级不足时唯一的线索是启动时那几行 `[Compat] X = 关闭（最低等级 Full）`。

### 本轮（标记按卡 / 兼容等级做成设置项 / 提示与置灰）

- **★ 「打开页面时字段没置灰，点一下卡再点掉才灰」= `Refresh()` 跑早了**：`Build()` 里那次
  `Refresh()` 是在**第二~四行还不存在**的时候调的，那些 `SetRowEnabled` / `SetFieldEnabled`
  全落到 null 上（不报错、也就什么都没做）。修法：**`Build()` 末尾（所有行都建好之后）再刷一次**。
  自测把每个控件的 `interactable` 直接打出来（`DebugGate`），这类"没生效但看不出"的问题才定得住。
- **状态要能跟着外部变化走**：兼容性等级是在**游戏的设置界面**里改的，本页不会收到任何回调 →
  于是置灰状态会停在旧值。做法：页面的慢速轮询（`Tick`，30 帧一次）里**整页重铺一次** `Refresh()`。
  自测里专门留了 before/after 两行：`等级提到 Full，尚未重铺`（还是灰的）→ `重铺一次后`（可点了）。
- **tooltip 要铺**所有页 **，不能只铺当前页**：官方
  `BlueprintDisplayerStandalone.UpdateItems` 会**一次性把每一页都建好填满**，而翻页只是
  `SetCurrentPage` 切一下 `activeSelf` —— **不经过我们的刷新**。只铺当前页的话，
  翻页后新页上的卡一个提示都没有，要等到再选一次卡（触发我们的刷新）才补上 ——
  实测症状正是「翻页后 tooltip 要选过蓝图才显示」。自测判据是逐页报覆盖率（`40/40 + 29/29`）。
- **重复铺提示要做缓存，而且缓存键要带上「铺的是哪个蓝图」**：网格卡是官方 `ElementList`
  复用同一批 `Blueprint` 实例的（翻页只换数据），只按实例 id 缓存会**跳过本该重铺的卡**。
- **`FindObjectsOfType` 不含未激活对象**：`「直接起关卡」时选卡那套 UI 是关着的，按场景遍历找
  `LevelBlueprintChooseController` 会**找不到**。优先走 `LevelController.BlueprintChoosePart`
  （控制器自己的部件表），场景遍历只当兜底。
- **克隆一个「只有图标没有文字」的按钮**：`UiClone.FindText` 找不到 TMP 是正常的
  （实测「X（取消全选）」= `CancelButton`，40x40，子节点只有一张 sprite=`close` 的 Image）。
  要显示 `-` / `+` 就得自己叠一个 TMP：**关掉子节点上的图标**（别关按钮自己那层的 Image ——
  它是射线目标，关了就点不动），再叠一个 anchor 拉满的 `UiKit.Label`。
- **注册 `ModInfo` 就等于要负责存档**：只 `ModBootstrap.Register` + `ModInfos.Create`、
  不覆写 `CreateSaveData` / `LoadSaveData`，读档时 `SaveManager.LoadModData` 会抛
  `EntryPointNotFoundException: Attempting to call abstract method 'Mod::CreateSaveData'`
  —— 而且是在 `LoadUserData` 链中间抛的。覆写并返回空 `LogicSaveData` 即可
  （本 mod 不存东西）。日志里只看 `ErrorLog.log` 是**看不出来**的，它是 Il2CppInterop 的
  trampoline 错误（`grep trampoline LogOutput.log`）。
- **`AddBehaviour` 是 `protected` 且没有移除 API**：想给关卡加行为，只能自己 new 一个、
  自己每帧 `Update` 它（`ConveyorRig` 就是这么干的），并且**别在「关卡本来就有」的重复加**
  （判据 = `GetBehaviourCount()` / `GetBehaviourAt(i) is ConveyorStageBehaviour`）。
  这种「自己驱动」的对象必须在**面板关着时也继续跑**（`Tick` 里要放在 `if (!IsOpen) return;` 之外）。

### 其它

1. **原版 UI 类型上 `AddComponent<T>()` 可能返回 null 且不抛异常** —— 单按钮/格子这类
   "克隆一个格子再挂个 Button"的写法在本环境不通。交互要么克隆**自带该组件**的原版节点，
   要么自己轮询鼠标位置。
2. **`UnityEvent.AddListener` 收的是 `UnityEngine.Events.UnityAction<T>`**（IL2CPP 委托），
   `System.Action<T>` / `Il2CppSystem.Action<T>` 都传不进去 → `DelegateSupport.ConvertDelegate<...>`。
   游戏自己的事件（`Action<T>` 字段）才是 `Il2CppSystem.Action<T>`。两者别混。
3. **il2cpp 侧只存裸函数指针** → 托管委托必须自己持有（`UiClone.KeepAlive`），
   否则 GC 一收就崩；属主销毁时用 `UiClone.PruneKeepAlive()` 回收。
4. **`string[]` 不能隐式转成 `Il2CppStringArray`**（interop 的转换运算符不会降到派生类型）→
   自己造数组（见 `LoadoutPanel.ToIl2Cpp`）。
5. **il2cpp 泛型方法尽量避开**（要 MethodInfoStore）→ 用 `ElementList.getElement(int)`（返回 GameObject）
   再 `GetComponent<T>()`，别用 `getElement<T>()`。
6. **`Instantiate` 会复制持久化 `onClick` 监听器** → 克隆选卡面板的「取消」按钮后必须
   `UiClone.ClearButtonListeners`，否则点我们的按钮会把选卡取消掉。
7. **Harmony 补丁里绝不能用 `async`/`await`**：续体会被丢到线程池线程，那一刻调 il2cpp 直接崩游戏。
   本 mod 的延时/轮询全部是**帧计数状态机**（`SelfTest`、`LevelFacade.TickPending`）。
8. **`ContentSizeFitter` 只做高度**：官方对话框宽度是 `sizeDelta.x` 写死的 400，要加宽只能改它
   （改完框体美术会自动跟随，因为 `Background` 是拉伸锚点）。
9. **DSHCore 的 `UiSkin.FindScrollContent` 是 `internal`**（外部拿不到）→ 用
   `ScrollRect.content`。
10. **克隆出的容器要先清空子节点**（`DestroyImmediate`，不能交给帧末的 `Destroy` ——
    模板的子孙可能带着会在激活时 `Awake` 的组件）。
11. **`LayoutGroup` 忽略未激活的子节点** → 补齐弹窗"隐藏"就是 `SetActive(false)`，
    既参与布局又不占高度，不需要自己算位置。

### 本轮（第三批需求：补满 / 禁用冷却 / 幂数 / 无条件放置 / 传送带卡槽化 / tooltip）

#### ★★ 最严重的一个：**别在 `Plugin.Load()` 里反射 `LogicLevelProps`**

`LogicLevelProps`（以及 `LogicHeldTypes` 一类带 `[PropertyRegistryRegion]` 的静态类）的
**类型初始化必须在游戏内容注册完成之后**才能跑。在 `Plugin.Load()` 里只要**碰一下这个类型**
（哪怕只是 `typeof(LogicLevelProps)` 去找方法），就会提前触发 `.cctor`，
而它内部依赖 `LogicHeldTypes` 等还没建好的东西 → `NullReferenceException`。

要命的是 **`TypeInitializationException` 会被运行时永久缓存**：一旦失败，这个类型在**本次进程内**
再也无法使用。后果（实测日志）：

```
[Error] [Refill] 挂补丁失败：… TypeInitializationException: … 'MVZ2Logic.Level.LogicLevelProps' …
[Warning] [Level] 读「starshardcount」失败：The type initializer for 'MVZ2Logic.Level.LogicLevelProps' threw an exception.
[Warning] [Level] 开关 buff 失败：PVZEngine.MissingDefinitionException: Trying to create a buff with missing definition mvz2:debug_no_recharge
[Error] [MVZ2 Modular Curse] [CurseBuffs] 修饰符展开失败 qc_c1: …（别的 mod 也炸）
```

- 连**原版自己的** `GetStarshardCount` 都开始失败（于是「出怪幂数」写进去读回来是 0）；
- `Content.GetBuffDefinition` 也开始返回 null（于是报 buff 定义缺失）；
- **别的 mod**（Modular Curse）跟着崩。

修法：**补丁分两批挂** —— 核心引擎类型（`LevelEngine.AddEnergy`）在 `Apply()` 里挂；
凡是反射 `MVZ2Logic.*` 静态类的，都挪到 `CheatRefill.Tick()`（面板心跳，**进关卡之后**才跑），
并且先 `level.GetStarshardCount()` 探一下，走通了再挂。

#### 无条件放置：只挂 `CanPlaceBlueprint` 是**不够的**（会"扣了费不出东西"）

放置有**两道**门，都要放行：

| 门 | 作用 | 只在第一道放行的后果 |
|----|------|--------------------|
| `PlacementDefinition.GetPlaceError(grid, entityDef)` | `LogicGridExt.CanPlaceBlueprint`（放手检查）与 `GetSeedHeldHighlight`（**格子染红/绿**）都经它 | 拖动时格子**仍然红色** |
| `PlacementDefinition.PlaceEntity(grid, entityDef, param)` | 它**又遍历一遍**各 `PlaceMethod.GetPlaceError`，全都有错就返回 `InvalidOutput` | **费用与冷却照扣，实体不生成** |

做法：两道都加 prefix；第二道在放行时**自己照抄 `EntityPlaceMethod.PlaceEntity`** 生成实体
（模仿者先生成 `command_block` + `LogicEntityProps.VARIANT`，否则 `grid.SpawnPlacedEntity(entity.GetID(), spawnParams)`）。

顺带解决了**紫卡**（升级类）：「无条件放置」下不再要求格子上有可升级目标，直接生成它自己的器械，
于是那张卡的标记（血量 / 阵营…）才有实体可落。

#### 出怪点数 / 倍数 / 幂数：填的必须是**生效值**

- 难度是**关卡级 buff** 实现的：`LevelHardBuff` 给 `LogicLevelProps.SPAWN_POINTS_POWER`
  挂了 `FloatModifier(…, NumberOperator.AddMultiple, 0.2f)` → 生效值 = `自身值 × 1.2`
  （`AddMultiple` 就是乘 `1+x`）。所以「输入 1.2 变 1.44」是必然的。
- 通用解法：**两样本仿射反解**。`ModifiableProperties.SetProperty` 会清缓存并重挂修饰器，所以
  先写 0 取 `e0`、再写 1 取 `e1`，得到 `eff = a·own + b`；再写 `own = (want - b) / a`。
  对原版四种算符（`Set` / `Add` / `AddMultiple` / `Multiply`）都成立，与难度、关卡、装备挂了多少层无关。
- `a ≈ 0`（有 `Set` 类修饰器把值钉死）时**无解** → 照写原值 + 记一行日志，不骗玩家。
- 注意 `PVZEngine.PropertyMeta<T>` 的命名空间是 **`PVZEngine`**（不是 `PVZEngine.Properties`，那是
  `PropertyKey`/`PropertyRegions` 所在的命名空间）。写错就是 `CS0234`。
- 原版属性名有个拼写错误：`LogicLevelProps.SPAWN_POINTS_MUTLIPLIER`（少一个 `I`）。

#### 「禁用冷却」不该依赖原版 buff

原版 `mvz2:debug_no_recharge` 有两个问题：

1. 它**只在 debug 存档里注册** —— 普通存档 `AddBuff` 直接 `MissingDefinitionException`；
2. 它是把**整关冷却速度**拉到 `1e8`，与需求要的「逐卡换成『无』」是两回事
   （还会让蓝图页的「冷却(秒)」显示一个荒谬数字）。

所以改成**自持一个关卡级开关**（`BlueprintFlags` 的 `nr:` 行），
开关状态与每张卡的备份（`rc:槽号=冷却id|基础速度`）一起写进 `.loadoutflags`：

- 开：先备份每张卡**当前**的 `RECHARGE_ID` / `RECHARGE_SPEED`（玩家改过的也算），再 `ApplyRechargePreset(none)`；
- 关：按备份换回，并清掉备份；
- 进关卡时由心跳（`NoRecharge.Tick`）做**边沿检测**自愈 ——
  因为「上次开着就退出、再进来」不会有任何按钮点击事件。

落盘受 `GcFeature.SaveCustomData`（Standard）管：Safe 档下开关与备份只在内存里
（需求原话：「禁用冷却在存档里写自定义数据的功能仅在 Standard 下实现，Safe 模式只写到内存」）。

#### 传送带页：第一行就是**卡槽**

按需求把这一页的镜像条改成显示/编辑**卡槽**（与自选卡页**同一份数据**），
卡池变成"卡槽的镜像 + 传送带专有属性"：

- 蓝图 id 与顺序 = 卡槽；**权重**（`ConveyorPoolEntry.Count`）与**数量限制**（`MinCount`）
  按蓝图 id 单独记（换掉再换回来，权重还在）；
- 「数量限制」默认 **0** = 不限量（`RefillConveyorPool` 里 `fillCount = max(MinCount, discardCount)`，
  0 就是把抽出去的收回来接着抽）；
- 卡槽**为空时不动卡池** —— 原版自带传送带的关卡卡槽本来就是空的，拿空表覆盖会毁掉关卡设计；
- `Tick` 里比对卡槽签名，变了就重建镜像（自选卡页改了这边也会跟上）；
- 写回 `LogicStageProps.CONVEYOR_POOL` = 关卡属性 → **随 `.lvl` 存盘**，
  这就是「从自选卡改到传送带模式后数据要写进存档」；
- 第四行按钮顺序与自选卡页一一对应（调整在前、清空在后），名字改成「调整卡池 / 清空卡池」。

> ⚠ 已知边界：原版抽卡只记**蓝图 id**（`conveyorSeedSpendRecord` 按 `NamespaceID` 计数），
> `ConveyorSeedPack` 上没有"卡池条目"身份 —— 所以**抽出来的传送带卡片**无法保留
> "同一蓝图多份不同血量"。权重/数量限制作用于卡池那一行；按卡标记（血量等）在传送带上
> 只对**该槽那一张**生效。

#### tooltip：置灰时**只说原因**

统一的取舍（需求原话：「避免大段文字和成堆括号说明」）：

- 置灰 → 提示里**只剩置灰原因**（功能说明整段不显示），原因由
  `CompatConfig.DisabledHint(feature)` 生成，自带「去哪儿调」（设置 → 更多选项 → 游戏）；
- 没选中任何一张卡 → 只显示 `ScopeHint`（先在框里点一张）；
- 「白天不睡觉」在**非夜用器械**上置灰并说明原因（`EntityDefinition.IsNocturnal()`）；
- 出怪三项的提示里点明**「你填的就是生效值，难度修正会被自动扣掉」**（这是玩家最需要知道的一句）。

#### 其它

- **选卡界面那几个按钮点不动 = 叠上去的 TMP 吃了射线**：`DSH_Icon` 是 anchor 拉满盖住整个按钮的，
  它是射线目标的话，下面的 `Button` 永远收不到点击 → 必须 `label.raycastTarget = false`。
  同时把它**居中对齐 + 白色 + 字号 30 + 往右下偏 (1.5,-1.5)**（`-`/`+` 的字形 ink 中心偏上，
  不偏一点看着不居中）。自测把 `raycastTarget` / 颜色 / 字号 / 偏移都打出来当判据。
- **模仿者要同时改"两处显示"**：战斗里那张卡（`RuntimeBlueprintController`）读的是
  `SeedPack` 的 `command_block` 属性，而**选卡阶段**用的是每个已选卡自己的
  `ChosenBlueprintController.CommandBlock` 字段（`CreateChosenBlueprint` 时从
  `BlueprintChooseItem` 拷进去的）→ 只改 pack 就会「镜像框里白了、原版选卡处还是原样」。
  `VanillaChooseBridge` 按「卡槽下标 → 蓝图 id」匹配那些控制器，改完 `UpdateView()` 重画。
- **`VanillaChooseBridge` 用字段不用反射**：`ChosenBlueprintController.CommandBlock` 是
  `public bool`、`Index` 是 `public int { get; set; }`，`UpdateView()` 是 `public` —— 直接改即可，
  不用 `Traverse`（也就避开了 interop 上反射字段的那些坑）。
- **清空卡槽要连标记一起清**：`BlueprintOps.ClearSlots` 现在会按槽号
  `BlueprintFlags.ClearAll(level, BlueprintKeys.Slot(i))` —— 不清的话，下次往这一格塞别的卡
  会继承上一张的血量/阵营。
- **`AddStarshardCount` / `GetStarshardCount` 这些是 `MVZ2Logic.Level.LogicLevelProps` 上的
  静态扩展方法**（不是 `LevelEngine` 的成员）→ 补丁要按扩展方法的签名
  `(LevelEngine, int)` 去找，调用处也要 `using MVZ2Logic.Level;`。
12. **`ScreenCapture.CaptureScreenshot` 是异步的**（帧末写盘）→ 截完要等 ≥20 帧再看文件；
    而且它偶尔会落下空白/黑帧，**结论优先看 dump 的数字**。
