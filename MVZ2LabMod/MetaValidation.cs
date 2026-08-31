using System;
using System.IO;
using System.Text;
using HarmonyLib;
using MVZ2.Managers;
using MVZ2.Metas;
using MVZ2.Modding;
using PVZEngine;
using UnityEngine;

namespace MVZ2LabMod
{
    /// <summary>
    /// XML 载入链路（Harmony 接管方案）与验证补丁：
    /// 1. ResourceManager.LoadMetaLists prefix —— 对本 Mod 直接读磁盘 XML，
    ///    调 MetaXMLParser.LoadMetaList 解析（完全绕过 Addressables Provider，
    ///    规避 ClassInjector 接口分派失败的坑）。
    /// 2. MetaXMLParser.LoadMetaList postfix —— 记录解析结果（验证）。
    ///
    /// 期望日志序列（验证通过的标准）：
    ///   [MetaValidation] Injected meta "stages" (... chars)
    ///   [MetaValidation] StageMetaList(3): real_prologue(minigame) unknow_palace(minigame) zero_k_space(minigame)
    /// </summary>
    internal static class MetaValidation
    {
        public static void Apply()
        {
            var harmony = new Harmony("qoznos.mvz2.labmod.metavalidation");
            harmony.Patch(
                AccessTools.Method(typeof(ResourceManager), "LoadMetaLists"),
                prefix: new HarmonyMethod(typeof(MetaValidation), nameof(LoadMetaListsPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(MetaXMLParser), "LoadMetaList"),
                postfix: new HarmonyMethod(typeof(MetaValidation), nameof(AfterLoadMetaList)));
            Plugin.Log?.LogInfo("Meta validation patches applied");
        }

        /// <summary>
        /// 接管 mvz2_lab 的 meta 列表加载：直接读 StreamingAssets/Mods/mvz2_lab/Content/metas/*.xml，
        /// 用游戏的 MetaXMLParser 解析。async 方法用 prefix + __result 整体替换。
        /// </summary>
        [HarmonyPrefix]
        private static bool LoadMetaListsPrefix(ResourceManager __instance, string modNamespace,
            ref Il2CppSystem.Threading.Tasks.Task __result)
        {
            if (modNamespace != LabMod.spaceName)
                return true; // 其他 Mod 走原逻辑
            try
            {
                var modResource = __instance.GetModResource(modNamespace);
                if (modResource == null)
                {
                    Plugin.Log?.LogWarning("[MetaValidation] GetModResource(mvz2_lab) 返回 null");
                    __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                    return false;
                }
                var metasDir = Path.Combine(Application.streamingAssetsPath, "Mods",
                    LabMod.spaceName, "Content", "metas");
                if (!Directory.Exists(metasDir))
                {
                    Plugin.Log?.LogWarning("[MetaValidation] metas dir not found: " + metasDir);
                    __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                    return false;
                }
                foreach (var file in Directory.GetFiles(metasDir, "*.xml"))
                {
                    var keyName = Path.GetFileNameWithoutExtension(file);
                    var text = File.ReadAllText(file);
                    var doc = new Il2CppSystem.Xml.XmlDocument();
                    doc.LoadXml(text);
                    MetaXMLParser.LoadMetaList(modResource, keyName, doc, "mvz2");
                    Plugin.Log?.LogInfo("[MetaValidation] Injected meta \"" + keyName + "\" (" + text.Length + " chars)");
                }
                __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[MetaValidation] LoadMetaLists replacement failed: " + e);
                __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                return false;
            }
        }

        [HarmonyPostfix]
        private static void AfterLoadMetaList(ModResource resource, string metaPath)
        {
            if (resource == null || resource.Namespace != LabMod.spaceName)
                return; // 只关心我们自己的 Mod

            if (metaPath == "stages")
            {
                var list = resource.StageMetaList;
                if (list == null || list.metas == null || list.metas.Length == 0)
                {
                    Plugin.Log?.LogWarning("[MetaValidation] StageMetaList 为空！");
                    return;
                }
                var sb = new StringBuilder();
                for (int i = 0; i < list.metas.Length; i++)
                {
                    var meta = list.metas[i];
                    if (meta != null)
                        sb.Append(meta.ID).Append('(').Append(meta.Type).Append(") ");
                }
                Plugin.Log?.LogInfo("[MetaValidation] StageMetaList(" + list.metas.Length + "): " + sb);
            }
            else if (metaPath == "arcade")
            {
                var list = resource.ArcadeMetaList;
                Plugin.Log?.LogInfo("[MetaValidation] ArcadeMetaList: "
                    + (list == null || list.metas == null ? "null" : list.metas.Length + " metas"));
            }
            else
            {
                Plugin.Log?.LogInfo("[MetaValidation] meta loaded: " + metaPath);
            }
        }
    }
}
