using System.Threading;
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
    SendText,
    RunTarget,
}

internal readonly record struct KeyChord(VIRTUAL_KEY Key, HotkeyModifiers Modifiers);
internal readonly record struct KeyBinding(KeyboardAction Action, byte Repeat = 1, string? Argument = null);

/// <summary>通过一次引用交换向低级 hook 发布不可变键位快照。</summary>
internal static class DefaultKeyBindings
{
    private sealed class BindingSnapshot(Dictionary<KeyChord, KeyBinding> bindings, KeyBinding capsTap)
    {
        public Dictionary<KeyChord, KeyBinding> Bindings { get; } = bindings;
        public KeyBinding CapsTap { get; } = capsTap;
    }

    private static readonly Dictionary<KeyChord, KeyBinding> DefaultBindings = CreateBindings();
    private static BindingSnapshot _active = new(DefaultBindings, new KeyBinding(KeyboardAction.ToggleCapsLock));

    public const int TotalDefaultBindingCount = 121;
    public static int CombinationBindingCount => Volatile.Read(ref _active).Bindings.Count;

    static DefaultKeyBindings()
    {
        // 保持原版默认键位总数；press_caps 单独计一条。
        if (DefaultBindings.Count + 1 != TotalDefaultBindingCount)
            throw new InvalidOperationException($"默认键位数错误：{DefaultBindings.Count + 1}。");
    }

    public static KeyBinding CapsTap => Volatile.Read(ref _active).CapsTap;

    public static bool TryGet(VIRTUAL_KEY key, HotkeyModifiers modifiers, out KeyBinding binding)
    {
        BindingSnapshot current = Volatile.Read(ref _active);
        return current.Bindings.TryGetValue(new KeyChord(key, modifiers), out binding);
    }

    public static void ReplaceOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var bindings = new Dictionary<KeyChord, KeyBinding>(DefaultBindings);
        KeyBinding capsTap = new(KeyboardAction.ToggleCapsLock);
        int applied = 0;

        foreach ((string rawKey, string functionSpec) in overrides)
        {
            string settingKey = NormalizeSettingKey(rawKey);
            if (!Actions.TryParseFunctionSpec(functionSpec, out KeyBinding binding, out string error))
            {
                AppLog.Warn($"忽略无效 [Keys] 函数：{settingKey}，{error}。");
                continue;
            }

            if (settingKey == "press_caps")
            {
                capsTap = binding;
                applied++;
                continue;
            }

            if (!TryParseChordName(settingKey, out KeyChord chord))
            {
                AppLog.Warn($"忽略无法识别的 [Keys] 键位：{settingKey}。");
                continue;
            }

            bindings[chord] = binding;
            applied++;
        }

        Volatile.Write(ref _active, new BindingSnapshot(bindings, capsTap));
        AppLog.Info($"[Keys] 配置快照已替换，生效覆盖数={applied}。");
    }

    internal static string NormalizeSettingKey(string value) => value.Trim().ToLowerInvariant();

    private static bool TryParseChordName(string settingKey, out KeyChord chord)
    {
        HotkeyModifiers modifiers;
        string keyName;
        if (settingKey.StartsWith("caps_lalt_", StringComparison.Ordinal))
        {
            modifiers = HotkeyModifiers.LeftAlt;
            keyName = settingKey[10..];
        }
        else if (settingKey.StartsWith("caps_win_", StringComparison.Ordinal))
        {
            modifiers = HotkeyModifiers.Windows;
            keyName = settingKey[9..];
        }
        else if (settingKey.StartsWith("caps_", StringComparison.Ordinal))
        {
            modifiers = HotkeyModifiers.None;
            keyName = settingKey[5..];
        }
        else
        {
            chord = default;
            return false;
        }

        if (!TryParseKeyName(keyName, out VIRTUAL_KEY key))
        {
            chord = default;
            return false;
        }

        chord = new KeyChord(key, modifiers);
        return true;
    }

    internal static bool TryParseKeyName(string value, out VIRTUAL_KEY key)
    {
        if (value.Length == 1 && char.IsAsciiLetterOrDigit(value[0]))
        {
            key = (VIRTUAL_KEY)char.ToUpperInvariant(value[0]);
            return true;
        }

        if (value.Length is 2 or 3 && value[0] == 'f' &&
            byte.TryParse(value.AsSpan(1), out byte functionNumber) && functionNumber is >= 1 and <= 12)
        {
            key = (VIRTUAL_KEY)((int)VIRTUAL_KEY.VK_F1 + functionNumber - 1);
            return true;
        }

        key = value switch
        {
            "backspace" or "bs" => VIRTUAL_KEY.VK_BACK,
            "esc" or "escape" => VIRTUAL_KEY.VK_ESCAPE,
            "space" => VIRTUAL_KEY.VK_SPACE,
            "tab" => VIRTUAL_KEY.VK_TAB,
            "enter" or "return" => VIRTUAL_KEY.VK_RETURN,
            "left" => VIRTUAL_KEY.VK_LEFT,
            "right" => VIRTUAL_KEY.VK_RIGHT,
            "up" => VIRTUAL_KEY.VK_UP,
            "down" => VIRTUAL_KEY.VK_DOWN,
            "home" => VIRTUAL_KEY.VK_HOME,
            "end" => VIRTUAL_KEY.VK_END,
            "pageup" or "pgup" or "prior" => VIRTUAL_KEY.VK_PRIOR,
            "pagedown" or "pgdn" or "next" => VIRTUAL_KEY.VK_NEXT,
            "insert" or "ins" => VIRTUAL_KEY.VK_INSERT,
            "delete" or "del" => VIRTUAL_KEY.VK_DELETE,
            "ctrl" or "control" => VIRTUAL_KEY.VK_CONTROL,
            "shift" => VIRTUAL_KEY.VK_SHIFT,
            "alt" or "menu" => VIRTUAL_KEY.VK_MENU,
            "lctrl" or "lcontrol" => VIRTUAL_KEY.VK_LCONTROL,
            "rctrl" or "rcontrol" => VIRTUAL_KEY.VK_RCONTROL,
            "lshift" => VIRTUAL_KEY.VK_LSHIFT,
            "rshift" => VIRTUAL_KEY.VK_RSHIFT,
            "lalt" => VIRTUAL_KEY.VK_LMENU,
            "ralt" => VIRTUAL_KEY.VK_RMENU,
            "lwin" => VIRTUAL_KEY.VK_LWIN,
            "rwin" => VIRTUAL_KEY.VK_RWIN,
            "backquote" => VIRTUAL_KEY.VK_OEM_3,
            "minus" => VIRTUAL_KEY.VK_OEM_MINUS,
            "equal" => VIRTUAL_KEY.VK_OEM_PLUS,
            "leftsquarebracket" => VIRTUAL_KEY.VK_OEM_4,
            "rightsquarebracket" => VIRTUAL_KEY.VK_OEM_6,
            "backslash" => VIRTUAL_KEY.VK_OEM_5,
            "semicolon" => VIRTUAL_KEY.VK_OEM_1,
            "quote" => VIRTUAL_KEY.VK_OEM_7,
            "comma" => VIRTUAL_KEY.VK_OEM_COMMA,
            "dot" => VIRTUAL_KEY.VK_OEM_PERIOD,
            "slash" => VIRTUAL_KEY.VK_OEM_2,
            _ => default,
        };
        return key != default;
    }

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
