using System.Runtime.InteropServices;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

public readonly record struct InputEvent(int Code, bool IsMouse, bool Down, bool Ctrl, bool Shift, bool Alt);

/// <summary>
/// Global keyboard / mouse button listener using Raw Input on its own thread.
/// Unlike low-level hooks, Raw Input never sits in the input path, so it adds zero latency to the game,
/// and it never blocks or changes any input.
/// </summary>
public sealed class RawInputService : IDisposable
{
    const int WM_INPUT = 0x00FF, WM_CLOSE = 0x0010, WM_USER = 0x0400;
    const int WM_APP_MOUSE = WM_USER + 1;
    const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    const uint RID_INPUT = 0x10000003;
    const int RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    const ushort RI_KEY_BREAK = 1;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly Thread _thread;
    readonly HashSet<int> _down = [];
    readonly ManualResetEventSlim _ready = new();
    IntPtr _hwnd;
    WndProc? _proc;
    bool _mouseRegistered;
    volatile bool _wantMouse;

    /// <summary>Raised on the input thread. Handlers must be quick.</summary>
    public event Action<InputEvent>? Input;

    public bool RightButtonHeld { get; private set; }

    public RawInputService(bool start = true)
    {
        if (!start) { _thread = new Thread(() => { }); return; }
        _thread = new Thread(Run) { IsBackground = true, Name = "RawInput" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    /// <summary>Mouse buttons are only listened to when something needs them (keeps the thread idle otherwise).</summary>
    public void SetMouseNeeded(bool needed)
    {
        _wantMouse = needed;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_MOUSE, IntPtr.Zero, IntPtr.Zero);
    }

    void Run()
    {
        try
        {
            _proc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = "FortniteHelperRawInput",
            };
            RegisterClassEx(ref wc);
            _hwnd = CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            Register(keyboard: true, mouse: _wantMouse);
        }
        catch (Exception ex)
        {
            Log.Error("Raw input init failed", ex);
        }
        finally
        {
            _ready.Set();
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    void Register(bool keyboard, bool mouse)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
        if (keyboard)
            RegisterRawInputDevices([new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }], 1, size);
        if (mouse != _mouseRegistered)
        {
            var dev = mouse
                ? new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }
                : new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero };
            RegisterRawInputDevices([dev], 1, size);
            _mouseRegistered = mouse;
        }
    }

    IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleInput(lParam); } catch { /* never let input handling kill the thread */ }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        if (msg == WM_APP_MOUSE)
        {
            if (_wantMouse != _mouseRegistered) Register(keyboard: false, mouse: _wantMouse);
            return IntPtr.Zero;
        }
        if (msg == WM_CLOSE)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    unsafe void HandleInput(IntPtr lParam)
    {
        uint size = 0;
        uint header = (uint)sizeof(RAWINPUTHEADER);
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0 || size > 1024) return;
        byte* buffer = stackalloc byte[(int)size];
        if (GetRawInputData(lParam, RID_INPUT, (IntPtr)buffer, ref size, header) != size) return;

        var hdr = (RAWINPUTHEADER*)buffer;
        if (hdr->dwType == RIM_TYPEKEYBOARD)
        {
            var kb = (RAWKEYBOARD*)(buffer + header);
            int vk = kb->VKey;
            if (vk is 0 or 255) return;
            bool down = (kb->Flags & RI_KEY_BREAK) == 0;
            // Normalise left/right modifiers
            vk = vk switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => vk };
            if (down)
            {
                if (!_down.Add(vk)) return; // auto-repeat
            }
            else if (!_down.Remove(vk)) return;
            Raise(vk, false, down);
        }
        else if (hdr->dwType == RIM_TYPEMOUSE)
        {
            var m = (RAWMOUSE*)(buffer + header);
            ushort f = m->usButtonFlags;
            if (f == 0) return; // movement only
            if ((f & 0x0001) != 0) Raise((int)MouseCode.Left, true, true);
            if ((f & 0x0002) != 0) Raise((int)MouseCode.Left, true, false);
            if ((f & 0x0004) != 0) { RightButtonHeld = true; Raise((int)MouseCode.Right, true, true); }
            if ((f & 0x0008) != 0) { RightButtonHeld = false; Raise((int)MouseCode.Right, true, false); }
            if ((f & 0x0010) != 0) Raise((int)MouseCode.Middle, true, true);
            if ((f & 0x0020) != 0) Raise((int)MouseCode.Middle, true, false);
            if ((f & 0x0040) != 0) Raise((int)MouseCode.Mouse4, true, true);
            if ((f & 0x0080) != 0) Raise((int)MouseCode.Mouse4, true, false);
            if ((f & 0x0100) != 0) Raise((int)MouseCode.Mouse5, true, true);
            if ((f & 0x0200) != 0) Raise((int)MouseCode.Mouse5, true, false);
            if ((f & 0x0400) != 0)
            {
                short delta = (short)m->usButtonData;
                var code = delta > 0 ? MouseCode.WheelUp : MouseCode.WheelDown;
                Raise((int)code, true, true);
                Raise((int)code, true, false);
            }
        }
    }

    void Raise(int code, bool isMouse, bool down)
    {
        Input?.Invoke(new InputEvent(code, isMouse, down, _down.Contains(0x11), _down.Contains(0x10), _down.Contains(0x12)));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- interop ----
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }

    [StructLayout(LayoutKind.Explicit)]
    struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
