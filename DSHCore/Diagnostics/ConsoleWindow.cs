using System;
using System.Runtime.InteropServices;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 隐藏 / 恢复 BepInEx 控制台窗口。
    ///
    /// <para><b>这是回答「能不能隐藏日志窗口」的两种方案之一</b>：</para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>方案 A（推荐，零代码）</b>：把 <c>BepInEx/config/BepInEx.cfg</c> 里的
    /// <c>[Logging.Console] Enabled</c> 改成 <c>false</c> —— 窗口根本不会被创建，
    /// 也就不会有「一闪而过」。日志照旧写 <c>LogOutput.log</c>，并且照旧进网页查看器。
    /// </description></item>
    /// <item><description>
    /// <b>方案 B（本类）</b>：窗口正常创建（能看到 BepInEx 早期启动日志），
    /// 等插件加载完、网页查看器就绪之后再把它藏起来。适合「想留个后路」的场景 ——
    /// 出问题时可以随时从查看器里点一下把窗口叫回来。
    /// </description></item>
    /// </list>
    ///
    /// <para><b>安全约束（很重要）</b>：如果游戏是从一个已有终端（cmd / PowerShell /
    /// Windows Terminal）里启动的，BepInEx 不会新建控制台，而是附着到父窗口上；
    /// 此时 <c>GetConsoleWindow()</c> 返回的是**那个终端**的窗口。把它藏起来等于
    /// 把用户自己的终端弄没了。所以 <see cref="OwnsConsole"/> 会比对窗口的所属进程，
    /// 只有「这个控制台窗口确实属于我们进程」时才允许隐藏。</para>
    /// </summary>
    internal static class ConsoleWindow
    {
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        /// <summary>本进程是否有可操作的控制台窗口（无窗口 / 无桌面会话时为 false）。</summary>
        public static bool IsAvailable => GetConsoleWindow() != IntPtr.Zero;

        /// <summary>当前是否可见。</summary>
        public static bool IsVisible
        {
            get
            {
                var hwnd = GetConsoleWindow();
                return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
            }
        }

        /// <summary>
        /// 控制台窗口是否属于**本进程**。
        /// 为 false 说明它是父进程（用户的终端）的窗口，此时任何隐藏操作都会被拒绝。
        /// </summary>
        public static bool OwnsConsole
        {
            get
            {
                var hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero)
                    return false;

                uint ownerPid;
                GetWindowThreadProcessId(hwnd, out ownerPid);
                return ownerPid == (uint)Environment.ProcessId;
            }
        }

        /// <summary>能否隐藏（有窗口 且 是自己的窗口）。</summary>
        public static bool CanHide => IsAvailable && OwnsConsole;

        /// <summary>
        /// 隐藏控制台窗口。返回是否真的执行了。
        /// 不是自己的窗口时返回 false 并且**什么都不做**（见类注释的安全约束）。
        /// </summary>
        public static bool Hide()
        {
            if (!CanHide)
                return false;

            ShowWindow(GetConsoleWindow(), SW_HIDE);
            return true;
        }

        /// <summary>恢复显示控制台窗口。返回是否真的执行了。</summary>
        public static bool Show()
        {
            if (!CanHide)
                return false;

            ShowWindow(GetConsoleWindow(), SW_SHOW);
            return true;
        }

        /// <summary>在显示 / 隐藏之间切换，返回切换后是否可见。</summary>
        public static bool Toggle()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
            }
            return IsVisible;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
