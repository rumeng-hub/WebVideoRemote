// 卸载程序：关闭主程序 → 删快捷方式 → 清理卸载注册表 → 删除安装目录（含自删除）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WebVideoRemote
{
    class UninstallForm : WvrForm
    {
        const string AppDisplay = "网页视频遥控器";
        // 与 SetupForm.ManifestName 对应：安装时写下的释放文件清单
        const string ManifestName = "WebVideoRemote-uninstall.files";

        readonly string _dir;
        Label _status;
        NeoButton _btnGo, _btnCancel;

        public UninstallForm()
        {
            _dir = Path.GetDirectoryName(Application.ExecutablePath);

            Caption = AppDisplay;
            ChromeSubtitle = "卸载";
            ShowMinimize = false;
            Icon = Assets.AppIcon;
            ClientSize = new Size(620, 440);
            BackColor = Palette.BgTop;
            BackgroundImage = UiBackground.Build(ClientSize.Width, ClientSize.Height, UiTheme.Furina);

            BuildUi();
        }

        void BuildUi()
        {
            Controls.Add(Lbl("卸载 " + AppDisplay, 24, 50, 480, 40, 24f, Color.White, FontStyle.Bold));
            Controls.Add(Lbl("删除程序文件、快捷方式和卸载记录", 26, 96, 500, 22, 11.5f,
                Color.FromArgb(228, 238, 255)));

            var card = new NeoCard { Location = new Point(26, 192), Size = new Size(568, 150) };
            card.Controls.Add(CardLbl(
                "安装位置：\r\n" + _dir + "\r\n\r\n" +
                "将删除：程序文件、桌面 / 开始菜单快捷方式、「应用和功能」里的卸载记录。\r\n" +
                "不会删除浏览器里的桥接扩展 —— 如需移除，请在 edge://extensions 里点「移除」。",
                18, 14, 532, 124, 10.5f, Palette.TextSub));
            Controls.Add(card);

            _status = Lbl("点「卸载」开始。", 28, 350, 500, 20, 10.5f, Palette.TextDim);
            Controls.Add(_status);

            _btnCancel = new NeoButton { Text = "取消", Location = new Point(26, 376), Size = new Size(110, 42) };
            _btnCancel.Click += (a, b) => Close();

            _btnGo = new NeoButton { Text = "卸载", Style = NeoButtonStyle.Danger, Location = new Point(484, 376), Size = new Size(110, 42) };
            _btnGo.Click += (a, b) => StartUninstall();

            Controls.AddRange(new Control[] { _btnCancel, _btnGo });
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

        static Label CardLbl(string text, int x, int y, int w, int h, float px, Color color)
        {
            var l = Lbl(text, x, y, w, h, px, color);
            l.BackColor = Palette.Card;
            return l;
        }

        void StartUninstall()
        {
            _btnGo.Enabled = false;
            _btnCancel.Enabled = false;
            _status.Text = "正在关闭正在运行的程序…";

            var th = new Thread(DoUninstall);
            th.IsBackground = true;
            th.Start();
        }

        void DoUninstall()
        {
            try
            {
                Kill(AppDisplay);
                Kill("WebVideoRemote");

                Post("正在删除快捷方式…");
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                TryDelete(Path.Combine(desktop, AppDisplay + ".lnk"));
                TryDelete(Path.Combine(programs, AppDisplay + ".lnk"));
                TryDelete(Path.Combine(programs, "卸载 " + AppDisplay + ".lnk"));
                TryDeleteDir(Path.Combine(programs, AppDisplay));

                Post("正在清理卸载记录…");
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WebVideoRemote", false);
                }
                catch (Exception e) { Log.Write("删除卸载注册表失败: " + e.Message); }

                // 旧版本曾把扩展释放到用户目录，这里一并清掉
                try
                {
                    string legacy = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebVideoRemote");
                    if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
                }
                catch (Exception e) { Log.Write("清理用户目录副本失败: " + e.Message); }

                Post("正在删除程序文件…");
                // 有卸载清单：只删清单里的文件，绝不碰安装目录里的其他文件；
                // 没有清单（旧版安装包装的）：沿用原来的整目录清理
                bool byManifest = File.Exists(Path.Combine(_dir, ManifestName));
                bool clean = byManifest ? DeleteListed(_dir) : DeleteAllContents(_dir);

                // 清单删干净但目录里还留着别的东西（比如用户自己的文件）——如实提示
                bool otherLeft = false;
                if (byManifest && clean)
                {
                    try
                    {
                        string selfExe = Application.ExecutablePath;
                        string manifestPath = Path.Combine(_dir, ManifestName);
                        foreach (var f in Directory.GetFiles(_dir))
                            if (!string.Equals(f, selfExe, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(f, manifestPath, StringComparison.OrdinalIgnoreCase))
                                otherLeft = true;
                        if (Directory.GetDirectories(_dir).Length > 0) otherLeft = true;
                    }
                    catch { }
                }

                if (byManifest) TryDelete(Path.Combine(_dir, ManifestName));
                ScheduleSelfDelete(_dir, byManifest);
                Log.Write("卸载完成，目录干净=" + clean + "（" + (byManifest ? "按清单" : "整目录") + "）: " + _dir);

                Post(() =>
                {
                    string msg;
                    if (!clean)
                        msg = byManifest
                            ? "已移除快捷方式和卸载记录，但安装目录里还有未能删除的文件（正在使用或其他文件）。\n\n请检查后手动删除：\n" + _dir
                            : "已移除快捷方式和卸载记录，但部分程序文件正在使用中，未能删除。\n\n" +
                              "请重启电脑后手动删除：\n" + _dir;
                    else if (otherLeft)
                        msg = "卸载完成。\n\n安装目录里还有其他文件，已保留不动：\n" + _dir;
                    else
                        msg = "卸载完成。\n\n安装目录和快捷方式已删除。";
                    MessageBox.Show(this, msg, AppDisplay, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                });
            }
            catch (Exception ex)
            {
                Log.Write("卸载失败: " + ex);
                Post(() =>
                {
                    MessageBox.Show(this, "卸载失败：\n" + ex.Message, AppDisplay,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _btnGo.Enabled = true;
                    _btnCancel.Enabled = true;
                    _status.Text = "卸载失败，可重试或手动删除安装目录。";
                });
            }
        }

        void Post(string status)
        {
            Post(() => _status.Text = status);
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        static void Kill(string name)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); } catch { return; }
            foreach (var p in list)
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(2500)) p.Kill();
                    p.WaitForExit(2000);
                }
                catch (Exception e) { Log.Write("结束进程失败 " + name + ": " + e.Message); }
            }
        }

        void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception e) { Log.Write("删除失败 " + file + ": " + e.Message); }
        }

        void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception e) { Log.Write("删除失败 " + dir + ": " + e.Message); }
        }

        // 旧逻辑：删光目录内全部内容（无清单时兜底）
        bool DeleteAllContents(string dir)
        {
            bool clean = true;
            string self = Application.ExecutablePath;
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); }
                    catch (Exception e) { clean = false; Log.Write("删除失败 " + f + ": " + e.Message); }
                }
                foreach (var d in Directory.GetDirectories(dir))
                {
                    try { Directory.Delete(d, true); }
                    catch (Exception e) { clean = false; Log.Write("删除失败 " + d + ": " + e.Message); }
                }
            }
            catch (Exception e)
            {
                clean = false;
                Log.Write("遍历安装目录失败: " + e.Message);
            }
            return clean;
        }

        // 按安装清单精确删除：只删 payload 释放过的文件与目录，运行期文件按名单补删。
        // 目录一律非递归删除——里面若有清单之外的文件，留着并报告，绝不静默清场。
        bool DeleteListed(string dir)
        {
            string self = Application.ExecutablePath;
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var dirs = new List<string>();
            bool clean = true;

            try
            {
                foreach (var line in File.ReadAllLines(Path.Combine(dir, ManifestName)))
                {
                    string rel = line.Trim();
                    if (rel.Length == 0) continue;
                    bool isDir = rel.EndsWith(Path.DirectorySeparatorChar.ToString());
                    string relPath = isDir ? rel.TrimEnd(Path.DirectorySeparatorChar) : rel;

                    string dest = Path.GetFullPath(Path.Combine(dir, relPath));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // 清单被改坏也不跳出目录

                    if (isDir) { dirs.Add(dest); continue; }
                    if (string.Equals(dest, self, StringComparison.OrdinalIgnoreCase)) continue; // 自己最后交给自删除

                    try { if (File.Exists(dest)) File.Delete(dest); }
                    catch (Exception e) { clean = false; Log.Write("删除失败 " + dest + ": " + e.Message); }
                }

                // 运行期生成、不在 payload 清单里的文件
                foreach (var name in new[]
                {
                    "WebVideoRemote.log", "WebVideoRemote.ini", "WebVideoRemote-crash.txt",
                    Path.Combine("extension", "安装说明.txt")
                })
                    TryDelete(Path.Combine(dir, name));
            }
            catch (Exception e)
            {
                Log.Write("读取卸载清单失败: " + e.Message);
                return false;
            }

            foreach (var d in dirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, false); }
                catch (Exception e) { clean = false; Log.Write("删除目录失败 " + d + ": " + e.Message); }
            }
            return clean;
        }

        // 卸载程序删不掉自己：交给 cmd 在进程退出后收尾。
        // 按清单卸载时只删自己 + 非递归移除空目录（目录里还有文件就留着，不 rmdir /s）。
        static void ScheduleSelfDelete(string dir, bool byManifest)
        {
            try
            {
                string self = Application.ExecutablePath;
                string tail = byManifest
                    ? "del /f /q \"" + self + "\" & rmdir \"" + dir + "\""
                    : "rmdir /s /q \"" + dir + "\"";
                var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & " + tail)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath()
                };
                Process.Start(psi);
            }
            catch (Exception e) { Log.Write("安排自删除失败: " + e.Message); }
        }
    }
}
