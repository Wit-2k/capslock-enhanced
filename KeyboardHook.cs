using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Remake;

/// <summary>低级键盘 hook 只跟踪状态、吞键并投递事件；键盘动作不在回调内执行。</summary>
internal sealed unsafe class KeyboardHook : IDisposable
{
    private const long CapsTapThresholdMilliseconds = 300;
    private const uint HookAction = 0;
    private static readonly HOOKPROC Callback = LowLevelKeyboardProcedure;
    private static KeyboardHook? _activeHook;

    private readonly InputDispatcher _dispatcher;
    private HHOOK _hook;
    private readonly Semaphore _workerCallbackAck;
    private readonly EventWaitHandle _workerHookReady;
    private readonly HashSet<uint> _swallowedKeyUps = new(capacity: 64);
    private bool _capsDown;
    private bool _capsPassthrough;
    private bool _capsWasUsed;
    private long _capsPressedAt;
    private bool _leftAltDown;
    private bool _leftAltSwallowed;
    private bool _leftAltPassedThrough;
    private bool _leftAltUsedWithCaps;
    private bool _leftWinDown;
    private bool _rightWinDown;
    private bool _leftControlDown;
    private bool _rightControlDown;
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _rightAltDown;
    private bool _disposed;

    public KeyboardHook(InputDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _workerCallbackAck = Semaphore.OpenExisting(WorkerHookGuard.WorkerCallbackAckSemaphoreName);
        try
        {
            _workerHookReady = EventWaitHandle.OpenExisting(WorkerHookGuard.WorkerHookReadyEventName);
        }
        catch
        {
            _workerCallbackAck.Dispose();
            throw;
        }
        _ = DefaultKeyBindings.CombinationBindingCount;
        if (_activeHook is not null)
        {
            _workerCallbackAck.Dispose();
            _workerHookReady.Dispose();
            throw new InvalidOperationException("当前进程已经安装键盘 hook。");
        }

        _activeHook = this;
        try
        {
            InstallHook();
        }
        catch
        {
            _activeHook = null;
            _workerCallbackAck.Dispose();
            _workerHookReady.Dispose();
            throw;
        }
        AppLog.Info($"WH_KEYBOARD_LL 已安装；默认绑定数={DefaultKeyBindings.TotalDefaultBindingCount}。");
    }

    private static LRESULT LowLevelKeyboardProcedure(int code, WPARAM wParam, LPARAM lParam)
    {
        KeyboardHook? hook = _activeHook;
        if (code < HookAction || hook is null)
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        bool ackReleased = false;
        try
        {
            KBDLLHOOKSTRUCT key = *(KBDLLHOOKSTRUCT*)lParam.Value;
            // 令牌在下一 hook 回调前发布；若动作吞键，则本回调在返回前回收令牌。
            hook._workerCallbackAck.Release();
            ackReleased = true;
            if ((key.flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0 && InputSender.IsOurInjectedEvent(key.dwExtraInfo))
            {
                LRESULT next = PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
                if (next.Value != 0)
                    hook._workerCallbackAck.WaitOne(0);
                return next;
            }

            bool keyUp = (key.flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_UP) != 0;
            LRESULT result = hook.HandleKey((VIRTUAL_KEY)key.vkCode, keyUp, code, wParam, lParam);
            if (result.Value != 0)
                hook._workerCallbackAck.WaitOne(0);
            return result;
        }
        catch (Exception ex)
        {
            // 异常只排入日志队列；若无法发布 ACK，guard 会按 hook 缺席处理。
            hook._dispatcher.TryReportHookError(ex);
            if (!ackReleased)
            {
                try
                {
                    hook._workerCallbackAck.Release();
                    ackReleased = true;
                }
                catch (SemaphoreFullException)
                {
                    // 满信号量意味着已有未消费 ACK，本次仍透传并让 guard 消费它。
                }
                catch (ObjectDisposedException)
                {
                    // hook 关闭竞争由 watchdog 的 guard 检出。
                }
            }
            LRESULT next = PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
            if (next.Value != 0)
                hook._workerCallbackAck.WaitOne(0);
            return next;
        }
    }

    private void InstallHook()
    {
        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
            Callback,
            default(HINSTANCE),
            0);
        if (_hook.Value is null)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) 失败。");
        _workerHookReady.Set();
    }

    public void Reinstall(string reason)
    {
        if (_disposed)
            return;

        if (_leftAltPassedThrough)
            _dispatcher.TrySetLeftAltVisible(visible: false, physicalEventForwarded: false);
        if (_hook.Value is not null && PInvoke.UnhookWindowsHookEx(_hook).Value == 0)
            AppLog.Warn($"重新安装键盘 hook 时旧句柄卸载失败，Win32Error={Marshal.GetLastWin32Error()}。");
        _hook = default;
        ResetKeyState();
        InstallHook();
        AppLog.Info($"WH_KEYBOARD_LL 已重新安装；reason={reason}。");
    }

    private void ResetKeyState()
    {
        _swallowedKeyUps.Clear();
        _capsDown = _capsPassthrough = _capsWasUsed = false;
        _leftAltDown = _leftAltSwallowed = _leftAltPassedThrough = _leftAltUsedWithCaps = false;
        _leftWinDown = _rightWinDown = false;
        _leftControlDown = _rightControlDown = false;
        _leftShiftDown = _rightShiftDown = false;
        _rightAltDown = false;
        _capsPressedAt = 0;
    }


    private LRESULT HandleKey(VIRTUAL_KEY key, bool keyUp, int code, WPARAM wParam, LPARAM lParam)
    {
        if (key == VIRTUAL_KEY.VK_CAPITAL)
            return HandleCapsLock(keyUp, code, wParam, lParam);
        if (key == VIRTUAL_KEY.VK_LMENU)
            return HandleLeftAlt(keyUp, code, wParam, lParam);
        if (key is VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN)
        {
            if (key == VIRTUAL_KEY.VK_LWIN)
                _leftWinDown = !keyUp;
            else
                _rightWinDown = !keyUp;
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }

        if (key is VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT)
        {
            UpdateOrdinaryModifier(key, keyUp);
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }

        if (key == VIRTUAL_KEY.VK_RMENU && !_capsDown)
        {
            _rightAltDown = !keyUp;
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }

        uint virtualKey = (uint)key;
        if (keyUp)
            return _swallowedKeyUps.Remove(virtualKey)
                ? Swallow()
                : PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        if (_swallowedKeyUps.Contains(virtualKey))
        {
            if (_capsDown)
                DispatchCapsBinding(key, code, wParam, lParam);
            return Swallow();
        }

        if (!_capsDown)
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        return DispatchCapsBinding(key, code, wParam, lParam);
    }

    private LRESULT HandleCapsLock(bool keyUp, int code, WPARAM wParam, LPARAM lParam)
    {
        if (!keyUp)
        {
            if (_capsDown)
                return Swallow();
            if (_capsPassthrough)
                return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

            if (_leftControlDown || _rightControlDown || _leftShiftDown || _rightShiftDown || _rightAltDown)
            {
                _capsPassthrough = true;
                return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
            }

            _capsDown = true;
            _capsWasUsed = _leftAltDown || _leftWinDown || _rightWinDown;
            _capsPressedAt = Environment.TickCount64;
            return Swallow();
        }

        if (_capsPassthrough)
        {
            _capsPassthrough = false;
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }
        if (!_capsDown)
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        _capsDown = false;
        bool isTap = !_capsWasUsed && Environment.TickCount64 - _capsPressedAt < CapsTapThresholdMilliseconds;
        _capsWasUsed = false;
        if (isTap)
        {
            KeyBinding tap = DefaultKeyBindings.CapsTap;
            _dispatcher.TryDispatch(tap.Action, VIRTUAL_KEY.VK_CAPITAL, HotkeyModifiers.None, tap.Repeat, usesLeftAlt: false, argument: tap.Argument);
        }
        return Swallow();
    }

    private LRESULT HandleLeftAlt(bool keyUp, int code, WPARAM wParam, LPARAM lParam)
    {
        if (!keyUp)
        {
            if (_leftAltDown)
                return _leftAltSwallowed ? Swallow() : PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

            _leftAltDown = true;
            if (_capsDown)
            {
                _leftAltSwallowed = true;
                _leftAltPassedThrough = false;
                _leftAltUsedWithCaps = false;
                return Swallow();
            }

            _leftAltSwallowed = false;
            _leftAltPassedThrough = true;
            _leftAltUsedWithCaps = false;
            _dispatcher.TrySetLeftAltVisible(visible: true, physicalEventForwarded: true);
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }

        if (!_leftAltDown)
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        _leftAltDown = false;
        if (_leftAltSwallowed)
        {
            _leftAltSwallowed = false;
            _leftAltUsedWithCaps = false;
            return Swallow();
        }

        if (_leftAltUsedWithCaps)
        {
            _leftAltPassedThrough = false;
            _leftAltUsedWithCaps = false;
            return _dispatcher.TrySetLeftAltVisible(visible: false, physicalEventForwarded: false)
                ? Swallow()
                : PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }

        if (_leftAltPassedThrough)
        {
            _leftAltPassedThrough = false;
            _dispatcher.TrySetLeftAltVisible(visible: false, physicalEventForwarded: true);
        }
        return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
    }

    private LRESULT DispatchCapsBinding(VIRTUAL_KEY key, int code, WPARAM wParam, LPARAM lParam)
    {
        HotkeyModifiers modifiers = _leftAltDown
            ? HotkeyModifiers.LeftAlt
            : (_leftWinDown || _rightWinDown) ? HotkeyModifiers.Windows : HotkeyModifiers.None;

        if (DefaultKeyBindings.TryGet(key, modifiers, out KeyBinding binding))
        {
            _capsWasUsed = true;
            switch (binding.Action)
            {
                case KeyboardAction.Consume:
                    _dispatcher.TryLogNoOpBinding(key, modifiers);
                    _swallowedKeyUps.Add((uint)key);
                    return Swallow();
                case KeyboardAction.PassThrough:
                    _dispatcher.TryLogPassThroughBinding(key, modifiers);
                    if ((modifiers & HotkeyModifiers.LeftAlt) != 0 && _leftAltSwallowed)
                        return QueueAction(KeyboardAction.NativeAltShortcut, key, modifiers, 1, usesLeftAlt: false, code, wParam, lParam);
                    return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
                default:
                    return QueueAction(binding.Action, key, modifiers, binding.Repeat, usesLeftAlt: (modifiers & HotkeyModifiers.LeftAlt) != 0, code, wParam, lParam, binding.Argument);
            }
        }

        // 剪贴板槽位被暂缓；无 LAlt 时透传字母，有 LAlt 时保留原生 Alt+C/X/V。
        if (key is VIRTUAL_KEY.VK_C or VIRTUAL_KEY.VK_X or VIRTUAL_KEY.VK_V)
        {
            _capsWasUsed = true;
            if ((modifiers & HotkeyModifiers.LeftAlt) != 0 && _leftAltSwallowed)
                return QueueAction(KeyboardAction.NativeAltShortcut, key, modifiers, 1, usesLeftAlt: false, code, wParam, lParam);
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
        }

        // AHK 的 *Esc 热键会吞掉该键，但它不占默认表中的 121 条配置项。
        if (key == VIRTUAL_KEY.VK_ESCAPE)
        {
            _capsWasUsed = true;
            _dispatcher.TryLogNoOpBinding(key, modifiers);
            _swallowedKeyUps.Add((uint)key);
            return Swallow();
        }

        return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);
    }

    private LRESULT QueueAction(
        KeyboardAction action,
        VIRTUAL_KEY key,
        HotkeyModifiers modifiers,
        byte repeat,
        bool usesLeftAlt,
        int code,
        WPARAM wParam,
        LPARAM lParam,
        string? argument = null)
    {
        if (!_dispatcher.TryDispatch(action, key, modifiers, repeat, usesLeftAlt, argument))
            return PInvoke.CallNextHookEx(default(HHOOK), code, wParam, lParam);

        if (usesLeftAlt && _leftAltPassedThrough)
            _leftAltUsedWithCaps = true;
        _swallowedKeyUps.Add((uint)key);
        return Swallow();
    }

    private void UpdateOrdinaryModifier(VIRTUAL_KEY key, bool keyUp)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_LCONTROL: _leftControlDown = !keyUp; break;
            case VIRTUAL_KEY.VK_RCONTROL: _rightControlDown = !keyUp; break;
            case VIRTUAL_KEY.VK_LSHIFT: _leftShiftDown = !keyUp; break;
            case VIRTUAL_KEY.VK_RSHIFT: _rightShiftDown = !keyUp; break;
        }
    }

    private static LRESULT Swallow() => (LRESULT)(nint)1;



    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_hook.Value is not null)
            PInvoke.UnhookWindowsHookEx(_hook);
        _hook = default;
        if (ReferenceEquals(_activeHook, this))
            _activeHook = null;
        _workerCallbackAck.Dispose();
        _workerHookReady.Dispose();
    }
}
