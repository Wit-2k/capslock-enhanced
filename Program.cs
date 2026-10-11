using System.Runtime.Versioning;

// 应用依赖 Win32 且以 win-x64 发布；其支持面限定在 Windows 10 及以上。
[assembly: SupportedOSPlatform("windows10.0")]

namespace Remake;

/// <summary>区分 watchdog 父进程与带隐藏窗口的 worker 子进程。</summary>
internal static class Program
{
    private const string WorkerArgument = "--worker";

    public static int Main(string[] args)
    {
        AppLog.Info($"入口启动：{string.Join(' ', args)}");

        if (args.Contains(WorkerArgument, StringComparer.Ordinal))
            return RunWorker();

        if (!SingleInstanceLease.TryAcquire(out SingleInstanceLease? lease))
        {
            AppLog.Info("检测到已有实例，本次启动正常结束。");
            return 0;
        }

        using (lease)
            return WorkerWatchdog.Run();
    }

    private static int RunWorker()
    {
        try
        {
            return WorkerApp.Run();
        }
        catch (Exception ex)
        {
            AppLog.Error("worker 异常退出", ex);
            return 1;
        }
    }
}
