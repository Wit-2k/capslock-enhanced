using System.Diagnostics;

namespace Remake;

/// <summary>监护 worker；Job Object 收尾孤儿进程，被动 hook guard 在主钩子失效时请求重启。</summary>
internal static class WorkerWatchdog
{
    // 这是 worker 与 watchdog 共享的内部协议，不作为异常退出统计。
    internal const int ReloadExitCode = 10;
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
    public static int Run()
    {
        using var hookGuard = new WorkerHookGuard();
        using var job = new JobObjectLease();
        int restartCount = 0;

        AppLog.Info("worker Job Object 与被动 hook guard 已就绪。");
        while (true)
        {
            try
            {
                hookGuard.PauseMonitoring();
                hookGuard.ResetWorkerReady();
                using Process worker = StartWorker(job);
                AppLog.Info($"worker 已启动，pid={worker.Id}，累计重启={restartCount}。");

                try
                {
                    bool hookReady = hookGuard.WaitForWorkerHookReady(worker, TimeSpan.FromSeconds(5));
                    if (!hookReady && !worker.HasExited)
                    {
                        worker.Kill();
                        worker.WaitForExit();
                        restartCount++;
                        AppLog.Warn($"worker 未能在 5 秒内安装 keyboard hook；已终止并重试，累计重启={restartCount}。");
                        Thread.Sleep(RestartDelay);
                        continue;
                    }

                    if (hookReady)
                    {
                        hookGuard.StartMonitoring();
                        if (WaitForWorkerOrLostHook(worker, hookGuard))
                        {
                            hookGuard.PauseMonitoring();
                            if (!worker.HasExited)
                                worker.Kill();
                            worker.WaitForExit();
                            restartCount++;
                            AppLog.Warn($"被动 guard 检测到 worker hook 缺席；当前按键已沿 Windows 原生链传递，worker 正在重启，累计重启={restartCount}。");
                            continue;
                        }
                    }
                }
                finally
                {
                    hookGuard.PauseMonitoring();
                }

                int exitCode = worker.ExitCode;
                if (exitCode == 0)
                {
                    AppLog.Info("worker 正常结束，watchdog 随之退出。");
                    AutostartState.DisableUntilNextLogon();
                    return 0;
                }

                if (exitCode == ReloadExitCode)
                {
                    // F5 是显式重载，不计为崩溃，也不增加异常重启计数。
                    AppLog.Info("worker 收到重载请求，watchdog 立即重启 worker。");
                    continue;
                }

                AppLog.Warn($"worker 异常退出，exitCode={exitCode}。");
                CrashDumpWriter.TryWriteWorkerDump(worker, exitCode);
            }
            catch (Exception ex)
            {
                AppLog.Error("watchdog 启动、监护或等待 worker 失败", ex);
                if (hookGuard.HasFailed)
                    return 1;
            }

            restartCount++;
            AppLog.Warn($"第 {restartCount} 次重启将在 2 秒后尝试。");
            Thread.Sleep(RestartDelay);
        }
    }

    private static bool WaitForWorkerOrLostHook(Process worker, WorkerHookGuard hookGuard)
    {
        while (!worker.WaitForExit(milliseconds: 100))
        {
            if (!hookGuard.IsRunning)
                throw new InvalidOperationException("worker hook guard 已停止，watchdog 将退出以便任务计划重新启动。");
            if (hookGuard.ConsumeHookLoss())
                return true;
        }

        return false;
    }

    private static Process StartWorker(JobObjectLease job)
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // dotnet run 时进程本体是 dotnet.exe；需要把当前程序集路径一起传给子进程。
        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assemblyName = typeof(WorkerWatchdog).Assembly.GetName().Name
                ?? throw new InvalidOperationException("无法确定当前程序集名称。");
            string assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assemblyPath))
                throw new InvalidOperationException("无法定位当前程序集文件。");
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--worker");
        Process worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start 未创建 worker 进程。");
        try
        {
            // 尽早归属 job，避免 watchdog 意外退出后遗留常驻 worker。
            job.Assign(worker);
            return worker;
        }
        catch
        {
            try
            {
                if (!worker.HasExited)
                    worker.Kill();
                worker.WaitForExit(milliseconds: 5000);
            }
            finally
            {
                worker.Dispose();
            }
            throw;
        }
    }
}
