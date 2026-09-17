using System;
using HarmonyLib;
using MVZ2.Level;
using MVZ2.Level.Curses;
using PVZEngine;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒池功能（选卡界面）。
    ///
    /// 为什么可以纯代码实现（不改任何 prefab / 资源）：
    ///   <c>LevelBlueprintChooseController.choosingCurses</c>（get/set）是诅咒面板的**唯一数据源**
    ///   （列表内容、<c>GetCurseIDByIndex</c>、<c>ApplyCurses</c> 都用它），
    ///   所以在 <c>ReloadChoosingCurses</c> 之后**就地过滤它**，下游全部自动一致；
    ///   <c>enabledCurses</c>（get/set）是已勾选集合，按过滤后的列表重建即可丢弃池外的勾选。
    ///
    /// 选择池的 UI 在 <see cref="CursePoolRow"/>：直接在**诅咒弹窗内部**挂一排池条目。
    /// （早期版本用「克隆原版制品选择框」做成独立弹窗，但制品弹窗的父节点是
    /// <c>SetActive(false)</c> 的包装对象，克隆体永远不可见 —— 已废弃。）
    /// </summary>
    internal static class CursePoolFeature
    {
        private static bool applied;
        private static Harmony harmony;

        /// <summary>诅咒弹窗当前是否打开（打开时换池要立刻刷新列表）。</summary>
        internal static bool DialogOpen;

        /// <summary>最近一次进入选卡界面的控制器（<c>/curse</c> 指令需要它才能立即生效）。</summary>
        internal static LevelBlueprintChooseController Current;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            harmony = new Harmony(Plugin.GUID + ".cursepool");

            Patch("LevelBlueprintChooseController.ReloadChoosingCurses",
                "ReloadChoosingCurses",
                postfix: nameof(ReloadChoosingCursesPostfix));
            Patch("LevelBlueprintChooseController.OpenChooseCurseDialog",
                "OpenChooseCurseDialog",
                prefix: nameof(OpenDialogPrefix),
                postfix: nameof(OpenDialogPostfix));
            Patch("LevelBlueprintChooseController.CloseCursePanel",
                "CloseCursePanel",
                prefix: nameof(ClosePanelPrefix));
            Patch("LevelBlueprintChooseController.ApplyCurses",
                "ApplyCurses",
                prefix: nameof(ApplyCursesPrefix));

            Plugin.Log?.LogInfo("[CursePool] all patches processed");
        }

        private static void Patch(string label, string methodName,
            string prefix = null, string postfix = null)
        {
            var target = AccessTools.Method(typeof(LevelBlueprintChooseController), methodName);
            if (target == null)
            {
                Plugin.Log?.LogError("[CursePool] patch target not found: " + label);
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(CursePoolFeature), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(CursePoolFeature), postfix));
                Plugin.Log?.LogInfo("[CursePool] patched " + label);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] patch failed: " + label + " -> "
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        #region 补丁
        /// <summary>列表重建后：装载本关的池，过滤列表，丢掉池外的勾选，并准备好弹窗里的池条目。</summary>
        private static void ReloadChoosingCursesPostfix(LevelBlueprintChooseController __instance)
        {
            try
            {
                Current = __instance;
                CursePoolState.LoadFor(StageIdOf(__instance));
                Enforce(__instance);
                // 弹窗此时通常还没打开（甚至还没被激活），提前把条目行建好，
                // 这样玩家点开诅咒面板时它已经在里面了。
                CursePoolRow.Ensure(__instance);
                CursePoolRow.Refresh();
                CurseDialogProbe.DumpOnce(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] ReloadChoosingCurses 处理失败: " + e);
            }
        }

        private static void OpenDialogPrefix(LevelBlueprintChooseController __instance)
        {
            try
            {
                Enforce(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] OpenChooseCurseDialog 前置失败: " + e);
            }
        }

        /// <summary>弹窗刚打开：把池选择条目准备好并同步选中态。</summary>
        private static void OpenDialogPostfix(LevelBlueprintChooseController __instance)
        {
            DialogOpen = true;
            Current = __instance;
            try
            {
                CursePoolRow.Ensure(__instance);
                CursePoolRow.Refresh();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] 弹窗内池条目初始化失败: " + e);
            }
        }

        private static void ClosePanelPrefix(LevelBlueprintChooseController __instance)
        {
            DialogOpen = false;
            CurseTooltip.Hide();
        }

        /// <summary>真正把诅咒挂到关卡上之前再保证一次（池外的勾选不会生效）。</summary>
        private static void ApplyCursesPrefix(LevelBlueprintChooseController __instance)
        {
            try
            {
                Enforce(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] ApplyCurses 前置失败: " + e);
            }
        }
        #endregion

        #region 过滤与勾选
        /// <summary>
        /// 让「诅咒面板的内容」与「已勾选集合」都只包含当前池的诅咒。幂等，可反复调用。
        /// </summary>
        internal static void Enforce(LevelBlueprintChooseController ctl)
        {
            if (ctl == null)
                return;
            // /curse all：强制显示所有诅咒池，即不过滤（连池外勾选也不丢）。
            if (CursePoolState.ForceAll)
                return;
            var pool = CursePoolState.Current;

            var list = ctl.choosingCurses;
            if (list == null || list.Count <= 0)
                return;

            // 1) 就地过滤列表（倒序删除，保持原有顺序）。
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!CursePoolRules.Matches(pool, list[i]))
                    list.RemoveAt(i);
            }

            // 2) 按过滤后的列表重建已勾选集合 —— 池外已勾选的自动取消。
            //    只用 Contains/Add 与公开 setter，避免在 IL2CPP 集合上做「边遍历边删」。
            var enabled = ctl.enabledCurses;
            if (enabled == null)
                return;
            var kept = new Il2CppSystem.Collections.Generic.HashSet<NamespaceID>();
            for (var i = 0; i < list.Count; i++)
            {
                var id = list[i];
                if (id != null && enabled.Contains(id))
                    kept.Add(id);
            }
            if (kept.Count != enabled.Count)
            {
                ctl.enabledCurses = kept;
                Plugin.Log?.LogInfo("[CursePool] 已取消池外勾选：" + enabled.Count + " → " + kept.Count);
            }
        }

        /// <summary>
        /// 选定一个池：写记录 → 过滤面板与勾选 → 刷新入口与弹窗内容。
        /// 弹窗里的池条目与按钮都走这一个入口，保证行为一致。
        /// </summary>
        internal static void ApplyPool(LevelBlueprintChooseController ctl, CursePool pool)
        {
            if (ctl == null)
                return;
            try
            {
                if (pool == CursePoolState.Current && !CursePoolState.ForceAll)
                {
                    CursePoolRow.Refresh();
                    return;
                }
                if (CursePoolState.ForceAll)
                {
                    // 手动选池 = 主动退出 /curse all 的调试模式。
                    CursePoolState.SetForceAll(false);
                    Plugin.Log?.LogInfo("[CursePool] 手动选池，已退出 /curse all 强制模式");
                }
                CursePoolState.Select(StageIdOf(ctl), pool);
                Plugin.Log?.LogInfo("[CursePool] 选择 " + CursePoolRules.Name(pool));

                // 重建列表（我们的后缀会按新池过滤），并刷新弹窗与入口。
                ctl.ReloadChoosingCurses();
                ctl.RefreshCursePanelEntrance();
                ctl.UpdateCursePanelEntranceNumberText();
                if (DialogOpen)
                    ctl.OpenChooseCurseDialog();
                CursePoolRow.Refresh();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] 应用池失败: " + e);
            }
        }

        /// <summary>
        /// <c>/curse all</c> / <c>/curse off</c>：强制显示所有诅咒池（不过滤）或恢复过滤。
        /// 立即刷新选卡界面；如果当前不在选卡界面，则只记下状态、下次进入时生效。
        /// 返回是否真的作用到了界面上。
        /// </summary>
        internal static bool ApplyForceAll(bool value)
        {
            CursePoolState.SetForceAll(value);
            Plugin.Log?.LogInfo("[CursePool] " + (value
                ? "强制显示所有诅咒池（不过滤）"
                : "恢复按诅咒池过滤（" + CursePoolRules.Name(CursePoolState.Current) + "）"));
            var ctl = Current;
            if (ctl == null)
                return false;
            try
            {
                // 列表要重新从解锁列表里填满，否则之前被过滤掉的诅咒回不来。
                ctl.ReloadChoosingCurses();
                ctl.RefreshCursePanelEntrance();
                ctl.UpdateCursePanelEntranceNumberText();
                if (DialogOpen)
                    ctl.OpenChooseCurseDialog();
                CursePoolRow.Refresh();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] 刷新强制模式失败: " + e);
                return false;
            }
        }

        /// <summary>当前状态的一行描述（给 <c>/curse</c> 用）。</summary>
        internal static string Describe()
        {
            if (CursePoolState.ForceAll)
                return "强制显示所有诅咒池（/curse off 可恢复）";
            return "按池过滤：" + CursePoolRules.Name(CursePoolState.Current);
        }

        internal static NamespaceID StageIdOf(LevelBlueprintChooseController ctl)
        {
            try
            {
                var level = ctl.Level;
                return level == null ? null : level.StageID;
            }
            catch (Exception)
            {
                return null;
            }        }
        #endregion
    }
}
