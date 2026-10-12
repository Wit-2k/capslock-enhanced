using System.Threading.Channels;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Remake;

internal enum InputWorkKind : byte
{
    DispatchAction,
    SetLeftAltVisible,
    LogNoOpBinding,
    LogPassThroughBinding,
    ReportHookError,
}

internal readonly record struct InputWorkItem(
    InputWorkKind Kind,
    KeyboardAction Action,
    VIRTUAL_KEY Key,
    byte Repeat,
    HotkeyModifiers Modifiers,
    bool UsesLeftAlt,
    bool LeftAltVisible,
    bool PhysicalEventForwarded,
    Exception? Error);

/// <summary>钩子只向无界单读队列投递小型值；SendInput 和日志都在此后台线程执行。</summary>
internal sealed class InputDispatcher : IDisposable
{
    private readonly Channel<InputWorkItem> _queue = Channel.CreateUnbounded<InputWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly NativeWindow _window;
    private readonly Task _consumer;
    private bool _leftAltVisible;
    private bool _disposed;

    public InputDispatcher(NativeWindow window)
    {
        _window = window;
        _consumer = Task.Run(ConsumeAsync);
    }

    public bool TryDispatch(KeyboardAction action, VIRTUAL_KEY key, HotkeyModifiers modifiers, byte repeat, bool usesLeftAlt) =>
        _queue.Writer.TryWrite(new InputWorkItem(InputWorkKind.DispatchAction, action, key, repeat, modifiers, usesLeftAlt, false, false, null));

    public bool TrySetLeftAltVisible(bool visible, bool physicalEventForwarded) =>
        _queue.Writer.TryWrite(new InputWorkItem(InputWorkKind.SetLeftAltVisible, default, default, 0, default, false, visible, physicalEventForwarded, null));

    public bool TryReplayLeftAltShortcut(VIRTUAL_KEY key) =>
        TryDispatch(KeyboardAction.NativeAltShortcut, key, HotkeyModifiers.LeftAlt, 1, usesLeftAlt: false);

    public bool TryLogNoOpBinding(VIRTUAL_KEY key, HotkeyModifiers modifiers) =>
        _queue.Writer.TryWrite(new InputWorkItem(InputWorkKind.LogNoOpBinding, default, key, 0, modifiers, false, false, false, null));

    public bool TryLogPassThroughBinding(VIRTUAL_KEY key, HotkeyModifiers modifiers) =>
        _queue.Writer.TryWrite(new InputWorkItem(InputWorkKind.LogPassThroughBinding, default, key, 0, modifiers, false, false, false, null));

    public bool TryReportHookError(Exception error) =>
        _queue.Writer.TryWrite(new InputWorkItem(InputWorkKind.ReportHookError, default, default, 0, default, false, false, false, error));

    private async Task ConsumeAsync()
    {
        await foreach (InputWorkItem item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Kind == InputWorkKind.SetLeftAltVisible)
            {
                if (!item.LeftAltVisible && _leftAltVisible && !item.PhysicalEventForwarded)
                    InputSender.SendLeftAltKeyUp();
                _leftAltVisible = item.LeftAltVisible;
                continue;
            }

            if (item.Kind == InputWorkKind.LogNoOpBinding)
            {
                AppLog.Info($"吞掉默认 no-op 键位：{item.Modifiers}+VK_{(ushort)item.Key:X2}。");
                continue;
            }

            if (item.Kind == InputWorkKind.LogPassThroughBinding)
            {
                AppLog.Info($"M3/M4 动作尚未接入，透传键位：{item.Modifiers}+VK_{(ushort)item.Key:X2}。");
                continue;
            }

            if (item.Kind == InputWorkKind.ReportHookError)
            {
                AppLog.Error("低级键盘钩子回调异常", item.Error);
                continue;
            }

            if (item.Action == KeyboardAction.ReloadWorker)
            {
                // 通过窗口消息请求 watchdog 有序重启，不把用户的 F5 当作崩溃。
                if (_window.RequestReload())
                    AppLog.Info("F5 已请求 watchdog 重启 worker。");
                else
                    AppLog.Warn("无法发送 worker reload 请求。");
                continue;
            }
            try
            {
                InputSender.Send(item.Action, item.Key, item.Repeat, item.UsesLeftAlt, _leftAltVisible);
                AppLog.Info($"输入派发：{item.Action}，repeat={item.Repeat}，leftAltModifier={item.UsesLeftAlt}，leftAltVisible={_leftAltVisible}。");
            }
            catch (Exception ex)
            {
                // 单条合成输入失败只记录，不让 worker 停止接收后续热键。
                AppLog.Error($"SendInput 动作失败：{item.Action}", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();
        _consumer.GetAwaiter().GetResult();
    }
}
