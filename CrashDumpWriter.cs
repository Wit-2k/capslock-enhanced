using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace Remake;

/// <summary>进程仍存活时先写小型转储；watchdog 仅对仍可读的 worker 句柄做尽力补救。</summary>
internal static unsafe class CrashDumpWriter
{
    private const int MaxRetainedDumps = 10;
    private static readonly string DumpDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CapsLockPlus",
        "dumps");

    public static void TryWriteCurrentProcessDump(string processKind, int exitCode) =>
        TryWriteDump(PInvoke.GetCurrentProcess(), PInvoke.GetCurrentProcessId(), processKind, exitCode);

    public static void TryWriteWorkerDump(Process worker, int exitCode)
    {
        if (HasDumpForProcess("worker", worker.Id))
            return;

        // exitCode=1 由 worker 的托管异常路径先自取证；强杀后的页通常已经无法读取。
        if (exitCode == 1)
        {
            AppLog.Warn($"worker exitCode=1 且未发现进程内转储；不再读取已退出进程的内存，pid={worker.Id}。");
            return;
        }

        TryWriteDump(new HANDLE(worker.Handle), unchecked((uint)worker.Id), "worker", exitCode);
    }

    private static void TryWriteDump(HANDLE processHandle, uint processId, string processKind, int exitCode)
    {
        string? dumpPath = null;
        try
        {
            Directory.CreateDirectory(DumpDirectory);
            RemoveOldDumps();
            dumpPath = Path.Combine(
                DumpDirectory,
                $"capslock_{processKind}_{processId}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{exitCode:X8}.dmp");

            using var dumpFile = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var fileHandle = new HANDLE(dumpFile.SafeFileHandle.DangerousGetHandle());
            if (PInvoke.MiniDumpWriteDump(
                    processHandle,
                    processId,
                    fileHandle,
                    (MINIDUMP_TYPE)0,
                    null,
                    null,
                    null).Value == 0)
            {
                int error = Marshal.GetLastWin32Error();
                dumpFile.Dispose();
                File.Delete(dumpPath);
                AppLog.Warn($"{processKind} minidump 写入失败，exitCode={exitCode}，Win32Error={error}。");
                return;
            }

            dumpFile.Flush(flushToDisk: true);
            AppLog.Info($"{processKind} minidump 已保存：{dumpPath}，exitCode={exitCode}。");
        }
        catch (Exception ex)
        {
            if (dumpPath is not null)
            {
                try { File.Delete(dumpPath); }
                catch { /* 清理部分转储失败不应阻止 watchdog 恢复。 */ }
            }
            AppLog.Error($"{processKind} minidump 生成失败，exitCode={exitCode}", ex);
        }
    }

    private static bool HasDumpForProcess(string processKind, int processId)
    {
        try
        {
            return Directory.Exists(DumpDirectory) &&
                   Directory.EnumerateFiles(DumpDirectory, $"capslock_{processKind}_{processId}_*.dmp", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"检查 {processKind} minidump 失败：{ex.Message}");
            return false;
        }
    }

    private static void RemoveOldDumps()
    {
        FileInfo[] existing = new DirectoryInfo(DumpDirectory)
            .EnumerateFiles("capslock_*.dmp", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        foreach (FileInfo oldDump in existing.Skip(MaxRetainedDumps - 1))
        {
            try { oldDump.Delete(); }
            catch (IOException ex) { AppLog.Warn($"旧 minidump 未能清理：{ex.Message}"); }
            catch (UnauthorizedAccessException ex) { AppLog.Warn($"旧 minidump 未能清理：{ex.Message}"); }
        }
    }
}
