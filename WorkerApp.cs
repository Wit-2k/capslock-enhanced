namespace Remake;

/// <summary>在消息泵线程上启动配置 watcher、M3 动作队列与 M2 键盘钩子。</summary>
internal static class WorkerApp
{
    public static int Run()
    {
        AppLog.Info("worker 初始化隐藏窗口、托盘、配置与键盘输入层。");
        using var window = new NativeWindow();
        // 首次配置快照在安装 hook 前发布，后续文件变更由 watcher 原子替换。
        using var settings = new SettingsManager();
        using var dispatcher = new InputDispatcher(window);
        // hook 与 GetMessage 共享线程；释放顺序保证先卸钩，再排空动作队列。
        using var keyboardHook = new KeyboardHook(dispatcher);
        int exitCode = window.Run();
        AppLog.Info($"worker 消息循环结束，exitCode={exitCode}。");
        return exitCode;
    }
}
