// All Win32 interop in one place. Only what the program actually uses.
using System.Drawing;
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

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_CAPTION = 0x00C00000;

    /// <summary>The resizing frame, the same bit WS_MAIN_FRAME counts among a real window's four. A window
    /// with it can be dragged by its edges and corners; without it, only moved.</summary>
    public const int WS_THICKFRAME = 0x00040000;

    /// <summary>
    /// The four frame bits that a real main window keeps even when it draws its own title bar, and
    /// which a menu, a tooltip, a dropdown or an invisible helper window never has.
    ///
    /// WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX, in one name because they are
    /// only ever asked about together: this is what tells a frameless Qt/Electron/WPF main window
    /// apart from the helper window its process creates alongside it. See Watcher.Inspect.
    /// </summary>
    public const int WS_MAIN_FRAME = 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
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

    /// <summary>The window has been resized, which is what a resizable window's layout hangs off.</summary>
    public const uint WM_SIZE = 0x0005;

    /// <summary>Asked, before a size change, for the smallest and largest the window may be.</summary>
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const int SIZE_MINIMIZED = 1;

    // ---- the stock controls the advertisement's rule window is made of -------------------------------------
    //
    // Its two fields are EDITs that are only allowed to hold a number, and the drop-down is a COMBOBOX. The
    // mask is not here: it is a function over text in AdRule, because this program has no WinForms and a
    // WinForms mask is the only place a mask is made of anything but text.

    /// <summary>A single-line edit that scrolls sideways rather than refusing a long line.</summary>
    public const uint ES_AUTOHSCROLL = 0x0080;

    /// <summary>Digits only, which is the whole of what the reset field's mask needs from the control.</summary>
    public const uint ES_NUMBER = 0x2000;

    /// <summary>The text is chosen from the list and cannot be typed in, which is what a unit is.</summary>
    public const uint CBS_DROPDOWNLIST = 0x0003;

    public const uint CB_ADDSTRING = 0x0143;
    public const uint CB_SETCURSEL = 0x014E;
    public const uint CB_GETCURSEL = 0x0147;

    /// <summary>Puts the selection - and so the caret - at a position in an edit. -1 is the end of the text.</summary>
    public const uint EM_SETSEL = 0x00B1;
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
    public const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0;
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    public const int SW_SHOWMINIMIZED = 2;

    /// <summary>Brings a minimized window back to the size and place it had, which is what a window that was
    /// minimized underneath the user needs rather than a plain show. See BlacklistPanel.Show.</summary>
    public const int SW_RESTORE = 9;

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
    /// <summary>GW_HWNDNEXT is 2; Animator walks the Z-order with it.</summary>
    public const uint GW_HWNDNEXT = 2;
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    /// <summary>The window at the top of the Z-order, which is where a walk down it starts. Asked with null for
    /// the whole desktop rather than for one window's children, which is the list a topmost panel lives in.</summary>
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);

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
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, IntPtr id);

    /// <summary>
    /// A timer's callback. With no window and an id of its own - SetTimer(NULL, ...) - it is called by
    /// whoever pumps this thread's queue, and never through DispatchMessage, which is what makes it the way
    /// back onto the message loop from somewhere that must not do the work itself. See AboutDialog.
    /// </summary>
    public delegate void TimerProc(IntPtr hwnd, uint msg, IntPtr id, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, TimerProc cb);

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

    /// <summary>
    /// How far the mouse may move while a button is held and still be a click rather than a drag. The system's
    /// own answer, which is also what it uses for the same question about its own windows - a drag that starts
    /// on a hand's tremor is a drag nobody asked for.
    /// </summary>
    public const int SM_CXDRAG = 68, SM_CYDRAG = 69;

    /// <summary>SM_CXSMICON: the size a small icon is on this display, which is the tray's own size.</summary>
    public const int SM_CXSMICON = 49;
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);

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
    /// <summary>Rounds a window off, which is how the advertisement's card wears the corner of the window it
    /// took over. The region is the system's once it is set, so it is not deleted here. See
    /// <see cref="AdsCard" />.</summary>
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b,
        int ellipseW, int ellipseH);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    /// <summary>
    /// A 1bpp bitmap with the caller's bits in it, which is what an icon's mask is. The array rather than a
    /// pointer because CreateBitmap with no bits at all leaves the contents undefined rather than blank, and an
    /// undefined mask is an icon that is transparent wherever the garbage bit happened to be. See AppIcon.
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[]? data);
    /// <summary>An icon from a colour bitmap and a mask; owned by the caller and freed with DestroyIcon.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool FIcon;
        public int XHotspot, YHotspot;
        public IntPtr HbmMask, HbmColor;
    }

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

    /// <summary>A screen point in a window's own coordinates; see AboutDialog's egg, which has a click and
    /// wants to know whether it was on the icon.</summary>
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);

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
    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOMOVE = 0x0002,
                      SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// Moves a window without carrying its old contents across. Without it Windows blits the pixels the window
    /// had into the shape it now has, which for a control that has just changed size leaves the old drawing
    /// lying under the new - the artifact that made the blacklist panel's labels and scrollbars show fragments
    /// of what used to be there while the window was being dragged. See BlacklistPanel.Layout.
    /// </summary>
    public const uint SWP_NOCOPYBITS = 0x0100;

    /// <summary>Keeps a child window from drawing over its siblings, which is what a control that has just been
    /// moved needs while the one next to it is repainting.</summary>
    public const uint WS_CLIPSIBLINGS = 0x04000000;

    /// <summary>
    /// Invalidates the whole client area when the window is resized, which is what a window full of controls
    /// needs so that the background under them is painted again rather than left as it was: a control that has
    /// moved leaves its old drawing behind otherwise, and nothing in it will repaint that. See
    /// BlacklistPanel.Register.
    /// </summary>
    public const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;

    /// <summary>Where SetWindowPos puts a window; -1 is "above everything", which an advertisement card is.</summary>
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_TOP = IntPtr.Zero;

    /// <summary>Sent when the window moves to a monitor with a different scale; the suggested rect is in lParam.</summary>
    public const uint WM_DPICHANGED = 0x02E0;

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    /// <summary>The display a window is on, for the About box's launch-ad code, which steps around it.</summary>
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
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

    // ---- what the blacklist panel's font measures, and what a window may be dragged to -------------------
    //
    // The font itself is not here. It comes from System.Drawing's own reading of the system metrics, which is a
    // struct this program keeps no copy of - see BlacklistPanel.UiFont for why. What is wanted here is the line
    // height measured off the font, which is a question for a device context.

    /// <summary>The metrics of the font a device context has selected; Height is the one wanted here.</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetTextMetricsW(IntPtr dc, out TEXTMETRICW tm);

    // CharSet is spelled out in full, and it is not a style choice: this struct has a field called CharSet of
    // its own, and inside the declaration it hides the name of the enum the attribute wants.
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public struct TEXTMETRICW
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth;
        public int Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
        public ushort FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, CharSet;
    }

    /// <summary>
    /// The smallest and largest a window may be dragged to, which is what WM_GETMINMAXINFO is asked for and
    /// what a resizable window owes the user: without a floor of its own, a window full of controls can be
    /// dragged until they overlap.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    // ---- the advertisement card's own controls, all of them stock ------------------------------------------
    public const uint WS_CLIPCHILDREN = 0x02000000, BS_PUSHBUTTON = 0x00000000, SS_CENTER = 0x00000001;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, BLACK_BRUSH = 4, DEFAULT_GUI_FONT = 17;
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int index);

    /// <summary>How the card hears a click on the video it is showing: the video is a child of its own.</summary>
    /// <summary>Walking the children of a window, which is the only way to see what a player put there.</summary>
    public const uint WM_PARENTNOTIFY = 0x0210, WM_CTLCOLORBTN = 0x0135;
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] public static extern uint SetBkColor(IntPtr dc, uint color);
    public static readonly IntPtr IDC_ARROW = (IntPtr)32512;
    /// <summary>IDI_APPLICATION, for the throwaway icon TrayCheck registers and removes at start-up.</summary>
    public static readonly IntPtr IDI_APPLICATION = (IntPtr)32512;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    /// <summary>A static asks its parent for the colours it should paint its text and background in.</summary>
    public const uint WM_CTLCOLORSTATIC = 0x0138;

    /// <summary>
    /// The mouse while a button is held. A click on the video is a click on a child window, and a child sends
    /// its parent a note about the button going DOWN and nothing about it coming up - so the up, the move and
    /// the release are only ever seen by whoever holds the capture. See AdsCard.Released.
    /// </summary>
    public const uint WM_MOUSEMOVE = 0x0200, WM_CAPTURECHANGED = 0x0215;
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();

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

    public const uint TDN_HYPERLINK_CLICKED = 3, TDN_DIALOG_CONSTRUCTED = 7;

    /// <summary>
    /// Rewrites one of the dialog's own elements while it is up, which is the only way its text can be changed:
    /// the content is a SysLink inside a DirectUIHWND and the main instruction is not a control at all, so
    /// there is no window whose text could be overwritten instead. WM_USER + 108 - and the six below it is
    /// TDM_CLICK_BUTTON, which closes the dialog rather than changing anything. probe/aboutdialog measures it.
    /// </summary>
    public const uint TDM_SET_ELEMENT_TEXT = 0x0400 + 108;
    /// <summary>TASKDIALOG_ELEMENTS, in commctrl.h's order.</summary>
    public const int TDE_CONTENT = 0, TDE_MAIN_INSTRUCTION = 3;

    /// <summary>
    /// TD_INFORMATION_ICON, and it is 0xFFFD rather than -3 on purpose: MAKEINTRESOURCEW truncates to a WORD
    /// and zero-extends, so the sign is gone by the time the system sees it. Passing (IntPtr)(-3) would be a
    /// pointer into the top of the address space and the dialog would come up with no icon.
    ///
    /// It is one of the values the union can hold beside a real HICON, and which of the two it is has to be said:
    /// with TDF_USE_HICON_MAIN the very same field is read as a handle, and a 0xFFFD read as one is a handle to
    /// nothing. The About box sets that flag when it has the program's own icon and leaves it off when it does
    /// not, so this value is what it falls back to rather than what it assumes.
    /// </summary>
    public static readonly IntPtr TD_INFORMATION_ICON = (IntPtr)0xFFFD;

    /// <summary>TDF_USE_HICON_MAIN: the main icon field is a handle this caller owns, not a resource value.</summary>
    public const uint TDF_USE_HICON_MAIN = 0x0002;

    public delegate int TaskDialogCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
                                           IntPtr refData);

    /// <summary>
    /// Shows the dialog and returns when it is closed. A failed HRESULT means nothing was shown, which is
    /// worth knowing about: the caller logs it rather than leaving a menu entry that appears to do nothing.
    /// </summary>
    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    public static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button,
                                                IntPtr radioButton, IntPtr verification);

    // ---- the one dialog with two answers of our own naming -------------------------------------------------

    /// <summary>IDCANCEL, which is also what a task dialog returns when it is dismissed instead of answered.</summary>
    public const int IDCANCEL = 2;

    /// <summary>IDOK: what the message-box fallback's first button answers with.</summary>
    public const int IDOK = 1;

    /// <summary>

    /// <summary>
    /// One of the dialog's own buttons, in commctrl.h's packing.
    ///
    /// Pack = 1 is measured rather than guessed, and it took fetching the header to settle: the Windows headers
    /// are not installed on the machine this was written on, and the structure is declared inside commctrl.h's
    /// pshpack1 block, together with the configuration that holds it. Getting it wrong is not a compile error and
    /// not a failed call - the dialog would read the text pointer four bytes further along than it was written,
    /// and the buttons would come out with whatever was there.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public struct TASKDIALOG_BUTTON
    {
        public int NButtonID;
        public string PszButtonText;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    /// <summary>
    /// The language the user's own Windows is in, as a LANGID: the primary language in the low ten bits and the
    /// sublanguage above it. It is the user's rather than the system's because this is a per-session tray program
    /// and the question it answers is what language to write to the person sitting at it.
    /// </summary>
    [DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();

    /// <summary>
    /// Asks a question whose two answers are named by the caller, and returns whether the first one was chosen.
    ///
    /// A task dialog rather than a message box because a message box's two buttons say OK and Cancel and cannot
    /// be made to say anything else - and the two answers here are not yes and no, they are "carry on from this
    /// folder" and "not this time", which are worth saying in words.
    ///
    /// The buttons are declared in the configuration, which is the documented way and the one with nothing to
    /// guess about. Adding the dialog's own OK and Cancel and rewriting their window text was tried first, and
    /// measured to be wrong: the text went in and the buttons read back empty, because the DirectUI host takes it
    /// back the moment it is set. See TASKDIALOG_BUTTON for the one thing here that had to be checked rather than
    /// known.
    ///
    /// Anything that is not the first button - the dialog dismissed with Esc or its X, or a dialog that could not
    /// be shown at all - is the second answer. The one caller is a program about to write a file into the folder
    /// it was started from, so the answer that has to be guessed at is the one that writes nothing.
    /// </summary>
    public static bool Ask(string caption, string question, string advice, string yes, string no)
    {
        AskNote = null;
        const int Yes = 100, No = 101;
        var buttons = new[]
        {
            new TASKDIALOG_BUTTON { NButtonID = Yes, PszButtonText = yes },
            new TASKDIALOG_BUTTON { NButtonID = No, PszButtonText = no },
        };
        // Held for the length of the call and handed to the callback, which reads the names back off it so that
        // the log can say whether the dialog took them - see Asked.
        var handle = GCHandle.Alloc(buttons);
        // And the same buttons in unmanaged memory, which is what the dialog is really handed. An array *field*
        // in the configuration was the first thing tried and it does not marshal at all: the call comes back as
        // E_INVALIDARG and the question falls through to the message box, which is a fallback that hides the
        // reason it was needed. So the array is laid out by hand, one structure at a time, with the strings
        // allocated by the marshaller and freed by it again in the finally below.
        int stride = Marshal.SizeOf<TASKDIALOG_BUTTON>();
        IntPtr block = Marshal.AllocHGlobal(stride * buttons.Length);
        try
        {
            for (int i = 0; i < buttons.Length; i++)
                Marshal.StructureToPtr(buttons[i], block + i * stride, false);

            var config = new TASKDIALOGCONFIG
            {
                CbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                HwndParent = IntPtr.Zero,
                DwFlags = TDF_ALLOW_DIALOG_CANCELLATION,
                DwCommonButtons = 0,                 // the two below are the whole of what it offers
                PszWindowTitle = caption,
                // The blue i, which is what this dialog is: a question about the folder, not a fault. Same
                // MAKEINTRESOURCE value as the About box's, and no TDF_USE_HICON_MAIN - see TD_INFORMATION_ICON.
                MainIcon = TD_INFORMATION_ICON,
                PszMainInstruction = question,
                PszContent = advice,
                CButtons = (uint)buttons.Length,
                PButtons = block,
                NDefaultButton = Yes,
                PfCallback = Asked,
                LpCallbackData = GCHandle.ToIntPtr(handle),
            };
            int hr = TaskDialogIndirect(ref config, out int button, IntPtr.Zero, IntPtr.Zero);
            if (hr >= 0) return button == Yes;
            AskNote = $"TaskDialogIndirect returned 0x{hr:X8}";
        }
        catch (Exception ex)
        {
            AskNote = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            for (int i = 0; i < buttons.Length; i++)
                Marshal.DestroyStructure<TASKDIALOG_BUTTON>(block + i * stride);
            Marshal.FreeHGlobal(block);
            handle.Free();
        }

        // And the question is still asked, with a message box. TaskDialogIndirect is comctl32 6's, and a process
        // that never asked for that version has no such entry point at all - which is what happened to the first
        // version of this, and what the probe could not see until it was given the manifest the program carries.
        // The two buttons then say OK and Cancel, the system's own names for the same two answers: the fallback is
        // not a lesser question, it is the same one in older clothes, and it is still asked rather than skipped.
        AskNote ??= "the task dialog was not available";
        return MessageBoxW(IntPtr.Zero, $"{question}{Environment.NewLine}{Environment.NewLine}{advice}",
                           caption, MB_OKCANCEL) == IDOK;
    }

    /// <summary>
    /// Whatever is worth writing down about the last <see cref="Ask" />, or null when it went as intended. Not an
    /// error either way - the question was asked and answered. It is here because both ways it can go wrong are
    /// invisible from the outside: a fallback to the message box, whose buttons are the system's, and a dialog
    /// whose buttons did not come out with the names they were given. The caller logs it while debug mode is on.
    /// </summary>
    public static string? AskNote;

    /// <summary>MB_OKCANCEL: the fallback's two buttons, which answer IDOK and IDCANCEL.</summary>
    public const uint MB_OKCANCEL = 0x00000001;

    /// <summary>
    /// Reads the dialog's own buttons back once they exist, and writes down any that did not come out as they
    /// were asked for.
    ///
    /// This is the check that the structure the names were declared in is the one the dialog read: a wrong
    /// packing would leave the buttons saying something nobody asked for, and nothing else in the program could
    /// notice. It reads rather than writes, which is the route that was tried first and measured to end with
    /// empty buttons. Whatever it finds reaches the log through AskNote, and nothing here changes what the dialog
    /// does.
    ///
    /// Returns S_OK for every notification, including the ones it does not care about: a dialog told anything
    /// else takes it as a refusal and closes.
    /// </summary>
    public const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0, ICON_BIG = 1;

    /// <summary>
    /// Takes the icon off a dialog's title bar, and leaves the one inside it alone.
    ///
    /// A task dialog inherits the executable's icon for its title bar, and both kinds of dialog here already
    /// carry an icon in the body - the blue information mark, or the program's own icon in the About box. The
    /// title bar's copy is the same picture twice in one window: the body is where a dialog says what it is
    /// about, and the strip across the top is a label, not a second announcement. Removing it is two
    /// WM_SETICONs with nothing in them, sent once the dialog exists; comctl32 does not put it back.
    /// </summary>
    public static void ClearTitleIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SendMessageW(hwnd, WM_SETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        SendMessageW(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
    }

    private static int Asked(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr refData)
    {
        if (msg == TDN_DIALOG_CONSTRUCTED)
        {
            ClearTitleIcon(hwnd);
            if (refData == IntPtr.Zero) return 0;
            try
            {
                var wanted = (TASKDIALOG_BUTTON[])GCHandle.FromIntPtr(refData).Target!;
                foreach (var button in wanted)
                {
                    // Nothing to read when the button is not a window of its own - a task dialog is free to keep
                    // its buttons inside its DirectUI host - and nothing to say about it either way.
                    IntPtr found = GetDlgItem(hwnd, button.NButtonID);
                    if (found == IntPtr.Zero) continue;
                    string got = TextOf(found);
                    if (got != button.PszButtonText)
                        AskNote = $"the button {button.NButtonID} says \"{got}\" and not \"{button.PszButtonText}\"";
                }
            }
            catch { }
        }
        return 0;
    }

    /// <summary>A window's text, or an empty string when there is no such window.</summary>
    private static string TextOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var text = new StringBuilder(256);
        int n = GetWindowTextW(hwnd, text, text.Capacity);
        return n > 0 ? text.ToString(0, n) : "";
    }

    // ---- opening a URL at the user's own level, with no ShellExecute anywhere -------------------------------

    /// <summary>
    /// What Windows has registered to handle a URL scheme, as the program that will be run, or null when it has
    /// nothing registered at all - which means no web address can be opened and is the answer to "is there a
    /// browser here" that costs nothing to ask. It is a query rather than a launch: nothing starts, and nothing
    /// is shown to anybody.
    ///
    /// ASSOCSTR_EXECUTABLE, and asking the shell rather than reading the registry: the mapping moves as the user
    /// changes their default browser, and the settings that hold it are not in one documented place - on the
    /// machine this was written on, HKCR's https handler says iexplore.exe while the answer here is a Chromium
    /// build, and only one of those is what would actually open.
    /// </summary>
    internal static string? HandlerFor(string scheme)
    {
        var text = new StringBuilder(1024);
        uint length = (uint)text.Capacity;
        int hr = AssocQueryStringW(0 /* ASSOCF_NONE */, 2 /* ASSOCSTR_EXECUTABLE */, scheme, "open",
                                   text, ref length);
        return hr < 0 || text.Length == 0 ? null : text.ToString();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int AssocQueryStringW(uint flags, uint what, string assoc, string extra,
                                                StringBuilder buffer, ref uint length);

    /// <summary>
    /// Opens a URL the way the user's own shell would, and says why not when it cannot.
    ///
    /// The policy is here and the mechanism is <see cref="AskExplorer" />. Two questions are asked first, and
    /// both are answered without starting anything: is there anything on this machine that opens an https address
    /// at all - a machine with no browser is what the caller puts a message box up for - and is the URL one of
    /// ours? The second is a shape, not a check on the string: a URL that does not begin with https:// is not
    /// something this program ever has a reason to open, and the only two callers build theirs from a constant.
    /// </summary>
    internal static bool OpenAsUser(string url, out string why)
    {
        why = "";
        if (url.Length == 0) { why = "there is no URL to open"; return false; }
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            why = "the URL is not one of ours, and nothing else is opened from this process";
            return false;
        }
        if (HandlerFor("https") is not string browser)
        {
            why = "nothing is registered to open an https address, so there is no browser to search with";
            return false;
        }

        // A URL is the only argument Explorer is given here, and it always begins with https:// - so it cannot
        // be one of Explorer's own switches - and it is percent-encoded, so it cannot be two arguments.
        if (!AskExplorer("\"" + url + "\"", out why)) return false;
        why += $", which is what hands the address to {browser}";
        return true;
    }

    /// <summary>
    /// Runs Explorer with a command line, as the user rather than as this process. False with the reason when it
    /// cannot.
    ///
    /// There is no ShellExecute here and that is the point of the whole method: ShellExecute starts the program
    /// from *this* process, so it inherits what this process is - and this one runs as administrator. What is
    /// done instead is that the process is created with the token of the running shell, which is at the user's own
    /// integrity level, and that what is created is Explorer, which is what knows how to turn a URL or a /select
    /// into the right application. That also means nothing here knows or cares which browser the user has, or
    /// where they keep it.
    ///
    /// Three things about the details, all of them measured rather than reasoned:
    ///
    ///   * The command line names the program and then gives it the arguments. That is the shape every
    ///     ShellExecute produces and the one a program's own command-line parsing expects; the same URL handed
    ///     over without the name in front was forwarded late or not at all.
    ///   * The token comes from the shell's own window (GetShellWindow is the desktop), and a token read out of a
    ///     process is already a primary one, so nothing has to be duplicated. The integrity level of what comes
    ///     out can be read back, which is what the caller logs and what probe/folder measures: see IntegrityOf.
    ///   * If creating the process with that token fails, the second attempt creates it with this program's own -
    ///     the one case where a process is started carrying this program's rights. It is worth having anyway: it
    ///     is Explorer, which is created only to hand one command line to the shell that is *already running*
    ///     (the token above could only be read because it is), and the browser is still opened by that shell at
    ///     the user's level. It is also the half of this that can be exercised on a machine where the first half
    ///     cannot be, which is what it was tested with; and it is only tried when there is a running shell, so a
    ///     machine with no Explorer is never given one of ours.
    ///
    /// It needs SeImpersonatePrivilege for the first attempt, which the program has because it runs as
    /// administrator - and which is exactly why that attempt can only be exercised by the program and never by a
    /// console that is not elevated. A console can still try both: probe/folder --open.
    /// </summary>
    internal static bool AskExplorer(string arguments, out string why)
    {
        why = "";
        // The full path rather than the name: CreateProcess searches for a bare name, and what it searches is a
        // list this program does not control.
        string explorer = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        string command = "\"" + explorer + "\" " + arguments;

        IntPtr token = ShellToken();
        if (token == IntPtr.Zero)
        {
            // No shell window means no Explorer is running, and the only thing this could start is a shell of our
            // own at this program's integrity level - which is the thing all of this exists to avoid.
            why = "there is no shell window to take a token from, so nothing was started";
            return false;
        }

        try
        {
            if (Create(token, explorer, command, out string asShell))
            {
                why = $"explorer.exe started at {asShell} with the shell's own token";
                return true;
            }
            if (Create(IntPtr.Zero, explorer, command, out string asOurs))
            {
                why = $"the shell's token did not work ({asShell}), so explorer.exe was started directly at " +
                      $"{asOurs} instead";
                return true;
            }
            why = $"neither starting explorer.exe with the shell's token ({asShell}) nor with this process's " +
                  $"({asOurs}) worked";
            return false;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// Creates one process, with <paramref name="token" /> or with this process's own when that is zero, and the
    /// integrity level it came out at as the reason it worked.
    /// </summary>
    private static bool Create(IntPtr token, string application, string command, out string why)
    {
        why = "";
        var start = new STARTUPINFOW { Cb = Marshal.SizeOf<STARTUPINFOW>() };
        PROCESS_INFORMATION info = default;
        bool ok;
        if (token == IntPtr.Zero)
            ok = CreateProcessW(application, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null,
                                ref start, out info);
        else
            ok = CreateProcessWithTokenW(token, 0 /* no logon flags */, application, command, 0, IntPtr.Zero, null,
                                         ref start, out info);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            why = (token == IntPtr.Zero ? "CreateProcess" : "CreateProcessWithTokenW") + $" failed with {err}" +
                  (err is 5 or 1314   /* ACCESS_DENIED, ERROR_PRIVILEGE_NOT_HELD */
                       ? ", which is what starting something at the user's own level costs: it needs a program" +
                         " that is running as administrator to pay it"
                       : "");
            return false;
        }
        try
        {
            // Said as what was created rather than as what was asked for: it is read off the process that came
            // out, and it is the whole of what "the browser is not elevated" rests on.
            why = $"{IntegrityOf(info.HProcess)} integrity";
        }
        finally
        {
            CloseHandle(info.HThread);
            CloseHandle(info.HProcess);
        }
        return true;
    }

    /// <summary>
    /// The token of the running shell, or zero when there is no shell window to take it from - which is a
    /// machine where Explorer is not running, and which has no desktop to open a browser on in any case. The
    /// caller owns the handle.
    /// </summary>
    private static IntPtr ShellToken()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(shell, out uint pid);
        if (pid == 0) return IntPtr.Zero;

        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            return OpenProcessToken(process, TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr token) ? token
                                                                                              : IntPtr.Zero;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// How privileged a process is, as the name of its integrity level: "Medium" for anything the user starts,
    /// "High" for anything a program running as administrator starts. It is here because that is the whole of
    /// what "the browser is not elevated" means, and because it can be read back rather than claimed - the
    /// caller writes it to the log, and probe/folder measures the shell's own.
    ///
    /// An integrity level is a SID whose last part is the level: 0x1000 medium, 0x2000 high, 0x3000 system.
    /// </summary>
    internal static string IntegrityOf(IntPtr process)
    {
        if (process == IntPtr.Zero || !OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return "unknown";
        try
        {
            // The size is asked for first, with a null buffer - which fails, and says how much is needed.
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out uint size);
            if (size < 8 || size > 4096) return "unknown";
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return "unknown";
                IntPtr sid = Marshal.ReadIntPtr(buffer);           // TOKEN_MANDATORY_LABEL.Label.Sid
                if (sid == IntPtr.Zero) return "unknown";
                int parts = Marshal.ReadByte(sid, 1);              // revision, count, authority, then the parts
                if (parts < 1) return "unknown";
                uint rid = (uint)Marshal.ReadInt32(sid, 8 + (parts - 1) * 4);
                return rid switch
                {
                    0x0000 => "Untrusted",
                    0x1000 => "Low",
                    0x2000 => "Medium",
                    0x2100 => "Medium plus",
                    0x3000 => "High",
                    0x4000 => "System",
                    _ => $"0x{rid:X4}",
                };
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25, TokenElevation = 20;

    /// <summary>
    /// The integrity level of this process, for the log line that says whether it is elevated. Both facts are
    /// written down because they can disagree: a token can be high integrity and still not be an elevated
    /// one, and a log that says only one of them leaves the other to be guessed at.
    /// </summary>
    internal static string Integrity() => IntegrityOf(GetCurrentProcess());

    /// <summary>
    /// Whether this process is at an integrity level UIPI will let it act from - at least High, which is what an
    /// elevated process has and what everything else on an ordinary machine is below.
    ///
    /// This, and not the elevation flag, is the question that matters, and the difference was measured on the
    /// machine this was written for: the Task Scheduler started a copy of this program with a token whose
    /// TokenElevation is **set** and whose integrity level is **Low**. WindowsPrincipal.IsInRole and
    /// TokenElevation therefore *both* answered "elevated" about a copy that could not add a tray icon or hide a
    /// single window - low is below the medium the shell and the applications run at, and UIPI is what compares
    /// them. The integrity level is the fact, so the check is this.
    ///
    /// "At least High" rather than "High": System is above it, and a copy running as System can hide windows.
    /// </summary>
    internal static bool IntegrityIsHighEnough()
    {
        uint rid = IntegrityRid();
        return rid >= 0x3000;   // 0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system
    }

    /// <summary>
    /// The integrity SID's last part, shared by the two callers above. See IntegrityOf for what it means and why
    /// the buffer is sized by asking first.
    /// </summary>
    private static uint IntegrityRid() => IntegrityRidOf(GetCurrentProcess());

    /// <summary>
    /// The integrity RID of a process, by id: 0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system; 0 when it
    /// cannot be read. This is the number TrayCheck compares against the taskbar's, because UIPI compares these
    /// levels and not the elevation flag.
    /// </summary>
    internal static uint IntegrityRidOf(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return 0;
        try { return IntegrityRidOf(process); }
        finally { CloseHandle(process); }
    }

    /// <summary>The same, from an already-open process handle.</summary>
    internal static uint IntegrityRidOf(IntPtr process)
    {
        if (process == IntPtr.Zero || !OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return 0;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out uint size);
            if (size < 8 || size > 4096) return 0;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return 0;
                IntPtr sid = Marshal.ReadIntPtr(buffer);           // TOKEN_MANDATORY_LABEL.Label.Sid
                if (sid == IntPtr.Zero) return 0;
                int parts = Marshal.ReadByte(sid, 1);              // revision, count, authority, then the parts
                if (parts < 1) return 0;
                return (uint)Marshal.ReadInt32(sid, 8 + (parts - 1) * 4);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// Whether this process is running with the elevated token, asked of the token itself.
    ///
    /// Not a role check, and the difference was measured rather than argued. A copy Windows started at logon
    /// on the machine this was written for - a scheduled task with /RL HIGHEST, "highest privileges", the
    /// whole of the mechanism - was found running at **medium** integrity while
    /// WindowsPrincipal.IsInRole(Administrator) had said *true* about it. So the check whose only job is to
    /// stop a copy that cannot work from running told it that it could, and what that copy could do is the
    /// rest of the evidence: its own windows were fine (the logon advertisement played, which is why it
    /// looked like it was running), while every window belonging to another process was out of reach - UIPI,
    /// because the applications on that machine and its shell run at high integrity - and Shell_NotifyIcon
    /// refused it a tray icon once a second for a minute.
    ///
    /// The same role check answers *false* about the shell this was diagnosed from, whose token is high
    /// integrity but not elevated either. It is not measuring elevation in either direction; TokenElevation
    /// is, and it is one DWORD.
    /// </summary>
    public static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            // Any failure to answer reads as "not elevated", which is the safe way round: the caller's
            // response is to leave, and a copy that cannot prove it has the rights should not be the one
            // that keeps running.
            if (!GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _)) return false;
            return elevated != 0;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// Starts a program elevated, through the shell's "runas" verb, which is the same request as the user
    /// choosing "run as administrator" - so Windows shows one consent prompt and this returns whether the user
    /// agreed. False means the request was refused: ERROR_CANCELLED (1223) when the user said no, something else
    /// when the account cannot elevate at all.
    ///
    /// The shell does the work rather than a custom launch: the consent machinery is only reachable through it,
    /// and manufacturing an elevated token is not a thing a program gets to do.
    /// </summary>
    internal static bool StartElevated(string file, string arguments, string directory)
    {
        var info = new SHELLEXECUTEINFOW
        {
            CbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            LpVerb = "runas",
            LpFile = file,
            LpParameters = arguments,
            LpDirectory = directory,
            NShow = 1,          // SW_SHOWNORMAL: a windowed program, and its own windows decide the rest
        };
        return ShellExecuteExW(ref info);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int CbSize;
        public uint FMask;
        public IntPtr Hwnd;
        public string? LpVerb;
        public string? LpFile;
        public string? LpParameters;
        public string? LpDirectory;
        public int NShow;
        public IntPtr HInstApp;
        public IntPtr LpIDList;
        public string? LpClass;
        public IntPtr HkeyClass;
        public uint DwHotKey;
        public IntPtr HIcon;
        public IntPtr HProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW info);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    // The DWORD form, for TokenElevation: the other declaration in this class takes a buffer.
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int what, out uint value, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int what, IntPtr info, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? application,
                                                       string commandLine, uint flags, IntPtr environment,
                                                       string? directory, ref STARTUPINFOW startup,
                                                       out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, string commandLine, IntPtr processAttributes,
                                              IntPtr threadAttributes, bool inheritHandles, uint flags,
                                              IntPtr environment, string? directory, ref STARTUPINFOW startup,
                                              out PROCESS_INFORMATION process);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int Cb;
        public string? LpReserved, LpDesktop, LpTitle;
        public int DwX, DwY, DwXSize, DwYSize, DwXCountChars, DwYCountChars, DwFillAttribute, DwFlags;
        public short WShowWindow, CbReserved2;
        public IntPtr LpReserved2, HStdInput, HStdOutput, HStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr HProcess, HThread;
        public int Pid, Tid;
    }

    /// <summary>
    /// The accent colour Windows is using - "主题色" in the Settings page - or null when one has never been
    /// chosen. Windows stores it as 0xAABBGGRR under DWM, cross-checked against ColorizationColor which holds
    /// the same colour as 0xAARRGGBB: the two agree once each is read in its own byte order, which is how the
    /// order here was confirmed. ColorizationColor is deliberately NOT a fallback - sharing one decoder with it
    /// would read it backwards and hand back the complement of the real accent.
    ///
    /// It lives here rather than in the animator that first needed it, because two things draw in it: the card's
    /// border and the program's own icon. One reader, so that the icon and an animation cannot disagree about
    /// what the user's colour is.
    /// </summary>
    public static Color? Accent()
    {
        var sources = new (string path, string name)[]
        {
            (@"Software\Microsoft\Windows\DWM", "AccentColor"),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentColorMenu"),
        };
        foreach (var (path, name) in sources)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
                if (k?.GetValue(name) is int v)
                    return Color.FromArgb(255, v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF);
            }
            catch { /* no accent stored; the caller falls back */ }
        }
        return null;
    }

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
