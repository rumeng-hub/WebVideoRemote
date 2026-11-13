// 命令行诊断模式：WebVideoRemote.exe --cli <命令>
// 用于排查「为什么按了没反应」，也可以直接在 cmd 里试控制效果。
using System;
using System.Threading;
using System.Windows.Forms;
using Windows.Media.Control;

namespace WebVideoRemote
{
    static class Cli
    {
        public static void Run(string[] args)
        {
            string cmd = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
            var s = Settings.Load();
            var smtc = new SmtcEngine();

            switch (cmd)
            {
                case "help":
                    Console.WriteLine("用法: WebVideoRemote.exe --cli <命令>");
                    Console.WriteLine("  status            列出系统媒体会话与检测到的浏览器窗口");
                    Console.WriteLine("  toggle            播放/暂停（走系统媒体控制）");
                    Console.WriteLine("  seek <秒>         相对跳转，例如 seek 30 / seek -15");
                    Console.WriteLine("  next / prev       下一个 / 上一个");
                    Console.WriteLine("  mode <0|1|2>      切换网站模式：0=B站 1=抖音 2=自动");
                    Console.WriteLine("  post <vk>         测试后台投递按键（十六进制，如 27 表示 →）");
                    Console.WriteLine("  focus <vk>        测试抢焦点注入按键");
                    Console.WriteLine("  hooktest          安装键盘钩子 4 秒并自注入按键，验证钩子是否工作");
                    Console.WriteLine("  bridge [指令]     启动桥接服务并等扩展连上，然后发一条指令（默认 next）");
                    return;

                case "status":
                    DumpSessions(smtc, s);
                    return;

                case "toggle":
                    {
                        var r = new Router(s, smtc);
                        Console.WriteLine("模式=" + r.ModeName(r.Mode()) + "  执行 播放/暂停 …");
                        r.Toggle();
                        Thread.Sleep(1500);
                        Console.WriteLine("使用通道: " + r.LastEngine + " 动作: " + r.LastAction);
                        DumpSessions(smtc, s);
                        return;
                    }

                case "seek":
                    {
                        double d = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5;
                        var r = new Router(s, smtc);
                        Console.WriteLine("模式=" + r.ModeName(r.Mode()) + "  跳转 " + d + "s …");
                        r.Seek(d > 0);
                        Thread.Sleep(2000);
                        Console.WriteLine("使用通道: " + r.LastEngine + " 动作: " + r.LastAction);
                        DumpSessions(smtc, s);
                        return;
                    }

                case "next":
                case "prev":
                    {
                        var r = new Router(s, smtc);
                        r.SwitchVideo(cmd == "prev");
                        Thread.Sleep(1500);
                        Console.WriteLine("使用通道: " + r.LastEngine + " 动作: " + r.LastAction);
                        DumpSessions(smtc, s);
                        return;
                    }

                case "mode":
                    {
                        int m = args.Length > 2 ? int.Parse(args[2]) : 2;
                        s.Mode = m; s.Save();
                        Console.WriteLine("已切换为 " + (m == 0 ? "B站" : m == 1 ? "抖音" : "自动识别") + " 模式");
                        return;
                    }

                case "post":
                    {
                        uint vk = Convert.ToUInt32(args.Length > 2 ? args[2] : "27", 16);
                        int mode = args.Length > 3 ? int.Parse(args[3]) : 0;
                        var wi = WindowEngine.Find(mode);
                        if (wi == null) { Console.WriteLine("没找到浏览器窗口"); return; }
                        Console.WriteLine("目标窗口: " + wi.Title);
                        Console.WriteLine("后台投递结果: " + WindowEngine.TryPostKey(wi, vk) + "（False 说明浏览器拒绝，属正常）");
                        return;
                    }

                case "focus":
                    {
                        uint vk = Convert.ToUInt32(args.Length > 2 ? args[2] : "27", 16);
                        int mode = args.Length > 3 ? int.Parse(args[3]) : 0;
                        Console.WriteLine("抢焦点注入结果: " + WindowEngine.FocusTap(mode, vk));
                        return;
                    }

                case "ext":
                    {
                        bool extracted;
                        string d = ExtensionPack.Ensure(out extracted);
                        Console.WriteLine("扩展目录: " + d);
                        Console.WriteLine("（安装目录下的 extension 文件夹）");
                        try
                        {
                            foreach (var f in System.IO.Directory.GetFiles(d))
                                Console.WriteLine("   " + System.IO.Path.GetFileName(f) + "   "
                                    + new System.IO.FileInfo(f).Length + " 字节");
                        }
                        catch (Exception e) { Console.WriteLine("   列出失败: " + e.Message); }
                        return;
                    }

                case "bridge":
                    {
                        BridgeServer.Instance.Start();
                        Console.WriteLine("桥接服务已启动: 127.0.0.1:" + BridgeServer.Port);
                        Console.Write("等待浏览器扩展连接（最多 15 秒）");
                        int waited = 0;
                        while (!BridgeServer.Instance.Connected && waited < 15000) { Thread.Sleep(300); waited += 300; Console.Write("."); }
                        Console.WriteLine();
                        if (!BridgeServer.Instance.Connected)
                        {
                            Console.WriteLine("没人连接：请确认浏览器里已装「网页视频遥控器 桥接」扩展，并打开抖音/B站页面。");
                            return;
                        }
                        string bridgeCmd = args.Length > 2 ? args[2] : "next";
                        string bridgeSite = args.Length > 3 ? args[3]
                            : BridgeServer.Instance.ConnectedFor("douyin") ? "douyin"
                            : BridgeServer.Instance.ConnectedFor("bilibili") ? "bilibili" : "any";
                        Console.WriteLine("已连接（" + BridgeServer.Instance.ConnectedSites + "），发送指令: "
                            + bridgeCmd + " → " + bridgeSite);
                        BridgeServer.Instance.Send(bridgeCmd, bridgeSite);
                        Thread.Sleep(2500);
                        Console.WriteLine("脚本版本: " + (BridgeServer.Instance.LastVersion.Length > 0
                            ? "v" + BridgeServer.Instance.LastVersion
                              + (BridgeServer.Instance.LastVersion == BridgeServer.ExpectedVer
                                 ? "（与程序一致）" : "  ← 过旧！程序需要 v" + BridgeServer.ExpectedVer)
                            : "未上报（说明浏览器里还是旧脚本，请在 edge://extensions 点「重新加载」）"));
                        Console.WriteLine("脚本上报状态: " + (BridgeServer.Instance.LastState.Length > 0
                            ? BridgeServer.Instance.LastState : "(无)"));
                        return;
                    }

                // 验证 exe 侧的长按看门狗：发起长按后故意不发「松开」，看它能不能自己恢复
                case "holdwatch":
                    {
                        BridgeServer.Instance.Start();
                        Console.Write("等待浏览器扩展连接");
                        int w = 0;
                        while (!BridgeServer.Instance.Connected && w < 15000) { Thread.Sleep(300); w += 300; Console.Write("."); }
                        Console.WriteLine();
                        if (!BridgeServer.Instance.Connected) { Console.WriteLine("没人连接"); return; }

                        s.Mode = 1;                       // 抖音
                        if (s.SafetyHoldMs > 10000) s.SafetyHoldMs = 10000;  // 测试时缩短一点
                        var smtc3 = new SmtcEngine();
                        var r3 = new Router(s, smtc3);
                        Console.WriteLine("发起长按…（之后故意不发松开）");
                        r3.HoldStart();
                        Thread.Sleep(1500);
                        Console.WriteLine("1.5 秒后脚本状态: " + BridgeServer.Instance.LastState);
                        Console.WriteLine("等待看门狗 " + s.SafetyHoldMs + "ms …");
                        Thread.Sleep(s.SafetyHoldMs + 3000);
                        Console.WriteLine("看门狗之后脚本状态: " + BridgeServer.Instance.LastState);
                        Console.WriteLine("最后动作: " + r3.LastEngine + " / " + r3.LastAction);
                        Thread.Sleep(1500);
                        Console.WriteLine("最终脚本状态: " + BridgeServer.Instance.LastState);
                        return;
                    }

                case "hooktest":
                    {
                        s.Enabled = true;
                        var smtc2 = new SmtcEngine();
                        var router = new Router(s, smtc2);
                        var hook = new KeyboardHook(s, router) { CaptureInjected = true };
                        int captured = 0;
                        hook.Captured += line => { captured++; Console.WriteLine("  钩子捕获: " + line); };
                        hook.Install();
                        if (!hook.Installed) { Console.WriteLine("钩子安装失败"); return; }

                        // 自注入按键（带「注入」标记，正常运行时会被忽略，这里做真值验证）
                        var t = new Thread(() =>
                        {
                            Thread.Sleep(800);
                            foreach (uint vk in new uint[] { 0x7C, 0x26, 0x28, 0x25, 0x27 })
                            {
                                KeyInjector.Tap(vk);
                                Thread.Sleep(150);
                            }
                        });
                        t.IsBackground = true; t.Start();

                        var stop = new System.Windows.Forms.Timer { Interval = 4000 };
                        stop.Tick += (a, b) => Application.ExitThread();
                        stop.Start();
                        Application.Run();
                        hook.Uninstall();
                        Console.WriteLine("钩子共捕获 " + captured + " 个按键事件（应 ≥10 才正常）");
                        return;
                    }

                default:
                    Console.WriteLine("未知命令: " + cmd + "（用 --cli help 看说明）");
                    return;
            }
        }

        static void DumpSessions(SmtcEngine smtc, Settings s)
        {
            smtc.Wait();
            Console.WriteLine();
            Console.WriteLine("系统媒体会话：");
            try
            {
                GlobalSystemMediaTransportControlsSessionManager mgr;
                Winrt.Run(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 6000, out mgr);
                if (mgr == null) { Console.WriteLine("  读取失败：请求媒体会话管理器超时"); return; }
                var list = mgr.GetSessions();
                if (list.Count == 0) Console.WriteLine("  （没有）Chrome/Edge 里要先有正在播放的网页");
                foreach (var sess in list)
                {
                    string app = "", title = "";
                    try { app = sess.SourceAppUserModelId; } catch { }
                    var mi = smtc.Describe(sess);
                    title = mi.Title;
                    Console.WriteLine(string.Format("  ● {0}", app));
                    Console.WriteLine(string.Format("     标题: {0}", title.Length == 0 ? "(无)" : title));
                    Console.WriteLine(string.Format("     状态: {0}  位置 {1:0.0}s / {2:0.0}s", mi.Status, mi.Position, mi.Duration));
                }
            }
            catch (Exception e) { Console.WriteLine("  读取失败: " + e.Message); }

            Console.WriteLine();
            Console.WriteLine("浏览器窗口（按当前模式 0=B站 1=抖音 2=自动 打分）：");
            for (int m = 0; m <= 2; m++)
            {
                var wi = WindowEngine.Find(m);
                Console.WriteLine(string.Format("  模式{0}: {1}", m, wi == null ? "(没找到)" : wi.Title + "   [" + wi.Exe + "]"));
            }
            Console.WriteLine();
            Console.WriteLine("当前配置: mode=" + s.Mode + " step=" + s.Step + " douyinseek=" + s.DouyinSeek
                + " usesmtc=" + s.UseSmtc + " usefocus=" + s.UseFocusInject + " usebgpost=" + s.UseBgPost);
        }
    }
}
