# MVZ2 Modular Curse（模块化诅咒）

> 《我的世界僵尸 2》（Minecraft VS Zombies 2）0.7.0 的社区内容 mod。
> 把自定义诅咒**接入原版诅咒系统**，并按「诅咒池」组织与切换。

- 插件 GUID：`qoznos.mvz2.modularcurse`
- 游戏内命名空间：`modular_curse`
- 当前版本：**0.2.1**
- 前置：**CustomCore**（`qoznos.mvz2.core`，必需）
- 适配：MVZ2 **0.7.0 test-4**（Windows **x86** / IL2CPP / Unity 2022.3.62f3）

> ⚠️ 社区制作的**非官方**内容，与游戏原作者无关。
> 📖 **每一条诅咒的效果与实现方式、诅咒池 UI 的实现细节，见 [CURSES.md](CURSES.md)。**

---

## 它做了什么

0.7.0（test-4 起）加入了**原生诅咒系统**：

```
curses.xml (CurseMeta)
  → ModLoader.LoadCurseMetas → DefaultCurseDefinition（带 BUFF_ID）
  → 关卡开始前的诅咒选择面板（LevelBlueprintChooseController）
  → Level.AddBuff(buffId)
  → CurseBuffDefinition（关卡级 buff）负责实际效果
```

本 mod **完全走这条官方链路**：注册自己的 `ModInfo`、用 `curses.xml` 提供条目、
用 `CurseBuffDefinition` 实现效果。原版那 7 条诅咒不受影响。

### 诅咒池

诅咒弹窗（标题「选择诅咒」）的滚动区**最上方**多了一行三个「池」条目，
下面一条横线，横线下才是真正的诅咒列表：

| 池 | 图标 | 诅咒 | 描述 |
| ---- | ---- | ---- | ---- |
| 原版池（默认） | 原版诅咒按钮图标 | `mvz2:*` 那 7 条 | 原版诅咒 |
| QC 池 | `curse_qc.png` | `modular_curse:qc_c1` … `qc_c12`（**12 条**） | QoZnoS的诅咒 |
| SC 池 | `curse_sc.png` | `modular_curse:sc_c1` … `sc_c13`（**13 条**） | 奇硅箱的诅咒 |

池是**单选且必选**的；换池后列表只显示该池，**其它池已勾选的自动取消**；
选择**按关卡分别记住**（存在 `userdata/userN/modular_curse/curse_pool.txt`）。

加新池只需改三处（UI 自动排版），详见 `CURSES.md`。

---

## 构建

```powershell
cd ModularCurse
.\build.ps1                 # 构建（含前置 CustomCore）+ 自动部署
.\build.ps1 -Pack           # 额外打包 dist\ModularCurse_v0.2.1_0.7.0.zip
.\build.ps1 -Pack -Launch   # 打包后启动游戏冒烟
```

| 参数 | 作用 |
| ---- | ---- |
| `-GameDir <dir>` | 部署目标。默认 `E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-4`；不存在则**只构建不部署** |
| `-SkipBuild` | 跳过构建，直接用已有产物 |
| `-Clean` | 先删除 `bin/` `obj/` |
| `-KeepGame` | 不结束正在运行的游戏（会锁住 DLL，一般不要用） |
| `-Pack` | 打包 zip 到 `dist\` |
| `-Launch` | 构建/打包后启动游戏 |

工程通过 `ProjectReference` 引用 `CustomCore`，构建本 mod 会**一并构建前置**；
本 mod 的包体**不含** BepInEx 运行环境（需先安装 CustomCore 的安装包），但会带上 `CustomCore.dll`。

---

## 资源（`assets/`）

`assets/` 的**内容**直接映射到游戏的
`MinecraftVSZombies2_Data/StreamingAssets/Mods/modular_curse/`：

```
assets/
└─ Content/
   ├─ metas/
   │   ├─ curses.xml    25 条诅咒的元数据（CurseMeta）
   │   └─ buffs.xml      对应的 buff 词条
   └─ sprites/
       ├─ curse_qc.png   QC 池图标
       └─ curse_sc.png   SC 池图标
```

> XML 由 `CustomCore.MetaInjection` 直读磁盘注入；
> 图标由本 mod 在运行时注册进游戏资源库。
> **注意：metas 的 XML 里不要写注释**（`Comment` 节点会触发 NRE）。

---

## 目录结构

```
ModularCurse/
├─ build.ps1                    构建 / 部署 / 打包脚本
├─ README.md                    本文件
├─ CURSES.md                    诅咒清单与实现细节（完整设计文档）
├─ ModularCurse.csproj          工程文件（ProjectReference → CustomCore）
├─ assets/                      Mod 资源（映射到 StreamingAssets/Mods/modular_curse/）
├─ Plugin.cs                    入口：CoreRegistry 登记 + 注入类注册 + 订阅 OnRegisterModInfos
├─ ModularCurseMod.cs           Mod 逻辑主体
├─ Il2CppPin.cs                 GC 句柄钉住工具
├─ CurseIds.cs                  诅咒 ID 表
├─ CurseDefinitionPatch.cs      拦截官方 LoadStages 时机创建诅咒 buff 定义
├─ CurseMetaApplier.cs          把 XML meta 套到注入 def
├─ CurseRuntime.cs / CurseState.cs        运行时状态
├─ CurseHooks.cs                各条诅咒的 Harmony 挂钩
├─ CurseCommand.cs              /curse 调试指令
├─ CursePool*.cs                诅咒池（枚举 / 规则 / 状态 / UI 行 / 图标）
├─ CurseTooltip.cs              池条目悬停提示
├─ CurseDialogProbe.cs          诅咒弹窗探针（诊断）
└─ CurseBuffs/
    ├─ CurseBuffBase.cs         诅咒 buff 基类
    ├─ CurseBuffs.cs            注册表
    ├─ QcBuffs.cs               QC 池 12 条 buff
    └─ ScBuffs.cs               SC 池 13 条 buff
```

---

## 调试

游戏调试控制台（debug 帐号）里多了一条指令：

```
/curse all    强制显示所有诅咒池（不按池过滤，列出全部诅咒）
/curse off    恢复按诅咒池过滤
/curse        查看当前状态
```

强制模式只对本次运行有效，在弹窗里手动点一下池就会退出。

| 日志 | 位置 |
| ---- | ---- |
| BepInEx | `<游戏>\BepInEx\LogOutput.log`、`<游戏>\BepInEx\ErrorLog.log` |
| 游戏 | `%LOCALAPPDATA%Low\Cuerzor\MinecraftVSZombies2Test\mvz2_log.log` |

---

## 已知限制

- 仅适配 0.7.0 test-4（Windows x86 IL2CPP）
- 诅咒名字：每个池前 7 条沿用原版那 7 条诅咒的名字（它们复用的正是那 7 个图标），
  第 8 条起用池内编号（`QC-C8` … / `SC-C8` …）
- 选卡界面右侧**没有**独立的「诅咒池」按钮：早期版本做过一个克隆入口，实测点不开
  （克隆体的父节点是 `SetActive(false)` 的包装对象），详见 `CURSES.md` 实现备注

---

## 许可证

[GPL-3.0](../LICENSE)。BepInEx 及其组件遵循各自原许可证。

## 致谢

- **Cuerzor** — 《我的世界僵尸 2》作者，以及 0.7.0 的原生诅咒系统设计
- [BepInEx](https://github.com/BepInEx/BepInEx) / [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) — IL2CPP 注入框架
