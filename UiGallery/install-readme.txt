MVZ2 UI 组件画廊（Ui Gallery）v{VERSION}
========================================

开发者工具：把 DSHCore 提供的每个原版 UI 素材以真实观感展示出来，
同时充当「用原版零件做 UI」的最小可编译范例。

【依赖】
必须同时安装 DSHCore（本包内含 DSHCore.dll，两者一起放进 plugins/ 即可）。

  游戏目录\BepInEx\plugins\
      ├─ DSHCore.dll
      └─ UiGallery.dll

【用法】
进游戏后按 / 打开调试控制台（需要存档用户名是 debug），输入：

  /dshui                打开画廊（已打开则显示状态）
  /dshui close          关闭
  /dshui page <名称>    跳到某一页（不带参数列出全部页）
  /dshui report         打印素材解析报告
  /dshui rebuild        强制重新解析 UI 素材
  /dshui help           帮助

/dshui 后面按 Tab 可以补全子指令。

面板：标题显示当前页与页码，中间是内容，底部是「上一页 / 下一页 / 关闭」。

【验证安装】
日志文件 BepInEx/LogOutput.log 应出现：
  [MVZ2 UI Gallery] [Command] 游戏内指令已就绪：/dshui（含 Tab 补全）
  [MVZ2 UI Gallery] [Plugin] MVZ2 UI Gallery v{VERSION} 已就绪。游戏内输入 /dshui 打开组件画廊。

【页面】
  文本       Label / Desc
  按钮       Button / ButtonRow（点一下有反馈）
  复选框     Toggle
  滑条       Slider
  下拉框     Dropdown（**只有外观**：TMP 下拉弹窗在本游戏环境里画不出来，
             节点都在、矩形也对，但屏幕上什么都看不到 —— 这是渲染层限制）
  输入框     InputField（可以直接打字，中文也行）
  提示       原版悬停提示（与设置界面 / 图鉴同一条链）
  报告       每个素材当前是否可用

【排障：自测模式】
改 BepInEx/config/qoznos.mvz2.uigallery.cfg：

  [General]
  SelfTest = true

进主菜单后会自动打开画廊、逐页装配、每页 dump 布局矩形并截图到
BepInEx\_uig_p<N>.png，全部写进日志（搜 [SelfTest]）。
这是「UI 基础设施到底能不能工作」的唯一权威判据 ——
截图可能落空白帧（异步写盘），所以**以布局 dump 的矩形为准**。

【卸载】
删掉 BepInEx\plugins\UiGallery.dll 以及 BepInEx\config\qoznos.mvz2.uigallery.cfg 即可。
（如果你也装了别的 DSHCore 内容 mod，别删 DSHCore.dll。）
