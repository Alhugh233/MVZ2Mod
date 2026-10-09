using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MVZ2.Level;
using MVZ2.Level.Entities;
using MVZ2.Level.HeldItems;
using MVZ2.Level.Inputing;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic.Games;
using MVZ2Logic.HeldItems;
using MVZ2Logic.Level;
using PVZEngine.Level;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 补丁安装。
    ///
    /// ⚠️ 全部目标都是 **PVZEngine / MVZ2 的普通类型**（含静态扩展类 LogicLevelExt 一律不碰）。
    /// 本仓库踩过的坑：凡是在 <c>Plugin.Load()</c> 里反射带
    /// <c>[PropertyRegistryRegion]</c> 的 <c>MVZ2Logic.*</c> 静态类，都会提前跑它的
    /// <c>.cctor</c> → NRE → 被运行时永久缓存的 TypeInitializationException，
    /// 之后连原版功能一起废掉。本 mod 的挂钩点避开了这类类型，可以安全地在 Load 里挂。
    /// </summary>
    internal static class ReplayHooks
    {
        private static Harmony harmony;
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            harmony = new Harmony(Plugin.GUID + ".hooks");
            int ok = 0, fail = 0;

            // ---- 漏斗：5 个调用点 ----
            // prefix = 回放期间拦掉玩家的真实输入（return false），postfix = 录制。
            Patch(typeof(LevelController), "UI_OnGridPointerInteractionCallback",
                new[] { typeof(int), typeof(int), typeof(PointerInteractionArgs) },
                nameof(ReplayPatches.GridPrefix), nameof(ReplayPatches.GridPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "UI_OnEntityPointerInteractionCallback",
                new[] { typeof(EntityController), typeof(PointerInteractionArgs) },
                nameof(ReplayPatches.EntityPrefix), nameof(ReplayPatches.EntityPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "UI_OnBlueprintPointerInteractionCallback",
                new[] { typeof(int), typeof(PointerInteractionArgs), typeof(bool) },
                nameof(ReplayPatches.BlueprintPrefix), nameof(ReplayPatches.BlueprintPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "UI_OnRaycastReceiverPointerInteractionCallback",
                new[] { typeof(LawnArea), typeof(PointerInteractionArgs) },
                nameof(ReplayPatches.LawnPrefix), nameof(ReplayPatches.LawnPostfix), ref ok, ref fail);

            // 蓝图热键不经过漏斗（直接构造 target 调 DoHeldItemPointerEvent），单独记。
            Patch(typeof(LevelController), "OnBlueprintKey",
                new[] { typeof(int), typeof(bool), typeof(int) },
                nameof(ReplayPatches.BlueprintKeyPrefix), nameof(ReplayPatches.BlueprintKeyPostfix), ref ok, ref fail);

            // ---- 手持物状态（漏斗之外的支路：工具按钮 / 工具热键）----
            //
            // 不挂 HeldItemComponent.SetHeldItem/ResetHeldItem/CancelHeldItem！
            // 实测（2026-10-04 test-9）：那三个方法本身没问题，但**触碰这个类型**
            // 就会触发它的静态构造函数
            //     public static readonly NamespaceID componentID = new NamespaceID(Global.BuiltinNamespace, "heldItem");
            // 而 Global.BuiltinNamespace 要等游戏初始化之后才可用 ⇒ 在 Plugin.Load 阶段
            // 解析到一个 SEHException，并且 TypeInitializationException 会被运行时
            // **永久缓存**（连带给以后想用这个类型的任何代码下毒）。
            //
            // 好在不需要它：手持物状态变化的全部来源是
            //   ① 漏斗内部（选卡/落地拾取）→ 已由上面的指针事件代表；
            //   ② 镐子/星之碎片/触发器的按钮与热键 → 就是下面这三个入口；
            //   ③ 自动派生（ValidateHeldItem / 行为 OnUpdate 的重置）→ 回放会自动重现。
            Patch(typeof(LevelController), "ClickPickaxe",
                Type.EmptyTypes,
                nameof(ReplayPatches.PickaxeClickPrefix), nameof(ReplayPatches.PickaxeClickPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "ClickStarshard",
                Type.EmptyTypes,
                nameof(ReplayPatches.StarshardClickPrefix), nameof(ReplayPatches.StarshardClickPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "ClickTrigger",
                Type.EmptyTypes,
                nameof(ReplayPatches.TriggerClickPrefix), nameof(ReplayPatches.TriggerClickPostfix), ref ok, ref fail);

            // ---- 时间轴与生命周期 ----
            // 前缀负责「把这一 tick 该发生的输入发出去」，后缀负责录制与指纹核对。
            Patch(typeof(LevelEngine), "Update",
                Type.EmptyTypes,
                nameof(ReplayPatches.EngineUpdatePrefix), nameof(ReplayPatches.EngineUpdatePostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "StartGame",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.StartGamePostfix), ref ok, ref fail);

            // LoadGame 有多个重载的风险，交给 AccessTools 自己找唯一那个。
            Patch(typeof(LevelController), "LoadGame",
                null,
                null, nameof(ReplayPatches.LoadGamePostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "Dispose",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.DisposePostfix), ref ok, ref fail);

            Patch(typeof(MainManager), "OnApplicationQuit",
                Type.EmptyTypes,
                nameof(ReplayPatches.QuitPrefix), null, ref ok, ref fail);

            // 自测的每帧驱动点。MainSceneController.Update 在主场景里**任何页面**都会跑
            // （主菜单/地图/关卡），所以拿它当心跳最省事；真正的自测开关在配置里。
            Patch(typeof(MainSceneController), "Update",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.SceneUpdatePostfix), ref ok, ref fail);

            // ---- Global.Input 的指针位置（回放时报告回放到的那一刻）----
            // 这三个方法是纯静态工具，没有静态字段，在 Load 阶段反射它们不会触发
            // 那种依赖游戏初始化的 .cctor（仓库里踩过的是 MVZ2Logic 里带
            // [PropertyRegistryRegion] 的那类静态类，不是这个）。
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "GetPointerPosition",
                new[] { typeof(int), typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.GetPointerPositionByButtonPrefix), null, ref ok, ref fail);

            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "GetPointerPosition",
                new[] { typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.GetPointerPositionByPointerIdPrefix), null, ref ok, ref fail);

            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "TryGetPointerPosition",
                new[] { typeof(int), typeof(int), typeof(Vector2).MakeByRefType() },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.TryGetPointerPositionPrefix), null, ref ok, ref fail);

            // ---- 假鼠标（纯视觉，见 FakePointer）----
            // 手持物的世界坐标：每帧由指针位置算出来。只在这一层换指针，
            // 换完立刻还原，所以 Global.Input / 矿车 / 喷瓜 / 打砖块看到的都是原值。
            Patch(typeof(LevelController), "UpdateHeldItemPosition",
                Type.EmptyTypes,
                nameof(ReplayPatches.HeldItemPosPrefix), nameof(ReplayPatches.HeldItemPosPostfix), ref ok, ref fail);

            // ---- 回放模式的菜单（重新播放 / 结束播放）----
            // 文案：挂在两个 Update*Button 之后 —— 原版每次刷新文案都会走那里，
            // 我们跟在后面覆盖，就不会出现「先对后错」。
            Patch(typeof(MVZ2.Options.OptionsDialogController), "UpdateRestartButton",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.OptionsRestartLabelPostfix), ref ok, ref fail);

            Patch(typeof(MVZ2.Options.OptionsDialogController), "UpdateLeaveLevelButton",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.OptionsLeaveLabelPostfix), ref ok, ref fail);

            // 行为：这两个按钮在回放期间由我们接管（重播 / 回放页）。
            Patch(typeof(MVZ2.Options.OptionsDialogController), "OnMainPageButtonClickCallback",
                new[] { typeof(MVZ2.UI.OptionsDialogMainPage.ButtonType) },
                nameof(ReplayPatches.OptionsMainButtonPrefix), null, ref ok, ref fail);

            // 结束面板开着时别让 Esc / 空格把别的对话框叠上来。
            Patch(typeof(LevelController), "OnOptionsKey",
                Type.EmptyTypes,
                nameof(ReplayPatches.OptionsKeyBlockPrefix), null, ref ok, ref fail);
            Patch(typeof(LevelController), "OnPauseKey",
                Type.EmptyTypes,
                nameof(ReplayPatches.OptionsKeyBlockPrefix), null, ref ok, ref fail);

            // ---- 回放里的「关卡结束」不再走原版流程 ----
            // 通关：原版会 RemoveLevelState（删关卡存档）+ 解锁关卡 + 退场动画，回放不能干这些。
            Patch(typeof(LevelController), "OnEngineClearCallback",
                Type.EmptyTypes,
                nameof(ReplayPatches.EngineClearPrefix), null, ref ok, ref fail);

            // 失败：原版会弹自己的「重试 / 返回」对话框，和我们的结束面板重复。
            Patch(typeof(LevelController), "ShowGameOverDialog",
                Type.EmptyTypes,
                nameof(ReplayPatches.GameOverDialogPrefix), null, ref ok, ref fail);

            // 落点：「结束播放」要回回放页（插件页），而不是地图。
            Patch(typeof(MainSceneController), "GotoMapOrMainmenu",
                Type.EmptyTypes,
                nameof(ReplayPatches.GotoMapOrMainmenuPrefix), null, ref ok, ref fail);

            Plugin.Log?.LogInfo("[Replay] 补丁安装完成：成功 " + ok + " 个，失败 " + fail + " 个");
            if (fail > 0)
                Plugin.Log?.LogWarning("[Replay] 有 " + fail + " 个补丁没挂上，录制内容会不完整");
        }

        #region 存档护栏（延迟安装）
        private static bool saveGuardApplied;
        private static int saveGuardInstalled;
        private static readonly System.Collections.Generic.List<string> saveGuardMissing =
            new System.Collections.Generic.List<string>();

        /// <summary>存档护栏补丁的安装情况（写进播放小结）。</summary>
        public static string SaveGuardStatus
        {
            get
            {
                if (!saveGuardApplied)
                    return "未安装";
                if (saveGuardMissing.Count == 0)
                    return "已挂 " + saveGuardInstalled + " 个";
                // ⛔ 失败的必须点名：少挂一个就意味着回放能改动存档的某一项，
                // 而「挂上了没生效」和「没挂上」从游戏表现上完全看不出来。
                return "已挂 " + saveGuardInstalled + " 个，❌ 失败 " + saveGuardMissing.Count
                    + " 个（" + string.Join("、", saveGuardMissing.ToArray()) + "）";
            }
        }

        /// <summary>装一个存档护栏补丁；失败就记下名字（会出现在播放小结里）。</summary>
        private static void GuardPatch(Type type, string method, Type[] args, string prefix)
        {
            int ok = 0, fail = 0;
            if (PatchIn(type, method, args, typeof(ReplaySaveGuard), prefix, null, ref ok, ref fail))
                saveGuardInstalled++;
            else
                saveGuardMissing.Add(type.Name + "." + method);
        }

        /// <summary>
        /// 安装「回放期间不许改动存档」的补丁（详见 <see cref="ReplaySaveGuard"/>）。
        ///
        /// <para><b>为什么故意拖到第一次播放才装：</b>目标里有一半在 <c>MVZ2Logic.*</c>
        /// （金币那两处）。本仓库踩过的坑是「在 <c>Plugin.Load()</c> 阶段反射
        /// <c>MVZ2Logic</c> 的类型会提前触发它的 <c>.cctor</c>，而那种 cctor 依赖游戏初始化 →
        /// 抛异常并被运行时永久缓存」。等到真的要播放时，游戏早就初始化完了，
        /// 这时候反射它们怎么都安全。</para>
        /// </summary>
        public static void EnsureSaveGuardHooks()
        {
            if (saveGuardApplied)
                return;
            saveGuardApplied = true;

            var ext = typeof(MVZ2Logic.Saves.LogicSaveExt);
            var data = typeof(MVZ2Logic.Saves.LogicSaveData);
            var sm = typeof(MVZ2.Saves.SaveManager);
            var ns = typeof(PVZEngine.NamespaceID);

            // ---- 金币①：LogicSaveExt（Global.Saves.* 的公开面，游戏绝大多数地方走这条）----
            GuardPatch(ext, "GetMoney", new[] { typeof(IGlobalSaveData) },
                nameof(ReplaySaveGuard.ExtGetMoneyPrefix));
            GuardPatch(ext, "SetMoney", new[] { typeof(IGlobalSaveData), typeof(int) },
                nameof(ReplaySaveGuard.ExtSetMoneyPrefix));
            GuardPatch(ext, "AddMoney", new[] { typeof(IGlobalSaveData), typeof(int) },
                nameof(ReplaySaveGuard.ExtAddMoneyPrefix));

            // ---- 金币②：LogicSaveData（直接摸到数据对象那条）----
            // 两层都挂是因为这两个方法都很短，IL2CPP 可能把里层内联进外层；
            // 只挂一层的话，真被内联了那一层就永远不生效（且不报任何错）。
            GuardPatch(data, "GetMoney", Type.EmptyTypes, nameof(ReplaySaveGuard.DataGetMoneyPrefix));
            GuardPatch(data, "SetMoney", new[] { typeof(int) }, nameof(ReplaySaveGuard.DataSetMoneyPrefix));

            // ---- 统计 / 解锁 / 难度记录 / 关卡进度 / 上次选卡 / 上次地图 / 地图对话：一律拦掉 ----
            // 统计那几个（击杀数 / 放置数 / 通关次数…）是「统计数据变化会反映到回放外」的
            // 直接来源：它们挂在 POST_ENEMY_SPAWNED / POST_ENTITY_DEATH / POST_LEVEL_CLEAR
            // 这些关卡回调上，回放重演时会一条条累加进 Global.Saves。
            var block = nameof(ReplaySaveGuard.BlockPrefix);
            GuardPatch(sm, "SetStat", new[] { ns, ns, typeof(long) }, block);
            GuardPatch(sm, "SetDirectEntryStat", new[] { ns, typeof(long) }, block);
            GuardPatch(sm, "Unlock", new[] { ns }, block);
            GuardPatch(sm, "Relock", new[] { ns }, block);
            GuardPatch(sm, "AddLevelDifficultyRecord", new[] { ns, ns }, block);
            GuardPatch(sm, "SetCurrentEndlessFlag", new[] { ns, typeof(int) }, block);

            // 这三个是 IGlobalSaveData 的扩展方法，Main.SaveManager.SetLastSelection(...) 走的正是它们。
            // 「上次选卡」在**选卡阶段**就会被写（LevelBlueprintChooseController），
            // 也就是说只要放一次回放，玩家自己存的上一次选卡就被换掉了。
            GuardPatch(ext, "SetLastMapID", new[] { typeof(IGlobalSaveData), ns }, block);
            GuardPatch(ext, "SetLastSelection", null, block);
            GuardPatch(ext, "SetMapTalk", new[] { typeof(IGlobalSaveData), ns }, block);

            Plugin.Log?.LogInfo("[Replay] 存档护栏补丁：" + SaveGuardStatus);
            if (saveGuardMissing.Count > 0)
                Plugin.Log?.LogWarning("[Replay] 有存档护栏补丁没挂上，回放可能会改动存档的对应项：" + SaveGuardStatus);
        }
        #endregion

        /// <summary>挂一个补丁（prefix/postfix 都在 <see cref="ReplayPatches"/> 里）；失败只记日志。</summary>
        private static bool Patch(Type type, string method, Type[] args, string prefix, string postfix, ref int ok, ref int fail)
        {
            return PatchIn(type, method, args, typeof(ReplayPatches), prefix, postfix, ref ok, ref fail);
        }

        /// <summary>挂一个补丁，可指定 prefix/postfix 所在的类型。</summary>
        private static bool PatchIn(Type type, string method, Type[] args, Type holder, string prefix, string postfix, ref int ok, ref int fail)
        {
            try
            {
                MethodInfo target = args == null
                    ? AccessTools.Method(type, method)
                    : AccessTools.Method(type, method, args);
                if (target == null)
                {
                    fail++;
                    Plugin.Log?.LogError("[Replay] 找不到补丁目标：" + type.FullName + "." + method);
                    return false;
                }

                HarmonyMethod pre = null;
                HarmonyMethod post = null;
                if (prefix != null)
                {
                    var m = AccessTools.Method(holder, prefix);
                    if (m == null) { fail++; Plugin.Log?.LogError("[Replay] 找不到 prefix " + prefix); return false; }
                    pre = new HarmonyMethod(m);
                }
                if (postfix != null)
                {
                    var m = AccessTools.Method(holder, postfix);
                    if (m == null) { fail++; Plugin.Log?.LogError("[Replay] 找不到 postfix " + postfix); return false; }
                    post = new HarmonyMethod(m);
                }

                harmony.Patch(target, pre, post);
                ok++;
                return true;
            }
            catch (Exception e)
            {
                fail++;
                Plugin.Log?.LogError("[Replay] 挂补丁失败 " + type.FullName + "." + method + " —— " + Describe(e));
                return false;
            }
        }

        /// <summary>把异常链摊平；Harmony 的失败原因通常在内层。</summary>
        private static string Describe(Exception e)
        {
            var sb = new StringBuilder();
            var cur = e;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                if (depth > 0)
                    sb.Append("  <-  ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }
}
