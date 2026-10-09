// The build's own identity, written into the assembly by build.ps1 at compile time: when it was built and the
// commit it was built from. About, the first line of the ini and the log file's name all come from here, so
// that a log or a settings file can be matched to the executable that produced it.
//
// Read out of the assembly rather than out of a file the program writes, because the number has to be true of
// the code that is running: a file beside the exe can be edited, or copied in from another build, or left
// behind by an upgrade, and then it describes something else.

using System.Reflection;

namespace Moa;

internal static class Build
{
    /// <summary>
    /// For example 2026.10.09-1941-05ab386, with -dirty appended when the tree it was built from had changes
    /// in it. "unknown" when the assembly carries no identity at all, which is what a build that did not go
    /// through build.ps1 gets: an honest answer rather than a plausible-looking wrong one.
    /// </summary>
    public static readonly string Version = Read();

    private static string Read()
    {
        string v = typeof(Build).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        // The compiler's own default and the placeholder in the project file: neither is an identity.
        return v.Length == 0 || v == "1.0.0" || v == "unknown" ? "unknown" : v;
    }
}
