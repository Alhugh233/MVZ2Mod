using System;
using DSHCore.Ui;
using HarmonyLib;

namespace UiGallery
{
    /// <summary>
    /// **诊断哨兵**：在 <c>ElementList.Awake</c> 真正跑之前看一眼它的模板还活着吗；
    /// 只有**不可用**时才输出。
    ///
    /// <para><b>为什么需要它：</b><c>ElementList.Awake</c> 第一件事就是
    /// <c>_template.transform.parent == _listRoot</c>，而 <c>_template</c> 一旦为空 / 已销毁，
    /// 就会在 <c>Instantiate</c> 到它的那一刻抛 <c>NullReferenceException</c>。
    /// <c>Player.log</c> 里只剩一行 <c>ElementList.Awake ← Instantiate</c> ——
    /// **调用方是 mod 代码时连调用者帧都不会出现**，完全看不出是谁的节点。
    /// 哨兵把「哪一个节点 / 模板是什么状态」在抛之前记下来，那行空指针就从"无解"变成"有名字"。</para>
    ///
    /// <para>实测就是这么定案的：设置界面的 <c>Scroll View/Viewport/Content</c> 上挂着一个
    /// <c>_template</c> 为空的 <c>ElementList</c>（原版的行模板是运行时才塞进去的），
    /// 我们的克隆体一被激活就触发它。修法见 <c>DSHCore.Ui.UiSkin</c> 的 <c>StripRowSpawners</c>。</para>
    ///
    /// <para>只在 <see cref="GalleryConfig.SelfTest"/> 打开时挂；正常玩不挂、不刷日志。</para>
    /// </summary>
    internal static class ElementListWatchdog
    {
        private const string HarmonyId = "qoznos.mvz2.uigallery.elwatch";

        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            try
            {
                var target = AccessTools.Method(typeof(PVZEngine.Tools.Unity.ElementList), "Awake");
                if (target == null)
                {
                    Plugin.Log?.LogWarning("[Watchdog] 找不到 ElementList.Awake，哨兵不可用。");
                    return;
                }

                new Harmony(HarmonyId).Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(ElementListWatchdog), nameof(AwakePrefix))));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Watchdog] 挂哨兵失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void AwakePrefix(PVZEngine.Tools.Unity.ElementList __instance)
        {
            try
            {
                if (__instance == null)
                {
                    Plugin.Log?.LogError("[Watchdog] ElementList.Awake：实例本身为空 —— 马上要抛空指针了。");
                    return;
                }

                if (UiFinder.IsAlive(__instance._template))
                    return;

                // 只报"要坏事"的那一种情况。
                var sb = new System.Text.StringBuilder(220);
                sb.Append("[Watchdog] ElementList.Awake 的模板不可用（马上会抛 NullReferenceException）：节点=");
                try { sb.Append(UiFinder.PathOf(__instance.transform)); }
                catch (Exception e) { sb.Append("<取路径失败 ").Append(e.GetType().Name).Append('>'); }
                sb.Append("  模板=");
                sb.Append(__instance._template == null ? "null" : "已销毁");

                try
                {
                    var root = __instance._listRoot;
                    sb.Append("  _listRoot=").Append(UiFinder.IsAlive(root)
                        ? UiFinder.PathOf(root) : "不可用");
                }
                catch (Exception) { }

                Plugin.Log?.LogError(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Watchdog] 自身失败：" + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
