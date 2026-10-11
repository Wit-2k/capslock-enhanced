namespace Remake;

/// <summary>运行 M1 常驻 worker；M2 的键盘钩子后续接入同一消息泵。</summary>
internal static class WorkerApp
{
    public static int Run()
    {
        AppLog.Info("worker 初始化隐藏窗口与系统托盘。");
        using var window = new NativeWindow();
        int exitCode = window.Run();
        AppLog.Info($"worker 消息循环结束，exitCode={exitCode}。");
        return exitCode;
    }
}
