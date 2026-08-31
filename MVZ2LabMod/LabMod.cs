using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2LabMod.Stages;
using MVZ2Logic.Games;
using MVZ2Logic.Modding;
using MVZ2Logic.Saves;
using PVZEngine;
using PVZEngine.Callbacks;

namespace MVZ2LabMod
{
    /// <summary>
    /// 实验室内容的 Mod 逻辑主体。
    /// 关卡定义沿用 vanilla 的 "mvz2" 命名空间（与 0.6.4 实验室内容一致，
    /// 存档写入原版命名空间；卸载 Mod 后相关存档条目可能报错，可接受）。
    /// </summary>
    public class LabMod : Mod
    {
        public const string spaceName = "mvz2_lab";

        /// <summary>
        /// 注入类标准绑定 ctor（Il2CppInterop 官方模式）。
        /// 不要调用 new LabMod()：代理基类 Mod(string) ctor 内部会
        /// il2cpp_object_new(原生 Mod)，创建出 vanilla Mod 原生对象
        /// （其 vtable 抽象槽 21/22 是抛 EntryPointNotFoundException 的 thunk）。
        /// </summary>
        public LabMod(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>
        /// 临时实例 ctor（仅用于 OnRegisterModInfos 的占位 Logic）。
        /// 游戏 LoadMods 会对每个 ModInfo 的 Logic 做 PreInit/AddMod 等
        /// 原生接口分派调用；注入类在 metadata v31 上接口分派可能崩溃，
        /// 因此占位实例故意用 vanilla 底（原生 Mod 对象），
        /// LoadMods 之后由 ModLogicFix 替换为真正的注入实例。
        /// </summary>
        public LabMod() : base(spaceName)
        {
        }

        /// <summary>
        /// 创建绑定注入类（klass=LabMod）的实例，并补做原生 Mod(string) ctor 的初始化。
        /// </summary>
        internal static LabMod CreateInstance()
        {
            // 1. 用注入类指针分配原生对象（IL2CPP 侧会跑注入 ctor 并创建托管包装）。
            var instance = new LabMod(ClassInjector.DerivedConstructorPointer<LabMod>());
            // 2. 初始化注入字段 + 把强 GCHandle 写进原生对象 InjectedClassData
            //    （trampoline 靠它找回托管实例）。
            ClassInjector.DerivedConstructorBody(instance);
            // 3. 原生 Mod(string nsp) ctor 未被调用，手动补做其初始化：
            //    Namespace 赋值 + definitionGroup / triggers 字段初始化。
            instance._Namespace_k__BackingField = spaceName;
            instance.definitionGroup = new DefinitionGroup();
            instance.triggers = new List<ITrigger>();
            return instance;
        }

        public override void OnInit(IGlobalGame game)
        {
            // 注意：绝不能调 base.OnInit(game)！
            // 代理虚调用经 vtable 槽 17 会回到本方法的 trampoline → 无限递归
            // 直到 native 栈溢出（进程无日志崩溃）。原生 Mod.OnInit 是空实现，跳过无影响。

            Plugin.Log?.LogInfo("LabMod.OnInit, registering stage mvz2:real_prologue");

            // 实验室关卡注册。冒烟目标：RealPrologue（纯 vanilla 行为组合，零自定义逻辑）。
            StageDefs.Clear();
            var stage = RealPrologueStage.CreateInstance();
            StageDefs.Add(stage);
            AddDefinition(stage);

            // TODO 后续移植：
            // AddDefinition(new ZeroKSpaceStage(VanillaNamespace, "zero_k_space"));
            // AddDefinition(new UnknowPalaceStage(VanillaNamespace, "unknow_palace"));
        }

        /// <summary>本 Mod 创建的关卡定义注册表（StageMetaApplier 用它匹配关卡 meta 并补属性）。</summary>
        internal static readonly System.Collections.Generic.List<PVZEngine.Level.StageDefinition> StageDefs
            = new System.Collections.Generic.List<PVZEngine.Level.StageDefinition>();

        public override ModSaveData CreateSaveData()
        {
            Plugin.Log?.LogInfo("[LabMod] CreateSaveData called");
            return new LogicSaveData(spaceName);
        }

        public override ModSaveData LoadSaveData(string json)
        {
            Plugin.Log?.LogInfo("[LabMod] LoadSaveData called");
            // 本 Mod 不存储任何自定义数据，读档时返回空存档即可。
            return new LogicSaveData(spaceName);
        }

        /// <summary>关卡定义使用的命名空间（与原版一致）。</summary>
        public const string VanillaNamespace = "mvz2";
    }
}
