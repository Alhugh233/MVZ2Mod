using System;
using System.Text;

namespace Replay
{
    /// <summary>
    /// <c>.mrep</c> 回放文件协议的**规范常量**。
    ///
    /// <para>规范文本见同目录 <c>PROTOCOL.md</c>（那是**唯一**的权威描述）。
    /// 本类只放程序要用到的常量与判定，不重复描述语义 —— 否则规范与实现迟早各说一套。</para>
    ///
    /// <para><b>两个版本号的区别（很容易混）：</b></para>
    /// <list type="bullet">
    /// <item><see cref="SpecVersion"/> = **规范文本**的版本。改一行字段的含义就要 +1，
    /// 哪怕字节格式没变。它只用于「这份文件是按第几版规范写的」这种人读信息。</item>
    /// <item><see cref="FormatVersion"/> = **文件结构**的版本（header 的 <c>ver</c>）。
    /// 读者**只据此**决定「能不能播 / 哪些字段该有」。加/删/改行结构才 +1。</item>
    /// </list>
    /// </summary>
    internal static class ReplayProtocol
    {
        /// <summary>header 第一行的 <c>magic</c>。不匹配就不是回放文件。</summary>
        public const string Magic = "mrep";

        /// <summary>
        /// 规范版本（<c>PROTOCOL.md</c> 的版本）。语义变化就 +1。
        /// 1 = 首版规范化协议（环境记录 / 调试模式 / 策略分离）。
        /// </summary>
        public const int SpecVersion = 1;

        /// <summary>
        /// 格式版本（header 的 <c>ver</c>）。
        ///
        /// <para>历史（10 之前见 <c>ReplayRecorder</c> 的版本表）：</para>
        /// <list type="bullet">
        /// <item>1..9 = 逐次加字段的历史版本（1 无位置、2 带位置、3 实体锚点、6 分层、
        /// 7 窗口尺寸、8 手持标记、9 指针轨迹 + 对话框 + 起始金币）。</item>
        /// <item>10 = **规范化协议**：header 加 <c>spec</c>/<c>unity</c>/<c>plat</c>/<c>tps</c>/
        /// <c>envHash</c>/<c>envCount</c>/<c>fpt</c>/<c>debug</c>/<c>caps</c>/<c>samplers</c>；
        /// 新增 <c>env</c>（环境）与 <c>mod</c>（已装 mod，每个一行）两种记录；
        /// <c>fp</c> 行允许携带任意**附加字段**（调试模式用得最多）。</item>
        /// </list>
        /// 老文件（&lt; 10）照样能播：缺的 header 字段一律按「旧行为」取值，
        /// 缺的记录类型跳过。<b>格式版本只增不改</b>，字段只加不删含义。
        /// </summary>
        public const int FormatVersion = 10;

        /// <summary>能读的最低格式版本。</summary>
        public const int MinReadableVersion = 1;

        /// <summary>能读的最高格式版本。高于它的文件要求玩家升级 mod。</summary>
        public const int MaxReadableVersion = FormatVersion;

        // ------------------------------------------------------------------
        // 记录类型（每行的 "k"）
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>env</c>：环境记录。**一类两形**，靠有没有 <c>id</c> 区分：
        /// <list type="bullet">
        /// <item><c>{"k":"env","t":"summary",...}</c> = 环境摘要（游戏 / 引擎 / 平台 / 时间轴单位 / 哈希）。整份文件一行。</item>
        /// <item><c>{"k":"env","kind":"content","id":"...","ver":"...","dv":4}</c> = 一个环境条目（本体 / 内容 mod / 插件 / 运行环境）。每个一行。</item>
        /// </list>
        /// </summary>
        public const string KindEnv = "env";

        /// <summary><c>keyframe</c>：起始关键帧（关卡初始状态的完整序列化）。整份文件一行。</summary>
        public const string KindKeyframe = "keyframe";

        /// <summary><c>fp</c>：状态指纹。这是「回放是否真的复现了实战」的**唯一**判据。</summary>
        public const string KindFingerprint = "fp";

        /// <summary><c>ptr</c>：逐 tick 指针轨迹采样（可选特性 <see cref="CapPointer"/>）。</summary>
        public const string KindPointer = "ptr";

        /// <summary><c>dlg</c>：选项对话框与玩家的选择（可选特性 <see cref="CapDialog"/>）。</summary>
        public const string KindDialog = "dlg";

        // ------------------------------------------------------------------
        // 能力标记（header 的 caps，逗号分隔）
        //
        // 「本文件用到了哪些可选特性」。读者据此知道「要不要去找这类记录」，
        // 而不用先扫一遍全文。**未知的能力标记必须被忽略**（向前兼容）。
        // ------------------------------------------------------------------

        /// <summary>文件里有 <c>ptr</c> 记录（逐 tick 指针轨迹）。</summary>
        public const string CapPointer = "ptr";

        /// <summary>文件里有 <c>dlg</c> 记录（选项对话框）。</summary>
        public const string CapDialog = "dlg";

        /// <summary>动作事件里带 <c>itg</c>/<c>evk</c>（手持蓝图的立即触发 / 激发标记）。</summary>
        public const string CapHeldFlags = "hfl";

        /// <summary>header 带录制时的窗口尺寸（<c>sw</c>/<c>sh</c>），播放时换算屏幕坐标。</summary>
        public const string CapViewport = "vp";

        /// <summary>这是**调试模式**文件：<c>fp</c> 行带全部附加字段、逐 tick 采样。</summary>
        public const string CapDebug = "dbg";

        // ------------------------------------------------------------------
        // 指纹行的键
        //
        // 内置键一律 2 个小写字母；第三方采样器一律用 FpExtraPrefix 前缀，
        // 这样以后协议加字段时不会撞名。
        // ------------------------------------------------------------------

        /// <summary>【调试】7 条 RNG 流的原始 int32 状态。</summary>
        public const string FpRawRng = "rx";

        /// <summary>【调试】每个实体的明细（id:定义:血量:坐标:分层）。</summary>
        public const string FpEntityDetail = "ei";

        /// <summary>【调试】关卡序列化里的内部标量。</summary>
        public const string FpLevelScalars = "lk";

        /// <summary>第三方采样器自定义字段的**强制**前缀。</summary>
        public const string FpExtraPrefix = "x_";

        // ------------------------------------------------------------------
        // 环境条目的种类（env 行的 kind）
        // ------------------------------------------------------------------

        /// <summary>游戏本体（<c>mvz2</c>）。它的 <c>dv</c> = <c>LevelManager.CURRENT_DATA_VERSION</c>。</summary>
        public const string ModBuiltin = "builtin";

        /// <summary>游戏内容 mod（走官方 <c>ModManager</c>，有自己的关卡数据版本）。</summary>
        public const string ModContent = "content";

        /// <summary>BepInEx 插件（只有 GUID / 名称 / 版本，不参与关卡存档兼容判定）。</summary>
        public const string ModPlugin = "plugin";

        /// <summary>运行环境（引擎版本、进程位数、时间轴单位…）。不参与内容兼容判定，但参与环境哈希。</summary>
        public const string ModRuntime = "runtime";

        // ------------------------------------------------------------------
        // 判定
        // ------------------------------------------------------------------

        /// <summary>这个格式版本本 mod 能不能读。</summary>
        public static bool CanRead(int version)
        {
            return version >= MinReadableVersion && version <= MaxReadableVersion;
        }

        /// <summary>把「能不能读」说成一句人话（写进播放小结 / 日志）。</summary>
        public static string VersionPolicy(int version)
        {
            if (version <= 0)
                return "文件没有版本号（可能是别的工具写的），无法安全读取";
            if (version < MinReadableVersion)
                return "格式版本 " + version + " 太旧（本 mod 最低支持 " + MinReadableVersion + "）";
            if (version > MaxReadableVersion)
                return "格式版本 " + version + " 比本 mod 支持的（最高 " + MaxReadableVersion
                    + "）更新 —— 请升级回放 mod";
            if (version < FormatVersion)
                return "格式版本 " + version + "（当前 " + FormatVersion + "），缺的字段按旧行为处理";
            return "格式版本 " + version + "（当前）";
        }

        /// <summary>本文件**写出**时用的版本信息。</summary>
        public static string Describe()
        {
            return "协议 v" + SpecVersion + "／格式 v" + FormatVersion;
        }

        /// <summary>把能力标记拼成 header 的 <c>caps</c>（逗号分隔；空串 = 没有可选特性）。</summary>
        public static string JoinCaps(params string[] caps)
        {
            if (caps == null || caps.Length == 0)
                return string.Empty;
            var sb = new StringBuilder(32);
            for (int i = 0; i < caps.Length; i++)
            {
                if (string.IsNullOrEmpty(caps[i]))
                    continue;
                if (sb.Length > 0)
                    sb.Append(',');
                sb.Append(caps[i]);
            }
            return sb.ToString();
        }

        /// <summary>header 的 <c>caps</c> 里有没有某一项。</summary>
        public static bool HasCap(string caps, string cap)
        {
            if (string.IsNullOrEmpty(caps) || string.IsNullOrEmpty(cap))
                return false;
            return ("," + caps + ",").IndexOf("," + cap + ",", StringComparison.Ordinal) >= 0;
        }

        /// <summary>今天这一代「写文件」的 mod 支持的全部能力（用于和读到的 caps 比对）。</summary>
        public static readonly string[] AllCaps =
        {
            CapPointer, CapDialog, CapHeldFlags, CapViewport, CapDebug,
        };
    }
}
