DSHCore（大肥鱼核心 / 前置模组） v{VERSION}
==========================================

适用版本：我的世界僵尸 2（MVZ2） 0.7.0 test-4（Windows x86 / IL2CPP）

【这是什么】
所有 MVZ2 mod 的公共前置与开发工具库。它本身不添加任何游戏内容，只提供：
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
          └─ DSHCore.dll

【验证】
BepInEx 控制台（或日志文件 BepInEx/LogOutput.log）应出现：
  [DSHCore (大肥鱼核心)] ClassInjector generic vtable patch applied
  [DSHCore (大肥鱼核心)] MetaInjection patches applied
  [DSHCore (大肥鱼核心)] DSHCore (大肥鱼核心) v{VERSION} loaded (infrastructure ready)

【若你装过 0.1.0 版本】
本模组 0.1.0 时叫 CustomCore。升级时请**手动删除**
  BepInEx/plugins/CustomCore.dll
否则两个插件 GUID 相同，会被判为重复而随机只加载其中一个。

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
  DSHCore/AI_GUIDE.md    面向 AI 与后来者的开发参考（API 速查 / 骨架 / 陷阱清单）
  DSHCore/README.md      公共 API 与自动修复项一览
