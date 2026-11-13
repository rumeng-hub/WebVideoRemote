// 安装程序里用到的日志：复用主程序的 Log 外观，写到自己临时目录，避免安装目录不可写。
using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WebVideoRemote
{
    static class Log
    {
        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        static string FilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "WebVideoRemote-setup.log"); }
        }

        public static void Write(string msg)
        {
            try
            {
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }
}
