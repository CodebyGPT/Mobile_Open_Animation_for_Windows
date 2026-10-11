// Where the program is being run from, and whether that looks like a folder of its own.
//
// This program has no installer: it is unzipped somewhere and run, and the somewhere is usually Downloads. It
// also keeps everything it makes next to itself - the ini, its logs, and the Ads folder the advertisements are
// read from - so a folder that already holds the user's own files ends up with the program's files in the middle
// of them, and the next thing that happens is a support question about which of them is safe to delete.
//
// Nothing here refuses to run, moves anything or writes anything. It is a question with an answer, asked once at
// start-up, and Program.cs is what turns the answer into a box. Keeping it a function of a directory is also
// what makes it checkable: probe/folder puts real folders through it, including the ones that are meant to come
// out clean.
//
// The rule, in the order it is read: the folder is the wrong place for the program when
//
//   1. there is no MobileOpenAnimation.ini in it - the program has never run here, so this is where it landed
//      rather than where it lives - AND, at least one of
//   2. it holds a subfolder other than Ads, or
//   3. it holds a file that is neither the exe that is running nor one of the program's own logs.
//
// Two of those names are the program's own and one is not, and the difference is the whole of why this file has
// a Self() in it. The ini, the logs and the Ads folder are matched by name because those names are fixed and
// belong to the program, whatever the exe beside them is called. The exe cannot be: renaming it is exactly the
// thing a user does, and a rule that looked for a file called MobileOpenAnimation.exe would count a renamed
// copy as a foreign file in its own folder - firing on the one case it is meant to leave alone. So the exe is
// identified by asking Windows which file this process is, and the answer is used whatever the name in it is.
//
// The ini is the first question rather than one of three because it is the one that says the program has already
// been living here: the first run in a folder writes one, and from then on the folder has been accepted, however
// much else is in it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Moa;

internal static class FolderCheck
{
    /// <summary>
    /// The settings file, by name. The name does not follow the exe's: it is the program's, so a renamed copy
    /// finds the settings it already had rather than starting again beside them.
    /// </summary>
    public const string IniName = "MobileOpenAnimation.ini";

    /// <summary>
    /// The program's logs, by their two ends rather than by one name: the middle of the name is the build, which
    /// changes with every build, and a log left by an older build is still the program's own file.
    /// </summary>
    public const string LogPrefix = "MobileOpenAnimation-", LogSuffix = ".log";

    /// <summary>
    /// The exe that is running, whatever it has been renamed to, or an empty string when Windows will not say.
    ///
    /// Asked of the process first, which is the real file on disk, and of the managed entry point second, which
    /// is the same file for a program built like this one and is there so that the answer does not depend on a
    /// single call.
    /// </summary>
    public static string Self()
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) return exe!;
        }
        catch { }
        try { return Assembly.GetEntryAssembly()?.Location ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Why <paramref name="folder" /> is the wrong place for the program, in a sentence for the log, or null
    /// when it is fine. <paramref name="self" /> is the running exe, from <see cref="Self" />; a caller that
    /// cannot answer gives an empty string, and the exe is then not recognised among the files - the one cost of
    /// that being a folder that holds nothing but the program reading as crowded.
    /// </summary>
    public static string? Why(string folder, string self)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        try
        {
            // 1. Never run here before, so this is where it was put rather than where it lives.
            if (File.Exists(Path.Combine(folder, IniName))) return null;

            // 2 and 3, which is the answer when either of them found anything.
            var folders = Directory.GetDirectories(folder)
                                   .Where(d => !Named(d, Ads.FolderName))
                                   .ToList();
            var files = Directory.GetFiles(folder)
                                 .Where(f => !IsOurs(f, self))
                                 .ToList();
            if (folders.Count == 0 && files.Count == 0) return null;

            var said = new List<string>();
            if (folders.Count > 0)
                said.Add($"{folders.Count} other {(folders.Count == 1 ? "folder" : "folders")} " +
                         $"({string.Join(", ", folders.Take(3).Select(Path.GetFileName))})");
            if (files.Count > 0)
                said.Add($"{files.Count} other {(files.Count == 1 ? "file" : "files")} " +
                         $"({string.Join(", ", files.Take(3).Select(Path.GetFileName))})");
            return $"no {IniName} in it, and {string.Join(", and ", said)}";
        }
        catch
        {
            // A folder that cannot be listed is not a verdict about it. Nothing is said and the program carries
            // on exactly as it did before this check existed - which is also what happens when the answer is
            // that the folder is fine.
            return null;
        }
    }

    /// <summary>Whether a path's own name is this name, the way Windows compares them.</summary>
    private static bool Named(string path, string name)
        => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a file is one of the program's own: the exe that is running, or one of its logs.
    ///
    /// Compared as whole paths rather than as names, so that it is this file and not merely a file called that.
    /// </summary>
    private static bool IsOurs(string path, string self)
    {
        if (!string.IsNullOrEmpty(self) && SameFile(path, self)) return true;
        string name = Path.GetFileName(path);
        return name.StartsWith(LogPrefix, StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(LogSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two paths naming one file, or false when either cannot be resolved at all.</summary>
    private static bool SameFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
