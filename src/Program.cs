// 网页视频遥控器 WebVideoRemote
// 玩游戏时在后台控制浏览器里的 B站 / 抖音 播放：播放暂停、快进快退、上下切换视频、长按快进。
//
// 控制通道（按优先级）：
//   1) SMTC  Windows 系统媒体控制（GlobalSystemMediaTransportControlsSession）
//             —— 不抢焦点、不激活浏览器，实测对 B站 的播放/暂停与精确跳转完全有效。
//   2) 窗口注入  把浏览器窗口临时置前，用带扫描码的真实按键注入，随后立刻把焦点还给游戏。
//             抖音的 ↑/↓ 切换视频、长按 → 快进 只能走这里（Chromium 拒绝跨进程投递键盘消息，
//             所以无法在后台把按键"塞"进网页，只能抢焦点）。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WebVideoRemote
{
    static class Program
    {
        static Mutex _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--cli" || args[0] == "/cli"))
            {
                var tee = new TeeWriter();
                Console.SetOut(tee);
                Native.AttachParentConsole();
                tee.ConsoleTarget = Console.Out;
                Console.SetOut(tee);
                try { Cli.Run(args); }
                catch (Exception e) { Console.WriteLine("ERR " + e.Message); }
                Console.Out.Flush();
                tee.Flush();
                return;
            }

            bool created;
            _mutex = new Mutex(true, "WebVideoRemote_SingleInstance_v1", out created);
            if (!created)
            {
                MessageBox.Show("网页视频遥控器已经在运行了（看右下角托盘图标）。",
                    "网页视频遥控器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ---------------- 日志 ----------------
    static class Log
    {
        static readonly object _gate = new object();
        static readonly System.Collections.Generic.Queue<string> _tail =
            new System.Collections.Generic.Queue<string>();

        public static string FilePath
        {
            get { return Path.Combine(AppDir, "WebVideoRemote.log"); }
        }

        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static void Write(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text;
            lock (_gate)
            {
                _tail.Enqueue(line);
                while (_tail.Count > 300) _tail.Dequeue();
                try
                {
                    if (!System.IO.File.Exists(FilePath))
                        System.IO.File.WriteAllText(FilePath, "网页视频遥控器 日志\r\n", new UTF8Encoding(true));
                    else if (new FileInfo(FilePath).Length > 2 * 1024 * 1024)
                        System.IO.File.WriteAllText(FilePath, "网页视频遥控器 日志（已截断）\r\n", new UTF8Encoding(true));

                    System.IO.File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        public static string Tail(int n)
        {
            lock (_gate)
            {
                var arr = _tail.ToArray();
                int start = Math.Max(0, arr.Length - n);
                var sb = new StringBuilder();
                for (int i = start; i < arr.Length; i++) sb.AppendLine(arr[i]);
                return sb.ToString();
            }
        }
    }

    // ---------------- 配置 ----------------
    class Settings
    {
        public bool Enabled = true;          // 热键总开关
        public int Mode = 2;                 // 0=B站  1=抖音  2=自动识别
        public int Step = 5;                 // B站 ←/→ 步长（秒）
        public int DouyinSeek = 15;          // 抖音 ←/→ 单次跳转（秒）
        public bool HoldFastForward = true;  // 抖音长按 → 持续快进
        public int HoldMs = 350;             // 长按判定阈值（毫秒）
        public bool InterceptCaps = true;    // 吞掉 CapsLock（不改大小写、不发光）
        public bool InterceptArrows = false; // 吞掉 ←/→（默认不吞，游戏还要用）
        public bool InterceptUpDown = false; // 吞掉 ↑/↓（默认不吞）
        public bool UseSmtc = true;          // 通道1：系统媒体控制
        public bool UseFocusInject = true;   // 通道2：抢焦点注入
        public bool UseBgPost = false;       // 通道3：后台投递（实验，Chromium 会拒绝）
        public bool UseBridge = true;        // 通道4：浏览器脚本桥接（需装扩展，抖音推荐）
        public bool MinimizeToTray = true;   // 关闭窗口时缩到托盘
        public int SafetyHoldMs = 15000;     // 长按快进看门狗：最长按住时间
        public bool AcceptInjected = false;  // 调试用：连「合成按键」也响应（默认关，开了可能自我循环）

        public bool IsDouyin { get { return Mode == 1; } }
        public bool IsAuto { get { return Mode == 2; } }

        public string FilePath { get { return Path.Combine(Log.AppDir, "WebVideoRemote.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(s.FilePath))
                {
                    foreach (var raw in File.ReadAllLines(s.FilePath, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || !line.Contains("=")) continue;
                        var kv = line.Split(new[] { '=' }, 2);
                        string k = kv[0].Trim().ToLowerInvariant();
                        string v = kv[1].Trim();
                        int n;
                        switch (k)
                        {
                            case "enabled": s.Enabled = v != "0"; break;
                            case "mode": if (int.TryParse(v, out n) && n >= 0 && n <= 2) s.Mode = n; break;
                            case "step": if (int.TryParse(v, out n) && n > 0) s.Step = n; break;
                            case "douyinseek": if (int.TryParse(v, out n) && n > 0) s.DouyinSeek = n; break;
                            case "holdff": s.HoldFastForward = v != "0"; break;
                            case "holdms": if (int.TryParse(v, out n) && n >= 100) s.HoldMs = n; break;
                            case "interceptcaps": s.InterceptCaps = v != "0"; break;
                            case "interceptarrows": s.InterceptArrows = v != "0"; break;
                            case "interceptupdown": s.InterceptUpDown = v != "0"; break;
                            case "usesmtc": s.UseSmtc = v != "0"; break;
                            case "usefocus": s.UseFocusInject = v != "0"; break;
                            case "usebgpost": s.UseBgPost = v != "0"; break;
                            case "usebridge": s.UseBridge = v != "0"; break;
                            case "minimizetotray": s.MinimizeToTray = v != "0"; break;
                            case "acceptinjected": s.AcceptInjected = v != "0"; break;
                            case "safetyhold": if (int.TryParse(v, out n) && n >= 1000) s.SafetyHoldMs = n; break;
                        }
                    }
                }
            }
            catch (Exception e) { Log.Write("读取配置失败: " + e.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "# 网页视频遥控器 配置（改完保存后重启程序生效）",
                    "# mode: 0=B站  1=抖音  2=自动识别",
                    "enabled="         + (Enabled ? 1 : 0),
                    "mode="            + Mode,
                    "step="            + Step,
                    "douyinseek="      + DouyinSeek,
                    "holdff="          + (HoldFastForward ? 1 : 0),
                    "holdms="          + HoldMs,
                    "interceptcaps="   + (InterceptCaps ? 1 : 0),
                    "interceptarrows=" + (InterceptArrows ? 1 : 0),
                    "interceptupdown=" + (InterceptUpDown ? 1 : 0),
                    "usesmtc="         + (UseSmtc ? 1 : 0),
                    "usefocus="        + (UseFocusInject ? 1 : 0),
                    "usebgpost="       + (UseBgPost ? 1 : 0),
                    "usebridge="       + (UseBridge ? 1 : 0),
                    "minimizetotray="  + (MinimizeToTray ? 1 : 0),
                    "safetyhold="      + SafetyHoldMs
                }, new UTF8Encoding(true));
            }
            catch (Exception e) { Log.Write("保存配置失败: " + e.Message); }
        }
    }

    // ---------------- 权限 ----------------
    static class Admin
    {
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                        return new System.Security.Principal.WindowsPrincipal(id)
                            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }
    }

    // ---------------- 开机自启 ----------------
    // 程序需要管理员权限，所以优先用「任务计划」（登录时以最高权限静默启动，不弹 UAC）；
    // 万一建不了任务，才退回 HKCU Run。
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "WebVideoRemote";
        const string TaskName = "WebVideoRemote";

        public static bool Get()
        {
            try
            {
                if (TaskExists()) return true;
            }
            catch { }
            return UsesRunKey();
        }

        // 是否还停留在「注册表 Run」方式（程序现在需要管理员权限，这种登录时会弹 UAC）
        public static bool UsesRunKey()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    var v = k.GetValue(Name) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                if (on)
                {
                    if (Admin.IsElevated && CreateTask())
                    {
                        RemoveRun();                       // 任务建好了就别再留 Run 项，免得登录时弹两次
                        Log.Write("开机自启 = 开（任务计划，最高权限，登录不弹 UAC）");
                        return;
                    }
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (k == null) return;
                        k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                    }
                    Log.Write("开机自启 = 开（注册表；程序是管理员权限的话，登录时可能会弹 UAC）");
                }
                else
                {
                    DeleteTask();
                    RemoveRun();
                    Log.Write("开机自启 = 关");
                }
            }
            catch (Exception e) { Log.Write("设置开机自启失败: " + e.Message); }
        }

        static void RemoveRun()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null) k.DeleteValue(Name, false);
            }
            catch { }
        }

        static bool TaskExists()
        {
            return RunSch("/query /tn " + TaskName) == 0;
        }

        static bool CreateTask()
        {
            string tr = "\\\"" + Application.ExecutablePath + "\\\"";
            return RunSch("/create /tn " + TaskName + " /tr " + tr + " /sc onlogon /rl highest /f") == 0;
        }

        static void DeleteTask()
        {
            try { RunSch("/delete /tn " + TaskName + " /f"); } catch { }
        }

        static int RunSch(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(8000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch { return -1; }
        }
    }

    // ---------------- 原生 API 小工具 ----------------
    // 命令行模式输出：同时写控制台和 WebVideoRemote-cli.txt
    // （GUI 子系统程序默认拿不到控制台句柄，落盘一份方便排查）
    class TeeWriter : TextWriter
    {
        public TextWriter ConsoleTarget;
        readonly StreamWriter _file;

        public TeeWriter()
        {
            try
            {
                _file = new StreamWriter(Path.Combine(Log.AppDir, "WebVideoRemote-cli.txt"), false, new UTF8Encoding(false));
            }
            catch { }
            ConsoleTarget = Console.Out;
        }

        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void Write(char c) { Safe(() => ConsoleTarget.Write(c)); Safe(() => { if (_file != null) _file.Write(c); }); }
        public override void Write(string s) { Safe(() => ConsoleTarget.Write(s)); Safe(() => { if (_file != null) _file.Write(s); }); }
        public override void WriteLine(string s) { Safe(() => ConsoleTarget.WriteLine(s)); Safe(() => { if (_file != null) _file.WriteLine(s); }); }

        static void Safe(Action a) { try { a(); } catch { } }

        public override void Flush()
        {
            Safe(() => ConsoleTarget.Flush());
            Safe(() => { if (_file != null) _file.Flush(); });
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool AllocConsole();

        public static void AttachParentConsole()
        {
            try
            {
                if (!AttachConsole(-1)) AllocConsole();
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch { }
        }
    }
}
