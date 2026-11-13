using System.Windows.Input;

namespace FortniteHelper.Infrastructure;

public enum MouseCode
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    Mouse4 = 4,
    Mouse5 = 5,
    WheelUp = 6,
    WheelDown = 7,
}

/// <summary>A key or mouse button, optionally with modifiers. Code is a Win32 virtual-key code or a <see cref="MouseCode"/>.</summary>
public sealed record KeyBind(int Code, bool IsMouse = false, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public static KeyBind None { get; } = new(0);
    public static KeyBind Key(Key key, bool ctrl = false, bool shift = false, bool alt = false) =>
        new(KeyInterop.VirtualKeyFromKey(key), false, ctrl, shift, alt);
    public static KeyBind Mouse(MouseCode b) => new((int)b, true);

    public bool IsEmpty => Code == 0;

    /// <summary>True when the pressed input is this binding (extra modifiers are ignored for plain bindings).</summary>
    public bool Matches(int code, bool isMouse, bool ctrl, bool shift, bool alt)
    {
        if (IsEmpty || code != Code || isMouse != IsMouse) return false;
        if (!Ctrl && !Shift && !Alt) return true;
        return Ctrl == ctrl && Shift == shift && Alt == alt;
    }

    public string Display
    {
        get
        {
            if (IsEmpty) return "Not set";
            var name = IsMouse ? MouseName((MouseCode)Code) : KeyName(Code);
            var mods = (Ctrl ? "Ctrl + " : "") + (Shift ? "Shift + " : "") + (Alt ? "Alt + " : "");
            return mods + name;
        }
    }

    public override string ToString() => Display;

    static string MouseName(MouseCode c) => c switch
    {
        MouseCode.Left => "Left Click",
        MouseCode.Right => "Right Click",
        MouseCode.Middle => "Middle Click",
        MouseCode.Mouse4 => "Mouse 4",
        MouseCode.Mouse5 => "Mouse 5",
        MouseCode.WheelUp => "Wheel Up",
        MouseCode.WheelDown => "Wheel Down",
        _ => "Mouse",
    };

    public static string KeyName(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num " + (key - System.Windows.Input.Key.NumPad0),
            System.Windows.Input.Key.Add => "Num +",
            System.Windows.Input.Key.Subtract => "Num -",
            System.Windows.Input.Key.Multiply => "Num *",
            System.Windows.Input.Key.Divide => "Num /",
            System.Windows.Input.Key.Decimal => "Num .",
            System.Windows.Input.Key.OemPlus => "=",
            System.Windows.Input.Key.OemMinus => "-",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod => ".",
            System.Windows.Input.Key.OemQuestion => "/",
            System.Windows.Input.Key.OemSemicolon => ";",
            System.Windows.Input.Key.OemQuotes => "'",
            System.Windows.Input.Key.OemOpenBrackets => "[",
            System.Windows.Input.Key.OemCloseBrackets => "]",
            System.Windows.Input.Key.OemPipe => "\\",
            System.Windows.Input.Key.OemTilde => "`",
            System.Windows.Input.Key.Return => "Enter",
            System.Windows.Input.Key.Capital => "Caps Lock",
            System.Windows.Input.Key.Next => "Page Down",
            System.Windows.Input.Key.Prior => "Page Up",
            System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift => "Shift",
            System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl => "Ctrl",
            System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt => "Alt",
            System.Windows.Input.Key.None => $"Key {vk}",
            _ => key.ToString(),
        };
    }
}
