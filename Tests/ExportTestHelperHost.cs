using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace WCAE
{
    // The self-test starts this same executable; no SDK or Framework compiler is needed.
    public static class ExportTestHelperHost
    {
        public static int Run(string[] args)
        {
            if (args == null || args.Length == 0) return 64;
            if (args[0] == "echo")
            {
                if (args.Length < 2) return 64;
                File.WriteAllLines(args[1], args.Skip(2));
                return 0;
            }
            if (args[0] == "fail")
            {
                Console.Error.WriteLine("fixture nonzero");
                return 7;
            }
            if (args[0] == "child")
            {
                Thread.Sleep(60000);
                return 0;
            }
            if (args[0] != "parent" || args.Length != 2) return 64;
            string executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) throw new IOException("无法定位进程测试宿主。");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--export-test-helper");
            start.ArgumentList.Add("child");
            using (var child = Process.Start(start))
            {
                if (child == null) throw new IOException("进程测试子进程未启动。");
                string temporary = args[1] + ".part";
                try
                {
                    File.WriteAllText(temporary, child.Id.ToString());
                    File.Move(temporary, args[1]);
                }
                catch
                {
                    try { child.Kill(true); } catch (InvalidOperationException) { }
                    throw;
                }
                Thread.Sleep(60000);
            }
            return 0;
        }
    }
}
