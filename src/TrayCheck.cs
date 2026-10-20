// The two facts that decide whether this program can be a tray program at all: whether it may talk to the
// taskbar's notification area, and whether the shell accepts an icon when it is asked.
//
// It exists because of a machine where the program worked only when elevated, and the cause was not the
// program: a copy started at low integrity - below the medium the taskbar runs at - is refused by UIPI when it
// posts Shell_NotifyIcon to Explorer's tray window. A tray program with no tray icon has no interface at all,
// so the answer cannot be "continue and hope"; it is one of the two ways out the failure dialog offers, and
// this file is only the question. See Program.Run for the dialog and the two answers.
//
// Both halves are measured rather than inferred, and the integrity comparison comes first because it explains
// the refusal: the probe is a throwaway icon with a throwaway window, removed immediately, so a run that is
// otherwise fine - every ordinary launch from an ordinary folder - leaves nothing behind and costs two shell
// calls.

using System.Runtime.InteropServices;

namespace Moa;

internal static class TrayCheck
{
    internal enum State
    {
        /// <summary>The icon was accepted; there is nothing to tell the user.</summary>
        Ok,

        /// <summary>This process is below the taskbar's integrity level, so UIPI refuses the icon.</summary>
        LowIntegrity,

        /// <summary>The levels are fine and the shell refused the icon for another reason.</summary>
        Denied,
    }

    /// <summary>
    /// Runs both checks and describes what it found, for the log and for the dialog. The summary is always
    /// filled, including when everything passed: a log that says "ok" and nothing else cannot be compared with
    /// the next machine.
    /// </summary>
    internal static State Run(out string summary)
    {
        uint me = Native.IntegrityRidOf(Native.GetCurrentProcessId());

        uint taskbar = 0;
        IntPtr bar = Native.FindWindowW("Shell_TrayWnd", null);
        // A logon start can arrive before Explorer has put the taskbar up. Waiting a moment is the difference
        // between "the notification area is unavailable" and "it is not there yet"; the wait is bounded and is
        // the only one in this program.
        for (int i = 0; i < 50 && bar == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            bar = Native.FindWindowW("Shell_TrayWnd", null);
        }
        if (bar != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(bar, out uint pid);
            taskbar = Native.IntegrityRidOf(pid);
        }

        summary = $"process={Name(me)}; taskbar={(bar == IntPtr.Zero ? "not running" : Name(taskbar))}";
        // Only when both are readable: an unreadable level is not evidence of a mismatch, and the probe below
        // is the fact that decides in any case.
        if (me != 0 && taskbar != 0 && me < taskbar) return State.LowIntegrity;

        if (!Probe(out string why)) { summary += $"; {why}"; return State.Denied; }
        summary += $"; {why}";
        return State.Ok;
    }

    /// <summary>
    /// Registers an icon and removes it again, which is the only way to ask whether the shell will take one:
    /// the refusal is a return value, not a message, and the integrity comparison alone cannot see the other
    /// reasons a tray window might not exist yet.
    /// </summary>
    private static bool Probe(out string message)
    {
        IntPtr hwnd = Native.CreateWindowExW(0, "STATIC", "MoaTrayProbe", Native.WS_POPUP,
                                             0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
                                             Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            message = $"the probe window could not be created (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            var data = new NOTIFYICONDATAW
            {
                CbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                Hwnd = hwnd,
                UID = 0x4D4F41,   // "MOA", so the throwaway icon cannot be confused with the real one
                UFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
                UCallbackMessage = 0x8000 + 7,
                HIcon = Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION),
                SzTip = "probe",
                SzInfo = "",
                SzInfoTitle = "",
            };

            bool ok = Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
            int error = Marshal.GetLastWin32Error();
            if (ok) Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
            message = ok ? "Shell_NotifyIcon accepted"
                         : $"Shell_NotifyIcon(NIM_ADD) failed: error {error}";
            return ok;
        }
        finally { Native.DestroyWindow(hwnd); }
    }

    /// <summary>An integrity level as its name and number, which is what a log reader needs to compare.</summary>
    private static string Name(uint rid) => rid switch
    {
        0x0000 => "untrusted (0x0000)",
        0x1000 => "low (0x1000)",
        0x2000 => "medium (0x2000)",
        0x2100 => "medium plus (0x2100)",
        0x3000 => "high (0x3000)",
        0x4000 => "system (0x4000)",
        _ => rid == 0 ? "unknown" : $"0x{rid:X4}",
    };
}
