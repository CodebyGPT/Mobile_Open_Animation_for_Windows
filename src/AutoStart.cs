// Registering the start-up entry
// ==============================
//
// What this writes
// ----------------
// One value in the current user's own part of the registry:
//
//   HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
//     "Mobile Open Animation for Windows" = "C:\...\MobileOpenAnimation.exe" --logon
//
// which is the other place Windows looks for "start this when the user logs on", and the one that is
// harder to interfere with: the contents of the Startup folder are the shell's to enable or disable
// (Windows 11 has a whole settings page for it, Task Manager's Start-up tab can switch one off, and
// anything that watches that folder can delete a file from it), while a Run value is a registry value
// the user owns, visible in regedit and readable by nothing that is not running as them.
//
// What it replaces, and why this is the third mechanism
// -----------------------------------------------------
// A scheduled task first: an ONLOGON trigger with /RL HIGHEST, which is the one way to start an
// elevated program at logon *without* a UAC prompt, and was given up deliberately because a program
// that starts itself, elevated, with nothing shown and nothing the user can see or remove is
// indistinguishable from what malware does.
//
// Then a shortcut in the Startup folder, so that UAC would ask. It was reported as blocked at logon -
// and the log agrees: the build that carried it has manual starts in it and not one logon start. So
// this writes the registry value instead, which is what the user asked for, and it is shorter: no
// shell object, no COM, no file format, two registry calls.
//
// Neither of those builds is published any more, and that is why there is no migration code here: the
// shortcut cannot be on anybody's machine, because nobody could download the build that wrote one. The
// one machine that had it was the one it was built on, and the file was deleted by hand.
//
// The logon argument
// ------------------
// The value starts the program with <see cref="LogonArgument" />, and that argument is the whole of
// how a copy started at logon is told from a copy the user started themselves. Nothing else can tell
// them apart: Explorer is the parent of both, the program's own path is the same, and the clock is no
// help - the logon launch can sit behind a consent prompt for as long as the user leaves it there, and
// a start ten minutes after logon is then indistinguishable from a start at logon. See
// Program.StartedAtLogon, which is what plays the logon advertisement.
//
// UAC is not changed by any of this
// ---------------------------------
// A Run value is started by the shell like anything else, so this executable's manifest
// (requireAdministrator, see src/app.manifest) is honoured: Windows shows the consent prompt and the
// program is elevated if the user accepts it. Nothing here starts it elevated *without* that prompt.
// The one mechanism that could was the scheduled task, and that is exactly what this program gave up.
//
// One thing this does not handle, and it is worth knowing rather than discovering
// ------------------------------------------------------------------------------
// HKCU here is the elevated process's user, which is the same person in the ordinary case - the user
// is prompted, clicks Yes, and the copy that continues is theirs. It is not the same person when an
// administrator elevates for somebody else (over-the-shoulder elevation): the value would be written
// into the administrator's hive, and the other user's logon would not see it. Writing into the
// *interactive* user's hive from an elevated process means reaching through their token or through the
// shell, which is a real change rather than this one. The Start-up shortcut this replaced had the same
// hole, for the same reason - Environment.SpecialFolder.Startup is the elevated user's folder.
//
// The three states a value can be in
// ----------------------------------
// Not there, ours, ours-but-out-of-date, or somebody else's - and the three are acted on differently
// rather than squashed into a bool. Ours is what the menu shows. Somebody else's is what the user is
// asked about before it is replaced. Ours-but-out-of-date is not asked about, because it is this
// program's own entry: it happens when the argument is edited out of the value by hand, and the next
// time the switch is turned on it is simply written again.
//
// There is no migration code, and that is deliberate rather than an oversight: there has never been a
// published build that wrote a Run value, so there is nothing on any machine to migrate from. The one
// machine with a Startup-folder shortcut from the mechanism this replaces was the one it was built on,
// and that file is gone by hand rather than by a function that would run on every start forever.
//
// The rule the caller depends on
// ------------------------------
// Apply reports the state that actually holds afterwards, not the state that was requested. The caller
// updates the menu from that answer, so a failed write or delete leaves the check where it was rather
// than lying about it.

using System;
using Microsoft.Win32;

namespace Moa;

internal static class AutoStart
{
    /// <summary>The value's name, which is what the user sees in regedit and in Task Manager.</summary>
    private const string ValueName = "Mobile Open Animation for Windows";

    /// <summary>The key Windows reads for "start this when the user logs on", under HKEY_CURRENT_USER.</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// What this program's own entry passes, and the only thing that distinguishes the copy Windows starts
    /// at logon from a copy the user started by hand. See Program.StartedAtLogon.
    ///
    /// It is not a switch the user is meant to type: the Debug menu's entry plays a logon advertisement
    /// whenever it is asked to, and this is what the registry entry does instead of that. Nothing else reads
    /// it.
    /// </summary>
    public const string LogonArgument = "--logon";

    /// <summary>
    /// The whole of what is written, as the string the user would see in regedit: the program's path quoted,
    /// because it may contain spaces, and the argument after it.
    /// </summary>
    public static string Command => $"\"{Compat.ExePath}\" {LogonArgument}";

    /// <summary>What an entry under this program's name turned out to be.</summary>
    private enum Kind
    {
        /// <summary>Nothing is there.</summary>
        Nothing,

        /// <summary>This program, started with the argument that makes it a logon start.</summary>
        Ours,

        /// <summary>This program, with that argument edited out of the value by hand.</summary>
        OutOfDate,

        /// <summary>A value under our name that starts something else.</summary>
        Foreign,
    }

    /// <summary>
    /// True only when the entry is there and is the one this program would write.
    ///
    /// An entry that is present but starts something else counts as absent, which is what makes the
    /// next enable replace it - the direction every version of this has taken, and for the same reason:
    /// a false "absent" costs one redundant write, while a false "present" leaves the menu claiming a
    /// state this program does not control.
    ///
    /// Called on every right click, and it is one registry read: the version before the shortcut spawned
    /// schtasks twice and cost tens of milliseconds before the menu could appear.
    /// </summary>
    public static bool IsEnabled() => IsEnabled(RunKeyPath);

    /// <summary>
    /// The same question about a key somewhere else, which is how probe/autostart asks it: writing into the
    /// real Run key is writing into the user's own logon, so the probe puts its value in a key of its own
    /// and hands the path in. The path is the only thing it can vary; the data is always this program,
    /// because that is what Compat.ExePath is.
    /// </summary>
    internal static bool IsEnabled(string keyPath)
    {
        Kind kind = What(keyPath, out string why);
        Log.Write($"autostart: {why}");
        return kind == Kind.Ours;
    }

    /// <summary>
    /// Whether an enable would replace an entry that starts something this program did not put there.
    ///
    /// False for an entry of this program's own that is merely out of date: that one is written again
    /// without asking. True only for a value that names another program - or for one that cannot be read at
    /// all, which is the same thing from here and is the answer that asks rather than the one that overwrites.
    /// </summary>
    public static bool Conflicts() => Conflicts(RunKeyPath);

    internal static bool Conflicts(string keyPath) => What(keyPath, out _) == Kind.Foreign;

    /// <summary>
    /// Whether a command line says this copy was started by the start-up entry. The argument is compared
    /// case-insensitively, which costs nothing and is the difference between the setting working and an
    /// entry that silently does not - the value is written by this program, but nothing stops anyone editing
    /// it.
    /// </summary>
    public static bool SaysLogon(string[] args)
    {
        foreach (string arg in args)
            if (string.Equals(arg, LogonArgument, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Turns the start-up entry on or off, then reports the state that actually holds afterwards.
    /// </summary>
    /// <param name="wanted">True to write the value, false to delete it.</param>
    /// <param name="error">Empty on success, otherwise something worth showing the user.</param>
    /// <returns>The state that holds after the attempt, which the caller should trust over
    /// <paramref name="wanted"/>.</returns>
    public static bool Apply(bool wanted, out string error) => Apply(RunKeyPath, wanted, out error);

    /// <summary>
    /// The same, about a key somewhere else. Internal for probe/autostart, which must not write into the
    /// user's real Run key to find out whether this works.
    /// </summary>
    internal static bool Apply(string keyPath, bool wanted, out string error)
    {
        error = "";

        try
        {
            if (wanted) Write(keyPath, Command);
            else Remove(keyPath);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Write($"autostart: wanted={wanted} at {keyPath} threw {error}");
        }

        // What is true, not what was asked for. Written synchronously, so a single read is enough: unlike the
        // task, there is no queue between the write and the query.
        bool now = IsEnabled(keyPath);
        Log.Write($"autostart: wanted={wanted} now={now} key=HKCU\\{keyPath}");
        if (now != wanted && error.Length == 0)
            error = $"The entry at HKCU\\{keyPath}\\{ValueName} did not end up in the state that was asked for.";
        return now;
    }

    /// <summary>
    /// Which of the three things an entry under our name is, and the sentence for the log that says how it
    /// was decided.
    ///
    /// The value is recognised by containing the two things this program writes into it - its own path and
    /// the logon argument - rather than by being parsed as a command line. A parser would tell "this program
    /// with an argument" from "this program named as somebody else's argument", which is not a case worth a
    /// parser for a string this program writes itself; a value that mentions our path without being the
    /// program has to be deliberately made up, and the worst it costs is that such a value is treated as ours
    /// and written over - which is what the switch being clicked asks for anyway.
    ///
    /// A value with the right path but no argument is "out of date" rather than "not ours", and the two are
    /// kept apart because they are different questions: ours-without-the-argument is written again without
    /// asking, and anything else gets the overwrite question. See Conflicts.
    /// </summary>
    private static Kind What(string keyPath, out string why)
    {
        string command = Read(keyPath);
        if (command.Length == 0)
        {
            why = $"nothing at HKCU\\{keyPath}\\{ValueName}";
            return Kind.Nothing;
        }

        string exe = Compat.ExePath;
        if (exe.Length == 0 || command.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0)
        {
            why = $"HKCU\\{keyPath}\\{ValueName} is \"{command}\", which does not start this program " +
                  $"(looking for \"{exe}\"), so it counts as absent";
            return Kind.Foreign;
        }

        if (command.IndexOf(LogonArgument, StringComparison.OrdinalIgnoreCase) < 0)
        {
            why = $"HKCU\\{keyPath}\\{ValueName} is this program but does not carry {LogonArgument}, so it is " +
                  "out of date and will be written again";
            return Kind.OutOfDate;
        }

        why = $"HKCU\\{keyPath}\\{ValueName} is this program, started at logon";
        return Kind.Ours;
    }

    /// <summary>The value's data, or an empty string when the key or the value is not there.</summary>
    private static string Read(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) as string ?? "";
        }
        catch (Exception ex)
        {
            // A key that cannot be read and a value that is not there are different things, and the log is
            // where the difference gets to matter: the first has a cause worth reading.
            Log.Write($"autostart: HKCU\\{keyPath} could not be read ({ex.GetType().Name}: {ex.Message})");
            return "";
        }
    }

    private static void Write(string keyPath, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"HKCU\\{keyPath} could not be opened for writing");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    /// <summary>
    /// Takes the value out, and leaves the key alone: Run is a key of the user's own with other people's
    /// entries in it - this machine has one for Coodesker - so deleting it is not this program's to do. An
    /// empty value is what "off" is, which is exactly how every other program that has ever been switched off
    /// there leaves it.
    /// </summary>
    private static void Remove(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
