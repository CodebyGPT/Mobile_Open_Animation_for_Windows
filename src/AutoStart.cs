// Registering the start-up shortcut
// =================================
//
// What this writes
// ----------------
// One .lnk in the user's own Startup folder, pointing at this executable:
//
//   C:\Users\<user>\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\
//
// which is the place Windows itself looks for "start this when the user logs on", and the only one
// that is visible where a user would look for it: Task Manager's Start-up tab lists it, Explorer
// shows it as a shortcut that can be deleted, and it is per-user by construction rather than by a
// flag.
//
// The shortcut starts the program with <see cref="LogonArgument" />, and that argument is the whole
// of how a copy started at logon is told from a copy the user started themselves. Nothing else can
// tell them apart: Explorer is the parent of both, the program's own path is the same, and the clock
// is no help - the logon launch can sit behind a consent prompt for as long as the user leaves it
// there, and a start ten minutes after logon is then indistinguishable from a start at logon. See
// Program.StartedAtLogon, which is what plays the logon advertisement.
//
// What it replaces, and why
// -------------------------
// A scheduled task, created through schtasks with an ONLOGON trigger and /RL HIGHEST. The point of
// the task was that "run with highest privileges" is the one way to start an elevated program at
// logon *without* a UAC prompt - and that turned out to be the wrong default to pick on the user's
// behalf. A program that starts by itself, elevated, with nothing shown anywhere the user was
// looking, is indistinguishable from the thing malware does; the task also cannot be seen or removed
// from anywhere except the Task Scheduler, and this program can create it without the user ever
// having agreed to anything more than a menu click.
//
// So the user asked for the prompt back, and for the mechanism to be one they can see: a shortcut in
// the Startup folder, and UAC asking them at logon, which is exactly what the user's own answer to
// "do you want this to run elevated" should look like.
//
// Why the shortcut needs no "run as administrator" flag of its own
// ---------------------------------------------------------------
// It does not: this executable's manifest already asks for requireAdministrator (see
// src/app.manifest), so the shell shows the consent prompt for *any* launch of it, this one
// included, and the user clicks it. Setting the shortcut's own RunAs bit as well would be the same
// prompt by a second route, and it is not a property WScript.Shell's object model has: the file
// format keeps it in a flags byte in the header that has to be patched by hand, which is a piece of
// .lnk binary layout to get wrong for no behaviour at all.
//
// Why WScript.Shell and not the .lnk format
// -----------------------------------------
// Because a shortcut is not a text file. A .lnk is a header, an item ID list, a LinkInfo block and
// an optional set of string blocks, with a target that is stored twice and in two encodings and a
// relative-path blob for when the target moves. Writing one by hand is a hundred lines of binary
// layout that only Windows can validate; asking the shell to write it is four property assignments
// and cannot be subtly wrong.
//
// The three states a shortcut can be in
// -------------------------------------
// Not there, ours, ours-but-out-of-date, or somebody else's - and the three are acted on differently
// rather than squashed into a bool. Ours is what the menu shows. Somebody else's is what the user is
// asked about before it is replaced. Ours-but-out-of-date is repaired without a word, because it is
// this program's own file: the only way it happens is a shortcut written by an older build, and
// asking the user to approve rewriting our own file is a question with one sensible answer.
//
// The rule the caller depends on
// ------------------------------
// Apply reports the state that actually holds afterwards, not the state that was requested. The
// caller updates the menu from that answer, so a failed write or delete leaves the check where it
// was rather than lying about it.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Moa;

internal static class AutoStart
{
    /// <summary>The shortcut's file name. Also what the user sees in Task Manager's Start-up tab.</summary>
    private const string ShortcutName = "Mobile Open Animation for Windows.lnk";

    /// <summary>
    /// What this program's own start-up shortcut passes, and the only thing that distinguishes the copy
    /// Windows starts at logon from a copy the user started by hand. See Program.StartedAtLogon.
    ///
    /// It is not a switch the user is meant to type: the Debug menu's entry plays a logon advertisement
    /// whenever it is asked to, and this is what the shortcut does instead of that. Nothing else reads it.
    /// </summary>
    public const string LogonArgument = "--logon";

    /// <summary>
    /// Where the shortcut lives: the current user's Startup folder, which is
    /// <c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</c>.
    ///
    /// Asked of the shell's own folder list rather than built from the environment, because Windows
    /// moves that folder for a redirected or roaming profile - and it is the folder Windows reads, so
    /// it is the one to ask.
    /// </summary>
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    // The .lnk stores its target, and its arguments, in the console code page as well as in UTF-16;
    // see What.
    private static readonly Encoding Oem =
        Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    /// <summary>What a shortcut under this program's name turned out to be.</summary>
    private enum Kind
    {
        /// <summary>Nothing is there.</summary>
        Nothing,

        /// <summary>This program, started with the argument that makes it a logon start.</summary>
        Ours,

        /// <summary>This program, written before the shortcut carried that argument.</summary>
        OutOfDate,

        /// <summary>A shortcut under our name that starts something else.</summary>
        Foreign,
    }

    /// <summary>
    /// True only when the shortcut is there and is the one this program would write.
    ///
    /// A shortcut that is present but points somewhere else counts as absent, which is what makes the
    /// next enable replace it - the same direction the scheduled task's field check took, and for
    /// the same reason: a false "absent" costs one redundant write, while a false "present" leaves the
    /// menu claiming a state this program does not control.
    ///
    /// Called on every right click, which is why the whole check is one file read: the version this
    /// replaces spawned schtasks twice and cost tens of milliseconds before the menu could appear.
    /// </summary>
    public static bool IsEnabled() => IsEnabled(ShortcutPath);

    /// <summary>
    /// The same question about a shortcut somewhere else, which is how probe/autostart asks it: writing
    /// into the real Startup folder is writing into the user's own logon, so the probe puts its shortcut
    /// in a folder of its own and hands the path in. The path is the only thing it can vary; the target
    /// is always this executable, because that is what Compat.ExePath is.
    /// </summary>
    internal static bool IsEnabled(string path)
    {
        Kind kind = What(path, out string why);
        Log.Write($"autostart: {why}");
        return kind == Kind.Ours;
    }

    /// <summary>
    /// Whether an enable would replace a shortcut that starts something this program did not put there.
    ///
    /// False for a shortcut of this program's own that is merely out of date: that one is repaired without
    /// asking. True only for a file that names another program - or for one that cannot be read at all,
    /// which is the same thing from here and is the answer that asks rather than the one that overwrites.
    /// </summary>
    public static bool Conflicts() => Conflicts(ShortcutPath);

    internal static bool Conflicts(string path) => What(path, out _) == Kind.Foreign;

    /// <summary>
    /// Brings this program's own shortcut up to date when it is out of date, and does nothing in every
    /// other case.
    ///
    /// Called once at start-up, before the menu is drawn and before the logon advertisement is considered,
    /// so that both see the shortcut that is really there. The state it exists for is a shortcut written by
    /// a build from before the shortcut carried the logon argument: it starts the program and the program
    /// then plays no advertisement, which is a silent difference between two builds that a user has no way
    /// to see. Rewriting our own file is not a question to ask, and it is the one thing here that writes
    /// without being clicked.
    /// </summary>
    public static void Repair()
    {
        string path = ShortcutPath;
        if (What(path, out string why) != Kind.OutOfDate) return;

        Log.Write($"autostart: {why}");
        bool now = Apply(path, true, out string error);
        Log.Write($"autostart: repaired the shortcut ({now}{(error.Length == 0 ? "" : ", " + error)})");
    }

    /// <summary>
    /// Whether a command line says this copy was started by the start-up shortcut. The argument is compared
    /// case-insensitively, which costs nothing and is the difference between the setting working and a
    /// shortcut that silently does not - the argument is written by this program, but nothing stops anyone
    /// editing the shortcut.
    /// </summary>
    public static bool SaysLogon(string[] args)
    {
        foreach (string arg in args)
            if (string.Equals(arg, LogonArgument, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Which of the three things a shortcut is, and the sentence for the log that says how it was decided.
    ///
    /// Its target is read out of the file's own bytes rather than through the shell: asking the shell to
    /// resolve a shortcut means COM and, for the question actually being asked, means a shortcut whose
    /// target has been deleted resolving to somewhere else entirely.
    ///
    /// Both the target and the argument are in there twice - as ANSI in the LinkInfo block, which is what
    /// resolves it, and as UTF-16 in the string blocks, which is what the property sheet shows - so both
    /// encodings are looked for and either one is enough. Whichever of the two a writer filled in, the
    /// answer is the same; requiring one of them would make the check depend on which shell version wrote
    /// the file.
    /// </summary>
    private static Kind What(string path, out string why)
    {
        if (!File.Exists(path))
        {
            why = $"no shortcut at {path}";
            return Kind.Nothing;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            why = $"the shortcut at {path} could not be read ({ex.GetType().Name}: {ex.Message})";
            return Kind.Foreign;
        }

        string exe = Compat.ExePath;
        if (exe.Length == 0 || !(Holds(bytes, exe) || Holds(bytes, exe, Oem)))
        {
            why = $"the shortcut at {path} exists but does not name this program (looking for \"{exe}\" " +
                  $"in {bytes.Length} bytes), so it counts as absent";
            return Kind.Foreign;
        }

        if (!Holds(bytes, LogonArgument) && !Holds(bytes, LogonArgument, Oem))
        {
            why = $"the shortcut at {path} is this program but does not carry {LogonArgument}, so it is " +
                  "out of date and will be written again";
            return Kind.OutOfDate;
        }

        why = $"the shortcut at {path} is this program, started at logon";
        return Kind.Ours;
    }

    private static bool Holds(byte[] bytes, string text) => Holds(bytes, text, Encoding.Unicode);

    private static bool Holds(byte[] bytes, string text, Encoding encoding)
    {
        byte[] needle = encoding.GetBytes(text);
        if (needle.Length == 0 || bytes.Length < needle.Length) return false;
        for (int i = 0; i + needle.Length <= bytes.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && bytes[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    /// <summary>
    /// Turns the start-up shortcut on or off, then reports the state that actually holds afterwards.
    /// </summary>
    /// <param name="wanted">True to write the shortcut, false to delete it.</param>
    /// <param name="error">Empty on success, otherwise something worth showing the user.</param>
    /// <returns>The state that holds after the attempt, which the caller should trust over
    /// <paramref name="wanted"/>.</returns>
    public static bool Apply(bool wanted, out string error) => Apply(ShortcutPath, wanted, out error);

    /// <summary>
    /// The same, about a shortcut somewhere else. Internal for probe/autostart, which must not write into
    /// the user's real Startup folder to find out whether this works.
    /// </summary>
    internal static bool Apply(string path, bool wanted, out string error)
    {
        error = "";

        try
        {
            if (wanted) Write(path);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Write($"autostart: wanted={wanted} at {path} threw {error}");
        }

        // What is true, not what was asked for. Written synchronously, so a single read is enough:
        // unlike the task, there is no queue between the write and the query.
        bool now = IsEnabled(path);
        Log.Write($"autostart: wanted={wanted} now={now} shortcut={path}");
        if (now != wanted && error.Length == 0)
            error = $"The shortcut at {path} did not end up in the state that was asked for.";
        return now;
    }

    /// <summary>
    /// Writes the shortcut, through the shell's own shortcut object.
    ///
    /// The working directory is set along with the target because a shortcut with no working directory
    /// starts the program in whatever directory the shell defaulted to, and this program reads and
    /// writes an ini beside its own executable - so the one thing a start-up launch must not do is
    /// leave the current directory to chance.
    ///
    /// The COM objects are released rather than left to the finalizer: this runs on the message loop
    /// in an elevated process, and an RCW dropping on a later thread is a thing to not have to reason
    /// about. Both are held as plain objects so that they can be, with the member access done through
    /// the dynamic binder in between.
    /// </summary>
    private static void Write(string path)
    {
        Type? type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) throw new InvalidOperationException("WScript.Shell is not registered on this machine");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        object shell = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("WScript.Shell could not be created");
        object? link = null;
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
            shortcut.TargetPath = Compat.ExePath;
            shortcut.Arguments = LogonArgument;
            shortcut.WorkingDirectory = Path.GetDirectoryName(Compat.ExePath) ?? "";
            shortcut.Save();
            link = (object)shortcut;
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(shell);
        }
    }
}
