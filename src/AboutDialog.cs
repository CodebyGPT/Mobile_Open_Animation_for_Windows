// The About box, and the only place in the program that opens a browser.
//
// A task dialog rather than the message box this used to be, for the one thing a message box cannot do: a link
// in the text that can be followed. comctl32 draws it - the frame, the icon and the link are all the system's,
// and there is no drawing routine here. The callback exists to be told which link was followed and nothing else.
//
// No buttons. Closing is the title bar's X, Esc, or Alt-F4, all of which TDF_ALLOW_DIALOG_CANCELLATION allows
// with no button present. There is nothing to decide in this box, so a button would only be a second way to do
// the one thing there is to do. The ini path is gone as well: "Open config directory" in the tray menu is where
// somebody asks for that, and the box is not the place to repeat it.
//
// What is gone with it is everything this box used to watch. It played the old arcade code into itself - thirteen
// keys with no more than three seconds between them - and counted clicks on its own icon, both as a way of
// revealing the advertisement entry that the tray menu was hiding, and both seen with a low-level keyboard hook
// and a low-level mouse hook installed for the life of the dialog. The entry is on the menu and always was
// meant to be, so there is nothing to reveal, and a box that reads the whole machine's input while it is open
// is not something a box about a program has any reason to do. Konami and the egg are gone with it.
//
// One box at a time. The box runs a modal loop of its own, and that loop is what pumps this thread's messages -
// which is where the tray icon's menu is answered. Asking for "About" a second time while the first box is up
// therefore arrives here with this method still on the stack, and building a second box over the first is not
// what the click meant. The second ask brings the box that is already there to the front instead.

using System.Runtime.InteropServices;

namespace Moa;

internal static class AboutDialog
{
    /// <summary>
    /// Held for the life of the dialog. A delegate marshalled as a function pointer is not kept alive by the
    /// native side, and the dialog outlives this method's stack frame, so a collected one would be a crash
    /// rather than a dialog that stops answering.
    /// </summary>
    private static Native.TaskDialogCallback? _callback;

    /// <summary>
    /// True from the moment a box is asked for until it is closed. See the header: the box's own modal loop is
    /// what lets a second ask arrive while the first is still on the stack.
    /// </summary>
    private static bool _open;

    /// <summary>The dialog, once it exists. Zero before that, and cleared as soon as it has been closed.</summary>
    private static IntPtr _dlg;

    /// <summary>Shows the box and returns when it is closed.</summary>
    public static void Show(IntPtr owner)
    {
        if (_open)
        {
            Log.Write("about: the box is already up; bringing it forward instead of building a second one");
            if (_dlg != IntPtr.Zero) Native.SetForegroundWindow(_dlg);
            return;
        }
        _open = true;
        try { ShowOnce(owner); }
        finally { _open = false; }
    }

    private static void ShowOnce(IntPtr owner)
    {
        _callback = OnNotification;
        _dlg = IntPtr.Zero;

        // The version first because it is the thing most often wanted from this box: which build am I running.
        // Everything AGPL-3.0 section 0 asks an interactive interface to show is below it.
        string content = Strings.T("Version") + ": " + Build.Version + "\n\n" + Strings.T("AboutText");

        // The program's own icon as the box's icon, in place of the blue information one. The dialog draws
        // whatever it is handed here exactly as it draws its own - see TDF_USE_HICON_MAIN - so this box still
        // draws nothing itself: the handle is the exe's own icon resource, taken out and given back.
        //
        // The flag is set only when there is a handle, because the field means two different things depending on
        // it: without it, 0xFFFD is the information icon, and with it the same value is a handle to nothing.
        IntPtr icon = AppIcon.FromExe();

        var config = new Native.TASKDIALOGCONFIG
        {
            CbSize = (uint)Marshal.SizeOf<Native.TASKDIALOGCONFIG>(),
            HwndParent = owner,
            // The link flag is what turns <a href> in the content into something clickable; the dialog does not
            // follow links itself, it tells the callback which one was followed. Cancellation is what keeps the
            // box closable with no button in it - without it a dialog with no buttons could not be dismissed.
            DwFlags = Native.TDF_ENABLE_HYPERLINKS | Native.TDF_ALLOW_DIALOG_CANCELLATION |
                      (icon != IntPtr.Zero ? Native.TDF_USE_HICON_MAIN : 0),
            PszWindowTitle = Strings.T("About"),
            MainIcon = icon != IntPtr.Zero ? icon : Native.TD_INFORMATION_ICON,
            PszMainInstruction = "Mobile Open Animation for Windows",
            PszContent = content,
            PfCallback = _callback,
        };

        int hr;
        try
        {
            hr = Native.TaskDialogIndirect(ref config, out _, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _dlg = IntPtr.Zero;
            // After the dialog has returned and not before: it is the dialog that is drawing it. See AppIcon.
            if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        }
        // Written either way. A dialog that never appeared and one that was closed both come out of this call
        // as nothing happening, and only the second is what the user meant.
        Log.Write(hr != 0
            ? $"about: TaskDialogIndirect failed 0x{hr:X8}; nothing was shown"
            : "about: closed");
    }

    private static int OnNotification(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr data)
    {
        // The URL comes from our own string, but read it anyway rather than substituting the constant: what is
        // opened should be what was clicked.
        if (msg == Native.TDN_HYPERLINK_CLICKED) Open(Marshal.PtrToStringUni(lParam) ?? "");
        // Sent once the box and its controls are both built. The handle is kept so that a second ask for About
        // can bring this box forward rather than building another; see Show.
        else if (msg == Native.TDN_DIALOG_CONSTRUCTED) _dlg = hwnd;
        return 0;   // S_OK
    }

    private static void Open(string url)
    {
        if (url.Length == 0) return;
        // Through the same door the advertisement's name uses, and for the same reason: this program runs as
        // administrator, and a browser started by it would be too. See Native.OpenAsUser.
        bool opened = Native.OpenAsUser(url, out string why);
        Log.Write(opened ? $"about: opening {url} - {why}" : $"about: could not open {url}: {why}");
    }
}
