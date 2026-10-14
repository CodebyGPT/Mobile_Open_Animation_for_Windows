// Registering the start-up launcher
// =================================
//
// What this writes
// ----------------
// One batch file in the current user's own Startup folder:
//
//   %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\
//     Mobile Open Animation for Windows.cmd
//
//     @echo off
//     start "" "<exe>"
//     exit
//
// Explorer starts whatever is in that folder at logon, and this needs no rights of its own to be started: a text
// file, executed by cmd.exe, naming an ordinary executable. The program it starts is the one that asks Windows
// for elevation - see Program.SelfElevate, and the asInvoker manifest that lets it exist unelevated at all.
//
// Every other way of doing this was tried on the machine this was written for, and what each of them did is the
// reason this is what is left:
//
//   * a scheduled task with HighestAvailable is started by the Task Scheduler service and needs no consent -
//     and the machine's Task Scheduler hands it a **low** integrity token with the elevation *flag* set, so
//     nothing the program can ask about the token refuses it, and UIPI then refuses the tray icon and every
//     window. (Watt Toolkit registers a task with these same fields; it works for most people, and it does not
//     work here.)
//   * a Startup-folder shortcut, and a value under HKCU\Run naming the program directly, both needed the *shell*
//     to raise the consent prompt for an elevation-requiring program - and both produced no logon start, no
//     prompt and no trace at all.
//   * a value under HKCU\Run holding a hidden-PowerShell launcher, which needs no elevation itself and therefore
//     cannot have been refused on that ground, produced no start and no prompt either. On that machine HKCU\Run
//     is simply not processed (another program's value there does not start either), which is why this is a file
//     in the Startup folder and not a value in the registry.
//   * a .vbs would avoid the black window this batch file flashes, and it is out: VBScript is deprecated, an
//     optional Windows feature now, with removal planned.
//   * a .lnk would avoid it too and is the tidier answer, but writing one needs COM. If the flash ever becomes
//     worth that, it is the change to make; the launcher would be a shortcut to this same executable.
//
// The mechanism is the prompt, not a way around it
// ------------------------------------------------
// This program cannot hide another process's window unless it holds rights UIPI will accept, so a copy that is
// not elevated can do nothing at all - it asks and leaves. That is the design, and this file is the half that
// needs no rights: getting a copy started, unelevated, so that the program itself can ask.
//
// No argument is passed, and there used to be one. It marked a copy started at logon so that the program could
// play a logon advertisement before asking for elevation, which is now gone - so nothing has to tell a logon
// start from a hand start any more, and the launcher is one line naming the program.
//
// One thing this does not handle, and it is worth knowing rather than discovering
// ------------------------------------------------------------------------------
// The Startup folder here is the elevated process's user, which is the same person in the ordinary case - the
// user is prompted, clicks Yes, and the copy that continues is theirs. It is not the same person when an
// administrator elevates for somebody else (over-the-shoulder elevation): the file would be written into the
// administrator's Startup folder and the other user's logon would not run it. Writing into the *interactive*
// user's folder from an elevated process means reaching through their token or through the shell, which is a
// real change rather than this one.
//
// The state, and what the menu shows
// ----------------------------------
// The file's own text. It is this program's when it names this executable; nothing there, or something else's
// file under our name, counts as off - and the next click writes ours over it, because the click means "start
// this program at logon", which is what every other program's Start with Windows means too.
//
// The rule the caller depends on
// ------------------------------
// Apply reports the state that actually holds afterwards, not the state that was requested. The caller updates
// the menu from that answer, so a write that failed leaves the check where it was rather than lying about it.

using System;
using System.IO;
using System.Text;

namespace Moa;

internal static class AutoStart
{
    /// <summary>The launcher's name, which is what the user sees in their Startup folder.</summary>
    private const string ScriptName = "Mobile Open Animation for Windows.cmd";

    /// <summary>The folder Windows looks in for "start this when I log on", for this user.</summary>
    private static string StartupFolder() => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    /// <summary>
    /// The launcher, as it is written.
    ///
    /// The `rem` lines are not decoration: a file in a Startup folder that starts a program is also the shape of
    /// something a user did not put there, so the first thing in it says what it is and how to stop it. The third
    /// one says who asks for the rights, because the answer is not this file: the program does, as it starts.
    ///
    /// No argument is passed. There used to be one - the marker that told a copy started at logon from a copy the
    /// user started themselves, which is what the logon advertisement hung on - and the advertisement is gone.
    /// </summary>
    internal static string ScriptText() => ScriptFor(Compat.ExePath);

    /// <summary>
    /// The same launcher for a program somewhere else. Internal for probe/autostart, which puts cmd.exe's own
    /// special character - a percent sign, which a batch file expands - into a path and checks that it is
    /// written the way a batch file needs it.
    ///
    /// It deletes itself when the program it names is not there any more, which is what keeps a launcher from
    /// outliving what it launches. A file in a Startup folder that runs a program that has been moved or
    /// uninstalled is a file that fails at every logon with a console window nobody asked for and nothing else,
    /// and there is no one to tell: the thing that would have said something about it is the program that is not
    /// there. So the launcher says it once, by going away - and the tray menu, which reads this folder on every
    /// right click, then shows Start with Windows as off, which is now true.
    ///
    /// The test and the start both name the same path, escaped the same way, and the test comes first.
    /// </summary>
    internal static string ScriptFor(string exePath)
    {
        // cmd.exe's own escaping, once, so that the path in the test is the same string as the path in the
        // start: two spellings of it would be two things to keep in step, and only one of them is checked.
        string command = CmdEscape("\"" + exePath + "\"");

        return
            "@echo off\r\n" +
            "rem Mobile Open Animation for Windows - starts the program at logon.\r\n" +
            "rem Written by the program's own tray menu. Delete this file to stop it starting at logon.\r\n" +
            "rem The program asks Windows for administrator rights itself, as it starts.\r\n" +
            "rem If the program it names is gone - moved, renamed, uninstalled - it removes itself and does\r\n" +
            "rem nothing else, so that a launcher cannot outlive what it launches.\r\n" +
            "if not exist " + command + " (\r\n" +
            "    del \"%~f0\"\r\n" +
            "    exit /b\r\n" +
            ")\r\n" +
            "start \"\" " + command + "\r\n" +
            "exit\r\n";
    }

    /// <summary>
    /// A value for inside a batch file: a percent sign is doubled, because a batch file expands one and a path is
    /// allowed to contain one. Nothing else needs anything - this text sits inside double quotes for cmd.exe,
    /// where its other special characters are literal.
    /// </summary>
    private static string CmdEscape(string value) => value.Replace("%", "%%");

    /// <summary>
    /// A value for inside a PowerShell single-quoted string: a quote is escaped by doubling it, and nothing else
    /// is special. Single quotes rather than double because the command line around this already uses double
    /// quotes, and a layer that does not touch them is one less thing to be wrong about.
    /// </summary>
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
    /// <summary>
    /// What is in the Startup folder, judged against the hash of what this program wrote there.
    ///
    /// A hash rather than a reading of the text, and that is a change of kind rather than of detail. The text used
    /// to be recognised by containing this program's path, which answers "would this start the program" and
    /// nothing about whether it is still the file this program wrote: a launcher somebody edited - another
    /// command, an argument added, a line appended - starts the program and is not the program's file, and the
    /// menu went on claiming it. What the menu means by on is "what this program put there is still there", and
    /// only the contents can answer that.
    /// </summary>
    public enum State
    {
        /// <summary>Nothing is there under that name.</summary>
        Nothing,

        /// <summary>There, with the contents this program recorded: the launcher is untouched.</summary>
        Ours,

        /// <summary>
        /// There, and not the contents this program recorded - or nothing was recorded.
        ///
        /// One state rather than three, because a file somebody else wrote, a file that has been edited, and a
        /// file whose record is gone (a deleted ini, a machine whose settings were reset) cannot be told apart
        /// from here and do not need to be: in all three the folder holds something this program did not put
        /// there, so the menu must not claim it and writing over it is the user's call rather than the program's.
        /// </summary>
        Changed,
    }

    /// <summary>
    /// What is in the Startup folder, given the hash of what this program last wrote there - "" when it has never
    /// written one, which is the state of a machine that has just had its settings reset and is read as "not
    /// ours" on purpose.
    ///
    /// Called on every right click, and it is one file read and one hash of a few hundred bytes.
    /// </summary>
    public static State Look(string recordedHash, out string why) => Look(StartupFolder(), recordedHash, out why);

    /// <summary>
    /// The same question about a folder somewhere else, which is how probe/autostart asks it: writing into the
    /// real Startup folder is writing into the user's own logon, so the probe puts its file in a folder of its
    /// own and hands the path in. The folder is the only thing it can vary; the text is always this program,
    /// because that is what Compat.ExePath is.
    /// </summary>
    internal static State Look(string folder, string recordedHash, out string why)
    {
        string path = Path.Combine(folder, ScriptName);
        string now = Hash(path);

        if (now.Length == 0)
        {
            why = $"nothing at {path}";
            return State.Nothing;
        }
        if (recordedHash.Length == 0)
        {
            why = $"{path} is there but this program has no record of writing it, so it is not ours";
            return State.Changed;
        }
        if (!string.Equals(now, recordedHash, StringComparison.OrdinalIgnoreCase))
        {
            why = $"{path} is there and its contents are not the ones this program wrote " +
                  $"({now.Substring(0, 8)} now, {recordedHash.Substring(0, 8)} recorded), so it is not ours";
            return State.Changed;
        }

        why = $"{path} is this program's launcher, unedited";
        return State.Ours;
    }

    /// <summary>
    /// Turns the start-up launcher on or off, then reports what actually holds afterwards.
    /// </summary>
    /// <param name="wanted">True to write the launcher, false to delete it.</param>
    /// <param name="written">The hash of the file that ended up there, or "" when there is none: what the caller
    /// records in the ini, and the only thing that lets the next look tell this file from an edited one.</param>
    /// <param name="error">Empty on success, otherwise something worth showing the user.</param>
    /// <returns>The state that holds after the attempt, which the caller should trust over
    /// <paramref name="wanted"/>.</returns>
    public static bool Apply(bool wanted, out string written, out string error)
        => Apply(StartupFolder(), wanted, out written, out error);

    /// <summary>
    /// The same, about a folder somewhere else. Internal for probe/autostart, which must not write into the
    /// user's real Startup folder to find out whether this works.
    /// </summary>
    internal static bool Apply(string folder, bool wanted, out string written, out string error)
    {
        error = "";
        string path = Path.Combine(folder, ScriptName);

        try
        {
            if (wanted)
            {
                Directory.CreateDirectory(folder);
                // No byte-order mark: cmd.exe reads batch files as the console code page and a mark would be a
                // command it tries to run. The text is ASCII in every case this program writes.
                File.WriteAllText(path, ScriptText(), new UTF8Encoding(false));
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Write($"autostart: wanted={wanted} at {path} threw {error}");
        }

        // What is true, not what was asked for: the file is hashed as it now is, and the hash is the answer. The
        // caller stores it, so a write that quietly produced something else - a mapped folder, a filter driver,
        // anything between here and the disk - is not recorded as the file this program expects.
        written = Hash(path);
        bool now = written.Length > 0;
        Log.Write($"autostart: wanted={wanted} now={now} file={path} " +
                  $"hash={(written.Length == 0 ? "-" : written.Substring(0, 8))}");
        if (now != wanted && error.Length == 0)
            error = $"The file at {path} did not end up in the state that was asked for.";
        return now;
    }

    /// <summary>
    /// The SHA-256 of a file, lowercase hex, or "" when it is not there or cannot be read.
    ///
    /// Recomputed every time rather than cached: the whole point of it is to notice a file that changed while
    /// this program was not looking, and a cache is one more thing that would have to be told.
    /// </summary>
    internal static string Hash(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] digest = sha.ComputeHash(stream);
            var hex = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
        catch (Exception ex)
        {
            Log.Write($"autostart: {path} could not be hashed ({ex.GetType().Name}: {ex.Message})");
            return "";
        }
    }
}
