MVZ2 Replay（关卡回放录制）
============================================

适用版本：Minecraft vs Zombies 2（MVZ2） 0.7.0 test-9（x86 / IL2CPP）

【前置要求】
无需单独安装前置 —— 本包含以下全部：
  · BepInEx 6 运行环境
  · DSHCore 前置库
  · 本 mod 的插件
解压到游戏根目录即可。

【录制】
进入关卡后会自动开始录制（可在配置里关掉）。关卡结束 / 退出关卡 / 退出游戏时，
回放文件写到**原版存档目录**里（按用户区分），扩展名为 .mrep：
  %USERPROFILE%\AppData\LocalLow\Cuerzor\<游戏名>\userdata\user<N>\replays\
（想换位置就改 BepInEx\config\qoznos.mvz2.replay.cfg 的 OutputDir；见 README）

【播放】
主菜单 → 更多选项 → 插件 → 回放（入口就在「资源包」下面那一行）
  · 面板第一行是回放数量，右边并排三个排序按钮：按时间 / 按关卡 / 按时长
  · 列表每一行是「关卡名 · 存档时间 · 时长」，右边是 [播放] [删除]
  · 「删除」会再弹一个面板二次确认
  · 最多保留 20 个（改配置的 MaxReplays；超出后每写出一份就清一次最旧的）
  · 不想看这个入口就设 GuiEnabled = false，用下面的指令代替

游戏内控制台指令：
  /replay rec [名字]   开始录制
  /replay stop         结束录制并写出文件
  /replay play <名字>  播放回放
  /replay playlast     播放最近写出的那一条
  /replay stopplay     中止播放
  /replay list         列出所有回放
  /replay status       查看状态
  /replay dir          查看输出目录

【回放一致性】
每 30 个逻辑 tick 会记一条「状态指纹」（能量 / 波次 / 旗数 + 实体清单哈希 +
7 条 RNG 流各自的哈希）。播放时会逐条重算并与录制时比对，不一致会写进控制台小结，
能直接指出「从哪个 tick 起、是哪一类实体 / 哪一条 RNG 流先跑偏」。

【注意】
回放依赖「起始关键帧 + 之后逐 tick 重演输入」。若游戏本体更新导致关卡逻辑变化，
旧回放可能复现不出同样的过程（指纹比对会报出来）。
