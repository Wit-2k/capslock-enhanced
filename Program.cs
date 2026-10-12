using System.Runtime.Versioning;

// 应用依赖 Win32 且以 win-x64 发布；其支持面限定在 Windows 10 及以上。
[assembly: SupportedOSPlatform("windows10.0")]

namespace Remake;

/// <summary>区分 watchdog 父进程与带隐藏窗口的 worker 子进程。</summary>
internal static class Program
{
    private const string WorkerArgument = "--worker";
    private const string RecoveryArgument = "--recover";

    public static int Main(string[] args)
    {
        bool isWorker = args.Contains(WorkerArgument, StringComparer.Ordinal);
        InstallCrashLogging(isWorker);
        AppLog.Info($"入口启动：{string.Join(' ', args)}");

        try
        {
            if (isWorker)
                return RunWorker();
            bool isRecovery = args.Contains(RecoveryArgument, StringComparer.Ordinal);
            if (isRecovery && AutostartState.IsDisabled)
            {
                AppLog.Info("用户已主动退出；周期恢复任务本次不启动程序。");
                return 0;
            }
            if (!isRecovery)
                AutostartState.AllowAutomaticStart();

            if (!SingleInstanceLease.TryAcquire(out SingleInstanceLease? lease))
            {
                AppLog.Info("检测到已有实例，本次启动正常结束。");
                return 0;
            }

            using (lease)
                return WorkerWatchdog.Run();
        }
        catch (Exception ex)
        {
            AppLog.Error("应用入口异常退出", ex);
            if (!isWorker)
                CrashDumpWriter.TryWriteCurrentProcessDump("watchdog", 1);
            return 1;
        }
    }

    private static void InstallCrashLogging(bool isWorker)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                AppLog.Error($"未处理异常，IsTerminating={eventArgs.IsTerminating}", exception);
            else
                AppLog.Error($"未处理的非 Exception 对象，IsTerminating={eventArgs.IsTerminating}：{eventArgs.ExceptionObject}");
            if (eventArgs.IsTerminating)
                CrashDumpWriter.TryWriteCurrentProcessDump(isWorker ? "worker" : "watchdog", -1);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("未观察到的 Task 异常", eventArgs.Exception);
            eventArgs.SetObserved();
        };
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
            CrashDumpWriter.TryWriteCurrentProcessDump("worker", 1);
            return 1;
        }
    }

}
