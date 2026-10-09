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

    // ---- the blacklist panel: standard controls, so nothing there is drawn by us -------------
    public const uint WM_SETFONT = 0x0030, WM_QUIT = 0x0012;
    public const uint LB_ADDSTRING = 0x0180, LB_RESETCONTENT = 0x0184, LB_SETCURSEL = 0x0186,
                      LB_GETCURSEL = 0x0188, LB_GETCOUNT = 0x018B, LB_GETTOPINDEX = 0x018E,
                      LB_SETTOPINDEX = 0x0197, LB_SETHORIZONTALEXTENT = 0x0194;

    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_VSCROLL = 0x00200000,
                      WS_HSCROLL = 0x00100000, WS_BORDER = 0x00800000, WS_TABSTOP = 0x00010000,
                      WS_SYSMENU = 0x00080000,
                      LBS_NOTIFY = 0x00000001, LBS_NOINTEGRALHEIGHT = 0x00000100;

    public const int SW_SHOW = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>The same entry point with a string, which is what LB_ADDSTRING takes.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessageTextW(IntPtr h, uint msg, IntPtr w, string l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu,
                                                                           uint exStyle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);

    /// <summary>
    /// A window's monitor and the usable area of it. The panel is a fixed-size window, so it has to size
    /// itself to the display it lands on rather than to the one it was designed on.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint CbSize;
        public RECT RcMonitor, RcWork;
        public uint DwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>Sent when the window moves to a monitor with a different scale; the suggested rect is in lParam.</summary>
    public const uint WM_DPICHANGED = 0x02E0;

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);

    /// <summary>
    /// A font for those controls, at the window's own DPI. The stock GUI font would instead be the size 96 DPI
    /// asked for, which is visibly too small on a scaled display - and being per monitor DPI aware, this
    /// program knows better.
    /// </summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    public const int COLOR_WINDOW = 5;
    public static readonly IntPtr IDC_ARROW = (IntPtr)32512;

    /// <summary>A static asks its parent for the colours it should paint its text and background in.</summary>
    public const uint WM_CTLCOLORSTATIC = 0x0138;

    // ---- what the standard controls need from us: nothing of theirs is drawn here ----------------------
    [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr h, bool enable);
    /// <summary>Asking for a repaint is not drawing it: the window and its controls paint themselves.</summary>
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);

    public const uint WM_GETFONT = 0x0031;

    /// <summary>
    /// How wide a string is in a device context, which is a measurement and not a drawing: GDI returns a size
    /// and paints nothing. Used to tell a list box how far its contents reach, because it will not work that
    /// out for itself - see BlacklistPanel.Widen.
    /// </summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int count, out SIZE size);

    // ---- the About box: comctl32's task dialog, whose frame, icon and links are all the system's ------------
    //
    // A task dialog rather than the message box this used to be, for the one thing a message box cannot do: a
    // link in the text that can be followed. TASKDIALOGCONFIG is declared Pack = 1 and that is not a guess:
    // commctrl.h wraps it in pshpack1.h, and the size the packing produces is the one TaskDialogIndirect
    // checks cbSize against. probe/aboutdialog measures it against the real call.

    /// <summary>
    /// The dialog's configuration. MainIcon and FooterIcon are unions in C - a handle with TDF_USE_HICON_*, a
    /// MAKEINTRESOURCE pointer otherwise - which is why they are IntPtr here and not string.
    ///
    /// The button members are still here because the structure's layout is the ABI and cannot be shortened;
    /// they are left zero, which is what "no custom buttons" is expressed as. TASKDIALOG_BUTTON is not declared
    /// at all any more - there is nothing to point at.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public struct TASKDIALOGCONFIG
    {
        public uint CbSize;
        public IntPtr HwndParent, HInstance;
        public uint DwFlags, DwCommonButtons;
        public string? PszWindowTitle;
        public IntPtr MainIcon;
        public string? PszMainInstruction, PszContent;
        public uint CButtons;
        public IntPtr PButtons;
        public int NDefaultButton;
        public uint CRadioButtons;
        public IntPtr PRadioButtons;
        public int NDefaultRadioButton;
        public string? PszVerificationText, PszExpandedInformation, PszExpandedControlText,
                       PszCollapsedControlText;
        public IntPtr FooterIcon;
        public string? PszFooter;
        public TaskDialogCallback? PfCallback;
        public IntPtr LpCallbackData;
        public uint CxWidth;
    }

    /// <summary>
    /// The one flag that closes a dialog with no button in it: Alt-F4, Esc and the title bar's X all work with
    /// it and none of them do without it, so it is not optional here.
    /// </summary>
    public const uint TDF_ENABLE_HYPERLINKS = 0x0001, TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;

    public const uint TDN_HYPERLINK_CLICKED = 3;

    /// <summary>
    /// TD_INFORMATION_ICON, and it is 0xFFFD rather than -3 on purpose: MAKEINTRESOURCEW truncates to a WORD
    /// and zero-extends, so the sign is gone by the time the system sees it. Passing (IntPtr)(-3) would be a
    /// pointer into the top of the address space and the dialog would come up with no icon.
    /// </summary>
    public static readonly IntPtr TD_INFORMATION_ICON = (IntPtr)0xFFFD;

    public delegate int TaskDialogCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
                                           IntPtr refData);

    /// <summary>
    /// Shows the dialog and returns when it is closed. A failed HRESULT means nothing was shown, which is
    /// worth knowing about: the caller logs it rather than leaving a menu entry that appears to do nothing.
    /// </summary>
    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    public static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button,
                                                IntPtr radioButton, IntPtr verification);

    /// <summary>The program's one way out to the browser, used by the About box's link.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string operation, string file,
                                              string? parameters, string? directory, int show);

    public const int SW_SHOWNORMAL = 1;

    /// <summary>
    /// The system's dark theme, reached - on Windows 10 - through entry points in uxtheme.dll that Microsoft
    /// never published. Ordinals rather than names for exactly that reason; every application that follows
    /// the theme calls the same ones.
    ///
    /// Nothing here draws anything. A control that has opted in is still painted top to bottom by the system,
    /// out of the system's own dark drawings; this only says which set to use.
    /// </summary>
    public static class DarkMode
    {
        /// <summary>
        /// False on a build without the dark common-control theme at all, which is anything before 1809.
        /// Ordinal 135 changed meaning in 1903 - from "is dark allowed" to "which app mode" - but a BOOL of
        /// TRUE and an app mode of 2 are the same word, so one signature serves both.
        /// </summary>
        private static readonly bool Supported = Environment.OSVersion.Version.Build >= 17763;

        /// <summary>Whether the system is asking applications to be dark, as of the last Apply.</summary>
        public static bool On { get; private set; }

        /// <summary>Created on first use and owned for the life of the process; a brush is not worth freeing at exit.</summary>
        private static IntPtr _darkBrush = IntPtr.Zero;

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
        private static extern void FlushMenuThemes();
        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? subIdList);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

        /// <summary>
        /// Reads what the user asked for and hands it to uxtheme. Called once at start-up, before the first
        /// window exists, and again when the theme is switched under a running program - uxtheme only takes
        /// the preference while nothing has been made with the old one, so the order matters at start-up and
        /// is why this is called from Run before the tray host is created.
        /// </summary>
        public static void Apply()
        {
            if (!Supported) return;
            On = SystemIsDark();
            try
            {
                // 2 forces dark and 0 hands the choice back to the system. Passing the answer rather than
                // "dark may be allowed" is what makes both states certain, and it is what FlushMenuThemes
                // then applies to menus that are already built.
                SetPreferredAppMode(On ? 2 : 0);
                FlushMenuThemes();
            }
            catch (Exception ex)
            {
                // A build without these ordinals, or a future one that moved them, keeps the light chrome it
                // would have had anyway. A line in the log, not an error box.
                Log.Write($"dark mode: uxtheme refused the opt-in ({ex.GetType().Name}); staying light");
                On = false;
            }
        }

        /// <summary>
        /// Whether the user has asked for the dark app theme. Read from the registry rather than from the
        /// undocumented ShouldAppsUseDarkMode, because this is the value the Settings page itself writes and
        /// this program is already reading a neighbouring key of the same hive for the card's colours.
        ///
        /// A missing or unreadable value counts as light, which is the state a program that says nothing about
        /// the theme gets anyway.
        /// </summary>
        private static bool SystemIsDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Puts one window onto the theme this process opted into. Every top-level window has to be told, and
        /// every control of it separately - and before it is first shown, which is why the callers do this in
        /// their create paths rather than after.
        /// </summary>
        public static void Style(IntPtr hwnd)
        {
            if (!Supported || hwnd == IntPtr.Zero) return;
            try
            {
                AllowDarkModeForWindow(hwnd, On);
                SetWindowTheme(hwnd, On ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        /// <summary>
        /// The title bar and frame, which DWM draws and uxtheme cannot reach. Attribute 20 is the one since
        /// 20H1 and 19 is the one before it; whichever the build knows is the one that answers 0.
        /// </summary>
        public static void Frame(IntPtr hwnd)
        {
            if (!Supported || hwnd == IntPtr.Zero) return;
            int on = On ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }

        /// <summary>
        /// The brush for the area of a window that no control covers, and for the background of a static. On
        /// the light theme both of those come from the system colours; on the dark theme there is no dark
        /// equivalent of COLOR_WINDOW to ask for, so the colour is named here - 32,32,32 is what the shell's
        /// own dark surfaces use.
        ///
        /// This is the one choice in the theme support that is ours rather than the system's, and it is still
        /// not drawing: a brush is handed back and the window or the control paints itself with it. Null on
        /// the light theme, where the caller leaves the system's own colour in place.
        /// </summary>
        public static IntPtr BackgroundBrush()
        {
            if (!On) return IntPtr.Zero;
            if (_darkBrush == IntPtr.Zero) _darkBrush = CreateSolidBrush(0x202020);
            return _darkBrush;
        }

        /// <summary>Windows' dark surface text, for the same reason BackgroundBrush exists.</summary>
        public const int DarkTextColour = 0xFFFFFF;
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(int colour);

    [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr dc, int colour);
    [DllImport("gdi32.dll")] public static extern int SetBkColor(IntPtr dc, int colour);

    /// <summary>
    /// GCLP_HBRBACKGROUND on a class that is already registered. Used to give a window a dark background brush
    /// without registering the class a second time; the brush still belongs to the system, which is what paints
    /// with it.
    ///
    /// Two entry points for one function, chosen at run time, and that is not belt-and-braces: 32-bit user32
    /// exports SetClassLongW and has no SetClassLongPtrW at all, while on 64-bit only the Ptr one can carry a
    /// handle. The C header hides this behind a macro; a DllImport cannot, so the test is here.
    ///
    /// The program normally takes the first arm - it builds as AnyCPU and so runs 64-bit on 64-bit Windows -
    /// but it is still AnyCPU rather than x64, so a 32-bit Windows runs this code down the other one, and a
    /// DllImport cannot be conditioned on anything at compile time.
    /// </summary>
    public static void SetClassBackground(IntPtr hwnd, IntPtr brush)
    {
        if (IntPtr.Size == 8) SetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND, brush);
        else SetClassLongW(hwnd, GCLP_HBRBACKGROUND, brush);
    }

    public const int GCLP_HBRBACKGROUND = -10;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern uint SetClassLongW(IntPtr hwnd, int index, IntPtr value);

    // ---- the two rectangle passes whose cost is the card's area, done at the C runtime's speed ----------
    // A loop of stores, one pixel at a time, measured an order of magnitude slower than the same work done by
    // memset, and these are the two passes that still scale with the card: clearing what is about to be drawn,
    // and writing the opaque background into it. Byte counts, as the C functions take them.
    [DllImport("msvcrt.dll", EntryPoint = "memset", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Memset(IntPtr dest, int value, IntPtr count);

    [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Memcpy(IntPtr dest, IntPtr src, IntPtr count);

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
