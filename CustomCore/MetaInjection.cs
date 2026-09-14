using System;
using System.IO;
using HarmonyLib;
using MVZ2.IO;
using MVZ2.Managers;
using MVZ2.Metas;
using MVZ2.Modding;
using UnityEngine;

namespace CustomCore
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
    /// </summary>
    internal static class MetaInjection
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.core.metainjection");
            harmony.Patch(
                AccessTools.Method(typeof(ResourceManager), "LoadMetaLists"),
                prefix: new HarmonyMethod(typeof(MetaInjection), nameof(LoadMetaListsPrefix)));
            Plugin.Log?.LogInfo("MetaInjection patches applied");
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
            try
            {
                var modResource = __instance.GetModResource(modNamespace);
                if (modResource == null)
                {
                    Plugin.Log?.LogWarning("[MetaInjection] GetModResource(" + modNamespace + ") 返回 null");
                    __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                    return false;
                }
                var defaultNsp = string.IsNullOrEmpty(reg.XmlDefaultNamespace)
                    ? "mvz2" : reg.XmlDefaultNamespace;
                var metasDir = Path.Combine(Application.streamingAssetsPath, "Mods",
                    modNamespace, "Content", "metas");
                if (!Directory.Exists(metasDir))
                {
                    Plugin.Log?.LogWarning("[MetaInjection] metas dir not found: " + metasDir);
                    __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                    return false;
                }
                foreach (var file in Directory.GetFiles(metasDir, "*.xml"))
                {
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
                __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[MetaInjection] LoadMetaLists replacement failed for "
                    + modNamespace + ": " + e);
                __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                return false;
            }
        }
    }
}
