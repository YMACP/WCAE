using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WCAE
{
    internal static class ExternalToolRunner
    {
        internal static string QuoteArgument(string argument)
        {
            if (argument == null) argument = "";
            if (argument.IndexOf('\0') >= 0) throw new ArgumentException("参数包含空字符。");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        internal static async Task RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken token, IReadOnlyDictionary<string,string> environment = null)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少导出工具：" + Path.GetFileName(executable), executable);
            var log = new StringBuilder();
            var logLock = new object();
            Action<string> append = line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                line = System.Text.RegularExpressions.Regex.Replace(line, @"(?i)(https?|socks5h?)://[^\s/@]+@", "$1://***@");
                lock (logLock)
                {
                    log.AppendLine(line);
                    if (log.Length > 12000) log.Remove(0, log.Length - 12000);
                }
            };
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var job = new ProcessJob())
            using (var process = new Process())
            {
                lifetime.CancelAfter(timeout);
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(executable),
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (environment != null)
                    foreach (var variable in environment) process.StartInfo.Environment[variable.Key] = variable.Value;
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.EnableRaisingEvents = true;
                process.Exited += (sender, args) => { try { exited.TrySetResult(process.ExitCode); } catch (InvalidOperationException) { } };
                process.OutputDataReceived += (sender, args) => append(args.Data);
                process.ErrorDataReceived += (sender, args) => append(args.Data);
                if (!process.Start()) throw new IOException("导出工具未能启动。");
                try { job.Assign(process); }
                catch
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (lifetime.Token.Register(() =>
                {
                    job.Terminate();
                    try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                }))
                {
                    if (process.HasExited) exited.TrySetResult(process.ExitCode);
                    int exitCode = await exited.Task.ConfigureAwait(false);
                    // A successful parent must not leave a detached downloader holding our pipes open.
                    job.Terminate();
                    // WaitForExit also drains the asynchronous output readers.
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (lifetime.IsCancellationRequested) throw new TimeoutException("导出工具超过允许的运行时间，已终止整个进程组。");
                    if (exitCode != 0)
                    {
                        string detail; lock (logLock) detail = log.ToString().Trim();
                        throw new IOException(Path.GetFileName(executable) + " 退出码 " + exitCode + (detail.Length == 0 ? "" : "：" + detail));
                    }
                }
            }
        }

        private sealed class ProcessJob : IDisposable
        {
            private readonly JobHandle handle;
            internal ProcessJob()
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "创建导出进程组失败。");
                var limits = new ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                int size = Marshal.SizeOf(typeof(ExtendedLimitInformation));
                var memory = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, memory, false);
                    if (!SetInformationJobObject(handle, 9, memory, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "设置导出进程组失败。");
                }
                catch { handle.Dispose(); throw; }
                finally { Marshal.FreeHGlobal(memory); }
            }
            internal void Assign(Process process)
            {
                if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法管理导出工具及其子进程，已停止启动。");
            }
            internal void Terminate() { if (!handle.IsClosed && !handle.IsInvalid) TerminateJobObject(handle, 1); }
            public void Dispose() { handle.Dispose(); }
        }

        private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public JobHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(JobHandle job, int infoClass, IntPtr information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(JobHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
