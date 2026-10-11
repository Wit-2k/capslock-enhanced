using System.Diagnostics;

namespace Remake;

/// <summary>监护 worker 的进程边界；clean exit 表示用户要求退出，不再拉起。</summary>
internal static class WorkerWatchdog
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    public static int Run()
    {
        int restartCount = 0;
        while (true)
        {
            try
            {
                using Process worker = StartWorker();
                AppLog.Info($"worker 已启动，pid={worker.Id}，累计重启={restartCount}。");
                worker.WaitForExit();

                int exitCode = worker.ExitCode;
                if (exitCode == 0)
                {
                    AppLog.Info("worker 正常结束，watchdog 随之退出。");
                    return 0;
                }

                AppLog.Warn($"worker 异常退出，exitCode={exitCode}。");
            }
            catch (Exception ex)
            {
                AppLog.Error("watchdog 启动或等待 worker 失败", ex);
            }

            restartCount++;
            AppLog.Warn($"第 {restartCount} 次重启将在 2 秒后尝试。");
            Thread.Sleep(RestartDelay);
        }
    }

    private static Process StartWorker()
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
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start 未创建 worker 进程。");
    }
}
