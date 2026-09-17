MVZ2 Laboratory Content（实验室小游戏） v{VERSION}
=================================================

适用版本：Minecraft vs Zombies 2（MVZ2） 0.7.0 test-4（x86 / IL2CPP）

【前置要求】
必须先安装 DSHCore （它提供 BepInEx 运行环境）。本包只含插件与关卡资源。

【包含内容】
三个难度极高的原创小游戏。

【安装】
1. 确保游戏关闭。
2. 安装 DSHCore 。
3. 把本压缩包内所有内容解压到游戏根目录（覆盖同名文件）。
4. 启动游戏 → 主菜单「小游戏」列表末尾即可看到 3 个新关卡。

【卸载】
删除 BepInEx/plugins/LaboratoryMod.dll 与
MinecraftVSZombies2_Data/StreamingAssets/Mods/mvz2_lab/ 即可。

【故障排查】
- 街机列表里没有新关卡：先看 BepInEx/LogOutput.log 里是否出现
  "[LoadStagesPatch] added stage mvz2_lab:..."。
- 原生崩溃的托管栈在 BepInEx/ErrorLog.log（LogOutput.log 里没有）。
