// A plain append-only log next to the exe, written only while debug mode is on.
//
// There is one build of this program and every diagnostic is in it. What is left to decide is decided at
// run time: Log.On is the debug-mode switch from the tray menu, and with it off nothing is buffered,
// nothing is written, and no log file appears next to the exe at all. The switch is read at every call, so
// turning it on or off in the menu takes effect on the next line rather than at the next start.
//
// What that costs is that the string a call site formats is built whether or not anyone is listening.
// That is accepted deliberately: guarding eighty-odd call sites by hand would be worse to read than the
// formatting is to run, and the two lines that fire often enough to be worth counting - a destroy event,
// and the age of a window event - test Log.On themselves before building anything. If a profile ever shows
// the formatting, the same test can be added at the call site that shows it: On is a public field, so it
// is one branch.
//
// CRITICAL: the window-created path must never touch the disk. Write is what that path calls, and it
// only appends to a list; Flush is what writes the file, and it runs from the animation tick.
//
// The window-created callback has as little as 2 ms to hide a window before its owner shows
// it - 7-Zip measured 1.96 ms from creating its window to showing it, against 5.3 ms for the
// next tightest application and 25-840 ms for everything else. The original version of this
// class wrote straight to the file, and it was called twice before the hide. That ate the
// whole budget for 7-Zip and only for 7-Zip, and the symptom was the 7-Zip window appearing
// together with its animation instead of being hidden behind it.
//
// So: lines are appended to a list here, and the animation tick writes them out a few times
// a second.

namespace Moa;

internal static class Log
{
    /// <summary>
    /// Debug mode, from the tray menu. Volatile because it is written on the message loop when the menu
    /// item is clicked and read from the animation thread and the watcher's hook callbacks, and the next
    /// line of either should see the change rather than a cached copy of the field.
    /// </summary>
    public static volatile bool On;

    private static readonly object Gate = new();
    private static readonly List<string> Buffer = new();
    /// <summary>
    /// This build's log, named after the build: a log can then be matched to the executable that wrote it
    /// without being opened, and a new build does not overwrite the log of the build whose fault is still
    /// being looked for.
    /// </summary>
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory,
        $"MobileOpenAnimation-{Build.Version}.log");

    /// <summary>
    /// The same name, for the file a fatal error is reported into. Same reasoning: such a report is worth
    /// more when it says which build produced it.
    /// </summary>
    public static readonly string ErrorPath = System.IO.Path.Combine(AppContext.BaseDirectory,
        $"MobileOpenAnimation-{Build.Version}.error.log");

    private static long _lastFlush;

    static Log()
    {
        // Anything still buffered when the process ends must still reach the file.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(force: true);
    }

    /// <summary>Cheap: appends to a list, no file handle, no blocking on I/O.</summary>
    public static void Write(string s)
    {
        if (!On) return;
        lock (Gate) Buffer.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
    }

    /// <summary>Opens a run in the log: the callers that turn debug mode on do it for that reason.</summary>
    public static void Start(string header)
    {
        if (!On) return;
        lock (Gate)
            Buffer.Add($"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {header}, " +
                       $"build {Build.Version} =====");
    }

    /// <summary>Called from the animation tick. At most four writes a second, off the hot path.</summary>
    public static void Flush(bool force = false)
    {
        if (!On) return;
        List<string> batch;
        lock (Gate)
        {
            if (Buffer.Count == 0) return;
            long now = Compat.TickCount64;
            if (!force && now - _lastFlush < 250) return;
            _lastFlush = now;
            batch = new List<string>(Buffer);
            Buffer.Clear();
        }
        try { File.AppendAllLines(Path, batch); } catch { }
    }
}
