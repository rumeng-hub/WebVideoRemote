// 桥接扩展的位置：安装目录下的 extension 文件夹（安装程序会释放好）。
// 扩展不再内嵌进 exe，缺失时明确提示「安装不完整」。
using System;
using System.IO;
using System.Text;

namespace WebVideoRemote
{
    static class ExtensionPack
    {
        public static string BesideDir
        {
            get { return Path.Combine(Log.AppDir, "extension"); }
        }

        public static string LocalDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WebVideoRemote", "extension");
            }
        }

        // 旧版本可能把扩展释放到用户目录，浏览器里加载的也是那份。
        // 启动时用安装目录的新副本覆盖，省得用户手动重新加载扩展。
        public static void RefreshLocalCopyIfExists()
        {
            try
            {
                if (!Directory.Exists(LocalDir)) return;
                if (!File.Exists(Path.Combine(BesideDir, "manifest.json"))) return;

                foreach (var f in Directory.GetFiles(BesideDir))
                {
                    string name = Path.GetFileName(f);
                    if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(f, Path.Combine(LocalDir, name), true);
                }
                Log.Write("已把用户目录里的扩展副本更新为安装目录版本: " + LocalDir);
            }
            catch (Exception e) { Log.Write("刷新扩展副本失败: " + e.Message); }
        }

        // 返回可直接用于「加载解压缩的扩展」的目录
        public static string Ensure(out bool extracted)
        {
            extracted = false;

            if (File.Exists(Path.Combine(BesideDir, "manifest.json")))
            {
                try
                {
                    string readme = Path.Combine(BesideDir, "安装说明.txt");
                    if (!File.Exists(readme)) File.WriteAllText(readme, ReadmeText, new UTF8Encoding(true));
                }
                catch { }
                return BesideDir;
            }

            Log.Write("未找到扩展目录: " + BesideDir + "（安装包可能没有完整解压，请重新运行安装程序）");
            return BesideDir;
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
            "1. 先启动 网页视频遥控器（保持运行）。\r\n" +
            "2. 用浏览器打开抖音并播放一个视频。\r\n" +
            "3. 回到遥控器窗口，状态栏第二行会显示「脚本已连接」。\r\n\r\n\r\n" +
            "【常见问题】\r\n" +
            "Q: 遥控器窗口显示「脚本未连接」？\r\n" +
            "A: ① 确认遥控器程序正在运行；② 确认扩展已启用；③ 刷新一下抖音页面。\r\n\r\n" +
            "Q: 想彻底移除？\r\n" +
            "A: 在 edge://extensions 里点「移除」，再运行安装目录里的「卸载.exe」即可。\r\n";
    }
}
