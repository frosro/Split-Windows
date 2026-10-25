// UpDownSplit - 上下分屏助手
// 常驻托盘的小工具：支持"分屏设置"窗口（选显示器 → 选布局和窗口 → 确定分屏）
// 以及快捷键快速贴靠。多显示器（横竖混搭）按窗口所在屏各自计算。
// 快捷键:
//   Win+Ctrl+↑   当前窗口贴到上半屏
//   Win+Ctrl+↓   当前窗口贴到下半屏
//   Win+Ctrl+S   一键上下分屏（当前窗口放上，下一个窗口放下）
//   Win+Ctrl+R   还原上次移动的窗口
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UpDownSplit
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Native.SetDpiAwareness();

            // 命令行模式（供测试/脚本调用）
            if (args.Length > 0) return RunCli(args);

            bool createdNew;
            Mutex mutex = new Mutex(true, "UpDownSplit::SingleInstance", out createdNew);
            if (!createdNew) return 0; // 已有实例在运行

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try
                {
                    MessageBox.Show("发生错误：" + e.Exception.Message, "上下分屏助手",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            using (TrayApp app = new TrayApp())
            {
                Application.Run(app);
            }
            GC.KeepAlive(mutex);
            return 0;
        }

        private static IntPtr ParseHwnd(string s)
        {
            long v;
            return long.TryParse(s, out v) ? new IntPtr(v) : IntPtr.Zero;
        }

        // place/testundo 参数: <hwnd> <x> <y> <width> <height>
        private static bool ParseRect(string[] a, int start, out Native.RECT r)
        {
            r = new Native.RECT();
            int x, y, w, h;
            if (a.Length < start + 4) return false;
            if (!int.TryParse(a[start], out x) || !int.TryParse(a[start + 1], out y) ||
                !int.TryParse(a[start + 2], out w) || !int.TryParse(a[start + 3], out h)) return false;
            r.L = x; r.T = y; r.R = x + w; r.B = y + h;
            return true;
        }

        private static int RunCli(string[] args)
        {
            WindowMover mover = new WindowMover();
            switch (args[0].TrimStart('-', '/').ToLowerInvariant())
            {
                case "top":
                    if (args.Length > 1) { IntPtr h = ParseHwnd(args[1]); if (h != IntPtr.Zero) return mover.Place(h, Half.Top) ? 0 : 1; }
                    return mover.PlaceForeground(Half.Top) ? 0 : 1;
                case "bottom":
                    if (args.Length > 1) { IntPtr h = ParseHwnd(args[1]); if (h != IntPtr.Zero) return mover.Place(h, Half.Bottom) ? 0 : 1; }
                    return mover.PlaceForeground(Half.Bottom) ? 0 : 1;
                case "split":
                {
                    if (args.Length > 1)
                    {
                        IntPtr a = ParseHwnd(args[1]);
                        if (a != IntPtr.Zero)
                        {
                            if (args.Length > 2)
                            {
                                IntPtr b = ParseHwnd(args[2]);
                                if (b != IntPtr.Zero) return mover.SplitWindows(a, b) ? 0 : 1;
                            }
                            return mover.SplitWindow(a) ? 0 : 1;
                        }
                    }
                    return mover.SplitTopBottom() ? 0 : 1;
                }
                case "restore":
                    mover.RestoreLast(); return 0;
                case "place":
                {
                    Native.RECT r;
                    IntPtr h = args.Length > 1 ? ParseHwnd(args[1]) : IntPtr.Zero;
                    if (h == IntPtr.Zero || !ParseRect(args, 2, out r)) return 1;
                    return mover.Place(h, r) ? 0 : 1;
                }
                case "testundo": // 摆位后立即还原，用于验证 undo 逻辑
                {
                    Native.RECT r;
                    IntPtr h = args.Length > 1 ? ParseHwnd(args[1]) : IntPtr.Zero;
                    if (h == IntPtr.Zero || !ParseRect(args, 2, out r)) return 1;
                    if (!mover.Place(h, r)) return 1;
                    mover.RestoreLast();
                    return 0;
                }
                case "listwindows": // listwindows <输出文件>
                {
                    if (args.Length < 2) return 1;
                    StringBuilder sb = new StringBuilder();
                    foreach (WindowInfo w in Native.ListWindows())
                        sb.Append(w.Handle.ToInt64()).Append('|').Append(w.ProcessName).Append('|')
                          .Append(w.Title).Append('\n');
                    try { File.WriteAllText(args[1], sb.ToString(), Encoding.UTF8); return 0; }
                    catch (Exception ex) { Console.Error.WriteLine("listwindows 失败：" + ex.Message); return 1; }
                }
                default:
                    return 1;
            }
        }
    }

    internal enum Half { Top, Bottom }

    internal sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string ProcessName;
        public bool Minimized;

        public static readonly WindowInfo None = new WindowInfo();

        public override string ToString()
        {
            if (Handle == IntPtr.Zero) return "(不指定)";
            string s = Title + " — " + ProcessName;
            return Minimized ? s + "（已最小化）" : s;
        }
    }

    internal static class Native
    {
        public const int MOD_WIN = 0x8, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        public const int WM_HOTKEY = 0x0312;
        public const int SW_RESTORE = 9;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint GW_HWNDNEXT = 3;
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L; public int T; public int R; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        public static extern int DwmGetWindowAttributeRect(IntPtr hWnd, int attr, ref RECT r, int size);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder path, ref uint size);

        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        public static void SetDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(PerMonitorV2)) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        private static bool IsAltTabStyle(IntPtr h)
        {
            return IsWindowVisible(h)
                && GetWindowTextLength(h) > 0
                && (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
                && (GetWindowLong(h, GWL_STYLE) & WS_MINIMIZEBOX) != 0
                && !IsCloaked(h);
        }

        // 列出当前所有可摆位的窗口（按 Z 序从上到下，含最小化窗口）
        public static List<WindowInfo> ListWindows()
        {
            List<WindowInfo> list = new List<WindowInfo>();
            Dictionary<int, string> pidNames = new Dictionary<int, string>(); // pid→进程名缓存，避免重复查询
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsAltTabStyle(h))
                {
                    StringBuilder sb = new StringBuilder(256);
                    GetWindowText(h, sb, sb.Capacity);
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    int key = (int)pid;
                    string pn;
                    if (!pidNames.TryGetValue(key, out pn)) { pn = GetProcessName(pid); pidNames[key] = pn; }
                    WindowInfo wi = new WindowInfo();
                    wi.Handle = h;
                    wi.Title = sb.ToString();
                    wi.ProcessName = pn;
                    wi.Minimized = IsIconic(h);
                    list.Add(wi);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // 直接用 OpenProcess + QueryFullProcessImageName 取进程名。
        // 不用 Process.GetProcessById：它每次都会全表扫一遍进程，窗口多时设置窗口会明显卡顿。
        private static string GetProcessName(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                return Path.GetFileNameWithoutExtension(sb.ToString(0, (int)size));
            }
            finally { CloseHandle(h); }
        }

        // 从 fg 往下按 Z 序找第一个"像 Alt-Tab 候选"的窗口（同显示器、可见、未最小化）
        public static IntPtr NextAltTabWindow(IntPtr fg, IntPtr mon)
        {
            IntPtr h = GetWindow(fg, GW_HWNDNEXT);
            while (h != IntPtr.Zero)
            {
                if (h != fg
                    && IsAltTabStyle(h)
                    && !IsIconic(h)
                    && MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == mon)
                {
                    return h;
                }
                h = GetWindow(h, GW_HWNDNEXT);
            }
            return IntPtr.Zero;
        }

        private static bool IsCloaked(IntPtr h)
        {
            try
            {
                int c;
                return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out c, sizeof(int)) == 0 && c != 0;
            }
            catch { return false; }
        }

        // 窗口"可见"边框（DWM 扩展边框），不含不可见的调整边框
        public static bool GetVisibleRect(IntPtr h, out RECT r)
        {
            r = new RECT();
            try
            {
                return DwmGetWindowAttributeRect(h, DWMWA_EXTENDED_FRAME_BOUNDS, ref r,
                    Marshal.SizeOf(typeof(RECT))) == 0;
            }
            catch { return false; }
        }
    }

    // 极简 key=value 配置（%APPDATA%\UpDownSplit\config.txt），读不出来就当没有
    internal static class Config
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpDownSplit");
        private static readonly string FilePath = Path.Combine(Dir, "config.txt");
        private static readonly Dictionary<string, string> Values = Load();

        public static string Get(string key, string def)
        {
            string v;
            return Values.TryGetValue(key, out v) ? v : def;
        }

        public static int GetInt(string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(Get(key, null), out v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }

        public static void Set(string key, string value) { Values[key] = value; }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string[] lines = new string[Values.Count];
                int i = 0;
                foreach (KeyValuePair<string, string> kv in Values)
                    lines[i++] = kv.Key + "=" + kv.Value;
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch { } // 配置写不进去不影响分屏功能
        }

        private static Dictionary<string, string> Load()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        d[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch { }
            return d;
        }
    }

    internal sealed class WindowMover
    {
        private readonly List<KeyValuePair<IntPtr, Native.RECT>> _undo =
            new List<KeyValuePair<IntPtr, Native.RECT>>();

        // GUI 模式开启：摆位后由 Timer 延迟复核，纠正迟到的还原动画覆盖（CLI 无消息循环，只靠同步校验）
        public bool EnableDeferredChecks { get; set; }

        public bool PlaceForeground(Half half)
        {
            IntPtr fg = Native.GetForegroundWindow();
            return fg != IntPtr.Zero && Place(fg, half);
        }

        public bool SplitTopBottom()
        {
            return SplitWindow(Native.GetForegroundWindow());
        }

        public bool SplitWindow(IntPtr fg)
        {
            if (fg == IntPtr.Zero) return false;
            IntPtr mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
            IntPtr other = Native.NextAltTabWindow(fg, mon);
            return SplitWindows(fg, other);
        }

        // 一键分屏：当前窗上、下一窗下。找不到下面的窗口时仍贴上面的窗口，但整体返回 false
        public bool SplitWindows(IntPtr top, IntPtr bottom)
        {
            if (top == IntPtr.Zero) return false;
            bool ok = Place(top, Half.Top);
            if (bottom == IntPtr.Zero) return false;
            return Place(bottom, Half.Bottom) && ok;
        }

        public void RestoreLast()
        {
            while (_undo.Count > 0)
            {
                int last = _undo.Count - 1;
                KeyValuePair<IntPtr, Native.RECT> e = _undo[last];
                _undo.RemoveAt(last);
                if (!Native.IsWindow(e.Key)) continue;
                bool wasMinMax = Native.IsIconic(e.Key) || Native.IsZoomed(e.Key);
                if (wasMinMax)
                {
                    Native.ShowWindow(e.Key, Native.SW_RESTORE);
                    WaitRestore(e.Key);
                }
                Native.SetWindowPos(e.Key, IntPtr.Zero,
                    e.Value.L, e.Value.T, e.Value.R - e.Value.L, e.Value.B - e.Value.T, 0);
                if (wasMinMax) Settle(e.Key, e.Value, false);
                return;
            }
        }

        public bool Place(IntPtr h, Half half)
        {
            Native.RECT wa = WorkArea(h);
            Native.RECT a, b;
            SplitRects(wa, true, 50, out a, out b);
            return Place(h, half == Half.Top ? a : b);
        }

        // 通用摆位：把窗口放进指定矩形（先还原最大/最小化状态，记录 undo）
        public bool Place(IntPtr h, Native.RECT zone)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return false;
            bool wasMinMax = Native.IsIconic(h) || Native.IsZoomed(h);
            if (wasMinMax)
            {
                Native.ShowWindow(h, Native.SW_RESTORE);
                WaitRestore(h);
            }
            SaveUndo(h);
            SetWindowToZone(h, zone);
            if (wasMinMax) Settle(h, zone, true);
            return Native.IsWindow(h);
        }

        // Windows 10 窗口带一圈不可见的调整边框，SetWindowPos 以外框为准。
        // 为让可见边框正好贴合区域边缘，先测外框与可见边框的差值再做补偿（与系统贴靠做法一致）。
        private static void SetWindowToZone(IntPtr h, Native.RECT zone)
        {
            int lp = 0, tp = 0, rp = 0, bp = 0;
            Native.RECT outer, vis;
            if (Native.GetWindowRect(h, out outer) && Native.GetVisibleRect(h, out vis))
            {
                lp = vis.L - outer.L; tp = vis.T - outer.T;
                rp = outer.R - vis.R; bp = outer.B - vis.B;
                if (lp < 0 || tp < 0 || rp < 0 || bp < 0 || lp > 32 || tp > 32 || rp > 32 || bp > 32)
                    lp = tp = rp = bp = 0; // 异常值（无边框窗口等）不做补偿
            }
            Native.SetWindowPos(h, IntPtr.Zero, zone.L - lp, zone.T - tp,
                (zone.R - zone.L) + lp + rp, (zone.B - zone.T) + tp + bp, 0);
        }

        private static Native.RECT WorkArea(IntPtr h)
        {
            IntPtr mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(mon, ref mi);
            return mi.rcWork;
        }

        // 由屏幕尺寸定位其显示器的工作区（设置窗口用，和摆位同一数据源，避免两套算法不一致）
        public static Native.RECT WorkAreaAtCenter(Rectangle screen)
        {
            Native.POINT p = new Native.POINT();
            p.X = screen.Left + screen.Width / 2;
            p.Y = screen.Top + screen.Height / 2;
            IntPtr mon = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (Native.GetMonitorInfo(mon, ref mi)) return mi.rcWork;
            Native.RECT fallback = new Native.RECT();
            fallback.L = screen.Left; fallback.T = screen.Top;
            fallback.R = screen.Right; fallback.B = screen.Bottom;
            return fallback;
        }

        // 在工作区内按比例切出两块区域（vertical=true 上下切，ratio 为第一块的占比）
        public static void SplitRects(Native.RECT wa, bool vertical, int ratio, out Native.RECT a, out Native.RECT b)
        {
            a = wa; b = wa;
            if (vertical)
            {
                a.B = wa.T + (wa.B - wa.T) * ratio / 100;
                b.T = a.B;
            }
            else
            {
                a.R = wa.L + (wa.R - wa.L) * ratio / 100;
                b.L = a.R;
            }
        }

        private void SaveUndo(IntPtr h)
        {
            for (int i = 0; i < _undo.Count; i++)
                if (_undo[i].Key == h) return; // 只记录第一次移动前的位置
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            _undo.Add(new KeyValuePair<IntPtr, Native.RECT>(h, r));
            if (_undo.Count > 30) _undo.RemoveAt(0);
        }

        // ---- 摆位/还原后的落位校验：最大化/最小化的还原是异步动画，可能在我们 SetWindowPos
        // 之后才落地并覆盖位置。只对这类窗口做有界重试（普通窗口零等待），GUI 下再补一次延迟复核 ----

        private struct SettleCheck
        {
            public IntPtr Hwnd;
            public Native.RECT Zone; // Compensate=true 时为期望的可见边框，否则为期望的外框
            public bool Compensate;
            public int Ticks;
        }

        private void Settle(IntPtr h, Native.RECT zone, bool compensate)
        {
            SettleCheck c = new SettleCheck();
            c.Hwnd = h; c.Zone = zone; c.Compensate = compensate; c.Ticks = 2;
            for (int i = 0; i < 2 && Native.IsWindow(h); i++)
            {
                Thread.Sleep(40);
                if (IsSettled(c)) return;
                Reapply(c);
            }
            if (EnableDeferredChecks) ScheduleRecheck(c);
        }

        private static bool IsSettled(SettleCheck c)
        {
            Native.RECT cur;
            bool ok = c.Compensate ? Native.GetVisibleRect(c.Hwnd, out cur)
                                   : Native.GetWindowRect(c.Hwnd, out cur);
            if (!ok) return true; // 查不到就不折腾了
            return Math.Abs(cur.L - c.Zone.L) <= 4 && Math.Abs(cur.T - c.Zone.T) <= 4
                && Math.Abs(cur.R - c.Zone.R) <= 4 && Math.Abs(cur.B - c.Zone.B) <= 4;
        }

        private static void Reapply(SettleCheck c)
        {
            if (c.Compensate) SetWindowToZone(c.Hwnd, c.Zone);
            else Native.SetWindowPos(c.Hwnd, IntPtr.Zero,
                c.Zone.L, c.Zone.T, c.Zone.R - c.Zone.L, c.Zone.B - c.Zone.T, 0);
        }

        private static void WaitRestore(IntPtr h)
        {
            // ShowWindow(SW_RESTORE) 异步生效：轮询等状态切换，通常第一轮就通过
            for (int i = 0; i < 30; i++)
            {
                if (!Native.IsIconic(h) && !Native.IsZoomed(h)) return;
                Thread.Sleep(5);
            }
        }

        private readonly List<SettleCheck> _pending = new List<SettleCheck>();
        private System.Windows.Forms.Timer _recheckTimer;

        private void ScheduleRecheck(SettleCheck c)
        {
            if (_recheckTimer == null)
            {
                _recheckTimer = new System.Windows.Forms.Timer { Interval = 150 };
                _recheckTimer.Tick += delegate
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        SettleCheck p = _pending[i];
                        if (!Native.IsWindow(p.Hwnd) || IsSettled(p)) { _pending.RemoveAt(i); continue; }
                        Reapply(p);
                        if (--p.Ticks <= 0) _pending.RemoveAt(i); else _pending[i] = p;
                    }
                    if (_pending.Count == 0) _recheckTimer.Stop();
                };
            }
            _pending.Add(c);
            _recheckTimer.Start();
        }
    }

    // 开启双缓冲的 Panel，拖比例滑条时预览不闪烁
    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel() { DoubleBuffered = true; }
    }

    internal sealed class ScreenCard
    {
        public Screen Screen;
        public int Index;
        public ComboBox Layout;
        public TrackBar Ratio;
        public Label RatioLabel;
        public ComboBox[] Zones;
        public Label[] ZoneLabels;
    }

    // 分屏设置窗口：显示器预览 + 每屏布局/比例/窗口选择 + 确定分屏
    internal sealed class SplitConfigForm : Form
    {
        private readonly WindowMover _mover;
        private readonly List<ScreenCard> _cards = new List<ScreenCard>();
        private List<WindowInfo> _windows = new List<WindowInfo>();
        private Panel _preview;
        private Label _winCount;
        private readonly Font _small = new Font("Microsoft YaHei UI", 9F);

        public SplitConfigForm(WindowMover mover)
        {
            _mover = mover;
            Text = "分屏设置 - 上下分屏助手";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 640);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ShowInTaskbar = true;

            // 底部按钮（先加，Dock 处理顺序原因）
            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.Dock = DockStyle.Bottom;
            btns.Height = 56;
            btns.FlowDirection = FlowDirection.RightToLeft;
            btns.Padding = new Padding(10, 10, 10, 10);
            Button ok = new Button();
            ok.Text = "确定分屏"; ok.Width = 110; ok.Height = 34;
            ok.Click += delegate { Apply(); };
            Button undo = new Button();
            undo.Text = "还原本次移动"; undo.Width = 110; undo.Height = 34;
            undo.Click += delegate { _mover.RestoreLast(); };
            Button cancel = new Button();
            cancel.Text = "取消"; cancel.Width = 80; cancel.Height = 34;
            cancel.Click += delegate { Close(); };
            btns.Controls.Add(ok);
            btns.Controls.Add(undo);
            btns.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            // 每屏配置卡容器
            FlowLayoutPanel cardHost = new FlowLayoutPanel();
            cardHost.Dock = DockStyle.Fill;
            cardHost.FlowDirection = FlowDirection.LeftToRight;
            cardHost.WrapContents = true;
            cardHost.AutoScroll = true;
            cardHost.Padding = new Padding(10, 6, 10, 6);

            // 显示器预览
            _preview = new BufferedPanel();
            _preview.Dock = DockStyle.Top;
            _preview.Height = 210;
            _preview.BackColor = Color.White;
            _preview.Paint += PreviewPaint;

            // 顶部工具条
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = 40;
            bar.Padding = new Padding(10, 6, 10, 4);
            Button refresh = new Button();
            refresh.Text = "重新扫描窗口";
            refresh.AutoSize = true;
            refresh.Click += delegate { Guard(RefreshWindows); };
            _winCount = new Label();
            _winCount.AutoSize = true;
            _winCount.Padding = new Padding(12, 8, 0, 0);
            bar.Controls.Add(refresh);
            bar.Controls.Add(_winCount);

            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                cardHost.Controls.Add(BuildCard(screens[i], i));

            Controls.Add(cardHost);
            Controls.Add(_preview);
            Controls.Add(bar);
            Controls.Add(btns);

            RefreshWindows();
            UpdateRatioUi();

            // 关窗时记住每块屏的布局和比例，下次打开自动填入
            FormClosed += delegate
            {
                foreach (ScreenCard c in _cards)
                {
                    Config.Set(MonitorKey(c.Screen) + "|layout", c.Layout.SelectedIndex.ToString());
                    Config.Set(MonitorKey(c.Screen) + "|ratio", c.Ratio.Value.ToString());
                }
                Config.Save();
            };
        }

        // 操作兜底：用户点的按钮不能让程序崩掉
        private void Guard(Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败：" + ex.Message, "分屏设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string MonitorKey(Screen s)
        {
            return "m|" + s.DeviceName + "|" + s.Bounds.Width + "x" + s.Bounds.Height;
        }

        private Control BuildCard(Screen s, int idx)
        {
            bool portrait = s.Bounds.Height >= s.Bounds.Width;

            Panel card = new Panel();
            card.Size = new Size(420, 205);
            card.BorderStyle = BorderStyle.FixedSingle;
            card.Margin = new Padding(10);

            Label title = new Label();
            title.Text = string.Format("显示器 {0}（{1} {2}×{3}{4}）",
                idx + 1, portrait ? "竖屏" : "横屏", s.Bounds.Width, s.Bounds.Height,
                s.Primary ? "，主屏" : "");
            title.AutoSize = true;
            title.Location = new Point(12, 10);
            card.Controls.Add(title);

            Label layLbl = new Label();
            layLbl.Text = "分屏方式：";
            layLbl.AutoSize = true;
            layLbl.Location = new Point(12, 44);
            card.Controls.Add(layLbl);

            ComboBox layout = new ComboBox();
            layout.DropDownStyle = ComboBoxStyle.DropDownList;
            layout.Location = new Point(95, 40);
            layout.Width = 150;
            layout.Items.Add("不分屏");
            layout.Items.Add("上下两分");
            layout.Items.Add("左右两分");
            // 竖屏默认上下、横屏默认左右；有上次的记录则优先
            layout.SelectedIndex = Config.GetInt(MonitorKey(s) + "|layout", portrait ? 1 : 2, 0, 2);
            layout.SelectedIndexChanged += delegate { UpdateRatioUi(); _preview.Invalidate(); };
            card.Controls.Add(layout);

            Label ratioLbl0 = new Label();
            ratioLbl0.Text = "分割比例：";
            ratioLbl0.AutoSize = true;
            ratioLbl0.Location = new Point(12, 84);
            card.Controls.Add(ratioLbl0);

            TrackBar ratio = new TrackBar();
            ratio.Location = new Point(95, 72);
            ratio.Width = 190;
            ratio.Minimum = 20;
            ratio.Maximum = 80;
            ratio.Value = Config.GetInt(MonitorKey(s) + "|ratio", 50, 20, 80); // 记住上次比例
            ratio.TickFrequency = 5;
            ratio.ValueChanged += delegate { UpdateRatioUi(); _preview.Invalidate(); };
            card.Controls.Add(ratio);

            Label ratioText = new Label();
            ratioText.AutoSize = true;
            ratioText.Location = new Point(295, 80);
            card.Controls.Add(ratioText);

            Label zl0 = new Label();
            zl0.Text = "区域一（上）：";
            zl0.AutoSize = true;
            zl0.Location = new Point(12, 124);
            card.Controls.Add(zl0);

            ComboBox zoneA = new ComboBox();
            zoneA.DropDownStyle = ComboBoxStyle.DropDownList;
            zoneA.Location = new Point(95, 120);
            zoneA.Width = 310;
            card.Controls.Add(zoneA);

            Label zl1 = new Label();
            zl1.Text = "区域二（下）：";
            zl1.AutoSize = true;
            zl1.Location = new Point(12, 164);
            card.Controls.Add(zl1);

            ComboBox zoneB = new ComboBox();
            zoneB.DropDownStyle = ComboBoxStyle.DropDownList;
            zoneB.Location = new Point(95, 160);
            zoneB.Width = 310;
            card.Controls.Add(zoneB);

            ScreenCard sc = new ScreenCard();
            sc.Screen = s;
            sc.Index = idx;
            sc.Layout = layout;
            sc.Ratio = ratio;
            sc.RatioLabel = ratioText;
            sc.Zones = new ComboBox[] { zoneA, zoneB };
            sc.ZoneLabels = new Label[] { zl0, zl1 };
            _cards.Add(sc);
            return card;
        }

        private void RefreshWindows()
        {
            _windows = Native.ListWindows();
            foreach (ScreenCard c in _cards)
            {
                foreach (ComboBox combo in c.Zones)
                {
                    IntPtr keep = IntPtr.Zero;
                    WindowInfo sel = combo.SelectedItem as WindowInfo;
                    if (sel != null) keep = sel.Handle;
                    combo.Items.Clear();
                    combo.Items.Add(WindowInfo.None);
                    foreach (WindowInfo w in _windows)
                        if (w.Handle != Handle) // 不列出设置窗口自己
                            combo.Items.Add(w);
                    int found = 0;
                    if (keep != IntPtr.Zero)
                        for (int i = 1; i < combo.Items.Count; i++)
                            if (((WindowInfo)combo.Items[i]).Handle == keep) { found = i; break; }
                    combo.SelectedIndex = found;
                }
            }
            _winCount.Text = string.Format("检测到 {0} 个可摆放的窗口，{1} 块显示器", _windows.Count, _cards.Count);
        }

        private void UpdateRatioUi()
        {
            foreach (ScreenCard c in _cards)
            {
                int lay = c.Layout.SelectedIndex;
                bool vertical = lay == 1;
                if (lay <= 0)
                {
                    c.Ratio.Enabled = false;
                    c.RatioLabel.Text = "—";
                    c.Zones[0].Enabled = false;
                    c.Zones[1].Enabled = false;
                    c.ZoneLabels[0].Text = "区域一：";
                    c.ZoneLabels[1].Text = "区域二：";
                }
                else
                {
                    c.Ratio.Enabled = true;
                    c.Zones[0].Enabled = true;
                    c.Zones[1].Enabled = true;
                    int v = c.Ratio.Value;
                    if (vertical)
                    {
                        c.RatioLabel.Text = string.Format("上 {0}% / 下 {1}%", v, 100 - v);
                        c.ZoneLabels[0].Text = "区域一（上）：";
                        c.ZoneLabels[1].Text = "区域二（下）：";
                    }
                    else
                    {
                        c.RatioLabel.Text = string.Format("左 {0}% / 右 {1}%", v, 100 - v);
                        c.ZoneLabels[0].Text = "区域一（左）：";
                        c.ZoneLabels[1].Text = "区域二（右）：";
                    }
                }
            }
        }

        private sealed class Placement
        {
            public IntPtr Hwnd;
            public Native.RECT Rect;
            public string Title;
        }

        private void Apply()
        {
            try
            {
                List<Placement> plan = new List<Placement>();
                HashSet<IntPtr> seen = new HashSet<IntPtr>();
                foreach (ScreenCard c in _cards)
                {
                    int lay = c.Layout.SelectedIndex;
                    if (lay <= 0) continue;
                    Native.RECT wa = WindowMover.WorkAreaAtCenter(c.Screen.Bounds);
                    Native.RECT ra, rb;
                    WindowMover.SplitRects(wa, lay == 1, c.Ratio.Value, out ra, out rb);
                    Native.RECT[] zones = new Native.RECT[] { ra, rb };
                    for (int i = 0; i < 2; i++)
                    {
                        WindowInfo wi = c.Zones[i].SelectedItem as WindowInfo;
                        if (wi == null || wi.Handle == IntPtr.Zero) continue;
                        if (!seen.Add(wi.Handle))
                        {
                            MessageBox.Show(this, "同一个窗口被放进了多个区域：\n" + wi.Title,
                                "分屏设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        Placement p = new Placement();
                        p.Hwnd = wi.Handle; p.Rect = zones[i]; p.Title = wi.Title;
                        plan.Add(p);
                    }
                }
                if (plan.Count == 0)
                {
                    MessageBox.Show(this, "没有为任何区域选择窗口，请先在下拉框中选择。",
                        "分屏设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                int placed = 0;
                List<string> skipped = new List<string>();
                foreach (Placement p in plan)
                {
                    if (_mover.Place(p.Hwnd, p.Rect)) placed++;
                    else skipped.Add(p.Title); // 扫描之后窗口已被关闭等
                }
                if (skipped.Count == 0)
                    MessageBox.Show(this, "分屏完成！共摆放 " + placed + " 个窗口。",
                        "分屏设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this,
                        "已摆放 " + placed + "/" + plan.Count + " 个窗口，以下窗口无法摆放（可能已关闭）：\n"
                        + string.Join("\n", skipped.ToArray()),
                        "分屏设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "分屏失败：" + ex.Message, "分屏设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PreviewPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            if (_cards.Count == 0) return;
            int minL = int.MaxValue, minT = int.MaxValue, maxR = int.MinValue, maxB = int.MinValue;
            foreach (ScreenCard c in _cards)
            {
                Rectangle b = c.Screen.Bounds;
                minL = Math.Min(minL, b.Left); minT = Math.Min(minT, b.Top);
                maxR = Math.Max(maxR, b.Right); maxB = Math.Max(maxB, b.Bottom);
            }
            int vw = maxR - minL, vh = maxB - minT;
            if (vw <= 0 || vh <= 0) return;
            float pad = 30f;
            float scale = Math.Min((_preview.ClientRectangle.Width - pad * 2) / vw,
                                   (_preview.ClientRectangle.Height - pad * 2) / vh);
            float ox = (_preview.ClientRectangle.Width - vw * scale) / 2f;
            float oy = (_preview.ClientRectangle.Height - vh * scale) / 2f;

            using (Pen border = new Pen(Color.FromArgb(60, 70, 90), 1.5f))
            using (Pen splitPen = new Pen(Color.FromArgb(220, 90, 40), 2f))
            {
                splitPen.DashStyle = DashStyle.Dash;
                foreach (ScreenCard c in _cards)
                {
                    Rectangle b = c.Screen.Bounds;
                    bool portrait = b.Height >= b.Width;
                    RectangleF dest = new RectangleF(
                        (b.Left - minL) * scale + ox, (b.Top - minT) * scale + oy,
                        b.Width * scale, b.Height * scale);
                    using (SolidBrush fill = new SolidBrush(portrait
                        ? Color.FromArgb(214, 228, 244) : Color.FromArgb(212, 236, 218)))
                    {
                        g.FillRectangle(fill, dest);
                    }
                    g.DrawRectangle(border, dest.X, dest.Y, dest.Width, dest.Height);
                    g.DrawString(string.Format("显示器 {0}  {1}", c.Index + 1, portrait ? "竖屏" : "横屏"),
                        _small, Brushes.Black, dest.X + 6, dest.Y + 5);

                    int lay = c.Layout.SelectedIndex;
                    if (lay > 0)
                    {
                        Native.RECT wa = WindowMover.WorkAreaAtCenter(b);
                        RectangleF wdest = new RectangleF(
                            (wa.L - minL) * scale + ox, (wa.T - minT) * scale + oy,
                            (wa.R - wa.L) * scale, (wa.B - wa.T) * scale);
                        float v = c.Ratio.Value / 100f;
                        if (lay == 1)
                        {
                            float ly = wdest.Top + wdest.Height * v;
                            g.DrawLine(splitPen, wdest.Left, ly, wdest.Right, ly);
                            DrawZoneNumber(g, wdest.Left + wdest.Width / 2, wdest.Top + wdest.Height * v / 2, "1");
                            DrawZoneNumber(g, wdest.Left + wdest.Width / 2, ly + wdest.Height * (1 - v) / 2, "2");
                        }
                        else
                        {
                            float lx = wdest.Left + wdest.Width * v;
                            g.DrawLine(splitPen, lx, wdest.Top, lx, wdest.Bottom);
                            DrawZoneNumber(g, wdest.Left + wdest.Width * v / 2, wdest.Top + wdest.Height / 2, "1");
                            DrawZoneNumber(g, lx + wdest.Width * (1 - v) / 2, wdest.Top + wdest.Height / 2, "2");
                        }
                    }
                }
            }
        }

        private static readonly Font ZoneNumberFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        private static readonly Brush ZoneNumberBrush = new SolidBrush(Color.FromArgb(90, 90, 90));

        private static void DrawZoneNumber(Graphics g, float x, float y, string n)
        {
            SizeF sz = g.MeasureString(n, ZoneNumberFont);
            g.DrawString(n, ZoneNumberFont, ZoneNumberBrush, x - sz.Width / 2, y - sz.Height / 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _small != null) _small.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const int HK_TOP = 1, HK_BOTTOM = 2, HK_SPLIT = 3, HK_RESTORE = 4;
        private const int VK_UP = 0x26, VK_DOWN = 0x28, VK_S = 0x53, VK_R = 0x52;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "UpDownSplit";

        private readonly WindowMover _mover = new WindowMover();
        private NotifyIcon _tray;
        private Icon _icon;
        private HotkeyWindow _hotkeys;
        private ToolStripMenuItem _autoStartItem;
        private bool _disposed;

        public TrayApp()
        {
            _mover.EnableDeferredChecks = true; // GUI 下摆位后做延迟复核

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("分屏设置...", null, delegate { Safe(OpenSettings); }));
            menu.Items.Add(new ToolStripMenuItem("一键上下分屏  Win+Ctrl+S", null,
                delegate { Safe(SplitAndNotify); }));
            menu.Items.Add(new ToolStripMenuItem("还原上次移动  Win+Ctrl+R", null,
                delegate { Safe(delegate { _mover.RestoreLast(); }); }));
            menu.Items.Add(new ToolStripMenuItem("重新注册快捷键", null,
                delegate { Safe(RegisterHotkeysAndNotify); }));
            menu.Items.Add(new ToolStripSeparator());
            _autoStartItem = new ToolStripMenuItem("开机自动启动", null,
                delegate { Safe(ToggleAutoStart); });
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitApp(); }));

            _tray = new NotifyIcon();
            _icon = MakeIcon();
            _tray.Icon = _icon;
            _tray.Text = "上下分屏助手";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { Safe(OpenSettings); };

            _autoStartItem.Checked = AutoStartEnabled();

            _hotkeys = new HotkeyWindow(OnHotkey);
            string failed = RegisterHotkeys();
            if (failed != null)
            {
                _tray.ShowBalloonTip(8000, "上下分屏助手",
                    "以下快捷键被其他软件占用，注册失败（释放后可在托盘菜单点“重新注册快捷键”）：\n" + failed,
                    ToolTipIcon.Warning);
            }
            else if (Config.GetInt("tipShown", 0, 0, 1) == 0) // 使用说明只在第一次启动时弹
            {
                Config.Set("tipShown", "1");
                Config.Save();
                _tray.ShowBalloonTip(8000, "上下分屏助手已启动",
                    "双击托盘图标打开“分屏设置”\n" +
                    "快捷键：Win+Ctrl+↑/↓ 贴上、下半屏；Win+Ctrl+S 一键分屏；Win+Ctrl+R 还原",
                    ToolTipIcon.Info);
            }
        }

        // 注册全部快捷键；有占用时返回占用清单，全部成功返回 null
        private string RegisterHotkeys()
        {
            int mods = Native.MOD_WIN | Native.MOD_CONTROL | Native.MOD_NOREPEAT;
            List<string> failed = new List<string>();
            if (!RegHotkey(HK_TOP, mods, VK_UP)) failed.Add("Win+Ctrl+↑");
            if (!RegHotkey(HK_BOTTOM, mods, VK_DOWN)) failed.Add("Win+Ctrl+↓");
            if (!RegHotkey(HK_SPLIT, mods, VK_S)) failed.Add("Win+Ctrl+S");
            if (!RegHotkey(HK_RESTORE, mods, VK_R)) failed.Add("Win+Ctrl+R");
            return failed.Count == 0 ? null : string.Join("、", failed.ToArray());
        }

        private bool RegHotkey(int id, int mods, int vk)
        {
            Native.UnregisterHotKey(_hotkeys.Handle, id); // 重试前先清掉旧的注册
            return Native.RegisterHotKey(_hotkeys.Handle, id, mods, vk);
        }

        private void RegisterHotkeysAndNotify()
        {
            string failed = RegisterHotkeys();
            if (failed != null)
                _tray.ShowBalloonTip(8000, "上下分屏助手",
                    "以下快捷键仍被其他软件占用：\n" + failed, ToolTipIcon.Warning);
            else
                _tray.ShowBalloonTip(3000, "上下分屏助手", "快捷键注册成功。", ToolTipIcon.Info);
        }

        // 一键分屏：找不到下半屏窗口时给个提示，而不是静默半成功
        private void SplitAndNotify()
        {
            if (_mover.SplitTopBottom()) return;
            _tray.ShowBalloonTip(3000, "上下分屏助手",
                "已将当前窗口贴到上半屏；同屏没有找到可以放到下半屏的窗口。", ToolTipIcon.Info);
        }

        private void OpenSettings()
        {
            foreach (Form f in Application.OpenForms)
            {
                SplitConfigForm scf = f as SplitConfigForm;
                if (scf != null) { scf.Activate(); return; }
            }
            SplitConfigForm form = new SplitConfigForm(_mover);
            form.Show();
        }

        private void OnHotkey(int id)
        {
            if (id == HK_TOP) Safe(delegate { _mover.PlaceForeground(Half.Top); });
            else if (id == HK_BOTTOM) Safe(delegate { _mover.PlaceForeground(Half.Bottom); });
            else if (id == HK_SPLIT) Safe(SplitAndNotify);
            else if (id == HK_RESTORE) Safe(delegate { _mover.RestoreLast(); });
        }

        private void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(3000, "上下分屏助手", "操作失败：" + ex.Message, ToolTipIcon.Error);
            }
        }

        private static string ExePath
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        private static bool AutoStartEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (k == null) return false;
                string v = k.GetValue(RunName) as string;
                if (v == null) return false;
                // 注册表路径必须仍指向当前 exe，exe 被移动/改名后视为失效
                return string.Equals(v.Trim('"'), ExePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ToggleAutoStart()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (_autoStartItem.Checked) k.DeleteValue(RunName, false);
                else k.SetValue(RunName, "\"" + ExePath + "\"");
            }
            _autoStartItem.Checked = AutoStartEnabled();
        }

        private void ExitApp()
        {
            Application.Exit(); // 快捷键注销、托盘与图标释放统一在 Dispose 里做
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                if (_hotkeys != null)
                {
                    Native.UnregisterHotKey(_hotkeys.Handle, HK_TOP);
                    Native.UnregisterHotKey(_hotkeys.Handle, HK_BOTTOM);
                    Native.UnregisterHotKey(_hotkeys.Handle, HK_SPLIT);
                    Native.UnregisterHotKey(_hotkeys.Handle, HK_RESTORE);
                    _hotkeys.ReleaseHandle();
                }
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                if (_icon != null) _icon.Dispose();
            }
            base.Dispose(disposing);
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush top = new SolidBrush(Color.FromArgb(64, 132, 220)),
                                       bot = new SolidBrush(Color.FromArgb(84, 185, 116)))
                {
                    g.FillRectangle(top, 2, 3, 28, 12);
                    g.FillRectangle(bot, 2, 17, 28, 12);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(h))
                        return (Icon)temp.Clone(); // 克隆一份自己持有，原句柄立即销毁，避免 GDI 句柄泄漏
                }
                finally { Native.DestroyIcon(h); }
            }
        }
    }

    internal sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action<int> _onHotkey;

        public HotkeyWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) _onHotkey(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }
}
