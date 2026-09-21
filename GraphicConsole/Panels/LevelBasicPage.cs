using System;
using System.Collections.Generic;
using PVZEngine;
using PVZEngine.Level;
using TMPro;
using UnityEngine;

namespace GraphicConsole
{
    /// <summary>
    /// 关卡内面板 · 页面1「基础」，按需求文档的 5×4 网格对齐：
    /// <code>
    /// 行1  []上帝模式      []无限能量       []无限星之碎片   []禁用冷却
    /// 行2  []后台暂停      []忽略大波事件   []自动收集       []隐藏HUD
    /// 行3  背景:&lt;&gt;         关卡:&lt;&gt;          难度:&lt;&gt;          音乐:&lt;&gt;
    /// 行4  旗数:__        波数:__          能量上限:__      星之碎片上限:__
    /// 行5  出怪点数:__    出怪倍数:__      出怪幂数:__      {重载}
    /// </code>
    ///
    /// 「即时生效 vs 需要重载」的判定（源码核实过）：
    ///   · 四个作弊开关 / 后台暂停 / 忽略大波 / 自动收集 / 隐藏HUD —— 每帧或事件触发时读属性，即时；
    ///   · 旗数、波数、能量上限、星之碎片上限 —— 即时（UI 每帧刷新）；
    ///   · 音乐、难度 —— 即时（音乐走 LevelController.CurrentMusic，难度自己换难度 buff）；
    ///   · 出怪三参数 —— 即时（<c>LogicLevelExt.CalculateSpawnPoints</c> 每波重读）；
    ///   · **背景 / 关卡** —— 必须重载（Area 决定地格布局，只在关卡初始化时建立）。
    /// 所以说明只挂在「背景 / 关卡 / 重载」上（需求：说明放悬停提示，不占面板空间）。
    /// </summary>
    internal sealed class LevelBasicPage : PanelPageBase
    {
        public override string Title { get { return "基础"; } }

        private const int Columns = 4;
        private const float RowH = 46f;
        private const float RowGap = 4f;
        /// <summary>窄格子里标签的最大宽度（要留出下拉框的位置）。</summary>
        private const float NarrowLabelWidth = 96f;

        // 行1
        private WidgetCell cGodMode, cEnergy, cStarshard, cNoRecharge;
        // 行2
        private WidgetCell cPause, cHugeWave, cAutoCollect, cHideGui;
        // 行3
        private WidgetCell cArea, cStage, cDifficulty, cMusic;
        // 行4
        private WidgetCell cFlag, cWave, cMaxEnergy, cStarshardSlots;
        // 行5
        private WidgetCell cSpawnAdd, cSpawnMul, cSpawnPow, cReload;

        private List<NamespaceID> areas = new List<NamespaceID>();
        private List<NamespaceID> stages = new List<NamespaceID>();
        private List<NamespaceID> difficulties = new List<NamespaceID>();
        private List<NamespaceID> musics = new List<NamespaceID>();

        private NamespaceID pendingArea;
        private NamespaceID pendingStage;

        private static float CellWidth
        {
            get { return (GcLayout.ContentWidth - RowGap * (Columns - 1)) / Columns; }
        }

        protected override void OnBuild()
        {
            areas = LevelFacade.ListAreas();
            stages = LevelFacade.ListStages();
            difficulties = LevelFacade.ListDifficulties();
            musics = LevelFacade.ListMusics();

            var y = 0f;
            // 行1
            var r1 = NewRow(Content, "Row1", y, RowH, RowGap);
            cGodMode = NewCell(r1, "godmode", CellKind.Toggle, "上帝模式", CellWidth);
            cEnergy = NewCell(r1, "energy", CellKind.Toggle, "无限能量", CellWidth);
            cStarshard = NewCell(r1, "starshard", CellKind.Toggle, "无限星之碎片", CellWidth);
            cNoRecharge = NewCell(r1, "norecharge", CellKind.Toggle, "禁用冷却", CellWidth);
            y += RowH + RowGap;

            // 行2
            var r2 = NewRow(Content, "Row2", y, RowH, RowGap);
            cPause = NewCell(r2, "pause", CellKind.Toggle, "后台暂停", CellWidth);
            cHugeWave = NewCell(r2, "hugewave", CellKind.Toggle, "忽略大波事件", CellWidth);
            cAutoCollect = NewCell(r2, "autocollect", CellKind.Toggle, "自动收集", CellWidth);
            cHideGui = NewCell(r2, "hidegui", CellKind.Toggle, "隐藏HUD", CellWidth);
            y += RowH + RowGap;

            // 行3：四个下拉框（窄格子 → 控件吸收剩余宽度）
            var r3 = NewRow(Content, "Row3", y, RowH, RowGap);
            cArea = NewCell(r3, "area", CellKind.Dropdown, "背景", CellWidth, NarrowLabelWidth, true);
            cStage = NewCell(r3, "stage", CellKind.Dropdown, "关卡", CellWidth, NarrowLabelWidth, true);
            cDifficulty = NewCell(r3, "difficulty", CellKind.Dropdown, "难度", CellWidth, NarrowLabelWidth, true);
            cMusic = NewCell(r3, "music", CellKind.Dropdown, "音乐", CellWidth, NarrowLabelWidth, true);
            y += RowH + RowGap;

            // 行4
            var r4 = NewRow(Content, "Row4", y, RowH, RowGap);
            cFlag = NewCell(r4, "flag", CellKind.Number, "旗数", CellWidth, NarrowLabelWidth, true);
            cWave = NewCell(r4, "wave", CellKind.Number, "波数", CellWidth, NarrowLabelWidth, true);
            cMaxEnergy = NewCell(r4, "maxenergy", CellKind.Number, "能量上限", CellWidth, NarrowLabelWidth, true);
            cStarshardSlots = NewCell(r4, "starshardSlots", CellKind.Number, "星之碎片上限", CellWidth, NarrowLabelWidth, true);
            y += RowH + RowGap;

            // 行5
            var r5 = NewRow(Content, "Row5", y, RowH, RowGap);
            cSpawnAdd = NewCell(r5, "spawnadd", CellKind.Number, "出怪点数", CellWidth, NarrowLabelWidth, true);
            cSpawnMul = NewCell(r5, "spawnmul", CellKind.Number, "出怪倍数", CellWidth, NarrowLabelWidth, true);
            cSpawnPow = NewCell(r5, "spawnpow", CellKind.Number, "出怪幂数", CellWidth, NarrowLabelWidth, true);
            cReload = NewCell(r5, "reload", CellKind.Button, "重载", CellWidth);
            y += RowH + RowGap;

            UiFactory.SetDropdownOptions(cArea, LevelFacade.LabelsFor(areas, NameResolver.Area));
            UiFactory.SetDropdownOptions(cStage, LevelFacade.LabelsFor(stages, NameResolver.Stage));
            UiFactory.SetDropdownOptions(cDifficulty, LevelFacade.LabelsFor(difficulties, NameResolver.Difficulty));
            UiFactory.SetDropdownOptions(cMusic, LevelFacade.LabelsFor(musics, NameResolver.Music));

            if (cReload != null)
                UiFactory.BindClick(cReload.Button, OnReloadClicked);

            RegisterTooltips();
        }

        /// <summary>
        /// 需求：特殊按钮的说明写在**鼠标悬停提示**里，不额外占用面板空间
        /// （所以这里没有常驻的提示文本行）。
        /// </summary>
        private void RegisterTooltips()
        {
            AddTooltip(cPause, "「后台暂停」= 原版设置界面里同一个选项（pause_on_focus_lost）。\n"
                + "勾上时：窗口失焦自动暂停；并且面板打开期间把游戏速度压到 0\n"
                + "（等效于速度倍率为 0 的加速）—— 关卡逻辑冻结，但蓝图 / 镐子 / 星之碎片 / 草坪点击照常可用。");
            AddTooltip(cHideGui, "一次性隐藏关卡 HUD：\n"
                + "蓝图（含传送带）、能量、星之碎片、铁镐、驱动、关卡进度、菜单按钮、加速按钮、\n"
                + "以及拾取金币时出现的金币数量。\n"
                + "取消勾选会按隐藏前的实际状态恢复（不会把本来就被关卡关掉的项打开）。");
            AddTooltip(cArea, "背景（章节）决定了地格布局，**只在关卡初始化时建立** —— 改了必须点「重载」才生效。\n"
                + "只改背景时关卡 id 没变，状态存档文件名不变，所以重载后进度保留。");
            AddTooltip(cStage, "关卡 id 决定状态存档的文件名（<用户存档>/<命名空间>/level/<关卡>.lvl）。\n"
                + "改成**别的关卡**后重载，找不到可复用的状态存档，会以新关卡从头开始。");
            AddTooltip(cReload, ReloadTooltip());
        }

        private void AddTooltip(WidgetCell cell, string text)
        {
            if (cell == null || Shell == null || text == null)
                return;
            Shell.AddTooltip(cell, text);
        }

        private string ReloadTooltip()
        {
            var text = "保存当前关卡数据 → 重新载入这个关卡（走官方 LevelController.ReloadLevel）。\n"
                + "只有「背景 / 关卡」需要重载。\n"
                + "出怪三参数受原版硬上限 " + LevelFacade.SpawnPointHardCap + " 限制。";
            var parts = new List<string>();
            if (pendingArea != null)
                parts.Add("背景=" + NameResolver.Area(pendingArea));
            if (pendingStage != null)
                parts.Add("关卡=" + NameResolver.Stage(pendingStage));
            if (parts.Count > 0)
                text += "\n将应用：" + string.Join("，", parts.ToArray());
            return text;
        }

        private void RefreshReloadTooltip()
        {
            // 「重载」的提示随待应用的背景/关卡变，直接改写已挂上的提示文案。
            PanelTooltips.SetText(cReload, ReloadTooltip());
        }

        protected override void OnRefresh()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;

            // ---- 作弊开关：复用原版 /cheat 的那四个 debug buff ----
            SyncToggle(cGodMode, LevelFacade.HasBuff(level, LevelFacade.BuffGodMode),
                v => LevelFacade.SetBuff(level, LevelFacade.BuffGodMode, v));
            SyncToggle(cEnergy, LevelFacade.HasBuff(level, LevelFacade.BuffEnergy),
                v => LevelFacade.SetBuff(level, LevelFacade.BuffEnergy, v));
            SyncToggle(cStarshard, LevelFacade.HasBuff(level, LevelFacade.BuffStarshard),
                v => LevelFacade.SetBuff(level, LevelFacade.BuffStarshard, v));
            SyncToggle(cNoRecharge, LevelFacade.HasBuff(level, LevelFacade.BuffNoRecharge),
                v => LevelFacade.SetBuff(level, LevelFacade.BuffNoRecharge, v));

            // ---- 后台暂停：直接读写原版全局选项，与游戏设置界面同一个开关 ----
            SyncToggle(cPause, PanelPause.Enabled, v => PanelPause.SetEnabled(v));

            // ---- 其它关卡属性开关 ----
            SyncToggle(cHugeWave, LevelFacade.GetIgnoreHugeWave(level), v => LevelFacade.SetIgnoreHugeWave(level, v));
            SyncToggle(cAutoCollect, LevelFacade.GetAutoCollect(level), v => LevelFacade.SetAutoCollect(level, v));
            SyncToggle(cHideGui, LevelHud.Hidden, v => LevelHud.Set(v));

            // ---- 背景 / 关卡：只记「待应用」，点重载才生效 ----
            SyncDropdown(cArea, LevelFacade.IndexOf(areas, LevelFacade.GetAreaID(level)), idx =>
            {
                pendingArea = SafeAt(areas, idx);
                RefreshReloadTooltip();
            });
            SyncDropdown(cStage, LevelFacade.IndexOf(stages, LevelFacade.GetStageID(level)), idx =>
            {
                pendingStage = SafeAt(stages, idx);
                RefreshReloadTooltip();
            });

            // ---- 难度 / 音乐：即时生效 ----
            SyncDropdown(cDifficulty, LevelFacade.IndexOf(difficulties, LevelFacade.GetDifficulty(level)), idx =>
            {
                var id = SafeAt(difficulties, idx);
                if (id != null)
                    LevelFacade.ApplyDifficulty(level, id);
            });
            SyncDropdown(cMusic, LevelFacade.IndexOf(musics, LevelFacade.GetMusicID(level)), idx =>
            {
                var id = SafeAt(musics, idx);
                if (id != null)
                    LevelFacade.SetMusicID(level, id);
            });

            // ---- 数值 ----
            SyncNumber(cFlag, LevelFacade.GetCurrentFlag(level).ToString(), v => LevelFacade.SetCurrentFlag(level, (int)v));
            SyncNumber(cWave, LevelFacade.GetCurrentWave(level).ToString(), v => LevelFacade.SetCurrentWave(level, (int)v));
            SyncNumber(cMaxEnergy, LevelFacade.GetMaxEnergy(level).ToString(), v => LevelFacade.SetMaxEnergy(level, (int)v));
            SyncNumber(cStarshardSlots, LevelFacade.GetStarshardSlots(level).ToString(), v => LevelFacade.SetStarshardSlots(level, (int)v));
            SyncNumber(cSpawnAdd, Fmt(LevelFacade.GetSpawnPointAddition(level)), v => LevelFacade.SetSpawnPointAddition(level, v));
            SyncNumber(cSpawnMul, Fmt(LevelFacade.GetSpawnPointMultiplier(level)), v => LevelFacade.SetSpawnPointMultiplier(level, v));
            SyncNumber(cSpawnPow, Fmt(LevelFacade.GetSpawnPointPower(level)), v => LevelFacade.SetSpawnPointPower(level, v));
        }

        protected override void OnLeaveCore()
        {
            // 关闭/切页时输入框不会失焦，所以把还没提交的数值补提交一次。
            var level = LevelFacade.Current;
            if (level == null)
                return;
            ApplyAllNumbers((id, value) => ApplyNumberById(level, id, value));
        }

        private void ApplyNumberById(LevelEngine level, string id, long value)
        {
            switch (id)
            {
                case "flag": LevelFacade.SetCurrentFlag(level, (int)value); break;
                case "wave": LevelFacade.SetCurrentWave(level, (int)value); break;
                case "maxenergy": LevelFacade.SetMaxEnergy(level, (int)value); break;
                case "starshardSlots": LevelFacade.SetStarshardSlots(level, (int)value); break;
                case "spawnadd": LevelFacade.SetSpawnPointAddition(level, value); break;
                case "spawnmul": LevelFacade.SetSpawnPointMultiplier(level, value); break;
                case "spawnpow": LevelFacade.SetSpawnPointPower(level, value); break;
            }
        }

        private void OnReloadClicked()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            if (pendingArea == null)
                pendingArea = LevelFacade.GetAreaID(level);
            if (pendingStage == null)
                pendingStage = LevelFacade.GetStageID(level);
            // 重载会重建整个关卡（面板是场景对象的子节点，会一起被销毁），
            // 所以先关掉，避免留下悬垂引用。
            if (Shell != null)
                Shell.Close();
            LevelFacade.ReloadLevel(pendingArea, pendingStage);
        }
    }
}
