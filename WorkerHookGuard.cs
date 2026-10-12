using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Remake;

/// <summary>被动观察 hook 链：主 hook 缺席时在下一个真实键盘事件通知 watchdog，不注入按键。</summary>
internal sealed unsafe class WorkerHookGuard : IDisposable
{
    private const int HookAction = 0;
    private const uint WmQuit = 0x0012;
    internal const string WorkerCallbackAckSemaphoreName = @"Local\CapsLockPlus.WorkerCallbackAck";
    internal const string WorkerHookReadyEventName = @"Local\CapsLockPlus.WorkerHookReady";
    private static readonly HOOKPROC Callback = GuardProcedure;
    private static WorkerHookGuard? _active;

    private readonly Semaphore _workerCallbackAck;
    private readonly EventWaitHandle _workerHookReady;
    private readonly AutoResetEvent _hookLost = new(false);
    private readonly AutoResetEvent _threadReady = new(false);
    private Thread? _messageThread;
    private HHOOK _hook;
    private uint _threadId;
    private Exception? _startupError;
    private volatile bool _running;
    private volatile bool _monitoring;
    private bool _disposed;

    public WorkerHookGuard()
    {
        _workerCallbackAck = new Semaphore(0, int.MaxValue, WorkerCallbackAckSemaphoreName);
        _workerHookReady = new EventWaitHandle(false, EventResetMode.ManualReset, WorkerHookReadyEventName);
        if (_active is not null)
            throw new InvalidOperationException("watchdog worker hook guard 已存在。");

        _active = this;
        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "CapsLock+ worker hook guard",
        };
        _messageThread.Start();

        if (!_threadReady.WaitOne(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("worker hook guard 未能在 5 秒内启动。");
        }
        if (_startupError is not null)
        {
            Exception error = _startupError;
            Dispose();
            throw new InvalidOperationException("无法安装 worker hook guard。", error);
        }
        AppLog.Info("被动 worker hook guard 已安装；不会注入按键。");
    }

    public bool IsRunning => _running;
    public bool HasFailed => _startupError is not null;

    public void ResetWorkerReady() => _workerHookReady.Reset();

    public bool WaitForWorkerHookReady(Process worker, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_workerHookReady.WaitOne(0))
                return true;
            if (worker.WaitForExit(milliseconds: 50))
                return false;
        }
        return _workerHookReady.WaitOne(0);
    }

    public void StartMonitoring()
    {
        if (!_running)
            throw new InvalidOperationException("worker hook guard 未运行，不能开始监护。");
        while (_workerCallbackAck.WaitOne(0)) { }
        _hookLost.Reset();
        _monitoring = true;
        AppLog.Info("worker hook 被动监护已启动。" );
    }

    public void PauseMonitoring() => _monitoring = false;

    public bool ConsumeHookLoss() => _hookLost.WaitOne(0);

    private void MessageLoop()
    {
        try
        {
            _threadId = PInvoke.GetCurrentThreadId();
            _hook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
                Callback,
                default(HINSTANCE),
                0);
            if (_hook.Value is null)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(worker hook guard) 失败。");

            // PeekMessage 会创建线程消息队列，确保卸钩时能通过 WM_QUIT 唤醒 GetMessage。
            MSG firstMessage = default;
            PInvoke.PeekMessage(&firstMessage, default, 0, 0, 0);
            _running = true;
            _threadReady.Set();

            MSG message = default;
            while (true)
            {
                BOOL result = PInvoke.GetMessage(&message, default, 0, 0);
                if (result.Value == 0)
                    break;
                if (result.Value < 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "worker hook guard GetMessage 失败。");

                PInvoke.TranslateMessage(&message);
                PInvoke.DispatchMessage(&message);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _threadReady.Set();
            AppLog.Error("worker hook guard 消息线程失败", ex);
        }
        finally
        {
            _monitoring = false;
            _running = false;
        }
    }

    private static LRESULT GuardProcedure(int code, WPARAM wParam, LPARAM lParam)
    {
        WorkerHookGuard? guard = _active;
        if (code < HookAction || guard is null || !guard._monitoring)
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        if (!guard._workerCallbackAck.WaitOne(0))
            guard._hookLost.Set();

        // 主 hook 缺席时也让当前真实输入按 Windows 原生路径继续，不丢键、不造键。
        return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        PauseMonitoring();

        if (_hook.Value is not null)
            PInvoke.UnhookWindowsHookEx(_hook);
        _hook = default;
        if (_threadId != 0 && _messageThread?.IsAlive == true)
            PInvoke.PostThreadMessage(_threadId, WmQuit, default, default);
        if (_messageThread?.IsAlive == true && !_messageThread.Join(TimeSpan.FromSeconds(2)))
            AppLog.Warn("worker hook guard 消息线程未能在 2 秒内退出。");
        _messageThread = null;
        _threadId = 0;
        if (ReferenceEquals(_active, this))
            _active = null;

        _workerCallbackAck.Dispose();
        _workerHookReady.Dispose();
        _hookLost.Dispose();
        _threadReady.Dispose();
    }
}
