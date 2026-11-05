// 控制引擎：SMTC 系统媒体控制 + 窗口按键注入
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace WebVideoRemote
{
    static class Site
    {
        static readonly string[] Bili = { "bilibili", "哔哩", "b站" };
        static readonly string[] Douyin = { "douyin", "抖音" };

        public static bool IsBili(string s) { return Has(s, Bili); }
        public static bool IsDouyin(string s) { return Has(s, Douyin); }

        static bool Has(string s, string[] marks)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.ToLowerInvariant();
            foreach (var m in marks) if (t.Contains(m)) return true;
            return false;
        }
    }

    // ---------------- 通道 1：SMTC 系统媒体控制 ----------------
    class MediaInfo
    {
        public GlobalSystemMediaTransportControlsSession Session;
        public string App = "", Title = "";
        public bool Playing;
        public string Status = "未知";
        public double Position, Duration;
    }

    class SmtcEngine
    {
        GlobalSystemMediaTransportControlsSessionManager _mgr;
        readonly Task _init;
        readonly object _gate = new object();

        static readonly string[] BrowserMarks =
        {
            "chrome", "msedge", "edge", "firefox", "brave", "vivaldi",
            "opera", "360", "qqbrowser", "quark", "sogou", "maxthon"
        };

        public SmtcEngine()
        {
            _init = Task.Run(() =>
            {
                try
                {
                    GlobalSystemMediaTransportControlsSessionManager mgr;
                    if (Winrt.Run(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 6000, out mgr))
                    {
                        _mgr = mgr;
                        Log.Write("SMTC 就绪");
                    }
                    else Log.Write("SMTC 初始化超时");
                }
                catch (Exception e) { Log.Write("SMTC 初始化失败: " + e.Message); }
            });
        }

        public bool Ready { get { return _mgr != null; } }
        public void Wait() { try { _init.Wait(4000); } catch { } }

        // mode: 0=B站 1=抖音 2=自动
        public MediaInfo Find(int mode)
        {
            Wait();
            var mgr = _mgr;
            if (mgr == null) return null;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = mgr.GetSessions(); } catch { return null; }

            GlobalSystemMediaTransportControlsSession best = null;
            int bestScore = -1;

            foreach (var s in sessions)
            {
                string app = "";
                try { app = (s.SourceAppUserModelId ?? "").ToLowerInvariant(); } catch { continue; }

                bool isBrowser = false;
                foreach (var m in BrowserMarks) if (app.Contains(m)) { isBrowser = true; break; }
                if (!isBrowser) continue;

                int score = 10;
                var st = TryStatus(s);
                if (st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) score += 2;
                else if (st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) score += 1;

                // 站点匹配优先（标题里带网站名）
                string title = TitleSync(s);
                if (mode == 0 && Site.IsBili(title)) score += 6;
                else if (mode == 1 && Site.IsDouyin(title)) score += 6;
                else if (mode == 2 && (Site.IsBili(title) || Site.IsDouyin(title))) score += 6;

                if (score > bestScore) { bestScore = score; best = s; }
            }

            if (best == null) { try { best = mgr.GetCurrentSession(); } catch { } }
            if (best == null) return null;

            return Describe(best);
        }

        public MediaInfo Describe(GlobalSystemMediaTransportControlsSession s)
        {
            var mi = new MediaInfo { Session = s };
            try { mi.App = s.SourceAppUserModelId ?? ""; } catch { }
            mi.Title = TitleSync(s);
            try
            {
                var pb = s.GetPlaybackInfo();
                mi.Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                mi.Status = StatusText(pb.PlaybackStatus);
            }
            catch { }
            try
            {
                var tl = s.GetTimelineProperties();
                mi.Position = tl.Position.TotalSeconds;
                if (mi.Playing) mi.Position += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                mi.Duration = tl.EndTime.TotalSeconds;
            }
            catch { }
            return mi;
        }

        readonly Dictionary<GlobalSystemMediaTransportControlsSession, string> _titleCache =
            new Dictionary<GlobalSystemMediaTransportControlsSession, string>();
        readonly Dictionary<GlobalSystemMediaTransportControlsSession, DateTime> _titleAt =
            new Dictionary<GlobalSystemMediaTransportControlsSession, DateTime>();

        // 会话标题带 1.5 秒缓存，避免每次轮询都去问一次系统
        string TitleSync(GlobalSystemMediaTransportControlsSession s)
        {
            lock (_titleCache)
            {
                string cached; DateTime at;
                if (_titleCache.TryGetValue(s, out cached) && _titleAt.TryGetValue(s, out at)
                    && (DateTime.Now - at).TotalMilliseconds < 1500)
                    return cached;

                string title = "";
                try
                {
                    GlobalSystemMediaTransportControlsSessionMediaProperties mp;
                    if (Winrt.Run(s.TryGetMediaPropertiesAsync(), 1500, out mp) && mp != null)
                        title = mp.Title ?? "";
                }
                catch { }

                if (_titleCache.Count > 24) { _titleCache.Clear(); _titleAt.Clear(); }
                _titleCache[s] = title;
                _titleAt[s] = DateTime.Now;
                return title;
            }
        }

        static GlobalSystemMediaTransportControlsSessionPlaybackStatus TryStatus(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus; }
            catch { return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed; }
        }

        public static string StatusText(GlobalSystemMediaTransportControlsSessionPlaybackStatus st)
        {
            switch (st)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing: return "播放中";
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused: return "已暂停";
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped: return "已停止";
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed: return "已关闭";
                default: return "未知";
            }
        }

        public bool Toggle(MediaInfo mi)
        {
            try
            {
                bool ok;
                return Winrt.Run(mi.Session.TryTogglePlayPauseAsync(), 2500, out ok) && ok;
            }
            catch (Exception e) { Log.Write("SMTC 播放暂停异常: " + e.Message); return false; }
        }

        // 跳到绝对位置（秒）
        public bool SeekTo(MediaInfo mi, double target)
        {
            try
            {
                if (target < 0) target = 0;
                if (mi.Duration > 0 && target > mi.Duration - 0.5) target = Math.Max(0, mi.Duration - 1.0);
                bool res;
                bool ok = Winrt.Run(mi.Session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(target).Ticks), 2500, out res) && res;
                Log.Write(string.Format("SMTC 跳转 -> {0:0.0}s 结果={1}", target, ok));
                return ok;
            }
            catch (Exception e) { Log.Write("SMTC 跳转异常: " + e.Message); return false; }
        }

        public bool Next(MediaInfo mi) { return Skip(mi, true); }
        public bool Prev(MediaInfo mi) { return Skip(mi, false); }

        bool Skip(MediaInfo mi, bool next)
        {
            try
            {
                bool res;
                var op = next ? mi.Session.TrySkipNextAsync() : mi.Session.TrySkipPreviousAsync();
                return Winrt.Run(op, 2000, out res) && res;
            }
            catch { return false; }
        }
    }

    // ---------------- WinRT 异步等待 ----------------
    // 不用 System.Runtime.WindowsRuntime 的 AsTask() 扩展：不同来源的 WinRT 元数据
    // （SDK 的 Windows.winmd 与系统自带的拆分 .winmd）程序集标识不一致，会导致扩展方法解析不到。
    // 直接用 Completed 回调 + GetResults，两边都能编译、运行时行为一致。
    static class Winrt
    {
        public static bool Run<T>(Windows.Foundation.IAsyncOperation<T> op, int timeoutMs, out T result)
        {
            result = default(T);
            if (op == null) return false;

            var done = new ManualResetEventSlim(false);
            T val = default(T);
            bool ok = false;

            try
            {
                op.Completed = (info, status) =>
                {
                    try
                    {
                        if (status == Windows.Foundation.AsyncStatus.Completed) { val = info.GetResults(); ok = true; }
                    }
                    catch { }
                    finally { try { done.Set(); } catch { } }
                };
            }
            catch { return false; }

            if (!done.Wait(timeoutMs)) return false;
            result = val;
            return ok;
        }
    }

    // ---------------- 真实按键注入（带扫描码） ----------------
    static class KeyInjector
    {
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public HARDWAREINPUT hi; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_SCANCODE = 0x0008;

        public static bool IsExtended(uint vk)
        {
            // 方向键 / Ins Del Home End PgUp PgDn / 左右 Win / 应用键
            return vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 ||
                   vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||
                   vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D;
        }

        public static void Send(uint vk, bool up)
        {
            ushort scan = 0;
            try { scan = (ushort)MapVirtualKey(vk, 0); } catch { }

            if (scan != 0)
            {
                var inp = new INPUT[1];
                inp[0].type = INPUT_KEYBOARD;
                inp[0].u.ki.wVk = 0;
                inp[0].u.ki.wScan = scan;
                inp[0].u.ki.dwFlags = KEYEVENTF_SCANCODE
                    | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u)
                    | (up ? KEYEVENTF_KEYUP : 0u);
                inp[0].u.ki.dwExtraInfo = IntPtr.Zero;
                if (SendInput(1, inp, Marshal.SizeOf(typeof(INPUT))) == 1) return;
            }
            keybd_event((byte)vk, (byte)scan, up ? KEYEVENTF_KEYUP : 0, IntPtr.Zero);
        }

        public static void Down(uint vk) { Send(vk, false); }
        public static void Up(uint vk) { Send(vk, true); }

        public static void Tap(uint vk)
        {
            Send(vk, false);
            Thread.Sleep(18);
            Send(vk, true);
        }

        // 抖音长按快进用：按下 + 按物理键盘自动重复节奏补发 + 松开
        public static void Hold(uint vk, ManualResetEventSlim release, int safetyMs)
        {
            Send(vk, false);
            int waited = 0;
            while (waited < safetyMs)
            {
                if (release.Wait(45)) break;
                waited += 45;
                Send(vk, false); // 补发 keydown（repeat）
            }
            if (waited >= safetyMs) Log.Write("长按快进看门狗自动松开（" + waited + "ms）");
            Send(vk, true);
        }
    }

    // ---------------- 通道 2：窗口级按键注入 ----------------
    class WindowInfo
    {
        public IntPtr Hwnd;
        public string Title = "", Class = "", Exe = "";
    }

    static class WindowEngine
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

        const int SW_RESTORE = 9;

        // 只认真正的浏览器进程，避免把 Electron 应用（VS Code / Discord / 各种桌面客户端）误判成浏览器
        static readonly string[] BrowserExes =
        {
            "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "vivaldi.exe",
            "opera.exe", "opera_gx.exe", "360se.exe", "360chrome.exe", "360speedld.exe",
            "qqbrowser.exe", "quarkpc.exe", "sogouexplorer.exe", "maxthon.exe",
            "ucbrowser.exe", "liebao.exe", "theworld.exe"
        };

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder sb, ref uint size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // 取进程可执行文件名（用受限查询权限，跨完整性级别也能读到）
        public static string ExeNameOf(uint pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
                if (h == IntPtr.Zero) return null;
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return null;
                return System.IO.Path.GetFileName(sb.ToString()).ToLowerInvariant();
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        static readonly object _workerGate = new object();
        static readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        static WindowEngine()
        {
            var t = new Thread(() =>
            {
                foreach (var a in _queue.GetConsumingEnumerable())
                {
                    try { a(); } catch (Exception e) { Log.Write("注入线程异常: " + e.Message); }
                }
            });
            t.IsBackground = true;
            t.Name = "WVRemoteInject";
            t.Start();
        }

        static void OnWorker(Action a) { try { _queue.Add(a); } catch { } }
        static void WaitWorker(Action a)
        {
            var done = new ManualResetEventSlim(false);
            OnWorker(() => { try { a(); } finally { done.Set(); } });
            done.Wait(8000);
        }

        public static WindowInfo Find(int mode)
        {
            WindowInfo any = null, preferred = null;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;

                uint pid;
                GetWindowThreadProcessId(h, out pid);
                string exe = ExeNameOf(pid);
                string cls = ClassOf(h);

                bool browser;
                if (exe != null) browser = Array.IndexOf(BrowserExes, exe) >= 0;
                else browser = cls == "Chrome_WidgetWin_1" || cls == "MozillaWindowClass"; // 读不到进程名时的兜底
                if (!browser) return true;

                string title = TitleOf(h);
                if (title.Length == 0) return true;

                var wi = new WindowInfo { Hwnd = h, Title = title, Class = cls, Exe = exe ?? "" };
                if (preferred == null)
                {
                    if (mode == 0 && Site.IsBili(title)) preferred = wi;
                    else if (mode == 1 && Site.IsDouyin(title)) preferred = wi;
                    else if (mode == 2 && (Site.IsBili(title) || Site.IsDouyin(title))) preferred = wi;
                }
                if (any == null) any = wi;
                return true;
            }, IntPtr.Zero);
            return preferred ?? any;
        }

        public static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            return sb.ToString();
        }

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, 256);
            return sb.ToString();
        }

        // 后台投递（实验）：Chromium 会以「拒绝访问」驳回，返回值即真相
        public static bool TryPostKey(WindowInfo wi, uint vk)
        {
            if (wi == null) return false;
            try
            {
                uint scan = (uint)MapVirtualKey(vk, 0);
                long lpDown = 1 | ((long)(scan & 0xFF) << 16) | (KeyInjector.IsExtended(vk) ? (1L << 24) : 0);
                long lpUp = lpDown | (1L << 30) | (1L << 31);
                bool d = PostMessageW(wi.Hwnd, 0x100, (IntPtr)vk, (IntPtr)lpDown);
                int err = Marshal.GetLastWin32Error();
                bool u = PostMessageW(wi.Hwnd, 0x101, (IntPtr)vk, (IntPtr)lpUp);
                Log.Write("后台投递 vk=0x" + vk.ToString("X2") + " 结果=" + d + "/" + u + " err=" + err);
                return d && u;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);

        // 抢焦点注入：把浏览器临时置前 → 注入按键 → 焦点还给原来的窗口
        public static bool FocusTap(int mode, uint vk)
        {
            var wi = Find(mode);
            if (wi == null) { Log.Write("抢焦点注入失败：没找到浏览器窗口"); return false; }
            bool ok = false;
            WaitWorker(() => { ok = WithBrowser(wi.Hwnd, () => KeyInjector.Tap(vk)); });
            return ok;
        }

        public static bool FocusHoldStart(int mode, uint vk, ManualResetEventSlim release, int safetyMs)
        {
            var wi = Find(mode);
            if (wi == null) { Log.Write("长按快进失败：没找到浏览器窗口"); return false; }
            bool ok = false;
            WaitWorker(() => { ok = WithBrowser(wi.Hwnd, () => KeyInjector.Hold(vk, release, safetyMs)); });
            return ok;
        }

        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        const uint GA_ROOT = 2;

        static bool WithBrowser(IntPtr browser, Action body)
        {
            if (IsIconic(browser)) ShowWindow(browser, SW_RESTORE); // 最小化的窗口收不到按键

            IntPtr fore = GetForegroundWindow();
            uint dummy;
            uint foreThread = GetWindowThreadProcessId(fore, out dummy);
            uint me = GetCurrentThreadId();
            bool attached = foreThread != me && AttachThreadInput(me, foreThread, true);

            try
            {
                // 轻敲 ALT 解锁前台锁定，提高 SetForegroundWindow 成功率
                KeyInjector.Tap(0x12);
                SetForegroundWindow(browser);
                BringWindowToTop(browser);
                SetActiveWindow(browser);
                Thread.Sleep(110);

                IntPtr now = GetForegroundWindow();
                bool got = now == browser || GetAncestor(now, GA_ROOT) == browser;
                if (!got)
                {
                    Log.Write("抢焦点失败：前台仍是「" + TitleOf(now) + "」，本次不注入按键（避免打到别的窗口）");
                    return false;
                }

                body();
                Thread.Sleep(60);
                return true;
            }
            catch (Exception e) { Log.Write("窗口注入异常: " + e.Message); return false; }
            finally
            {
                try
                {
                    if (fore != IntPtr.Zero) SetForegroundWindow(fore); // 焦点还给游戏
                }
                catch { }
                if (attached) AttachThreadInput(me, foreThread, false);
            }
        }
    }

    // ---------------- 动作路由：按优先级挑通道 ----------------
    class Router
    {
        readonly Settings _s;
        readonly SmtcEngine _smtc;
        readonly BlockingCollection<Action> _jobs = new BlockingCollection<Action>();
        ManualResetEventSlim _holdRelease;
        volatile bool _holding;

        public string LastEngine = "-";
        public string LastAction = "-";
        public volatile int CachedMode = 0;   // 钩子线程只读这个缓存，绝不在钩子里做耗时查询
        public event Action Changed;
        public event Action<string> BridgeHint;   // 桥接没连上时的提醒（界面弹气泡用）

        public Router(Settings s, SmtcEngine smtc)
        {
            _s = s; _smtc = smtc;
            var t = new Thread(() =>
            {
                foreach (var j in _jobs.GetConsumingEnumerable())
                {
                    try { j(); } catch (Exception e) { Log.Write("动作异常: " + e.Message); }
                }
            });
            t.IsBackground = true; t.Name = "WVRemoteRoute"; t.Start();
        }

        // 由界面定时器周期调用，刷新「当前是 B站 还是 抖音」
        public void RefreshMode()
        {
            if (!_s.IsAuto) { CachedMode = _s.Mode; return; }
            try { CachedMode = Mode(); } catch { }
        }

        void Post(Action a) { try { _jobs.Add(a); } catch { } }

        void Done(string engine, string action)
        {
            LastEngine = engine; LastAction = action;
            Log.Write("动作结果: " + engine + " / " + action);   // 每次热键走了哪条通道都记下来
            var h = Changed; if (h != null) h();
        }

        int _lastBridgeWarn;

        // 需要用桥接脚本、但它没连上时，记一条明确提示（30 秒最多一次），并让界面弹个气泡
        void BridgeMiss(string site, string what)
        {
            if (!_s.UseBridge) return;
            if (BridgeServer.Instance.ConnectedFor(site)) return;
            if (unchecked(Environment.TickCount - _lastBridgeWarn) < 30000) return;
            _lastBridgeWarn = Environment.TickCount;

            string now = BridgeServer.Instance.ConnectedSites;
            Log.Write("提示: 浏览器脚本未连接（需要 " + site + "，当前: " + (now.Length == 0 ? "无" : now)
                + "），这次「" + what + "」只能走其它通道");
            var h = BridgeHint;
            if (h != null) h(what);
        }

        // 有效模式：自动模式时看媒体会话标题 → 浏览器窗口标题 → 桥接脚本连的是哪个网站
        public int Mode()
        {
            if (!_s.IsAuto) return _s.Mode;
            try
            {
                var mi = _smtc.Find(2);
                if (mi != null && mi.Title.Length > 0)
                {
                    if (Site.IsDouyin(mi.Title)) return 1;
                    if (Site.IsBili(mi.Title)) return 0;
                }
            }
            catch { }
            try
            {
                var wi = WindowEngine.Find(2);
                if (wi != null)
                {
                    if (Site.IsDouyin(wi.Title)) return 1;
                    if (Site.IsBili(wi.Title)) return 0;
                }
            }
            catch { }
            // 标题都认不出来时，看桥接脚本：只有抖音连着就按抖音算
            try
            {
                bool dy = BridgeServer.Instance.ConnectedFor("douyin");
                bool bl = BridgeServer.Instance.ConnectedFor("bilibili");
                if (dy && !bl) return 1;
                if (bl && !dy) return 0;
            }
            catch { }
            return CachedMode; // 再认不出来就沿用上次判断
        }

        public string ModeName(int m) { return m == 1 ? "抖音" : "B站"; }

        // 桥接指令按网站分流，避免 B站 标签页把抖音的指令抢走
        static string SiteOf(int mode) { return mode == 1 ? "douyin" : "bilibili"; }

        bool BridgeReady(int mode)
        {
            return _s.UseBridge && BridgeServer.Instance.ConnectedFor(SiteOf(mode));
        }

        // ---- 播放 / 暂停 ----
        public void Toggle()
        {
            Post(() =>
            {
                int mode = Mode();
                string site = SiteOf(mode);
                bool bridge = BridgeReady(mode);
                if (!bridge) BridgeMiss(site, "播放/暂停");

                // 抖音网页对「系统媒体控制」的播放/暂停常常返回成功却不动作，
                // 所以抖音模式下优先用浏览器脚本直接操作播放器（这条是本机实测可靠的）。
                if (bridge && mode == 1)
                {
                    BridgeServer.Instance.Send("toggle", site);
                    Done("浏览器脚本", "播放/暂停");
                    return;
                }
                if (_s.UseSmtc)
                {
                    var mi = _smtc.Find(mode);
                    if (mi != null && _smtc.Toggle(mi)) { Done("系统媒体", "播放/暂停"); return; }
                }
                if (bridge)
                {
                    BridgeServer.Instance.Send("toggle", site);
                    Done("浏览器脚本", "播放/暂停");
                    return;
                }
                if (_s.UseBgPost)
                {
                    var wi = WindowEngine.Find(mode);
                    if (WindowEngine.TryPostKey(wi, 0x20)) { Done("后台投递", "播放/暂停"); return; }
                }
                if (_s.UseFocusInject)
                {
                    if (WindowEngine.FocusTap(mode, 0x20)) { Done("窗口注入", "播放/暂停"); return; }
                }
                Done("失败", "播放/暂停");
            });
        }

        // ---- 快进 / 快退 ----
        public void Seek(bool forward)
        {
            Post(() =>
            {
                int mode = Mode();
                double step = mode == 1 ? _s.DouyinSeek : _s.Step;
                double delta = forward ? step : -step;
                if (!BridgeReady(mode)) BridgeMiss(SiteOf(mode), forward ? "快进" : "快退");

                if (_s.UseSmtc)
                {
                    var mi = _smtc.Find(mode);
                    if (mi != null)
                    {
                        double target = mi.Position + delta;
                        if (_smtc.SeekTo(mi, target))
                        {
                            // 验证是否真的动了（有些网站会「假成功」）
                            Thread.Sleep(160);
                            var after = _smtc.Find(mode);
                            if (after != null && Math.Abs(after.Position - target) < 4.0)
                            {
                                Done("系统媒体", (forward ? "快进 " : "快退 ") + step + "s");
                                return;
                            }
                            Log.Write("SMTC 跳转未生效，改走窗口注入");
                        }
                    }
                }
                if (BridgeReady(mode))
                {
                    BridgeServer.Instance.Send((forward ? "seekf:" : "seekb:")
                        + step.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), SiteOf(mode));
                    Done("浏览器脚本", (forward ? "快进 " : "快退 ") + step + "s");
                    return;
                }
                if (_s.UseBgPost)
                {
                    var wi = WindowEngine.Find(mode);
                    if (WindowEngine.TryPostKey(wi, forward ? 0x27u : 0x25u)) { Done("后台投递", (forward ? "快进 " : "快退 ") + step + "s"); return; }
                }
                if (_s.UseFocusInject)
                {
                    uint vk = forward ? 0x27u : 0x25u;
                    if (WindowEngine.FocusTap(mode, vk)) { Done("窗口注入", (forward ? "快进 " : "快退 ") + step + "s"); return; }
                }
                Done("失败", "快进/快退");
            });
        }

        // ---- 抖音：上/下一个视频 ----
        public void SwitchVideo(bool up)
        {
            Post(() =>
            {
                int mode = 1;
                // 实测：抖音网页不会把系统媒体控制的「上一个/下一个」当作换视频（返回成功但不动作），
                // 所以这里优先用浏览器脚本桥接，其次按键注入。
                if (!BridgeReady(1)) BridgeMiss("douyin", up ? "上一个视频" : "下一个视频");
                if (BridgeReady(1))
                {
                    BridgeServer.Instance.Send(up ? "prev" : "next", "douyin");
                    Done("浏览器脚本", up ? "上一个视频" : "下一个视频");
                    return;
                }
                if (_s.UseBgPost)
                {
                    var wi = WindowEngine.Find(mode);
                    if (WindowEngine.TryPostKey(wi, up ? 0x26u : 0x28u)) { Done("后台投递", up ? "上一个视频" : "下一个视频"); return; }
                }
                if (_s.UseFocusInject)
                {
                    uint vk = up ? 0x26u : 0x28u; // ↑ / ↓
                    if (WindowEngine.FocusTap(mode, vk)) { Done("窗口注入", up ? "上一个视频" : "下一个视频"); return; }
                }
                Done("失败", up ? "上一个视频" : "下一个视频");
            });
        }

        // ---- 抖音：长按 → 持续快进 ----
        volatile bool _holdViaBridge;
        long _holdToken;

        public void HoldStart()
        {
            // 桥接可用时「立刻」把快进指令发出去，不进队列 ——
            // 这样按住键的瞬间就开始快进，不会等前面的动作做完（手感要紧）。
            if (BridgeReady(1))
            {
                if (_holding) return;
                _holding = true;
                _holdViaBridge = true;
                BridgeServer.Instance.Send("holdStart", "douyin");
                LastEngine = "浏览器脚本"; LastAction = "快进中";
                Log.Write("动作结果: 浏览器脚本 / 开始快进（即时下发）");
                var h0 = Changed; if (h0 != null) h0();
                StartHoldWatchdog();
                return;
            }

            if (!_s.UseBridge) { }
            else BridgeMiss("douyin", "长按快进");

            Post(() =>
            {
                if (_holding) return;
                _holding = true;
                _holdRelease = new ManualResetEventSlim(false);
                var release = _holdRelease;
                Log.Write("开始长按快进（无桥接，走按键注入兜底）");

                if (_s.UseFocusInject)
                {
                    if (WindowEngine.FocusHoldStart(1, 0x27u, release, _s.SafetyHoldMs))
                    {
                        Done("窗口注入", "长按快进结束");
                        _holding = false;
                        return;
                    }
                }
                _holding = false;
                Done("失败", "长按快进");
            });
        }

        // 看门狗：万一「松开」的按键事件丢了（切窗口、游戏独占输入等），
        // 到点也要把倍速恢复回来，不能让网页一直停在 2 倍速。
        void StartHoldWatchdog()
        {
            long my = System.Threading.Interlocked.Increment(ref _holdToken);
            int ms = _s.SafetyHoldMs;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(ms);
                    if (System.Threading.Interlocked.Read(ref _holdToken) == my && _holding)
                    {
                        Log.Write("长按看门狗：超过 " + ms + "ms 没等到松开，自动设为 1 倍速");
                        HoldStop(true);
                    }
                }
                catch { }
            });
        }

        // to1x=true：松手后让网页停在 1 倍速（长按松手）；false=让页面自己恢复之前的倍速
        public void HoldStop(bool to1x)
        {
            System.Threading.Interlocked.Increment(ref _holdToken); // 让看门狗作废
            if (_holdViaBridge)
            {
                _holdViaBridge = false;
                if (_s.UseBridge) BridgeServer.Instance.Send(to1x ? "holdStop1x" : "holdStop", "douyin");
                _holding = false;
                Log.Write("发送恢复指令（" + (to1x ? "holdStop1x → 1 倍速" : "holdStop") + "）");
                Done("浏览器脚本", to1x ? "长按快进结束 → 1 倍速" : "长按快进结束");
                return;
            }
            var r = _holdRelease;
            if (r != null) { try { r.Set(); } catch { } }
            _holding = false;
        }
    }
}
