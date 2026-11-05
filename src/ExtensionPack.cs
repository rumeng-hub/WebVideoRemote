// 桥接扩展的释放：优先用 exe 同级的 extension 文件夹；
// 找不到（比如只拷了 exe 出来）就把内嵌在程序里的扩展释放到用户目录，保证「装扩展」永远可用。
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace WebVideoRemote
{
    static class ExtensionPack
    {
        static readonly string[] Embedded = { "manifest.json", "content.js", "background.js", "options.html", "options.js" };

        public static string LocalDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WebVideoRemote", "extension");
            }
        }

        public static string BesideDir
        {
            get { return Path.Combine(Log.AppDir, "extension"); }
        }

        // 如果用户当初是从「用户目录」那份副本加载的扩展，
        // 每次启动都把程序内嵌的最新脚本覆盖过去 —— 这样他点「重新加载」拿到的就是新版。
        public static void RefreshLocalCopyIfExists()
        {
            try
            {
                if (!Directory.Exists(LocalDir)) return;
                var asm = Assembly.GetExecutingAssembly();
                int n = 0;
                foreach (var name in Embedded)
                {
                    string res = FindResource(asm, name);
                    if (res == null) continue;
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var f = File.Create(Path.Combine(LocalDir, name)))
                        s.CopyTo(f);
                    n++;
                }
                Log.Write("已把扩展副本刷新为当前版本（" + n + " 个文件）: " + LocalDir);
            }
            catch (Exception e) { Log.Write("刷新扩展副本失败: " + e.Message); }
        }

        // 返回可直接用于「加载解压缩的扩展」的目录
        public static string Ensure(out bool extracted)
        {
            extracted = false;

            if (File.Exists(Path.Combine(BesideDir, "manifest.json")))
                return BesideDir;

            string dir = LocalDir;
            try
            {
                Directory.CreateDirectory(dir);
                var asm = Assembly.GetExecutingAssembly();
                foreach (var name in Embedded)
                {
                    string res = FindResource(asm, name);
                    if (res == null) { Log.Write("内嵌扩展缺少: " + name); continue; }
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var f = File.Create(Path.Combine(dir, name)))
                        s.CopyTo(f);
                }
                File.WriteAllText(Path.Combine(dir, "安装说明.txt"), ReadmeText, new UTF8Encoding(true));
                extracted = true;
                Log.Write("已把桥接扩展释放到: " + dir);
                return dir;
            }
            catch (Exception e)
            {
                Log.Write("释放扩展失败: " + e.Message);
                return BesideDir;
            }
        }

        static string FindResource(Assembly asm, string fileName)
        {
            foreach (var r in asm.GetManifestResourceNames())
                if (r.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                    r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    return r;
            return null;
        }

        public const string ReadmeText =
            "网页视频遥控器 · 浏览器桥接扩展  安装说明\r\n" +
            "=========================================\r\n\r\n" +
            "这个扩展只做一件事：让你电脑上的「网页视频遥控器」能够直接控制\r\n" +
            "抖音 / B站 网页播放器（上下切换视频、长按快进、播放暂停、快进快退）。\r\n\r\n" +
            "它不联网、不上传任何数据，只和本机 127.0.0.1:17777 上的遥控器程序通信。\r\n\r\n\r\n" +
            "【Edge 安装步骤】\r\n" +
            "1. 在地址栏输入并回车：  edge://extensions\r\n" +
            "2. 打开左下角（或右侧）的「开发人员模式」开关。\r\n" +
            "3. 点「加载解压缩的扩展」。\r\n" +
            "4. 选中本文件所在的这个文件夹（把上面的路径粘进地址栏最快）。\r\n" +
            "5. 装好后列表里会出现「网页视频遥控器 桥接」。\r\n\r\n" +
            "【Chrome 安装步骤】\r\n" +
            "同上，地址栏改成： chrome://extensions\r\n\r\n\r\n" +
            "【验证是否装好】\r\n" +
            "1. 先启动 网页视频遥控器.exe（保持运行）。\r\n" +
            "2. 用浏览器打开抖音并播放一个视频。\r\n" +
            "3. 回到遥控器窗口，状态栏第二行会显示「脚本已连接」。\r\n\r\n\r\n" +
            "【常见问题】\r\n" +
            "Q: 遥控器窗口显示「脚本未连接」？\r\n" +
            "A: ① 确认遥控器程序正在运行；② 确认扩展已启用；③ 刷新一下抖音页面。\r\n\r\n" +
            "Q: 想彻底移除？\r\n" +
            "A: 在 edge://extensions 里点「移除」，再删掉这个文件夹即可。\r\n";
    }
}
