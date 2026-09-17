using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.GameContent.Buffs.Level;
using MVZ2Logic.Entities;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Entities;
using PVZEngine.Level;

namespace ModularCurse
{
    /// <summary>
    /// 所有模块化诅咒 buff 的基类（关卡级 buff）。
    ///
    /// 继承原版 <see cref="CurseBuffDefinition"/>，因此自动带上
    /// <c>CURSE_COUNT += 1</c> 修饰符 —— 与原版 7 条诅咒完全同构。
    ///
    /// 生命周期（由 <see cref="CurseBuffs"/> 工厂驱动）：
    ///   1. ClassInjector 注册注入类型；
    ///   2. <see cref="Build{T}"/> 用 DerivedConstructorPointer/Body 建实例
    ///      （不跑原生 ctor），手工补上 Definition/BuffDefinition 的字段初值；
    ///   3. 调用 <see cref="Setup"/> 展开本诅咒的修饰符与触发器。
    ///
    /// 注意：本 mod 的程序集不参与官方的 PropertyMapper 扫描
    /// （ModInfo.Assemblies 为空），因此**不要**声明自定义 PropertyMeta ——
    /// 未注册的属性键 Key==0，所有未注册键会互相碰撞。
    /// 跨帧状态统一放在托管静态字段里（见 <see cref="CurseState"/>）。
    /// </summary>
    public class CurseBuffBase : CurseBuffDefinition
    {
        public CurseBuffBase(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>池内 ID（如 qc_c1），与 curses.xml / buffs.xml 的 id 一致。</summary>
        public virtual string CurseId => string.Empty;

        /// <summary>是否占用诅咒位（关卡 CURSE_COUNT +1）。子 buff 设为 false。</summary>
        public virtual bool CountsAsCurse => true;

        /// <summary>本诅咒的游戏内命名空间 ID。</summary>
        public NamespaceID CurseID => CurseIds.Get(CurseId);

        /// <summary>展开定义内容（等价于原生 ctor 体）。由工厂在建好字段后调用。</summary>
        internal virtual void Setup()
        {
        }

        /// <summary>创建注入实例（字段初值 → Setup）。</summary>
        internal static T Build<T>() where T : CurseBuffBase
        {
            return CurseBuffs.Construct<T>();
        }

        private static readonly Random rng = new Random();

        /// <summary>掷一次百分比判定。</summary>
        internal static bool Roll(int percent)
        {
            return rng.Next(100) < percent;
        }

        /// <summary>ENEMY 与 BOSS 都算"怪物"。</summary>
        internal static bool IsEnemyType(int type)
        {
            return type == EntityTypes.ENEMY || type == EntityTypes.BOSS;
        }

        /// <summary>
        /// 该关卡当前是否挂着这条诅咒。
        ///
        /// 注意：Il2CppInterop 生成的代理**没有**复制接口声明
        /// （LevelEngine 实际实现了 IBuffTarget，代理上却看不到），
        /// 因此无法直接对 LevelEngine/Entity 调 IBuffTarget 的扩展方法，
        /// 必须先用指针包一个 IBuffTarget。
        /// </summary>
        internal static bool Active(LevelEngine level, NamespaceID id)
        {
            if (level == null || !NamespaceID.IsValid(id))
                return false;
            try
            {
                return new IBuffTarget(level.Pointer).Buffs.HasBuff(id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>某实体所属关卡是否挂着这条诅咒。</summary>
        internal static bool ActiveOn(Entity entity, NamespaceID id)
        {
            return entity != null && Active(entity.Level, id);
        }

        /// <summary>本定义是否是"当前关卡生效中"的那一份（原版诅咒 buff 的写法）。</summary>
        internal bool ActiveOn(Entity entity)
        {
            if (entity == null || entity.Level == null)
                return false;
            try
            {
                return new IBuffTarget(entity.Level.Pointer).Buffs.HasBuff(this);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>给实体挂 buff（同上：代理看不到 IBuffTarget）。</summary>
        internal static Buff AddBuff(Entity entity, BuffDefinition definition)
        {
            if (entity == null || definition == null)
                return null;
            return new IBuffTarget(entity.Pointer).AddBuff(definition);
        }

        /// <summary>移除实体身上的某个 buff。</summary>
        internal static void RemoveBuffsOf(Entity entity, NamespaceID id)
        {
            if (entity == null || !NamespaceID.IsValid(id))
                return;
            new IBuffTarget(entity.Pointer).RemoveBuffs(id);
        }

        private static Il2CppSystem.Func<Entity, bool> alivePredicate;

        /// <summary>
        /// 取关卡内全部存活实体。
        /// 需要缓存 Il2CppSystem.Func 委托（托管 lambda 不能隐式转换）。
        /// </summary>
        internal static Il2CppReferenceArray<Entity> FindAll(LevelEngine level)
        {
            if (level == null)
                return null;
            if (alivePredicate == null)
            {
                Func<Entity, bool> managed = e => e != null && !e.IsDead;
                alivePredicate = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Func<Entity, bool>>(managed);
            }
            return level.FindEntities(alivePredicate);
        }

        /// <summary>按 native 指针在关卡里找回实体。</summary>
        internal static Entity FindByPointer(LevelEngine level, IntPtr ptr)
        {
            var all = FindAll(level);
            if (all == null)
                return null;
            for (var i = 0; i < all.Length; i++)
            {
                var entity = all[i];
                if (entity != null && entity.Pointer == ptr)
                    return entity;
            }
            return null;
        }
    }
}
