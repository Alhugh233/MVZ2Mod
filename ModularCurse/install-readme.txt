MVZ2 Modular Curse（模块化诅咒） v{VERSION}
============================================

适用版本：Minecraft vs Zombies 2（MVZ2） 0.7.0 test-9（x86 / IL2CPP）

【前置要求】
无需单独安装前置 —— 本包含以下全部：
  · BepInEx 6 运行环境
  · DSHCore 前置库
  · 本 mod 的插件与诅咒资源
解压到游戏根目录即可。

【包含内容】
把自定义诅咒接入**原版诅咒系统**，共 44 条诅咒，分三个诅咒池：
  QC 池  12 条（QoZnoS的诅咒）
  SC 池  16 条（奇硅箱的诅咒）
  ZY 池  16 条（咒鵺兼程 / 3q4a的诅咒）
另有「原版池」= 原版那 7 条诅咒（默认）。

【安装】
1. 关闭游戏。
2. 把本压缩包内所有内容解压到游戏根目录（覆盖同名文件）。
   如果游戏里已经装过别的 mod / 前置，直接覆盖即可（同一份运行环境）。
3. 启动游戏 → 进关卡前的选卡界面点「诅咒」入口即可看到诅咒列表。

【使用】
诅咒弹窗（标题「选择诅咒」）的滚动区最上方有一行**四个池条目**，
下面一条横线，横线下面才是真正的诅咒列表。

  - 点池条目即可换池，一次只能选一个池（没有「全部」）；
  - 换池后列表只显示该池的诅咒，其它池已勾选的会自动取消；
  - 选择按关卡分别记住，存在 userdata/userN/modular_curse/curse_pool.txt。

【调试指令】（调试控制台，按 / 打开）
  /curse all    强制显示所有诅咒池（不按池过滤）
  /curse off    恢复按诅咒池过滤
  /curse        查看当前状态

  /dshlog       开启本地网页日志查看器（实时看所有 mod 的日志）
  /dshlog off   关闭它
（需要游戏的存档用户名是 debug 才能打开调试控制台。）

【卸载】
删除 BepInEx/plugins/ModularCurse.dll 与
MinecraftVSZombies2_Data/StreamingAssets/Mods/modular_curse/ 即可。

【故障排查】
- 诅咒列表里看不到模组诅咒：先看 BepInEx/LogOutput.log 里是否出现
  本 mod 的注册与诅咒定义创建日志。
- 原生崩溃的托管栈在 BepInEx/ErrorLog.log（LogOutput.log 里没有）。
