namespace Remake;

/// <summary>在 M1 隐藏窗口消息泵线程上安装 M2 键盘钩子并启动动作消费者。</summary>
internal static class WorkerApp
{
    public static int Run()
    {
        AppLog.Info("worker 初始化隐藏窗口、托盘和键盘输入层。");
        using var window = new NativeWindow();
        using var dispatcher = new InputDispatcher(window);
        // hook 与 GetMessage 共享线程；释放顺序保证先卸钩，再排空动作队列。
        using var keyboardHook = new KeyboardHook(dispatcher);
        int exitCode = window.Run();
        AppLog.Info($"worker 消息循环结束，exitCode={exitCode}。");
        return exitCode;
    }
}
