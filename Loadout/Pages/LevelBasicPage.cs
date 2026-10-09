using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.UI;
using PVZEngine;
using PVZEngine.Level;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 关卡内 · 页1「基础」：**5 行 × 4 列**的网格（与需求里的 5*4 网格对齐一致）。
    ///
    /// <para>一行 = 一个横向容器（见 <see cref="UiGridRow"/>）；每个单元格自己
    /// <c>flexibleWidth = 1</c>，所以四列自动等宽。整页没有任何坐标／像素常量。</para>
    ///
    /// <para><b>即时生效 vs 需要重载</b>（原版行为，改不了）：背景决定地格布局，只在
    /// <c>LevelEngine.Init</c> 时建立，所以「背景 / 关卡」只是**记下待改的值**，
    /// 由第五行的「重载」按钮真正应用；其余项都是即时生效。</para>
    /// </summary>
    internal sealed class LevelBasicPage : IPanelPage
    {
        public string Name { get { return "基础"; } }

        // ------------------------------------------------------------------
        // 项目表
        // ------------------------------------------------------------------

        private sealed class ToggleItem
        {
            public string Label;
            public string Tooltip;
            public Func<LevelEngine, bool> Get;
            public Action<LevelEngine, bool> Set;
            /// <summary>功能开关所属的兼容等级档位；null = 不归等级管。</summary>
            public GcFeature? Feature;
            /// <summary>切换之后要补一手的事（例如把能量/星之碎片补到上限）。</summary>
            public Action<LevelEngine, bool> After;
            public UiOptionRow Row;
        }

        private sealed class NumberItem
        {
            public string Label;
            public string Tooltip;
            public Func<LevelEngine, string> Get;
            public Action<LevelEngine, string> Set;
            public UiField Field;
        }

        private readonly List<ToggleItem> _toggles = new List<ToggleItem>();
        private readonly List<NumberItem> _numbers = new List<NumberItem>();
        private UiCompleter _area, _stage, _difficulty, _music;
        /// <summary>待重载项：选了但还没生效的值（null = 无待改）。</summary>
        private NamespaceID _pendingArea, _pendingStage, _pendingDifficulty;
        /// <summary>重载按钮的文字（有待重载项时变黄）与其原本颜色。</summary>
        private TextMeshProUGUI _reloadText;
        private Color _reloadColor = Color.white;
        /// <summary>热一下索引缓存（真的建不了也无妨：补齐字段自己会在用的时候再取）。</summary>
        private static void WarmIndexes()
        {
            try
            {
                NameIndexes.Get(NameKind.Area);
                NameIndexes.Get(NameKind.Stage);
                NameIndexes.Get(NameKind.Difficulty);
                NameIndexes.Get(NameKind.Music);
            }
            catch (Exception) { }
        }
        private bool _built;
        /// <summary>上次刷置灰时的兼容等级（变了才重刷，包括单项开关的改动）。</summary>
        private string _gateStamp;
        /// <summary>最后装配的那个实例（自测用）。</summary>
        private static LevelBasicPage _last;

        // ------------------------------------------------------------------
        // 装配
        // ------------------------------------------------------------------

        public void Build()
        {
            _last = this;
            _toggles.Clear();
            _numbers.Clear();
            _area = _stage = _difficulty = _music = null;
            _pendingArea = _pendingStage = _pendingDifficulty = null;
            _reloadText = null;
            _reloadColor = Color.white;
            _built = false;

            var level = LevelFacade.Current;
            if (level == null)
            {
                AddNote("本页只在关卡内可用（当前不在关卡内）。");
                return;
            }

            BuildToggles(level);
            BuildPickers(level);
            BuildNumbers(level);
            BuildReloadRow(level);
            _built = true;
            Plugin.Log?.LogInfo("[BasicPage] 已装配：勾选项 " + _toggles.Count + "，数值项 " + _numbers.Count);
        }

        private void BuildToggles(LevelEngine level)
        {
            var items = new List<ToggleItem>
            {
                new ToggleItem
                {
                    Label = "上帝模式",
                    Tooltip = "关卡不会失败，且不扣血。",
                    Get = l => LevelFacade.GetGodMode(l),
                    Set = (l, v) => LevelFacade.SetGodMode(l, v),
                },
                new ToggleItem
                {
                    Label = "无限能量",
                    Tooltip = "把能量补到上限，之后每次消耗都立即回满。\n"
                        + "消耗的那一帧就补，所以面板把游戏暂停着也能正常使用蓝图。",
                    Get = l => LevelFacade.HasBuff(l, LevelFacade.BuffEnergy),
                    Set = (l, v) => LevelFacade.SetBuff(l, LevelFacade.BuffEnergy, v),
                    // 开启的当下就补一次，不必等到下一次消耗。
                    After = (l, v) => { if (v) CheatRefill.Energy(l); },
                },
                new ToggleItem
                {
                    Label = "无限星之碎片",
                    Tooltip = "把星之碎片补到槽位数上限，之后每次消耗都立即回满。\n"
                        + "消耗的那一帧就补，所以面板把游戏暂停着也能正常使用星之碎片。",
                    Get = l => LevelFacade.HasBuff(l, LevelFacade.BuffStarshard),
                    Set = (l, v) => LevelFacade.SetBuff(l, LevelFacade.BuffStarshard, v),
                    After = (l, v) => { if (v) CheatRefill.Starshard(l); },
                },
                new ToggleItem
                {
                    Label = "禁用冷却",
                    Tooltip = "所有蓝图立刻充能完成（等价于原版 /cheat recharge）。\n"
                        + "写的是关卡属性，所以跟着存档走；蓝图页里的「冷却(秒)」仍然可以单独改。",
                    Feature = GcFeature.NoRecharge,
                    // ★ 直接用**原版**的实现：原作者已经把 /cheat recharge 改成写关卡属性
                    //   `EngineLevelProps.NO_RECHARGE`（种子包的 IsCharged() 在它为 true 时恒为 true）。
                    //   所以这里不再自己逐卡备份/替换冷却 —— 那份备份没必要了，
                    //   而「逐卡改冷却」这件独立的事仍然保留在蓝图页（冷却/预设/重置）。
                    Get = l => PVZEngine.Level.EngineLevelProps.NoRecharge(l),
                    Set = (l, v) => PVZEngine.Level.EngineLevelProps.SetNoRecharge(l, v),
                },
                new ToggleItem
                {
                    Label = "后台暂停",
                    Tooltip = "面板开着时把游戏速度压到 0。\n该开关与设置里的「失去焦点时暂停」是同一个，所以失焦时不会再弹暂停框。",
                    Get = l => PanelPause.Enabled,
                    Set = (l, v) => PanelPause.SetEnabled(v),
                },
                new ToggleItem
                {
                    Label = "忽略大波事件",
                    Tooltip = "不再出大波（一大波僵尸正在接近）。",
                    Get = l => LevelFacade.GetIgnoreHugeWave(l),
                    Set = (l, v) => LevelFacade.SetIgnoreHugeWave(l, v),
                },
                new ToggleItem
                {
                    Label = "自动收集",
                    Tooltip = "自动收集掉落的银币、金币、星之碎片等资源。",
                    Get = l => LevelFacade.GetAutoCollect(l),
                    Set = (l, v) => LevelFacade.SetAutoCollect(l, v),
                },
                new ToggleItem
                {
                    Label = "隐藏HUD",
                    Tooltip = "隐藏关卡底部那一整条界面（蓝图、能量、星之碎片、铁镐、驱动、进度条、菜单与加速按钮等）。",
                    Get = l => LevelHud.Hidden,
                    Set = (l, v) => LevelHud.Set(v),
                },
            };

            for (var i = 0; i < items.Count; i += 4)
            {
                var row = UiGridRow.Create("Row_Toggle" + (i / 4));
                if (row == null)
                    return;
                for (var c = 0; c < 4 && i + c < items.Count; c++)
                {
                    var item = items[i + c];
                    var cell = row.AddOptionRow("T_" + item.Label, UiOptionKind.Toggle);
                    if (cell == null)
                        continue;
                    var captured = item;
                    cell.SetToggle(item.Label, item.Get(level), v => OnToggle(captured, v));
                    cell.SetTooltip(item.Label, TooltipOf(item));
                    ApplyGate(item);
                    item.Row = cell;
                    _toggles.Add(item);
                }
                LoadoutPanel.AddRow(row.Finish());
            }
        }

        private void BuildPickers(LevelEngine level)
        {
            WarmIndexes();

            var row = UiGridRow.Create("Row_Pick");
            if (row == null)
                return;
            var rowRoot = row.Finish();

            var areaField = row.AddField("F_Area", "背景");
            if (areaField != null)
            {
                _area = UiCompleter.Attach(areaField, NameKind.Area, rowRoot);
                if (_area != null)
                    _area.SetHandler(entry => OnPickArea(level, entry)).SetValue(LevelFacade.GetAreaID(level));
                LoadoutPanel.AddTooltip(areaField.Root, "背景", "选好后点「重载」生效。\n");
            }

            var stageField = row.AddField("F_Stage", "关卡");
            if (stageField != null)
            {
                _stage = UiCompleter.Attach(stageField, NameKind.Stage, rowRoot);
                if (_stage != null)
                    _stage.SetHandler(entry => OnPickStage(level, entry)).SetValue(LevelFacade.GetStageID(level));
                LoadoutPanel.AddTooltip(stageField.Root, "关卡", "选好后点「重载」生效。");
            }

            var diffField = row.AddField("F_Difficulty", "难度");
            if (diffField != null)
            {
                _difficulty = UiCompleter.Attach(diffField, NameKind.Difficulty, rowRoot);
                if (_difficulty != null)
                    _difficulty.SetHandler(entry => OnPickDifficulty(level, entry))
                        .SetValue(LevelFacade.GetDifficulty(level));
                LoadoutPanel.AddTooltip(diffField.Root, "难度", "会修改全局难度。\n选好后点「重载」生效。");
            }

            var musicField = row.AddField("F_Music", "音乐");
            if (musicField != null)
            {
                _music = UiCompleter.Attach(musicField, NameKind.Music, rowRoot);
                if (_music != null)
                    _music.SetHandler(entry => OnPickMusic(level, entry)).SetValue(LevelFacade.GetMusicID(level));
            }

            LoadoutPanel.AddRow(rowRoot);
        }

        private void BuildNumbers(LevelEngine level)
        {
            var items = new List<NumberItem>
            {
                new NumberItem
                {
                    Label = "旗数",
                    Tooltip = "当前是第几旗，改完立刻生效。",
                    Get = l => LevelFacade.GetCurrentFlag(l).ToString(),
                    Set = (l, t) => ApplyInt(t, v => LevelFacade.SetCurrentFlag(l, v)),
                },
                new NumberItem
                {
                    Label = "波数",
                    Tooltip = "当前是第几波，改完立刻生效。",
                    Get = l => LevelFacade.GetCurrentWave(l).ToString(),
                    Set = (l, t) => ApplyInt(t, v => LevelFacade.SetCurrentWave(l, v)),
                },
                new NumberItem
                {
                    Label = "能量上限",
                    Tooltip = "能量上限。改完之后当前能量会立刻补到新上限。",
                    Get = l => LevelFacade.GetMaxEnergy(l).ToString(),
                    Set = (l, t) => ApplyInt(t, v => LevelFacade.SetMaxEnergy(l, v)),
                },
                new NumberItem
                {
                    Label = "星之碎片上限",
                    Tooltip = "能同时持有的星之碎片数。改完之后会立刻补满。",
                    Get = l => LevelFacade.GetStarshardSlots(l).ToString(),
                    Set = (l, t) => ApplyInt(t, v => LevelFacade.SetStarshardSlots(l, v)),
                },
                new NumberItem
                {
                    Label = "出怪点数",
                    Tooltip = SpawnTip("点数"),
                    Get = l => Num(LevelFacade.GetSpawnPointAddition(l)),
                    Set = (l, t) => ApplyFloat(t, v => LevelFacade.SetSpawnPointAddition(l, v)),
                },
                new NumberItem
                {
                    Label = "出怪倍数",
                    Tooltip = SpawnTip("倍数"),
                    Get = l => Num(LevelFacade.GetSpawnPointMultiplier(l)),
                    Set = (l, t) => ApplyFloat(t, v => LevelFacade.SetSpawnPointMultiplier(l, v)),
                },
                new NumberItem
                {
                    Label = "出怪幂数",
                    Tooltip = SpawnTip("幂数"),
                    Get = l => Num(LevelFacade.GetSpawnPointPower(l)),
                    Set = (l, t) => ApplyFloat(t, v => LevelFacade.SetSpawnPointPower(l, v)),
                },
            };

            for (var i = 0; i < items.Count; i += 4)
            {
                var row = UiGridRow.Create("Row_Num" + (i / 4));
                if (row == null)
                    return;
                for (var c = 0; c < 4 && i + c < items.Count; c++)
                {
                    var item = items[i + c];
                    var field = row.AddField("N_" + item.Label, item.Label, numeric: true,
                        integer: item.Label != "出怪点数" && item.Label != "出怪倍数" && item.Label != "出怪幂数");
                    if (field == null)
                        continue;
                    field.Text = item.Get(level);
                    var captured = item;
                    field.OnSubmit(text => OnNumber(captured, text));
                    LoadoutPanel.AddTooltip(field.Root, item.Label, item.Tooltip);
                    item.Field = field;
                    _numbers.Add(item);
                }
                _lastNumberRow = row;
                LoadoutPanel.AddRow(row.Finish());
            }
        }

        private UiGridRow _lastNumberRow;

        /// <summary>
        /// 第五行最后一格是「重载」按钮：它补在**最后一行**的第四个格里，
        /// 所以出怪点数/倍数/幂数/重载 是同一行（与需求一致）。
        /// </summary>
        private void BuildReloadRow(LevelEngine level)
        {
            var row = _lastNumberRow;
            if (row == null)
                return;
            var button = row.AddOptionRow("R_Reload", UiOptionKind.Button);
            if (button == null)
                return;
            button.SetButton("重载", () => OnReload(level));
            button.SetTooltip("重载", ReloadTooltip());
            // 记下按钮文字：有待重载项时把它变黄（需求）。
            try
            {
                var tb = button.Widgets == null ? null : button.Widgets.TextButton;
                _reloadText = tb == null ? null : tb.Text;
                if (_reloadText != null)
                    _reloadColor = _reloadText.color;
            }
            catch (Exception) { }
            UpdateReloadVisual();
        }

        private bool HasPending
        {
            get { return _pendingArea != null || _pendingStage != null || _pendingDifficulty != null; }
        }

        /// <summary>把「有待重载项」画到重载按钮上（文字变黄 + 加一个 * 提示）。</summary>
        private void UpdateReloadVisual()
        {
            if (_reloadText == null)
                return;
            try
            {
                var pending = HasPending;
                _reloadText.text = pending ? "重载 *" : "重载";
                _reloadText.color = pending
                    ? new Color(1f, 0.86f, 0.28f, _reloadColor.a)
                    : _reloadColor;
            }
            catch (Exception) { }
        }

        private void AddNote(string text)
        {
            var go = UiHost.CreateNode("Note", null);
            if (!UiFinder.IsAlive(go))
                return;
            var label = UiKit.Label(go.transform, "Text", text);
            if (label != null)
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.minHeight = -1f;
                    le.preferredHeight = 40f;
                    le.flexibleWidth = 1f;
                }
            }
            LoadoutPanel.AddRow(go);
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        private void OnToggle(ToggleItem item, bool value)
        {
            var level = LevelFacade.Current;
            if (level == null || item == null || item.Set == null)
                return;
            try
            {
                item.Set(level, value);
                if (item.After != null)
                    item.After(level, value);
                Plugin.Log?.LogInfo("[BasicPage] " + item.Label + " → " + (value ? "开" : "关"));
            }
            catch (Exception e)
            {
                Plugin.LogOnce("basic-toggle", "[BasicPage] 应用「" + item.Label + "」失败：" + e.GetType().Name);
            }
        }

        // ------------------------------------------------------------------
        // 兼容性等级：扩展功能的置灰与提示
        // ------------------------------------------------------------------

        /// <summary>
        /// 这一项在当前等级下能不能用。（<see cref="CompatConfig.IsEnabled"/> 已经含单项开关。）
        /// </summary>
        private static bool Allowed(ToggleItem item)
        {
            return item == null || !item.Feature.HasValue
                || CompatConfig.IsEnabled(item.Feature.Value);
        }

        /// <summary>
        /// 提示文字：**被等级挡住时只说原因**（原来的功能说明整段不显示）。
        /// 这正是「部分内容做成响应式」的要求 —— 置灰了还写一大堆「会发生什么」，
        /// 玩家会以为功能坏了。
        /// </summary>
        private static string TooltipOf(ToggleItem item)
        {
            if (item == null)
                return null;
            if (!Allowed(item))
                return CompatConfig.DisabledHint(item.Feature.Value);
            return item.Tooltip;
        }

        /// <summary>按当前等级把这一项点亮 / 置灰。</summary>
        private static void ApplyGate(ToggleItem item)
        {
            if (item == null || item.Row == null)
                return;
            SetRowEnabled(item.Row, Allowed(item));
        }

        /// <summary>批量刷一遍（等级是配置项，随时会变）。</summary>
        private void ApplyGates()
        {
            for (var i = 0; i < _toggles.Count; i++)
            {
                var item = _toggles[i];
                ApplyGate(item);
                if (item.Row != null)
                    item.Row.SetTooltip(item.Label, TooltipOf(item));
            }
        }

        /// <summary>改一个设置项（复选框）的可点状态。</summary>
        private static void SetRowEnabled(UiOptionRow row, bool on)
        {
            var w = row == null ? null : row.Widgets;
            if (w == null)
                return;
            try
            {
                if (w.Toggle != null && w.Toggle.Toggle != null)
                    w.Toggle.Toggle.interactable = on;
            }
            catch (Exception) { }
        }

        private void OnNumber(NumberItem item, string text)
        {
            var level = LevelFacade.Current;
            if (level == null || item == null || item.Set == null)
                return;
            try
            {
                item.Set(level, text);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("basic-num", "[BasicPage] 应用「" + item.Label + "」失败：" + e.GetType().Name);
            }
            // 解析失败时回退成游戏里的实际值（与补齐字段同一规则）。
            SyncNumber(item, level, true);
        }

        private void OnPickArea(LevelEngine level, NameIndex.Entry entry)
        {
            if (entry == null)
            {
                // entry 为 null = 框被清空（不是“无法识别”）→ 取消待改，不动当前值。
                _pendingArea = null;
                UpdateReloadVisual();
                return;
            }
            var now = LevelFacade.GetAreaID(level);
            if (now != null && now.Equals(entry.Id))
            {
                // 选回原值 = 没有改动（聚焦后原样提交就会走到这里，不该报“待改为”）。
                _pendingArea = null;
                UpdateReloadVisual();
                return;
            }
            _pendingArea = entry.Id;
            UpdateReloadVisual();
            Plugin.Log?.LogInfo("[BasicPage] 背景待改为 " + entry.Display + "（需点「重载」生效）");
        }

        private void OnPickStage(LevelEngine level, NameIndex.Entry entry)
        {
            if (entry == null)
            {
                _pendingStage = null;
                UpdateReloadVisual();
                return;
            }
            var now = LevelFacade.GetStageID(level);
            if (now != null && now.Equals(entry.Id))
            {
                _pendingStage = null;
                UpdateReloadVisual();
                return;
            }
            _pendingStage = entry.Id;
            UpdateReloadVisual();
            Plugin.Log?.LogInfo("[BasicPage] 关卡待改为 " + entry.Display + "（需点「重载」生效）");
        }

        /// <summary>
        /// 难度：**改为待重载项**。
        ///
        /// <para>早期实现是当场 <c>level.SetDifficulty</c>，但它会被官方的
        /// <c>UpdateDifficulty()</c>（每次开局用全局设置覆写）改回去 —— 实测“改完立刻变回去”。
        /// 现在只记下选择，真正的应用在「重载」里（连全局设置一起写）。</para>
        /// </summary>
        private void OnPickDifficulty(LevelEngine level, NameIndex.Entry entry)
        {
            if (entry == null)
            {
                _pendingDifficulty = null;
                UpdateReloadVisual();
                return;
            }
            var now = LevelFacade.GetDifficulty(level);
            if (now != null && now.Equals(entry.Id))
            {
                _pendingDifficulty = null;
                UpdateReloadVisual();
                return;
            }
            _pendingDifficulty = entry.Id;
            UpdateReloadVisual();
            Plugin.Log?.LogInfo("[BasicPage] 难度待改为 " + entry.Display + "（需点「重载」生效）");
        }

        private void OnPickMusic(LevelEngine level, NameIndex.Entry entry)
        {
            if (entry == null)
                return;
            LevelFacade.SetMusicID(level, entry.Id);
        }

        private void OnReload(LevelEngine level)
        {
            var area = _pendingArea;
            var stage = _pendingStage;
            var diff = _pendingDifficulty;
            Plugin.Log?.LogInfo("[BasicPage] 重载：背景=" + Id(area)
                + "  关卡=" + Id(stage) + "  难度=" + Id(diff));
            if (LevelFacade.ReloadLevel(area, stage, diff))
            {
                _pendingArea = null;
                _pendingStage = null;
                _pendingDifficulty = null;
                UpdateReloadVisual();
            }
            else
            {
                Plugin.Log?.LogWarning("[BasicPage] 重载失败（详见上方日志）");
            }
        }

        /// <summary>
        /// 出怪点数 / 倍数 / 幂数的提示。
        ///
        /// <para>三者合起来决定出怪强度：<c>点数 = 基础点数^幂数 × 倍数 + 点数</c>。
        /// ★ 关键要去掉括号堆的那句：**「你填的就是生效的」** ——
        /// 难度等修饰器（例如困难的 <c>+20%</c>）会被自动反算掉，所以填完不会看到值被改掉。</para>
        /// </summary>
        private static string SpawnTip(string which)
        {
            return "出怪强度 = 基础点数^幂数 × 倍数 + 点数，向上取整。\n"
                + "你填的" + which + "就是实际生效的值，难度那些修正会被自动扣掉。";
        }

        private static string ReloadTooltip()
        {
            return "把当前关卡状态搬到上面选的「背景 / 关卡 / 难度」下重进。\n"
                + "原关卡的存档保留，新关卡接着你现在的进度跑。";
        }

        // ------------------------------------------------------------------
        // 慢速同步
        // ------------------------------------------------------------------

        public void Tick()
        {
            if (!_built)
                return;
            var level = LevelFacade.Current;
            if (level == null)
            {
                LoadoutPanel.Invalidate();
                return;
            }

            // 兼容等级 / 单项开关随时可能变（设置项改了当场生效），变了就重刷一遍置灰与提示。
            var stamp = CompatConfig.Level.ToString();
            if (stamp != _gateStamp)
            {
                _gateStamp = stamp;
                ApplyGates();
            }

            for (var i = 0; i < _toggles.Count; i++)
            {
                var item = _toggles[i];
                if (item.Row == null)
                    continue;
                try
                {
                    var value = item.Get(level);
                    var toggle = item.Row.Widgets == null || item.Row.Widgets.Toggle == null
                        ? null
                        : item.Row.Widgets.Toggle.Toggle;
                    if (toggle != null && toggle.isOn != value)
                        toggle.SetIsOnWithoutNotify(value);
                }
                catch (Exception) { }
            }

            for (var i = 0; i < _numbers.Count; i++)
                SyncNumber(_numbers[i], level, false);

            // 选值格：游戏内部也会改这些值（换音乐、切难度、过关换关卡）→ 静默跟一下。
            //
            // ⚠️ **有待重载项的格子不能回写**：那会把用户刚选的值当场改回游戏里的值
            // （实测症状就是“改了立刻变回去”）。要更正就重新打开面板/翻页 ——
            // 那时页面会重建、重新从游戏读值，于是没能生效的改动自然就显露出来了。
            if (_pendingArea == null)
                SyncPicker(_area, LevelFacade.GetAreaID(level));
            if (_pendingStage == null)
                SyncPicker(_stage, LevelFacade.GetStageID(level));
            if (_pendingDifficulty == null)
                SyncPicker(_difficulty, LevelFacade.GetDifficulty(level));
            SyncPicker(_music, LevelFacade.GetMusicID(level));
        }

        /// <summary>把选值格同步到游戏里的实际值（编辑中不覆盖，值没变也不写）。</summary>
        private static void SyncPicker(UiCompleter completer, NamespaceID id)
        {
            try
            {
                if (completer != null)
                    completer.SyncTo(id);
            }
            catch (Exception) { }
        }

        private static void SyncNumber(NumberItem item, LevelEngine level, bool force)
        {
            if (item == null || item.Field == null)
                return;
            try
            {
                // 正在编辑的输入框不覆盖（否则用户打字打到一半会被游戏值刷掉）。
                if (!force && item.Field.IsFocused)
                    return;
                var text = item.Get(level);
                if (force || item.Field.Text != text)
                    item.Field.Text = text;
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 数值解析
        // ------------------------------------------------------------------

        private static void ApplyInt(string text, Action<int> apply)
        {
            int value;
            if (!TryInt(text, out value))
                return;
            apply(value);
        }

        private static void ApplyFloat(string text, Action<float> apply)
        {
            float value;
            if (!TryFloat(text, out value))
                return;
            apply(value);
        }

        private static bool TryInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool TryFloat(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return float.TryParse(text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static string Num(float v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        /// <summary>
        /// 自测用：每个勾选项的**可点状态 + 实际生效的提示文字**。
        ///
        /// <para>本轮两个需求靠它证明：① 扩展功能按当前兼容等级置灰（Safe 下「禁用冷却」可点、
        /// Standard 下才落盘）；② 置灰时提示里**只剩置灰原因**，不再是一大段功能说明。</para>
        /// </summary>
        public static string DebugGate()
        {
            if (_last == null || !_last._built)
                return "（基础页未装配）";
            var p = _last;
            var sb = new System.Text.StringBuilder(600);
            sb.Append("等级=").Append(CompatConfig.Level)
              .Append("  可落盘=").Append(CompatConfig.IsEnabled(GcFeature.SaveCustomData) ? "是" : "否")
              .Append('\n');
            for (var i = 0; i < p._toggles.Count; i++)
            {
                var it = p._toggles[i];
                var on = false;
                try
                {
                    on = it.Row != null && it.Row.Widgets != null && it.Row.Widgets.Toggle != null
                        && it.Row.Widgets.Toggle.Toggle.interactable;
                }
                catch (Exception) { }
                sb.Append("  ").Append(it.Label)
                  .Append("  可点=").Append(on ? "是" : "否")
                  .Append("  功能=").Append(it.Feature.HasValue ? it.Feature.Value.ToString() : "-")
                  .Append("\n    提示：").Append(TooltipOf(it)).Append('\n');
            }
            return sb.ToString();
        }

        public string Report()
        {
            var sb = new System.Text.StringBuilder(220);
            var level = LevelFacade.Current;
            sb.Append("关卡=").Append(level == null ? "（无）" : "OK")
              .Append("  勾选项=").Append(_toggles.Count)
              .Append("  数值项=").Append(_numbers.Count)
              .Append("  选值格=").Append(PickerCount())
              .Append('\n');
            sb.Append("待重载：背景=").Append(Id(_pendingArea))
              .Append("  关卡=").Append(Id(_pendingStage))
              .Append("  难度=").Append(Id(_pendingDifficulty))
              .Append("  按钮变黄=").Append(_reloadText != null && HasPending ? 1 : 0).Append('\n');
            sb.Append("选值格=")
              .Append("背景「").Append(Text(_area)).Append("」 ")
              .Append("关卡「").Append(Text(_stage)).Append("」 ")
              .Append("难度「").Append(Text(_difficulty)).Append("」 ")
              .Append("音乐「").Append(Text(_music)).Append("」").Append('\n');
            return sb.ToString();
        }

        private int PickerCount()
        {
            var n = 0;
            if (_area != null) n++;
            if (_stage != null) n++;
            if (_difficulty != null) n++;
            if (_music != null) n++;
            return n;
        }

        private static string Text(UiCompleter completer)
        {
            try { return completer == null ? "（无）" : completer.Text; }
            catch (Exception) { return "?"; }
        }

        private static string Id(NamespaceID id)
        {
            if (id == null)
                return "（不变）";
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return "（无法读取）"; }
        }
    }
}
