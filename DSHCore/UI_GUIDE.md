# DSHCore UI 开发指南（子 mod 用）

> 这份文档是**动手写 UI 时唯一需要读的东西**。
> 它只讲"怎么做"；"为什么是这样"的论证、实测路径与完整坑表在
> [`UI_RESEARCH.md`](UI_RESEARCH.md)，可运行范例在 [`../UiGallery/`](../UiGallery/README.md)。
>
> 全部结论都来自**本机实测**（MVZ2 0.7.0 test-4 / x86 / IL2CPP metadata v31.1）。
> 凡是没实测过的，我都在这份文档里标了「未验证」。

---

## 0. 三十秒决策树

```
我要给玩家看点什么 / 让玩家选点什么
│
├─ 一条信息 / 一个问题？
│    └─ ✅ L1：level.ShowDialog(...) / Global.GUI.ShowDialog(...)      ← 零 UI 代码
│
├─ 顶部提示条？
│    └─ ✅ L1：level.ShowAdvice(text, timeout)                          ← 零 UI 代码
│
├─ 要「开 / 关」「0~1 的滑条」这类设置项？
│    └─ ✅ L2：UiOptions.DeclareToggle / DeclareSlider + options.xml            → §3
│         （零 UI 代码，外观 100% 原生，进原版设置界面）
│
├─ 要往原版已有的面板/对话框里加东西？
│    └─ ✅ L3：UiSkin.Clone + 插进原版 LayoutGroup                       → §4
│
├─ 要一个自己的面板 / 列表 / 常驻窗口？
│    └─ ✅ L3：UiSkin.Clone(Dialog) + UiOptionRow / UiList              → §5
│
└─ 想自己画控件（new GameObject + AddComponent）？
     └─ ❌ 做不到。字体、Sprite、点击音效全是私有 [SerializeField]，
        AddComponent 出来一定是空的（见 §1）。唯一可行路径是**克隆原版节点**。
```

**绝大多数 mod 的 UI 需求在 L1 就结束了。** 先确认自己是不是真的需要 L3。

---

## 1. 一条底层事实（决定了所有做法）

原版 UI 是 **prefab 驱动**的：

- 控件的字体 / Sprite / 点击音效都挂在**私有 `[SerializeField]`** 上，`AddComponent` 拿不到；
- UI prefab **不在 Addressables 条目里**，mod 侧加载不到；
- 全仓库 `new GameObject(` 只有 2 处且与 UI 无关，`AddComponent<Canvas>` 零命中 ——
  **原版自己也是靠"克隆 prefab 上的模板节点"造 UI 的**。

所以：**唯一可行路径是「克隆场景里已经存在的原版节点」**。`DSHCore.Ui` 就是把这件事固化下来。

---

## 2. L1 —— 语义层（零 UI 代码，优先用）

| 需求 | 用什么 |
| --- | --- |
| 给玩家看一条信息 / 问一个问题 | `level.ShowDialog(...)` 或 `Global.GUI.ShowDialog(...)` |
| 顶部提示条 | `level.ShowAdvice(text, timeout)`（`timeout` 单位是 tick，30 tps） |
| HUD 显隐 / 震屏 / 提示箭头 | `LogicLevelExt` 的扩展方法 |

```csharp
// 关卡内：一条带确认的对话框（模态、自动暂停、自带点击拦截）
level.ShowDialog("标题", "正文", new[] { "确定", "取消" }, index => { /* … */ });

// 关卡外（主菜单等）：只有这一条路
Global.GUI.ShowDialog("标题", "正文", new[] { "好" }, index => { });
```

**不要自己拼模态**：很容易忘了暂停游戏或漏掉点击拦截，最后表现成"背景还能点"。

---

## 3. L2 —— 数据驱动的设置项（零 UI 代码）

原版设置界面（主菜单 / 地图 / 关卡三处共用）会**自动**列出所有 `OptionWidgetDefinition`。
mod 只要声明几条 + 一份 `options.xml`，就能得到 100% 原生观感的设置项，
连分类页、滚动、值持久化、悬停提示都不用管。

```csharp
// ① Load()：声明（纯托管，不碰游戏）
UiOptions.DeclareToggle("my_mod", "my_toggle", "general", "我的开关", "说明文字", 0,
    getter: () => _value, setter: v => _value = v);

UiOptions.DeclareSlider("my_mod", "my_volume", "graphics", "音量", 0f, 1f, false,
    order: 1, getter: () => _vol, setter: v => _vol = v, format: v => (v * 100) + "%");

// ② LoadStages 时机（放在你自己已有的 LoadStages prefix 里即可）
UiOptions.Attach(mod);

// ③ PostInit
UiOptions.ApplyProperties();
```

**为什么必须是三次调用（本次实测结论，别合并）：**

| 步骤 | 时机 | 为什么 |
| --- | --- | --- |
| `DeclareXxx` | `Plugin.Load()` | 纯托管，随时都行 |
| `Attach(mod)` | 官方 `ModLoader.LoadStages` | 创建定义 + `Mod.AddDefinition` 必须落在 `LoadDefinitions` 内、`game.AddMod` 之前 |
| `ApplyProperties()` | `Mod.PostInit` | ⚠️ 写早了**会被静默丢弃**：属性键那时还没就绪，`SetProperty` 一声不吭、"已应用"日志照打，但官方渲染时 `GetCategoryID()` 读回 null → 被 `!NamespaceID.IsValid(categoryID)` 过滤掉，界面上什么都不多 |

`options.xml`（`Content/metas/options.xml`）承载文案/分类/顺序；**它其实是可选的**
（声明里也带这些，找不到 meta 时代码会兜底并记一行警告），但正式发布建议写：

```xml
<options>
  <widgets>
    <toggle id="my_toggle" category="general" label="我的开关">
      <tooltip>悬停说明。</tooltip>
    </toggle>
    <slider id="my_volume" category="graphics" label="音量" minValue="0" maxValue="1" wholeNumber="false"/>
  </widgets>
</options>
```

- 节点的**标签名**决定控件类型（`button` / `toggle` / `dropdown` / `slider`），`id` + `category` 必填。
- **`category` 必须是已存在的分类 ID**。合法取值（运行时 dump 出来的）：
  `general` / `graphics` / `gameplay` / `hp_bar` / `control` / `about`
  （裸名按 mod 的默认命名空间解析，`general` → `mvz2:general`）。
  写错了**不会报错**，整个分类组会被静默丢掉。
- 值的持久化：自己的 setter 里存。最省事的是游戏自己的全局选项
  （`Global.Options.SetOptionBool/Int(自己的 NamespaceID, v)` + `SaveOptions()`）。

> 底层设施是 `DSHCore.InjectedDefinitions`（把注入定义接进官方链路：托管定义列表 +
> 拦 `Mod.GetDefinitions` + 跳 `Mod.PostReloadMods` + 钉 GC 根）。要自己造非设置项的定义
> （关卡、buff…）看它的类注释；活样本见 `UiGallery/SampleOptions.cs`（**默认关闭**，
> 开 `General.SampleOptions` 或跑自测就能看到它把两条设置项钉进原版设置界面）。
> 另：注入 `Mod` 实例的 `Namespace` 字段会被 GC 回收，判定归属一律用
> `InjectedDefinitions.NamespaceOf(mod)`，并让 `ModNamespacePin` 把它钉住 —— 否则整套登记会静默失败。

---

## 4. L3 基础 —— 把东西插进原版已有的布局

**核心原则：只克隆 + 插进原版已有的 `LayoutGroup`，坐标/尺寸交给原版算。**

```csharp
using DSHCore.Ui;

// 找到原版某个容器（例如对话框内容区），把我们的一行插进它
var host = UiGame.SceneUi.dialog.buttonRowList._listRoot;   // 只是一个例子
```

`DSHCore.Ui` 提供的定位工具：

| 类型 | 用途 |
| --- | --- |
| `UiGame` | 官方引用链：`Main` / `Scene` / `SceneUi` / `Dialog` / `InputDialog` / `MainCanvas` |
| `UiFinder` | 找**未激活**的节点（`FindObjectOfType` 找不到它们）；`FindAll<T>` / `FindOne<T>` / `FindChild` / `FindByPath` / `PathOf` |
| `UiHost` | 挂哪儿：`Resolve(layer)` / `DialogLayer()` / `CreateNode` / `CreateStretched` / `InsertAfter` / `MarkOwn` |

---

## 5. L3 实战 —— 三个可直接抄的例子

### 例 1：一个「标题 + 正文 + 若干按钮」的面板

```csharp
var shell = UiSkin.Clone(UiSkinPart.Dialog, UiHost.Resolve(UiLayer.DialogLayer), "MyPanel");
var dlg   = shell.GetComponent<MVZ2.UI.CustomDialog>();   // 克隆体上带可用的 CustomDialog
dlg.SetDialog(
    "标题",                                  // ⚠️ 保持短：标题素材只能占一行（见约定 6）
    "正文。\n可以换行。",
    new[] { "上一页", "下一页", "关闭" },       // ⚠️ 别超过 3~4 个：按钮每多一行、框体就长高一行
    index => { /* 0/1/2 … */ });
```

面板高度是 `ContentSizeFitter` 撑的（**内容多高它就多高，不会滚动**），所以：

- 内容多 → 用 §例 3 的列表；
- 正文长 → 用**一个** `Text` 节点 + 换行，不要每行一个节点（实测一份 11 行报告折到 2156px，
  而屏幕只有 725px）。

### 例 2：往内容区插一行控件（复选框 / 滑条 / 单按钮）

```csharp
var buttons = dlg.buttonRowList._listRoot;                // Root/Buttons
var root    = buttons.parent.TryCast<RectTransform>();    // Root（带 VerticalLayoutGroup）

var row = UiOptionRow.Create(null, "Row", UiOptionKind.Toggle);
row.Root.transform.SetParent(root, false);
row.Root.transform.SetSiblingIndex(buttons.GetSiblingIndex());   // 插在按钮区之前
row.SetStretch()                                                 // 铺满内容宽度（必须）
   .SetToggle("我的开关", true, v => { /* v = 新值 */ })
   .SetTooltip("标题", "悬停说明");                                // 原生提示，出现在行的上方

// 换成滑条（标签会自动变成「标签：值」并实时刷新）
// row.SetSlider("音量", 0f, 1f, false, 0.5f, v => { }, null, v => (v * 100) + "%");

// 换成单按钮
// row.SetButton("重置", () => { });
```

> ⚠️ **不要用 `SetDropdown`**：碍于技术问题，下拉框组件无法正常工作，
> 请自行寻找替代方案，如果你对修复该组件有任何建议，请联系
> QoZnoS[https://github.com/QoZnoS]。本库不提供替代实现。

### 例 3：一个可滚动列表

```csharp
// 参数是父节点（跟 UiSkin.Clone 一致：传 null 就自己 SetParent）
var list = UiList.Create(myContentHost, "MyList");

// 视口高度：滚动视图**必须**有有界高度。父容器是 ContentSizeFitter 时用这个；
// 父容器自身有确定高度（占满屏幕的面板）时改用 list.SetFlexible()。
list.SetViewportHeight(UiList.SuggestHeight(0.5f));   // 按主画布高度折算，不写死像素

list.AddButton("第一项", () => { });
list.AddButton("第二项", () => { });

// 也可以加设置项行：
// var row = list.AddOptionRow("开关");  row.SetToggle("开关", true, v => { });
```

实测（`/dshui` 列表页，自测日志）：

```
行数=10  滚动位置=1  视口=380x267
Content=368x510                      ← 10 行撑开，比视口高 → 真的可滚动
ScrollRect：horizontal=0 vertical=1 viewport=…/DSH_Sample_List/Viewport
行1=348x40
```

---

## 6. 七条硬约定（写 UI 时的规范）

1. **绝不算坐标、绝不定尺寸、绝不定义全局缩放。** 插进原版已有的 `LayoutGroup` 里，排版交给原版。
   *唯一的例外是滚动视图的视口高度* —— 它必须有界，用 `UiList.SetViewportHeight(UiList.SuggestHeight())`
   或 `SetFlexible()`，不要写 `420f` 这种魔术数字。
2. **对话框的按钮别超过一行。** 框体高度是 `ContentSizeFitter` 撑的；内容多的场合用 `UiList`。
3. **素材缺失是正常情况**（`Clone` 返回 null、只记一次警告）。标题界面早期拿不到
   `OptionWidgets`（它来自主菜单的设置界面），进主菜单后自动就绪。**不要**把"拿不到"当致命错误。
4. **控件行只能用 `UiOptionRow` 造**（它克隆的是官方四合一容器 `OptionWidgets`）。
   不要单独克隆 `LabeledToggle` / `TextSlider` / `LabeledDropdown`：它们的尺寸契约与提示锚点
   都长在父容器上（`LabeledToggle` 甚至没有 `LayoutElement`），单拎出来就会出现
   "复选框过大 / 滑条看不见"。
5. **不要关 TMP 的 `enableAutoSizing`。** 标题素材靠它把长标题缩进 40px 高的 `TitleArea`；
   关掉就会换成两行并向上溢出框体。要"统一字号"请**换素材**（标题用 `Title`、正文用 `Text`、
   控件行用 `UiOptionRow`），而不是关自动缩放。
   <br>**本库不对素材字号做任何事**：`UiSkin` 克隆出来的东西和原版一模一样
   （`Text` 素材 = 原版 24pt / 自动换行）。字号由**每个使用者自己选**：
   克隆后 `UiClone.SetFontSize(node, size)`（只改字号、不动文本，并会关掉自动缩放）。
   想先知道某个素材出厂多大 → `UiSkin.MeasureFont(part)`；想读现有节点的字号 →
   `UiClone.DescribeFont(node)`。
   <br>**为什么把选择权交出去**：原版字号是为「短文本提示」调的，拿 24pt 写大段说明会
   把不滚动的对话框顶出屏幕；但反过来，"库替你统一成 12pt"会让所有素材的观感被一个
   库常量绑死 —— 这是使用者的版面决策，不是库的。
   （`UiGallery` 自己选的是标题 22 / 正文 14，见 `GalleryPanel.TitleFontSize` / `BodyFontSize`。）
6. **标题保持短。** `Title` 是 36pt + 自动缩放、容器固定 40px 高 —— 它天生只能占一行。
   页号/说明这类信息放进正文（`Desc`）。
7. **提示（tooltip）用 `UiTooltip.Attach`，别自己拼。** 只加 `TooltipAnchor` + `TooltipHandler`
   是不够的（鼠标悬停毫无反应且不报错），而且锚点必须是**目标顶边中点**的独立子节点
   （`pivot=(0.5,0)`、`LayoutElement.ignoreLayout=1`）—— 挂在目标自己身上会让提示正中压在控件上。
   `UiOptionRow` 的行自带正确锚点，用 `row.SetTooltip(...)` 即可。

---

## 7. 坑清单速查（完整版见 `UI_RESEARCH.md` §6）

| 主题 | 记住这一条 |
| --- | --- |
| 查找 | `FindObjectOfType` **跳过未激活对象**，而 UI 素材平时全是关着的 → 用 `UiFinder` |
| 查找 | 不要把自己的克隆体当模板 → 打己方标记，查找时跳过己方祖先链 |
| 克隆 | 克隆体的 `Image.enabled` 可能是 `0` → `UiClone.ForceGraphicsVisible` |
| 克隆 | `Instantiate` 会复制持久化 `onClick` 监听器（闭包指向**原对象**）→ `UiClone.ClearButtonListeners` |
| 克隆 | 模板架必须挂在**激活**的父节点下，否则 `LayoutGroup` 一次都不跑（子节点全停在 `100×100 @0,0`） |
| 布局 | 动态增删子节点后 `LayoutGroup` 不会立刻重排 → `LayoutRebuilder.ForceRebuildLayoutImmediate` |
| 布局 | 父布局组 `forceExpandWidth = 0` 时，子节点不给 `flexibleWidth` 就是 **0 宽**（列表踩过） |
| 布局 | 对话框高度是**内容撑的、而且不能滚**：内容超过屏幕高度时框体直接顶出屏幕（标题整个跑到屏幕外，但所有 `activeInHierarchy` 都是 1，看图才发现）→ 每行都要短、行数要克制；拿不准就量 `_selftest.txt` 里的 `框体` 矩形 |
| 克隆 | `Image.sprite == null` 时 uGUI 画的是**纯白矩形**（不是透明）→ 空图要手动 `alpha = 0`（还需吃射线时）或 `enabled = false` |
| 遮罩 | 换成 `RectMask2D` 之后，原本当 `Mask` 图形用的那个 `Image` 会**开始画自己** —— `Mask.showMaskGraphic = false` 时它只写模板缓冲不画，换了遮罩就变成一个亮框 → `UiSkin` 会自动把它 `alpha = 0`（这就是"列表背景变白"的根因） |
| 文本 | 中文字符串里混进 ASCII 双引号会**截断字符串**并报一堆 `CS1003`（行号还指在别处）→ 用 `「」` |
| 克隆 | 克隆体一被**激活**，挂在它上面的组件就会跑 `Awake` —— 所以「要删的组件」必须在激活**之前**删（`UiClone.Instantiate(..., activate: false)` → 修补 → `UiClone.Activate`），而且要用 `DestroyImmediate`（`Destroy` 帧末生效，同帧照样会 Awake） |
| 克隆 | 原版有些节点上挂着**我们不需要的行生成器**（`ElementList` / `ElementListUI`），它们的 `_template` 可能是空的（运行时才赋值）→ `Awake` 必抛空指针。`UiSkin` 对可滚动容器会自动摘掉它们 |
| 遮罩 | 别用 `Mask`（模板缓冲会让 TMP 的子网格去申请**共享**的 Stencil 材质实例，克隆体与模板共用后互相销毁 → 对方 `OnDisable` 时空指针）→ `RectMask2D`（矩形裁剪、不需要 Sprite） |
| 交互 | il2cpp 事件在 interop 里是**属性**（`add_OnXxx`）→ `DelegateSupport.ConvertDelegate` + 自己持有托管委托 |
| 定义 | **属性要在 `PostInit` 写**：早了（`LoadStages` 时）属性键还没就绪，`SetProperty` 静默丢弃，官方于是把这条设置项过滤掉 —— 而所有日志看起来都成功了 |
| 定义 | 注入类上**不要有返回纯托管类型的方法**：Il2CppInterop 会给每个方法生成代理，报 `has unsupported return type` 警告 → 挪到普通静态 helper 上 |
| 定义 | 注入类实例的字段不被 il2cpp GC 追踪 → `id`/`propertyDict`/`triggers` 一律 `Il2CppPin.Pin(...)`；托管委托则放在**静态**表里（`UiOptions` 就是这么做的） |
| 定义 | 注入 `Mod` 实例的 `Namespace` 字段（`<Namespace>k__BackingField`）**也不被 il2cpp GC 扫描** → 字符串随时可能被回收，`mod.Namespace` 间歇性读到空，所有"按命名空间找本 mod 内容"的补丁**静默失效**（没有任何报错）。用 `ModNamespacePin.Ensure(mod, nsp)` 钉住，判定归属用 `InjectedDefinitions.NamespaceOf(mod)` 而**不是**直接比 `mod.Namespace` |
| 诊断 | 屏幕坐标一律走「局部角点 → `GetWorldCorners` → `WorldToScreenPoint`」，不要 `rect.size * scaleFactor` |

---

## 8. 怎么验收（本仓库的方法：没有"看截图"这条路）

**「节点存在 + 尺寸正确」≠「看得见」。** 必须用数字：

1. 打开 `BepInEx/config/qoznos.mvz2.uigallery.cfg` 的 `SelfTest = true`，
   或者直接跑 VS Code 任务 **「构建 + 自测: UiGallery」**。
2. 报告落在 `<游戏目录>\BepInEx\_selftest.txt`：逐页的**屏幕矩形** + 字号/自动缩放 +
   哪个子控件开着 + 提示锚点位置 + 关键像素差分。
3. 判读：矩形要落在 `[0, Screen.width] × [0, Screen.height]` 内、行与行 y 不重叠、
   `fontSize`/`enableAutoSizing` 是预期值、控件行里只有一个子控件是 `on`。

⚠️ 两个必须遵守的细节：

- **像素 A/B 一定要配"关着"的对照图 + 限制区域**。标题界面自己会动，全屏差分会被动画淹没
  （本仓库就因此把 13.9% 的全屏差异误读过一次）。
- **`ScreenCapture.CaptureScreenshot` 是异步的**（帧末写盘），要隔 ≥20 帧再拍。

工具：`tools/run-selftest.ps1`（跑+报告）、`tools/img-diff.ps1`（像素差分，支持 `-RegionX/Y/W/H`）。

---

## 9. 已知不可用 / 未验证

| 项 | 状态 |
| --- | --- |
| **下拉框（`TMP_Dropdown` 的展开列表）** | ❌ **不可用。** 碍于技术问题，该组件无法正常工作，请自行寻找替代方案，如果你对修复该组件有任何建议，请联系 QoZnoS[https://github.com/QoZnoS]。**本库不提供替代实现。** 细节见 `UI_RESEARCH.md` §6.4-17 |
| L2 设置项（`options.xml` + 定义注入） | ✅ **已交付**（`UiOptions`）。实测：两个注入的设置项（Toggle + Slider）通过官方渲染判据（`可渲染 27/28`），值为 `mvz2:general` / `mvz2:graphics`。细节与踩过的坑见 `UI_RESEARCH.md` §8 |
| 设置项的**实际显示** | ⚠️ 验证方式是**复刻官方渲染判据**（分类有效 + 分类 meta 存在 + 类型模式匹配 + GetValue 可读），没有真的打开设置界面截图。判据本身就是数据驱动的，但保守起见归为"未截图确认" |
| `UiList` 的实际滚动（拖拽/滚轮） | ⚠️ 只验证了"建得出来、行数对、内容比视口高、布局不重叠"；**输入交互没做模拟验证** |
| 关卡内 `UiSkin` 是否仍有效 | ⚠️ 预期有效（`Main` 场景不卸载），未实测 |
