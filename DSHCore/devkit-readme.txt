MVZ2 Mod 开发环境（不含 DSHCore 源码）
DSHCore v{VERSION}  /  适配 MVZ2 0.7.0（Windows x86）

================================================================
这是什么
================================================================

一个可以立刻开工的 MVZ2 mod 开发工作区。它与主仓库的区别只有一处：

  **不含 DSHCore 的源码**，只提供编译好的 _refs\DSHCore\DSHCore.dll。

mod 工程会自动检测到这个情况，改用二进制引用（见根目录的
Directory.Build.props），因此你不需要 DSHCore 的源码也能编译、
部署和调试自己的 mod。

仓库里自带 LaboratoryMod / ModularCurse 两个完整 mod 作为可运行的
范例，可以照着抄，也可以按需删掉只留一个。

================================================================
先做三件事
================================================================

1) 装 .NET 6 SDK
   https://dotnet.microsoft.com/download/dotnet/6.0

2) 告诉它你的游戏装在哪
   复制 Directory.Build.local.props.example 为
   Directory.Build.local.props，把里面的 <GameDir> 改成你的游戏目录。
   （结尾的反斜杠不能少；这个文件不会被提交到版本库。）

   不想用文件也行，设一个环境变量即可：
       setx MVZ2_GAME_DIR "E:\Game\PVZ\MVZ2\MVZ2 0.7.0\"

3) 用 VS Code 打开**本目录（工作区根）**，不要单独打开某个 mod 子目录。
   单独打开子目录会让语言服务器看不到 DSHCore，于是满屏
   「未能找到类型或命名空间名 DSHCore」——那只是编辑器的问题，
   dotnet build 其实是好的。真要单开一个 mod，用
   MVZ2Mod.code-workspace（多根工作区）而不是直接打开子目录。

然后：

    .\build.ps1                 # 构建全部（游戏目录有效时自动部署）
    .\build.ps1 -Pack -Launch   # 构建 → 打包 → 启动游戏冒烟

VS Code 里按 Ctrl+Shift+P → Tasks: Run Task，有现成的任务。

================================================================
编译引用从哪来
================================================================

按下面的顺序自动找（见根目录 Directory.Build.props）：

  1. -p:RuntimeDir=  或环境变量 MVZ2_RUNTIME_DIR
  2. Directory.Build.local.props 里的 <RuntimeDir>
  3. 本包根目录自带的 BepInEx\（如果这份 DevKit 带了 runtime）
  4. 你 <GameDir> 里的 BepInEx\core 与 BepInEx\interop
     —— 游戏目录本身就是一套合法的 BepInEx 环境，没必要再复制一份

也就是说：只要你有一个装了 BepInEx 的游戏目录，本包完全可以不带
runtime，照样能编译（当然，要运行还是得装游戏）。

两条硬约束：

  * 引用必须是 **BepInEx be.788 x86 IL2CPP** 产生的。
    本游戏的 IL2CPP metadata 是 v31.1，旧版 BepInEx（<= be.697）
    自带的 Cpp2IL 只支持到 v29，生成不出 interop。
  * runtime\BepInEx\interop 是**由具体游戏二进制生成**的派生内容，
    换游戏版本就必须重新生成，不能跨版本混用。

若 <GameDir> 里还没有 BepInEx：下载 be.788 x86 IL2CPP 解压到游戏根
目录，确认 BepInEx\config\BepInEx.cfg 里 [IL2CPP] 段的
UpdateInteropAssemblies = true，启动一次游戏（约 20 秒，需联网下载
Unity 基础库）让它生成 BepInEx\interop。

================================================================
关于 DSHCore
================================================================

DSHCore 是本工作区的**前置库**（GUID qoznos.mvz2.core），运行期它必须
待在 BepInEx\plugins\DSHCore.dll。它负责修好 IL2CPP 注入环境里的一堆
硬坑，并给 mod 提供公共 API：

  Il2CppPin / InjectedMod / ModBootstrap / ModInfos / CoreRegistry /
  VTableRebind / MetaInjection / ClassInjectorFix ...

**动手写代码之前，请先读 DSHCore\AI_GUIDE.md。**
那份文档记录了「症状 → 根因 → 对策」的实测陷阱清单，以及 0.7 官方
集成链的时序图。绕过它的代价通常是反复崩溃试错。

================================================================
想改 DSHCore 本身？
================================================================

本包不含源码。需要的话去主仓库取 DSHCore\ 目录，放回本工作区根的
DSHCore\ 下（覆盖现有的文档目录即可）—— 工程会**自动**切换回源码
引用，不需要改任何配置。反向而行（删掉源码、只留 _refs）同理。

也可以强制指定：dotnet build -p:DshCoreFromSource=false

================================================================
目录速览
================================================================

    Directory.Build.props            编译环境解析（引用的唯一真源）
    Directory.Build.local.props.example   本机配置模板（复制成 .props 后生效）
    MVZ2Mod.code-workspace           多根工作区（想单开一个 mod 时用）
    build.ps1                        总构建脚本
    _refs\DSHCore\                   编译引用：DSHCore.dll（**无源码**）
    BepInEx\  dotnet\  winhttp.dll   编译引用：BepInEx 环境（可无）
    tools\                           构建辅助脚本 / 日志查看
    .vscode\                         构建、打包、看日志任务
    DSHCore\                         仅文档（AI_GUIDE.md 等）
    LaboratoryMod\  ModularCurse\    两个可运行的范例 mod
    dist\                            打包输出

================================================================
许可与声明
================================================================

本包为社区制作的**非官方**内容，与《Minecraft vs Zombies 2》原作者无关。
