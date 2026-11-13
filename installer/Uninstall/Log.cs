// 卸载程序复用的日志：写到临时目录，避免安装目录删到一半时日志文件占用句柄。
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
            get { return Path.Combine(Path.GetTempPath(), "WebVideoRemote-uninstall.log"); }
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
