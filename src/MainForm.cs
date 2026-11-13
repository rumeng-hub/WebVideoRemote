// 主界面 + 托盘
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace WebVideoRemote
{
    class MainForm : WvrForm
    {
        readonly Settings _s;
        readonly SmtcEngine _smtc;
        readonly Router _router;
        readonly KeyboardHook _hook;

        // 顶部 / 状态
        Label _status, _detail, _bridge, _hint;
        NeoDot _dot;
        NeoProgress _progress;
        NeoBadge _badge;

        // 页签与页面（页面用 Visibility 切换，避免嵌套透明容器产生绘制残影）
        NeoTabBar _tabs;
        readonly System.Collections.Generic.List<Control> _page0 = new System.Collections.Generic.List<Control>();
        readonly System.Collections.Generic.List<Control> _page1 = new System.Collections.Generic.List<Control>();
        readonly System.Collections.Generic.List<Control> _page2 = new System.Collections.Generic.List<Control>();

        // 控制页
        NeoSegment _segMode;
        NeoButton _btnBack, _btnToggle, _btnFwd;

        // 开关
        NeoSwitch _chkEnabled, _chkCaps, _chkArrows, _chkUpDown;
        NeoSwitch _chkBridge, _chkSmtc, _chkFocus, _chkBgPost;
        NeoSwitch _chkAuto, _chkTray;

        // 参数
        NeoSelect _cmbStep, _cmbHold, _cmbHoldMs;

        // 主题
        NeoButton _btnThemeF, _btnThemeL;
        NeoButton _btnExit;

        NotifyIcon _tray;
        System.Windows.Forms.Timer _timer;
        bool _realExit;

        public MainForm()
        {
            _s = Settings.Load();
            Palette.Current = (UiTheme)_s.Theme;
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
            ExtensionPack.RefreshLocalCopyIfExists();   // 旧版扩展副本也跟着更新
            _router.RefreshMode();
            Log.Write("桥接状态: " + (BridgeServer.Instance.ConnectedSites.Length > 0
                ? "已连接 " + BridgeServer.Instance.ConnectedSites
                : "暂无浏览器脚本连接（需要装扩展并刷新抖音/B站页面）"));

            _timer = new System.Windows.Forms.Timer { Interval = 1500 };
            _timer.Tick += (a, b) =>
            {
                // 自动识别可能在长按期间切换网站；切了就立刻收尾，避免长按状态卡死
                int before = _router.CachedMode;
                _router.RefreshMode();
                if (before != _router.CachedMode) _hook.AbortHold();
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
                bool verOld = _s.UseBridge && sites.Length > 0 && ver != BridgeServer.ExpectedVer;
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
                using (var f = new WvrForm
                {
                    Caption = "桥接脚本版本过旧",
                    ChromeSubtitle = "桥接脚本版本过旧",
                    ClientSize = new Size(620, 356),
                    StartPosition = FormStartPosition.CenterParent,
                    ShowInTaskbar = false,
                    ShowMinimize = false
                })
                {
                    f.Controls.Add(new Label
                    {
                        Location = new Point(20, 56), Size = new Size(580, 190),
                        BackColor = Color.Transparent, ForeColor = Palette.TextSub,
                        Font = Draw.Px(11.5f, FontStyle.Regular),
                        Text =
                            "浏览器里的桥接脚本是「" + shownVersion + "」，程序需要 v" + BridgeServer.ExpectedVer + "。\n" +
                            "旧脚本缺少「松手恢复 1 倍速」「按下即快进」等修复，抖音功能会表现异常。\n\n" +
                            "请按顺序做（只需一次）：\n" +
                            "   1. 点「打开扩展页面」，地址栏会是 edge://extensions\n" +
                            "   2. 找到「网页视频遥控器 桥接」，点它下面的「重新加载」\n" +
                            "   3. 回到抖音页面按 F5 刷新\n\n" +
                            "如果列表里根本没有这个扩展：点「打开扩展文件夹」→ 打开「开发人员模式」→\n" +
                            "「加载解压缩的扩展」→ 选中刚打开的那个文件夹。"
                    });

                    var btnPage = new NeoButton { Location = new Point(20, 264), Size = new Size(150, 38), Text = "打开扩展页面" };
                    btnPage.Click += (a, b) =>
                    {
                        try { System.Diagnostics.Process.Start("msedge.exe", "edge://extensions"); } catch { }
                        try { System.Diagnostics.Process.Start("chrome.exe", "chrome://extensions"); } catch { }
                    };
                    var btnDir = new NeoButton { Location = new Point(182, 264), Size = new Size(150, 38), Text = "打开扩展文件夹" };
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
                    var btnOk = new NeoButton { Location = new Point(480, 264), Size = new Size(120, 38), Text = "知道了", Style = NeoButtonStyle.Primary };
                    btnOk.Click += (a, b) => f.Close();
                    f.Controls.AddRange(new Control[] { btnPage, btnDir, btnOk });
                    f.ShowDialog(this);
                }
            }
            catch (Exception e) { Log.Write("提示窗口异常: " + e.Message); }
        }

        bool _verWarned;

        // ---------------- 界面 ----------------
        void BuildUi()
        {
            Text = "网页视频遥控器";
            Icon = Assets.AppIcon;
            ClientSize = new Size(600, 700);
            ShowMinimize = true;
            ShowClose = true;
            BackColor = Palette.BgTop;

            ApplyTheme();

            // 主视觉上的标题
            Controls.Add(MakeLabel("网页视频遥控器", 22, 54, 360, 42, 27f, Color.White, FontStyle.Bold));
            Controls.Add(MakeLabel("B站 / 抖音 · 在游戏里后台遥控，不切窗口", 24, 100, 380, 20, 11.5f,
                Color.FromArgb(228, 238, 255)));
            _badge = new NeoBadge { Location = new Point(24, 128), Size = new Size(190, 24), Text = "自动识别" };
            Controls.Add(_badge);

            BuildTabs();
            BuildControlPage();
            BuildChannelPage();
            BuildSettingPage();

            var footer = MakeLabel("v2.0  ·  桥接 127.0.0.1:17777  ·  日志 WebVideoRemote.log",
                22, 674, 556, 18, 10f, Palette.TextDim);
            Controls.Add(footer);

            ShowPage(0);
            BuildTray();
            ApplyMode();
            UpdateThemeButtons();
        }

        void UpdateThemeButtons()
        {
            if (_btnThemeF == null) return;
            _btnThemeF.Style = _s.Theme == 0 ? NeoButtonStyle.Primary : NeoButtonStyle.Secondary;
            _btnThemeL.Style = _s.Theme == 1 ? NeoButtonStyle.Primary : NeoButtonStyle.Secondary;
            _btnThemeF.Invalidate();
            _btnThemeL.Invalidate();
        }

        static Label MakeLabel(string text, int x, int y, int w, int h, float px, Color color, FontStyle style = FontStyle.Regular)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, h),
                Font = Draw.Px(px, style), ForeColor = color, BackColor = Color.Transparent,
                AutoSize = false, UseMnemonic = false
            };
        }

        void BuildTabs()
        {
            _tabs = new NeoTabBar
            {
                Location = new Point(0, 188), Size = new Size(600, 46),
                Items = new[] { "控制台", "控制通道", "偏好设置" }
            };
            _tabs.SelectedChanged += ShowPage;
            Controls.Add(_tabs);
        }

        void ShowPage(int index)
        {
            ShowGroup(_page0, index == 0);
            ShowGroup(_page1, index == 1);
            ShowGroup(_page2, index == 2);
        }

        static void ShowGroup(System.Collections.Generic.List<Control> g, bool visible)
        {
            foreach (var c in g) c.Visible = visible;
        }

        void AddPage(Control c, int page)
        {
            Controls.Add(c);
            (page == 0 ? _page0 : page == 1 ? _page1 : _page2).Add(c);
        }

        static T Solid<T>(T c, Color surface) where T : Control { c.BackColor = surface; return c; }

        void BuildControlPage()
        {
            const int Y = 234;
            Color surf = Palette.Card;

            // 状态卡
            var statusCard = new NeoCard { Location = new Point(18, Y + 12), Size = new Size(564, 110) };
            AddPage(statusCard, 0);
            _dot = Solid(new NeoDot { Location = new Point(14, 20), Size = new Size(10, 10) }, surf);
            _status = Solid(MakeLabel("正在检测播放会话…", 32, 12, 512, 28, 14f, Palette.TextMain, FontStyle.Bold), surf);
            _detail = Solid(MakeLabel("", 32, 42, 514, 18, 11f, Palette.TextSub), surf);
            _detail.AutoEllipsis = true;
            _bridge = Solid(MakeLabel("", 32, 64, 514, 18, 10.5f, Palette.TextDim), surf);
            _bridge.AutoEllipsis = true;
            _progress = Solid(new NeoProgress { Location = new Point(32, 94), Size = new Size(514, 6) }, surf);
            statusCard.Controls.AddRange(new Control[] { _dot, _status, _detail, _bridge, _progress });

            // 传输控制
            var transport = new NeoCard { Location = new Point(18, Y + 132), Size = new Size(564, 64), DrawBorder = false };
            AddPage(transport, 0);
            _btnBack = Solid(new NeoButton { Location = new Point(10, 10), Size = new Size(172, 44), Style = NeoButtonStyle.Secondary }, surf);
            _btnToggle = Solid(new NeoButton { Location = new Point(192, 10), Size = new Size(180, 44), Text = "播放 / 暂停", Style = NeoButtonStyle.Primary }, surf);
            _btnFwd = Solid(new NeoButton { Location = new Point(382, 10), Size = new Size(172, 44), Style = NeoButtonStyle.Secondary }, surf);
            _btnBack.Click += (a, b) => { if (_router.CachedMode == 1) _router.SwitchVideo(true); else _router.Seek(false); };
            _btnToggle.Click += (a, b) => _router.Toggle();
            _btnFwd.Click += (a, b) => { if (_router.CachedMode == 1) _router.SwitchVideo(false); else _router.Seek(true); };
            transport.Controls.AddRange(new Control[] { _btnBack, _btnToggle, _btnFwd });

            // 热键开关
            var hot = new NeoCard { Location = new Point(18, Y + 206), Size = new Size(564, 188), Title = "热键" };
            AddPage(hot, 0);
            _chkEnabled = MakeSwitch(hot, "启用热键（CapsLock 播放/暂停，方向键快进快退）", 30, _s.Enabled, v => _s.Enabled = v);
            _chkCaps = MakeSwitch(hot, "拦截 CapsLock（不切换大小写、指示灯不亮）", 68, _s.InterceptCaps, v => _s.InterceptCaps = v);
            _chkArrows = MakeSwitch(hot, "拦截 ← →（游戏里也收不到这两个键）", 106, _s.InterceptArrows, v => _s.InterceptArrows = v);
            _chkUpDown = MakeSwitch(hot, "拦截 ↑ ↓（抖音模式下用）", 144, _s.InterceptUpDown, v => _s.InterceptUpDown = v);

            _hint = MakeLabel("", 20, Y + 394, 560, 34, 10.5f, Palette.TextDim);
            AddPage(_hint, 0);
        }

        void BuildChannelPage()
        {
            const int Y = 234;
            Color surf = Palette.Card;

            var modeCard = new NeoCard { Location = new Point(18, Y + 12), Size = new Size(564, 86), Title = "网站模式" };
            AddPage(modeCard, 1);
            _segMode = Solid(new NeoSegment
            {
                Location = new Point(14, 44), Size = new Size(300, 32),
                Items = new[] { "B站", "抖音", "自动识别" }
            }, surf);
            _segMode.SelectedIndex = _s.Mode;
            _segMode.SelectedChanged += i => { _s.Mode = i; ApplyMode(); };
            var modeTip = Solid(MakeLabel("自动识别会跟随当前正在播放的网站", 330, 50, 220, 20, 10.5f, Palette.TextDim), surf);
            modeCard.Controls.AddRange(new Control[] { _segMode, modeTip });

            var chan = new NeoCard { Location = new Point(18, Y + 108), Size = new Size(564, 196), Title = "控制通道 · 从上到下依次尝试" };
            AddPage(chan, 1);
            _chkBridge = MakeSwitch(chan, "浏览器脚本桥接（抖音推荐 · 需装扩展）", 30, _s.UseBridge, v => _s.UseBridge = v);
            _chkSmtc = MakeSwitch(chan, "系统媒体控制（B站推荐 · 不抢焦点）", 68, _s.UseSmtc, v => _s.UseSmtc = v);
            _chkFocus = MakeSwitch(chan, "抢焦点按键注入（兜底：抖音 ↑↓ / 长按快进）", 106, _s.UseFocusInject, v => _s.UseFocusInject = v);
            _chkBgPost = MakeSwitch(chan, "后台投递按键（实验 · Chromium 会拒绝）", 144, _s.UseBgPost, v => _s.UseBgPost = v);

            var hint = MakeLabel("抖音的 ↑↓ 与长按快进优先走浏览器脚本；没装扩展时会退回抢焦点注入。\n装一次即可，之后 Edge/Chrome 更新也不用重装。",
                20, Y + 312, 560, 36, 10.5f, Palette.TextDim);
            AddPage(hint, 1);

            var extCard = new NeoCard { Location = new Point(18, Y + 352), Size = new Size(564, 64), DrawBorder = false };
            AddPage(extCard, 1);
            var btnExt = Solid(new NeoButton { Location = new Point(12, 12), Size = new Size(190, 40), Text = "装桥接扩展", Style = NeoButtonStyle.Primary }, surf);
            btnExt.Click += (a, b) => OpenExtensionFolder();
            var state = Solid(MakeLabel("扩展状态同时显示在「控制台」状态卡里", 214, 22, 336, 20, 10.5f, Palette.TextDim), surf);
            extCard.Controls.AddRange(new Control[] { btnExt, state });
        }

        void BuildSettingPage()
        {
            const int Y = 234;
            Color surf = Palette.Card;

            var param = new NeoCard { Location = new Point(18, Y + 12), Size = new Size(564, 110), Title = "跳转与长按" };
            AddPage(param, 2);

            var l1 = Solid(MakeLabel("B站步长", 14, 34, 150, 16, 10.5f, Palette.TextDim), surf);
            _cmbStep = new NeoSelect
            {
                Location = new Point(14, 54), Size = new Size(160, 30),
                Items = new[] { "5 秒", "10 秒", "15 秒", "30 秒", "60 秒" },
                SelectedIndex = IndexOfStep(_s.Step)
            };
            _cmbStep.SelectedIndexChanged += (a, b) => { _s.Step = new[] { 5, 10, 15, 30, 60 }[_cmbStep.SelectedIndex]; SaveApply(); };

            var l2 = Solid(MakeLabel("抖音跳转", 196, 34, 150, 16, 10.5f, Palette.TextDim), surf);
            _cmbHold = new NeoSelect
            {
                Location = new Point(196, 54), Size = new Size(160, 30),
                Items = new[] { "5 秒", "10 秒", "15 秒", "30 秒" },
                SelectedIndex = IndexOfHold(_s.DouyinSeek)
            };
            _cmbHold.SelectedIndexChanged += (a, b) => { _s.DouyinSeek = new[] { 5, 10, 15, 30 }[_cmbHold.SelectedIndex]; SaveApply(); };

            var l3 = Solid(MakeLabel("长按阈值", 378, 34, 150, 16, 10.5f, Palette.TextDim), surf);
            var vals = new[] { 200, 250, 300, 400, 500, 650 };
            int idx = 0;
            for (int i = 0; i < vals.Length; i++) if (vals[i] == _s.HoldMs) idx = i;
            _cmbHoldMs = new NeoSelect
            {
                Location = new Point(378, 54), Size = new Size(160, 30),
                Items = new[] { "200 ms", "250 ms", "300 ms", "400 ms", "500 ms", "650 ms" },
                SelectedIndex = idx
            };
            _cmbHoldMs.SelectedIndexChanged += (a, b) => { _s.HoldMs = vals[_cmbHoldMs.SelectedIndex]; SaveApply(); };

            param.Controls.AddRange(new Control[] { l1, _cmbStep, l2, _cmbHold, l3, _cmbHoldMs });

            var boot = new NeoCard { Location = new Point(18, Y + 132), Size = new Size(564, 110), Title = "启动与托盘" };
            AddPage(boot, 2);
            _chkAuto = Solid(new NeoSwitch
            {
                Location = new Point(14, 30), Size = new Size(536, 38),
                Text = "开机自动启动（任务计划 · 登录不弹 UAC）", Checked = AutoStart.Get()
            }, surf);
            _chkAuto.CheckedChanged += (a, b) => AutoStart.Set(_chkAuto.Checked);
            _chkTray = MakeSwitch(boot, "关闭窗口时缩到托盘（热键继续工作）", 68, _s.MinimizeToTray, v => _s.MinimizeToTray = v);
            boot.Controls.Add(_chkAuto);

            var theme = new NeoCard { Location = new Point(18, Y + 252), Size = new Size(564, 96), Title = "角色主题" };
            AddPage(theme, 2);
            _btnThemeF = Solid(new NeoButton { Location = new Point(14, 44), Size = new Size(168, 38), Text = "芙宁娜 · 水色" }, surf);
            _btnThemeL = Solid(new NeoButton { Location = new Point(194, 44), Size = new Size(168, 38), Text = "流萤 · 萤火" }, surf);
            _btnThemeF.Click += (a, b) => SetTheme(0);
            _btnThemeL.Click += (a, b) => SetTheme(1);
            theme.Controls.AddRange(new Control[] { _btnThemeF, _btnThemeL });

            var actionCard = new NeoCard { Location = new Point(18, Y + 352), Size = new Size(564, 64), DrawBorder = false };
            AddPage(actionCard, 2);
            var btnLog = Solid(new NeoButton { Location = new Point(12, 12), Size = new Size(168, 40), Text = "查看运行日志" }, surf);
            btnLog.Click += (a, b) => new LogForm().ShowDialog(this);
            var btnSelf = Solid(new NeoButton { Location = new Point(194, 12), Size = new Size(168, 40), Text = "自检并写日志" }, surf);
            btnSelf.Click += (a, b) => { RunSelfTest(); new LogForm().ShowDialog(this); };
            _btnExit = Solid(new NeoButton { Location = new Point(376, 12), Size = new Size(168, 40), Text = "退出程序", Style = NeoButtonStyle.Danger }, surf);
            _btnExit.Click += (a, b) => { _realExit = true; Close(); };
            actionCard.Controls.AddRange(new Control[] { btnLog, btnSelf, _btnExit });
        }

        NeoSwitch MakeSwitch(Control parent, string text, int y, bool init, Action<bool> set)
        {
            var c = new NeoSwitch
            {
                Location = new Point(14, y), Size = new Size(parent.Width - 28, 38),
                Text = text, Checked = init, BackColor = parent.BackColor
            };
            c.CheckedChanged += (a, b) => { set(c.Checked); _s.Save(); };
            parent.Controls.Add(c);
            return c;
        }

        int IndexOfStep(int v) { var a = new[] { 5, 10, 15, 30, 60 }; for (int i = 0; i < a.Length; i++) if (a[i] == v) return i; return 0; }
        int IndexOfHold(int v) { var a = new[] { 5, 10, 15, 30 }; for (int i = 0; i < a.Length; i++) if (a[i] == v) return i; return 2; }

        void SaveApply() { _s.Save(); ApplyMode(); }

        // ---------------- 主题 ----------------
        void SetTheme(int t)
        {
            if (_s.Theme == t) return;
            _s.Theme = t;
            _s.Save();
            ApplyTheme();
        }

        void ApplyTheme()
        {
            Palette.Current = (UiTheme)_s.Theme;

            var old = BackgroundImage;
            BackgroundImage = UiBackground.Build(ClientSize.Width, ClientSize.Height, Palette.Current);
            if (old != null) old.Dispose();

            if (_badge != null) _badge.Tint = Palette.Accent;
            UpdateThemeButtons();

            Retint(this);
            Invalidate(true);
        }

        void Retint(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                var card = c as NeoCard;
                if (card != null)
                {
                    card.BackColor = Palette.Card;
                    foreach (Control ch in card.Controls)
                        if (ch is Label || ch is NeoSwitch || ch is NeoButton || ch is NeoDot ||
                            ch is NeoProgress || ch is NeoSegment)
                            ch.BackColor = Palette.Card;
                }
                Retint(c);
            }
        }

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
            _tray = new NotifyIcon { Icon = Assets.AppIcon, Text = "网页视频遥控器", Visible = true };
            var menu = new ContextMenuStrip { BackColor = Color.FromArgb(16, 26, 48), ForeColor = Palette.TextMain };
            menu.Renderer = new NeoMenuRenderer();
            menu.Items.Add("显示设置窗口", null, (a, b) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add(new ToolStripSeparator());
            var miEnabled = new ToolStripMenuItem("启用热键") { Checked = _s.Enabled, CheckOnClick = true };
            miEnabled.CheckedChanged += (a, b) => { _s.Enabled = miEnabled.Checked; _chkEnabled.Checked = miEnabled.Checked; _s.Save(); };
            menu.Items.Add(miEnabled);
            menu.Items.Add(new ToolStripSeparator());
            var miBili = new ToolStripMenuItem("B站模式") { Checked = _s.Mode == 0 };
            var miDouyin = new ToolStripMenuItem("抖音模式") { Checked = _s.Mode == 1 };
            var miAuto = new ToolStripMenuItem("自动识别") { Checked = _s.Mode == 2 };
            miBili.Click += (a, b) => { _s.Mode = 0; _segMode.SelectedIndex = 0; ApplyMode(); };
            miDouyin.Click += (a, b) => { _s.Mode = 1; _segMode.SelectedIndex = 1; ApplyMode(); };
            miAuto.Click += (a, b) => { _s.Mode = 2; _segMode.SelectedIndex = 2; ApplyMode(); };
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
            if (_segMode.SelectedIndex != _s.Mode) _segMode.SelectedIndex = _s.Mode;
            bool douyin = _s.Mode == 1;
            if (_s.Mode == 2)
            {
                _btnBack.Text = "快退 / 上一个";
                _btnFwd.Text = "快进 / 下一个";
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
            if (_badge != null)
                _badge.Text = (_s.Mode == 0 ? "B站模式" : _s.Mode == 1 ? "抖音模式" : "自动识别")
                    + (_s.Enabled ? " · 热键开" : " · 热键关");
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
                    _status.ForeColor = Palette.TextMain;
                    _status.Text = "未检测到浏览器播放会话";
                    _detail.Text = "请先在 Edge / Chrome 里打开并播放 B站 或 抖音";
                    _dot.DotColor = Palette.Error;
                    _progress.Value = 0;
                }
                else
                {
                    _status.ForeColor = Palette.Ok;
                    _status.Text = "控制中（" + (mode == 1 ? "抖音" : "B站") + "）：" + Truncate(mi.Title, 24);
                    _detail.Text = string.Format("{0}    {1} / {2}", mi.Status,
                        Time(mi.Position), mi.Duration > 0 ? Time(mi.Duration) : "?");
                    _dot.DotColor = Palette.Ok;
                    _progress.Value = mi.Duration > 1 ? mi.Position / mi.Duration : 0;
                }
                _bridge.Text = bridgeText;
                _bridge.ForeColor = (!Admin.IsElevated || bridgeText.StartsWith("⚠")) ? Palette.Warn : Palette.TextDim;
                _badge.Text = (_s.Mode == 0 ? "B站模式" : _s.Mode == 1 ? "抖音模式" : "自动识别")
                    + (_s.Enabled ? " · 热键开" : " · 热键关");

                Text = "网页视频遥控器  ·  " + (mode == 1 ? "抖音" : "B站") + "  ·  " + (_s.Enabled ? "热键开" : "热键关")
                    + (bridge ? "  ·  脚本已连" : "")
                    + (_router.LastEngine != "-" ? "  ·  " + _router.LastEngine + " " + _router.LastAction : "");
            }
            catch { }
        }

        // 装桥接扩展：打开安装目录里的 extension（由安装程序释放好），给出可复制的路径
        void OpenExtensionFolder()
        {
            try
            {
                bool extracted;
                string dir = ExtensionPack.Ensure(out extracted);
                if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "manifest.json")))
                    Log.Write("扩展目录缺少 manifest.json: " + dir);
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

    // 装桥接扩展的小窗口：给出可复制的路径，并尝试打开文件夹
    class ExtInstallForm : WvrForm
    {
        readonly string _dir;
        readonly NeoTextBox _path;

        public ExtInstallForm(string dir)
        {
            _dir = dir;
            Caption = "安装浏览器桥接扩展";
            ChromeSubtitle = "安装浏览器桥接扩展";
            ClientSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowMinimize = false;

            Controls.Add(MakeText(
                "1. 浏览器地址栏输入  edge://extensions   （Chrome 用 chrome://extensions）\n" +
                "2. 打开页面上的「开发人员模式」开关（通常在左下角）\n" +
                "3. 点「加载解压缩的扩展」\n" +
                "4. 在弹出的选择框里，把下面这个路径粘到地址栏并回车，然后「选择文件夹」\n\n" +
                "装好后回到本程序，状态栏第二行会显示「脚本已连接」。",
                20, 54, 600, 108, 11.5f, Palette.TextSub));

            Controls.Add(MakeText("扩展文件夹路径（可直接复制）：", 20, 168, 400, 18, 10.5f, Palette.TextDim));

            var field = new NeoCard { Location = new Point(20, 190), Size = new Size(600, 40), Radius = 9 };
            Controls.Add(field);
            _path = new NeoTextBox
            {
                Location = new Point(12, 10), Size = new Size(576, 22),
                ReadOnly = true, Text = dir
            };
            field.Controls.Add(_path);

            var btnOpen = new NeoButton { Location = new Point(20, 244), Size = new Size(132, 38), Text = "打开文件夹", Style = NeoButtonStyle.Primary };
            var btnCopy = new NeoButton { Location = new Point(164, 244), Size = new Size(120, 38), Text = "复制路径" };
            var btnClose = new NeoButton { Location = new Point(514, 244), Size = new Size(106, 38), Text = "关闭" };
            btnOpen.Click += (a, b) => TryOpen();
            btnCopy.Click += (a, b) =>
            {
                try { Clipboard.SetText(_dir); MessageBox.Show("路径已复制，粘贴到浏览器的地址栏即可。", "网页视频遥控器"); }
                catch { }
            };
            btnClose.Click += (a, b) => Close();
            Controls.AddRange(new Control[] { btnOpen, btnCopy, btnClose });

            Controls.Add(MakeText(
                "提示：程序一直在 127.0.0.1:17777 等扩展连接，扩展只和本机通信、不联网。\n" +
                "没装扩展时，抖音的 ↑↓ / 长按快进 会退化成「抢焦点按键注入」，部分机器上会失效。",
                20, 296, 600, 60, 10.5f, Palette.TextDim));

            Shown += (a, b) => TryOpen();   // 打开窗口时顺便试着弹一下资源管理器
            Activated += (a, b) => { _path.Focus(); _path.SelectAll(); };
        }

        static Label MakeText(string text, int x, int y, int w, int h, float px, Color color)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, h),
                Font = Draw.Px(px, FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent,
                AutoSize = false, UseMnemonic = false
            };
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

    class LogForm : WvrForm
    {
        public LogForm()
        {
            Caption = "运行日志";
            ChromeSubtitle = "运行日志";
            ClientSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowMinimize = false;

            var shell = new NeoCard { Location = new Point(16, 52), Size = new Size(668, 364), Radius = 12 };
            Controls.Add(shell);
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                BorderStyle = BorderStyle.None,
                Location = new Point(12, 12), Size = new Size(644, 340),
                Text = Log.Tail(300), BackColor = Color.FromArgb(10, 17, 32), ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 12F, FontStyle.Regular, GraphicsUnit.Pixel)
            };
            shell.Controls.Add(box);

            var btnCopy = new NeoButton { Location = new Point(16, 428), Size = new Size(104, 36), Text = "复制全部" };
            btnCopy.Click += (a, b) => { try { Clipboard.SetText(box.Text); MessageBox.Show("已复制到剪贴板"); } catch { } };
            var btnOpen = new NeoButton { Location = new Point(128, 428), Size = new Size(124, 36), Text = "打开日志文件" };
            btnOpen.Click += (a, b) => { try { System.Diagnostics.Process.Start("notepad.exe", Log.FilePath); } catch { } };
            var btnClose = new NeoButton { Location = new Point(584, 428), Size = new Size(100, 36), Text = "关闭" };
            btnClose.Click += (a, b) => Close();
            Controls.AddRange(new Control[] { btnCopy, btnOpen, btnClose });
        }
    }
}
