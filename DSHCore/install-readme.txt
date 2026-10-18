DSHCore（大肥鱼核心 / 前置模组） v{VERSION}
==========================================

适用版本：Minecraft vs Zombies 2（MVZ2） 0.7.0（Windows x86 / IL2CPP）

【这是什么】
所有 MVZ2 mod 的公共前置与开发工具库。它本身不添加任何游戏内容，只提供：
  1. BepInEx 运行环境（本包已包含）
  2. ClassInjector / IL2CPP 注入修复、XML metas 注入、存档兼容等公共基建
  3. 本地网页日志查看器（游戏内 /dshlog 开启）
   注：装了其它 mod 的玩家不必再单独装本包 —— 那些包已自带运行环境与 DSHCore。

【安装】
1. 关闭游戏。
2. 把本压缩包内**所有内容**解压到游戏根目录。
   （即与 MinecraftVSZombies2.exe 同一层，直接覆盖同名文件）。
3. 启动游戏。本包已把 BepInEx 的控制台窗口关闭（见下），不会再弹黑窗。

安装后目录形如：
  游戏根目录/
  ├─ MinecraftVSZombies2.exe
  ├─ winhttp.dll
  ├─ doorstop_config.ini
  ├─ .doorstop_version
  ├─ dotnet/
  └─ BepInEx/
      ├─ core/
      ├─ interop/
      └─ plugins/
          └─ DSHCore.dll

【验证】
日志文件 BepInEx/LogOutput.log 应出现：
  [DSHCore] ClassInjector generic vtable patch applied
  [DSHCore] MetaInjection patches applied
  [DSHCore] 日志查看器待命（捕获已开启，共 5000 条环形缓冲）。游戏内输入 /dshlog 开启网页。
  [DSHCore] 游戏内指令已就绪：/dshlog（含 Tab 补全）
  [DSHCore] DSHCore v{VERSION} loaded (infrastructure ready)

【看日志：游戏内指令 /dshlog】
本核心自带一个本地网页日志查看器，**默认不开**（不占端口、不弹浏览器），
需要时在游戏里当场开：

  1. 进入游戏后按 / 打开调试控制台
  2. 输入：/dshlog
  3. 会自动用浏览器打开一个本地网页，实时显示**所有 mod** 的日志

  指令名不用背：输入 /ds 然后按 Tab，就会补成 /dshlog；
  指令名写完之后接着输入 /dshlog o，Tab 会补出 off / open 等子指令。

  可用的子指令：
  /dshlog            开启（已开启则显示状态）
  /dshlog off        关闭并释放端口（内存历史保留）
  /dshlog open       用默认浏览器打开
  /dshlog url        只打印带令牌的地址
  /dshlog status     显示状态
  /dshlog console    显示 / 隐藏 BepInEx 控制台窗口
  /dshlog clear      清空内存里的历史
  /dshlog help       帮助

> 日志是从**游戏启动**就开始记的，所以你一开网页就能看到之前的完整历史，
> 而不是「开了之后才有的那几行」。
> 调试控制台需要存档用户名是 debug（游戏自身的限制，与本包无关）。

网页里可以按等级/来源/关键词过滤，点某一行看完整内容与报错堆栈，
遇到问题时可以「复制可见」把日志贴给作者。

界面是三栏：左边筛选、中间日志、右边详情。两条分隔条都可以拖动来调宽度，
双击分隔条或点左下的「重置分栏宽度」恢复默认；调整后的宽度会记住。

想换端口、改成开机自动开启、或允许局域网访问，改
BepInEx/config/qoznos.mvz2.core.cfg 里的 [LogViewer] 段。

【那个黑色日志窗口】
本包把 BepInEx 的控制台窗口关掉了：BepInEx/config/BepInEx.cfg 里
[Logging.Console] 段的 Enabled 已设为 false。日志不受影响，
仍会写 BepInEx/LogOutput.log，也会进上面的网页查看器。

想要它回来：把那一行改回 true；或在游戏内用 /dshlog console 临时显示。
（如果游戏是从命令行/终端启动的，窗口是那个终端的，本核心不会去动它。）

【卸载】
删除 winhttp.dll / doorstop_config.ini / .doorstop_version / dotnet/ / BepInEx/ 即可。
（注意：依赖本核心的其它 mod 也会随之失效。）

【故障排查】
- 游戏无法启动：确认 winhttp.dll 与 doorstop_config.ini 都已解压到位。
- 报 interop 相关错误、或游戏随机崩溃（无提示退出）：
  说明游戏版本与本包不符。删除 BepInEx/interop，并把
  BepInEx/config/BepInEx.cfg 里 [IL2CPP] 的 UpdateInteropAssemblies 改成 true，
  启动一次游戏让它按当前游戏重新生成，再重新安装本包。
- 崩溃细节请看 BepInEx/ErrorLog.log（LogOutput.log 里没有原生崩溃的托管栈）。

【给 mod 开发者】
本包同时是编译前置与开发工具库。源码与完整开发参考见项目仓库：
  DSHCore/AI_GUIDE.md    面向 AI 与后来者的开发参考（API 速查 / 骨架 / 16 条陷阱清单）
  DSHCore/README.md      公共 API、自动修复项、日志查看器实现说明

日志查看器的 HTTP 接口（都需 ?token=，`GET /` 除外）可以直接用命令行取：
  GET /api/status               会话 / 进程 / 缓冲 / 队列 / 客户端计数
  GET /api/history?limit=N      最近 N 条历史
  GET /api/events               SSE 实时流
  GET /api/console?action=toggle 显示或隐藏控制台窗口
