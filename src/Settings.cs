// Settings live in an .ini next to the exe. There is deliberately no settings window:
// the tray menu handles toggles and enums, and this file handles the lists that a menu
// cannot reasonably edit (excluded apps and window classes).
using System.Globalization;
using System.Text;

namespace Moa;

internal enum Lang { English = 0, ChineseSimplified = 1 }

internal sealed class Settings
{
    // ---- menu-editable ----------------------------------------------------------------
    public Lang Language = Lang.English;

    /// <summary>
    /// Whether a window gets an advertisement over its opening animation - the 开屏广告 entry, which is on the
    /// tray menu beside the animation switches rather than hidden behind a code, and is off until it is ticked.
    ///
    /// Ticking it is refused when there is nothing to play, which is why the two settings under it are about
    /// where the videos are rather than about whether the feature exists: see Ads and Program.SwitchOnOpeningAd.
    /// </summary>
    public bool WindowAd = false;

    /// <summary>
    /// Whether advertisements play silently, remembered from the last time the mute control was used.
    ///
    /// On by default, which is the only defensible default for something that makes a noise without being
    /// asked, and written back whenever the mute control is clicked: an advertisement that starts silent and
    /// cannot be told to stay that way is a mute button that does nothing.
    /// </summary>
    public bool AdsMuted = true;

    /// <summary>
    /// The SHA-256 of the start-up launcher this program wrote, lowercase hex, or "" when it has never written
    /// one.
    ///
    /// The *state* of Start with Windows is still read from the Startup folder rather than from here - what is in
    /// that folder is the truth, and a copy here could only ever disagree with it. This is not that copy: it is
    /// what the program expects to find there, so that a file with the right name and the wrong contents can be
    /// told from the one it wrote. Without it the menu's tick would say on for any file that starts this program,
    /// including one somebody edited afterwards, which is a start-up the program does not control and must not
    /// claim. See AutoStart.Look, and the ini's own note about this key.
    /// </summary>
    public string StartupHash = "";

    /// <summary>
    /// Whether every start asks Windows for administrator rights.
    ///
    /// Off by default: from an ordinary folder at medium integrity the tray icon registers, and the windows
    /// this program animates are at that level too - so an elevation prompt at every logon is a price for
    /// rights most runs do not need. On, Program.Run asks before anything else and leaves if the consent is
    /// refused; that is the behaviour this program used to have unconditionally, kept as a choice.
    ///
    /// Read at start-up and nowhere else, so ticking the menu entry does not change the copy that is running -
    /// "always" is about the next start. An ini that does not mention it is off, like every other switch here.
    /// </summary>
    public bool RunAsAdmin = false;

    /// <summary>
    /// Whether a window animates as it closes. On by default, and checked per close rather than at
    /// startup, so turning it off takes effect on the very next window.
    /// </summary>
    public bool CloseAnimation = true;

    /// <summary>
    /// Dynamic rounding: whether the card's corner animates as the card grows or shrinks.
    ///
    /// Windows 11 only, and off by default. On, the small end of the card - where an opening card starts and
    /// where a closing card ends - is a 50 per cent corner, a circle, and it rounds out into the window's own
    /// corner as the card grows. The interpolation is linear in time rather than on the card's Material curve,
    /// so the change is spread across the whole animation instead of being mostly over in its first third.
    ///
    /// Off - the default on every version - the card keeps the corner the window itself has for the whole of
    /// its life: square on Windows 10, eight pixels on Windows 11.
    ///
    /// Windows 10 is not a default but a refusal: the entry is not on the menu there, and Load treats the key in
    /// an ini as absent. A square window whose card turns into a circle is a second shape changing beside the
    /// one the eye is following; a week of using it decided that looks wrong rather than dynamic.
    /// </summary>
    public bool DynamicCorner = false;

    /// <summary>
    /// Whether a window that was opened from a click on the desktop or the taskbar closes back towards the
    /// square the opening animation started from, instead of shrinking to its own centre.
    ///
    /// Off by default, and marked experimental in the menu. It fires only for a window opened from the
    /// desktop or the taskbar, whose opening animation really played - the card has to have a place it came
    /// from - and whose launch point is still visible when the window closes; a window that does not
    /// qualify closes to its own centre exactly as it did before this existed. That is a narrow enough set
    /// of conditions that switching it on should be a decision rather than something inherited from a
    /// default, which is why it is off.
    /// </summary>
    public bool ReturnToOrigin = false;

    /// <summary>
    /// Debug mode: off by default, and the thing every diagnostic in this program is waiting for.
    ///
    /// There is one build and it contains all of the logging, so this decides whether any of it happens
    /// rather than whether it exists. With it off, nothing is buffered, nothing is written, and no log
    /// file appears next to the exe; turning it on in the menu starts the logging on the spot, without a
    /// restart, and turning it off again closes the file with a line saying so.
    ///
    /// It is a setting rather than a command-line switch because it has to survive a restart: the run being
    /// diagnosed is usually the one that starts with Windows, where nobody is there to pass an argument.
    /// </summary>
    public bool DebugMode = false;

    // ---- animation --------------------------------------------------------------------
    public int StartSizePx = 96;      // logical size of the square the animation starts from; scaled by the monitor's DPI
    public int DurationMs = 240;      // phone-like
    public int HandoffFadeMs = 160;   // panel fade-out once the app is ready
    public int ReadyTimeoutMs = 2400; // give up waiting for the app to paint
    public int MaxHideMs = 20000;     // absolute cap: never keep a window hidden longer

    // ---- the advertisement's own rule ---------------------------------------------------
    //
    // When a program that has already shown an advertisement may show another one. The three parts are what
    // AdRule.Chance is given: every time a window of the program appears its chance restarts at AdRuleReset, and
    // it grows by AdRuleIncrease for every AdRuleUnit since that appearance. The defaults are nought, one percent
    // and one minute - so a program that has been open for five minutes is asked a five percent chance on its
    // next window, and a program that is opened again and again is asked the same small chance each time instead
    // of accumulating one between openings. A program that has not appeared before always gets one; the rule is
    // about the *next* advertisement.
    //
    // The two numbers were typed into a window and are written here; the name is what AdRule.Name makes of the
    // unit, so a hand-edited ini with a word that is not a unit falls back to the default rather than failing.
    public string AdRuleUnit = AdRule.Name(AdRule.DefaultUnit);
    public double AdRuleIncrease = AdRule.DefaultIncrease;
    public double AdRuleReset = AdRule.DefaultReset;

    // ---- lists ------------------------------------------------------------------------
    public readonly List<string> ExcludedClasses = new();
    public readonly List<string> ExcludedProcesses = new();

    /// <summary>
    /// When each rule was added, oldest first, as "class:Name" or "process:Name".
    ///
    /// The two lists above cannot answer that question - a rule added today sits at the end of one of them and
    /// an older rule of the other kind sits in front of it - and the panel shows one list of rules in the order
    /// they were added, newest at the bottom, so that the newest rule is where the eye ends up. It is also
    /// what lets a rule be moved between the two kinds without moving in the list: moving it is a change of
    /// kind, not a new rule.
    /// </summary>
    public readonly List<string> ExcludeOrder = new();

    /// <summary>
    /// Marks an entry that is switched off rather than removed. A leading '!' on the entry itself, which no
    /// window class and no executable name can begin with, so the file keeps one list per kind and an entry
    /// can be turned off without being forgotten - which is what the blacklist panel's disable button does.
    /// </summary>
    public const char DisabledMark = '!';

    /// <summary>
    /// Whether a class name or process name is in one of those lists, switched off entries excluded.
    ///
    /// Written as a comparison against the entry's offset rather than as a substring of a cleaned-up copy
    /// because this runs once per candidate window on the path that has two milliseconds to hide it: one pass,
    /// no allocation, no trimming.
    /// </summary>
    public static bool Matches(List<string> entries, string value)
    {
        foreach (var entry in entries)
        {
            int off = entry.Length > 0 && entry[0] == DisabledMark ? 1 : 0;
            if (entry.Length - off != value.Length) continue;
            if (string.Compare(entry, off, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        }
        return false;
    }

    public static string IniPath =>
        Path.Combine(AppContext.BaseDirectory, "MobileOpenAnimation.ini");

    /// <summary>
    /// Whether the user's Windows is in Chinese, which is the one language this program picks for a user rather
    /// than asking them: the interface is translated into English and Chinese and into nothing else, and a
    /// Chinese system left in English reads as a program that did not notice.
    ///
    /// Simplified and Traditional alike, which is one answer here and not two: the program has one Chinese, and
    /// Taiwan, Hong Kong, Macau and Singapore are all Chinese systems. See ChineseLangId for the ids.
    ///
    /// Read from Windows and never from the ini, because of where it is asked: both callers are deciding the
    /// language of a run that has no ini yet - the first start, and the question asked before the settings are
    /// opened at all - so the ini is not merely the wrong source, it is not a source.
    /// </summary>
    public static bool WindowsIsChinese() => ChineseLangId(Native.GetUserDefaultUILanguage());

    /// <summary>
    /// Whether a LANGID is Chinese, in either script.
    ///
    /// The primary language is the low ten bits of the id and LANG_CHINESE is 4; everything above it is the
    /// sublanguage, which is what separates zh-CN from zh-TW and what this deliberately does not look at. Their
    /// values, for the record, because "is Traditional covered" is the kind of question that outlives a comment:
    /// zh-CN 0x0804, zh-SG 0x1004, zh-TW 0x0404, zh-HK 0x0C04, zh-MO 0x1404 - the four scripts-and-regions that
    /// are not the mainland are 0x0404, 0x0C04, 0x1404 and 0x1004, and all six share the low ten bits.
    /// </summary>
    public static bool ChineseLangId(int id) => (id & 0x03FF) == 0x0004;

    /// <summary>
    /// Bumped whenever a default changes. An existing .ini overrides the defaults, so
    /// without this a changed default silently has no effect on anyone who has already run
    /// the program once - which is exactly how "the timeout is still 8 seconds" happened.
    /// </summary>
    public const int CurrentConfigVersion = 2;

    public static Settings Load(Lang? firstRunLanguage = null)
    {
        var s = new Settings();
        s.ExcludedClasses.AddRange(DefaultExcludedClasses);
        s.ExcludedProcesses.AddRange(DefaultExcludedProcesses);
        // The language of a run that has no ini yet is decided here rather than by the caller afterwards, and
        // that is not tidiness: this is the one place that knows the file was not there, and a second look for it
        // from outside would have to ask the question the file system has already answered - as well as writing
        // the ini twice, once in English and then again in the language the first write should have used.
        if (!File.Exists(IniPath)) { s.Language = firstRunLanguage ?? s.Language; s.Save(); return s; }

        var lines = File.ReadAllLines(IniPath);

        int fileVersion = 0;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            int i = l.IndexOf('=');
            // Substring, not range syntax: System.Index/System.Range are .NET Core types.
            if (i > 0 && l.Substring(0, i).Trim().Equals("version", StringComparison.OrdinalIgnoreCase))
                fileVersion = Int(l.Substring(i + 1).Trim(), 0);
        }
        if (fileVersion < CurrentConfigVersion)
        {
            // Regenerate from the new defaults. This does discard whatever the user had customised,
            // including the exclusion lists, so CurrentConfigVersion is only worth bumping when a
            // changed default matters more than what would be lost.
            Log.Write($"ini version {fileVersion} < {CurrentConfigVersion}: regenerating {IniPath}");
            s.Save();
            return s;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();

            switch (key.ToLowerInvariant())
            {
                case "language": s.Language = val.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? Lang.English : Lang.ChineseSimplified; break;
                case "windowad": s.WindowAd = Bool(val, s.WindowAd); break;
                case "adsmuted": s.AdsMuted = Bool(val, s.AdsMuted); break;
                // Hex, kept as it stands rather than parsed: empty means "no launcher of ours has been
                // recorded", which is what a settings file written from defaults says, and what the menu reads as
                // Start with Windows being off.
                case "startupscripthash": s.StartupHash = val.Trim(); break;
                case "runasadmin": s.RunAsAdmin = Bool(val, s.RunAsAdmin); break;
                case "closeanimation": s.CloseAnimation = Bool(val, s.CloseAnimation); break;
                // Windows 11 only, and a hand-edited ini does not get to overrule that: a square window has no
                // rounded corner for a dynamic card to arrive at.
                case "dynamiccorner":
                    s.DynamicCorner = Native.IsWindows11 && Bool(val, false);
                    break;
                case "returntoorigin": s.ReturnToOrigin = Bool(val, s.ReturnToOrigin); break;
                case "debugmode": s.DebugMode = Bool(val, s.DebugMode); break;
                case "startsize": s.StartSizePx = Clamp(Int(val, s.StartSizePx), 16, 800); break;
                case "duration": s.DurationMs = Clamp(Int(val, s.DurationMs), 30, 2000); break;
                case "handofffade": s.HandoffFadeMs = Clamp(Int(val, s.HandoffFadeMs), 0, 1000); break;
                case "readytimeout": s.ReadyTimeoutMs = Clamp(Int(val, s.ReadyTimeoutMs), 500, 20000); break;
                case "maxhidems": s.MaxHideMs = Clamp(Int(val, s.MaxHideMs), 500, 60000); break;
                // The rule's three parts, as the window wrote them. The unit is kept as the word it is and
                // checked where it is used, so that a hand-edited ini with something else in it behaves like an
                // ini with no rule at all rather than like a broken one; see AdRule.TryParse.
                case "adruleunit": s.AdRuleUnit = val.Trim().ToLowerInvariant(); break;
                case "adruleincrease": s.AdRuleIncrease = Bound(Number(val, s.AdRuleIncrease), 0, 99.99); break;
                case "adrulereset": s.AdRuleReset = Bound(Number(val, s.AdRuleReset), 0, 100); break;
                case "excludeclasses": s.ExcludedClasses.Clear(); AddList(s.ExcludedClasses, val); break;
                case "excludeprocesses": s.ExcludedProcesses.Clear(); AddList(s.ExcludedProcesses, val); break;
                case "excludeorder": s.ExcludeOrder.Clear(); AddList(s.ExcludeOrder, val); break;
            }
        }

        // An ini written before the order existed, or edited by hand, has rules the ledger does not mention.
        // They are appended in the order the lists hold them, which is the best that can be said for a rule
        // whose age nobody recorded - and the alternative, treating them as absent, would show a rule the
        // user cannot see in the panel and cannot explain.
        foreach (var name in s.ExcludedClasses) SeedOrder(s, "class", name);
        foreach (var name in s.ExcludedProcesses) SeedOrder(s, "process", name);
        return s;
    }

    /// <summary>Adds an entry to the ledger if it is not in it already, as "kind:name".</summary>
    private static void SeedOrder(Settings s, string kind, string entry)
    {
        int off = entry.Length > 0 && entry[0] == DisabledMark ? 1 : 0;
        string name = entry.Substring(off);
        if (name.Length == 0) return;
        foreach (var known in s.ExcludeOrder)
            if (string.Equals(known, $"{kind}:{name}", StringComparison.OrdinalIgnoreCase)) return;
        s.ExcludeOrder.Add($"{kind}:{name}");
    }

    public void Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("; Mobile Open Animation for Windows - window open animation, standalone");
        sb.AppendLine("; The tray menu edits the entries marked [menu]; edit the rest here.");
        sb.AppendLine("; NOTE: Start with Windows is deliberately NOT stored in this file. The state is");
        sb.AppendLine("; whether this program's launcher is in your own Startup folder, read from that folder,");
        sb.AppendLine("; and a copy here could only ever disagree with it. What IS stored here is the hash of");
        sb.AppendLine("; the launcher this program wrote - StartupScriptHash below - so that a file of that");
        sb.AppendLine("; name whose contents have changed since is not mistaken for the program's own.");
        sb.AppendLine();
        sb.AppendLine("[general]");
        // This line is the whole reason settings survive a restart: Load regenerates the file from
        // defaults whenever the version it finds is older than CurrentConfigVersion, and if Save
        // does not write it the file always looks older, so every launch resets everything.
        sb.AppendLine("; file format version; do not edit. Written by the program.");
        sb.AppendLine($"version={CurrentConfigVersion}");
        sb.AppendLine("; [menu] en or zh-CN");
        sb.AppendLine($"Language={(Language == Lang.English ? "en" : "zh-CN")}");
        sb.AppendLine("; [menu] the satirical opening-ad feature: an advertisement over a window's opening");
        sb.AppendLine("; animation. Off by default, and refused when there is nothing to play: switching it");
        sb.AppendLine("; on checks that the Ads folder beside the exe holds a video the player can open, and");
        sb.AppendLine("; says what to do about it when it does not. Subfolders are not read.");
        sb.AppendLine($"WindowAd={(WindowAd ? 1 : 0)}");
        sb.AppendLine("; whether advertisements play silently. On by default, and written by the mute control");
        sb.AppendLine("; on the card, which remembers between showings.");
        sb.AppendLine($"AdsMuted={(AdsMuted ? 1 : 0)}");
        sb.AppendLine("; [menu] the rule for a program that has already shown an advertisement: every time one");
        sb.AppendLine("; of its windows appears the chance restarts at AdRuleReset, and it grows by AdRuleIncrease");
        sb.AppendLine("; for every AdRuleUnit since that appearance. The three defaults are nought, one percent and");
        sb.AppendLine("; one minute - so a program is asked the same small chance on each window it opens,");
        sb.AppendLine("; rather than one that has been climbing since its last advertisement. Both numbers are");
        sb.AppendLine("; what that window writes; a program that has not appeared before always gets one, and");
        sb.AppendLine("; the rule is not consulted.");
        sb.AppendLine($"AdRuleUnit={AdRuleUnit}");
        sb.AppendLine($"AdRuleIncrease={AdRuleIncrease.ToString("0.##", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"AdRuleReset={AdRuleReset.ToString("0.##", CultureInfo.InvariantCulture)}");
        sb.AppendLine("; not a setting: the SHA-256 of the start-up launcher this program wrote into your own");
        sb.AppendLine("; Startup folder. Empty means it has never written one, which reads as Start with");
        sb.AppendLine("; Windows being off. Deleting this line is the same as deleting the launcher: the menu");
        sb.AppendLine("; stops claiming it, and the next click writes a new one over whatever is there.");
        sb.AppendLine($"StartupScriptHash={StartupHash}");
        sb.AppendLine("; [menu] always run as administrator. Off by default: an ordinary folder at medium");
        sb.AppendLine("; integrity can register the tray icon, so the consent prompt is asked for only when");
        sb.AppendLine("; this is on. On, every start asks for elevation before doing anything else, and a");
        sb.AppendLine("; refused prompt means the program does not start. Read at start-up only, so ticking the");
        sb.AppendLine("; menu entry takes effect on the next launch.");
        sb.AppendLine($"RunAsAdmin={(RunAsAdmin ? 1 : 0)}");
        sb.AppendLine($"CloseAnimation={(CloseAnimation ? 1 : 0)}");
        sb.AppendLine("; [menu] dynamic rounding, Windows 11 only. On, an opening card starts as a circle - a 50%");
        sb.AppendLine("; corner - and rounds into the window's own corner on a linear curve; a closing card ends");
        sb.AppendLine("; at the same circle. Off, the card keeps the window's own corner from start to finish and");
        sb.AppendLine("; only its size changes. Off by default, and ignored on Windows 10, whose windows are");
        sb.AppendLine("; square.");
        sb.AppendLine($"DynamicCorner={(DynamicCorner ? 1 : 0)}");
        sb.AppendLine("; [menu] closing animation: shrink back to the square the window was opened from,");
        sb.AppendLine("; instead of to its own centre. Off by default. Only for windows opened from the");
        sb.AppendLine("; desktop or the taskbar - a launch from the Start menu or from search is refused,");
        sb.AppendLine("; because those flyouts are gone by the time the window closes. Experimental: it");
        sb.AppendLine("; also needs the opening animation to have really played, the display's shape and");
        sb.AppendLine("; the window's DPI to be unchanged, and the launch point to still be visible.");
        sb.AppendLine($"ReturnToOrigin={(ReturnToOrigin ? 1 : 0)}");
        sb.AppendLine("; [menu] Debug mode > Enable. Off by default. On, the program writes a log next");
        sb.AppendLine("; to the exe whose name carries this build's identity - MobileOpenAnimation-");
        sb.AppendLine("; <build>.log, so that a log belongs to the exe that wrote it; off, it writes no log");
        sb.AppendLine("; at all - no file is created and nothing is buffered. It exists as a setting rather");
        sb.AppendLine("; than a switch because the runs worth diagnosing start with Windows, where nobody is");
        sb.AppendLine("; around to pass one.");
        sb.AppendLine($"DebugMode={(DebugMode ? 1 : 0)}");
        sb.AppendLine();
        sb.AppendLine("[animation]");
        sb.AppendLine("; logical size of the square the window grows out of (96 ~ a desktop icon),");
        sb.AppendLine("; scaled by the monitor's DPI");
        sb.AppendLine($"StartSize={StartSizePx}");
        sb.AppendLine("; ms; phones use 200-300");
        sb.AppendLine($"Duration={DurationMs}");
        sb.AppendLine("; ms the panel takes to fade out once the app has painted");
        sb.AppendLine($"HandoffFade={HandoffFadeMs}");
        sb.AppendLine("; ms to wait for the app to paint before revealing it anyway");
        sb.AppendLine($"ReadyTimeout={ReadyTimeoutMs}");
        sb.AppendLine("; hard cap: a window is never kept hidden longer than this");
        sb.AppendLine($"MaxHideMs={MaxHideMs}");
        sb.AppendLine();
        sb.AppendLine("[exclusions]");
        sb.AppendLine("; window classes that never animate; one per line, comma separated. The tray menu's");
        sb.AppendLine("; Debug mode > Blacklist manages these from the log; an entry that begins with '!' is");
        sb.AppendLine("; switched off rather than deleted, and can be switched back on there.");
        sb.AppendLine("ExcludeClasses=" + string.Join(",", ExcludedClasses));
        sb.AppendLine("; whole processes (exe name without .exe); one per line, comma separated. '!' as above.");
        sb.AppendLine("ExcludeProcesses=" + string.Join(",", ExcludedProcesses));
        sb.AppendLine();
        sb.AppendLine("; when each rule was added, oldest first, as class:name or process:name. Written by the");
        sb.AppendLine("; panel, which lists the rules in this order - newest at the bottom. A rule that is not");
        sb.AppendLine("; mentioned here is treated as the oldest.");
        sb.AppendLine("ExcludeOrder=" + string.Join(",", ExcludeOrder));
        File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
        LogState("saved");
    }

    /// <summary>
    /// Writes what the program is set to do on one line, so that a log says which settings were in force and
    /// at which moment they changed - which is what a run of frames has to be read against.
    ///
    /// Called from Save, which every menu entry goes through and which the regenerated file ends in, and once
    /// at startup. Deliberately not per animation: the settings a frame used are the ones in force since the
    /// last line, and a copy per frame would bury everything else in the file.
    /// </summary>
    public void LogState(string why)
    {
        Log.Write($"settings ({why}): lang={Language} duration={DurationMs}ms handoffFade={HandoffFadeMs}ms" +
                  $" startSize={StartSizePx} close={OnOff(CloseAnimation)}" +
                  $" dynamicCorner={OnOff(DynamicCorner)} returnToOrigin={OnOff(ReturnToOrigin)}" +
                  $" adsWindow={OnOff(WindowAd)}" +
                  $" startupHash={(StartupHash.Length == 0 ? "none" : StartupHash.Substring(0, 8))}" +
                  $" runAsAdmin={OnOff(RunAsAdmin)}" +
                  $" adRule=+{AdRuleIncrease.ToString("0.##", CultureInfo.InvariantCulture)}%" +
                  $"/{AdRuleUnit}/reset {AdRuleReset.ToString("0.##", CultureInfo.InvariantCulture)}%" +
                  $" debug={OnOff(DebugMode)} excluded={ExcludedClasses.Count} classes," +
                  $" {ExcludedProcesses.Count} processes");
    }

    private static string OnOff(bool v) => v ? "on" : "off";

    private static void AddList(List<string> into, string csv)
    {
        foreach (var part in csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length > 0 && !into.Contains(t, StringComparer.OrdinalIgnoreCase)) into.Add(t);
        }
    }
    private static bool Bool(string v, bool d) => v is "1" or "true" or "yes" or "on" || (v is "0" or "false" or "no" or "off" ? false : d);
    private static int Int(string v, int d) => int.TryParse(v, out var i) ? i : d;
    private static long Long(string v, long d) => long.TryParse(v, out var i) ? i : d;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>
    /// A decimal out of the file, read with the invariant culture because that is how Save writes it and a
    /// machine whose decimal separator is a comma must not read its own settings as nonsense.
    /// </summary>
    private static double Number(string v, double d)
        => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : d;

    private static double Bound(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    // Straight from the Windhawk mod; independent of this project's settings.
    public static readonly string[] DefaultExcludedClasses =
    {
        "ApplicationFrameWindow", "Windows.UI.Core.CoreWindow",
        "Microsoft.UI.Content.PopupWindowSiteBridge", "XamlExplorerHostIslandWindow",
        "Windows.UI.Input.InputSite.WindowClass",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "MsoSplash", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "Progman", "WorkerW",
        "TaskListThumbnailWnd", "SysShadow", "tooltips_class32",
        "DropdownWindow", "CEF-OSC-WIDGET",
    };

    public static readonly string[] DefaultExcludedProcesses =
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp",
        "SystemSettings", "TextInputHost", "Widgets", "PhoneExperienceHost",
        "GameBarPresenceWriter", "RuntimeBroker", "dllhost", "sihost",
        "taskhostw", "ctfmon", "CredentialUIBroker", "backgroundTaskHost",
        "SecurityHealthSystray", "OneDrive", "msedgewebview2",
    };
}

/// <summary>Menu text. No resource files; one small dictionary is enough.</summary>
internal static class Strings
{
    private static Lang _lang = Lang.English;
    public static void Use(Lang l) => _lang = l;

    /// <summary>Which language the strings are in, for a caller that wants to put it back.</summary>
    public static Lang Language => _lang;

    /// <summary>
    /// Where a click on an advertisement's own name searches, and the one string in this file that nobody is
    /// shown: it is handed to the shell, see AdsCard.SearchUrlFor, which is what turns a file's name into the
    /// part that follows the query key. Per language because the useful engine is - Google for an English
    /// interface and Baidu for a Chinese one is the whole of the rule - and because Windows has no notion of a
    /// "default search engine" at all: that setting belongs to each browser and is not published to anything.
    ///
    /// Both are https URLs that end in their own query key, and that is a requirement rather than a style: what
    /// is appended to them is a file's name, and the scheme, host and key being ours is what keeps it a search
    /// term rather than anything else. See AdsCard.SearchUrlFor for the rest of that argument.
    /// </summary>
    public static string SearchUrl => _lang == Lang.ChineseSimplified
        ? "https://www.baidu.com/s?wd="
        : "https://www.google.com/search?q=";

    private static readonly Dictionary<string, (string en, string zh)> Map = new()
    {
        ["Language"]     = ("Language", "语言"),
        ["OpenConfig"]   = ("Open config directory", "打开配置目录"),
        ["AutoStart"]    = ("Start with Windows", "开机自启"),
        // The two entries directly under Start with Windows. The first is a setting read at the next launch;
        // the second acts on this copy and is not stored anywhere.
        ["RunAsAdmin"]   = ("Always run as administrator", "始终以管理员身份运行"),
        ["RestartAdmin"] = ("Restart as administrator", "以管理员身份重启本程序"),
        // Four characters, because it sits in a column of toggles where the other three are read at a glance -
        // 回退动画, 动态圆角, 开机自启, in the order the menu lists them - and "窗口关闭动画" was the only one
        // that took a second look. It is the same switch it always was; the ini's key and the log's name for it
        // are unchanged.
        ["CloseAnim"]    = ("Closing animation", "关闭动画"),
        ["DynamicCorner"] = ("Dynamic corner radius", "动态圆角"),
        // Not named for the desktop any more, which is what made the name long: it covers the taskbar and
        // the desktop both, and what it does not cover is worth saying out loud - a launch from the Start
        // menu or from search is refused, because those flyouts are gone by the time the window closes and
        // there would be nothing at that place to return to.
        ["ReturnToOrigin"] = ("Return animation (experimental)",
                              "回退动画（实验性）"),
        // The one advertisement entry, on the tray menu beside the animation switches and visible like them.
        ["AdsWindow"]    = ("Opening ad", "开屏广告"),
        // The rule window's menu entry, and the window's own title: the same words in both places, because the
        // menu is where the user asked for it and a window with a different name is a second thing to read.
        ["AdRule"]       = ("Custom opening ad trigger conditions", "自定义开屏广告触发条件"),
        // That window, in two lines, and the controls sit inside them: "Every [minute] add [01.00]% chance of
        // another opening ad" and "Each time a window appears, reset the chance to [000]%". The fragments are
        // separate strings because the controls are between them.
        ["AdRuleEvery"]  = ("Every", "每"),
        ["AdRuleAdd"]    = ("add", "增加"),
        ["AdRuleChance"] = ("% chance of another opening ad", "%概率再次播放开屏广告"),
        ["AdRuleAfter"]  = ("Each time a window appears, reset the chance to", "每次窗口出现时将概率重置为"),
        ["AdRuleUnitSecond"] = ("second", "每秒"),
        ["AdRuleUnitMinute"] = ("minute", "每分钟"),
        ["AdRuleUnitHour"]   = ("hour", "每小时"),
        ["AdRuleUnitDay"]    = ("day", "每天"),
        ["AdRuleReset"]  = ("Reset", "重置"),
        // What the user is told when switching it on finds nothing to play: a question, and its own title, and
        // the title is what the window says rather than the program's name - the program's name over a question
        // about a folder is the same non-answer as an error box titled "Error". The body says the two things
        // that are the user's to do - make the folder, name the file - and the one thing the program cannot know
        // for them, which is what this machine's Media Foundation will play. The folder's name is compared
        // case-insensitively (see Ads.FindFolder), and the file's name is what the advertisement shows and
        // searches for, which is why it is worth choosing.
        // The tray preflight's failure dialog. A tray program without a tray icon has no interface, so this is
        // not advice about a setting: the two answers are "cancel this start and put the program somewhere it
        // can run" and "run it with the rights that make the icon register". The same two exits the reference
        // program offers, in this program's own words.
        ["TrayFailedTitle"] = ("Taskbar notification icon registration failed", "任务栏通知图标注册失败"),
        ["TrayFailedAsk"]   = ("The notification icon cannot be added to the taskbar",
                               "无法把通知图标注册到任务栏"),
        ["TrayFailedAdvice"] =
            ("Cancel this start, move the program to an ordinary location - the current user's Desktop, " +
             "or the root of a non-system drive - and try again.\n\n" +
             "You can also restart the program as administrator, so that the icon can register in the taskbar.",
             "建议取消本次启动，将程序移动到常见普通目录（例如当前用户的桌面、非系统盘根目录）后重试。\n\n" +
             "也可以选择以管理员身份重启本程序，让通知图标能够注册到任务栏。"),
        // The exit button's label carries the seconds left, so it is a format string rather than a word: the
        // dialog rewrites it every tick. See TrayFailureDialog, which is what makes the countdown possible.
        ["TrayExitCountdown"] = ("Exit ({0}s)", "退出（{0}s）"),
        ["AdsNeededTitle"] = ("No usable content found", "未找到可用内容"),
        ["AdsNeeded"]    =
            ("Create an \"Ads\" subfolder in this program's configuration directory and put your named video " +
             "files in it.\n\n" +
             "The video container and codec formats that will play are decided by this machine's system. " +
             "H.264 + AAC in an MP4 is recommended.",
             "请在本程序配置目录中新建“Ads”子目录，并将命名好的视频文件放入该子目录中。\n\n" +
             "受支持的视频封装格式和编码格式均由本机系统决定，推荐使用 H.264 + AAC 的 MP4。"),
        // Asked before a start-up launcher is written over one that is already there and is not the file this
        // program wrote - edited, or somebody else's, or written before the ini was reset. Four strings, because
        // the question has two answers of the program's own naming and a message box's two buttons say OK and
        // Cancel and cannot be made to say anything else; see Native.Ask.
        ["AutoStartConflictTitle"] = ("Startup entry conflict", "启动项存在冲突"),
        ["AutoStartConflict"] =
            ("A start-up script with this name is already in shell:startup. Overwrite it?",
             "shell:startup 中已存在同名启动脚本，是否覆盖？"),
        ["AutoStartOverwrite"] = ("Overwrite (recommended)", "覆盖（推荐）"),
        ["AutoStartCancel"]    = ("Cancel", "取消"),
        ["AdsMute"]      = ("Mute", "静音"),
        ["AdsUnmute"]    = ("Unmute", "取消静音"),
        // The countdown capsule is two labels and a separator: what is left, and what the button it is on does.
        // The number is the only part of it that changes, which is why the words around it are one format string
        // rather than three strings joined in the card - a translator has a whole sentence to work with.
        ["AdsRemaining"] = ("{0}s left", "视频剩余：{0}s"),
        // And what it says until there is a number to put in that format: the player is asked how long the file
        // is and answers when it is ready, so there is always a moment with no answer at all. A placeholder
        // rather than the cap, which is what used to be there and was a figure the card had made up - the
        // longest it may stay up, not the length of what is playing. Two dashes, and the same in every language;
        // it is in the table because everything the user reads is.
        ["AdsPending"]   = ("--", "--"),
        ["AdsSkip"]      = ("Skip", "跳过"),
        // The bold words in front of the advertisement's own name, on the capsule that searches for it. Short on
        // purpose: what follows is the file's name, which is the thing being shown.
        ["AdsSearch"]    = ("Search", "搜一搜"),
        // A submenu rather than a toggle, and one entry in it so far: what belongs under it is any number
        // of separate diagnostics, each of which can be turned on without the others.
        ["DebugMode"]    = ("Debug mode", "调试模式"),
        ["DebugEnable"]  = ("Enable", "启用"),
        // Where the setting actually lives, said in the message that reports it failing: one file in the user's
        // own Startup folder, which a user who is told that can look at - and delete - there, in Windows'
        // Start-up settings page, or in Task Manager's Start-up tab. "Startup folder" is named rather than the
        // file's exact path being repeated, because the path is in AutoStart and the two would be a second thing
        // to keep in step.
        ["AutoStartFailed"] =
            ("Could not change the Start with Windows setting, so it has been left as it was.\n\n" +
             "It is a script in your own Startup folder, named after the program: it can be seen and deleted " +
             "there, or switched off in Windows' Start-up settings.",
             "无法修改开机自启设置，已保持原状。\n\n" +
             "它是放在你自己「启动」文件夹里、以本程序命名的一个脚本：可以在那里看到并删除，" +
             "也可以在 Windows 的「启动应用」设置里关闭。"),
        // There is deliberately no string for "this must run as an administrator". A copy that is not elevated
        // leaves without a word - see Program.Run - so there is nothing left for a string to say, and the one
        // that was here is what made a launch Windows makes at logon sit there invisibly waiting to be
        // answered.
        // The question asked at the first start in a folder that already has things in it, the title it is asked
        // under, and the two answers to it. The title names the situation rather than the program: the question is
        // about the folder, and "Mobile Open Animation for Windows" over it would be the tray tooltip, not a
        // heading. It says "for the first time" because that is the whole of when it is asked - the ini beside the
        // program is the record that it has run here before, so a folder this program has already run in is never
        // asked about again - and a title that did not say so read as a warning that would come back every start.
        // Asked rather than announced because the answer decides whether anything is written at all: the
        // caller has to be able to leave without the program having made a file in the folder the user is about
        // to move it out of. Deliberately says nothing about what the program keeps beside itself - which is the
        // whole reason the folder matters, and is also a feature nobody has been told about yet.
        ["CrowdedTitle"] =
            ("Starting for the first time from a folder that already has other files in it", "首次启动时脏目录提醒"),
        ["CrowdedAsk"] =
            ("This folder already has other files in it. Continue starting the program from this folder?",
             "当前目录已存在其他的文件，确定让程序继续从该目录启动吗？"),
        ["CrowdedAdvice"] =
            ("You can also cancel this run, move the program to a clean folder, and start it there instead.",
             "您也可以取消本次启动，将本程序移动到干净的目录后再启动。"),
        ["ContinueAnyway"] = ("Continue", "继续"),
        ["About"]        = ("About", "关于"),
        ["Version"]      = ("Version", "版本"),
        ["Blacklist"]    = ("Blacklist", "黑名单管理"),
        // No parenthetical about debug mode: the line that appears in the empty list already says it, and the
        // label is the place the two lists are told apart, not the place the log is explained.
        ["BlacklistRecent"] = ("Recent records", "最近记录"),
        ["BlacklistEntries"] = ("Blacklist", "黑名单"),
        ["BlacklistRefresh"] = ("Refresh", "刷新"),
        ["BlacklistByClass"] = ("Block this window class", "拉黑此类窗口"),
        ["BlacklistByProcess"] = ("Block the whole process", "拉黑整个进程"),
        ["BlacklistToggle"] = ("Enable / disable", "启用 / 禁用"),
        ["BlacklistRemove"] = ("Delete", "删除"),
        // No "Close" for the panel: it has no close button any more, only its title bar's X. See BlacklistPanel.
        // Square brackets, not the full-width parentheses these used to be, and no trailing space: RuleText
        // joins the notes with one and puts them all before the name, so the spacing lives in one place.
        ["BlacklistDisabled"] = ("[disabled]", "[已禁用]"),
        ["BlacklistBuiltIn"] = ("[built-in rule]", "[内置规则]"),
        ["BlacklistCovered"] = ("[covered by the process rule]", "[已被进程规则覆盖]"),
        ["BlacklistClass"] = ("class", "窗口类"),
        ["BlacklistProcess"] = ("process", "进程"),
        ["BlacklistEmpty"] = ("Nothing in the log yet. Turn Debug mode on, reproduce it once, then open "
            + "this again.",
            "日志里还没有候选窗口。先启用调试模式、复现一次，再打开这里。"),
        ["Exit"]         = ("Exit", "退出"),
        // The About text is not decoration: AGPL-3.0 section 0 requires an interactive interface
        // to show appropriate legal notices - a copyright notice, a statement that there is no
        // warranty, that the work may be conveyed under the licence, and how to view it - and
        // section 5a requires a notice that the work was modified, with a date. The tray menu's
        // About entry is the prominent item that satisfies it.
        //
        // The source line is a hyperlink rather than a bare URL because the box it is shown in is a
        // task dialog, which draws links itself and tells us when one was followed. Nothing about the
        // text is drawn by us. No ini path: that is what "Open config directory" in the tray menu is for.
        ["AboutText"] =
            ("Mobile Open Animation for Windows\n" +
             "Adds a smartphone-style open animation to Windows.\n\n" +
             "Built on Nico6719/windhawk-mod-mobile-open-animation (AGPL-3.0),\n" +
             "rewritten as a standalone program in 2026.\n\n" +
             "Copyright (C) 2026 CodebyGPT\n" +
             "Released under the GNU Affero General Public License, version 3 or\n" +
             "later, with NO WARRANTY.\n" +
             "License and source: <a href=\"" + SourceUrl + "\">" + SourceName + "</a>",
             "Mobile Open Animation for Windows\n" +
             "给 Windows 增加类似智能手机的开屏过渡动画。\n\n" +
             "以 Nico6719/windhawk-mod-mobile-open-animation (AGPL-3.0)\n" +
             "为基础，于 2026 年改写为独立程序。\n\n" +
             "Copyright (C) 2026 CodebyGPT\n" +
             "本程序按 GNU Affero 通用公共许可证第 3 版或更高版本发布，\n" +
             "不提供任何担保。\n" +
             "许可证全文与源代码：<a href=\"" + SourceUrl + "\">" + SourceName + "</a>"),
    };

    /// <summary>
    /// Where the program lives, and the one place it is written down. The About box's link and the
    /// check-for-updates button are both built from these, so the address appears once in the source.
    /// </summary>
    public const string SourceUrl =
        "https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows";
    public const string SourceName = "github.com/CodebyGPT/Mobile_Open_Animation_for_Windows";

    public static string T(string key)
    {
        if (!Map.TryGetValue(key, out var v)) return key;
        return _lang == Lang.English ? v.en : v.zh;
    }
}
