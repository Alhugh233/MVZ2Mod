# DSHCore UI 开发指南（子 mod 用）

> 这份文档是**动手写 UI 时唯一需要读的东西**。
> 它只讲"怎么做"；"为什么是这样"的论证、实测路径与完整坑表在
> [`UI_RESEARCH.md`](UI_RESEARCH.md)，可运行范例在 [`../UiGallery/`](../UiGallery/README.md)。
>
> 全部结论都来自**本机实测**（MVZ2 0.7.0 / x86 / IL2CPP metadata v31.1）。
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
│    └─ ✅ L2：UiOptions.DeclareToggle / DeclareSlider / DeclareChoice             → §3
│         （零 UI 代码，进「插件页 → 模组设置（DSHCore）」，不占用原版设置菜单；
│          想让某一项进原版设置界面就加 scope: VanillaMenu，代价见 §3.1）
│
├─ 要在「插件」页里加一个自己的页面？
│    └─ ✅ L2.5：AddonPage（入口按钮 + 页面壳 + 生命周期全自动）                  → §4
│
├─ 界面上的文字要跟着游戏语言变？
│    └─ ✅ UiLocale（纯托管，不用打语言包也有译文）                              → §11
│
├─ 要往原版已有的面板/对话框里加东西？
│    └─ ✅ L3：UiSkin.Clone + 插进原版 LayoutGroup                              → §5
│
├─ 要一个自己的面板 / 列表 / 常驻窗口？
│    └─ ✅ L3：UiSkin.Clone(Dialog) + UiOptionRow / UiList                      → §6
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

## 3. L2 —— 数据驱动的设置项（默认进「模组设置」页）

**默认路径：声明一句就够了，设置项会出现在「插件页 → 模组设置（DSHCore）」里**，
按命名空间归到对应 mod 名下。不需要 `ModInfo`、不需要注入定义类、不需要 `options.xml`、
不需要三时机 —— 这条路是**零注入**的。

```csharp
// Load()：声明（纯托管，不碰游戏）。就这一句，没有第 ② ③ 步。
UiOptions.DeclareMod("my_mod", "My Mod",                  // 可选但推荐：左栏里显示的名字
    description: "一句话说明（右栏「说明」那一行）",
    author: "你的名字",
    chinese: "我的模组");                                   // 显示名的中文译文（见 §11）

UiOptions.DeclareToggle("my_mod", "my_toggle", label: "我的开关",
    tooltip: "说明文字", getter: () => _value, setter: v => _value = v);

UiOptions.DeclareSlider("my_mod", "my_volume", label: "音量", min: 0f, max: 1f, wholeNumbers: false,
    getter: () => _vol, setter: v => _vol = v, format: v => (v * 100) + "%");

UiOptions.DeclareChoice("my_mod", "my_level", label: "强度", items: new[] { "低", "中", "高" },
    getter: () => _level, setter: i => _level = i);
```

- **值怎么存由你决定**（BepInEx 配置项 / `Global.Options` / 静态字段都行）——
  声明只要求一个 `getter` 和一个 `setter`。
- 三种控件：复选框 / 滑条 / **多值选择**。多值在模组设置页里是**按钮行**
  （「标签：当前值」，点一下切到下一个值），因为原版下拉框的展开列表在本环境画不出来（§10）。
- `category` 参数只在「同时进原版设置界面」时才用得到，走默认路径时可以随便填。
- 页面本身在 `DSHCore.Plugin.Load` 里就登记好了（内建，不需要任何 mod 去开）。
- **`DeclareMod` 还决定「这个 mod 会不会出现在列表里」**：声明过的命名空间**无条件列出**，
  哪怕它既没注册 `ModInfo`、也没有任何设置项 —— 纯补丁 mod（只有 Harmony 补丁、
  没有任何游戏内容，例如 `Replay`）就靠这一句露脸。不声明又没有设置项 = 列表里看不到它。
- 说明 / 作者 / 显示名都是**可翻译的源串**（§11）；没声明「说明」「作者」时右栏显示「（未声明）」。


### 3.1 另一条路：放进**原版设置界面**

原版设置界面（主菜单 / 地图 / 关卡三处共用）会**自动**列出游戏认得的 `OptionWidgetDefinition`。
想让某一项出现在那里就显式写 `scope: UiOptionScope.VanillaMenu` ——
它要完整走一遍官方链路（三时机 + `options.xml` + 一个真的 `Mod` 实例）：

```csharp
UiOptions.DeclareToggle("my_mod", "my_toggle", category: "general", label: "我的开关",
    getter: () => _value, setter: v => _value = v,
    scope: UiOptionScope.VanillaMenu);      // ← 唯一的区别

// ② 在你自己已有的 LoadStages prefix 里（官方 ModLoader.LoadStages 时机）
UiOptions.Attach(mod);

// ③ Mod.PostInit
UiOptions.ApplyProperties();
```

**为什么这一步必须是三次调用（实测结论，别合并）：**

| 步骤 | 时机 | 为什么 |
| --- | --- | --- |
| `DeclareXxx` | `Plugin.Load()` | 纯托管，随时都行 |
| `Attach(mod)` | 官方 `ModLoader.LoadStages` | 创建定义 + `Mod.AddDefinition` 必须落在 `LoadDefinitions` 内、`game.AddMod` 之前 |
| `ApplyProperties()` | `Mod.PostInit` | ⚠️ 写早了**会被静默丢弃**：属性键那时还没就绪，`SetProperty` 一声不吭、"已应用"日志照打，但官方渲染时 `GetCategoryID()` 读回 null → 被 `!NamespaceID.IsValid(categoryID)` 过滤掉，界面上什么都不多 |

`options.xml`（`Content/metas/options.xml`）承载文案/分类/顺序：

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

> 底层设施是 `DSHCore.InjectedDefinitions`（把注入定义接进官方链路：托管定义列表 +
> 拦 `Mod.GetDefinitions` + 跳 `Mod.PostReloadMods` + 钉 GC 根）。要自己造非设置项的定义
> （关卡、buff…）看它的类注释；活样本见 `UiGallery/SampleVanillaOptions.cs`（**默认关闭**，
> 开 `General.SampleVanillaOptions` 就能看到它把两条设置项钉进原版设置界面）。
> 另：注入 `Mod` 实例的 `Namespace` 字段会被 GC 回收，判定归属一律用
> `InjectedDefinitions.NamespaceOf(mod)`，并让 `ModNamespacePin` 把它钉住 —— 否则整套登记会静默失败。

---

## 4. 插件页子页面（`AddonPage`）—— 在「插件」页里加一个自己的页面

**主菜单 → 更多选项 → 插件** 那一页是游戏留给扩展内容的地方。DSHCore 把「在那儿加一个页面」
整件事封成了 `AddonPage`：入口按钮的安装与文案纠正、页面壳的克隆与修补、
「藏起插件页索引 → 叠上自己的面板」、离开插件页自动收起、以及心跳驱动 —— 全都不用自己写。

```csharp
internal sealed class MyPage : AddonPage
{
    public override string Id { get { return "MyPage"; } }           // 唯一 id（节点名 / 日志）
    public override string EntryLabel { get { return "我的页面"; } }  // 插件页上的按钮文案
    public override string Title { get { return "我的页面"; } }       // 页面标题

    public MyPage() { Register(); }        // ← 构造时登记（幂等）

    protected override bool BuildContent(AddonShell shell)
    {
        // PreparePanel：杀掉它自带的 Scroll View + 让框体美术不参与排版 + 保证有纵向布局组
        var body = shell.PreparePanel(shell.Node("Center/List"));
        if (body == null) return Fail("页面壳里找不到 Center/List。");

        var list = UiList.Create(body.transform, "MyList");
        if (list == null || !list.IsValid) return Fail("可滚动列表建不出来。");
        list.SetFlexible();
        list.AddButton("你好", () => Plugin.Log.LogInfo("点了"));
        return true;
    }
}
```

然后：

| 事项 | 怎么做 |
| --- | --- |
| 让页面出现在插件页 | 什么都不用做 —— 构造（`Register()`）之后心跳会自己把入口装上 |
| 心跳 | **DSHCore 自己驱动**（挂在 `MainSceneController.Update` 上），不要再自己挂一份 |
| 两种版式 | `ShellSource = AddonsIndex`（单栏，默认，与「资源包」「回放」同款）/ `ArchiveIndex`（左右两栏，与「对话档案」同款） |
| 面板宽度 | `WidthTargetPath` + `WidthRatio`（**比例**，不是像素）。原版面板写死 500 单位，一行里多放几格就放不下 |
| 两栏版式的**合计**宽度 | `TwoColumnWidthRatio`（比例）。左栏保持 prefab 宽度、**右栏吸收全部增量**，两栏整体重新水平居中（`UiKit.SetTwoColumnWidth`）。想让总宽与某个单栏页面一致时，两边填同一个比例 |
| 两栏版式的栏位路径 | `LeftColumnPath` / `RightColumnPath`（默认 `Center/Filter` / `Center/List`） |
| 标题行太高、想给内容区让高度 | `TopHeight`（单位，0 = 用 prefab 原值 80）。壳是纵向布局组、`Center` 是弹性那一个，所以调小标题行 = 内容区自动变高 |
| 「先切页、页面就绪后再打开」 | `RequestOpen()`；`PumpNow()` 是「同帧抢页面」（用于从关卡直接落到本页，别让玩家看到插件页索引闪一下） |
| 「关掉本页之后去哪」 | `SetReturnTo(action)`；非 null 时**不会**因为离开插件页而自动收起 |
| 不想装入口 / 不想开页面的时机 | override `Suspended()`（例如「回放」在播放中） |
| 面板建不出来时的提示 | override `OnBuildFailed(error)`（有对话框能力的子类可以弹提示） |
| 验收 | `EntryInstalled` / `Root` / `LastError` / `DescribeEntry()`；全局状态用 `AddonPages.Report()` |

**内建与现成的两个实现**：

- `DSHCore.Ui.ModSettingsPage`（「模组设置（DSHCore）」）—— 左栏已加载的 mod、右栏详细信息与设置项，
  版式取自 `ArchiveIndex`（两栏合计宽 = 0.94 画布宽，与「回放」页同宽）；
  左栏条目是**档案页自己的条目行**（`UiTagRow`，见 §4.1），它同时是 §3 那条设置项链路的**消费者**。
- `Replay/Ui/ReplayPage.cs` —— 单栏版式（`AddonsIndex`），列表里放回放条目。

### 4.1 档案样式的条目行（`UiTagRow`）

「像对话档案那样的一行」= 原版 `ArchiveTagItem`（`Toggle` + 文字）。
`UiTagRow` 从档案页左栏那个 `ElementList` 的**条目模板**克隆，所以行高、勾选条、悬停视觉
全是原版像素同源，不用自己描一个。

```csharp
UiTagRow.Template(left.transform);          // 先从壳里捞出模板（必须在清空那棵树之前）
var row = UiTagRow.Create(list.Content, "Row0", "我的条目", isOn: true, v => Select(0));
list.Add(row);
UiTagRow.Set(row, "改个文案", isOn: false);  // 改文案 / 改勾选（不回调）
```

- **勾选 = 选中**：行的勾选条铺满整行（绿色），悬停时另有一层白色半透明条。
- 行的点击由它自己的 `Toggle` 处理，本库只订阅 `OnValueChanged` 做单选归一。
- ⚠️ **必须让 `UiTagRow.Create` 造行**：行的色块是**无 sprite 的纯色 Image**，
  而整备页面壳时 `UiKit.ClearEmptySpriteAlpha` 会把这类图清成透明 ——
  `Create` 内部会把它们还原回来（否则文字在、看不出选了哪一条）。
- 拿不到素材时 `Create` 返回 null：**降级用 `UiList.AddButton`**，功能不要丢。

⚠️ **不要**自己克隆插件页索引、也不要自己插入口按钮：两个页面各插一次会互相撞车
（各自 `MakeVertical` 同一个列表容器、各自把文案写回去），而且「入口文案会被插件页自己的
刷新回调写回原样、需要在心跳里纠正」这个细节只有一处实现才不会分叉。

### 4.2 两栏页面的排版要点（照抄「模组设置」页就行）

`ModSettingsPage` 是两栏版式的参考实现（左栏列表 + 右栏详情）。四个可复制的结论：

| 想要的效果 | 怎么做 | 为什么 |
| --- | --- | --- |
| **列表条目紧密排列、并且填满框体** | `list.SetRowFit(true, spacing: 2f)` | 原版列表素材的行间距是 10 单位；而**内容容器自己比视口窄一圈**（实测 12 屏幕像素），行因此看着没填满框体 |
| 栏目标题比正文醒目、又不占高度 | `UiKit.Label(..., fontSize: 18f, ..., preferredHeight: 26f)` + `UiKit.SetBold(tmp)` | 原版行高是 40 单位，标题只占一行 → 一半是空白；标题行矮下来，弹性高度的列表自己就变高了 |
| 信息行之间不要那么空 | `UiKit.Fit(row, -1f, -1f, 1f)` + `UiKit.AutoHeight(...)`（**别给固定高度**） | 行高由里面最高的那个文本决定（单行 ≈ 19 px，而原版控件行是 51 px） |
| 长文本换行而不是被截断 | 值用 `UiKit.Paragraph(...)`（`Label` 是省略号截断） | `Label` 是给「一行里摆好几格」用的 |

**原版分割线**：`UiKit.Separator(parent, name)` → 克隆设置界面里的 `CategoryTemplate/Title/Line`
（2 单位高、铺满宽、**无 sprite 的纯色 Image**，就是原版分类标题下面那条）。
拿不到时返回 null，降级成一行空白 —— **不要**用文字画分割线。

---

## 5. L3 基础 —— 把东西插进原版已有的布局

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

## 6. L3 实战 —— 三个可直接抄的例子

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

## 7. 七条硬约定（写 UI 时的规范）

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

## 8. 坑清单速查（完整版见 `UI_RESEARCH.md` §6）

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
| 克隆 | 但**无 sprite 的纯色图也可能是正当视觉**（档案条目行的勾选条/悬停条就是）—— 整备页面壳时的一刀切清 alpha 会连它们一起清掉，克隆出来的行就「文字在、看不出选了哪一条」。`UiTagRow.Create` 内部会按「rgb 还在、alpha 被清」的指纹还原 |
| 遮罩 | 换成 `RectMask2D` 之后，原本当 `Mask` 图形用的那个 `Image` 会**开始画自己** —— `Mask.showMaskGraphic = false` 时它只写模板缓冲不画，换了遮罩就变成一个亮框 → `UiSkin` 会自动把它 `alpha = 0`（这就是"列表背景变白"的根因） |
| 文本 | 中文字符串里混进 ASCII 双引号会**截断字符串**并报一堆 `CS1003`（行号还指在别处）→ 用 `「」` |
| 克隆 | 克隆体一被**激活**，挂在它上面的组件就会跑 `Awake` —— 所以「要删的组件」必须在激活**之前**删（`UiClone.Instantiate(..., activate: false)` → 修补 → `UiClone.Activate`），而且要用 `DestroyImmediate`（`Destroy` 帧末生效，同帧照样会 Awake） |
| 克隆 | 原版有些节点上挂着**我们不需要的行生成器**（`ElementList` / `ElementListUI`），它们的 `_template` 可能是空的（运行时才赋值）→ `Awake` 必抛空指针。`UiSkin` 对可滚动容器会自动摘掉它们 |
| 遮罩 | 别用 `Mask`（模板缓冲会让 TMP 的子网格去申请**共享**的 Stencil 材质实例，克隆体与模板共用后互相销毁 → 对方 `OnDisable` 时空指针）→ `RectMask2D`（矩形裁剪、不需要 Sprite） |
| 交互 | il2cpp 事件在 interop 里是**属性**（`add_OnXxx`）→ `DelegateSupport.ConvertDelegate` + 自己持有托管委托 |
| 交互 | `DisplayAddons(null)` = **一屏黑**：`MainScenePage.Return()` 是 `OnReturnClick?.Invoke(); Hide();`，回调为空时只把页面藏起来、没有东西顶上来。程序化调用必须自己传回调（DSHCore 对 `DisplayAddons` 挂了前缀兜底，但自己传才是正解） |
| 文本 | **统一取词会把「原文」也换掉**：`AddInfo` 之类的值过一遍 `UiLocale.Get` 之后，「名称原文 = MVZ2 UI Gallery」就变成中文名了 → 需要原文的地方要显式跳过取词 |
| 布局 | **想让行高由内容（换行后的文本）决定，就不能给 `LayoutElement.preferredHeight` 填 0**：`LayoutElement` 的 `layoutPriority`（1）高于 TMP（0），任何 ≥ 0 的返回值都会把 TMP 自己算的换行高度盖掉。要么不挂 `LayoutElement`，要么把三项高度都清成 -1（`UiKit.AutoHeight`） |
| 诊断 | interop 类型上的 `params object[]` 会生成**两个重载**（`Il2CppReferenceArray<T>` 与 `T[]`）→ `AccessTools.Method` 找方法时要把四个参数类型都写全，否则歧义 |
| 定义 | **属性要在 `PostInit` 写**：早了（`LoadStages` 时）属性键还没就绪，`SetProperty` 静默丢弃，官方于是把这条设置项过滤掉 —— 而所有日志看起来都成功了 |
| 定义 | 注入类上**不要有返回纯托管类型的方法**：Il2CppInterop 会给每个方法生成代理，报 `has unsupported return type` 警告 → 挪到普通静态 helper 上 |
| 定义 | 注入类实例的字段不被 il2cpp GC 追踪 → `id`/`propertyDict`/`triggers` 一律 `Il2CppPin.Pin(...)`；托管委托则放在**静态**表里（`UiOptions` 就是这么做的） |
| 定义 | 注入 `Mod` 实例的 `Namespace` 字段（`<Namespace>k__BackingField`）**也不被 il2cpp GC 扫描** → 字符串随时可能被回收，`mod.Namespace` 间歇性读到空，所有"按命名空间找本 mod 内容"的补丁**静默失效**（没有任何报错）。用 `ModNamespacePin.Ensure(mod, nsp)` 钉住，判定归属用 `InjectedDefinitions.NamespaceOf(mod)` 而**不是**直接比 `mod.Namespace` |
| 诊断 | 屏幕坐标一律走「局部角点 → `GetWorldCorners` → `WorldToScreenPoint`」，不要 `rect.size * scaleFactor` |

---

## 9. 怎么验收（本仓库的方法：没有"看截图"这条路）

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

## 10. 已知不可用 / 未验证

| 项 | 状态 |
| --- | --- |
| **下拉框（`TMP_Dropdown` 的展开列表）** | ❌ **不可用。** 碍于技术问题，该组件无法正常工作，请自行寻找替代方案，如果你对修复该组件有任何建议，请联系 QoZnoS[https://github.com/QoZnoS]。**本库不提供替代实现。** 细节见 `UI_RESEARCH.md` §6.4-17 |
| L2 设置项（`options.xml` + 定义注入） | ✅ **已交付**（`UiOptions`）。实测：两个注入的设置项（Toggle + Slider）通过官方渲染判据（`可渲染 27/28`），值为 `mvz2:general` / `mvz2:graphics`。细节与踩过的坑见 `UI_RESEARCH.md` §8 |
| 设置项的**实际显示** | ⚠️ 验证方式是**复刻官方渲染判据**（分类有效 + 分类 meta 存在 + 类型模式匹配 + GetValue 可读），没有真的打开设置界面截图。判据本身就是数据驱动的，但保守起见归为"未截图确认" |
| `UiList` 的实际滚动（拖拽/滚轮） | ⚠️ 只验证了"建得出来、行数对、内容比视口高、布局不重叠"；**输入交互没做模拟验证** |
| 关卡内 `UiSkin` 是否仍有效 | ⚠️ 预期有效（`Main` 场景不卸载），未实测 |
| 档案条目行（`UiTagRow`） | ✅ 已截图确认：文字 + 选中行的绿色整行条都与档案页同源。**点击切换只做了代码路径分析，没模拟鼠标** |
| 两栏合计宽度（`UiKit.SetTwoColumnWidth`） | ✅ 实测：左 300 + 间距 20 + 右 432 = 752 单位（= 画布 800×0.94），与「回放」页的 963 屏幕像素完全一致 |

---

## 11. 模组文本本地化（`UiLocale`）

### 11.1 游戏那一套是怎么回事

- **源语言是 `zh-Hans`**（`LanguageManager.SOURCE_LANGUAGE`）—— 游戏自己的文案就写中文，
  其它语言是 catalog 里查出来的；默认语言也是 `zh-Hans`。
- 唯一的查找入口是 `LanguageManager.TryGetLocalizedString(text, language, out str)`：
  依次问每个 `ICatalogProvider`，全都没命中就返回**原文**。
  `Global.Localization.GetText(...)` / `_(...)` / TMP 上的 `TextMeshProUGUITranslator`
  最终都走到这里。
- **官方的 mod 多语言 = 语言包资源包**：`assets/<nsp>/<lang>/*.mo`（gettext），
  由 `ResourcePackManager` 读成 `ResourcePack`（它自己实现 `ICatalogProvider`）
  再 `LanguageManager.AddCatalogProvider`。玩家在 **插件页 → 语言包** 里启用。
  —— **出正式多语言就打包这个，不需要写代码。**

### 11.2 本库给的第二条路：`UiLocale`（几行代码，不用打包）

```csharp
// Load() 里声明（纯托管）
UiLocale.Add("Better Furnace", "更好的熔炉");                     // 省略语言 = 源语言(zh-Hans)
UiLocale.Add("Better Furnace", "Better Furnace", UiLocale.English);

string label = UiLocale.Get("更好的熔炉");                         // 当前语言下的文案
```

它把自己挂进游戏的查找链（由 `Plugin.Load()` 调 `UiLocale.Apply()`；挂不上只影响「别人查我们」，
`UiLocale.Get` 自己永远生效）。启动会打一条 `[UiLocale] 已挂进游戏文本查找链（补丁 N/4 处）`；
挂链**自检失败**时才会再打一条警告 —— 没有警告就是好的，别猜。

它挂的是**两条**查找（各两个重载）：

| 挂在哪 | 管什么 |
| --- | --- |
| `TryGetLocalizedString(text, lang, out, args)` | 普通界面文字（无上下文条目） |
| `TryGetLocalizedStringParticular(context, text, lang, out, args)` | **游戏内容名** —— 关卡名 / 诅咒名与说明 / 实体名 / 难度名…全部走这条（见 §11.6） |

### 11.2.1 推荐写法：一个 mod 一张表（`UiLocale.Table`）

直接在 `Load()` 里堆一长串 `UiLocale.Add` 很快会失控（看不出属于谁、容易漏语言、
分不清「内容名」与「界面文字」）。改成一张表：

```csharp
// Localization.cs —— 一个 mod 一个文件，全部文案都在这里
internal static class LabLocale
{
    private static readonly UiLocaleTable T = UiLocale.Table("mvz2_lab");

    public static void Register()
    {
        T.En("实验室小游戏（real_prologue / zero_k_space / unknow_palace）",
             "Laboratory minigames (real_prologue / zero_k_space / unknow_palace)");

        // 游戏内容名：必须带上下文
        T.EnAllWith(LogicStrings.CONTEXT_LEVEL_NAME,
            "真正的序章", "Real Prologue",
            "零度空间", "Zero-K Space");
    }
}
```

在 `Plugin.Load()` 里调一次 `LabLocale.Register()`（早于任何界面/关卡取词）。

| 方法 | 用途 |
| --- | --- |
| `Add(源串, 译文, 语言?, 上下文?)` | 单条（最底层） |
| `Both(源串, 英文, 中文?, 上下文?)` | 一次登记两种语言 |
| `En(中文, 英文, 上下文?)` | 「中文源串 → 英文」简写（最常用） |
| `Zh(源串, 中文, 上下文?)` | 「英文源串 → 中文」（mod 自述名这类） |
| `EnAll("甲","A","乙","B")` | 批量简写 |
| `EnAllWith(上下文, "甲","A",…)` | 批量 + 同一上下文（内容名成片登记） |

### 11.2.2 界面文字是**自动**取词的（写文案时不用管）

本库的界面辅助函数在画字的时候会过一遍 `UiLocale.Get`，所以 **mod 只要把译文登记好，
界面就会跟着游戏语言变** —— 不必在每个调用点包一层：

| 入口 | 行为 |
| --- | --- |
| `UiKit.Label` / `UiKit.Paragraph` / `UiKit.TextButton` | ✅ 自动（`TextButton` 的第 2 参是**节点名**，不过取词；第 3 参才是文案） |
| `UiOptionRow.SetLabel` / `SetTooltip` 等 | ✅ 自动 |
| `UiList.SetButtonLabel` | ✅ 自动 |
| `AddonPage.EntryLabel` / `Title` | ✅ 自动（显示走 `EntryLabelText` / `TitleText`；原属性保持稳定，日志里打的是原文） |
| 游戏原生的 `CustomDialog` / `ShowDialog`（自己拼的对话框） | ❌ 得自己 `UiLocale.Get(...)` —— 它拿到什么就画什么 |

> 没登记的文本会**原样通过**，所以「先写中文、以后补英文」完全可行。
> 要显示不可翻译的原文（例如「名称原文」那一行）就直接用 `UiClone.SetLabel` 绕开取词。

| 事项 | 怎么做 |
| --- | --- |
| 只给自己界面用 | `UiLocale.Get(源串)`；查不到返回源串，不补译文也不会崩 |
| 让游戏里的别处也读到（例如 mod 自己注册的实体名） | 什么都不用做 —— 补丁挂上之后全局生效 |
| 源串写什么 | **语言中立的那一份**（一般是英文名）。源语言是中文，所以中文用 `Add(key, 中文)` 补 |
| 界面里的一整句中文 | 直接以中文为源串，再 `AddAll(UiLocale.English, "中文", "English", …)` 补英文（`ModSettingsPage.DeclareTexts` 就是这么做的） |
| 列表里的 mod 名 | `UiOptions.DeclareMod(space, 源串, 说明, 作者, 中文)` —— 声明一次，名字/说明/作者全有了 |
| 设置项文案 | 声明时给的 label/tooltip 就是源串，模组设置页会过一遍 `UiLocale.Get` |

### 11.3 为什么不实现 `ICatalogProvider`

托管类要实现 il2cpp 接口得走 `ClassInjector` 的接口代理，而这套环境里接口分派是有坑的
（`MetaInjection` 就是为了绕开它才改成直读磁盘 XML）。改在**查找入口挂前缀**与
「多注册一个 provider」效果等价，且失败模式温和（挂不上只是不生效，有日志）。

### 11.4 坑

- **带参数的串（`{0}`）不走本表**：官方 provider 命中后会自己 `string.Format`，
  而我们拿到的是 il2cpp 的 `object[]`；本库只接管**不带参数**的查找（带参数的直接放行）。
- **取词时机**：模组设置页是在**打开时**取词的，所以玩家切语言后重进本页即可看到新语言；
  页面上已经画出来的文字不会自动重刷。
- `MVZ2.Localization.TextMeshProUGUITranslator` 会让 TMP **自动跟着语言变**
  （组件上配 Context + Key）。本库对**克隆来的原版节点**一律摘掉它
  （`UiKit.StripTextTranslators`，否则你改的文案会被它写回去）；
  自己的文字请走 `UiLocale`，不要指望那个组件。
- 官方语言包里的 `zh-Hans` catalog 是合法的（可以覆盖源串）——
  所以「英文当 key、中文写在 catalog 里」这条路也是通的。

### 11.5 第三条路：外部文件（玩家自己加语言，不用改 mod）

想让「不碰 mod 本体也能加语言」，就不能把译文只写在代码里。`UiLocale` 除了代码表，
还会读一份**玩家可编辑的文件**，取词顺序是：

```
外部文件 → 代码里的 Add 表 → 游戏本地化（语言包 .mo / catalog）→ 原样返回源串
```

**放哪（`<游戏目录>/BepInEx/config/DSHCore/lang/`）**

```
lang/zh-Hans.txt        主文件 —— 玩家直接编辑这一份
lang/zh-Hans.d/*.txt    附加文件 —— 按文件名排序，可放多份，后加载的覆盖先加载的
```

语言码里的 `-` 也接受写成 `_`（`zh_Hans.txt`），两种都读。

**写什么**（UTF-8；`#` / `;` 开头是注释；空行忽略；`=` 取第一个，译文里可以再出现 `=`）

```ini
MVZ2 Loadout = Graphical Console
UI 组件画廊 = UI Gallery
```

转义：`\n` 换行、`\t` 制表符、`\\` 反斜杠。键**必须与 mod 里的源串逐字符一致** —— 
照抄 `/dshtrans keys` 的输出最保险。

**译文留空（`键 =`）等于「还没译」**：这一条会被跳过，取词继续往下走。所以
`/dshtrans write` 导出的模板可以直接当工作稿 —— 没填的行不会把界面变成空白。

**游戏内指令 `/dshtrans`**

| 指令 | 作用 |
| --- | --- |
| `/dshtrans` | 状态：外部目录、当前语言读到了几条、见过多少源串 |
| `/dshtrans keys` | 列出全部已知源串与它们当前的取值 |
| `/dshtrans write en-US` | 把键表写成 `lang/en-US.txt` 模板（**已存在就不覆盖**；加 `overwrite` 强制） |
| `/dshtrans reload` | 重新读盘（改完文件不用重启游戏） |

**关键约束：它不会凭空让游戏多出一种语言。**
mod 的显示语言始终跟着**游戏设置**走，而游戏的语言列表来自 catalog provider —— 
只有玩家在 **插件 → 语言包** 里启用了一份支持该语言的语言包，这个语言码才存在，
我们的 `lang/<该语言码>.txt` 才会被读到。所以流程是：
**先导入并启用语言包 → 再放/编辑 `<语言码>.txt` → 重进页面（或 `/dshtrans reload`）**。

| 事项 | 怎么做 |
| --- | --- |
| 加载时机 | 每种语言**第一次取词时惰性读一次**，之后走缓存（热路径只有一次字典查找） |
| 文件不存在 | 静默跳过 —— 没放文件就是没有外部译文，不会有任何警告 |
| 读到了文件 | 打**一条** `[UiLocale] 已加载外部译文（<语言>）：<文件名（N 条）> 目录 …` |
| 解析出错 | 打警告并跳过该文件（不影响代码表与语言包） |
| 空译文 | `键 =`（右边为空）= 还没译 → 跳过这一条，取词继续往下走 |
| 导出模板 | `WriteTemplate(lang)` / `/dshtrans write <lang>`；键表来自本次会话**实际查过**的源串（`KnownKeys`） |
| 诊断 | `UiLocale.Report()` / `DescribeExternal(lang)` / `DumpKeys()` |

### 11.6 游戏内容名：一定要带**上下文**

这是 mod 多语言最容易漏的一半，也是唯一「翻完觉得没生效」的常见原因。

游戏里有两套查找：普通查找（§11.2.1 那套）与**带上下文的查找**。名字类文本全部走后者：

| 内容 | 上下文常量 |
| --- | --- |
| 关卡名（`stages.xml` 的 `name`） | `LogicStrings.CONTEXT_LEVEL_NAME` = `levelname` |
| 诅咒名 / 诅咒说明 | `LogicStrings.CONTEXT_CURSE_NAME` / `CONTEXT_CURSE_TOOLTIP` |
| 实体名 / 说明 / 死亡信息 | `CONTEXT_ENTITY_NAME` / `CONTEXT_ENTITY_TOOLTIP` / `CONTEXT_DEATH_MESSAGE` |
| 制品、难度、音乐、图鉴标签… | `LogicStrings.CONTEXT_*` 里对应那个 |

显示时游戏会 `_p(上下文, 名字)` → `LanguageManager.TryGetLocalizedStringParticular(context, text, …)`。
**所以 mod 的 XML 里写的中文名字，只有登记成带上下文的条目才会被翻。**

```csharp
// ✅ 对：关卡选择列表、关卡内标题、统计页都会跟着语言变
T.En("真正的序章", "Real Prologue", LogicStrings.CONTEXT_LEVEL_NAME);

// ❌ 错：只在你自己用 UiLocale.Get 的地方生效，游戏自己的关卡列表依然是中文
T.En("真正的序章", "Real Prologue");
```

**外部文件里带上下文的行**写成 `[上下文] 源串 = 译文`：

```ini
[curse.name] 怪物的大脑 = The Creature's Brain
[curse.tooltip] 器械受到伤害时漏电 3s。 = Contraptions leak electricity for 3s when damaged.
[levelname] 真正的序章 = Real Prologue
```

**键必须逐字符一致** —— 带富文本标记的名字（`<color=#FF4040>永恒之心</color>`）
标记也是键的一部分（游戏是把整串拿去做查找的）。漏了标记就查不到，而**不会报任何错**。

> 上下文查找**不做回退**：带上下文的查询不会命中不带上下文的条目。
> 这是故意的 —— 游戏会对官方的**每一条**名字做这种查找，若允许回退，
> mod 界面里的泛词（「音乐」「背景」「基础」…）就会掺进官方内容名的翻译里。
