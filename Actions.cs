using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Remake;

    [Flags]
    internal enum SendModifiers : byte
    {
        None = 0,
        Control = 1,
        Shift = 2,
        Alt = 4,
        Windows = 8,
    }

    internal readonly record struct SendSegment(
        string? Text,
        VIRTUAL_KEY Key,
        byte Repeat,
        SendModifiers Modifiers,
        SendModifiers AlreadyDown,
        bool IsKeyStateEvent = false,
        bool KeyUp = false)
    {
        public static SendSegment Literal(string text) => new(text, default, 1, SendModifiers.None, SendModifiers.None);

        public static SendSegment Stroke(VIRTUAL_KEY key, byte repeat, SendModifiers modifiers, SendModifiers alreadyDown) =>
            new(null, key, repeat, modifiers, alreadyDown);

        public static SendSegment KeyState(VIRTUAL_KEY key, bool keyUp) =>
            new(null, key, 1, SendModifiers.None, SendModifiers.None, true, keyUp);
    }

/// <summary>把安全白名单内的 keyFunc 名称解析为强类型动作，不执行配置中的任意代码。</summary>
internal static class Actions
{
    public static bool TryParseFunctionSpec(string functionSpec, out KeyBinding binding, out string error)
    {
        binding = default;
        if (!TryParseCall(functionSpec, out string name, out List<string> arguments, out error))
            return false;

        name = name.ToLowerInvariant();
        if (name == "keyfunc_send")
        {
            if (arguments.Count != 1)
            {
                error = "keyFunc_send 需要一个文本参数";
                return false;
            }

            binding = new KeyBinding(KeyboardAction.SendText, Argument: arguments[0]);
            error = string.Empty;
            return true;
        }

        if (name == "keyfunc_run")
        {
            if (arguments.Count != 1 || string.IsNullOrWhiteSpace(arguments[0]))
            {
                error = "keyFunc_run 需要一个非空目标参数";
                return false;
            }

            binding = new KeyBinding(KeyboardAction.RunTarget, Argument: arguments[0]);
            error = string.Empty;
            return true;
        }

        if (!TryMapKeyboardAction(name, out KeyboardAction action, out bool acceptsRepeat))
        {
            error = "该函数不在 remake 当前迁移范围内";
            return false;
        }

        byte repeat = 1;
        if (acceptsRepeat)
        {
            if (arguments.Count > 1 || (arguments.Count == 1 &&
                !byte.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat)))
            {
                error = "重复次数必须是 0 到 255 之间的整数";
                return false;
            }
        }
        else if (arguments.Count != 0)
        {
            error = "该函数不接受参数";
            return false;
        }

        binding = new KeyBinding(action, repeat);
        error = string.Empty;
        return true;
    }

    public static void SendCommand(string command, bool usesLeftAlt, bool leftAltVisible) =>
        InputSender.SendSequence(ParseSendCommand(command), usesLeftAlt, leftAltVisible);

    public static void RunTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("keyFunc_run 目标不能为空。", nameof(target));

        (string fileName, string arguments) = SplitCommandLine(target.Trim());
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        using Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Windows 未能启动 keyFunc_run 指定的目标。");
    }
    private static (string FileName, string Arguments) SplitCommandLine(string command)
    {
        if (command[0] == '"')
        {
            var fileName = new StringBuilder();
            int index = 1;
            bool closed = false;
            while (index < command.Length)
            {
                char value = command[index++];
                if (value == '\\' && index < command.Length && command[index] == '"')
                {
                    fileName.Append('"');
                    index++;
                }
                else if (value == '"')
                {
                    closed = true;
                    break;
                }
                else
                    fileName.Append(value);
            }

            if (!closed || fileName.Length == 0)
                throw new FormatException("keyFunc_run 的带引号路径格式不正确。");
            return (fileName.ToString(), command[index..].TrimStart());
        }

        int separator = command.IndexOfAny([' ', '\t']);
        if (separator < 0)
            return (command, string.Empty);
        return (command[..separator], command[(separator + 1)..].TrimStart());
    }

    private static bool TryMapKeyboardAction(string name, out KeyboardAction action, out bool acceptsRepeat)
    {
        (action, acceptsRepeat) = name switch
        {
            "keyfunc_donothing" => (KeyboardAction.Consume, false),
            "keyfunc_togglecapslock" => (KeyboardAction.ToggleCapsLock, false),
            "keyfunc_reload" => (KeyboardAction.ReloadWorker, false),
            "keyfunc_moveleft" => (KeyboardAction.MoveLeft, true),
            "keyfunc_moveright" => (KeyboardAction.MoveRight, true),
            "keyfunc_moveup" => (KeyboardAction.MoveUp, true),
            "keyfunc_movedown" => (KeyboardAction.MoveDown, true),
            "keyfunc_movewordleft" => (KeyboardAction.MoveWordLeft, true),
            "keyfunc_movewordright" => (KeyboardAction.MoveWordRight, true),
            "keyfunc_backspace" => (KeyboardAction.Backspace, false),
            "keyfunc_delete" => (KeyboardAction.Delete, false),
            "keyfunc_deleteall" => (KeyboardAction.DeleteAll, false),
            "keyfunc_deleteword" => (KeyboardAction.DeleteWord, false),
            "keyfunc_forwarddeleteword" => (KeyboardAction.ForwardDeleteWord, false),
            "keyfunc_end" => (KeyboardAction.End, false),
            "keyfunc_home" => (KeyboardAction.Home, false),
            "keyfunc_movetopagebeginning" => (KeyboardAction.MovePageBeginning, false),
            "keyfunc_movetopageend" => (KeyboardAction.MovePageEnd, false),
            "keyfunc_deleteline" => (KeyboardAction.DeleteLine, false),
            "keyfunc_deletetolinebeginning" => (KeyboardAction.DeleteToLineBeginning, false),
            "keyfunc_deletetolineend" => (KeyboardAction.DeleteToLineEnd, false),
            "keyfunc_deletetopagebeginning" => (KeyboardAction.DeleteToPageBeginning, false),
            "keyfunc_deletetopageend" => (KeyboardAction.DeleteToPageEnd, false),
            "keyfunc_enterwherever" => (KeyboardAction.EnterWherever, false),
            "keyfunc_esc" => (KeyboardAction.Escape, false),
            "keyfunc_enter" => (KeyboardAction.Enter, false),
            "keyfunc_selectleft" => (KeyboardAction.SelectLeft, true),
            "keyfunc_selectright" => (KeyboardAction.SelectRight, true),
            "keyfunc_selectup" => (KeyboardAction.SelectUp, true),
            "keyfunc_selectdown" => (KeyboardAction.SelectDown, true),
            "keyfunc_selectwordleft" => (KeyboardAction.SelectWordLeft, true),
            "keyfunc_selectwordright" => (KeyboardAction.SelectWordRight, true),
            "keyfunc_selecthome" => (KeyboardAction.SelectHome, false),
            "keyfunc_selectend" => (KeyboardAction.SelectEnd, false),
            "keyfunc_selecttopagebeginning" => (KeyboardAction.SelectPageBeginning, false),
            "keyfunc_selecttopageend" => (KeyboardAction.SelectPageEnd, false),
            "keyfunc_selectcurrentword" => (KeyboardAction.SelectCurrentWord, false),
            "keyfunc_selectcurrentline" => (KeyboardAction.SelectCurrentLine, false),
            _ => (default, false),
        };

        return name is
            "keyfunc_donothing" or "keyfunc_togglecapslock" or "keyfunc_reload" or
            "keyfunc_moveleft" or "keyfunc_moveright" or "keyfunc_moveup" or "keyfunc_movedown" or
            "keyfunc_movewordleft" or "keyfunc_movewordright" or "keyfunc_backspace" or "keyfunc_delete" or
            "keyfunc_deleteall" or "keyfunc_deleteword" or "keyfunc_forwarddeleteword" or "keyfunc_end" or
            "keyfunc_home" or "keyfunc_movetopagebeginning" or "keyfunc_movetopageend" or "keyfunc_deleteline" or
            "keyfunc_deletetolinebeginning" or "keyfunc_deletetolineend" or "keyfunc_deletetopagebeginning" or
            "keyfunc_deletetopageend" or "keyfunc_enterwherever" or "keyfunc_esc" or "keyfunc_enter" or
            "keyfunc_selectleft" or "keyfunc_selectright" or "keyfunc_selectup" or "keyfunc_selectdown" or
            "keyfunc_selectwordleft" or "keyfunc_selectwordright" or "keyfunc_selecthome" or "keyfunc_selectend" or
            "keyfunc_selecttopagebeginning" or "keyfunc_selecttopageend" or "keyfunc_selectcurrentword" or
            "keyfunc_selectcurrentline";
    }

    private static bool TryParseCall(string spec, out string functionName, out List<string> arguments, out string error)
    {
        functionName = string.Empty;
        arguments = [];
        string text = spec.Trim();
        int open = text.IndexOf('(');
        string parameterText;
        if (open < 0)
        {
            functionName = text;
            parameterText = string.Empty;
        }
        else
        {
            if (text.Length < open + 2 || text[^1] != ')')
            {
                error = "函数参数缺少右括号";
                return false;
            }

            functionName = text[..open].Trim();
            parameterText = text[(open + 1)..^1];
        }

        if (!IsFunctionName(functionName) || !functionName.StartsWith("keyFunc_", StringComparison.OrdinalIgnoreCase))
        {
            error = "只接受 keyFunc_ 开头的函数名";
            return false;
        }

        if (parameterText.Length != 0 && !TrySplitArguments(parameterText, out arguments))
        {
            error = "参数引号没有配对";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFunctionName(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]) && value[i] != '_')
                return false;
        }
        return true;
    }

    private static bool TrySplitArguments(string text, out List<string> arguments)
    {
        arguments = [];
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (quote != '\0')
            {
                // 普通反斜杠是 Windows 路径分隔符；只折叠转义引号与双反斜杠。
                if (value == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\'))
                    current.Append(text[++i]);
                else if (value == quote)
                    quote = '\0';
                else
                    current.Append(value);
            }
            else if (value is '"' or '\'')
                quote = value;
            else if (value == ',')
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
            }
            else
                current.Append(value);
        }

        if (quote != '\0')
            return false;
        arguments.Add(current.ToString().Trim());
        return true;
    }

    private static bool TryParseRepeat(List<string> arguments, out byte repeat, out string error)
    {
        repeat = 1;
        if (arguments.Count == 0)
        {
            error = string.Empty;
            return true;
        }
        if (arguments.Count == 1 && byte.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat))
        {
            error = string.Empty;
            return true;
        }
        error = "重复次数必须是 0 到 255 之间的整数";
        return false;
    }

    private static List<SendSegment> ParseSendCommand(string value)
    {
        var segments = new List<SendSegment>();
        var text = new StringBuilder();
        SendModifiers pending = SendModifiers.None;
        SendModifiers held = SendModifiers.None;
        bool raw = false;

        void FlushText()
        {
            if (text.Length == 0)
                return;
            segments.Add(SendSegment.Literal(text.ToString()));
            text.Clear();
        }

        void AddModifiedCharacter(char character)
        {
            if (!InputSender.TryMapCharacterToKey(character, out VIRTUAL_KEY key, out bool keyControl, out bool keyShift, out bool keyAlt))
                throw new FormatException($"目标键盘布局无法发送字符 U+{(int)character:X4} 的修饰组合。");
            SendModifiers characterModifiers = (keyControl ? SendModifiers.Control : SendModifiers.None) |
                                               (keyShift ? SendModifiers.Shift : SendModifiers.None) |
                                               (keyAlt ? SendModifiers.Alt : SendModifiers.None);
            segments.Add(SendSegment.Stroke(key, 1, pending | held | characterModifiers, held));
            pending = SendModifiers.None;
        }

        for (int index = 0; index < value.Length;)
        {
            char current = value[index];
            if (raw)
            {
                text.Append(current);
                index++;
                continue;
            }

            if (current is '^' or '+' or '!' or '#')
            {
                FlushText();
                pending |= current switch
                {
                    '^' => SendModifiers.Control,
                    '+' => SendModifiers.Shift,
                    '!' => SendModifiers.Alt,
                    _ => SendModifiers.Windows,
                };
                index++;
                continue;
            }

            if (current == '{')
            {
                if (value.AsSpan(index).StartsWith("{{}", StringComparison.Ordinal))
                {
                    if (pending != SendModifiers.None || held != SendModifiers.None)
                        AddModifiedCharacter('{');
                    else
                        text.Append('{');
                    index += 3;
                    continue;
                }
                if (value.AsSpan(index).StartsWith("{}}", StringComparison.Ordinal))
                {
                    if (pending != SendModifiers.None || held != SendModifiers.None)
                        AddModifiedCharacter('}');
                    else
                        text.Append('}');
                    index += 3;
                    continue;
                }

                int close = value.IndexOf('}', index + 1);
                if (close < 0)
                    throw new FormatException("Send 表达式中的按键缺少右花括号。");
                string token = value[(index + 1)..close].Trim();
                index = close + 1;
                if (token.Equals("raw", StringComparison.OrdinalIgnoreCase) || token.Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    if (pending != SendModifiers.None)
                        throw new FormatException("{Raw}/{Text} 不能跟在修饰键前缀之后。");
                    FlushText();
                    raw = true;
                    continue;
                }
                if (!TryParseSendKeyToken(token, out VIRTUAL_KEY keyCode, out byte repeat, out bool isKeyState, out bool keyUp))
                {
                    if (token.Length == 1)
                    {
                        if (pending != SendModifiers.None || held != SendModifiers.None)
                            AddModifiedCharacter(token[0]);
                        else
                            text.Append(token[0]);
                        continue;
                    }
                    throw new FormatException($"不支持的 Send 按键标记：{{{token}}}。");
                }

                FlushText();
                if (isKeyState)
                {
                    if (pending != SendModifiers.None)
                        throw new FormatException("按键 down/up 标记前不能有修饰键前缀。");
                    segments.Add(SendSegment.KeyState(keyCode, keyUp));
                    SendModifiers modifier = ModifierFor(keyCode);
                    if (keyUp)
                        held &= ~modifier;
                    else
                        held |= modifier;
                }
                else
                {
                    segments.Add(SendSegment.Stroke(keyCode, repeat, pending | held, held));
                    pending = SendModifiers.None;
                }
                continue;
            }

            if (pending != SendModifiers.None || held != SendModifiers.None)
            {
                FlushText();
                AddModifiedCharacter(current);
                index++;
                continue;
            }

            text.Append(current);
            index++;
        }

        if (pending != SendModifiers.None)
            throw new FormatException("Send 表达式末尾的修饰键前缀没有对应按键。");
        FlushText();
        return segments;
    }

    private static SendModifiers ModifierFor(VIRTUAL_KEY key) => key switch
    {
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL => SendModifiers.Control,
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT => SendModifiers.Shift,
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU => SendModifiers.Alt,
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN => SendModifiers.Windows,
        _ => SendModifiers.None,
    };

    private static bool TryParseSendKeyToken(string token, out VIRTUAL_KEY key, out byte repeat, out bool isKeyState, out bool keyUp)
    {
        key = default;
        repeat = 1;
        isKeyState = false;
        keyUp = false;
        string[] parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        string keyName = parts[0].ToLowerInvariant();
        if (parts.Length == 2 && (parts[1].Equals("down", StringComparison.OrdinalIgnoreCase) ||
                                  parts[1].Equals("up", StringComparison.OrdinalIgnoreCase)))
        {
            if (!DefaultKeyBindings.TryParseKeyName(keyName, out key))
                return false;
            isKeyState = true;
            keyUp = parts[1].Equals("up", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        if (parts.Length > 2 || (parts.Length == 2 &&
            !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat)))
            return false;
        return DefaultKeyBindings.TryParseKeyName(keyName, out key);
    }

}
