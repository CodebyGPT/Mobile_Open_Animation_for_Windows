// Registering the logon task
// ==========================
//
// Why a scheduled task rather than the usual Run key
// --------------------------------------------------
// The program requires administrator rights (see app.manifest), and the HKCU\...\Run key starts
// programs unelevated. A Run entry would therefore launch a process that immediately refuses to
// run - an error box at every logon. A scheduled task with a logon trigger and "run with highest
// privileges" is the one way to start an elevated program at logon without a UAC prompt.
//
// Why schtasks.exe
// ----------------
// The Task Scheduler COM interface (Schedule.Service) would avoid spawning a process, which is
// the friendlier thing to do from an antivirus heuristics point of view - "program launches
// schtasks.exe" is a familiar persistence pattern. It was not chosen because it needs COM
// interop through dynamic, which is several times the code and far easier to get subtly wrong.
// If that trade ever needs revisiting, this file is the only thing that changes: the rest of the
// program talks to IsEnabled and Apply and knows nothing about how the task is managed.
//
// The rule the caller depends on
// ------------------------------
// Apply reports the state that actually holds afterwards, not the state that was requested. The
// caller updates the menu from that answer, so a failed create or delete leaves the check where
// it was rather than lying about it.

using System.Diagnostics;
using System.Text;

namespace Moa;

internal static class AutoStart
{
    private const string TaskName = "Mobile Open Animation for Windows";

    // schtasks is a console program: it writes in the console code page, not UTF-8. Resolved once,
    // because without it its message arrives as mojibake and would be shown to the user verbatim in
    // the Start with Windows error dialog - the same fault that made the About box garbled.
    private static readonly Encoding Oem =
        Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    /// <summary>
    /// True only when the logon task exists AND still matches the configuration this program
    /// creates.
    ///
    /// Every field is checked, and a task that fails any of them counts as absent. A task that
    /// has been repointed at another program, or that has lost its logon trigger or its
    /// highest-privileges run level, is not ours: the menu must not claim a state we do not
    /// control, and treating it as absent means the next create overwrites it with /F, which
    /// repairs it. The check is deliberately in that direction - a false "absent" costs one
    /// redundant create, a false "present" leaves a foreign program starting at logon.
    /// </summary>
    public static bool IsEnabled()
    {
        if (Query(out _) != 0) return false;          // does not exist at all
        if (QueryXml(out string xml) != 0)
        {
            Log.Write("autostart: the task exists but /XML could not be read");
            return false;
        }
        if (!Matches(xml))
        {
            // The XML is logged because a failing field check is otherwise indistinguishable from a
            // task that genuinely was tampered with, and this cannot be reproduced anywhere but on a
            // machine that has the task.
            Log.Write("autostart: task exists but does not match; xml follows");
            Log.Write(xml.Length > 1200 ? xml.Substring(0, 1200) : xml);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The configuration this program creates, compared as literal XML element names and values.
    /// Those tokens are the same in every locale, unlike the labels schtasks prints in its list
    /// formats, which is why the XML form is used here rather than /FO LIST /V.
    /// </summary>
    private static bool Matches(string xml)
        => xml.Contains("<LogonTrigger>")                          // starts at logon
        // Absent means enabled. The schema defaults Enabled to true and schtasks does not write the
        // element for a task it created enabled, so requiring it to be present made every task this
        // program created look tampered with - which is exactly what broke this feature. An explicit
        // <Enabled>false</Enabled> is still caught.
        && (!xml.Contains("<Enabled>") || xml.Contains("<Enabled>true</Enabled>"))
        && xml.Contains("<RunLevel>HighestAvailable</RunLevel>")   // elevated, no UAC prompt
        // The path as a substring rather than the whole element: schtasks stores the command with
        // whatever quoting it was given, and the stored form here is quoted:
        //   <Command>"C:\...\MobileOpenAnimation.exe"</Command>
        // so an exact match would fail on the quotes alone. Requiring both the element and our path
        // still catches a task repointed at another program.
        && xml.Contains("<Command>") && xml.Contains(Compat.ExePath);

    /// <summary>Runs the existence query and hands back schtasks' own exit code and output. Exposed so the
    /// start-up log can tell "the task is not there" apart from "we could not ask at all" - from
    /// IsEnabled alone those two look identical, and only one of them is worth worrying about.</summary>
    public static int Query(out string output) => Run($"/Query /TN \"{TaskName}\"", out output);

    /// <summary>The full task definition as XML, for the field-by-field check in IsEnabled.</summary>
    private static int QueryXml(out string output) => Run($"/Query /TN \"{TaskName}\" /XML", out output);

    /// <summary>
    /// Turns the logon task on or off, then reports the state that actually holds afterwards.
    /// </summary>
    /// <param name="wanted">True to register the task, false to delete it.</param>
    /// <param name="error">Empty on success, otherwise something worth showing the user.</param>
    /// <returns>The state that holds after the attempt, which the caller should trust over
    /// <paramref name="wanted"/>.</returns>
    public static bool Apply(bool wanted, out string error)
    {
        error = "";

        // /RL HIGHEST is the point of the exercise: it is what makes an elevated program start at
        // logon without a UAC prompt. /F overwrites an existing task, so re-enabling is idempotent
        // and picks up a moved executable.
        string args = wanted
            ? $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"\\\"{Compat.ExePath}\\\"\""
            : $"/Delete /TN \"{TaskName}\" /F";

        int rc = Run(args, out string output);

        // Check what is true, not what was asked for - retried briefly, because the task is not
        // always queryable the instant schtasks returns. Without the retry a create that worked can
        // be reported as failed, which leaves the menu unchecked while the task exists, and the next
        // click then tries to create it again instead of deleting it.
        bool now = IsEnabled();
        for (int attempt = 0; attempt < 4 && now != wanted; attempt++)
        {
            System.Threading.Thread.Sleep(150);
            now = IsEnabled();
        }

        Log.Write($"autostart: wanted={wanted} now={now} create/delete rc={rc}");
        if (now != wanted)
        {
            error = rc != 0
                ? $"schtasks exited with code {rc}.\n\n{output.Trim()}"
                : "The task did not end up in the state that was asked for.";
        }
        return now;
    }

    /// <summary>
    /// Runs schtasks and decodes what it wrote by looking for a byte-order mark.
    ///
    /// Guessing an encoding is what produced the garbled dialogs earlier in this program, and here
    /// guessing would be worse than cosmetic: the XML check compares text, so mis-decoded XML would
    /// look exactly like a task that had been tampered with. schtasks writes its text forms in the
    /// console code page and its /XML form as UTF-16, and the BOM is the reliable way to tell them
    /// apart, so the raw bytes are read and decoded deliberately rather than left to a default.
    /// </summary>
    private static int Run(string args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Only a placeholder: Decode reads the bytes itself and decides.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;

            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.StandardError.BaseStream.CopyTo(ms);

            if (!p.WaitForExit(10000))
            {
                try { p.Kill(); } catch { }
                return -2;
            }
            output = Decode(ms.ToArray());
            // Every call is logged with its exit code and its own words. The behaviour of this
            // feature depends on schtasks in ways that cannot be reproduced on a machine that has
            // no Task Scheduler access, so the log is the only way to see what actually happened.
            Log.Write($"schtasks {args} -> rc={p.ExitCode} {output.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    /// <summary>Byte-order mark first, then a NUL-pattern guess, then the console code page.</summary>
    private static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);

        // No mark. UTF-16 without one is common for redirected output, and reading it as single-byte
        // text leaves a NUL next to every character - that pattern is the tell. Getting this wrong
        // is not cosmetic here: the XML check compares text, so a mis-decoded answer looks exactly
        // like a task that has been tampered with, and the feature would silently disable itself.
        int nulAtOdd = 0, nulAtEven = 0, n = Math.Min(b.Length, 512);
        for (int i = 0; i < n; i++)
            if (b[i] == 0) { if ((i & 1) == 1) nulAtOdd++; else nulAtEven++; }
        if (nulAtOdd > n / 8 && nulAtEven == 0) return Encoding.Unicode.GetString(b);
        if (nulAtEven > n / 8 && nulAtOdd == 0) return Encoding.BigEndianUnicode.GetString(b);

        return Oem.GetString(b);
    }
}
