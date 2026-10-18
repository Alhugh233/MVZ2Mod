using System;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2Logic.Curses;
using MVZ2Logic.Definitions;
using MVZ2Logic.Games;
using MVZ2Logic.Modding;
using MVZ2Logic.Saves;
using MVZ2Logic.Serialization;
using PVZEngine;
using PVZEngine.Callbacks;
using PVZEngine.Definitions;

namespace ModularCurse
{
    /// <summary>
    /// 模块化诅咒的 Mod 逻辑主体。
    ///
    /// 官方 ModLoader.LoadAll 会对本实例依次调用 Preload/PreInit/Load/OnInit/AddMod/PostInit。
    /// 本 mod 不提供关卡/敌人等内容，只提供：
    ///   - 25 个诅咒 buff 定义（24 条诅咒 + SC-C7 的敌人子 buff），
    ///     由 CurseDefinitionPatch 在 LoadStages 时机创建并 AddDefinition；
    ///   - 24 条诅咒元数据（curses.xml），由官方 LoadCurseMetas 生成
    ///     DefaultCurseDefinition 并 AddDefinition。
    /// </summary>
    public class ModularCurseMod : Mod
    {
        public const string spaceName = "modular_curse";

        /// <summary>
        /// 本 mod 存档数据格式版本，写进 <c>SerializableModSaveData.version</c>。
        /// 读档时 <c>version &gt; SAVE_DATA_VERSION</c> 就抛 <c>NewerSaveDataException</c>
        /// （与官方 VanillaMod 同款语义）。旧存档（早期内测版时期写下的）version=0。
        /// </summary>
        private const int SAVE_DATA_VERSION = 1;

        /// <summary>注入类标准绑定 ctor（Il2CppInterop 官方模式）。</summary>
        public ModularCurseMod(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>
        /// 创建绑定注入类（klass=ModularCurseMod）的实例。
        /// 补做原生 Mod(string) ctor 的初始化（Namespace / definitionGroup / triggers
        /// 并把两个容器钉住）交给 DSHCore.InjectedMod 的公共样板。
        /// </summary>
        internal static ModularCurseMod CreateInstance()
        {
            var instance = InjectedMod.Create<ModularCurseMod>(spaceName);
            self = instance;
            // 立刻把 Namespace 字段钉住（详见 EnsureNamespace 注释）。
            EnsureNamespace();
            // 绑到定义设施（双保险：注入实例的 Namespace 字符串曾经间歇性读到空，
            // 而归属判定错一次整套补丁就对本文静默失效）。
            InjectedDefinitions.Bind(spaceName, instance);
            try
            {
                Plugin.Log?.LogInfo("[ModularCurseMod] CreateInstance ptr=" + instance.Pointer
                    + " Namespace='" + (instance.Namespace ?? "<null>") + "'");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ModularCurseMod] 读取 Namespace 失败: " + e.GetType().Name);
            }
            return instance;
        }

        public override void PreInit(IModLoader modLoader)
        {
            // 绝不能调 base.PreInit(modLoader)：代理虚调用会回到本方法 trampoline
            // 无限递归直到 native 栈溢出。原生 Mod.PreInit 是空实现，跳过无影响。
            Plugin.Log?.LogInfo("[ModularCurseMod] PreInit");
        }

        public override void OnInit(IGlobalGame game)
        {
            // 不能调 base.OnInit(game)（递归）。
            // LogicSaveData 的可序列化 DTO 必须登记进 BSON 类映射，否则读档时
            // Mongo 报 Unknown discriminator（官方 VanillaMod 也在 OnInit 里做这件事），
            // 且因 WriteSaveData 会「读回来验证」，验证失败会导致整份存档都不写。
            RegisterSerializableType<SerializableLogicSaveData>();
            Plugin.Log?.LogInfo("[ModularCurseMod] OnInit(game)");
        }

        public override void PostInit(IGlobalGame game)
        {
            // 不能调 base.PostInit(game)（递归）。
            EnsureNamespace();
            var buffs = 0;
            lock (Plugin.AllDefinitions)
            {
                foreach (var def in Plugin.AllDefinitions)
                {
                    if (def is PVZEngine.Buffs.BuffDefinition)
                        buffs++;
                }
            }
            Plugin.Log?.LogInfo("[ModularCurseMod] PostInit: 共 " + Plugin.AllDefinitions.Count
                + " 个定义（其中 buff " + buffs + " 个）");
            try
            {
                Plugin.Log?.LogInfo("[ModularCurseMod] PostInit 时 selfPtr="
                    + (self == null ? "<null>" : self.Pointer.ToString())
                    + " Namespace='" + (self == null ? "<null>" : (self.Namespace ?? "<null>"))
                    + "', CurseBuffs=" + CurseBuffs.Count);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ModularCurseMod] PostInit Namespace 读取失败: " + e.GetType().Name);
            }

            // mod 自带的 PNG 要手工进资源库（图片不走 XML 注入那套）。
            // 放在诅咒词条之前：词条里写的 sprite id 就是这些贴图的 id。
            CurseSprites.RegisterAll();

            // 官方 LoadCurseMetas 写属性时 MVZ2.Logic 的属性键可能尚未注册
            // （Key==0 → 写入落空），此时键已就绪，重新套一遍。
            CurseMetaApplier.ApplyAll();

            // 同上：修饰符也必须等属性键注册之后再建，否则 CURSE_COUNT 等修饰符失效。
            CurseBuffs.ApplyModifiers();
        }

        /// <summary>
        /// 全部 mod 与存档加载完成后的只读自检：
        /// 确认 24 条诅咒已进入全局内容表、处于"已解锁"状态（会出现在诅咒面板里），
        /// 且每条诅咒指向的 buff 定义都能解析出来。
        /// </summary>
        public override void PostGameInit()
        {
            // 不能调 base.PostGameInit()（空实现，递归风险）。
            try
            {
                EnsureNamespace();
                var main = MVZ2.Managers.MainManager.Instance;
                if (main == null)
                {
                    Plugin.Log?.LogWarning("[Diag] MainManager 为 null，跳过自检");
                    return;
                }
                var game = main.Game;
                if (game == null)
                {
                    Plugin.Log?.LogWarning("[Diag] GlobalGame 为 null，跳过自检");
                    return;
                }

                // 注意：代理不会复制接口声明，IGlobalGame 上看不到 IGameContent
                // 的扩展方法（LogicGameDefinitionsExt.GetAllCurseDefinitions），
                // 因此直接用 IGlobalGame 自身的泛型定义查询。
                var all = game.GetDefinitions<CurseDefinition>(LogicDefinitionTypes.CURSE);
                var ours = 0;
                var buffFailures = 0;
                if (all != null)
                {
                    for (var i = 0; i < all.Length; i++)
                    {
                        var def = all[i];
                        if (def == null)
                            continue;
                        var id = def.GetID();
                        if (id == null || id.SpaceName != spaceName)
                            continue;
                        ours++;
                        var buffID = def.GetBuffID();
                        var buffDef = buffID == null
                            ? null
                            : game.GetDefinition<PVZEngine.Buffs.BuffDefinition>(
                                EngineDefinitionTypes.BUFF, buffID);
                        if (buffDef == null)
                        {
                            buffFailures++;
                            Plugin.Log?.LogError("[Diag] " + id.Path + " 的 buff 无法解析: " + buffID);
                        }
                    }
                }

                var unlocked = main.SaveManager.GetUnlockedCurses();
                var unlockedOurs = 0;
                if (unlocked != null)
                {
                    for (var i = 0; i < unlocked.Length; i++)
                    {
                        var id = unlocked[i];
                        if (id != null && id.SpaceName == spaceName)
                            unlockedOurs++;
                    }
                }

                Plugin.Log?.LogInfo("[Diag] 诅咒定义 " + ours + "/" + (all == null ? 0 : all.Length)
                    + "，已解锁 " + unlockedOurs + "/" + (unlocked == null ? 0 : unlocked.Length)
                    + "，buff 解析失败 " + buffFailures + " 条"
                    + "，修饰符缺失 " + CurseBuffs.CountWithoutModifiers() + "/" + CurseBuffs.Count + " 条"
                    + "，CURSE_COUNT 键失效 " + CurseBuffs.CountWithStaleCurseCountKey() + " 条");

                // 面板顺序自检：前几项应该是原版（mvz2）的诅咒。
                if (unlocked != null && unlocked.Length > 0)
                {
                    var head = string.Empty;
                    var take = Math.Min(5, unlocked.Length);
                    for (var i = 0; i < take; i++)
                    {
                        var id = unlocked[i];
                        head += (i > 0 ? " " : string.Empty)
                            + (id == null ? "<null>" : id.SpaceName + ":" + id.Path);
                    }
                    Plugin.Log?.LogInfo("[Diag] 解锁列表前 " + take + " 项: " + head);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Diag] PostGameInit 自检失败: " + e);
            }
        }

        public override ModSaveData CreateSaveData()
        {
            return new LogicSaveData(spaceName);
        }

        // 存档接口在 0.7.0 由「只读的 LoadSaveData(string)」改成对称的一对
        // SerializeSaveData / DeserializeSaveData（改名 + 新增，老方法被删，override 它会 CS0115）。
        // 实现照抄官方 VanillaMod，走 Mod 基类的受保护 helper + LogicSaveData 自己的
        // ToSerializable / DeserializeFrom；不要自己拼 BSON（实测读不回来：
        // Unknown discriminator value 'SerializableLogicSaveData'）。
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
                // 见 LabMod 里同处的说明：官方 VanillaMod 会抛 NewerSaveDataException，
                // 但 interop 生成的它不继承 System.Exception，托管侧 throw 编译不过（CS0029），
                // 所以这里只明确告警。
                Plugin.Log?.LogWarning("[ModularCurseMod] 存档 version=" + seri.version
                    + " 高于本 mod 支持的 " + SAVE_DATA_VERSION + "，按兼容方式继续读取");
            }
            return LogicSaveData.DeserializeFrom(seri);
        }

        /// <summary>注入的 Mod 实例（诊断用）。</summary>
        internal static ModularCurseMod self;

        /// <summary>已钉住的 Namespace 字符串指针（唯一，永不释放）。</summary>
        private static IntPtr namespaceString = IntPtr.Zero;
        /// <summary>该字段的 FieldInfo 与偏移（首次使用时解析一次）。</summary>
        private static IntPtr namespaceField = IntPtr.Zero;
        private static int namespaceOffset = -1;
        private static bool namespaceFieldMissing;

        /// <summary>
        /// 确保 <c>mod.Namespace</c> 字段**始终**是可读的正确值，并把它钉成 GC 根。
        ///
        /// 为什么必须做：
        ///   1. 我们用 <c>DerivedConstructorPointer/Body</c> 造实例，**原生 ctor 不执行**，
        ///      所以 <c>&lt;Namespace&gt;k__BackingField</c> 只能靠托管侧赋值；
        ///   2. 该字段属于「注入类实例的引用字段」，il2cpp GC 不扫描
        ///      （同 Il2CppPin 注释里那条已知限制）→ 赋进去的 il2cpp 字符串
        ///      可能在任意时刻被回收，之后 <c>mod.Namespace</c> 读到空/悬垂值；
        ///   3. 游戏自己要用 <c>mod.Namespace</c>（`ModLoader.LoadXxxMetas` 里的
        ///      `var nsp = mod.Namespace;` + `res.GetModXxxMetas(nsp)`）来找本 mod 的
        ///      meta 与内容表，一旦读空，诅咒定义/buff 定义/词条全都注册不上
        ///      —— 这正是「诅咒一个都没进游戏、列表里看不到」的根因，且是间歇性的。
        ///
        /// 做法（全部走已核实的 API）：
        ///   - 自建一个 il2cpp 字符串并 `il2cpp_gchandle_new` **永久钉住**（成为 GC 根）；
        ///   - 用 `il2cpp_object_get_class` + `GetIl2CppField(klass, ...)` +
        ///     `il2cpp_field_get_offset` 算出字段地址，`Marshal.ReadIntPtr` 读回比较；
        ///   - 不一致就用 `FieldWriteWbarrierStub`（与 interop 生成的 setter 完全同一路径）
        ///     写回我们那个钉住的字符串。
        ///   全程只比指针、不解引用旧值（旧值可能是悬垂指针），幂等且可反复调用。
        /// </summary>
        internal static void EnsureNamespace()
        {
            var instance = self;
            if (instance == null)
                return;
            try
            {
                if (namespaceString == IntPtr.Zero)
                {
                    namespaceString = Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(spaceName);
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(namespaceString, false);
                }
                if (namespaceField == IntPtr.Zero)
                {
                    if (namespaceFieldMissing)
                        return;
                    // 注意：IL2CPP 元数据里自动属性的字段名就是 `<Namespace>k__BackingField`；
                    // Il2CppInterop 只是把它改写成 `_Namespace_k__BackingField`，原生查找要用原名。
                    var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(instance.Pointer);
                    while (klass != IntPtr.Zero && namespaceField == IntPtr.Zero)
                    {
                        namespaceField = FindField(klass, "<Namespace>k__BackingField");
                        if (namespaceField == IntPtr.Zero)
                            namespaceField = FindField(klass, "_Namespace_k__BackingField");
                        if (namespaceField != IntPtr.Zero)
                            break;
                        klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_parent(klass);
                    }
                    if (namespaceField == IntPtr.Zero)
                    {
                        namespaceFieldMissing = true;
                        Plugin.Log?.LogWarning("[Namespace] 找不到 <Namespace>k__BackingField（已停用该项保护）");
                        return;
                    }
                    namespaceOffset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(namespaceField);
                }
                var addr = IntPtr.Add(instance.Pointer, namespaceOffset);
                var current = System.Runtime.InteropServices.Marshal.ReadIntPtr(addr);
                if (current == namespaceString)
                    return;
                Il2CppInterop.Runtime.IL2CPP.FieldWriteWbarrierStub(
                    instance.Pointer, addr, namespaceString);
                Plugin.Log?.LogInfo("[Namespace] Namespace 字段已重写（"
                    + (current == IntPtr.Zero ? "空" : "失效") + " → " + spaceName + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Namespace] EnsureNamespace 失败: " + e.GetType().Name
                    + ": " + e.Message);
            }
        }

        /// <summary>按名找字段（找不到返回 0，不抛）。</summary>
        private static IntPtr FindField(IntPtr klass, string name)
        {
            try
            {
                return Il2CppInterop.Runtime.IL2CPP.GetIl2CppField(klass, name);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }
    }
}
