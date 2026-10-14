// The opening-ad feature's own rules: where its videos live, and whether there is one worth switching on.
//
// Nothing here plays anything - Media.cs does that - and nothing here draws anything; this is the switchboard
// the rest is hung off. Keeping the decisions away from the drawing is also what makes them checkable: what is
// below is a function of a directory and some file names, so probe/ads can put the whole rule through it with no
// screen, no video and no player.

using System;
using System.Collections.Generic;
using System.IO;

namespace Moa;

internal static class Ads
{
    /// <summary>The one folder this feature reads, directly beside the exe.</summary>
    public const string FolderName = "Ads";

    /// <summary>Where the videos go, whether or not the folder is there.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>Whether that folder is there, in any spelling of the name.</summary>
    public static bool FolderExists => FindFolder() != null;

    /// <summary>
    /// The videos in the folder, in whatever order the file system hands them over.
    ///
    /// The top level only, deliberately: the folder is the list, and a folder of re-encoded copies beside the
    /// originals - which is what the one in this repository holds - would otherwise double the count and put
    /// the wrong ones in front of the user. A file whose extension is not one a video comes in is not one, and
    /// is left alone rather than handed to the player to fail on.
    /// </summary>
    public static string[] Videos()
    {
        var folder = FindFolder();
        if (folder == null) return Array.Empty<string>();

        var found = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(folder))
                if (IsVideo(Path.GetExtension(file))) found.Add(file);
        }
        catch { }
        return found.ToArray();
    }

    /// <summary>
    /// Whether there is a file that could be an advertisement at all: a video's extension and something in it.
    ///
    /// The size is the half of it that needs no player. A file of no bytes is one of the two ways this folder
    /// holds something that is not a video - the other is a file that opens and turns out to be nothing long,
    /// which only the player can say and only by being asked; see MediaPlayer.MeasureMs, which is what asks.
    /// </summary>
    public static bool AnyVideo()
    {
        foreach (var file in Videos())
        {
            try { if (new FileInfo(file).Length > 0) return true; }
            catch { }
        }
        return false;
    }

    private static bool IsVideo(string extension)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".mp4": case ".m4v": case ".mov": case ".mkv":
            case ".webm": case ".avi": case ".wmv": return true;
            default: return false;
        }
    }

    /// <summary>
    /// Whether the feature can run at all.
    ///
    /// It used to be "is ffmpeg next to the exe". That dependency is gone - the player is Media Foundation's,
    /// which is part of Windows - so the only thing that can stop the feature now is having nothing to play,
    /// which is a much better thing to freeze on: it is the state the user is actually in when they have just
    /// switched the feature on and have not put anything in the folder yet.
    ///
    /// Asked rather than remembered, so that dropping a file in makes the answer change without a restart.
    /// </summary>
    public static bool Available => Videos().Length > 0;

    /// <summary>
    /// How long an advertisement may run. Six minutes.
    ///
    /// It is a cap rather than a target: an advertisement is shown for as long as the video is, and this is the
    /// point at which the card comes down whatever is still playing.
    /// </summary>
    public const int CapSeconds = 360;

    /// <summary>
    /// The folder, whatever it is spelled like, or null when there is none.
    ///
    /// Compared by name rather than asked with Directory.Exists: on an ordinary volume the two spellings are one
    /// directory and both answers are right, but a volume with case-sensitive names allowed on it can hold an
    /// "ads" and an "Ads" side by side, and reading the wrong one - or writing a second one beside the one the
    /// user is already filling - would be the wrong answer. Nothing below the top level is looked at: subfolders
    /// are ignored here and everywhere else this feature reads, so a folder of re-encoded copies is not a folder
    /// of ads.
    /// </summary>
    private static string? FindFolder()
    {
        try
        {
            foreach (var dir in new DirectoryInfo(AppContext.BaseDirectory).EnumerateDirectories())
                if (string.Equals(dir.Name, FolderName, StringComparison.OrdinalIgnoreCase)) return dir.FullName;
        }
        catch { }
        return null;
    }
}
