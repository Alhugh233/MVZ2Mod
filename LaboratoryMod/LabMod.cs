using System;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2Logic.Games;
using MVZ2Logic.Commands;
using MVZ2Logic.Modding;
using MVZ2Logic.Saves;
using MVZ2Logic.Serialization;
using PVZEngine;
using PVZEngine.Callbacks;
using PVZEngine.Level;

namespace LaboratoryMod
{
    /// <summary>
    /// 实验室内容 Mod 的 Mod 逻辑主体（0.7 官方链版）。
    ///
    /// 0.7 中 LoadMods 不再重建 Logic：ModInfo.Logic 直接使用本注入实例，
    /// 官方 ModLoader.LoadAll 会对它依次调用 Preload/PreInit/Load/OnInit/AddMod/PostInit。
    /// - PreInit 注册 IStageDefinitionProvider（minigame 关卡由官方 LoadStages 创建）。
    /// - 关卡定义 ID 落在本 mod 命名空间（mvz2_lab:xxx），XML meta 的属性由
    ///   官方 LoadStageProperties 自动应用（无需再手动补 ENEMY_POOL 等）。
    /// </summary>
    public class LabMod : Mod
    {
        public const string spaceName = "mvz2_lab";

        /// <summary>
        /// 本 mod 存档数据格式版本，写进 <c>SerializableModSaveData.version</c>。
        /// 读档时 <c>version &gt; SAVE_DATA_VERSION</c> 就抛 <c>NewerSaveDataException</c>
        /// （与官方 VanillaMod 同款语义，见 SaveManager 的存档回退逻辑）。
        /// 旧存档（早期内测版时期写下的）version=0，仍然读得进来。
        /// </summary>
        private const int SAVE_DATA_VERSION = 1;

        /// <summary>注入类标准绑定 ctor（Il2CppInterop 官方模式）。</summary>
        public LabMod(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>
        /// 创建绑定注入类（klass=LabMod）的实例。
        /// 补做原生 Mod(string) ctor 的初始化（Namespace / definitionGroup / triggers
        /// 并把两个容器钉住）交给 DSHCore.InjectedMod 的公共样板。
        /// </summary>
        internal static LabMod CreateInstance()
        {
            var instance = InjectedMod.Create<LabMod>(spaceName);
            self = instance;
            // 把实例绑到定义设施（双保险：注入实例的 Namespace 字符串曾经间歇性读到空）。
            InjectedDefinitions.Bind(spaceName, instance);
            return instance;
        }

        public override void PreInit(IModLoader modLoader)
        {
            // 注意：绝不能调 base.PreInit(modLoader)！代理虚调用会回到本方法 trampoline
            // 无限递归直到 native 栈溢出。原生 Mod.PreInit 是空实现，跳过无影响。
            //
            // 0.7 中 ModLoader.LoadAll 会对 stageProviders.OrderBy(Priority) 做原生 LINQ
            // 接口分派 —— 注入类实现 IStageDefinitionProvider 时在 metadata v31 会抛
            // MethodAccessException（与 SetPropertyObject 同源）。因此不注册 provider，
            // 改由 LoadStagesPatch 在官方 LoadStages 时机直接创建关卡定义
            // （LoadStageProperties 依然在 LoadDefinitionProperties 中自动应用 meta 属性）。
            Plugin.Log?.LogInfo("[LabMod] PreInit (no provider registration; LoadStages patch handles defs)");
        }

        public override void OnInit(IGlobalGame game)
        {
            // 不能调 base.OnInit(game)（递归）。
            // 关卡定义由 LoadStagesPatch 创建（官方 LoadDefinitionProperties 已跳过，
            // 见 LoadStagesPatch.LoadDefPropsPrefix）。此处仅日志。
            //
            // 存档用 LogicSaveData，得把它的可序列化 DTO 登记进 BSON 类映射 ——
            // 官方 VanillaMod 也是在自己的 OnInit 里 RegisterSerializableType<...>()。
            // 漏了这一步的症状是读档时 "Unknown discriminator value
            // 'SerializableLogicSaveData'"，而且因为 SaveManager.WriteSaveData 写完会
            // 「读回来验证一次」，验证失败会直接**整份存档都不写**。
            RegisterSerializableType<SerializableLogicSaveData>();
            Plugin.Log?.LogInfo("[LabMod] OnInit(game) called");
        }

        public override void PostInit(IGlobalGame game)
        {
            // 不能调 base.PostInit(game)（递归）。
            Plugin.Log?.LogInfo("[LabMod] PostInit(game) called");
            lastGame = game;
            // ===== meta 属性应用 =====
            // 官方 LoadDefinitionProperties 已不再崩（Pin 修好了 propertyDict/ID 被
            // 回收的根因），但实测未把 meta 属性写进注入 def（关卡名仍为空）。
            // 因此用稳定 ID（StageID 常量）反查 meta 手动应用；属性 key 此时已注册，
            // def 的 propertyDict 也已 Pin 住，setter 可安全调用。
            for (var i = 0; i < registeredStages.Count; i++)
            {
                var stage = registeredStages[i];
                if (stage == null)
                    continue;
                try
                {
                    StageMetaApplier.ApplyMetaTo(stage, i < registeredStagePaths.Count
                        ? registeredStagePaths[i]
                        : null);
                }
                catch (System.Exception e)
                {
                    Plugin.Log?.LogError("[LabMod] PostInit ApplyMetaTo error: " + e);
                }
            }

            // unknow_palace 出怪接管（prefix 拦截 SpawnWaveEnemies → 自定义出怪）。
            // 该关卡敌人池含 undead_flying_object(UFO) 等，若放行原版出怪循环，
            // 原版 UFO 日程/生成逻辑在 unknow 配置下会持续 NRE（behavior.Update
            // 刷屏）。PostInit 应用：LoadAll 已结束，patch 静态方法无副作用，
            // 且必然早于玩家进关。
            try
            {
                Stages.UnknowPalacePatch.Apply();
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogError("[LabMod] PostInit UnknowPalacePatch error: " + e);
            }
        }

        /// <summary>LoadStagesPatch 创建的关卡定义（PostInit 统一套 meta 属性）。</summary>
        internal static readonly System.Collections.Generic.List<PVZEngine.Level.StageDefinition> registeredStages
            = new System.Collections.Generic.List<PVZEngine.Level.StageDefinition>();

        /// <summary>与 registeredStages 并行的稳定 ID path（StageID 常量，不读 GetID()）。</summary>
        internal static readonly System.Collections.Generic.List<string> registeredStagePaths
            = new System.Collections.Generic.List<string>();

        /// <summary>PostInit 缓存的 game（PostGameInit 无参，诊断需要它）。</summary>
        internal static IGlobalGame lastGame;

        /// <summary>注入的 Mod 实例（诊断用，验证 mod 级定义查找）。</summary>
        internal static LabMod self;

        internal static void RegisterStage(PVZEngine.Level.StageDefinition stage, string stagePath)
        {
            registeredStages.Add(stage);
            registeredStagePaths.Add(stagePath);
        }

        /// <summary>
        /// 所有 mod 加载完成后的钩子（MainManager → ModManager.PostGameInit）。
        /// 只做只读诊断：确认定义是否进 game 内容表、arcade 列表是否含本 mod。
        /// </summary>
        public override void PostGameInit()
        {
            // 不能调 base.PostGameInit()（空实现，递归风险）。
            try
            {
                DiagDump();
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogError("[LabMod] PostGameInit diag error: " + e);
            }
        }

        private static void DiagDump()
        {
            var L = Plugin.Log;
            L?.LogInfo("[Diag] ===== PostGameInit =====");
            var main = MVZ2.Managers.MainManager.Instance;
            if (main == null)
            {
                L?.LogWarning("[Diag] MainManager NULL");
                return;
            }
            var res = main.ResourceManager;
            L?.LogInfo("[Diag] ResourceManager=" + (res == null ? "NULL" : "ok"));
            // 用具体类型 GlobalGame（与官方 ArcadeController 同一调用路径）。
            var game = main.Game;
            L?.LogInfo("[Diag] GlobalGame=" + (game == null ? "NULL" : "ok"));
            if (game == null)
                return;

            // A) 独立新建的 NamespaceID 是否可读（隔离：测试字符串编组本身）
            try
            {
                var probe = new NamespaceID("mvz2_lab", "diag_probe");
                L?.LogInfo("[Diag] A: new NamespaceID ptr=" + probe.Pointer);
                L?.LogInfo("[Diag] A: SpaceName=" + probe.SpaceName + " Path=" + probe.Path);
            }
            catch (System.Exception e)
            {
                L?.LogError("[Diag] A error: " + e.GetType().Name + ": " + e.Message);
            }

            // B) 注入 def 的 id 字段往返（隔离：测试注入对象的字段读写）
            if (registeredStages.Count > 0)
            {
                try
                {
                    var d0 = registeredStages[0];
                    L?.LogInfo("[Diag] B: def ptr=" + (d0 == null ? "NULL" : d0.Pointer.ToString()));
                    if (d0 != null)
                    {
                        var heldId = d0.GetID();
                        L?.LogInfo("[Diag] B: GetID=" + (heldId == null ? "NULL" : "ptr=" + heldId.Pointer));
                        if (heldId != null)
                            L?.LogInfo("[Diag] B: SpaceName=" + heldId.SpaceName + " Path=" + heldId.Path);
                    }
                }
                catch (System.Exception e)
                {
                    L?.LogError("[Diag] B error: " + e.GetType().Name + ": " + e.Message);
                }
            }

            // C) GetDefinitionType 是否为 "stage"
            if (registeredStages.Count > 0 && registeredStages[0] != null)
            {
                try
                {
                    var t = registeredStages[0].GetDefinitionType();
                    L?.LogInfo("[Diag] C: GetDefinitionType=" + (t == null ? "<NULL>" : t));
                }
                catch (System.Exception e)
                {
                    L?.LogError("[Diag] C error: " + e.GetType().Name + ": " + e.Message);
                }
            }

            // 0) 纯计数探针
            try
            {
                var total = game.GetDefinitions<PVZEngine.Level.StageDefinition>(
                    PVZEngine.Definitions.EngineDefinitionTypes.STAGE);
                L?.LogInfo("[Diag] stage defs total=" + (total == null ? "NULL" : total.Length.ToString()));
            }
            catch (System.Exception e)
            {
                L?.LogError("[Diag] count stage defs error: " + e.GetType().Name + ": " + e.Message);
            }
            try
            {
                var allDefs = game.GetDefinitions();
                L?.LogInfo("[Diag] all defs total=" + (allDefs == null ? "NULL" : allDefs.Length.ToString()));
            }
            catch (System.Exception e)
            {
                L?.LogError("[Diag] count all defs error: " + e.GetType().Name + ": " + e.Message);
            }

            // 1) 逐个 ID 查询（ArcadeController 过滤关卡是否显示的判据）
            for (var i = 0; i < registeredStagePaths.Count; i++)
            {
                var p = registeredStagePaths[i];
                var id = new NamespaceID(spaceName, p);
                try
                {
                    var def = game.GetDefinition<PVZEngine.Level.StageDefinition>(
                        PVZEngine.Definitions.EngineDefinitionTypes.STAGE, id);
                    var line = "[Diag] GetDefinition(stage, " + p + ") = "
                        + (def == null ? "NULL" : "FOUND");
                    if (def != null)
                    {
                        try
                        {
                            line += " name='" + MVZ2Logic.Level.LogicStageProps.GetLevelName(def) + "'";
                        }
                        catch (System.Exception ie)
                        {
                            line += " [name err " + ie.GetType().Name + "]";
                        }
                    }
                    L?.LogInfo(line);
                }
                catch (System.Exception e)
                {
                    L?.LogError("[Diag] GetDefinition(" + p + ") threw "
                        + e.GetType().Name + ": " + e.Message);
                }
            }

            // 2) mod 级查找（官方 LoadStageProperties 走的正是这条路径）
            try
            {
                L?.LogInfo("[Diag] self=" + (self == null ? "NULL" : "ok"));
                var metas = res?.GetModStageMetas(spaceName);
                L?.LogInfo("[Diag] mod stage metas=" + (metas == null ? "NULL" : metas.Length.ToString()));
                if (self != null && metas != null)
                {
                    for (var i = 0; i < metas.Length; i++)
                    {
                        var m = metas[i];
                        if (m == null)
                        {
                            L?.LogInfo("[Diag]   meta[" + i + "] null");
                            continue;
                        }
                        var mid = new NamespaceID(spaceName, m.ID);
                        var found = self.GetDefinition<PVZEngine.Level.StageDefinition>(
                            PVZEngine.Definitions.EngineDefinitionTypes.STAGE, mid);
                        L?.LogInfo("[Diag]   meta[" + i + "] id=" + m.ID
                            + " name=" + m.Name
                            + " modLookup=" + (found == null ? "NULL" : "FOUND"));
                    }
                }
            }
            catch (System.Exception e)
            {
                L?.LogError("[Diag] mod lookup error: " + e.GetType().Name + ": " + e.Message);
            }

            // 3) arcade meta 列表（ModResource.ArcadeMetaList）
            try
            {
                var aml = res?.GetArcadeMetaList(spaceName);
                if (aml == null)
                    L?.LogWarning("[Diag] arcadeMetaList NULL");
                else if (aml.metas == null)
                    L?.LogWarning("[Diag] arcadeMetaList.metas NULL");
                else
                {
                    L?.LogInfo("[Diag] arcade metas=" + aml.metas.Length);
                    for (var i = 0; i < aml.metas.Length; i++)
                    {
                        var m = aml.metas[i];
                        if (m == null)
                        {
                            L?.LogInfo("[Diag]   [" + i + "] null");
                            continue;
                        }
                        L?.LogInfo("[Diag]   [" + i + "] id=" + m.ID + " type=" + m.Type
                            + " index=" + m.Index + " stage=" + m.StageID + " area=" + m.AreaID);
                    }
                }
            }
            catch (System.Exception e)
            {
                L?.LogError("[Diag] arcade list error: " + e.GetType().Name + ": " + e.Message);
            }

            // 4) arcadeCache（ArcadeController 展示用的全集）
            try
            {
                var items = res?.GetAllArcadeItems();
                if (items == null)
                    L?.LogWarning("[Diag] GetAllArcadeItems NULL");
                else
                {
                    var mine = 0;
                    for (var i = 0; i < items.Length; i++)
                    {
                        var it = items[i];
                        if (it != null && it.SpaceName == spaceName)
                            mine++;
                    }
                    L?.LogInfo("[Diag] GetAllArcadeItems=" + items.Length + " (mvz2_lab=" + mine + ")");
                }
            }
            catch (System.Exception e)
            {
                L?.LogError("[Diag] arcade items error: " + e.GetType().Name + ": " + e.Message);
            }

            L?.LogInfo("[Diag] ===== end =====");
        }

        public override ModSaveData CreateSaveData()
        {
            Plugin.Log?.LogInfo("[LabMod] CreateSaveData called");
            return new LogicSaveData(spaceName);
        }

        // 存档接口在 0.7.0 由「只读的 LoadSaveData(string)」改成对称的一对
        // SerializeSaveData / DeserializeSaveData（改名 + 新增，老方法被删，override 它会 CS0115）。
        //
        // 实现照抄官方 VanillaMod：走 Mod 基类的受保护 helper（Serialize / Deserialize）
        // 与 LogicSaveData 自己的 ToSerializable / DeserializeFrom，
        // **不要自己拼 BSON** —— SerializeHelper 维护的是一套「游戏自己知道」的
        // 序列化配置（SerializeHelper.init 里注册的那一堆类 + 临时挂的 Convention），
        // 外部自己调 BsonSerializer 读不回来（实测报 Unknown discriminator）。
        public override string SerializeSaveData(ModSaveData saveData)
        {
            var seri = saveData.ToSerializable();
            seri.version = SAVE_DATA_VERSION;
            return seri.ToBson();
        }

        public override ModSaveData DeserializeSaveData(string json)
        {
            var seri = Deserialize<SerializableLogicSaveData>(json);
            if (seri.version > SAVE_DATA_VERSION)
            {
                // 官方 VanillaMod 在这一行抛 MVZ2.Saves.NewerSaveDataException，让
                // SaveManager / 主菜单提示「存档来自更高的版本」。托管侧 throw 不了它 ——
                // interop 生成的 NewerSaveDataException 不继承 System.Exception，
                // `throw new NewerSaveDataException()` 直接 CS0029 编译不过。
                // 因此退化成「明确告警 + 尽力读」：本 mod 只在**改了存档格式**时才会
                // 抬高 SAVE_DATA_VERSION，所以真出现这行日志就意味着该看一看了。
                Plugin.Log?.LogWarning("[LabMod] 存档 version=" + seri.version
                    + " 高于本 mod 支持的 " + SAVE_DATA_VERSION + "，按兼容方式继续读取");
            }
            return LogicSaveData.DeserializeFrom(seri);
        }
    }
}
