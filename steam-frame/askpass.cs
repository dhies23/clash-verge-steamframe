// 专用 SSH askpass 程序（控制台子系统，必须与主程序分开）
//
// ssh 在需要密码时会以 `SSH_ASKPASS程序 "<提示文本>"` 的方式调用它，
// 并从它的 stdout 读取密码。注意参数是"提示文本"，不是我们能自定义的开关，
// 所以这个程序不能兼任别的用途 —— 它的唯一职责就是把环境变量里的密码打出来。

using System;
using System.IO;
using System.Text;

internal static class AskPass
{
    private static int Main(string[] args)
    {
        string pw = Environment.GetEnvironmentVariable("CVFRAME_PW") ?? "";
        try
        {
            // 直接写标准输出句柄：这样无论有没有控制台都能正确输出到 ssh 的管道
            using (Stream s = Console.OpenStandardOutput())
            {
                byte[] b = Encoding.UTF8.GetBytes(pw + "\n");
                s.Write(b, 0, b.Length);
                s.Flush();
            }
        }
        catch
        {
            return 1;
        }
        return 0;
    }
}
