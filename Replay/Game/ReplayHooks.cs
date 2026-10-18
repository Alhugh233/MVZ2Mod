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

        /// <summary>补丁安装结果（给自测断言用：Harmony 挂失败是**不会抛的**，只能自己报）。</summary>
        private static readonly System.Collections.Generic.List<string> missing =
            new System.Collections.Generic.List<string>();

        /// <summary>形如 <c>成功 24 个，失败 2 个（A.B、C.D）</c>。</summary>
        public static string PatchStatus { get; private set; } = "未安装";

        /// <summary>有没有补丁没挂上。</summary>
        public static bool HasMissingPatches => missing.Count > 0;

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
            // 实测（2026-10-04 早期内测版）：那三个方法本身没问题，但**触碰这个类型**
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

            // 回放进关卡：不再自己切场景（0.7.0 上会崩），而是发游戏自己的指令（/gotolevel / /load），
            // 然后在 ReplaySceneEntry 的轮询里接管。详见 ReplaySceneEntry 与 DESIGN.md。
            Patch(typeof(MVZ2.Level.LevelManager), "InitLevel",
                null,
                nameof(ReplayPatches.LevelManagerInitLevelPrefix), null, ref ok, ref fail);

            // ==== 回放接管的两道闸门（不是临时诊断，别删）====
            // ① LevelController.InitLevel —— 真正重建关卡那一步。
            //    ⚠️ 只拦 LevelManager.InitLevel 不够：/load 的迟到续体会直接调它，
            //    而且不经过 LevelManager.InitLevel 的托管桩（实测它的前缀只在启动时打过一次）。
            // ② LevelController.StartLevelIntro —— 新开一局的流程，接管过的控制器上要跳过。
            Patch(typeof(LevelController), "InitLevel",
                null,
                nameof(ReplayPatches.LevelControllerInitLevelPrefix), null, ref ok, ref fail);
            Patch(typeof(LevelController), "StartLevelIntro",
                null,
                nameof(ReplayPatches.LevelControllerStartLevelIntroPrefix), null, ref ok, ref fail);

            // ==== 关卡内的「减速按钮」（见 ReplaySlowDown）====
            // ① 倍率：直接乘在 GetGameSpeed 的最后，不动游戏自己的 slowMotions 列表
            //    （那个带超时、没有移除接口）。
            // ② 互斥：加速的入口太多（按钮/热键/别的 mod），但它们最终都会走到
            //    LevelUIPreset.SetSpeedUp(bool)，所以只拦这一个公开方法。
            // ⚠️ 这两个 postfix 写在 ReplaySlowDown 里，所以必须用 PatchIn 指定 holder ——
            //    默认的 Patch() 会在 ReplayPatches 里找，找不到就静默失败（只记一行日志）。
            PatchIn(typeof(LevelController), "GetGameSpeed",
                Type.EmptyTypes,
                typeof(ReplaySlowDown), null, nameof(ReplaySlowDown.GetGameSpeedPostfix), ref ok, ref fail);
            PatchIn(typeof(MVZ2.Level.UI.LevelUIPreset), "SetSpeedUp",
                new[] { typeof(bool) },
                typeof(ReplaySlowDown), null, nameof(ReplaySlowDown.SetSpeedUpPostfix), ref ok, ref fail);

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

            // ---- 按键状态接管 + 掐掉游戏自己的鼠标轮询（v9）----
            //
            // 为什么必须做这一整套（详见 ReplayPointerTrajectory / ReplayInputPatches）：
            //   ① InputManager.UpdatePointer 是「第二条输入通路」的源头，每帧读的是**旁观者**的鼠标
            //      → 不掐的话右键取消手持物、碾压墙的每次点击都会被观众触发；
            //   ② InputHelper.IsPointerOfPhase 读的是 Input.GetMouseButton*，同样旁观者说了算
            //      → 不接管的话重装兵器在回放里永远不开火（它要 IsPointerHolding(MOUSE,0)）。
            //
            // 这些方法都很短、IL2CPP 可能把里层内联进外层，所以**每一层都挂**；
            // 同时命中是安全的（答案都由同一个掩码算出，覆盖不会累加）。
            PatchIn(typeof(MVZ2.Inputs.InputManager), "UpdatePointer",
                Type.EmptyTypes,
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.UpdatePointerPrefix), null, ref ok, ref fail);

            var phaseType = typeof(MVZ2Logic.Inputs.PointerPhase);
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerOfPhase",
                new[] { typeof(int), phaseType },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerOfPhasePrefix), null, ref ok, ref fail);

            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerDown",
                new[] { typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerDownByIdPrefix), null, ref ok, ref fail);
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerHolding",
                new[] { typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerHoldingByIdPrefix), null, ref ok, ref fail);
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerUp",
                new[] { typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerUpByIdPrefix), null, ref ok, ref fail);

            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerDown",
                new[] { typeof(int), typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerDownByButtonPrefix), null, ref ok, ref fail);
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerHolding",
                new[] { typeof(int), typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerHoldingByButtonPrefix), null, ref ok, ref fail);
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "IsPointerUp",
                new[] { typeof(int), typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.IsPointerUpByButtonPrefix), null, ref ok, ref fail);

            // ---- 选项对话框（v9）----
            // 关卡里所有多选对话框都汇到 CustomDialog：录制时记「选项列表 + 玩家选了哪一项」，
            // 回放时显示 N 秒后自动按录制选择点掉（否则逻辑 tick 会一直暂停在那里等真人）。
            //
            // ⚠️ 这里**不能**用 `new[]{ typeof(string), …, typeof(Action<int>) }` 去找 SetDialog：
            // 那个委托参数在 interop 里的类型不是 `System.Action<int>`，按签名找会直接找不到目标
            // （实测“成功 35 个，失败 1 个（CustomDialog.SetDialog（找不到目标））”）。
            // 改成「按名字 + 参数个数」找；前缀也只声明自己需要的几个参数。
            var setDialog = FindByArity(typeof(MVZ2.UI.CustomDialog), "SetDialog", 4);
            if (setDialog != null)
            {
                PatchMethod(setDialog, typeof(ReplayDialogPatches),
                    nameof(ReplayDialogPatches.SetDialogPrefix), null, ref ok, ref fail);
            }
            else
            {
                fail++;
                missing.Add("CustomDialog.SetDialog（按名字+参数个数也没找到）");
                Plugin.Log?.LogError("[Replay] 找不到 CustomDialog.SetDialog（回放里无法自动应答对话框）");
            }

            PatchIn(typeof(MVZ2.UI.CustomDialog), "OnButtonRowItemClickCallback",
                new[] { typeof(MVZ2.UI.ButtonRow), typeof(int) },
                typeof(ReplayDialogPatches), nameof(ReplayDialogPatches.OptionClickPrefix), null, ref ok, ref fail);

            // 每行的按钮文字（「选项文字 → (行, 列)」的根据；私有字段 interop 拿不到）。
            // ⚠️ 参数类型在 interop 里是 Il2CppStringArray，**不是** string[]。
            PatchIn(typeof(MVZ2.UI.ButtonRow), "UpdateButtons",
                new[] { typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray) },
                typeof(ReplayDialogPatches), nameof(ReplayDialogPatches.RowTextsPrefix), null, ref ok, ref fail);

            // ---- 假鼠标（纯视觉，见 FakePointer）----
            // 手持物模型的世界坐标：挂在**终点**（LevelUI.SetHeldItemPosition）而不是
            // 起点（LevelController.UpdateHeldItemPosition 里那条 InputHelper 链）——
            // 链尾的 InputHelper.GetPointerPosition 只有一行，IL2CPP 可能把它内联进这个调用点，
            // 那样前缀就永远不执行（症状正是「手持蓝图时指针瞬移」）。详见 HeldItemWorldPrefix。
            //
            // ⚠️ 类型是 **LevelUI**（运行时那个 UI 对象），不是 LevelUIPreset（预制体/预设）。
            // 挂错过一次：dump 里 `SetHeldItemPosition` 的实际声明类是 `LevelUI`。
            Patch(typeof(MVZ2.Level.UI.LevelUI), "SetHeldItemPosition",
                new[] { typeof(Vector2) },
                nameof(ReplayPatches.HeldItemWorldPrefix), null, ref ok, ref fail);

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

            PatchStatus = "成功 " + ok + " 个，失败 " + fail + " 个"
                + (missing.Count > 0
                    ? "；没挂上：" + string.Join("、", missing.ToArray())
                    : string.Empty)
                + (bindProblems.Count > 0
                    ? "；⚠️ 形参绑不上：" + string.Join("、", bindProblems.ToArray())
                    : string.Empty);
            // 有失败就**点名**打一条 —— 只报个数的话，排查时得靠猜是哪一个。
            if (fail > 0 || missing.Count > 0)
                Plugin.Log?.LogWarning("[Replay] 补丁情况：" + PatchStatus);
            if (bindProblems.Count > 0)
            {
                Plugin.Log?.LogWarning("[Replay] 有补丁挂上了但形参绑不上（Harmony 不报错，会静静用默认值）："
                    + string.Join("；", bindProblems.ToArray()));
            }
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
            //
            // ⚠️ 但 0.7.0 正式版上这一层**实测会让播放崩溃**（见 SaveGuardDataPatches 的配置说明）：
            // 只要它真的拦下一次 LogicSaveData.GetMoney，进程就 AV 在
            // GameAssembly.dll 0x0141b822（RuntimeType.IsAssignableFrom）。
            // 所以默认关掉，只留上面 LogicSaveExt 那一层 —— 游戏绝大多数地方走公开面。
            if (ReplayConfig.SaveGuardDataPatchesOn)
            {
                GuardPatch(data, "GetMoney", Type.EmptyTypes, nameof(ReplaySaveGuard.DataGetMoneyPrefix));
                GuardPatch(data, "SetMoney", new[] { typeof(int) }, nameof(ReplaySaveGuard.DataSetMoneyPrefix));
            }
            else
            {
                Plugin.Log?.LogInfo("[Replay] [General] SaveGuardDataPatches = false："
                    + "只挂 LogicSaveExt 那一层（直连 LogicSaveData 的补丁会崩，默认不挂）");
            }

            // ---- 统计 / 解锁 / 难度记录 / 关卡进度 / 上次选卡 / 上次地图 / 地图对话：一律拦掉 ----
            // 统计那几个（击杀数 / 放置数 / 通关次数…）是「统计数据变化会反映到回放外」的
            // 直接来源：它们挂在 POST_ENEMY_SPAWNED / POST_ENTITY_DEATH / POST_LEVEL_CLEAR
            // 这些关卡回调上，回放重演时会一条条累加进 Global.Saves。
            if (!ReplayConfig.SaveGuardBlockWritesOn)
            {
                Plugin.Log?.LogWarning("[Replay] [General] SaveGuardBlockWrites = false："
                    + "只拦金币，统计/解锁这一半不拦");
                Plugin.Log?.LogInfo("[Replay] 存档护栏补丁：" + SaveGuardStatus);
                return;
            }

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
        /// <summary>
        /// 装一个补丁，并把成败计进 <paramref name="ok"/><paramref name="fail"/>。
        ///
        /// <para>目标方法不在 <c>ReplayPatches</c> 里时，必须用 <paramref name="holder"/> 指明
        /// 前缀/后缀实际所在的类型 —— <c>Patch()</c> 只会在 <c>ReplayPatches</c> 上找，
        /// 找不到时 HarmonyX **不抛异常**，只打一行 <c>Could not find method</c>。</para>
        /// </summary>
        internal static bool PatchIn(Type type, string method, Type[] args, Type holder, string prefix, string postfix, ref int ok, ref int fail)
        {            try
            {
                MethodInfo target = args == null
                    ? AccessTools.Method(type, method)
                    : AccessTools.Method(type, method, args);
                if (target == null)
                {
                    fail++;
                    missing.Add(type.Name + "." + method + "（找不到目标）");
                    Plugin.Log?.LogError("[Replay] 找不到补丁目标：" + type.FullName + "." + method);
                    return false;
                }

                HarmonyMethod pre = null;
                HarmonyMethod post = null;
                if (prefix != null)
                {
                    var m = AccessTools.Method(holder, prefix);
                    if (m == null)
                    {
                        fail++;
                        missing.Add(type.Name + "." + method + "（找不到 prefix " + prefix + "）");
                        Plugin.Log?.LogError("[Replay] 找不到 prefix " + prefix);
                        return false;
                    }
                    pre = new HarmonyMethod(m);
                }
                if (postfix != null)
                {
                    var m = AccessTools.Method(holder, postfix);
                    if (m == null)
                    {
                        fail++;
                        missing.Add(type.Name + "." + method + "（找不到 postfix " + postfix + "）");
                        Plugin.Log?.LogError("[Replay] 找不到 postfix " + postfix);
                        return false;
                    }
                    post = new HarmonyMethod(m);
                }

                harmony.Patch(target, pre, post);
                ok++;
                if (prefix != null)
                    ValidateBinding(target, holder, prefix);
                if (postfix != null)
                    ValidateBinding(target, holder, postfix);
                return true;
            }
            catch (Exception e)
            {
                fail++;
                missing.Add(type.Name + "." + method + "（" + e.GetType().Name + "）");
                Plugin.Log?.LogError("[Replay] 挂补丁失败 " + type.FullName + "." + method + " —— " + Describe(e));
                return false;
            }
        }

        /// <summary>按「名字 + 参数个数」找一个方法（用于参数类型不好写的那些）。</summary>
        private static MethodInfo FindByArity(Type type, string name, int arity)
        {
            try
            {
                var methods = AccessTools.GetDeclaredMethods(type);
                for (int i = 0; i < methods.Count; i++)
                {
                    var m = methods[i];
                    if (m.Name == name && m.GetParameters().Length == arity)
                        return m;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 枚举 " + type.Name + "." + name + " 出错：" + e.GetType().Name);
            }
            return null;
        }

        /// <summary>挂一个已经解析好的方法（<see cref="FindByArity"/> 用）。</summary>
        private static bool PatchMethod(MethodInfo target, Type holder, string prefix, string postfix,
            ref int ok, ref int fail)
        {
            try
            {
                HarmonyMethod pre = null;
                HarmonyMethod post = null;
                if (prefix != null)
                {
                    var m = AccessTools.Method(holder, prefix);
                    if (m == null)
                    {
                        fail++;
                        missing.Add(target.Name + "（找不到 prefix " + prefix + "）");
                        return false;
                    }
                    pre = new HarmonyMethod(m);
                }
                if (postfix != null)
                {
                    var m = AccessTools.Method(holder, postfix);
                    if (m == null)
                    {
                        fail++;
                        missing.Add(target.Name + "（找不到 postfix " + postfix + "）");
                        return false;
                    }
                    post = new HarmonyMethod(m);
                }
                harmony.Patch(target, pre, post);
                ok++;
                ValidateBinding(target, holder, prefix);
                ValidateBinding(target, holder, postfix);
                return true;
            }
            catch (Exception e)
            {
                fail++;
                missing.Add(target.DeclaringType.Name + "." + target.Name + "（" + e.GetType().Name + "）");
                Plugin.Log?.LogError("[Replay] 挂补丁失败 " + target.DeclaringType.FullName + "." + target.Name
                    + " —— " + Describe(e));
                return false;
            }
        }

        /// <summary>形参绑定问题（名字对不上 / 类型不匹配）。</summary>
        private static readonly System.Collections.Generic.List<string> bindProblems =
            new System.Collections.Generic.List<string>();

        /// <summary>有没有任何补丁问题（没挂上、或挂上了但形参绑不上）。</summary>
        public static bool HasPatchProblems => missing.Count > 0 || bindProblems.Count > 0;

        /// <summary>
        /// 校验一个 Harmony 补丁的形参能不能真的绑到原方法的参数上。
        ///
        /// <para><b>为什么非做不可：</b>Harmony 是**按名字**把前缀/后缀的形参绑到原方法参数上的，
        /// 名字对不上时它**不会报错** —— 那个形参就拿到默认值（null / 0 / false），
        /// 于是补丁「看起来挂上了、实际啥也没干」。本仓库已经因此吃过两次亏：</para>
        /// <list type="bullet">
        /// <item><c>SetMoney(IGlobalSaveData save, int value)</c> 写成 <c>int value</c>（原名是 <c>money</c>）
        /// → 存档护栏的两个补丁根本没生效；</item>
        /// <item><c>SetDialog(…, Il2CppStringArray options, …)</c> 写成 <c>string[] options</c>
        /// → 数组绑不上，对话框的选项列表全是空的。</item>
        /// </list>
        /// <para>所以：名字必须在原方法里存在，类型必须相等或可赋值。</para>
        /// </summary>
        private static void ValidateBinding(MethodInfo target, Type holder, string patchName)
        {
            if (string.IsNullOrEmpty(patchName))
                return;
            try
            {
                var patch = AccessTools.Method(holder, patchName);
                if (patch == null)
                    return; // 找不到补丁方法这件事已经在别处报过了

                var originals = target.GetParameters();
                var declared = patch.GetParameters();
                for (int i = 0; i < declared.Length; i++)
                {
                    var p = declared[i];
                    var name = p.Name;
                    if (name == null || name.StartsWith("__", StringComparison.Ordinal))
                        continue; // __instance / __result / __state … 是 Harmony 自己的

                    ParameterInfo match = null;
                    for (int j = 0; j < originals.Length; j++)
                    {
                        if (originals[j].Name == name)
                        {
                            match = originals[j];
                            break;
                        }
                    }
                    if (match == null)
                    {
                        bindProblems.Add(target.DeclaringType.Name + "." + target.Name + " ← " + patchName
                            + "：形参名 " + name + " 在原方法里不存在（Harmony 会静静给默认值）");
                        continue;
                    }
                    if (match.ParameterType == p.ParameterType)
                        continue;
                    if (match.ParameterType.IsAssignableFrom(p.ParameterType))
                        continue;
                    // `ref X` 形参接管值参数 `X` 是 Harmony 的标准用法（改的是原方法栈上的那个变量），
                    // 例如 LevelUI.SetHeldItemPosition(Vector2) ← prefix(ref Vector2 worldPos)。
                    // 不认这条会误报「形参绑不上」。
                    if (p.ParameterType.IsByRef && p.ParameterType.GetElementType() == match.ParameterType)
                        continue;
                    bindProblems.Add(target.DeclaringType.Name + "." + target.Name + " ← " + patchName
                        + "：形参 " + name + " 类型不匹配（原方法 " + match.ParameterType.Name
                        + " / 补丁 " + p.ParameterType.Name + "）");
                }
            }
            catch (Exception e)
            {
                bindProblems.Add(target.DeclaringType.Name + "." + target.Name + "（校验出错 "
                    + e.GetType().Name + "）");
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
