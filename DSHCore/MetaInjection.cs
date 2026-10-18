using System;
using System.IO;
using HarmonyLib;
using MVZ2.IO;
using MVZ2.Managers;
using MVZ2.Metas;
using MVZ2.Modding;
using UnityEngine;

namespace DSHCore
{
    /// <summary>
    /// 外部内容 mod 的 XML metas 磁盘注入（按 CoreRegistry 注册表泛化）。
    ///
    /// 作用：在 ResourceManager.LoadMetaLists 为已注册的外部 mod 命名空间
    /// （如 mvz2_lab）加载 meta 时，直接从磁盘
    /// StreamingAssets/Mods/&lt;nsp&gt;/Content/metas/*.xml 读取并用游戏的
    /// MetaXMLParser 解析进 ModResource —— 完全绕过 Addressables Provider，
    /// 规避 ClassInjector 接口分派 / provider 链路在 IL2CPP 下的坑。
    ///
    /// 0.7 官方链（ModLoader.LoadStages / LoadStageProperties / ArcadeController）会读取
    /// ModResource.StageMetaList / ArcadeMetaList 完成关卡注册与属性应用。
    ///
    /// <para><b>为什么要挂多个入口。</b>metas 不只是"塞进 ModResource"就完事：
    /// <c>ResourceManager.LoadModResourcesInit</c> 读完 meta 后还会把
    /// ArmorMetaList / AchievementMetaList / NoteMetaList / TalkMetas / ArcadeMetaList
    /// 逐个搬进 <c>armorSlotsCacheDict</c> / <c>arcadeCache</c> 等私有缓存，
    /// 而 <c>arcadeCache</c> 正是 <c>GetAllArcadeItems</c> 的数据源。
    /// 注入**晚于** LoadModResourcesInit，就会表现为"stage metas 有了，街机条目还是 0"。
    ///
    /// 而官方那条 <c>LoadMetaLists</c> 是私有 async 薄壳（真正的工作在
    /// <c>&lt;LoadMetaLists&gt;d__11.MoveNext</c>），release 构建下 IL2CPP 可能把它整个内联掉 ——
    /// 此时 Harmony 补丁装得上、却**永远不被调用**（0.7.0 实测：该补丁零日志）。
    /// 所以这里挂多个入口，谁先到算谁的（幂等）：
    ///   1. <c>LoadModResourcesInit</c> prefix —— 最优，正好在官方建缓存之前；
    ///   2. <c>LoadMetaLists</c> prefix —— 原入口，早期内测版上走的就是它；
    ///   3. <c>GetModResource(string)</c> prefix —— 兜底，官方取 ModResource 时顺手补；
    ///   4. <c>ModManager.LoadMods</c> prefix（<see cref="LoadModsPatch"/>）—— 最后兜底。</para>
    /// </summary>
    internal static class MetaInjection
    {
        private static bool applied;

        /// <summary>
        /// 命名空间 → 已经注入过的 <c>ModResource</c> 指针（幂等标记）。
        ///
        /// <para>用指针而不是"命名空间出现过"当标记：<c>ResourceManager.Init()</c> 每次都会
        /// <c>ClearResources()</c> 再重建 <c>ModResource</c>，那时我们必须**重新注入**，
        /// 否则内容会静默消失（关卡列表空、街机空）。指针能同时区分「同一个对象」与「重建后的新对象」。</para>
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, IntPtr> doneNs
            = new System.Collections.Generic.Dictionary<string, IntPtr>(StringComparer.Ordinal);

        /// <summary>入口命中日志去重（命名空间 + 入口各一行）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> loggedNs
            = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>已警告过"metas 目录不存在"的命名空间，避免刷屏。</summary>
        private static readonly System.Collections.Generic.HashSet<string> warnedDirNs
            = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static readonly object gate = new object();

        /// <summary>注入过程会回调游戏方法，可能重入本类，用它掐断。</summary>
        private static bool injecting;

        private static bool warnedMissingResource;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            var harmony = new Harmony("qoznos.mvz2.core.metainjection");

            // 主入口（早期内测版上生效的就是它）。
            var mLoadMetaLists = AccessTools.Method(typeof(ResourceManager), "LoadMetaLists");
            if (mLoadMetaLists != null)
            {
                harmony.Patch(mLoadMetaLists,
                    prefix: new HarmonyMethod(typeof(MetaInjection), nameof(LoadMetaListsPrefix)));
            }

            // 备入口 1：release 构建下 LoadMetaLists 这个私有 async 薄壳可能被 IL2CPP
            // 整个内联（补丁装得上、却永远不被调用），这个调用点更"实"。
            var mLoadInit = AccessTools.Method(typeof(ResourceManager), "LoadModResourcesInit",
                new[] { typeof(string), typeof(ModResource) });
            if (mLoadInit != null)
            {
                harmony.Patch(mLoadInit,
                    prefix: new HarmonyMethod(typeof(MetaInjection), nameof(LoadModResourcesInitPrefix)));
            }

            // 备入口 2：取 ModResource 时顺手补（前两个都没命中时的兜底）。
            var mGetModResource = AccessTools.Method(typeof(ResourceManager), "GetModResource",
                new[] { typeof(string) });
            if (mGetModResource != null)
            {
                harmony.Patch(mGetModResource,
                    prefix: new HarmonyMethod(typeof(MetaInjection), nameof(GetModResourcePrefix)));
            }

            Plugin.Log?.LogInfo("MetaInjection patches applied（LoadMetaLists="
                + (mLoadMetaLists != null) + " LoadModResourcesInit=" + (mLoadInit != null)
                + " GetModResource=" + (mGetModResource != null) + "）");
        }

        /// <summary>本框架最后兜底入口（<see cref="LoadModsPatch"/> 的 prefix 调用）。</summary>
        public static void InjectAllRegistered()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return;
                foreach (var reg in CoreRegistry.GetAll())
                    InjectIfRegistered(main.ResourceManager, reg.Namespace, null, "LoadMods");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[MetaInjection] InjectAllRegistered error: " + e);
            }
        }

        // ------------------------------------------------------------------
        // 补丁体
        // ------------------------------------------------------------------

        [HarmonyPrefix]
        private static void LoadModResourcesInitPrefix(ResourceManager __instance,
            string modNamespace, ModResource modResource)
        {
            InjectIfRegistered(__instance, modNamespace, modResource, "LoadModResourcesInit");
        }

        [HarmonyPrefix]
        private static void GetModResourcePrefix(ResourceManager __instance, string spaceName)
        {
            InjectIfRegistered(__instance, spaceName, null, "GetModResource");
        }

        /// <summary>
        /// 接管已注册外部 mod 命名空间的 meta 列表加载：
        /// 直接读磁盘 XML，用游戏的 MetaXMLParser 解析。
        /// async 方法用 prefix + __result 整体替换。
        /// </summary>
        [HarmonyPrefix]
        private static bool LoadMetaListsPrefix(ResourceManager __instance, string modNamespace,
            ref Il2CppSystem.Threading.Tasks.Task __result)
        {
            var reg = CoreRegistry.Get(modNamespace);
            if (reg == null)
                return true; // 非本框架管理的 mod，走原逻辑

            InjectIfRegistered(__instance, modNamespace, null, "LoadMetaLists");
            // 本框架的 mod 走空 ResourceLocator，原逻辑必然读不到东西，直接短路。
            __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
            return false;
        }

        // ------------------------------------------------------------------
        // 注入
        // ------------------------------------------------------------------

        private static void InjectIfRegistered(ResourceManager rm, string modNamespace,
            ModResource modResource, string source)
        {
            if (rm == null || string.IsNullOrEmpty(modNamespace))
                return;
            var reg = CoreRegistry.Get(modNamespace);
            if (reg == null)
                return; // 不是本框架的命名空间
            lock (gate)
            {
                if (injecting)
                    return;
                injecting = true;
            }
            try
            {
                LogHit(modNamespace, source);
                if (modResource == null)
                    modResource = rm.GetModResource(modNamespace);
                if (modResource == null)
                {
                    if (!warnedMissingResource)
                    {
                        warnedMissingResource = true;
                        Plugin.Log?.LogWarning("[MetaInjection] 入口 " + source + "：" + modNamespace
                            + " 的 ModResource 还不存在，本次跳过（后续入口会再试）");
                    }
                    return;
                }

                // 幂等：同一个 ModResource 只注入一次（重建后会拿到新指针，那时必须重注）。
                IntPtr ptr;
                try { ptr = modResource.Pointer; }
                catch (Exception) { return; }
                IntPtr donePtr;
                if (doneNs.TryGetValue(modNamespace, out donePtr) && donePtr == ptr)
                    return;

                Inject(modNamespace, reg.XmlDefaultNamespace, modResource, source);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[MetaInjection] 注入 " + modNamespace
                    + "（入口 " + source + "）失败：" + e);
            }
            finally
            {
                lock (gate) injecting = false;
            }
        }

        private static void Inject(string modNamespace, string xmlDefaultNamespace,
            ModResource modResource, string source)
        {
            var defaultNsp = string.IsNullOrEmpty(xmlDefaultNamespace) ? "mvz2" : xmlDefaultNamespace;
            var metasDir = Path.Combine(Application.streamingAssetsPath, "Mods",
                modNamespace, "Content", "metas");
            if (!Directory.Exists(metasDir))
            {
                lock (gate)
                {
                    if (!warnedDirNs.Add(modNamespace))
                        return;
                }
                Plugin.Log?.LogWarning("[MetaInjection] metas dir not found: " + metasDir);
                return;
            }

            var files = Directory.GetFiles(metasDir, "*.xml");
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var keyName = Path.GetFileNameWithoutExtension(file);
                var text = File.ReadAllText(file);
                // 直接用 XmlDocument.LoadXml 解析。
                // 注意：不能调用 MVZ2.IO.XMLHelper.ReadXmlDocument(string) —— 官方
                // string 重载内部 StreamWriter 未 Flush 就读流，实际会抛
                // "Root element is missing"（官方只走 Stream/Addressables 路径）。
                // 约定：metas/*.xml 内容不要写 XML 注释（Comment 节点会进入
                // BuffMetaList.FromXmlNode 等子节点遍历，触发 NRE —— 与官方
                // XmlReaderSettings.IgnoreComments 行为不同）。
                var doc = new Il2CppSystem.Xml.XmlDocument();
                doc.LoadXml(text);
                MetaXMLParser.LoadMetaList(modResource, keyName, doc, defaultNsp);
                Plugin.Log?.LogInfo("[MetaInjection] Injected meta \"" + keyName + "\" ("
                    + text.Length + " chars) for " + modNamespace);
            }

            IntPtr ptr;
            try { ptr = modResource.Pointer; }
            catch (Exception) { return; }
            lock (gate) doneNs[modNamespace] = ptr;
            Plugin.Log?.LogInfo("[MetaInjection] " + modNamespace + " 注入完成："
                + files.Length + " 个 meta 文件（入口 " + source + "）");
        }

        /// <summary>入口留痕：补丁体到底有没有被调到，是"metas 没注入"这类症状的第一分叉点。</summary>
        private static void LogHit(string modNamespace, string source)
        {
            try
            {
                lock (gate)
                {
                    if (!loggedNs.Add(modNamespace + "@" + source))
                        return;
                }
                Plugin.Log?.LogInfo("[MetaInjection] " + source + " 命中 \"" + modNamespace
                    + "\"  已注册=" + (CoreRegistry.Get(modNamespace) != null));
            }
            catch (Exception) { }
        }
    }
}
