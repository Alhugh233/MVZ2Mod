using DSHCore.Ui;
using MVZ2Logic.Localization;

namespace ModularCurse
{
    /// <summary>
    /// 本 mod 的全部用户可见文案（<b>多语言的唯一真源</b>）。
    ///
    /// <para>两种情况分开登记，这是 DSHCore 那套本地化接口的核心规则：</para>
    /// <list type="number">
    /// <item><b>游戏内容名</b>（诅咒名 / 诅咒说明 / 关卡名…）—— 游戏自己会用
    /// <c>_p(上下文, 名字)</c> 去查，所以登记时**必须带上下文**
    /// （<see cref="LogicStrings.CONTEXT_CURSE_NAME"/> 等）。
    /// DSHCore 已经把那两条查找链都挂上了，所以这里登记完，
    /// 游戏里的诅咒面板、悬停提示、关卡选择列表会**自动**跟着语言变。</item>
    /// <item><b>界面文字</b>（本 mod 自己画的按钮/提示）—— 不带上下文，
    /// 且由 DSHCore 的 UI 辅助函数（<c>UiKit.Label</c> / <c>UiOptionRow</c> …）
    /// 在画的时候自动取词。</item>
    /// </list>
    ///
    /// <para><b>键必须与 XML / 代码里的源串逐字符一致</b> ——
    /// 诅咒名里带 <c>&lt;color=#ffffff&gt;</c> 这类富文本标记的，标记也是键的一部分
    /// （游戏是把整串拿去做查找的），漏了标记就查不到。</para>
    ///
    /// <para>玩家想加第三种语言（或改这里的任何一句）不用改 mod：见 DSHCore 的
    /// <c>BepInEx/config/DSHCore/lang/&lt;语言码&gt;.txt</c>（<c>/dshtrans</c> 指令）。</para>
    /// </summary>
    internal static class CurseLocale
    {
        private static readonly UiLocaleTable T = UiLocale.Table("modular_curse");

        /// <summary>在 <c>Plugin.Load()</c> 里调一次（早于任何界面/关卡取词）。</summary>
        public static void Register()
        {
            Mod();

            CurseNames();
            CurseTooltips();

            PoolUi();
        }

        // ------------------------------------------------------------------
        // mod 自述（进 DSHCore 的「模组设置」页左栏 + 右栏「说明」）
        // ------------------------------------------------------------------

        private static void Mod()
        {
            // 显示名的源串是英文名（English 那半边由 UiOptions.DeclareMod 的 chinese 参数负责）
            T.En("模块化诅咒（QC 池 + SC 池）",
                "Modular curses (QC / SC / ZY pools)");
        }

        // ------------------------------------------------------------------
        // 诅咒名（44 条；上下文 = curse.name）
        // ------------------------------------------------------------------

        private static void CurseNames()
        {
            T.EnAllWith(LogicStrings.CONTEXT_CURSE_NAME,
                "怪物的大脑", "The Creature's Brain",
                "梦魇的触手", "Nightmare's Tentacle",
                "凋灵的头颅", "Wither's Skull",
                "巨人的肉块", "The Giant's Flesh",
                "红龙的鳞片", "Red Dragon's Scale",
                "上锁箱子的锁链", "Locked Chest's Chains",
                "克拉肯的护板", "Kraken's Backplate",
                "QC-C8", "QC-C8",
                "QC-C9", "QC-C9",
                "QC-C10", "QC-C10",
                "QC-C11", "QC-C11",
                "QC-C12", "QC-C12",
                "怪人的肋骨", "Freak's Ribs",
                "梦魇碎片", "Nightmare Fragment",
                "恶咒之星", "Hex Star",
                "血肉核心", "Flesh Core",
                "龙头骨", "Dragon Skull",
                "折断的钥匙", "Broken Key",
                "钢铁吸盘", "Steel Sucker",
                "再生藤蔓", "Regrowing Vines",
                "古代历石", "Ancient Calendar Stone",
                "灾厄链甲", "Calamity Chainmail",
                "权衡之杖", "Scales of Judgement",
                "黄铜齿轮", "Brass Gear",
                "<color=#FF4040>永恒之心</color>", "<color=#FF4040>Eternal Heart</color>",
                "<color=#FF4040>命运之瓶</color>", "<color=#FF4040>Bottle of Fate</color>",
                "<color=#FF4040>强固补丁</color>", "<color=#FF4040>Hardening Patch</color>",
                "<color=#FF4040>超缓冲胶</color>", "<color=#FF4040>Super Buffer Gel</color>",
                "<color=#FF4040>趋光性</color>", "<color=#FF4040>Phototaxis</color>",
                "<color=#FF4040>夜长梦多</color>", "<color=#FF4040>Long Night</color>",
                "<color=#FF4040>地狱残响</color>", "<color=#FF4040>Hellish Echo</color>",
                "<color=#FF4040>排异反应</color>", "<color=#FF4040>Rejection</color>",
                "<color=#00FF00>斋禁</color>", "<color=#00FF00>Fast</color>",
                "<color=#00FF00>磨灭</color>", "<color=#00FF00>Obliteration</color>",
                "<color=#00FF00>室上性心动过速</color>", "<color=#00FF00>Supraventricular Tachycardia</color>",
                "<color=#00FF00>我们不去幻想乡</color>", "<color=#00FF00>We Don't Go to Gensokyo</color>",
                "<color=#00B0FF>它回来了</color>", "<color=#00B0FF>It Came Back</color>",
                "<color=#00B0FF>病毒式传播</color>", "<color=#00B0FF>Viral Spread</color>",
                "<color=#00B0FF>衰变</color>", "<color=#00B0FF>Decay</color>",
                "<color=#00B0FF>久经沙场</color>", "<color=#00B0FF>Battle-Hardened</color>",
                "纯粹", "Pure",
                "恶意", "Malice",
                "晚安", "Good Night",
                "抗拒", "Resistance");
        }

        // ------------------------------------------------------------------
        // 诅咒说明（上下文 = curse.tooltip）
        // ------------------------------------------------------------------

        private static void CurseTooltips()
        {
            T.EnAllWith(LogicStrings.CONTEXT_CURSE_TOOLTIP,
                "器械受到伤害时漏电 3s。",
                "Contraptions leak electricity for 3s when damaged.",
                "镐子和星之碎片各自具有 3s 冷却时间。",
                "The pickaxe and starshards each have a 3s cooldown.",
                "器械被星之碎片激发后凋零 27s。",
                "Contraptions evoked by a starshard wither for 27s.",
                "有尸体的敌怪倒下时有 25% 概率复活（BOSS 除外）。",
                "Enemies that leave a corpse have a 25% chance to revive when killed (bosses excluded).",
                "怪物和器械死亡时留下火焰。",
                "Enemies and contraptions leave fire behind when they die.",
                "怪物有 10% 概率隐形。",
                "Enemies have a 10% chance to be invisible.",
                "很长的冷却时间变为 75s。",
                "Very long cooldowns become 75s.",
                "怪物的出场时间提前 5s。",
                "Enemies spawn 5s earlier.",
                "器械子弹的速度变为 1/3。",
                "Contraption projectiles travel at 1/3 speed.",
                "重质量怪物可以阻挡穿透伤害。",
                "Heavy enemies can block piercing damage.",
                "每一大波所有器械价格上涨 50。",
                "Every huge wave raises the cost of all contraptions by 50.",
                "关卡开始时在第三列随机一路生成一个不给予星碎的借贷告示牌。",
                "When the level starts, a loan sign that grants no starshards appears in a random lane of the third column.",
                "第 10 个同种器械后，每多 3 个使该种器械的花费 +20%（13 个 → 120%，16 个 → 140%）。",
                "After the 10th contraption of a kind, every 3 more increase that kind's cost by 20% (13 → 120%, 16 → 140%).",
                "无可激活或忠诚标签的器械放下时叛变 2s（御柱 / 冲击活塞 无视此诅咒）。",
                "Contraptions without the evocable or loyal tag defect for 2s when placed (pillars and impact pistons ignore this curse).",
                "第 10 波后，每个敌人死亡时使所有非免控器械眩晕 0.1s（可叠加）。视为亡语。",
                "After wave 10, each enemy death stuns all stun-vulnerable contraptions for 0.1s (stacking). Counts as a death effect.",
                "敌人的本体（不包括防具，BOSS 除外）每 4s 恢复 5% 的生命（每次最多 100 点）。",
                "Enemy bodies (not armor, bosses excluded) recover 5% health every 4s (at most 100 per tick).",
                "敌人的防具耐久 ×1.3。",
                "Enemy armor durability ×1.3.",
                "每使用 3 个碎片使下 1 个碎片无效。",
                "Every 3 starshards used makes the next starshard useless.",
                "敌人受到的击退量减半，接触地面的敌人重量至少为重。",
                "Knockback on enemies is halved, and ground-bound enemies count as at least heavy.",
                "第 10 波后，非无来源的敌人首次死亡时会复活至本体 15% 的血量（不包括防具，不触发亡语，也不消除尸体）。",
                "After wave 10, enemies that were not spawned from nothing revive at 15% body health the first time they die (armor excluded, no death effects, corpses remain).",
                "敌人受到的 debuff 累积时长（持有种类 × 经过秒数）超过 36s 时，12s 内无视所有 debuff —— 清除已有的 debuff 并拒绝再获取。",
                "When an enemy's accumulated debuff time (kinds held × seconds elapsed) exceeds 36s, it ignores debuffs for 12s — clearing existing ones and refusing new ones.",
                "非无来源的敌人出场后 10s 内不会死亡，血量至少保留 1 点。",
                "Enemies that were not spawned from nothing cannot die within 10s of appearing and keep at least 1 health.",
                "每行不在最前面那一格的敌人受伤 ×40%。",
                "Enemies that are not in the frontmost cell of their lane take only 40% damage.",
                "敌人生成间隔 ×90%（不影响第一波出怪）。",
                "Enemy spawn interval ×90% (the first wave is unaffected).",
                "第 10 波后，非无来源的敌人受到单次超出（本体血量上限 + 当前防具耐久）的伤害时不会立刻死亡，还能无敌并额外行动 3s。",
                "After wave 10, when an enemy that was not spawned from nothing takes a single hit exceeding (max body health + current armor durability), it survives, becomes invincible and acts for 3 more seconds.",
                "每 10 波时，禁用所有器械放置、镐子、星之碎片 15s。",
                "Every 10 waves, contraption placement, the pickaxe and starshards are all disabled for 15s.",
                "敌人每秒受到的伤害被限制为当前血量/防具耐久的 40% 或 600（取最大值）；防具吃不完的溢出伤害会结算到本体。",
                "Damage an enemy takes per second is capped at 40% of its current health/armor durability or 600, whichever is higher; overflow that armor cannot absorb is dealt to the body.",
                "敌人获得 5 的护甲（本体与防具受到的伤害都先减少 5 点），但最终结算时至少保留 1 点伤害。",
                "Enemies gain 5 armor (damage to body and armor is reduced by 5 first), but at least 1 damage always goes through.",
                "排除日光影响后，非夜行性器械在光照范围外 8s 后睡着。（无法被美梦丝入眠的器械不受影响）",
                "Ignoring sunlight, non-nocturnal contraptions fall asleep after 8s outside light range. (Contraptions that dream silk cannot put to sleep are unaffected.)",
                "每小波开始时，器械蓝图的冷却时间增加 2.5%（上限 50%）。",
                "At the start of every small wave, contraption blueprint cooldowns increase by 2.5% (up to 50%).",
                "矿场的生产间隔改为 30s。",
                "Mine production interval becomes 30s.",
                "拾取星之碎片后会阻止你使用星之碎片 12s，可叠加，无上限。",
                "Picking up a starshard prevents you from using starshards for 12s. Stacks without limit.",
                "每大波开始时，失去所有机械能，并失去 1 个星之碎片。",
                "At the start of every huge wave you lose all mechanical energy and 1 starshard.",
                "每大波开始时，从右往左永久禁用你 1 张器械蓝图。",
                "At the start of every huge wave, one of your contraption blueprints is permanently disabled, from right to left.",
                "第一大波开始前，波长最短为 16s。",
                "Before the first huge wave starts, the shortest wave length is 16s.",
                "倒数第二大波开始后，出怪量增加 200%（单格关卡不生效）。",
                "After the second-to-last huge wave starts, spawn count increases by 200% (no effect in single-cell levels).",
                "被消除亡语 / 坠空 / 落水的敌人重生为携带成真 buff 的恶魂。（被地雷击杀的敌人除外）",
                "Enemies removed by death-effect removal, falling or drowning are reborn as ghasts carrying the coming-true buff. (Enemies killed by landmines are excluded.)",
                "第 x 大波开始后，出怪点数低于 x 的敌人生成时会额外在其他行生成 1 个不占用出怪点数的复制。",
                "After huge wave x starts, enemies whose spawn cost is lower than x also spawn one free copy in another lane.",
                "所有友方爆炸不分敌我。",
                "All friendly explosions hit allies as well.",
                "敌人单次受到超过 80 的伤害后若未死亡，其之后单次受到的伤害不超过 20。",
                "After an enemy survives a single hit of more than 80 damage, later single hits deal at most 20.",
                "你不能携带任何制品。（放轻松。）",
                "You cannot carry any artifacts. (Relax.)",
                "第一大波开始前，器械的攻击间隔与生产间隔取最大值。（平时这种情况确实有可能发生。）",
                "Before the first huge wave starts, contraption attack and production intervals are set to their maximum. (This can genuinely happen on its own.)",
                "除了荧石以外的发光器械 ×50% 光照范围。（完全没有影响，对吧？）",
                "Glowing contraptions other than glowstone have ×50% light range. (No effect at all, right?)",
                "你使用鼠标点击器械时，其失去 10 点耐久。（他们很可爱。）",
                "Clicking a contraption with the mouse makes it lose 10 durability. (They're cute.)");
        }

        // ------------------------------------------------------------------
        // 诅咒池（选卡界面弹窗顶部那排条目 + 悬停提示 + 状态栏）
        // ------------------------------------------------------------------

        private static void PoolUi()
        {
            T.EnAll(
                "原版池", "Vanilla pool",
                "QC 池", "QC pool",
                "SC 池", "SC pool",
                "咒鵺兼程", "3q4a's pool",
                "原版诅咒", "Vanilla curses",
                "QoZnoS的诅咒", "Curses by QoZnoS",
                "奇硅箱的诅咒", "Curses by 奇硅箱",
                "3q4a的诅咒", "Curses by 3q4a",
                "按池过滤：", "Filtered by pool: ",
                "恢复按诅咒池过滤（", "Back to pool filtering (",
                "不限池", "All pools");
        }
    }
}
