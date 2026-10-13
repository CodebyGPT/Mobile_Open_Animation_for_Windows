// Registering the logon task
// ==========================
//
// What this writes
// ----------------
// One scheduled task, with an ONLOGON trigger and the highest privileges available to its owner:
//
//   \Mobile Open Animation for Windows
//     Trigger   : at logon, by this user
//     Principal : this user, InteractiveToken, HighestAvailable
//     Action    : "C:\...\MobileOpenAnimation.exe" --logon
//
// HighestAvailable is the point of the whole thing: it is the one way Windows starts an elevated program at
// logon without a UAC prompt, and nothing else does it.
//
// Why the task is registered from XML, and through PowerShell
// ----------------------------------------------------------
// A task is registered from an XML definition with
//
//   Register-ScheduledTask -Force -TaskName '<name>' -Xml '<definition>'
//
// rather than with `schtasks /Create`, and that is not a preference: `schtasks /Create` writes
// <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries> and
// <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries> into every task it makes, and **no switch turns them
// off** - so a task made that way does not start at logon on a laptop running on battery, and is killed if
// the machine goes on battery while it runs. The definition below sets both to false, and also expresses two
// things schtasks has no argument for: no execution time limit (its default is three days) and a working
// directory. This is how the mechanism is done in shipped software - Watt Toolkit registers its
// "at logon, highest privileges" task exactly this way, with this same set of fields.
//
// The copying is deliberate and worth naming: on the machine this was written for, the schtasks version of
// this task started the program at logon *without* elevation - the task's own XML was right (interactive
// token, highest available, this program's path) and Windows still handed the process a medium integrity
// token, which UIPI then used to refuse it the tray icon and every window it tried to hide. What Watt Toolkit
// does differently is how the definition is written and registered, so that is what is being tried, copied
// field for field. If this still comes out medium, the mechanism is not the difference and the machine is.
//
// What it replaces, and what that cost
// ------------------------------------
// Before this: the same task registered through schtasks, then a shortcut in the Startup folder, then a value
// under Run in the registry, and then the task again through schtasks. The shortcut and the Run value were
// both reported as blocked at logon, and the log agreed: the builds that carried them have manual starts in
// them and not one logon start. Both are started by the *shell*, and this program's manifest asks for
// elevation, so both need a UAC prompt at logon - on a machine that is set to prompt, the answer is usually
// nobody. A task is started by the Task Scheduler service, which is not the shell and does not ask.
//
// The logon argument
// ------------------
// The task starts the program with <see cref="LogonArgument" />, and that argument is the whole of how a copy
// started at logon is told from a copy the user started themselves. Nothing else can tell them apart: the
// same executable, the same user, and the clock is no help - the logon launch can sit behind whatever the
// machine does at logon, so a start ten minutes later is indistinguishable from a start at logon. See
// Program.StartedAtLogon, which is what plays the logon advertisement.
//
// What is checked before the menu says it is on
// ---------------------------------------------
// The task's own XML, field by field: a logon trigger, HighestAvailable, this program's path, the logon
// argument, and not disabled. It is read rather than assumed because a task is a thing other people can
// reach: Task Scheduler shows it, another account can edit it, and a cleanup tool can remove it. A task that
// fails any of those counts as absent, so the next click writes it again - the registration is forced - which
// repairs it rather than leaving the menu claiming a state this program does not control.
//
// The rule the caller depends on
// ------------------------------
// Apply reports the state that actually holds afterwards, not the state that was requested. The caller
// updates the menu from that answer, so a failed registration or deletion leaves the check where it was
// rather than lying about it.

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Moa;

internal static class AutoStart
{
    /// <summary>The task's name, which is what Task Scheduler shows and what every query uses.</summary>
    private const string TaskName = "Mobile Open Animation for Windows";

    /// <summary>
    /// What the task's action passes, and the only thing that distinguishes the copy Windows starts at logon
    /// from a copy the user started by hand. See Program.StartedAtLogon.
    ///
    /// It is not a switch the user is meant to type: the Debug menu's entry plays a logon advertisement
    /// whenever it is asked to, and this is what the task does instead of that. Nothing else reads it.
    /// </summary>
    public const string LogonArgument = "--logon";

    // schtasks is a console program: it writes in the console code page, not UTF-8. Resolved once, because
    // without it its message arrives as mojibake and would be shown to the user verbatim in the Start with
    // Windows error dialog - the same fault that made the About box garbled.
    private static readonly Encoding Oem =
        Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    /// <summary>
    /// True only when the task exists AND is still the one this program writes.
    ///
    /// A task that fails any of the field checks counts as absent, which is what makes the next enable write it
    /// again: a false "absent" costs one redundant registration, while a false "present" leaves the menu
    /// claiming a state this program does not control.
    ///
    /// Called on every right click, and it is two schtasks runs - tens of milliseconds, which is why the menu
    /// does not ask any more often than that.
    /// </summary>
    public static bool IsEnabled()
    {
        if (Query(out _) != 0)
        {
            Log.Write($"autostart: no task named \"{TaskName}\"");
            return false;
        }

        if (Run("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML", out string xml) != 0)
        {
            Log.Write("autostart: the task exists but /XML could not be read");
            return false;
        }

        if (!Matches(xml))
        {
            // The XML is logged because a failing field check is otherwise indistinguishable from a task that
            // genuinely was tampered with, and this cannot be reproduced anywhere but on a machine that has the
            // task. It is cut at 1200 characters, which is past every field that is checked.
            Log.Write("autostart: the task exists but is not the one this program writes; xml follows");
            Log.Write(xml.Length > 1200 ? xml.Substring(0, 1200) : xml);
            return false;
        }

        Log.Write("autostart: the task is this program's, started at logon");
        return true;
    }

    /// <summary>Runs the existence query and hands back schtasks' own exit code and output. Exposed so the
    /// start-up log, and probe/autostart, can tell "the task is not there" apart from "we could not ask at
    /// all" - from IsEnabled alone those two look identical, and only one of them is worth worrying about.</summary>
    public static int Query(out string output) => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"", out output);

    /// <summary>
    /// The configuration this program creates, compared as literal XML element names and values.
    ///
    /// Those tokens are the same in every locale, unlike the labels schtasks prints in its list formats, which
    /// is why the XML form is used here rather than /FO LIST /V.
    ///
    /// Internal because it is the half of this that probe/autostart can check: the writer cannot be exercised
    /// there - the only task a probe could create is one that starts the probe at logon - so it is checked
    /// against the XML this program generates instead.
    /// </summary>
    internal static bool Matches(string xml)
        => xml.Contains("<LogonTrigger>")                          // starts at logon
        // A disabled task or a disabled trigger is one Windows will not run, and it is caught by looking for
        // the *value* rather than for the element. Absent means enabled - the schema defaults it and a task
        // registered without the element is enabled - so requiring the element to be present would make every
        // task this program created look tampered with, which is what broke this feature once already.
        //
        // The value, and not "the document contains an Enabled element that is true", because this program's
        // own definition now has more than one: the trigger carries <Enabled>true</Enabled> too, and a task
        // switched off in Task Scheduler would then still be found by the trigger's copy of the element. The
        // probe checks this exact case.
        && !xml.Contains("<Enabled>false</Enabled>")
        && xml.Contains("<RunLevel>HighestAvailable</RunLevel>")   // elevated, no UAC prompt
        // The path as a substring rather than the whole element: the stored command is what was registered,
        // which is this path, and requiring both the element and the path still catches a task repointed at
        // another program.
        && xml.Contains("<Command>") && xml.Contains(Compat.ExePath)
        // And the argument, which may be stored inside the command or in an <Arguments> element of its own
        // depending on how the registration split it. Either way it is in the XML once, and a task without it
        // would start the program and play no logon advertisement - a silent difference between two builds that
        // nobody could see.
        && xml.Contains(LogonArgument);

    /// <summary>
    /// The task definition, as the XML that gets registered.
    ///
    /// Every field here is deliberate, and most of them are the ones `schtasks /Create` cannot express:
    ///
    ///   * the trigger names the user, so the task fires for their logon and not for anybody else's;
    ///   * <c>InteractiveToken</c> is what puts the copy in the user's own session - the tray icon it draws and
    ///     the windows it hides only exist there;
    ///   * <c>HighestAvailable</c> is the silent elevation this whole mechanism exists for;
    ///   * both battery settings are false, which is the reason this is XML rather than a command line;
    ///   * <c>ExecutionTimeLimit</c> is zero, because the default three days would kill the program if it ran
    ///     that long;
    ///   * the working directory is the program's own folder rather than whatever the Task Scheduler would
    ///     otherwise start it in.
    ///
    /// Internal for probe/autostart, which checks that the reader accepts what this writes and that it parses.
    /// </summary>
    internal static string TaskXml()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userName = identity.Name;
        string userId = identity.User?.ToString() ?? userName;

        string exe = XmlEscape(Compat.ExePath);
        string dir = XmlEscape(Path.GetDirectoryName(Compat.ExePath) ?? "");

        return
            // UTF-16, and it is not a style choice: the Task Scheduler's own parser refuses any other
            // declaration with "the task XML is malformed - cannot switch encoding", which is what this line
            // said when it was UTF-8. The definition is written to its temp file in that encoding as well, so
            // the declaration describes the bytes as well as satisfying the parser.
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            $"<RegistrationInfo><Description>{TaskName} logon start</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled>" +
            $"<UserId>{XmlEscape(userName)}</UserId></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\">" +
            $"<UserId>{XmlEscape(userId)}</UserId>" +
            "<LogonType>InteractiveToken</LogonType>" +
            "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<AllowHardTerminate>false</AllowHardTerminate>" +
            "<StartWhenAvailable>false</StartWhenAvailable>" +
            "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
            "<IdleSettings><Duration>PT10M</Duration><WaitTimeout>PT1H</WaitTimeout>" +
            "<StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
            "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>" +
            "<RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>" +
            $"<Actions Context=\"Author\"><Exec><Command>{exe}</Command>" +
            $"<Arguments>{LogonArgument}</Arguments><WorkingDirectory>{dir}</WorkingDirectory>" +
            "</Exec></Actions></Task>";
    }

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
        string output;
        int rc;

        if (wanted)
        {
            // The definition goes through a file rather than on the command line. It is XML full of double
            // quotes, and the command line it would travel in is already made of the quotes that make up
            // PowerShell's own argument - escaping either layer into the other is a bug waiting to happen, and
            // the file also means the definition that was registered can be read back afterwards.
            string path = TaskXmlPath();
            try
            {
                File.WriteAllText(path, TaskXml(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
                rc = Run("powershell.exe", RegisterArguments(path), out output);
            }
            catch (Exception ex)
            {
                output = $"{ex.GetType().Name}: {ex.Message}";
                rc = -1;
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
        else
        {
            rc = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", out output);
        }

        // Check what is true, not what was asked for - retried briefly, because the task is not always
        // queryable the instant the registration returns. Without the retry a registration that worked can be
        // reported as failed, which leaves the menu unchecked while the task exists, and the next click then
        // tries to register it again instead of deleting it.
        bool now = IsEnabled();
        for (int attempt = 0; attempt < 4 && now != wanted; attempt++)
        {
            System.Threading.Thread.Sleep(150);
            now = IsEnabled();
        }

        Log.Write($"autostart: wanted={wanted} now={now} register/delete rc={rc}");
        if (now != wanted)
        {
            error = rc != 0
                ? $"The command that changes the task exited with code {rc}.\n\n{output.Trim()}"
                : "The task did not end up in the state that was asked for.";
        }
        return now;
    }

    /// <summary>
    /// Where the definition is written on its way to PowerShell. Named for this process, so that two copies of
    /// this program in different folders - which are two instances as far as the single-instance guard is
    /// concerned - cannot write over each other's definition while one of them is being registered.
    /// </summary>
    internal static string TaskXmlPath() =>
        Path.Combine(Path.GetTempPath(), $"moa-task-{Process.GetCurrentProcess().Id}.xml");

    /// <summary>
    /// The arguments that register the task, which is the one place in this file that talks to a shell, and it
    /// is encoded rather than written out for a reason worth remembering:
    ///
    /// <c>-EncodedCommand</c> takes the script as base64, so nothing on the way to it can be mistaken for
    /// quoting. The first version of this passed the script as <c>-Command '...'</c>, and PowerShell read a
    /// command whose first character is a quote as a *string* - it evaluated it, printed it and exited 0 with
    /// nothing registered. The exit code said success, the program's own check said no task existed, and the
    /// dialog said the setting could not be changed. Nothing else would have shown it: the log had a zero.
    ///
    /// Internal for probe/autostart, which decodes it and checks the script that would run.
    /// </summary>
    internal static string RegisterArguments(string xmlPath)
    {
        // $ProgressPreference first, for two reasons: the cmdlet's module load emits progress records, and when
        // its output is redirected those arrive as CLIXML soup - which is what the user would be shown, verbatim,
        // in the "could not change the setting" dialog. And a script that begins with $ is one more step away
        // from the mistake described above.
        string script = "$ProgressPreference='SilentlyContinue'; " +
                        "Register-ScheduledTask -Force -TaskName " + PsQuote(TaskName) +
                        " -Xml (Get-Content -Raw -LiteralPath " + PsQuote(xmlPath) + ") | Out-Null";
        return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
               Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>
    /// Whether a command line says this copy was started by the logon task. The argument is compared
    /// case-insensitively, which costs nothing and is the difference between the setting working and a task
    /// that silently does not - the argument is written by this program, but nothing stops anyone editing it.
    /// </summary>
    public static bool SaysLogon(string[] args)
    {
        foreach (string arg in args)
            if (string.Equals(arg, LogonArgument, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// A value for inside an XML text node: the five characters that would otherwise end the text or the
    /// element. This program's own path and folder go into an XML document, and a path is not allowed to be
    /// able to break it.
    /// </summary>
    internal static string XmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    /// <summary>
    /// A value for inside a PowerShell single-quoted string, which is what every argument here is: a quote is
    /// escaped by doubling it, and nothing else is special. Single quotes rather than double because the XML
    /// this program builds is full of double quotes, and a shell that does not touch them is one less thing to
    /// be wrong about.
    /// </summary>
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Runs a program and decodes what it wrote by looking for a byte-order mark.
    ///
    /// Guessing an encoding is what produced the garbled dialogs earlier in this program, and here guessing
    /// would be worse than cosmetic: the XML check compares text, so mis-decoded XML would look exactly like a
    /// task that had been tampered with, and the feature would silently disable itself. schtasks writes its
    /// text forms in the console code page and its /XML form as UTF-16, and the BOM is the reliable way to tell
    /// them apart, so the raw bytes are read and decoded deliberately rather than left to a default.
    /// </summary>
    private static int Run(string file, string args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
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

            if (!p.WaitForExit(30000))
            {
                try { p.Kill(); } catch { }
                return -2;
            }
            output = Decode(ms.ToArray());
            // Every call is logged with its exit code and its own words. The behaviour of this feature depends
            // on the Task Scheduler in ways that cannot be reproduced on a machine that has no task, so the log
            // is the only way to see what actually happened.
            Log.Write($"{Path.GetFileName(file)} {args} -> rc={p.ExitCode} {output.Trim()}");
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

        // No mark. UTF-16 without one is common for redirected output, and reading it as single-byte text
        // leaves a NUL next to every character - that pattern is the tell. Getting this wrong is not cosmetic
        // here: the XML check compares text, so a mis-decoded answer looks exactly like a task that has been
        // tampered with, and the feature would silently disable itself.
        int nulAtOdd = 0, nulAtEven = 0, n = Math.Min(b.Length, 512);
        for (int i = 0; i < n; i++)
            if (b[i] == 0) { if ((i & 1) == 1) nulAtOdd++; else nulAtEven++; }
        if (nulAtOdd > n / 8 && nulAtEven == 0) return Encoding.Unicode.GetString(b);
        if (nulAtEven > n / 8 && nulAtOdd == 0) return Encoding.BigEndianUnicode.GetString(b);

        return Oem.GetString(b);
    }
}
