using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>页集合：关卡内 / 关卡外各一套（需求里就是两份面板）。</summary>
    internal static class PanelPages
    {
        private static readonly IPanelPage[] InLevelPages =
        {
            new LevelBasicPage(),
            new BlueprintPage(),
            new ArtifactPage(),
        };
        private static readonly IPanelPage[] ConveyorPages =
        {
            new LevelBasicPage(),
            new ConveyorPage(),
            new ArtifactPage(),
        };
        private static readonly IPanelPage[] MenuPages = { new ProgressPage() };

        public static IPanelPage[] For(bool inLevel, bool conveyor)
        {
            if (!inLevel)
                return MenuPages;
            return conveyor ? ConveyorPages : InLevelPages;
        }

        public static string Report()
        {
            var sb = new System.Text.StringBuilder(200);
            sb.Append("关卡内(自选卡)：");
            for (var i = 0; i < InLevelPages.Length; i++)
            {
                if (i > 0)
                    sb.Append(" / ");
                sb.Append(InLevelPages[i].Name);
            }
            sb.Append('\n').Append("关卡内(传送带)：");
            for (var i = 0; i < ConveyorPages.Length; i++)
            {
                if (i > 0)
                    sb.Append(" / ");
                sb.Append(ConveyorPages[i].Name);
            }
            sb.Append('\n').Append("关卡外：");
            for (var i = 0; i < MenuPages.Length; i++)
            {
                if (i > 0)
                    sb.Append(" / ");
                sb.Append(MenuPages[i].Name);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 关卡外 · 页1「进度」：左列 = 绿宝石 + 三个批量操作按钮；
    /// 右列 = **搜索框 + 可多选的条目列表** + 「解锁 / 锁定」。
    ///
    /// <para><b>列表与搜索框都是克隆对话档案（Archive）索引页左侧那一块</b>
    /// （见 <see cref="UiArchiveList"/> / <see cref="UiSearchBox"/>）：外观、滚动、多选交互
    /// 全部是原版素材与行为。换掉早期「输入框 + 添加/删除」形态的理由：那一形态一次只能动一条，
    /// 而进度页真正要做的是"挑一批一起解锁 / 收回"。</para>
    ///
    /// <para><b>条目 = 全部解锁条件的定义侧</b>（实测 155 条），**不是**"已解锁的那些" ——
    /// 行首的 <c>[x]</c> / <c>[ ]</c> 才是解锁状态，列表必须同时包含两类，否则"锁定"无从下手。</para>
    ///
    /// <para><b>不可撤销</b>：这些操作直接写用户存档。原版没有「按成就解锁」的入口
    /// （成就是条件驱动的），所以「解锁全部成就」实际做的是解锁全部已注册的解锁条件。</para>
    /// </summary>
    internal sealed class ProgressPage : IPanelPage
    {
        public string Name { get { return "进度"; } }

        private UiField _money;
        private UiSearchBox _search;
        private UiArchiveList _list;
        private UiOptionRow _unlock;
        private UiOptionRow _lock;
        private bool _built;
        private int _sourceCount;
        private int _lastApplyMs;

        /// <summary>
        /// 最近一次从**定义侧**拿到的解锁条件数 —— 给自测用的（资源在标题界面要过一会儿才加载完，
        /// 自测要等它非空再 dump，否则读到的永远是空列表）。
        /// </summary>
        public static int SourceCount { get; private set; }

        // ------------------------------------------------------------------
        // 装配
        // ------------------------------------------------------------------

        public void Build()
        {
            _money = null;
            _search = null;
            _list = null;
            _unlock = _lock = null;
            _built = false;
            _sourceCount = 0;

            var columns = UiGridRow.Create("Row_Progress");
            if (columns == null)
                return;

            BuildLeft(columns);
            BuildRight(columns);

            LoadoutPanel.AddRow(columns.Finish());
            RefreshSource();
            UpdateButtons();
            _built = true;
            Plugin.Log?.LogInfo("[ProgressPage] 已装配：" + Describe());
        }

        private void BuildLeft(UiGridRow columns)
        {
            var left = columns.AddContainer("Col_Left", true);
            if (!UiFinder.IsAlive(left))
                return;

            _money = UiField.Create("F_Money", "绿宝石", numeric: true, integer: true);
            if (_money != null)
            {
                _money.Root.transform.SetParent(left.transform, false);
                UiContainer.FitCell(_money.Root, 0f, UiKit.RowHeight);
                _money.Text = ProgressOps.GetMoney().ToString();
                _money.OnSubmit(OnMoneySubmitted);
                LoadoutPanel.AddTooltip(_money.Root, "绿宝石",
                    "直接改写用户存档里的绿宝石数量。会立刻写入存档，不可撤销。");
            }

            AddLeftButton(left, "解锁全部关卡", "把所有关卡的解锁条件解锁。\n"
                + "（批内不逐条重算，只做一次官方重算 + 一次落盘。）",
                () => ProgressOps.UnlockAllLevels());
            AddLeftButton(left, "解锁全部成就", "原版成就是**条件驱动**的（AchievementMeta.Unlock 是一串条件），\n"
                + "没有「按成就解锁」的入口，所以这里做的是：解锁全部已注册的解锁条件（含关卡与商店）。",
                () => ProgressOps.UnlockAllAchievements());
            AddLeftButton(left, "移除商店已购买项", "遍历原版商店产物表，按 ProductMeta.Stages[].Unlocks\n"
                + "把已购项**反解锁**（收回购买记录）。",
                () => ProgressOps.RemovePurchasedStoreItems());
        }

        private void AddLeftButton(GameObject column, string label, string tooltip, Func<int> action)
        {
            var row = UiOptionRow.Create(column.transform, "B_" + label, UiOptionKind.Button);
            if (row == null)
                return;
            UiContainer.FitCell(row.Root, 0f, UiKit.RowHeight);
            UiKit.FullWidth(row.Root);
            row.SetButton(label, () =>
            {
                try
                {
                    var t0 = Time.realtimeSinceStartup;
                    var n = action();
                    _lastApplyMs = Mathf.RoundToInt((Time.realtimeSinceStartup - t0) * 1000f);
                    Plugin.Log?.LogInfo("[ProgressPage] " + label + " → 影响 " + n
                        + " 项，耗时 " + _lastApplyMs + " ms");
                    // 只重算行文本（不重建行）—— 155 条重建一遍会白卡一下。
                    if (_list != null)
                        _list.RefreshLabels();
                }
                catch (Exception e)
                {
                    Plugin.LogOnce("progress-op", "[ProgressPage] " + label + " 失败："
                        + e.GetType().Name + ": " + e.Message);
                }
            });
            row.SetTooltip(label, tooltip);
        }

        private void BuildRight(UiGridRow columns)
        {
            var right = columns.AddContainer("Col_Right", true);
            if (!UiFinder.IsAlive(right))
                return;

            // 搜索框（克隆档案页的那一个；带自己的底图与占位文本）——
            // 前面带一个「搜索: 」标签：单独一行，标签窄、输入框宽，所以那一行**不做等分**。
            var searchRow = UiGridRow.Create("Row_Search", equalize: false);
            if (searchRow != null)
            {
                var label = UiKit.Label(searchRow.Root.transform, "Label", "搜索:");
                if (label != null)
                {
                    // 标签自己就是“只占文字宽”（UiKit.Label 已经把 min/preferred 钉成文字宽），
                    // 高度与同行控件对齐。
                    var le = label.gameObject.GetComponent<LayoutElement>();
                    if (le == null)
                        le = label.gameObject.AddComponent<LayoutElement>();
                    if (le != null)
                        le.preferredHeight = UiKit.RowHeight;
                }
                _search = UiSearchBox.Create(searchRow.Root.transform, "Search", OnSearchChanged);
                var searchNode = searchRow.Finish();
                searchNode.transform.SetParent(right.transform, false);
            }
            if (_search != null)
            {
                LoadoutPanel.AddTooltip(_search.Root, "搜索",
                    "按关键字过滤下面的条目。行文本形如 `[x] mvz2:xxx`：\n"
                    + "敲 `[x]` 只看**已解锁**，敲 `[ ]` 只看**未解锁**；也可以直接敲 id 片段。");
            }

            // 可多选的条目列表（克隆档案页左侧那个列表）。
            _list = UiArchiveList.Create(right.transform, "UnlockList");
            if (_list != null)
                _list.OnSelectionChanged(UpdateButtons);

            // 两个按钮并排：这一行也**不做等分** —— 两个单元格都是 preferred 0 + flexible 1，
            // 布局组自己就会平分（而强制等分会按面板宽度算，宽度超过这一列 → 锁定按钮跑到框外）。
            var buttons = UiGridRow.Create("Row_UnlockButtons", equalize: false);
            if (buttons != null)
            {
                _unlock = buttons.AddOptionRow("B_Unlock", UiOptionKind.Button);
                if (_unlock != null)
                {
                    _unlock.SetButton("解锁", () => ApplyToChecked(true));
                    _unlock.SetTooltip("解锁", "把列表里**勾选**的条目全部解锁。");
                }
                _lock = buttons.AddOptionRow("B_Lock", UiOptionKind.Button);
                if (_lock != null)
                {
                    _lock.SetButton("锁定", () => ApplyToChecked(false));
                    _lock.SetTooltip("锁定", "把列表里**勾选**的条目全部取消解锁（收回）。");
                }
                var node = buttons.Finish();
                node.transform.SetParent(right.transform, false);
            }

            if (_search == null && _list == null)
            {
                Plugin.LogOnce("progress-none",
                    "[ProgressPage] 档案页的搜索框与列表都拿不到 —— 进度页只剩左侧的批量操作。");
            }
        }

        // ------------------------------------------------------------------
        // 数据
        // ------------------------------------------------------------------

        /// <summary>
        /// 用**定义侧**的全部解锁条件填列表（实测 155 条）。
        /// 资源侧在标题界面可能还没加载完（拿到 0 条）→ 返回是否拿到了非空集合。
        /// </summary>
        private bool RefreshSource()
        {
            var ids = ProgressOps.GetAllUnlockConditions();
            _sourceCount = ids == null ? 0 : ids.Count;
            SourceCount = _sourceCount;
            if (_list != null && _sourceCount > 0)
                _list.SetSource(ids);
            return _sourceCount > 0;
        }

        private void OnSearchChanged(string text)
        {
            try
            {
                if (_list != null)
                    _list.SetFilter(text);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("progress-search", "[ProgressPage] 过滤失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 把勾选的条目批量解锁 / 锁定。
        ///
        /// <para>必须包在 <see cref="ProgressOps.BeginBatch"/> 里：官方的 <c>Unlock/Relock</c>
        /// 每次都会做一次**全量重算**，逐条调就会卡死（这是那几个按钮卡顿的根因）。</para>
        /// </summary>
        private void ApplyToChecked(bool unlock)
        {
            if (_list == null)
                return;
            List<UiArchiveList.Row> rows;
            try
            {
                rows = _list.CheckedRows();
            }
            catch (Exception)
            {
                return;
            }
            if (rows.Count == 0)
            {
                Plugin.Log?.LogWarning("[ProgressPage] 没有勾选任何条目（勾选后才能解锁 / 锁定）");
                return;
            }

            var t0 = Time.realtimeSinceStartup;
            var done = 0;
            ProgressOps.BeginBatch();
            try
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    if (unlock)
                        ProgressOps.Unlock(rows[i].Id);
                    else
                        ProgressOps.Relock(rows[i].Id);
                    done++;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("progress-apply", "[ProgressPage] 应用失败：" + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                ProgressOps.EndBatch();
            }

            _lastApplyMs = Mathf.RoundToInt((Time.realtimeSinceStartup - t0) * 1000f);
            _list.RefreshLabels();
            UpdateButtons();
            Plugin.Log?.LogInfo("[ProgressPage] " + (unlock ? "解锁" : "锁定") + " " + done
                + " 条，耗时 " + _lastApplyMs + " ms");
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        private void OnMoneySubmitted(string text)
        {
            int value;
            if (!int.TryParse((text ?? string.Empty).Trim(), out value))
            {
                if (_money != null)
                    _money.Text = ProgressOps.GetMoney().ToString();
                return;
            }
            try
            {
                ProgressOps.SetMoney(value);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("progress-money", "[ProgressPage] 写绿宝石失败：" + e.GetType().Name);
            }
            if (_money != null)
                _money.Text = ProgressOps.GetMoney().ToString();
        }

        /// <summary>按钮可用性：没勾选任何条目时，「解锁 / 锁定」都不可用。</summary>
        private void UpdateButtons()
        {
            var any = _list != null && _list.CheckedCount > 0;
            SetInteractable(_unlock, any);
            SetInteractable(_lock, any);
        }

        private static void SetInteractable(UiOptionRow row, bool value)
        {
            if (row == null || row.Widgets == null || row.Widgets.TextButton == null)
                return;
            try
            {
                var button = row.Widgets.TextButton.Button;
                if (button != null)
                    button.interactable = value;
            }
            catch (Exception) { }
        }

        /// <summary>每帧：绿宝石是可被游戏改写的值，慢速同步一下。</summary>
        public void Tick()
        {
            if (!_built || _money == null || _money.IsFocused)
                return;
            try
            {
                // 解锁条件来自资源侧，标题界面第一次建页时可能还没加载完（拿到 0 条）→
                // 空表时重试，加载完就自己长出来，不用让用户重开面板。
                if (_sourceCount == 0)
                    RefreshSource();

                var text = ProgressOps.GetMoney().ToString();
                if (_money.Text != text)
                    _money.Text = text;
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        public string Report()
        {
            var sb = new System.Text.StringBuilder(260);
            sb.Append("来源条目=").Append(_sourceCount).Append('\n');
            sb.Append(_search == null ? "搜索框=（无）" : _search.Describe()).Append('\n');
            sb.Append(_list == null ? "列表=（无）" : _list.Describe()).Append('\n');
            sb.Append("最近一次应用耗时=").Append(_lastApplyMs).Append(" ms")
              .Append("  解锁按钮可用=").Append(Interactable(_unlock) ? 1 : 0)
              .Append("  锁定按钮可用=").Append(Interactable(_lock) ? 1 : 0)
              .Append('\n');
            return sb.ToString();
        }

        private static bool Interactable(UiOptionRow row)
        {
            try
            {
                if (row == null || row.Widgets == null || row.Widgets.TextButton == null)
                    return false;
                var button = row.Widgets.TextButton.Button;
                return button != null && button.interactable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string Describe()
        {
            return "来源条目=" + _sourceCount
                + "  搜索=" + (_search == null ? "无" : "OK")
                + "  列表=" + (_list == null ? "无" : ("OK/" + _list.RowCount + " 行"));
        }
    }
}
