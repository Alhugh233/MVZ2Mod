# MVZ2 UI 组件画廊（Ui Gallery）

**开发者工具。** 把 `DSHCore.Ui` 提供的每个 UI 素材以真实观感展示出来，同时充当
「用原版零件做 UI」的最小可编译范例。

- 适配：MVZ2 0.7.0 test-4 / Windows x86 / IL2CPP metadata v31.1 / BepInEx be.788
- 前置：**硬依赖 DSHCore**（`UiSkin` / `UiHost` / `UiClone` / `UiTooltip` 都在它里面）
- 设计论证：`DSHCore/UI_RESEARCH.md`

---

## 0. 它是干什么用的

三件事，按重要性排：

1. **视觉验收**：`DSHCore.Ui` 的每个素材长什么样，一眼看到。改 UI 基础设施之后重跑一次
   就知道有没有画坏。
2. **活文档**：比文档里的代码片段可靠 —— 这个文件是**真的能编译、真的在跑**的。
3. **质量指标**：看一眼 `GalleryPanel.cs` 的代码量就知道 API 好不好用。
   **如果做一个面板需要几百行，或者必须算坐标，那就说明 API 该改，而不是这个 mod 该加代码。**
   这是这个 mod 最重要的作用。

---

## 1. 用法

```
/dshui                打开画廊（已打开则显示状态）
/dshui close          关闭
/dshui page <名称>    跳到某一页（不带参数列出全部页；也支持序号）
/dshui report         打印素材解析报告（含每个素材的来源节点路径）
/dshui dd             展开 TMP 下拉框并打印它的运行时状态（画布/矩形/Graphic）
/dshui rebuild        强制重新解析 UI 素材（切场景后素材找不到时用）
/dshui help           帮助
```

`/dshui` 后按 Tab 可以补全子指令。

面板本身：标题固定为「UI 组件画廊」（标题素材只能占一行），页号/页名/说明在正文里，
底部是「上一页 / 下一页 / 关闭」。

### 页面

| 页 | 演示的素材 |
| --- | --- |
| 文本 | **字号一览**：每一行都是同一份 `Text` 素材的克隆，只改字号；★ = **原版出厂值**（`Title` 36pt / `Text` 24pt / 设置项标签与按钮 16pt / 输入框 14pt —— 全部是 `UiSkin.MeasureFont` **实时量出来的**，不是文档里拄的数字），再叠上本画廊自己选的 22 / 14 做对照 |
| 复选框 | `OptionRow`（`UiOptionKind.Toggle`） |
| 滑条 | `OptionRow`（`UiOptionKind.Slider`）—— 标签自动变「标签：值」并逐帧刷新 |
| 下拉框 | `OptionRow`（`UiOptionKind.Dropdown`）—— **⚠️ 该控件已知不可用**（展开列表不画），页上写了原话与联系方式；本库不提供替代实现 |
| 按钮 | `Button` / `ButtonRow`（含点击反馈）+ `OptionRow`（`UiOptionKind.Button`） |
| 列表 | `UiList`（官方 `ScrollRect` + 官方 `ButtonRow`，10 行，可滚动） |
| 输入框 | `InputField` |
| 提示 | 原版悬停提示（`UiTooltip`）：锚点应在**行的上方**，不遮控件 |
| 报告 | 素材解析状态一览 |

---

## 2. 配置

`BepInEx/config/qoznos.mvz2.uigallery.cfg`

| 键 | 默认 | 说明 |
| ---- | ---- | ---- |
| `General.SelfTest` | `false` | **排障用**：进主菜单后自动打开画廊、逐页装配、每页 dump 一次布局矩形并截图到 `<游戏目录>\BepInEx\_uig_p<N>.png`，全部写进日志（`[SelfTest]` 开头）。在下拉框那一页还会额外做一组探针：`dd0` 全关 → `dd1` TMP 弹窗开着 → `dd2` 弹窗关掉（对照）。另外会挂上 `ElementListWatchdog`：它盯着 `ElementList.Awake` 的模板，一发现不可用就把**节点路径**打进日志（否则那种空指针在日志里只是一行没有调用者的 `ElementList.Awake ← Instantiate`）。 |
| `General.SampleOptions` | `false` | **示例设置项**：往原版设置界面里注入 2 条设置项（`general` 分类下一个开关、`graphics` 分类下一个滑条），用来演示 `DSHCore.Ui.UiOptions`（「零 UI 代码加设置项」）。开启后本 mod **会注册 ModInfo、因而会出现在 mod 列表里** —— 这是本文唯一一处被授权的例外（见 §3）。`SelfTest = true` 时会被强制开启 |

自测是为了让「UI 基础设施在这台机器上到底能不能工作」变成一个**可以 grep 的结论** ——
不需要在游戏里打字，也不需要有人盯着屏幕。

判读工具（`tools/`，均为 ASCII 脚本，避免无 BOM 的 ps1 被按 GBK 误读）：

| 脚本 | 干什么 |
| --- | --- |
| `run-selftest.ps1` | 开自测 → 启动游戏 → 等日志出现 `SELFTEST_DONE` → 把关键行与**按区域**的像素差分写进 `BepInEx\_selftest.txt`（`-NoLaunch` 可复用已有日志） |
| `img-diff.ps1` | 两张 PNG 的像素差分：`DIFF_PIXELS` / `SCANNED` / `DIFF_BBOX_*`；`-RegionX/Y/W/H`（图像坐标）只统计某块区域 |
| `diff-dd-regions.ps1` | 针对上面那组下拉框探针的现成封装 |
| `find-log.ps1` | 带上下文的日志检索（`BepInEx` 两个日志 / Unity 的 `Player.log`）：`-Pattern`、`-Context`、`-Last`，`-Which player-all` 会统计每个 `Player.log` 里的命中数 —— **`Player.log` 才是未捕获异常真正落盘的地方**（`LogOutput.log` 里往往什么都没有） |
| `reset-selftest.ps1` | 关掉自测开关、结束游戏、清理截图与报告 |
| `build-and-selftest.ps1` | 构建（**编译失败直接中止**，否则下面就变成拿旧 DLL 跑自测）→ 自测

⚠️ 像素 A/B **必须配“关着”的对照图 + 区域限制**：标题界面自己会动，全屏差分会被动画淹没
（本仓库就因此把 13.9% 的全屏差异误读过一次）。

---

## 3. 这个 mod 自己遵守的规则

写它的时候就按 `DSHCore/UI_RESEARCH.md` §5 的决策树来，所以它本身就是那份文档的范例：

- **不算任何坐标、不定任何尺寸、不定义全局缩放。** 内容行直接塞进官方对话框自己的
  `VerticalLayoutGroup`，排版全交给原版。（`GraphicConsole` 就是从"自己定一个设计尺寸 +
  全局 scale"开始失控的。）
- **不 `AddComponent` 任何装饰性组件。** 虽然 `TooltipAnchor` / `TooltipHandler` 是例外
  （唯二可安全 `AddComponent` 的），其余一律克隆。
- **零注入。** 不注册 il2cpp 类型、不注册 Definition、不注册 ModInfo。
  连每帧心跳都挂在官方本来就每帧跑的 `MainSceneController.Update` 上。
  <br>❗ **唯一例外**：`General.SampleOptions` 打开时（默认关）会注入一个 `Mod` 子类
  （`UiGalleryMod`）与两条设置项定义 —— 因为「mod 能不能给原版设置界面加一项」这件事
  必须有一个真的注册了 `ModInfo` 的 mod 才能验证，而这正是这个 dev 工具该承担的试验。
  它原本挂在 `LaboratoryMod`（玩家向的小游戏 mod）里，已搬到这里。
  这一段同时也是 `ModNamespacePin` 的现场证据来源：实测三个注入 mod 在 ModInfo 刚创建时
  其 `Namespace` 字段就已是「失效」（详见 `DSHCore/UI_RESEARCH.md` §7）。
- **不注册自己的热键、不改任何游戏状态。** 它只显示。

---

## 4. 已知边界

| 现象 | 原因 |
| ---- | ---- |
| 下拉框点不开 | **已知不可用。** 碍于技术问题，该组件无法正常工作，请自行寻找替代方案，如果你对修复该组件有任何建议，请联系 QoZnoS[https://github.com/QoZnoS]。实测：展开列表在本环境（`GlobalUICanvas` 相机画布）下**一个像素都不画**（节点 active、矩形正确、`CanvasGroup.alpha=1`、像素差分为 0，但它的射线还生效）。六种画布/层级修法（不干预 / 对齐宿主 / 强制 Overlay / 挪出面板 / 搬根画布 / 删画布）全部无效，本库**不提供替代实现**。用 `/dshui dd` 看运行时状态 |
| 素材显示 `[MISS]` | 场景里还没有对应的原版节点。标题界面早期拿不到 `OptionWidgets`（复选框/滑条/下拉框的来源），进主菜单或关卡后自动就绪。 |
| 页面内容过长 | 对话框的框体高度由 `ContentSizeFitter` 决定，**没有滚动能力**。长内容请用 `UiList`（官方 `ScrollRect` 克隆，见「列表」页）。实测判据：量 `_selftest.txt` 里的 `框体` 矩形，要求 `y >= 0 && y + h <= Screen.height`（1017×755 屏幕上 `y=29 h=696` 就是合格的） |
| 列表背景会变成**一整个白块** | 已修：`Scroll View/Viewport` 上那个 `Image` 的 `sprite` 是空的（uGUI 把空精灵画成**纯白矩形**），而它原本只是 `Mask` 的「模板写手」（`showMaskGraphic = false`，不画自己）；遮罩换成 `RectMask2D` 后它开始画了。现在 `UiSkin` 会把这类空图 `alpha` 置 0（视口无条件，其余空图：要吃射线就置 0、不吃就 `enabled = false`）。证据见 `_selftest.txt` 的「图形（本列表）/（原版原型）」对比 |
| 曾经刷过 11 行 `Material.GetFloat ← TMP_SubMeshUI.UpdateMaterial ← OnDisable` | 已修：视口的 `Mask` 会让 TMP 的子网格去申请**共享**的 Stencil 材质，克隆体与模板互相销毁 → 换成 `RectMask2D`。实测 11 → 0 |
| 曾经报一行 `ElementList.Awake ← Instantiate` | 已修：`Scroll View/Viewport/Content` 上的 `ElementList` 的 `_template` 是空的（原版运行时才赋值），克隆体一激活就抛。现在 `UiSkin` 会把这类行生成器组件在激活前 `DestroyImmediate` 掉。实测 1 → 0 |
| 截图是空白/加载画面 | `ScreenCapture.CaptureScreenshot` 是异步的（帧末写盘），自测前几页可能落在游戏加载阶段。**判据以布局 dump 的矩形为准，截图只当辅助。** |

---

## 5. 构建

```powershell
.\build.ps1                 # 构建（有游戏目录则自动部署）
.\build.ps1 -Pack           # 打包到 dist\（**不带** BepInEx 运行环境 —— 拿它的人一定有开发环境）
```

或在仓库根：`.\build.ps1 -Mod uig`

---

## 6. 文件

```
UiGallery/
├─ Plugin.cs              插件入口（BepInDependency 硬依赖 DSHCore）
├─ GalleryConfig.cs       配置：SelfTest / SampleOptions
├─ UiGalleryCommand.cs    /dshui（含 Tab 补全）
├─ GalleryTick.cs         心跳 + 自测状态机（含布局 dump 与截图）
├─ GalleryPanel.cs        面板本体 —— 「用 DSHCore.Ui 做一个面板要写多少代码」的答案
├─ GalleryPages.cs        9 个页面，每个演示一个素材
├─ ElementListWatchdog.cs 诊断哨兵：给「ElementList.Awake 空指针」那行日志命名（只在 SelfTest 下挂）
├─ UiGalleryMod.cs        示例设置项用的注入 Mod（只在 SampleOptions 下创建）
├─ SampleOptions.cs       示例设置项：声明/登记/写属性 + 复刻官方渲染判据的验收
├─ assets/Content/metas/options.xml   两条示例项的文案与分类
└─ build.ps1
```
