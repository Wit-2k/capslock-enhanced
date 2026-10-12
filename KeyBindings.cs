using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Remake;

[Flags]
internal enum HotkeyModifiers : byte
{
    None = 0,
    LeftAlt = 1,
    Windows = 2,
}

internal enum KeyboardAction : byte
{
    Consume,
    PassThrough,
    ReloadWorker,
    ToggleCapsLock,
    MoveLeft,
    MoveRight,
    MoveUp,
    MoveDown,
    MoveWordLeft,
    MoveWordRight,
    Backspace,
    Delete,
    DeleteAll,
    DeleteWord,
    ForwardDeleteWord,
    Home,
    End,
    MovePageBeginning,
    MovePageEnd,
    DeleteLine,
    DeleteToLineBeginning,
    DeleteToLineEnd,
    DeleteToPageBeginning,
    DeleteToPageEnd,
    EnterWherever,
    Enter,
    Escape,
    SelectLeft,
    SelectRight,
    SelectUp,
    SelectDown,
    SelectWordLeft,
    SelectWordRight,
    SelectHome,
    SelectEnd,
    SelectPageBeginning,
    SelectPageEnd,
    SelectCurrentWord,
    SelectCurrentLine,
    NativeAltShortcut,
}

internal readonly record struct KeyChord(VIRTUAL_KEY Key, HotkeyModifiers Modifiers);
internal readonly record struct KeyBinding(KeyboardAction Action, byte Repeat = 1);

/// <summary>只保存本次输入层需要的默认映射；INI 覆盖和完整 action registry 属于 M3。</summary>
internal static class DefaultKeyBindings
{
    private static readonly Dictionary<KeyChord, KeyBinding> Bindings = CreateBindings();

    public const int TotalDefaultBindingCount = 121;
    public static int CombinationBindingCount => Bindings.Count;

    static DefaultKeyBindings()
    {
        // 121 条包括 press_caps；剪贴板的六个自定义槽位绑定不迁移。
        if (Bindings.Count + 1 != TotalDefaultBindingCount)
            throw new InvalidOperationException($"默认键位数错误：{Bindings.Count + 1}。");
    }

    public static KeyBinding CapsTap => new(KeyboardAction.ToggleCapsLock);

    public static bool TryGet(VIRTUAL_KEY key, HotkeyModifiers modifiers, out KeyBinding binding) =>
        Bindings.TryGetValue(new KeyChord(key, modifiers), out binding);

    private static Dictionary<KeyChord, KeyBinding> CreateBindings()
    {
        var bindings = new Dictionary<KeyChord, KeyBinding>(capacity: 120);

        // 未列出的默认 no-op 键也要吞掉，避免 CapsLock 修饰键漏成普通字符。
        foreach (char key in "ABDEFGHIJKLMNOPQRSTUWYZ")
            Add(bindings, (VIRTUAL_KEY)key, HotkeyModifiers.None, KeyboardAction.Consume);
        foreach (char key in "ABDEFGHIJKLMNOPQRSTUWYZ")
            Add(bindings, (VIRTUAL_KEY)key, HotkeyModifiers.LeftAlt, KeyboardAction.Consume);

        // 十个数字键的既有动作属于 M4 窗口绑定；M2 暂时让系统按键透传。
        foreach (char key in "0123456789")
        {
            Add(bindings, (VIRTUAL_KEY)key, HotkeyModifiers.None, KeyboardAction.PassThrough);
            Add(bindings, (VIRTUAL_KEY)key, HotkeyModifiers.LeftAlt, KeyboardAction.PassThrough);
            Add(bindings, (VIRTUAL_KEY)key, HotkeyModifiers.Windows, KeyboardAction.PassThrough);
        }

        VIRTUAL_KEY[] punctuation =
        [
            VIRTUAL_KEY.VK_OEM_3, VIRTUAL_KEY.VK_OEM_MINUS, VIRTUAL_KEY.VK_OEM_PLUS,
            VIRTUAL_KEY.VK_OEM_4, VIRTUAL_KEY.VK_OEM_6, VIRTUAL_KEY.VK_OEM_5,
            VIRTUAL_KEY.VK_OEM_1, VIRTUAL_KEY.VK_OEM_7, VIRTUAL_KEY.VK_OEM_COMMA,
            VIRTUAL_KEY.VK_OEM_PERIOD, VIRTUAL_KEY.VK_OEM_2, VIRTUAL_KEY.VK_BACK,
            VIRTUAL_KEY.VK_TAB, VIRTUAL_KEY.VK_RETURN, VIRTUAL_KEY.VK_SPACE,
            VIRTUAL_KEY.VK_RMENU,
        ];
        foreach (VIRTUAL_KEY key in punctuation)
        {
            Add(bindings, key, HotkeyModifiers.None, KeyboardAction.Consume);
            Add(bindings, key, HotkeyModifiers.LeftAlt, KeyboardAction.Consume);
        }

        for (int keyCode = (int)VIRTUAL_KEY.VK_F1; keyCode <= (int)VIRTUAL_KEY.VK_F12; keyCode++)
            Add(bindings, (VIRTUAL_KEY)keyCode, HotkeyModifiers.None, KeyboardAction.Consume);

        // Base layer: navigation, selection and deletion.
        Add(bindings, VIRTUAL_KEY.VK_A, HotkeyModifiers.None, KeyboardAction.MoveWordLeft);
        Add(bindings, VIRTUAL_KEY.VK_B, HotkeyModifiers.None, KeyboardAction.MoveDown, 10);
        Add(bindings, VIRTUAL_KEY.VK_D, HotkeyModifiers.None, KeyboardAction.MoveDown);
        Add(bindings, VIRTUAL_KEY.VK_E, HotkeyModifiers.None, KeyboardAction.MoveUp);
        Add(bindings, VIRTUAL_KEY.VK_F, HotkeyModifiers.None, KeyboardAction.MoveRight);
        Add(bindings, VIRTUAL_KEY.VK_G, HotkeyModifiers.None, KeyboardAction.MoveWordRight);
        Add(bindings, VIRTUAL_KEY.VK_H, HotkeyModifiers.None, KeyboardAction.SelectWordLeft);
        Add(bindings, VIRTUAL_KEY.VK_I, HotkeyModifiers.None, KeyboardAction.SelectUp);
        Add(bindings, VIRTUAL_KEY.VK_J, HotkeyModifiers.None, KeyboardAction.SelectLeft);
        Add(bindings, VIRTUAL_KEY.VK_K, HotkeyModifiers.None, KeyboardAction.SelectDown);
        Add(bindings, VIRTUAL_KEY.VK_L, HotkeyModifiers.None, KeyboardAction.SelectRight);
        Add(bindings, VIRTUAL_KEY.VK_N, HotkeyModifiers.None, KeyboardAction.SelectDown, 10);
        Add(bindings, VIRTUAL_KEY.VK_O, HotkeyModifiers.None, KeyboardAction.SelectEnd);
        Add(bindings, VIRTUAL_KEY.VK_P, HotkeyModifiers.None, KeyboardAction.Home);
        Add(bindings, VIRTUAL_KEY.VK_R, HotkeyModifiers.None, KeyboardAction.Delete);
        Add(bindings, VIRTUAL_KEY.VK_S, HotkeyModifiers.None, KeyboardAction.MoveLeft);
        Add(bindings, VIRTUAL_KEY.VK_T, HotkeyModifiers.None, KeyboardAction.MoveUp, 10);
        Add(bindings, VIRTUAL_KEY.VK_U, HotkeyModifiers.None, KeyboardAction.SelectHome);
        Add(bindings, VIRTUAL_KEY.VK_W, HotkeyModifiers.None, KeyboardAction.Backspace);
        Add(bindings, VIRTUAL_KEY.VK_Y, HotkeyModifiers.None, KeyboardAction.SelectUp, 10);
        Add(bindings, VIRTUAL_KEY.VK_BACK, HotkeyModifiers.None, KeyboardAction.DeleteLine);
        Add(bindings, VIRTUAL_KEY.VK_OEM_4, HotkeyModifiers.None, KeyboardAction.DeleteToLineBeginning);
        Add(bindings, VIRTUAL_KEY.VK_OEM_1, HotkeyModifiers.None, KeyboardAction.End);
        Add(bindings, VIRTUAL_KEY.VK_RETURN, HotkeyModifiers.None, KeyboardAction.EnterWherever);
        Add(bindings, VIRTUAL_KEY.VK_OEM_COMMA, HotkeyModifiers.None, KeyboardAction.SelectCurrentWord);
        Add(bindings, VIRTUAL_KEY.VK_OEM_PERIOD, HotkeyModifiers.None, KeyboardAction.SelectWordRight);
        Add(bindings, VIRTUAL_KEY.VK_OEM_2, HotkeyModifiers.None, KeyboardAction.DeleteToLineEnd);
        Add(bindings, VIRTUAL_KEY.VK_SPACE, HotkeyModifiers.None, KeyboardAction.Enter);
        // README“其他”里的 F1/F4/F6 不迁移到 remake，继续让系统接收原键。
        Add(bindings, VIRTUAL_KEY.VK_F1, HotkeyModifiers.None, KeyboardAction.PassThrough);
        Add(bindings, VIRTUAL_KEY.VK_F4, HotkeyModifiers.None, KeyboardAction.PassThrough);
        Add(bindings, VIRTUAL_KEY.VK_F5, HotkeyModifiers.None, KeyboardAction.ReloadWorker);
        Add(bindings, VIRTUAL_KEY.VK_F6, HotkeyModifiers.None, KeyboardAction.PassThrough);

        // LAlt layer: larger navigation steps use the same exact physical key table.
        Add(bindings, VIRTUAL_KEY.VK_A, HotkeyModifiers.LeftAlt, KeyboardAction.MoveWordLeft, 3);
        Add(bindings, VIRTUAL_KEY.VK_B, HotkeyModifiers.LeftAlt, KeyboardAction.MoveDown, 30);
        Add(bindings, VIRTUAL_KEY.VK_D, HotkeyModifiers.LeftAlt, KeyboardAction.MoveDown, 3);
        Add(bindings, VIRTUAL_KEY.VK_E, HotkeyModifiers.LeftAlt, KeyboardAction.MoveUp, 3);
        Add(bindings, VIRTUAL_KEY.VK_F, HotkeyModifiers.LeftAlt, KeyboardAction.MoveRight, 5);
        Add(bindings, VIRTUAL_KEY.VK_G, HotkeyModifiers.LeftAlt, KeyboardAction.MoveWordRight, 3);
        Add(bindings, VIRTUAL_KEY.VK_H, HotkeyModifiers.LeftAlt, KeyboardAction.SelectWordLeft, 3);
        Add(bindings, VIRTUAL_KEY.VK_I, HotkeyModifiers.LeftAlt, KeyboardAction.SelectUp, 3);
        Add(bindings, VIRTUAL_KEY.VK_J, HotkeyModifiers.LeftAlt, KeyboardAction.SelectLeft, 5);
        Add(bindings, VIRTUAL_KEY.VK_K, HotkeyModifiers.LeftAlt, KeyboardAction.SelectDown, 3);
        Add(bindings, VIRTUAL_KEY.VK_L, HotkeyModifiers.LeftAlt, KeyboardAction.SelectRight, 5);
        Add(bindings, VIRTUAL_KEY.VK_N, HotkeyModifiers.LeftAlt, KeyboardAction.SelectDown, 30);
        Add(bindings, VIRTUAL_KEY.VK_O, HotkeyModifiers.LeftAlt, KeyboardAction.SelectPageEnd);
        Add(bindings, VIRTUAL_KEY.VK_P, HotkeyModifiers.LeftAlt, KeyboardAction.MovePageBeginning);
        Add(bindings, VIRTUAL_KEY.VK_R, HotkeyModifiers.LeftAlt, KeyboardAction.ForwardDeleteWord);
        Add(bindings, VIRTUAL_KEY.VK_S, HotkeyModifiers.LeftAlt, KeyboardAction.MoveLeft, 5);
        Add(bindings, VIRTUAL_KEY.VK_T, HotkeyModifiers.LeftAlt, KeyboardAction.MoveUp, 30);
        Add(bindings, VIRTUAL_KEY.VK_U, HotkeyModifiers.LeftAlt, KeyboardAction.SelectPageBeginning);
        Add(bindings, VIRTUAL_KEY.VK_W, HotkeyModifiers.LeftAlt, KeyboardAction.DeleteWord);
        Add(bindings, VIRTUAL_KEY.VK_Y, HotkeyModifiers.LeftAlt, KeyboardAction.SelectUp, 30);
        Add(bindings, VIRTUAL_KEY.VK_BACK, HotkeyModifiers.LeftAlt, KeyboardAction.DeleteAll);
        Add(bindings, VIRTUAL_KEY.VK_OEM_4, HotkeyModifiers.LeftAlt, KeyboardAction.DeleteToPageBeginning);
        Add(bindings, VIRTUAL_KEY.VK_OEM_1, HotkeyModifiers.LeftAlt, KeyboardAction.MovePageEnd);
        Add(bindings, VIRTUAL_KEY.VK_OEM_COMMA, HotkeyModifiers.LeftAlt, KeyboardAction.SelectCurrentLine);
        Add(bindings, VIRTUAL_KEY.VK_OEM_PERIOD, HotkeyModifiers.LeftAlt, KeyboardAction.SelectWordRight, 3);
        Add(bindings, VIRTUAL_KEY.VK_OEM_2, HotkeyModifiers.LeftAlt, KeyboardAction.DeleteToPageEnd);

        return bindings;
    }

    private static void Add(Dictionary<KeyChord, KeyBinding> bindings, VIRTUAL_KEY key, HotkeyModifiers modifiers, KeyboardAction action, byte repeat = 1)
    {
        bindings[new KeyChord(key, modifiers)] = new KeyBinding(action, repeat);
    }
}
