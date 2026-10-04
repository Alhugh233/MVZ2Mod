using System;
using HarmonyLib;
using MVZ2.Debugs;
using MVZ2.Level;
using MVZ2.Managers;
using MVZ2Logic;
using MVZ2Logic.Commands;

namespace Replay
{
    /// <summary>
    /// 控制台指令 <c>/replay</c>。
    ///
    ///   /replay rec [名字]    从当前 tick 开始录制（默认自动生成名字）
    ///   /replay stop          结束录制并写出 .mrep
    ///   /replay play &lt;名字&gt;   播放一个回放（进入关卡、恢复关键帧、按 tick 重演输入）
    ///   /replay playlast      播放最近写出的那个回放
    ///   /replay stopplay      中止播放（保留当前关卡状态）
    ///   /replay list          列出回放目录里的文件
    ///   /replay status        查看录制 / 播放状态
    ///   /replay dir           查看输出目录
    ///
    /// 为什么直接挂 Harmony 而不是走官方 <c>CommandDefinition</c>：
    ///   官方命令是数据驱动的（<c>commands.xml</c> → <c>ModResource</c> → <c>ModLoader.LoadCommandProperties</c>），
    ///   而控制台解析裸命令名用的默认命名空间是 <c>mvz2</c>，mod 自己的定义必须写成
    ///   <c>/replay:xxx</c> 才找得到。我们只需要一条排障指令，所以照本仓库
    ///   <c>ModularCurse/CurseCommand.cs</c> 的做法在 <c>DebugManager.ExecuteCommand</c> 上前缀拦截。
    /// </summary>
    internal static class ReplayCommand
    {
        private const string CommandName = "replay";

        private static bool applied;
        private static Harmony harmony;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            harmony = new Harmony(Plugin.GUID + ".command");
            try
            {
                var target = AccessTools.Method(typeof(DebugManager), "ExecuteCommand",
                    new[] { typeof(string), typeof(int) });
                if (target == null)
                {
                    Plugin.Log?.LogError("[Replay] 找不到 DebugManager.ExecuteCommand");
                    return;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ReplayCommand), nameof(ExecuteCommandPrefix))));
                Plugin.Log?.LogInfo("[Replay] 已挂 /" + CommandName);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 挂 /" + CommandName + " 失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>返回 false = 这是我们的指令，不再交给原版解析。</summary>
        private static bool ExecuteCommandPrefix(string input)
        {
            try
            {
                if (!IsReplayCommand(input))
                    return true;
                Plugin.Log?.LogInfo("[Replay] 收到：" + input.Trim());
                PrintLine("> " + input.Trim());
                Handle(input);
                return false;
            }
            catch (Exception e)
            {
                PrintLine("[replay] 执行出错: " + e.GetType().Name + ": " + e.Message);
                Plugin.Log?.LogError("[Replay] " + e);
                return false;
            }
        }

        private static bool IsReplayCommand(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;
            var text = input.Trim();
            if (!text.StartsWith("/", StringComparison.Ordinal))
                return false;
            var parts = CommandUtility.SplitCommand(text);
            if (parts == null || parts.Length == 0)
                return false;
            return string.Equals(parts[0], CommandName, StringComparison.OrdinalIgnoreCase);
        }

        private static void Handle(string input)
        {
            var parts = CommandUtility.SplitCommand(input.Trim());
            var sub = parts != null && parts.Length > 1 && parts[1] != null
                ? parts[1].ToLowerInvariant()
                : string.Empty;

            switch (sub)
            {
                case "rec":
                case "record":
                case "start":
                    {
                        var ctrl = GetController();
                        if (ctrl == null)
                        {
                            PrintLine("[replay] 当前不在关卡中，无法开始录制。");
                            break;
                        }
                        var name = parts.Length > 2 ? parts[2] : null;
                        if (ReplayRecorder.BeginManual(name, ctrl))
                            PrintLine("[replay] 已开始录制：" + ReplayRecorder.CurrentName);
                        else
                            PrintLine("[replay] 开始录制失败。");
                        break;
                    }
                case "stop":
                    {
                        if (ReplayPlayer.IsActive)
                        {
                            PrintLine(ReplayPlayer.Stop("user"));
                            break;
                        }
                        if (ReplayRecorder.StopManual())
                        {
                            PrintLine("[replay] 已结束录制。");
                            var path = ReplayRecorder.LastWrittenPath;
                            PrintLine(path != null ? "[replay] 文件：" + path : "[replay] 未产生文件（没有任何动作）。");
                        }
                        else
                        {
                            PrintLine("[replay] 当前没有在录制。");
                        }
                        break;
                    }
                case "play":
                case "p":
                    {
                        var name = parts.Length > 2 ? parts[2] : null;
                        if (string.IsNullOrEmpty(name))
                        {
                            PrintLine("[replay] 用法：/replay play <名字>（用 /replay list 看有哪些）");
                            break;
                        }
                        PrintLine(ReplayPlayer.Start(name));
                        break;
                    }
                case "playlast":
                    {
                        var last = ReplayRecorder.LastWrittenPath;
                        if (string.IsNullOrEmpty(last))
                        {
                            PrintLine("[replay] 这次启动还没有写过回放文件。");
                            break;
                        }
                        PrintLine(ReplayPlayer.Start(last));
                        break;
                    }
                case "stop-play":
                case "stopplay":
                case "endplay":
                    PrintLine(ReplayPlayer.Stop("user"));
                    break;
                case "list":
                    {
                        var names = ReplayFile.ListNames();
                        PrintLine("[replay] " + ReplayConfig.ResolveOutputDir());
                        if (names.Count == 0)
                            PrintLine("  （没有回放文件）");
                        for (int i = 0; i < names.Count; i++)
                            PrintLine("  " + names[i]);
                        break;
                    }
                case "status":
                case "info":
                    {
                        PrintLine("[replay] 录制：" + ReplayRecorder.Describe());
                        PrintLine("[replay] 播放：" + ReplayPlayer.Describe());
                        var path = ReplayRecorder.LastWrittenPath;
                        if (path != null)
                            PrintLine("[replay] 最近文件：" + path);
                        break;
                    }
                case "dir":
                    PrintLine("[replay] 输出目录：" + ReplayConfig.ResolveOutputDir());
                    break;
                default:
                    PrintUsage();
                    break;
            }
        }

        private static LevelController GetController()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return null;
                var lm = main.LevelManager;
                if (lm == null)
                    return null;
                return lm.GetLevelController();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void PrintUsage()
        {
            PrintLine("[replay] 用法：");
            PrintLine("  /replay rec [名字]    从当前 tick 开始录制");
            PrintLine("  /replay stop          结束录制并写出 .mrep");
            PrintLine("  /replay play <名字>   播放回放（播放中玩家只能暂停/加速/开关血条）");
            PrintLine("  /replay playlast      播放最近写出的回放");
            PrintLine("  /replay stopplay      中止播放");
            PrintLine("  /replay list          列出目录里的回放");
            PrintLine("  /replay status        查看录制与播放状态");
            PrintLine("  /replay dir           查看输出目录");
        }

        private static void PrintLine(string text)
        {
            try
            {
                Global.Debugs?.Print(text + "\n");
            }
            catch (Exception)
            {
            }
        }
    }
}
