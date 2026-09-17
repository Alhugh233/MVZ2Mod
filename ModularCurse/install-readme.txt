MVZ2 Modular Curse（模块化诅咒） v{VERSION}
============================================

适用版本：我的世界僵尸 2（MVZ2） 0.7.0 test-4（x86 / IL2CPP）

【前置要求】
必须先安装 **DSHCore** 的安装包
（它提供 BepInEx 运行环境）。本包只含插件与诅咒资源。

【包含内容】
把自定义诅咒接入**原版诅咒系统**，共 25 条诅咒，分两个诅咒池：
  QC 池  12 条（QoZnoS的诅咒）
  SC 池  13 条（奇硅箱的诅咒）
另有「原版池」= 原版那 7 条诅咒（默认）。

【安装】
1. 先装好 DSHCore 的安装包。
2. 关闭游戏。
3. 把本压缩包内所有内容解压到游戏根目录（覆盖同名文件）。
4. 启动游戏 → 进关卡前的选卡界面点「诅咒」入口即可看到诅咒列表。

【使用】
诅咒弹窗（标题「选择诅咒」）的滚动区最上方有一行**三个池条目**，
下面一条横线，横线下面才是真正的诅咒列表。

  - 点池条目即可换池，一次只能选一个池（没有「全部」）；
  - 换池后列表只显示该池的诅咒，其它池已勾选的会自动取消；
  - 选择按关卡分别记住，存在 userdata/userN/modular_curse/curse_pool.txt。

【调试指令】（调试控制台）
  /curse all    强制显示所有诅咒池（不按池过滤）
  /curse off    恢复按诅咒池过滤
  /curse        查看当前状态

【卸载】
删除 BepInEx/plugins/ModularCurse.dll 与
MinecraftVSZombies2_Data/StreamingAssets/Mods/modular_curse/ 即可。

【故障排查】
- 诅咒列表里看不到模组诅咒：先看 BepInEx/LogOutput.log 里是否出现
  本 mod 的注册与诅咒定义创建日志。
- 原生崩溃的托管栈在 BepInEx/ErrorLog.log（LogOutput.log 里没有）。
