using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Remake;

/// <summary>把导航与编辑动作编码为成对的 key-down/key-up 输入事件。</summary>
internal static unsafe class InputSender
{
    private const int MaximumEventsPerAction = 256;
    private const uint WmCancelMode = 0x001F;
    internal static readonly nuint InjectionMarker = unchecked((nuint)0x434150534C4F434BUL);

    public static bool IsOurInjectedEvent(nuint extraInfo) => extraInfo == InjectionMarker;

    public static void Send(KeyboardAction action, VIRTUAL_KEY key, byte repeat, bool usesLeftAlt, bool leftAltVisible)
    {
        Span<INPUT> events = stackalloc INPUT[MaximumEventsPerAction];
        int count = 0;
        bool releaseLeftAlt = usesLeftAlt;
        bool restoreLeftAlt = releaseLeftAlt && leftAltVisible;
        HKL leftAltLayout = default;
        if (releaseLeftAlt)
        {
            // 即使 Alt 按下事件被钩子吞掉，也先清除系统修饰状态；仅透传过的 Alt 才在动作后恢复。
            leftAltLayout = GetForegroundKeyboardLayout();
            AppendKey(events, ref count, VIRTUAL_KEY.VK_MENU, keyUp: true, leftAltLayout, useScanCode: true);
        }

        AppendAction(events, ref count, action, key, repeat);

        if (restoreLeftAlt)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_MENU, keyUp: false, leftAltLayout, useScanCode: true);
        if (count == 0)
            return;

        uint inserted;
        if (restoreLeftAlt)
        {
            // 先让目标处理 Alt-up，再取消菜单态，最后投递导航键，避免方向键被系统菜单截走。
            uint released = PInvoke.SendInput(events[..1], Marshal.SizeOf<INPUT>());
            if (released != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时释放左 Alt 按键。");

            // 让目标先处理 Alt-up，再投递取消菜单态的消息。
            Thread.Sleep(20);
            HWND foreground = PInvoke.GetForegroundWindow();
            if (foreground.Value is not null)
                PInvoke.PostMessage(foreground, WmCancelMode, default, default);
            // 给目标消息循环处理 WM_CANCELMODE 的机会，再发送导航键。
            Thread.Sleep(20);
            inserted = released + PInvoke.SendInput(events[1..count], Marshal.SizeOf<INPUT>());
        }
        else
        {
            inserted = PInvoke.SendInput(events[..count], Marshal.SizeOf<INPUT>());
        }

        if (inserted == count)
            return;

        int lastError = Marshal.GetLastWin32Error();
        RecoverAfterPartialSend(events, (int)inserted, restoreLeftAlt && inserted > 0);
        throw new Win32Exception(lastError, $"SendInput 只插入 {inserted}/{count} 个事件。");
    }
    private static void AppendRepeated(Span<INPUT> events, ref int count, VIRTUAL_KEY key, byte repetitions, bool control = false, bool shift = false, HKL keyboardLayout = default)
    {
        if ((control || shift) && keyboardLayout.Value is null)
            keyboardLayout = GetForegroundKeyboardLayout();

        for (int i = 0; i < repetitions; i++)
            AppendStroke(events, ref count, key, control, shift, keyboardLayout: keyboardLayout);
    }
    private static void AppendStroke(
        Span<INPUT> events,
        ref int count,
        VIRTUAL_KEY key,
        bool control = false,
        bool shift = false,
        bool alt = false,
        HKL keyboardLayout = default)
    {
        bool useScanCode = control || shift || alt;
        if (useScanCode && keyboardLayout.Value is null)
            keyboardLayout = GetForegroundKeyboardLayout();

        if (control)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_CONTROL, keyUp: false, keyboardLayout, useScanCode);
        if (shift)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_SHIFT, keyUp: false, keyboardLayout, useScanCode);
        if (alt)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_MENU, keyUp: false, keyboardLayout, useScanCode);

        AppendKey(events, ref count, key, keyUp: false, keyboardLayout, useScanCode);
        AppendKey(events, ref count, key, keyUp: true, keyboardLayout, useScanCode);

        if (alt)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_MENU, keyUp: true, keyboardLayout, useScanCode);
        if (shift)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_SHIFT, keyUp: true, keyboardLayout, useScanCode);
        if (control)
            AppendKey(events, ref count, VIRTUAL_KEY.VK_CONTROL, keyUp: true, keyboardLayout, useScanCode);
    }
    private static void AppendKey(Span<INPUT> events, ref int count, VIRTUAL_KEY key, bool keyUp, HKL keyboardLayout = default, bool useScanCode = false)
    {
        if ((uint)count >= (uint)events.Length)
            throw new InvalidOperationException("单个默认动作生成的输入事件超过缓冲区容量。");

        ushort scanCode = 0;
        KEYBD_EVENT_FLAGS flags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        if (useScanCode)
        {
            if (keyboardLayout.Value is null)
                keyboardLayout = GetForegroundKeyboardLayout();

            uint mapped = PInvoke.MapVirtualKeyEx((uint)key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX, keyboardLayout);
            scanCode = (ushort)(mapped & 0xFF);
            if (scanCode == 0)
                throw new InvalidOperationException($"无法将虚拟键 {(ushort)key:X2} 映射到目标键盘扫描码。");

            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE;
            // 导航键需 E0 扩展标志；布局映射可能只返回基础扫描码。
            if ((mapped & 0xFF00) == 0xE000 || IsExtendedVirtualKey(key))
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        INPUT input = default;
        input.type = INPUT_TYPE.INPUT_KEYBOARD;
        // 扫描码模式必须清空 wVk，避免系统把该事件按虚拟键解释。
        input.ki = new KEYBDINPUT
        {
            wVk = useScanCode ? default : key,
            wScan = scanCode,
            dwFlags = flags,
            dwExtraInfo = InjectionMarker,
        };
        events[count++] = input;
    }
    private static HKL GetForegroundKeyboardLayout()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground.Value is null)
            return PInvoke.GetKeyboardLayout(0);

        uint threadId = PInvoke.GetWindowThreadProcessId(foreground, out _);
        return threadId == 0 ? PInvoke.GetKeyboardLayout(0) : PInvoke.GetKeyboardLayout(threadId);
    }
    private static bool IsExtendedVirtualKey(VIRTUAL_KEY key) =>
        key is VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_UP or VIRTUAL_KEY.VK_DOWN
            or VIRTUAL_KEY.VK_HOME or VIRTUAL_KEY.VK_END or VIRTUAL_KEY.VK_PRIOR or VIRTUAL_KEY.VK_NEXT
            or VIRTUAL_KEY.VK_INSERT or VIRTUAL_KEY.VK_DELETE;


    private static void AppendAction(Span<INPUT> events, ref int count, KeyboardAction action, VIRTUAL_KEY key, byte repeat)
    {
        switch (action)
        {
            case KeyboardAction.ToggleCapsLock:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_CAPITAL);
                break;
            case KeyboardAction.MoveLeft:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_LEFT, repeat);
                break;
            case KeyboardAction.MoveRight:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_RIGHT, repeat);
                break;
            case KeyboardAction.MoveUp:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_UP, repeat);
                break;
            case KeyboardAction.MoveDown:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_DOWN, repeat);
                break;
            case KeyboardAction.MoveWordLeft:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_LEFT, repeat, control: true);
                break;
            case KeyboardAction.MoveWordRight:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_RIGHT, repeat, control: true);
                break;
            case KeyboardAction.Backspace:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_BACK);
                break;
            case KeyboardAction.Delete:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_DELETE);
                break;
            case KeyboardAction.DeleteAll:
                AppendStroke(events, ref count, (VIRTUAL_KEY)'A', control: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_DELETE);
                break;
            case KeyboardAction.DeleteWord:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_LEFT, control: true, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_DELETE);
                break;
            case KeyboardAction.ForwardDeleteWord:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_RIGHT, control: true, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_DELETE);
                break;
            case KeyboardAction.Home:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME);
                break;
            case KeyboardAction.End:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END);
                break;
            case KeyboardAction.MovePageBeginning:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME, control: true);
                break;
            case KeyboardAction.MovePageEnd:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END, control: true);
                break;
            case KeyboardAction.DeleteLine:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_BACK);
                break;
            case KeyboardAction.DeleteToLineBeginning:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_BACK);
                break;
            case KeyboardAction.DeleteToLineEnd:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_BACK);
                break;
            case KeyboardAction.DeleteToPageBeginning:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME, control: true, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_BACK);
                break;
            case KeyboardAction.DeleteToPageEnd:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END, control: true, shift: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_BACK);
                break;
            case KeyboardAction.EnterWherever:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_RETURN);
                break;
            case KeyboardAction.Enter:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_RETURN);
                break;
            case KeyboardAction.Escape:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_ESCAPE);
                break;
            case KeyboardAction.SelectLeft:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_LEFT, repeat, shift: true);
                break;
            case KeyboardAction.SelectRight:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_RIGHT, repeat, shift: true);
                break;
            case KeyboardAction.SelectUp:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_UP, repeat, shift: true);
                break;
            case KeyboardAction.SelectDown:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_DOWN, repeat, shift: true);
                break;
            case KeyboardAction.SelectWordLeft:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_LEFT, repeat, control: true, shift: true);
                break;
            case KeyboardAction.SelectWordRight:
                AppendRepeated(events, ref count, VIRTUAL_KEY.VK_RIGHT, repeat, control: true, shift: true);
                break;
            case KeyboardAction.SelectHome:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME, shift: true);
                break;
            case KeyboardAction.SelectEnd:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END, shift: true);
                break;
            case KeyboardAction.SelectPageBeginning:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME, control: true, shift: true);
                break;
            case KeyboardAction.SelectPageEnd:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END, control: true, shift: true);
                break;
            case KeyboardAction.SelectCurrentWord:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_LEFT, control: true);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_RIGHT, control: true, shift: true);
                break;
            case KeyboardAction.SelectCurrentLine:
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_HOME);
                AppendStroke(events, ref count, VIRTUAL_KEY.VK_END, shift: true);
                break;
            case KeyboardAction.NativeAltShortcut:
                AppendStroke(events, ref count, key, alt: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "此动作不能直接合成为键盘事件。");
        }

    }

    public static void SendLeftAltKeyUp() => SendSingleKey(VIRTUAL_KEY.VK_MENU, keyUp: true);

    private static void SendSingleKey(VIRTUAL_KEY key, bool keyUp)
    {
        Span<INPUT> eventBuffer = stackalloc INPUT[1];
        HKL keyboardLayout = GetForegroundKeyboardLayout();
        int count = 0;
        AppendKey(eventBuffer, ref count, key, keyUp, keyboardLayout, useScanCode: true);
        SendSingleInput(eventBuffer[0].ki);
    }

    private static void SendSingleInput(KEYBDINPUT keyInput)
    {
        Span<INPUT> eventBuffer = stackalloc INPUT[1];
        eventBuffer[0] = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, ki = keyInput };
        if (PInvoke.SendInput(eventBuffer, Marshal.SizeOf<INPUT>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复键盘按键状态。");
    }
    private static void RecoverAfterPartialSend(ReadOnlySpan<INPUT> events, int inserted, bool restoreLeftAlt)
    {
        Span<KEYBDINPUT> pressed = stackalloc KEYBDINPUT[8];
        int count = 0;
        int firstEvent = restoreLeftAlt ? 1 : 0;
        for (int i = firstEvent; i < inserted; i++)
        {
            KEYBDINPUT input = events[i].ki;
            bool keyUp = (input.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) != 0;
            TrackPressedKey(pressed, ref count, input, keyUp);
        }

        ReleasePressedKeys(pressed, ref count);
        if (restoreLeftAlt)
        {
            // 用原始 key-up 事件还原 Alt 的扫描码和扩展标志。
            KEYBDINPUT leftAltDown = events[0].ki;
            leftAltDown.dwFlags &= ~KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            SendSingleInput(leftAltDown);
        }
    }

    private static void TrackPressedKey(Span<KEYBDINPUT> pressed, ref int count, KEYBDINPUT key, bool keyUp)
    {
        KEYBD_EVENT_FLAGS identityFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE | KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        if (!keyUp)
        {
            if (count == pressed.Length)
                throw new InvalidOperationException("单个动作同时按下的修饰键过多。");
            pressed[count++] = key;
            return;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            if (pressed[i].wVk != key.wVk || pressed[i].wScan != key.wScan ||
                (pressed[i].dwFlags & identityFlags) != (key.dwFlags & identityFlags))
                continue;
            for (int j = i + 1; j < count; j++)
                pressed[j - 1] = pressed[j];
            count--;
            return;
        }
    }

    private static void ReleasePressedKeys(Span<KEYBDINPUT> pressed, ref int count)
    {
        while (count > 0)
        {
            KEYBDINPUT keyUp = pressed[--count];
            keyUp.dwFlags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            SendSingleInput(keyUp);
        }
    }
}
