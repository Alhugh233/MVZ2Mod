MVZ2 Laboratory Content（实验室小游戏） v{VERSION}
=================================================

适用版本：Minecraft vs Zombies 2（MVZ2） 0.7.0 test-10（x86 / IL2CPP）

【前置要求】
无需单独安装前置 —— 本包含以下全部：
  · BepInEx 6 运行环境
  · DSHCore 前置库
  · 本 mod 的插件与关卡资源
解压到游戏根目录即可。

【包含内容】
三个难度极高的原创小游戏。

【安装】
1. 确保游戏关闭。
2. 把本压缩包内所有内容解压到游戏根目录（覆盖同名文件）。
   如果游戏里已经装过别的 mod / 前置，直接覆盖即可（同一份运行环境）。
3. 启动游戏 → 主菜单「小游戏」列表末尾即可看到 3 个新关卡。

【看日志 / 调试】
本 mod 的日志可以在游戏里当场看：
  进游戏后按 / 打开调试控制台，输入：/dshlog
会启动一个本地网页（自动开浏览器）实时显示所有 mod 的日志，
可从那里过滤、搜索、点开堆栈。关掉用 /dshlog off。

（需要游戏的存档用户名是 debug 才能打开调试控制台。）
本包已把 BepInEx 的控制台窗口关掉（[Logging.Console] Enabled = false），
想要那个黑窗口回来：把 BepInEx/config/BepInEx.cfg 里那行改回 true。

【卸载】
删除 BepInEx/plugins/LaboratoryMod.dll 与
MinecraftVSZombies2_Data/StreamingAssets/Mods/mvz2_lab/ 即可。

【故障排查】
- 街机列表里没有新关卡：先看 BepInEx/LogOutput.log 里是否出现
  "[LoadStagesPatch] added stage mvz2_lab:..."。
- 原生崩溃的托管栈在 BepInEx/ErrorLog.log（LogOutput.log 里没有）。
