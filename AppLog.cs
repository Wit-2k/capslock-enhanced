using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Remake;

/// <summary>将 watchdog 与 worker 的诊断信息串行写入同一份滚动日志。</summary>
internal static class AppLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly Mutex CrossProcessLock = new(false, @"Local\CapsLockPlus.Log");
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CapsLockPlus",
        "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "remake.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        bool ownsLock = false;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            try
            {
                ownsLock = CrossProcessLock.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                // 上个进程异常退出时日志锁会被系统释放，此线程此时已取得锁。
                ownsLock = true;
            }

            if (!ownsLock)
            {
                Debug.WriteLine("CapsLock+ 日志锁等待超时。");
                return;
            }

            RotateIfNeeded();
            string line = $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} [{level}] [pid:{Environment.ProcessId}] {message}{Environment.NewLine}";
            using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(line);
        }
        catch (Exception ex)
        {
            // 日志失败不得弹窗或递归记录；调试输出只用于开发期诊断。
            Debug.WriteLine($"CapsLock+ 无法写入日志：{ex.Message}");
        }
        finally
        {
            if (ownsLock)
                CrossProcessLock.ReleaseMutex();
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxLogBytes)
            return;

        string firstBackup = LogPath + ".1";
        string secondBackup = LogPath + ".2";
        if (File.Exists(secondBackup))
            File.Delete(secondBackup);
        if (File.Exists(firstBackup))
            File.Move(firstBackup, secondBackup);
        File.Move(LogPath, firstBackup);
    }
}
