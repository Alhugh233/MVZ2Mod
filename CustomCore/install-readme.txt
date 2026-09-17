MVZ2 Custom Core（前置核心模组） v{VERSION}
==========================================

适用版本：我的世界僵尸 2（MVZ2） 0.7.0 test-4（x86 / IL2CPP）

【这是什么】
所有 MVZ2 系列 mod 的公共前置。它本身不添加任何游戏内容，只提供：
  1. BepInEx 运行环境（本包已包含，装一次即可，其它 mod 包不再重复携带）
  2. ClassInjector / IL2CPP 注入修复、XML metas 注入、存档兼容等公共基建

【安装】
1. 关闭游戏。
2. 把本压缩包内**所有内容**解压到游戏根目录
   （即与 MinecraftVSZombies2.exe 同一层，直接覆盖同名文件）。
3. 启动游戏。BepInEx 控制台会随游戏弹出。

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
          └─ CustomCore.dll

【验证】
BepInEx 控制台应出现：
  [MVZ2 Custom Core] MVZ2 Custom Core v{VERSION} loaded (infrastructure ready)

【卸载】
删除 winhttp.dll / doorstop_config.ini / .doorstop_version / dotnet/ / BepInEx/ 即可。
（注意：依赖本核心的其它 mod 也会随之失效。）

【故障排查】
- 游戏无法启动：确认 winhttp.dll 与 doorstop_config.ini 都已解压到位。
- 报 interop 相关错误：说明游戏版本与本包不符。删除 BepInEx/interop，
  并把 BepInEx/config/BepInEx.cfg 里的 [IL2CPP] UpdateInteropAssemblies
  改成 true，启动一次让它按当前游戏重新生成（需联网）。
- 原生崩溃的托管栈在 BepInEx/ErrorLog.log（LogOutput.log 里没有）。
