// 全局低级键盘钩子：抓 CapsLock / 方向键 / 上下键，路由到 Router
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WebVideoRemote
{
    class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int WM_KEYUP = 0x101, WM_SYSKEYUP = 0x105;
        const uint LLKHF_INJECTED = 0x10;

        const int VK_CAPITAL = 0x14, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_UP = 0x26, VK_DOWN = 0x28;

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KbdDllHookStruct
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);

        readonly Settings _s;
        readonly Router _r;
        readonly LowLevelKeyboardProc _proc;   // 必须持引用，防止被 GC
        IntPtr _hook = IntPtr.Zero;

        // 抖音 → 键状态（钩子回调都在同一线程，但定时判定在别的线程，所以加锁）
        bool _rightHeld, _rightLong;
        int _rightDownTick;
        bool _leftHandled;
        readonly object _holdGate = new object();
        long _holdGen;
        readonly System.Collections.Generic.HashSet<int> _down = new System.Collections.Generic.HashSet<int>();

        public bool CaptureInjected;           // 自检用：连自己注入的按键也抓
        public event Action<string> Captured;  // 自检用

        public bool Installed { get { return _hook != IntPtr.Zero; } }

        public KeyboardHook(Settings s, Router r)
        {
            _s = s; _r = r;
            _proc = Callback;
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            Log.Write("键盘钩子安装: " + (_hook != IntPtr.Zero ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
        }

        public void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (isDown || isUp)
                    {
                        var kbd = (KbdDllHookStruct)Marshal.PtrToStructure(lParam, typeof(KbdDllHookStruct));
                        bool injected = (kbd.flags & LLKHF_INJECTED) != 0;

                        if (injected && !CaptureInjected)
                            return CallNextHookEx(_hook, nCode, wParam, lParam); // 放行自己注入的键，避免回环

                        if (CaptureInjected)
                        {
                            var h0 = Captured;
                            if (h0 != null) h0((isDown ? "down " : "up   ") + "vk=0x" + kbd.vkCode.ToString("X2")
                                + (injected ? " [注入]" : ""));
                        }

                        // 低级钩子看不到「本次是不是自动重复」，只能自己记住按键状态
                        int vk = (int)kbd.vkCode;
                        bool repeat = isDown && _down.Contains(vk);
                        if (isDown) _down.Add(vk);
                        else if (isUp) _down.Remove(vk);

                        if (_s.Enabled && Handle(vk, isDown, isUp, repeat))
                            return (IntPtr)1; // 吞掉，不传给游戏
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("钩子异常: " + e.Message);
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // 返回 true 表示吞掉该按键。repeat = 系统自动重复产生的事件，不重复触发动作
        bool Handle(int vk, bool isDown, bool isUp, bool repeat)
        {
            bool handled = false;
            bool swallow = false;

            // ---- →（右键）的「松开」必须无条件处理 ----
            // 就算长按期间自动识别把模式切成了 B站，也必须把倍速恢复回来，
            // 否则页面会一直停在 2 倍速上下不来。
            if (vk == VK_RIGHT && _rightLong && isUp)
            {
                FinishHold();
                return _s.InterceptArrows;
            }

            if (vk == VK_CAPITAL)
            {
                if (isDown && !repeat)
                {
                    Log.Write("热键: CapsLock → 播放/暂停");
                    _r.Toggle();
                }
                handled = true;
                swallow = _s.InterceptCaps;
            }
            else if (_r.CachedMode == 1) // 抖音
            {
                if (vk == VK_UP)
                {
                    if (isDown && !repeat) { Log.Write("热键: ↑ → 上一个视频"); _r.SwitchVideo(true); }
                    handled = true; swallow = _s.InterceptUpDown;
                }
                else if (vk == VK_DOWN)
                {
                    if (isDown && !repeat) { Log.Write("热键: ↓ → 下一个视频"); _r.SwitchVideo(false); }
                    handled = true; swallow = _s.InterceptUpDown;
                }
                else if (vk == VK_LEFT)
                {
                    if (isDown && !_leftHandled) { _leftHandled = true; _r.Seek(false); }
                    if (isUp) _leftHandled = false;
                    handled = true; swallow = _s.InterceptArrows;
                }
                else if (vk == VK_RIGHT)
                {
                    handled = true; swallow = _s.InterceptArrows;
                    if (isDown)
                    {
                        if (!_rightHeld)
                        {
                            long gen;
                            int downTick = Environment.TickCount;
                            lock (_holdGate)
                            {
                                _rightHeld = true;
                                _rightLong = false;
                                _rightDownTick = downTick;
                                gen = ++_holdGen;
                            }
                            if (_s.HoldFastForward)
                            {
                                // 按下就立刻开始快进，不再等阈值 —— 手感是「按住就在快进」。
                                // 松开时再按持续时间决定：短按额外补一次「快进」。
                                lock (_holdGate) { _rightLong = true; }
                                Log.Write("按下 → 开始快进");
                                _r.HoldStart();
                            }
                        }
                    }
                    else if (isUp)
                    {
                        bool wasLong; int downTick;
                        lock (_holdGate)
                        {
                            _rightHeld = false;
                            wasLong = _rightLong;
                            _rightLong = false;
                            downTick = _rightDownTick;
                            _holdGen++;
                        }
                        if (wasLong)
                        {
                            int held = unchecked(Environment.TickCount - downTick);
                            bool to1x = held >= _s.HoldMs;
                            _r.HoldStop(to1x);
                            if (!to1x)
                            {
                                Log.Write("短按（" + held + "ms）→ 恢复原速并快进一次");
                                _r.Seek(true);
                            }
                            else Log.Write("松开（按住 " + held + "ms）→ 设为 1 倍速");
                        }
                        else _r.Seek(true); // 没走过快进的（比如刚开机）也按短按处理
                    }
                }
            }
            else // B站
            {
                if (vk == VK_LEFT)
                {
                    if (isDown && !repeat) _r.Seek(false);
                    handled = true; swallow = _s.InterceptArrows;
                }
                else if (vk == VK_RIGHT)
                {
                    if (isDown && !repeat) _r.Seek(true);
                    handled = true; swallow = _s.InterceptArrows;
                }
            }

            return handled && swallow;
        }

        // 结束一次长按（松手、模式切换、或看门狗触发时都可能调用）
        void FinishHold()
        {
            bool wasLong; int downTick;
            lock (_holdGate)
            {
                _rightHeld = false;
                wasLong = _rightLong;
                _rightLong = false;
                downTick = _rightDownTick;
                _holdGen++;
            }
            if (wasLong)
            {
                int held = unchecked(Environment.TickCount - downTick);
                bool to1x = held >= _s.HoldMs;   // 按满长按阈值：松手设为 1 倍速；轻点：页面自己恢复原速
                Log.Write("结束长按快进（按住 " + held + "ms）" + (to1x ? " → 设为 1 倍速" : " → 恢复原速"));
                _r.HoldStop(to1x);
            }
        }

        // 模式切换时兜底：万一长按还没结束就切了模式，立刻恢复倍速
        public void AbortHold()
        {
            bool wasLong;
            lock (_holdGate) wasLong = _rightLong;
            if (wasLong) FinishHold();
        }

        public void Dispose() { Uninstall(); }
    }
}
