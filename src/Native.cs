// All Win32 interop in one place. Only what the program actually uses.
using System.Runtime.InteropServices;
using System.Text;

namespace Moa;

[StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] internal struct SIZE { public int Cx, Cy; }
[StructLayout(LayoutKind.Sequential)] internal struct FILETIME { public uint Low, High; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam;
    public uint Time; public int X, Y; public uint LPrivate;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public uint CbSize, Style;
    public IntPtr LpfnWndProc;
    public int CbClsExtra, CbWndExtra;
    public IntPtr HInstance, HIcon, HCursor, HbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string? LpszClassName;
    public IntPtr HIconSm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATAW
{
    public uint CbSize;
    public IntPtr Hwnd;
    public uint UID, UFlags, UCallbackMessage;
    public IntPtr HIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string SzTip;
    public uint DwState, DwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SzInfo;
    public uint UTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string SzInfoTitle;
    public uint DwInfoFlags;
    public Guid GuidItem;
    public IntPtr HBalloonIcon;
}

internal static class Native
{
    public const uint GW_OWNER = 4;
    /// <summary>The window below this one in Z-order: what shows through when this one is made
    /// transparent rather than hidden.</summary>
    public const uint GW_HWNDNEXT = 2;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_CAPTION = 0x00C00000;
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public const uint WS_POPUP = 0x80000000;

    public const uint LWA_ALPHA = 0x00000002;

    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;

    /// <summary>
    /// IsIconic, under a name of our own. It catches a window that is minimized at the moment it is
    /// hidden, and misses one that is minimized and then hidden, because a window that has been hidden
    /// is not minimized any more - which is why GetWindowPlacement is consulted as well.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    public static extern bool WindowIsMinimized(IntPtr hwnd);
    public const int OBJID_WINDOW = 0;

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_TRAYICON = 0x0400 + 1;
    /// <summary>Posted by the animation thread to ask the message loop for one animation frame.</summary>
    public const uint WM_APP_TICK = 0x8000 + 1;

    public const uint PM_REMOVE = 0x0001;

    public const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    public const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_UNCHECKED = 0x0,
                       MF_SEPARATOR = 0x800, MF_POPUP = 0x10, MF_GRAYED = 0x1;

    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    public const uint ULW_ALPHA = 0x2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd,
                                          int idObject, int idChild, uint thread, uint time);
    public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max,
        IntPtr hmod, WinEventDelegate cb, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool ShowWindow(IntPtr h, int cmd);
    public const int SW_SHOWNOACTIVATE = 4;
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    public const int SW_SHOWMINIMIZED = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT minPosition;
        public POINT maxPosition;
        public RECT normalPosition;
    }

    /// <summary>
    /// How a window was last told to show itself. It separates minimizing to the tray from closing to
    /// it only when the application actually minimizes: one that hides the window without minimizing
    /// leaves the placement reading normal, and was measured doing exactly that, so in that case the
    /// two are indistinguishable from out here.
    /// </summary>
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    /// <summary>
    /// Blocks until the compositor presents its next frame, which is how the animation follows the
    /// display. It BLOCKS, so it must not be called on the message loop - see AnimationThread.cs,
    /// which is where the waiting happens. Returns non-zero when there is no compositor to wait for;
    /// and on some systems it returns at once even when there is one, which is why the animation
    /// thread also knows the display's frame length and paces itself with a sleep to match.
    /// </summary>
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    /// <summary>DWMWA_CLOAKED is 14; see Animator.DwmCloaked for what it is used for.</summary>
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);

    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);

    /// <summary>The GetDeviceCaps index that reports a display's refresh rate in hertz.</summary>
    public const int VREFRESH = 116;

    /// <summary>
    /// Raises the system timer resolution. Without it Windows only guarantees about 15.6 ms of sleep,
    /// which is why a sleep meant to pace the animation to a 165 Hz display measured about 60 frames a
    /// second instead: the sleep was long enough to lose most of the frames the display could show.
    /// Held only while something is animating, since a finer timer costs power.
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] public static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] public static extern uint TimeEndPeriod(uint ms);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dcDst, ref POINT dst,
        ref SIZE size, IntPtr dcSrc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr cb);

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, IntPtr text);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    /// <summary>
    /// The display's shape: how many monitors there are, and the rectangle that covers all of them.
    /// Any resolution change, monitor added, removed or rearranged moves the virtual screen, so
    /// comparing these five numbers is how a remembered screen position is told apart from one that
    /// the display has moved out from under. See Animator's Origin.
    ///
    /// A scaling change on its own does not move them - they are physical pixels - which is why the
    /// DPI the window had is compared as well, and unreadable (0) is treated as "cannot say" rather
    /// than as a change.
    /// </summary>
    public const int SM_CMONITORS = 80, SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                     SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, uint attr, out RECT value, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    /// <summary>
    /// Extracts icons of a requested size, returning the closest size the file actually contains.
    ///
    /// ExtractIconEx, which this used before, only ever hands back the two legacy sizes - 32 by 32 and
    /// 16 by 16 - no matter what the file holds. That is what made icons look soft: a 32 by 32 source
    /// drawn at 128 pixels is a four-fold enlargement, and no filter makes that sharp. Asking for 256
    /// returns whatever is really in there, which for anything written this century is 256 by 256, and
    /// drawing that down to 128 is a reduction instead - which looks sharp without any cleverness.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy,
                                                   [Out] IntPtr[] icons, [Out] uint[] ids,
                                                   uint count, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    // CharSet.Unicode is not optional here. DllImport marshals strings as ANSI by default, and
    // the W suffix on the entry point name does NOT turn that on: without this, ANSI bytes are
    // handed to the wide-character function and every dialog comes out garbled - in English as
    // well as Chinese, which is what gave it away.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);

    /// <summary>
    /// A message number the system picks at run time for a name both sides agree on. Returns 0 when it
    /// fails, which is worth checking rather than assuming: the id is compared against every message the
    /// tray window receives, and 0 would then match every message whose id happens to be 0.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    /// <summary>
    /// GDI and USER object counts for a process. flags: 0 = GDI objects, 1 = USER objects.
    ///
    /// Worth having because a leak of these does not look like a leak: the memory counter moves by
    /// a tenth of a megabyte, while the process is quietly approaching the 10,000 object limit, and
    /// the program dies days later with no obvious cause.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll")] public static extern int MulDiv(int number, int numerator, int denominator);

    /// <summary>
    /// Auto-detected, never a setting: Windows 11 rounds the corners of top-level windows
    /// (8 px at 96 DPI, which is the value the Windhawk mod hardcodes), Windows 10 does not.
    /// </summary>
    public static readonly bool IsWindows11 = Compat.IsWindows11;
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint BiSize; public int BiWidth, BiHeight;
        public ushort BiPlanes, BiBitCount; public uint BiCompression, BiSizeImage;
        public int BiXPelsPerMeter, BiYPelsPerMeter; public uint BiClrUsed, BiClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Colors; }

    public const string SelfClassName = "MoaTrayHostClass";

    // ---- reading a process's image path cheaply -------------------------------------------
    //
    // Process.GetProcessById(pid).MainModule does the same job, but it opens the target
    // process and enumerates its modules: milliseconds, on a process that is still starting
    // up. QueryFullProcessImageNameW is one call, and the window-created callback has only a
    // few milliseconds in total before the application shows the window.
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
                                                         StringBuilder name, ref int size);

    // How old a process is, which is the one fact that tells a click that launched a program from
    // a click that merely happened nearby: a process that did not exist when the click was made was
    // started by it. PROCESS_QUERY_LIMITED_INFORMATION is enough for this, the same access right
    // the image path above needs.
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr process, out FILETIME creation,
                                              out FILETIME exit, out FILETIME kernel,
                                              out FILETIME user);

    // ---- low-level mouse hook, used only to learn where the user last clicked -------------
    public const int WH_MOUSE_LL = 14;
    public const uint WM_LBUTTONDOWN = 0x0201;

    /// <summary>What the mouse hook's point is asked about; see ClickTracker.ClassifyClick.</summary>
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT Pt; public uint MouseData, Flags, Time; public IntPtr ExtraInfo;
    }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    // ---- low-level keyboard hook, used only to learn where the user pressed Enter -------------
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    /// <summary>
    /// The Enter key, main and numeric keypad both: the keypad's arrives as this virtual key with the
    /// extended flag set, so the one value is enough. See KeyTracker.Callback.
    /// </summary>
    public const uint VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo;
    }

    /// <summary>
    /// The foreground thread's own state, of which only the focused control is used. It is worth more than
    /// the foreground window for this purpose - see KeyTracker for why - and cbSize must be set before the
    /// call, which is why the field is named as the API names it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint CbSize, Flags;
        public IntPtr HwndActive, HwndFocus, HwndCapture, HwndMenuOwner, HwndMoveSize, HwndCaret;
        public RECT RcCaret;
    }

    /// <summary>A thread id of 0 means the foreground thread, which is the one being typed into.</summary>
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    /// <summary>The display's mode changing: resolution, colour depth, or a monitor arriving or leaving.</summary>
    public const uint WM_DISPLAYCHANGE = 0x007E;

    /// <summary>
    /// Sent to every top-level window when something about the system's settings changes; the lParam names
    /// what changed. Only two of those names are this program's business, and Program.cs says which and why.
    /// </summary>
    public const uint WM_SETTINGCHANGE = 0x001A;

    /// <summary>The shell's own window - the desktop, in practice - and through it the shell's process.</summary>
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    // ---- the system's own animation-effects switch -----------------------------------------
    //
    // Settings > Accessibility > Visual effects > Animation effects (Windows 10: Ease of Access >
    // Display > Show animations in Windows). Whoever turned it off did so because motion is a
    // problem for them, and this program is the largest single piece of motion on the screen, so it
    // obeys the switch: no opening animation, no closing animation, and the two animation toggles in
    // the tray menu go grey instead of lying about what they control.
    //
    // Read on demand - once per window event, once per menu build - rather than cached and
    // invalidated from WM_SETTINGCHANGE. A cache is one more thing that can be wrong: the broadcast
    // is documented only as "a system-wide setting changed" and a missed one would leave the switch
    // apparently ignored until the program was restarted. The call costs about 4 microseconds, next
    // to the eight other user32 calls Inspect already makes before the window is hidden.
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);

    /// <summary>
    /// Whether Windows' animation effects are on. A call that fails counts as on: the system refusing
    /// to answer is not the user asking for less motion, and the worse failure by far would be a
    /// program that animates nothing because a query it did not need to make went wrong.
    /// </summary>
    public static bool SystemAnimationsEnabled()
    {
        if (!SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, out int enabled, 0)) return true;
        return enabled != 0;
    }

}
