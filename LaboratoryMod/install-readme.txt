MVZ2 Laboratory Content（实验室小游戏） v{VERSION}
=================================================

适用版本：我的世界僵尸 2（MVZ2） 0.7.0 test-4（x86 / IL2CPP）

【前置要求】
必须先安装 **DSHCore** 的安装包
（它提供 BepInEx 运行环境）。本包只含插件与关卡资源。

【包含内容】
  real_prologue   真正的序章    —— 纯原版行为组合的迷你关卡
  zero_k_space    零度空间      —— 瞬间充能 / 火药爆炸 / 费用倍率
  unknow_palace   真理伪证殿堂  —— 逐波替换卡槽 + 完全接管出怪

【安装】
1. 先装好 DSHCore 的安装包。
2. 关闭游戏。
3. 把本压缩包内所有内容解压到游戏根目录（覆盖同名文件）。
4. 启动游戏 → 主菜单「小游戏」（街机）列表末尾即可看到 3 个新关卡。

【卸载】
删除 BepInEx/plugins/LaboratoryMod.dll 与
MinecraftVSZombies2_Data/StreamingAssets/Mods/mvz2_lab/ 即可。

【调试指令】（调试控制台）
  /gotolevel mvz2_lab:real_prologue  mvz2:day
  /gotolevel mvz2_lab:zero_k_space   mvz2:day
  /gotolevel mvz2_lab:unknow_palace  mvz2:day

【故障排查】
- 街机列表里没有新关卡：先看 BepInEx/LogOutput.log 里是否出现
  "[LoadStagesPatch] added stage mvz2_lab:..."。
- 原生崩溃的托管栈在 BepInEx/ErrorLog.log（LogOutput.log 里没有）。
