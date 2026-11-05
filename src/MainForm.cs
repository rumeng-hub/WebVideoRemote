// 主界面 + 托盘
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace WebVideoRemote
{
    class MainForm : Form
    {
        readonly Settings _s;
        readonly SmtcEngine _smtc;
        readonly Router _router;
        readonly KeyboardHook _hook;

        Label _status, _detail, _hint;
        RadioButton _rbBili, _rbDouyin, _rbAuto;
        Button _btnBack, _btnToggle, _btnFwd, _btnDiag, _btnExit;
        CheckBox _chkEnabled, _chkCaps, _chkArrows, _chkUpDown, _chkBridge, _chkSmtc, _chkFocus, _chkBgPost, _chkAuto, _chkTray;
        ComboBox _cmbStep, _cmbHold;
        Label _stepLabel, _holdLabel;
        NotifyIcon _tray;
        System.Windows.Forms.Timer _timer;
        bool _realExit;

        public MainForm()
        {
            _s = Settings.Load();
            _smtc = new SmtcEngine();
            _router = new Router(_s, _smtc);
            _hook = new KeyboardHook(_s, _router);
            _router.Changed += () => { try { BeginInvoke(new Action(RefreshStatus)); } catch { } };
            _router.BridgeHint += what => { try { BeginInvoke(new Action(() => OnBridgeHint(what))); } catch { } };

            BuildUi();
            _hook.CaptureInjected = _s.AcceptInjected; // 仅供诊断
            if (_s.AcceptInjected) _hook.Captured += line => Log.Write("按键: " + line);
            _hook.Install();
            BridgeServer.Instance.Start();   // 浏览器桥接（抖音上下切换用）
            ExtensionPack.RefreshLocalCopyIfExists();   // 让用户目录里的扩展副本也保持最新
            _router.RefreshMode();
            Log.Write("桥接状态: " + (BridgeServer.Instance.ConnectedSites.Length > 0
                ? "已连接 " + BridgeServer.Instance.ConnectedSites
                : "暂无浏览器脚本连接（需要装扩展并刷新抖音/B站页面）"));

            _timer = new System.Windows.Forms.Timer { Interval = 1500 };
            _timer.Tick += (a, b) =>
            {
                _router.RefreshMode();
                CheckBridgeChange();
                RefreshStatus();
            };
            _timer.Start();
            RefreshStatus();
            Log.Write("=== 程序启动，模式=" + _s.Mode + " 步长=" + _s.Step + " ===");
            Log.Write("权限: " + (Admin.IsElevated
                ? "管理员（正常；游戏是管理员权限时热键才收得到）"
                : "普通权限！游戏若以管理员运行，热键在游戏里会完全没反应"));

            // 老版本用注册表自启；程序现在要管理员权限，注册表方式会在登录时弹 UAC，自动迁移到任务计划
            try
            {
                if (Admin.IsElevated && AutoStart.UsesRunKey()) AutoStart.Set(true);
            }
            catch { }
        }

        // 桥接脚本没连上时，弹个托盘气泡把「该怎么办」直接说清楚（30 秒最多一次，由 Router 限流）
        string _lastSites = "";
        bool _hinted;

        void OnBridgeHint(string what)
        {
            try
            {
                if (_tray != null)
                    _tray.ShowBalloonTip(4000, "网页视频遥控器：浏览器脚本没连上",
                        "「" + what + "」需要浏览器里的桥接扩展。\n" +
                        "请检查：① edge://extensions 里「网页视频遥控器 桥接」是否被关闭（Edge 有时会自动关掉开发者模式的扩展）；" +
                        "② 点它下面的「重新加载」；③ 刷新抖音/B站页面。",
                        ToolTipIcon.Warning);
            }
            catch { }
        }

        // 连接/断开、脚本版本变化时各记一条日志，方便事后判断
        void CheckBridgeChange()
        {
            try
            {
                string sites = BridgeServer.Instance.ConnectedSites;
                string ver = BridgeServer.Instance.LastVersion;

                if (sites != _lastSites)
                {
                    _lastSites = sites;
                    if (sites.Length > 0) { Log.Write("浏览器脚本已连接: " + sites); _hinted = false; }
                    else Log.Write("浏览器脚本已断开（扩展被关闭？页面没刷新？）");
                }

                // 版本不一致（含「旧脚本根本没上报版本」）→ 浏览器里跑的还是旧脚本，
                // 这是「功能全都不好使」最常见的原因
                bool verOld = sites.Length > 0 && ver != BridgeServer.ExpectedVer;
                if (verOld && !_verWarned)
                {
                    _verWarned = true;
                    string shown = ver.Length > 0 ? "v" + ver : "未知（旧脚本没有版本号）";
                    Log.Write("⚠ 浏览器脚本版本: " + shown + "，程序需要 v" + BridgeServer.ExpectedVer
                        + " → 请在 edge://extensions 点该扩展的「重新加载」，然后刷新抖音/B站页面");
                    ShowExtHelp(shown);
                }
                else if (!verOld) _verWarned = false;
            }
            catch { }
        }

        // 弹一个带操作按钮的窗口，把「怎么让脚本变成最新」讲清楚（比托盘气泡更难忽略）
        void ShowExtHelp(string shownVersion)
        {
            try
            {
                var f = new Form
                {
                    Text = "桥接脚本版本过旧 —— 需要重新加载扩展",
                    ClientSize = new Size(560, 300),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false, MinimizeBox = false,
                    Font = new Font("Microsoft YaHei UI", 9F)
                };
                var txt = new Label
                {
                    Location = new Point(16, 14), Size = new Size(528, 190),
                    Text =
                        "浏览器里的桥接脚本是「" + shownVersion + "」，程序需要 v" + BridgeServer.ExpectedVer + "。\n" +
                        "旧脚本里没有「松手恢复 1 倍速」「按下即快进」这些修复，所以抖音功能会表现异常。\n\n" +
                        "请按顺序做（只需一次）：\n" +
                        "   1. 点下面的「打开扩展页面」，地址栏会是 edge://extensions\n" +
                        "   2. 找到「网页视频遥控器 桥接」，点它下面的「重新加载」按钮\n" +
                        "   3. 回到抖音页面按 F5 刷新\n\n" +
                        "如果列表里根本没有这个扩展（被 Edge 关掉或删掉了）：\n" +
                        "   点「打开扩展文件夹」→ 在扩展页面打开「开发人员模式」→\n" +
                        "   「加载解压缩的扩展」→ 选中刚打开的那个文件夹。"
                };
                var btnPage = new Button { Location = new Point(16, 216), Size = new Size(150, 34), Text = "打开扩展页面" };
                btnPage.Click += (a, b) =>
                {
                    try { System.Diagnostics.Process.Start("msedge.exe", "edge://extensions"); } catch { }
                    try { System.Diagnostics.Process.Start("chrome.exe", "chrome://extensions"); } catch { }
                };
                var btnDir = new Button { Location = new Point(176, 216), Size = new Size(150, 34), Text = "打开扩展文件夹" };
                btnDir.Click += (a, b) =>
                {
                    try
                    {
                        bool ex;
                        string d = ExtensionPack.Ensure(out ex);
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\"");
                    }
                    catch { }
                };
                var btnOk = new Button { Location = new Point(420, 216), Size = new Size(124, 34), Text = "知道了" };
                btnOk.Click += (a, b) => f.Close();
                f.Controls.AddRange(new Control[] { txt, btnPage, btnDir, btnOk });
                f.ShowDialog(this);
            }
            catch (Exception e) { Log.Write("提示窗口异常: " + e.Message); }
        }

        bool _verWarned;

        Icon BuildIcon()        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(Color.FromArgb(251, 114, 153)))
                    g.FillEllipse(bg, 1, 1, 30, 30);
                using (var f = new SolidBrush(Color.White))
                {
                    Point[] tri = { new Point(13, 9), new Point(13, 23), new Point(24, 16) };
                    g.FillPolygon(f, tri);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void BuildUi()
        {
            Text = "网页视频遥控器  ·  B站 / 抖音 后台控制";
            Icon = BuildIcon();
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(476, 546);
            Font = new Font("Microsoft YaHei UI", 9F);

            _status = new Label
            {
                Location = new Point(12, 10), Size = new Size(428, 22),
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Text = "正在检测播放会话…"
            };
            _detail = new Label
            {
                Location = new Point(12, 32), Size = new Size(428, 20),
                ForeColor = Color.DimGray, Text = ""
            };

            var modeLabel = new Label { Location = new Point(12, 60), Size = new Size(64, 24), Text = "网站模式：" };
            _rbBili = new RadioButton { Location = new Point(84, 59), Size = new Size(62, 24), Text = "B站", Checked = _s.Mode == 0 };
            _rbDouyin = new RadioButton { Location = new Point(150, 59), Size = new Size(62, 24), Text = "抖音", Checked = _s.Mode == 1 };
            _rbAuto = new RadioButton { Location = new Point(216, 59), Size = new Size(230, 24), Text = "自动识别（推荐）", Checked = _s.Mode == 2 };
            _rbBili.CheckedChanged += (a, b) => { if (_rbBili.Checked) { _s.Mode = 0; ApplyMode(); } };
            _rbDouyin.CheckedChanged += (a, b) => { if (_rbDouyin.Checked) { _s.Mode = 1; ApplyMode(); } };
            _rbAuto.CheckedChanged += (a, b) => { if (_rbAuto.Checked) { _s.Mode = 2; ApplyMode(); } };

            _btnBack = new Button { Location = new Point(12, 92), Size = new Size(138, 36) };
            _btnToggle = new Button { Location = new Point(157, 92), Size = new Size(138, 36), Text = "播放 / 暂停" };
            _btnFwd = new Button { Location = new Point(302, 92), Size = new Size(138, 36) };
            _btnBack.Click += (a, b) => { if (_router.CachedMode == 1) _router.SwitchVideo(true); else _router.Seek(false); };
            _btnToggle.Click += (a, b) => _router.Toggle();
            _btnFwd.Click += (a, b) => { if (_router.CachedMode == 1) _router.SwitchVideo(false); else _router.Seek(true); };

            int y = 140;
            _chkEnabled = MakeCheck("启用热键（CapsLock 播放/暂停，方向键快进快退）", y, _s.Enabled, v => _s.Enabled = v); y += 26;
            _chkCaps = MakeCheck("拦截 CapsLock（不切换大小写、指示灯不亮）", y, _s.InterceptCaps, v => _s.InterceptCaps = v); y += 26;
            _chkArrows = MakeCheck("拦截 ← →（游戏里也收不到这两个键）", y, _s.InterceptArrows, v => _s.InterceptArrows = v); y += 26;
            _chkUpDown = MakeCheck("拦截 ↑ ↓（抖音模式下用）", y, _s.InterceptUpDown, v => _s.InterceptUpDown = v); y += 30;

            var chan = new Label { Location = new Point(12, y), Size = new Size(428, 20), Text = "控制通道（从上到下依次尝试）：", ForeColor = Color.DimGray }; y += 22;
            _chkBridge = MakeCheck("浏览器脚本桥接（抖音推荐 · 需装扩展，完全不抢焦点）", y, _s.UseBridge, v => _s.UseBridge = v); y += 26;
            _chkSmtc = MakeCheck("系统媒体控制（B站推荐 · 不抢焦点）", y, _s.UseSmtc, v => _s.UseSmtc = v); y += 26;
            _chkFocus = MakeCheck("抢焦点按键注入（兜底：抖音 ↑↓ / 长按快进）", y, _s.UseFocusInject, v => _s.UseFocusInject = v); y += 26;
            _chkBgPost = MakeCheck("后台投递按键（实验：Chromium 会拒绝，默认关）", y, _s.UseBgPost, v => _s.UseBgPost = v); y += 30;

            _stepLabel = new Label { Location = new Point(12, y + 3), Size = new Size(74, 22), Text = "B站步长：" };
            _cmbStep = new ComboBox { Location = new Point(88, y), Size = new Size(68, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var v in new[] { 5, 10, 15, 30, 60 }) _cmbStep.Items.Add(v + " 秒");
            _cmbStep.SelectedIndex = IndexOfStep(_s.Step);
            _cmbStep.SelectedIndexChanged += (a, b) => { _s.Step = new[] { 5, 10, 15, 30, 60 }[_cmbStep.SelectedIndex]; SaveApply(); };

            _holdLabel = new Label { Location = new Point(164, y + 3), Size = new Size(76, 22), Text = "抖音跳转：" };
            _cmbHold = new ComboBox { Location = new Point(242, y), Size = new Size(68, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var v in new[] { 5, 10, 15, 30 }) _cmbHold.Items.Add(v + " 秒");
            _cmbHold.SelectedIndex = IndexOfHold(_s.DouyinSeek);
            _cmbHold.SelectedIndexChanged += (a, b) => { _s.DouyinSeek = new[] { 5, 10, 15, 30 }[_cmbHold.SelectedIndex]; SaveApply(); };

            var holdMsLabel = new Label { Location = new Point(318, y + 3), Size = new Size(76, 22), Text = "长按阈值：" };
            var cmbLongPress = new ComboBox { Location = new Point(396, y), Size = new Size(68, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            var vals = new[] { 200, 250, 300, 400, 500, 650 };
            foreach (var v in vals) cmbLongPress.Items.Add(v + " ms");
            int idx = 0;
            for (int i = 0; i < vals.Length; i++) if (vals[i] == _s.HoldMs) idx = i;
            cmbLongPress.SelectedIndex = idx;
            cmbLongPress.SelectedIndexChanged += (a, b) => { _s.HoldMs = vals[cmbLongPress.SelectedIndex]; SaveApply(); };

            y += 34;
            _chkAuto = new CheckBox { Location = new Point(12, y), Size = new Size(150, 24), Text = "开机自动启动", Checked = AutoStart.Get() };
            _chkAuto.CheckedChanged += (a, b) => AutoStart.Set(_chkAuto.Checked);
            _chkTray = new CheckBox { Location = new Point(170, y), Size = new Size(180, 24), Text = "关闭窗口时缩到托盘", Checked = _s.MinimizeToTray };
            _chkTray.CheckedChanged += (a, b) => { _s.MinimizeToTray = _chkTray.Checked; _s.Save(); };
            y += 30;

            _hint = new Label { Location = new Point(12, y), Size = new Size(428, 52), ForeColor = Color.Gray };

            _btnDiag = new Button { Location = new Point(12, y + 58), Size = new Size(110, 30), Text = "查看运行日志" };
            _btnDiag.Click += (a, b) => new LogForm().ShowDialog(this);
            var btnSelf = new Button { Location = new Point(128, y + 58), Size = new Size(104, 30), Text = "自检并写日志" };
            btnSelf.Click += (a, b) => { RunSelfTest(); new LogForm().ShowDialog(this); };
            var btnExt = new Button { Location = new Point(238, y + 58), Size = new Size(106, 30), Text = "装桥接扩展" };
            btnExt.Click += (a, b) => OpenExtensionFolder();
            _btnExit = new Button { Location = new Point(350, y + 58), Size = new Size(90, 30), Text = "退出程序" };
            _btnExit.Click += (a, b) => { _realExit = true; Close(); };

            Controls.AddRange(new Control[]
            {
                _status, _detail, modeLabel, _rbBili, _rbDouyin, _rbAuto,
                _btnBack, _btnToggle, _btnFwd,
                _chkEnabled, _chkCaps, _chkArrows, _chkUpDown, chan, _chkBridge, _chkSmtc, _chkFocus, _chkBgPost,
                _stepLabel, _cmbStep, _holdLabel, _cmbHold, holdMsLabel, cmbLongPress,
                _chkAuto, _chkTray, _hint, _btnDiag, btnSelf, btnExt, _btnExit
            });

            BuildTray();
            ApplyMode();
        }

        int IndexOfStep(int v) { var a = new[] { 5, 10, 15, 30, 60 }; for (int i = 0; i < a.Length; i++) if (a[i] == v) return i; return 0; }
        int IndexOfHold(int v) { var a = new[] { 5, 10, 15, 30 }; for (int i = 0; i < a.Length; i++) if (a[i] == v) return i; return 2; }

        CheckBox MakeCheck(string text, int y, bool init, Action<bool> set)
        {
            var c = new CheckBox { Location = new Point(12, y), Size = new Size(428, 24), Text = text, Checked = init };
            c.CheckedChanged += (a, b) => { set(c.Checked); _s.Save(); };
            return c;
        }

        void SaveApply() { _s.Save(); ApplyMode(); }

        // 自检：把「钩子/媒体会话/浏览器窗口/通道开关」全部写进日志，方便定位问题
        void RunSelfTest()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                Log.Write("========== 自检 ==========");
                Log.Write("权限: " + (Admin.IsElevated ? "管理员" : "普通权限（游戏是管理员运行时热键会失效！）"));
                Log.Write("键盘钩子: " + (_hook.Installed ? "已安装" : "安装失败！(可能被杀软拦截)"));
                Log.Write("热键开关: " + (_s.Enabled ? "启用" : "已关闭（热键不会响应！）"));
                Log.Write("网站模式: " + (_s.Mode == 0 ? "B站" : _s.Mode == 1 ? "抖音" : "自动识别")
                    + "  当前判定为: " + (_router.CachedMode == 1 ? "抖音" : "B站"));
                Log.Write("控制通道: 系统媒体=" + (_s.UseSmtc ? "开" : "关")
                    + "  浏览器脚本桥接=" + (_s.UseBridge ? "开" : "关")
                    + (BridgeServer.Instance.Connected ? "（已连接）" : "（未连接，需在浏览器里装扩展）")
                    + "  抢焦点注入=" + (_s.UseFocusInject ? "开" : "关")
                    + "  后台投递=" + (_s.UseBgPost ? "开" : "关(Chromium会拒绝，建议保持关闭)"));
                Log.Write("桥接服务: 127.0.0.1:" + BridgeServer.Port
                    + (BridgeServer.Instance.LastState.Length > 0 ? "  脚本上报: " + BridgeServer.Instance.LastState : "  脚本暂无上报"));
                try
                {
                    var mi = _smtc.Find(_s.Mode == 2 ? 2 : _router.CachedMode);
                    Log.Write(mi == null || mi.Title.Length == 0
                        ? "系统媒体会话: 未检测到（浏览器里要正在播放视频）"
                        : "系统媒体会话: " + mi.App + " / " + mi.Title + " / " + mi.Status
                          + string.Format(" / {0:0.0}s of {1:0.0}s", mi.Position, mi.Duration));
                }
                catch (Exception e) { Log.Write("系统媒体会话: 读取失败 " + e.Message); }
                try
                {
                    var wi = WindowEngine.Find(_s.Mode == 2 ? 2 : _router.CachedMode);
                    Log.Write(wi == null
                        ? "浏览器窗口: 未找到（请打开 Edge/Chrome 的 B站 或 抖音）"
                        : "浏览器窗口: " + wi.Exe + " / " + wi.Title);
                }
                catch (Exception e) { Log.Write("浏览器窗口: 读取失败 " + e.Message); }
                Log.Write("用法提示: 按 CapsLock 看标题栏是否出现「播放/暂停」；没反应就先看这里。");
                Log.Write("==========================");
            });
        }

        void BuildTray()
        {
            _tray = new NotifyIcon { Icon = Icon, Text = "网页视频遥控器", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示设置窗口", null, (a, b) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add(new ToolStripSeparator());
            var miEnabled = new ToolStripMenuItem("启用热键") { Checked = _s.Enabled, CheckOnClick = true };
            miEnabled.CheckedChanged += (a, b) => { _s.Enabled = miEnabled.Checked; _chkEnabled.Checked = miEnabled.Checked; _s.Save(); };
            menu.Items.Add(miEnabled);
            menu.Items.Add(new ToolStripSeparator());
            var miBili = new ToolStripMenuItem("B站模式") { Checked = _s.Mode == 0 };
            var miDouyin = new ToolStripMenuItem("抖音模式") { Checked = _s.Mode == 1 };
            var miAuto = new ToolStripMenuItem("自动识别") { Checked = _s.Mode == 2 };
            miBili.Click += (a, b) => { _s.Mode = 0; _rbBili.Checked = true; ApplyMode(); };
            miDouyin.Click += (a, b) => { _s.Mode = 1; _rbDouyin.Checked = true; ApplyMode(); };
            miAuto.Click += (a, b) => { _s.Mode = 2; _rbAuto.Checked = true; ApplyMode(); };
            menu.Items.Add(miBili); menu.Items.Add(miDouyin); menu.Items.Add(miAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("查看运行日志", null, (a, b) => new LogForm().ShowDialog(this));
            menu.Items.Add("退出", null, (a, b) => { _realExit = true; Close(); });
            menu.Opening += (a, b) =>
            {
                miBili.Checked = _s.Mode == 0; miDouyin.Checked = _s.Mode == 1; miAuto.Checked = _s.Mode == 2;
                miEnabled.Checked = _s.Enabled;
            };
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (a, b) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        }

        void ApplyMode()
        {
            _s.Save();
            bool douyin = _s.Mode == 1;
            if (_s.Mode == 2)
            {
                _btnBack.Text = "快退 / 上一个";
                _btnFwd.Text = "快进 / 下一个";
                _stepLabel.Text = "B站步长：";
                _holdLabel.Text = "抖音跳转：";
                _hint.Text = "自动识别：按当前浏览器里正在播放的网站自动切换。\nCapsLock 播放/暂停；B站 ←/→ 快退快进；抖音 ↑/↓ 换视频、长按 → 快进。";
            }
            else if (douyin)
            {
                _btnBack.Text = "↑ 上一个视频";
                _btnFwd.Text = "↓ 下一个视频";
                _hint.Text = "抖音模式：CapsLock 播放/暂停，↑/↓ 切换视频，←/→ 快退快进。\n长按 → 持续快进，松开恢复（需要勾选「抢焦点按键注入」）。";
            }
            else
            {
                _btnBack.Text = "快退 -" + _s.Step + "s";
                _btnFwd.Text = "快进 +" + _s.Step + "s";
                _hint.Text = "B站模式：CapsLock 播放/暂停，←/→ 按步长快退快进。\n走系统媒体控制，不会打扰全屏游戏。";
            }
            _chkBgPost.Enabled = true;
            RefreshStatus();
        }

        void RefreshStatus()
        {
            RefreshStatusAsync();
        }

        volatile bool _busy;

        // 状态查询放到后台线程，避免界面卡顿
        void RefreshStatusAsync()
        {
            if (_busy) return;
            _busy = true;
            int mode = _s.Mode == 2 ? 2 : _router.CachedMode;
            System.Threading.Tasks.Task.Run(() =>
            {
                MediaInfo mi = null;
                try { mi = _smtc.Find(mode); } catch { }
                try { BeginInvoke(new Action(() => { ApplyStatus(mi); _busy = false; })); }
                catch { _busy = false; }
            });
        }

        void ApplyStatus(MediaInfo mi)
        {
            try
            {
                int mode = _router.CachedMode;
                string sites = BridgeServer.Instance.ConnectedSites;
                string ver = BridgeServer.Instance.LastVersion;
                bool bridge = sites.Length > 0;
                string bridgeText = _s.UseBridge
                    ? (bridge ? "脚本已连接：" + sites + (ver.Length > 0 ? " (v" + ver + ")" : "")
                              : "脚本未连接（点「装桥接扩展」）")
                    : "脚本桥接已关闭";
                if (bridge && ver.Length > 0 && ver != BridgeServer.ExpectedVer)
                    bridgeText = "⚠ 脚本版本过旧 v" + ver + "，需要 v" + BridgeServer.ExpectedVer
                        + "：请在 edge://extensions 点「重新加载」并刷新页面";
                if (!Admin.IsElevated) bridgeText += "    ｜    ⚠ 普通权限：游戏里可能收不到热键";
                if (mi == null || mi.Title.Length == 0)
                {
                    _status.ForeColor = Color.Firebrick;
                    _status.Text = "未检测到浏览器播放会话";
                    _detail.Text = "请先在 Edge / Chrome 里打开并播放 B站 或 抖音    ｜    " + bridgeText;
                }
                else
                {
                    _status.ForeColor = Color.ForestGreen;
                    _status.Text = "控制中（" + (mode == 1 ? "抖音" : "B站") + "）：" + Truncate(mi.Title, 24);
                    _detail.Text = string.Format("{0}  {1} / {2}    ｜    {3}", mi.Status,
                        Time(mi.Position), mi.Duration > 0 ? Time(mi.Duration) : "?", bridgeText);
                }
                Text = "网页视频遥控器  ·  " + (mode == 1 ? "抖音" : "B站") + "  ·  " + (_s.Enabled ? "热键开" : "热键关")
                    + (bridge ? "  ·  脚本已连" : "")
                    + (_router.LastEngine != "-" ? "  ·  " + _router.LastEngine + " " + _router.LastAction : "");
            }
            catch { }
        }

        // 装桥接扩展：确保扩展目录存在（exe 旁边没有就从 exe 里释放出来），再给出可复制的路径
        void OpenExtensionFolder()
        {
            try
            {
                bool extracted;
                string dir = ExtensionPack.Ensure(out extracted);
                if (extracted) Log.Write("exe 旁边没有 extension 文件夹，已从程序内释放到: " + dir);
                new ExtInstallForm(dir).ShowDialog(this);
            }
            catch (Exception e)
            {
                Log.Write("准备扩展目录失败: " + e.Message);
                MessageBox.Show("出错了：" + e.Message, "网页视频遥控器");
            }
        }

        static string Time(double s)
        {
            if (s < 0) s = 0;
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        static string Truncate(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_realExit && e.CloseReason == CloseReason.UserClosing && _s.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                WindowState = FormWindowState.Minimized;
                _tray.ShowBalloonTip(1500, "网页视频遥控器", "已缩到托盘继续工作，热键照常可用。", ToolTipIcon.Info);
                return;
            }
            try { _hook.Uninstall(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            base.OnFormClosing(e);
        }
    }

    // 运行日志窗口
    // 装桥接扩展的小窗口：给出可复制的路径，并尝试打开文件夹
    class ExtInstallForm : Form
    {
        readonly string _dir;
        readonly TextBox _path;

        public ExtInstallForm(string dir)
        {
            _dir = dir;
            Text = "安装浏览器桥接扩展（只需做一次）";
            ClientSize = new Size(600, 336);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            var steps = new Label
            {
                Location = new Point(14, 12), Size = new Size(572, 122),
                Text =
                    "1. 浏览器地址栏输入  edge://extensions   （Chrome 用 chrome://extensions）\n" +
                    "2. 打开页面上的「开发人员模式」开关（通常在左下角）\n" +
                    "3. 点「加载解压缩的扩展」\n" +
                    "4. 在弹出的选择框里，把下面这个路径粘到地址栏并回车，然后「选择文件夹」\n\n" +
                    "装好后回到本程序，状态栏第二行会显示「脚本已连接」。"
            };
            var lab = new Label { Location = new Point(14, 142), Size = new Size(572, 20), Text = "扩展文件夹路径（可直接复制）：" };
            _path = new TextBox
            {
                Location = new Point(14, 164), Size = new Size(572, 24),
                ReadOnly = true, Text = dir, BackColor = Color.White
            };

            var btnOpen = new Button { Location = new Point(14, 200), Size = new Size(120, 32), Text = "打开文件夹" };
            var btnCopy = new Button { Location = new Point(144, 200), Size = new Size(120, 32), Text = "复制路径" };
            var btnClose = new Button { Location = new Point(466, 200), Size = new Size(120, 32), Text = "关闭" };
            btnOpen.Click += (a, b) => TryOpen();
            btnCopy.Click += (a, b) =>
            {
                try { Clipboard.SetText(_dir); MessageBox.Show("路径已复制，粘贴到浏览器的地址栏即可。", "网页视频遥控器"); }
                catch { }
            };
            btnClose.Click += (a, b) => Close();

            var tip = new Label
            {
                Location = new Point(14, 244), Size = new Size(572, 76), ForeColor = Color.Gray,
                Text = "提示：程序一直在 127.0.0.1:17777 等扩展连接，扩展只和本机通信、不联网。\n" +
                       "没装扩展时，抖音的 ↑↓ / 长按快进 会退化成「抢焦点按键注入」，部分机器上会失效。"
            };

            Controls.AddRange(new Control[] { steps, lab, _path, btnOpen, btnCopy, btnClose, tip });
            Shown += (a, b) => TryOpen();   // 打开窗口时顺便试着弹一下资源管理器
            Activated += (a, b) => { _path.Focus(); _path.SelectAll(); };
        }

        void TryOpen()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + _dir + "\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                try { System.Diagnostics.Process.Start(_dir); }
                catch (Exception e)
                {
                    Log.Write("打开扩展文件夹失败: " + e.Message);
                    MessageBox.Show("自动打开文件夹失败了，请手动复制上面的路径：\n\n" + _dir,
                        "网页视频遥控器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }

    class LogForm : Form
    {
        public LogForm()
        {
            Text = "运行日志";
            ClientSize = new Size(620, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Consolas", 9F);
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Dock = DockStyle.Fill, Text = Log.Tail(300), BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            var btnCopy = new Button { Text = "复制全部", Location = new Point(10, 6), Size = new Size(90, 28) };
            btnCopy.Click += (a, b) => { try { Clipboard.SetText(box.Text); MessageBox.Show("已复制到剪贴板"); } catch { } };
            var btnOpen = new Button { Text = "打开日志文件", Location = new Point(110, 6), Size = new Size(110, 28) };
            btnOpen.Click += (a, b) => { try { System.Diagnostics.Process.Start("notepad.exe", Log.FilePath); } catch { } };
            var btnClose = new Button { Text = "关闭", Location = new Point(520, 6), Size = new Size(90, 28) };
            btnClose.Click += (a, b) => Close();
            bar.Controls.AddRange(new Control[] { btnCopy, btnOpen, btnClose });
            Controls.Add(box);
            Controls.Add(bar);
        }
    }
}
