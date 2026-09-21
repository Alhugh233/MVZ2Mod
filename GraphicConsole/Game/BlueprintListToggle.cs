using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>
    /// 在**原版选卡界面**上，紧挨着「取消选卡」（那个 ✕ 按钮）右边加一个
    /// 「全部蓝图 / 原版蓝图」切换按钮。
    ///
    /// 需求：「如果可以，在选卡UI上取消全部蓝图的X按钮右侧增加一个 全部蓝图/原版蓝图
    /// 按钮来切换显示范围。」
    ///
    /// 做法与仓库既定套路一致 —— **克隆现成节点**（UI prefab 在 Addressables bundle 里，
    /// mod 侧没有可靠的按名加载入口；从零 `new GameObject` 则要自己重建九宫格 sprite、
    /// TMP 字体材质等一堆引用）：
    ///   · 源 = <c>LevelUIBlueprintChoose.blueprintChoosePanel.cancelButton</c>（那个 ✕）；
    ///   · 位置 = 原按钮的 RectTransform 复制一份，x 再偏移「原宽 + 间距」；
    ///   · ⚠️ **必须 `onClick.RemoveAllListeners()`** —— <c>Instantiate</c> 会把原按钮
    ///     序列化的 <c>m_OnClick</c> 持久监听一起复制过来，不清掉的话点我们的按钮
    ///     会连带触发“取消选卡”。
    ///   · 音效/Deselector 等组件保留（与原按钮一致的手感）。
    ///
    /// 什么时候创建：没挂钩子，直接在心跳里轮询
    /// <c>LevelUIBlueprintChoose.blueprintChooseDisplaying</c> —— 面板显示过就建一次。
    /// 比猜“哪个方法一定会被调用”可靠，代价只是一帧一次布尔判断（还做了节流）。
    /// </summary>
    internal static class BlueprintListToggle
    {
        private const string NodeName = "GcAllBlueprintsButton";
        private const float Gap = 8f;

        private static GameObject root;
        private static TextMeshProUGUI label;
        private static int tick;

        /// <summary>每帧（节流）调用：选卡界面出现时保证按钮存在，并把文案同步成当前模式。</summary>
        public static void Tick()
        {
            if (++tick % 15 != 0)
                return;
            try
            {
                var panel = ChooseUI();
                if (panel == null)
                    return;
                if (UiFinder.IsAlive(root))
                {
                    SyncLabel();
                    return;
                }
                Build(panel);
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    Plugin.Log?.LogWarning("[Blueprints] 创建切换按钮失败：" + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        private static bool warned;

        /// <summary>选卡面板（未在显示 / 不在关卡里都返回 null）。</summary>
        private static MVZ2.Level.Blueprints.BlueprintChoosePanel ChooseUI()
        {
            var controller = LevelFacade.Controller;
            if (controller == null)
                return null;
            var part = controller.BlueprintChoosePart;
            if (part == null)
                return null;
            var ui = part.chooseUI;
            if (ui == null || !ui.blueprintChooseDisplaying)
                return null;
            return ui.blueprintChoosePanel;
        }

        private static void Build(MVZ2.Level.Blueprints.BlueprintChoosePanel panel)
        {
            var cancel = panel.cancelButton;
            if (cancel == null)
                return;
            var parent = cancel.transform.parent;
            if (parent == null)
                return;

            var go = UiFactory.Clone(cancel.gameObject, parent, NodeName);
            if (go == null)
                return;
            root = go;

            // 位置：复制原按钮的矩形，再往右挪一格。
            var src = cancel.transform.TryCast<RectTransform>();
            var dst = UiFactory.RectOf(go);
            if (src != null && dst != null)
            {
                dst.anchorMin = src.anchorMin;
                dst.anchorMax = src.anchorMax;
                dst.pivot = src.pivot;
                dst.sizeDelta = new Vector2(Mathf.Max(src.sizeDelta.x, 160f), src.sizeDelta.y);
                dst.anchoredPosition = src.anchoredPosition + new Vector2(src.rect.width + Gap, 0f);
            }
            // 紧跟在原按钮后面（父节点若有 LayoutGroup，就靠兄弟序号排队）。
            go.transform.SetSiblingIndex(cancel.transform.GetSiblingIndex() + 1);

            var button = go.GetComponent<Button>();
            if (button != null)
            {
                // ⚠️ 清掉从原按钮复制过来的持久监听（否则会连带触发取消选卡）。
                button.onClick.RemoveAllListeners();
                UiFactory.BindClick(button, OnClick);
                button.interactable = true;
            }

            label = UiFinder.FindChildComponent<TextMeshProUGUI>(go.transform);
            if (label != null)
                UiFactory.StyleLabel(label, GcLayout.SmallFontSize, false, TextAlignmentOptions.Center);
            SyncLabel();

            Plugin.Log?.LogInfo("[Blueprints] 已在选卡界面「取消」按钮右侧加了一个「"
                + (AllBlueprintsPatch.ShowAll ? "全部蓝图" : "原版蓝图") + "」切换按钮");
        }

        private static void OnClick()
        {
            AllBlueprintsPatch.ToggleShowAll();
            SyncLabel();
            // 重建过程会刷新面板元素，按钮自己不受影响（它是挂在面板外面那一层）。
            if (!UiFinder.IsAlive(root))
                Plugin.Log?.LogWarning("[Blueprints] 切换后按钮已失效（面板重建？），下次显示选卡界面会重新创建");
        }

        private static void SyncLabel()
        {
            if (label == null)
                return;
            UiFactory.SetLabelText(label, AllBlueprintsPatch.ShowAll ? "全部蓝图" : "原版蓝图");
        }

        /// <summary>离开关卡 / 换场景时复位。</summary>
        public static void Reset()
        {
            root = null;
            label = null;
        }

        public static string Describe()
        {
            return "选卡蓝图范围=" + (AllBlueprintsPatch.ShowAll ? "全部" : "原版")
                + " 按钮=" + (UiFinder.IsAlive(root) ? "有" : "无")
                + " 选卡清单=" + AllBlueprintsPatch.LastOfficialCount + "官方+"
                + (AllBlueprintsPatch.LastOrderedCount - AllBlueprintsPatch.LastOfficialCount) + "追加";
        }
    }
}
