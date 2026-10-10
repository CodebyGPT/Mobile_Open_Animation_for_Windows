// The launch-ad feature's own rules: where its videos live, and which of the tray menu's entries may be
// clicked.
//
// Nothing here plays anything - Media.cs does that - and nothing here draws anything; this is the switchboard
// the rest is hung off. Keeping the decisions away from the drawing is also what makes them checkable: most of
// what is below is a function of values, so probe/ads can put the whole table through it with no screen, no
// video and no player.

using System;
using System.Collections.Generic;
using System.IO;

namespace Moa;

/// <summary>What the tray menu's 播放广告 submenu should offer, worked out in one place.</summary>
internal readonly struct AdsMenuState
{
    /// <summary>Whether the submenu may be opened at all. False freezes every entry under it.</summary>
    public readonly bool Submenu;

    /// <summary>Whether a window advertisement is asked for.</summary>
    public readonly bool WindowAd;

    /// <summary>Whether a logon advertisement is asked for. The tick, not whether it may be changed.</summary>
    public readonly bool BootAd;

    /// <summary>Whether the logon-advertisement entry may be clicked.</summary>
    public readonly bool BootAdUsable;

    public AdsMenuState(bool submenu, bool windowAd, bool bootAd, bool bootAdUsable)
    {
        Submenu = submenu;
        WindowAd = windowAd;
        BootAd = bootAd;
        BootAdUsable = bootAdUsable;
    }
}

internal static class Ads
{
    /// <summary>The one folder this feature reads, directly beside the exe.</summary>
    public const string FolderName = "Ads";

    /// <summary>Where the videos go.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, FolderName);

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
    /// Whether the feature can run at all, which is what the whole submenu is frozen on.
    ///
    /// It used to be "is ffmpeg next to the exe". That dependency is gone - the player is Media Foundation's,
    /// which is part of Windows - so the only thing that can stop the feature now is having nothing to play,
    /// which is a much better thing to freeze the menu on: it is the state the user is actually in when they
    /// have just switched the feature on and have not put anything in the folder yet.
    ///
    /// Asked rather than remembered, so that dropping a file in un-freezes the menu without a restart.
    /// </summary>
    public static bool Available => Videos().Length > 0;

    /// <summary>
    /// How long an advertisement may run, whichever kind it is. Six minutes.
    ///
    /// One number for both kinds, deliberately. There were three modes once, each with its own cap, and the
    /// length of an advertisement turned out to be the wrong thing for a mode to be about: what a user wants to
    /// say is whether they want one at all, not how long the well-behaved flavour of one is.
    ///
    /// It is a cap rather than a target: an advertisement is shown for as long as the video is, and this is the
    /// point at which the card comes down whatever is still playing.
    /// </summary>
    public const int CapSeconds = 360;

    /// <summary>
    /// Makes the folder the videos go in. True when it was created, false when it was already there.
    ///
    /// Compared by name rather than asked with Directory.Exists: on an ordinary volume the two spellings are
    /// one directory and both answers are right, but a volume with case-sensitive names allowed on it can hold
    /// an "ads" and an "Ads" side by side, and making a second folder beside the one the user is already
    /// filling would be the wrong answer. Nothing below the top level is looked at - subfolders are ignored
    /// here and everywhere else this feature reads, so a folder of re-encoded copies is not a folder of ads.
    /// </summary>
    public static bool EnsureFolder()
    {
        if (FindFolder() != null) return false;
        Directory.CreateDirectory(Folder);
        return true;
    }

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

    // ---- what the submenu may offer ---------------------------------------------------------------------

    /// <summary>
    /// Whether the two switches are to be honoured at all, given whether the submenu is on the tray menu.
    ///
    /// This is a rule and not a linkage, and the difference is the whole point of it. Hiding the submenu - a
    /// thing the ini can do, and the thing the About box's code undoes - must not clear the switches: what they
    /// are set to is the user's, and showing the menu again has to bring back exactly what was there. But an
    /// advertisement left switched on behind a hidden submenu is an advertisement with no way to switch it off
    /// from the tray, and the tray is the only way out this feature has. So a hidden submenu means neither
    /// switch is acted on, and the switches are left exactly as they were.
    ///
    /// The joke is allowed to be annoying. It is not allowed to be inescapable.
    /// </summary>
    public static bool Allowed(bool submenuVisible) => submenuVisible;

    /// <summary>
    /// Which of the two entries are ticked and which may be clicked.
    ///
    /// There is no master switch and there should not be one: each entry is a switch of its own, so a third
    /// entry saying "the feature is on" would be a second way to write down the same thing - and a way for the
    /// two to disagree, which is the state a user cannot get out of. The feature is on when either of them is.
    ///
    /// The whole submenu hangs on one thing, having something to play: with an empty folder nothing in the
    /// feature can run, so entries that could still be ticked would be promising what cannot happen.
    ///
    /// The one entry with conditions of its own is the logon advertisement. It is an advertisement at logon, so
    /// it needs the program to start at logon; without that there is no logon for it to run at, and the entry
    /// is frozen rather than merely useless. That is the only thing that freezes it - in particular the window
    /// advertisement is not part of the answer, however it is set.
    ///
    /// The ticks are reported whatever the state, frozen or not: an entry that cannot be changed is still no
    /// reason to hide what it is set to.
    /// </summary>
    public static AdsMenuState Menu(bool playable, bool autoStart, bool windowAd, bool bootAd)
        => new AdsMenuState(playable, windowAd, bootAd, playable && autoStart);
}
