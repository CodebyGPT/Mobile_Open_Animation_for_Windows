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

    public static void Show(IntPtr owner)
    {
        _callback = OnNotification;

        var config = new Native.TASKDIALOGCONFIG
        {
            CbSize = (uint)Marshal.SizeOf<Native.TASKDIALOGCONFIG>(),
            HwndParent = owner,
            // The link flag is what turns <a href> in the content into something clickable; the dialog does not
            // follow links itself, it tells the callback which one was followed. Cancellation is what keeps the
            // box closable with no button in it - without it a dialog with no buttons could not be dismissed.
            DwFlags = Native.TDF_ENABLE_HYPERLINKS | Native.TDF_ALLOW_DIALOG_CANCELLATION,
            PszWindowTitle = Strings.T("About"),
            MainIcon = Native.TD_INFORMATION_ICON,
            PszMainInstruction = "Mobile Open Animation for Windows",
            // The version first because it is the thing most often wanted from this box: which build am I
            // running. Everything AGPL-3.0 section 0 asks an interactive interface to show is below it.
            PszContent = Strings.T("Version") + ": " + Build.Version + "\n\n" + Strings.T("AboutText"),
            PfCallback = _callback,
        };

        int hr = Native.TaskDialogIndirect(ref config, out _, IntPtr.Zero, IntPtr.Zero);
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
        return 0;   // S_OK
    }

    private static void Open(string url)
    {
        if (url.Length == 0) return;
        Log.Write($"about: opening {url}");
        try
        {
            Native.ShellExecuteW(IntPtr.Zero, "open", url, null, null, Native.SW_SHOWNORMAL);
        }
        catch (Exception ex)
        {
            Log.Write($"about: could not open {url} ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
