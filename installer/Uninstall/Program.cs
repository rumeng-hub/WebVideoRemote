using System;
using System.Windows.Forms;

namespace WebVideoRemote
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (a, b) =>
            {
                Log.Write("卸载程序异常: " + b.Exception);
                MessageBox.Show("卸载程序遇到问题：\n" + b.Exception.Message,
                    "网页视频遥控器 卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new UninstallForm());
        }
    }
}
