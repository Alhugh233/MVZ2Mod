MVZ2 Mod Runtime v{VERSION}
===========================

适用版本：Minecraft vs Zombies 2（MVZ2） 0.7.0（Windows x86 / IL2CPP）
打包时间：{BUILD_DATE}

【这是什么】
一份「装好就能用」的整合运行环境，包含两部分：

  1. BepInEx 注入环境（游戏侧）
     让游戏能加载 mod 的那些文件：winhttp.dll / doorstop_config.ini /
     dotnet\ / BepInEx\。{DOTNET_LINE}

  2. MVZ2ModManager（模组管理器）
     启动前管理模组的桌面程序，可以导入 .mvz2pack、启用/禁用插件、
     备份与还原整套模组配置。
{MODMANAGER_LINE}

{MODS_LINE}

【安装】
1. 关闭游戏。
2. 把本压缩包内**所有内容**解压到游戏根目录
   （即与 MinecraftVSZombies2.exe 同一层，有同名文件就直接覆盖）。
3. 启动一次游戏，确认能进主菜单。
4. 之后每次想改模组，双击游戏根目录下的 MVZ2ModManager.exe 即可。

装好后的目录长这样：

  游戏根目录/
  ├─ MinecraftVSZombies2.exe
  ├─ MVZ2ModManager.exe           ← 模组管理器（双击启动）
  ├─ 安装说明.txt
  ├─ winhttp.dll                  ← 注入入口
  ├─ doorstop_config.ini
  ├─ .doorstop_version
  ├─ dotnet/                      ← 游戏侧托管运行时
  └─ BepInEx/
      ├─ core/
      ├─ interop/                 ← 已按 0.7.0 test-1 预生成，游戏更新后会自动重建
      ├─ config/
      └─ plugins/                 ← mod 插件放这里

【验证】
启动游戏后，用记事本打开 BepInEx\LogOutput.log，应能看到 BepInEx
的启动横幅（含 "BepInEx ... IL2CPP" 字样）以及各插件自己输出的行。

【常见问题】

· 游戏启动后什么都没变、日志也没生成
  → winhttp.dll 没解压到正确位置。它必须和 MinecraftVSZombies2.exe 同级。
  → 或者被杀毒软件拦截了（winhttp.dll 是注入用的，常被误判），
    请把游戏目录加入白名单后重新解压。

· 游戏更新后第一次启动特别慢（可能几分钟）
  → 正常。本包开着 interop 自动重建：游戏版本一变，BepInEx 就会用新版
    本重新生成 BepInEx\interop\（100 多个程序集），之后再启动就快了。
    请等它跑完，中途不要强杀进程 —— 半套 interop 会让下次启动直接崩。
    Unity 基础库已随包放在 BepInEx\unity-libs\，重建时不需要联网。

· 游戏闪退 / 黑屏后秒退
  → 先看 BepInEx\LogOutput.log 末尾。若是 interop 生成到一半失败：
    把整个 BepInEx\interop\ 目录删掉再启动一次，让它从头重建。
  → 若提示 winhttp.dll 无法加载，多半是杀软拦截或与游戏位数（x86）不符。

· ModManager 双击没反应或提示 "需要 .NET 才能运行此应用程序"
  → 你拿到的是框架依赖版（文件名带 _lite）。装一下
    .NET 8 Desktop Runtime (x64)：
    https://dotnet.microsoft.com/download/dotnet/8.0/runtime
    或者改用带 _full 的包（已内置运行时，不用装任何东西）。

· 我不想要 ModManager，只想让游戏能加载 mod
  → 删掉根目录下的 MVZ2ModManager.exe 即可，不影响游戏侧注入。

· 想彻底卸载
  → 删掉 winhttp.dll、doorstop_config.ini、.doorstop_version、dotnet\、
    BepInEx\、MVZ2ModManager.exe 这几项即可恢复原状。
    （注意：BepInEx\ 里可能还有你自己放进去的 mod 配置，删前确认一下。）

【注意】
· 本包不含游戏本体，也不含任何游戏反编译产物以外的派生内容。
· interop\ 是从游戏二进制生成的，属游戏派生内容，请勿单独分发。
· 本包为社区制作的非官方内容，与游戏原作者无关。
