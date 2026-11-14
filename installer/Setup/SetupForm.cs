// 网页视频遥控器 安装向导
// 常见的「选择安装路径」流程：欢迎 → 选择位置/选项 → 释放文件 → 完成。
// 安装负载（payload.zip）里是程序主体 + .NET 运行时 + assets 素材 + extension 扩展 + 卸载程序。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WebVideoRemote
{
    class SetupForm : WvrForm
    {
        const string AppDisplay = "网页视频遥控器";
        const string ExeName = "网页视频遥控器.exe";
        const string UninstallName = "卸载.exe";
        const string PayloadResource = "payload.zip";
        // 卸载清单：卸载时只删这里列出的文件，避免误删用户选的目录里的其他文件
        public const string ManifestName = "WebVideoRemote-uninstall.files";

        readonly List<Control>[] _pages = new List<Control>[4];

        // 欢迎页
        Label _sizeHint;

        // 选择位置页
        TextBox _txtPath;
        Label _spaceHint;
        NeoSwitch _optDesktop, _optStart, _optRun;

        // 进度页
        Label _status;
        NeoProgress _bar;

        // 完成页
        Label _doneText;

        // 按钮
        NeoButton _btnNext, _btnInstall, _btnCancel, _btnOpen, _btnFinish;

        long _payloadBytes = -1;
        string _installedDir;

        public SetupForm()
        {
            Caption = AppDisplay;
            ChromeSubtitle = "安装向导";
            ShowMinimize = false;
            Icon = Assets.AppIcon;
            ClientSize = new Size(760, 584);
            BackColor = Palette.BgTop;
            BackgroundImage = UiBackground.Build(ClientSize.Width, ClientSize.Height, UiTheme.Furina);

            BuildUi();
            ShowPage(0);
        }

        // ---------------- 界面 ----------------
        void BuildUi()
        {
            // 主视觉上的标题
            Controls.Add(Lbl(AppDisplay, 24, 50, 420, 44, 27f, Color.White, FontStyle.Bold));
            Controls.Add(Lbl("B站 / 抖音 · 在游戏里后台遥控，不切窗口", 26, 98, 460, 22, 11.5f,
                Color.FromArgb(228, 238, 255)));

            // 版本徽标：宽度按文字实测，避免拉出一截空白
            var badge = new NeoBadge { Location = new Point(26, 126), Size = new Size(236, 24), Text = "安装向导 · 版本 " + AppVersion };
            try
            {
                using (var g = CreateGraphics())
                {
                    int tw = TextRenderer.MeasureText(g, badge.Text, Draw.Px(10.5f, FontStyle.Regular), Size.Empty,
                        TextFormatFlags.NoPadding).Width;
                    badge.Width = Math.Max(120, tw + 42);
                }
            }
            catch { }
            Controls.Add(badge);

            BuildWelcomePage();
            BuildPathPage();
            BuildProgressPage();
            BuildDonePage();

            // 底部提示：离底边留足 28px 余量（高 DPI 下布局会整体放大，贴边会被裁掉一半）
            Controls.Add(new Label
            {
                Text = "安装信息会写入当前用户的「应用和功能」，之后可在 Windows 设置里直接卸载。",
                Location = new Point(26, 550), Size = new Size(708, 22),
                AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false,
                ForeColor = Palette.TextSub, Font = Draw.Px(10f, FontStyle.Regular)
            });

            UpdatePayloadLabels();
        }

        void BuildWelcomePage()
        {
            var card = new NeoCard { Location = new Point(26, 200), Size = new Size(708, 244) };

            card.Controls.Add(CardLbl("欢迎安装 " + AppDisplay, 20, 14, 660, 28, 15f, Palette.TextMain, FontStyle.Bold));
            card.Controls.Add(CardLbl(
                "安装程序会把程序运行所需的全部文件释放到你选择的文件夹：\r\n" +
                "程序主体、.NET 运行时、界面素材（assets）以及浏览器桥接扩展（extension）。\r\n\r\n" +
                "主程序 exe 里只保留运行必需的代码，素材和扩展不再打包进 exe，\r\n" +
                "统一由安装步骤释放，后续升级素材也不需要重新编译主程序。\r\n\r\n" +
                "点「下一步」选择安装位置。",
                20, 50, 668, 150, 11.2f, Palette.TextSub));

            _sizeHint = CardLbl("", 20, 208, 668, 20, 10f, Palette.TextSub);
            card.Controls.Add(_sizeHint);

            _btnCancel = new NeoButton { Text = "取消", Style = NeoButtonStyle.Ghost, Location = new Point(26, 470), Size = new Size(110, 42) };
            _btnCancel.Click += (a, b) => Close();

            _btnNext = new NeoButton { Text = "下一步", Style = NeoButtonStyle.Primary, Location = new Point(612, 470), Size = new Size(122, 42) };
            _btnNext.Click += (a, b) => ShowPage(1);

            Controls.AddRange(new Control[] { card, _btnCancel, _btnNext });
            _pages[0] = new List<Control> { card, _btnCancel, _btnNext };
        }

        void BuildPathPage()
        {
            var cardA = new NeoCard { Location = new Point(26, 200), Size = new Size(708, 112) };
            cardA.Controls.Add(CardLbl("安装位置", 20, 12, 300, 22, 12f, Palette.TextMain, FontStyle.Bold));

            var pathBox = new Panel { Location = new Point(20, 40), Size = new Size(526, 40), BackColor = Color.FromArgb(11, 19, 36) };
            _txtPath = new NeoTextBox { Location = new Point(10, 9), Size = new Size(506, 22), Text = DefaultDir };
            pathBox.Controls.Add(_txtPath);
            cardA.Controls.Add(pathBox);

            var browse = new NeoButton { Text = "浏览…", Location = new Point(558, 40), Size = new Size(130, 40) };
            browse.Click += (a, b) => BrowseFolder();
            cardA.Controls.Add(browse);

            _spaceHint = CardLbl("默认安装在当前用户目录，不需要管理员权限；点「浏览…」可以换到 D 盘等位置。",
                20, 82, 668, 22, 10f, Palette.TextDim);
            cardA.Controls.Add(_spaceHint);

            var cardB = new NeoCard { Location = new Point(26, 322), Size = new Size(708, 142) };
            _optDesktop = new NeoSwitch
            {
                Text = "创建桌面快捷方式", Checked = true, BackColor = Palette.Card,
                Location = new Point(16, 10), Size = new Size(676, 36)
            };
            _optStart = new NeoSwitch
            {
                Text = "创建开始菜单快捷方式（含卸载入口）", Checked = true, BackColor = Palette.Card,
                Location = new Point(16, 52), Size = new Size(676, 36)
            };
            _optRun = new NeoSwitch
            {
                Text = "安装完成后运行 " + AppDisplay, Checked = true, BackColor = Palette.Card,
                Location = new Point(16, 94), Size = new Size(676, 36)
            };
            cardB.Controls.AddRange(new Control[] { _optDesktop, _optStart, _optRun });

            _btnInstall = new NeoButton { Text = "安装", Style = NeoButtonStyle.Primary, Location = new Point(612, 470), Size = new Size(122, 42) };
            _btnInstall.Click += (a, b) => { Log.Write("点击安装，路径=" + _txtPath.Text); StartInstall(); };

            var back = new NeoButton { Text = "上一步", Location = new Point(474, 470), Size = new Size(120, 42) };
            back.Click += (a, b) => ShowPage(0);

            Controls.AddRange(new Control[] { cardA, cardB, back, _btnInstall });
            _pages[1] = new List<Control> { cardA, cardB, back, _btnInstall };
        }

        void BuildProgressPage()
        {
            var card = new NeoCard { Location = new Point(26, 200), Size = new Size(708, 190) };
            card.Controls.Add(CardLbl("正在安装", 20, 14, 400, 28, 15f, Palette.TextMain, FontStyle.Bold));

            _status = CardLbl("准备安装…", 20, 52, 668, 24, 11.5f, Palette.TextSub);
            card.Controls.Add(_status);

            _bar = new NeoProgress { Location = new Point(20, 96), Size = new Size(668, 8), BackColor = Palette.Card };
            card.Controls.Add(_bar);

            card.Controls.Add(CardLbl(
                "安装过程中请不要关闭窗口。\r\n" +
                "如果杀毒软件弹出提示，请选择「允许」（本程序只在本机释放文件，不联网）。",
                20, 118, 668, 44, 10f, Palette.TextDim));

            var cancel = new NeoButton { Text = "取消", Style = NeoButtonStyle.Ghost, Location = new Point(26, 470), Size = new Size(110, 42), Enabled = false };
            Controls.AddRange(new Control[] { card, cancel });
            _pages[2] = new List<Control> { card, cancel };
        }

        void BuildDonePage()
        {
            var card = new NeoCard { Location = new Point(26, 200), Size = new Size(708, 250) };
            card.Controls.Add(CardLbl("安装完成", 20, 14, 200, 28, 15f, Palette.Ok, FontStyle.Bold));
            card.Controls.Add(CardLbl("100%", 590, 16, 98, 24, 13f, Palette.Ok, FontStyle.Bold));

            // 满格进度条：给「装完了」一个明确的视觉确认
            var doneBar = new NeoProgress { Location = new Point(20, 50), Size = new Size(668, 6), BackColor = Palette.Card, Value = 1 };
            card.Controls.Add(doneBar);

            _doneText = CardLbl("", 20, 72, 668, 160, 11f, Palette.TextSub);
            card.Controls.Add(_doneText);

            _btnOpen = new NeoButton { Text = "打开安装文件夹", Location = new Point(442, 470), Size = new Size(160, 42) };
            _btnOpen.Click += (a, b) => OpenFolder();

            _btnFinish = new NeoButton { Text = "完成", Style = NeoButtonStyle.Primary, Location = new Point(612, 470), Size = new Size(122, 42) };
            _btnFinish.Click += (a, b) => FinishInstall();

            Controls.AddRange(new Control[] { card, _btnOpen, _btnFinish });
            _pages[3] = new List<Control> { card, _btnOpen, _btnFinish };
        }

        static Label Lbl(string text, int x, int y, int w, int h, float px, Color color, FontStyle style = FontStyle.Regular)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, h),
                AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false,
                ForeColor = color, Font = Draw.Px(px, style)
            };
        }

        static Label CardLbl(string text, int x, int y, int w, int h, float px, Color color, FontStyle style = FontStyle.Regular)
        {
            var l = Lbl(text, x, y, w, h, px, color, style);
            l.BackColor = Palette.Card;
            return l;
        }

        void ShowPage(int index)
        {
            for (int p = 0; p < _pages.Length; p++)
            {
                if (_pages[p] == null) continue;
                foreach (var c in _pages[p]) c.Visible = (p == index);
            }
            Invalidate(true);
        }

        // ---------------- 安装逻辑 ----------------
        static string DefaultDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "WebVideoRemote");
            }
        }

        static string AppVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v == null) return "2.0.0";
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        long PayloadBytes
        {
            get
            {
                if (_payloadBytes >= 0) return _payloadBytes;
                long total = 0;
                try
                {
                    using (var s = OpenPayload())
                    {
                        if (s != null)
                            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                                foreach (var e in zip.Entries) total += e.Length;
                    }
                }
                catch { }
                _payloadBytes = total;
                return total;
            }
        }

        static Stream OpenPayload()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
        }

        void UpdatePayloadLabels()
        {
            string size = (PayloadBytes / 1048576.0).ToString("0.#");
            if (_sizeHint != null)
                _sizeHint.Text = "版本 " + AppVersion + "   ·   安装大小约 " + size
                    + " MB   ·   Windows 10 1809 及以上（64 位）";
        }

        void BrowseFolder()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "选择 " + AppDisplay + " 的安装位置";
                d.ShowNewFolderButton = true;
                try { d.SelectedPath = _txtPath.Text; } catch { }
                if (d.ShowDialog(this) == DialogResult.OK && d.SelectedPath.Length > 0)
                    _txtPath.Text = d.SelectedPath;
            }
        }

        bool CheckDir(string input, out string dir, out string err)
        {
            err = null;
            string p = (input ?? "").Trim().Trim('"');
            dir = p;
            if (p.Length == 0) { err = "请填写安装位置。"; return false; }
            try { p = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
            catch { err = "安装位置不是有效的路径。"; return false; }
            dir = p;

            try
            {
                if (File.Exists(p)) { err = "这个路径已经是一个文件，请选择一个文件夹。"; return false; }
                Directory.CreateDirectory(p);
                string probe = Path.Combine(p, ".wvr-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch
            {
                err = "没有写入权限。\n请换一个文件夹（例如 D:\\Programs\\WebVideoRemote），\n" +
                      "或关闭安装程序后右键选择「以管理员身份运行」。";
                return false;
            }

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(p));
                long need = PayloadBytes + 60L * 1024 * 1024;
                if (drive.AvailableFreeSpace > 0 && drive.AvailableFreeSpace < need)
                {
                    err = "磁盘剩余空间不足：需要约 " + (need / 1048576.0).ToString("0")
                        + " MB，当前可用 " + (drive.AvailableFreeSpace / 1048576.0).ToString("0") + " MB。";
                    return false;
                }
            }
            catch { }

            return true;
        }

        void StartInstall()
        {
            string dir, err;
            if (!CheckDir(_txtPath.Text, out dir, out err))
            {
                Log.Write("安装目录校验失败: " + err);
                MessageBox.Show(this, err, AppDisplay + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Log.Write("安装目录校验通过: " + dir);
            if (!StopRunningApp()) return;

            _txtPath.Text = dir;
            bool desktop = _optDesktop.Checked;
            bool startMenu = _optStart.Checked;

            _btnInstall.Enabled = false;
            _bar.Value = 0;
            _status.Text = "准备安装…";
            ShowPage(2);

            var th = new Thread(() => DoInstall(dir, desktop, startMenu));
            th.IsBackground = true;
            th.Start();
        }

        bool StopRunningApp()
        {
            var procs = new List<Process>();
            foreach (var n in new[] { ExeName.Replace(".exe", ""), "WebVideoRemote" })
            {
                try { procs.AddRange(Process.GetProcessesByName(n)); } catch { }
            }
            if (procs.Count == 0) return true;

            var r = MessageBox.Show(this,
                AppDisplay + " 正在运行。\n安装前需要先退出它，是否现在自动关闭？",
                AppDisplay + " 安装程序", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;

            foreach (var p in procs)
                try { p.CloseMainWindow(); } catch { }
            foreach (var p in procs)
            {
                try
                {
                    if (!p.WaitForExit(3000)) p.Kill();
                    p.WaitForExit(2000);
                }
                catch { }
            }
            Thread.Sleep(300);
            return true;
        }

        void DoInstall(string dir, bool desktop, bool startMenu)
        {
            try
            {
                long total = 0, done = 0;
                using (var s = OpenPayload())
                {
                    if (s == null)
                        throw new Exception("安装包不完整：缺少安装数据。请重新下载完整的安装程序。");

                    using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                    {
                        foreach (var e in zip.Entries) total += e.Length;

                        var manifest = new List<string>();
                        string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        foreach (var e in zip.Entries)
                        {
                            string rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                            string dest = Path.GetFullPath(Path.Combine(dir, rel));
                            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                            if (rel.EndsWith(Path.DirectorySeparatorChar.ToString()))
                            {
                                Directory.CreateDirectory(dest);
                                manifest.Add(rel);
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            e.ExtractToFile(dest, true);
                            manifest.Add(rel);
                            done += e.Length;

                            int pct = total > 0 ? (int)(done * 100L / total) : 0;
                            string name = Path.GetFileName(dest);
                            Post(() =>
                            {
                                _bar.Value = pct / 100.0;
                                _status.Text = "正在释放：" + name;
                            });
                        }

                        // 卸载时按这份清单精确删除；写失败只影响卸载精度，不算安装失败
                        try
                        {
                            File.WriteAllLines(Path.Combine(dir, ManifestName), manifest, new UTF8Encoding(true));
                        }
                        catch (Exception me) { Log.Write("写卸载清单失败: " + me.Message); }
                    }
                }

                Post(() =>
                {
                    _bar.Value = 1;
                    _status.Text = "正在创建快捷方式与卸载信息…";
                });

                CreateShortcuts(dir, desktop, startMenu);
                WriteUninstallInfo(dir, done);

                _installedDir = dir;
                Log.Write("安装完成: " + dir + "，共 " + (done / 1048576.0).ToString("0.#") + " MB");

                Post(() =>
                {
                    _doneText.Text =
                        "已安装到：\r\n" + dir + "\r\n\r\n" +
                        "· 桌面 / 开始菜单里可以找到「" + AppDisplay + "」\r\n" +
                        "· 首次启动会引导安装浏览器桥接扩展（抖音上下切换、长按快进需要）\r\n" +
                        "· 卸载：Windows 设置 → 应用 → " + AppDisplay + "，\r\n" +
                        "   或运行安装目录里的「" + UninstallName + "」";
                    ShowPage(3);
                });
            }
            catch (Exception ex)
            {
                Log.Write("安装失败: " + ex);
                Post(() =>
                {
                    MessageBox.Show(this, "安装失败：\n" + ex.Message,
                        AppDisplay + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _btnInstall.Enabled = true;
                    ShowPage(1);
                });
            }
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        void CreateShortcuts(string dir, bool desktop, bool startMenu)
        {
            string exe = Path.Combine(dir, ExeName);
            if (desktop)
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    AppDisplay + ".lnk");
                MakeShortcut(path, exe, dir, AppDisplay);
            }
            if (startMenu)
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                MakeShortcut(Path.Combine(programs, AppDisplay + ".lnk"), exe, dir, AppDisplay);
                MakeShortcut(Path.Combine(programs, "卸载 " + AppDisplay + ".lnk"),
                    Path.Combine(dir, UninstallName), dir, "卸载 " + AppDisplay);
            }
        }

        static void MakeShortcut(string lnk, string target, string workDir, string desc)
        {
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return;
                dynamic shell = Activator.CreateInstance(type);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target;
                sc.WorkingDirectory = workDir;
                sc.IconLocation = target + ",0";
                sc.Description = desc;
                sc.Save();
            }
            catch (Exception e) { Log.Write("创建快捷方式失败 " + lnk + ": " + e.Message); }
        }

        void WriteUninstallInfo(string dir, long bytes)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WebVideoRemote"))
            {
                k.SetValue("DisplayName", AppDisplay);
                k.SetValue("DisplayVersion", AppVersion);
                k.SetValue("Publisher", "WebVideoRemote");
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", Path.Combine(dir, ExeName));
                k.SetValue("UninstallString", "\"" + Path.Combine(dir, UninstallName) + "\"");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
            }
        }

        void OpenFolder()
        {
            try { Process.Start("explorer.exe", "\"" + _installedDir + "\""); }
            catch (Exception e) { Log.Write("打开目录失败: " + e.Message); }
        }

        void FinishInstall()
        {
            if (_optRun.Checked && !string.IsNullOrEmpty(_installedDir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(_installedDir, ExeName))
                    {
                        WorkingDirectory = _installedDir,
                        UseShellExecute = true
                    });
                }
                catch (Exception e)
                {
                    MessageBox.Show(this, "启动失败：" + e.Message, AppDisplay, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            Close();
        }
    }
}
