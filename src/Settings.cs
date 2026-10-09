// Settings live in an .ini next to the exe. There is deliberately no settings window:
// the tray menu handles toggles and enums, and this file handles the lists that a menu
// cannot reasonably edit (excluded apps and window classes).
using System.Text;

namespace Moa;

internal enum Lang { English = 0, ChineseSimplified = 1 }

internal sealed class Settings
{
    // ---- menu-editable ----------------------------------------------------------------
    public Lang Language = Lang.English;
    public bool EnableAds = false;

    /// <summary>
    /// Whether a window animates as it closes. On by default, and checked per close rather than at
    /// startup, so turning it off takes effect on the very next window.
    /// </summary>
    public bool CloseAnimation = true;

    /// <summary>
    /// Dynamic rounding: whether the card's corner animates as the card grows or shrinks.
    ///
    /// On, an opening card begins as a circle - the roundest shape a card of that size can hold - and squares
    /// off into the window's own corner as it grows, while a closing card goes the other way and ends as a
    /// circle. Off, the card keeps the corner the window itself has for the whole of its life: square on
    /// Windows 10, eight pixels on Windows 11, so the shape never changes and only its size does.
    ///
    /// The default follows the shape the card settles into, which is what makes it differ by version: on for
    /// Windows 11, whose windows have an eight pixel round corner, so the card arrives at a shape that is
    /// already rounded; off for Windows 10, whose windows are square, where the same animation would land on
    /// a square corner - a larger change to look at than a default should be making on its own. An ini that
    /// does not mention this key takes that default, so the usual "missing means off" does not apply here.
    /// </summary>
    public bool DynamicCorner = Native.IsWindows11;

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

    // ---- lists ------------------------------------------------------------------------
    public readonly List<string> ExcludedClasses = new();
    public readonly List<string> ExcludedProcesses = new();

    public static string IniPath =>
        Path.Combine(AppContext.BaseDirectory, "MobileOpenAnimation.ini");

    /// <summary>
    /// Bumped whenever a default changes. An existing .ini overrides the defaults, so
    /// without this a changed default silently has no effect on anyone who has already run
    /// the program once - which is exactly how "the timeout is still 8 seconds" happened.
    /// </summary>
    public const int CurrentConfigVersion = 2;

    public static Settings Load()
    {
        var s = new Settings();
        s.ExcludedClasses.AddRange(DefaultExcludedClasses);
        s.ExcludedProcesses.AddRange(DefaultExcludedProcesses);
        if (!File.Exists(IniPath)) { s.Save(); return s; }

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
                case "enableads": s.EnableAds = Bool(val, s.EnableAds); break;
                case "closeanimation": s.CloseAnimation = Bool(val, s.CloseAnimation); break;
                case "dynamiccorner": s.DynamicCorner = Bool(val, s.DynamicCorner); break;
                case "returntoorigin": s.ReturnToOrigin = Bool(val, s.ReturnToOrigin); break;
                case "debugmode": s.DebugMode = Bool(val, s.DebugMode); break;
                case "startsize": s.StartSizePx = Clamp(Int(val, s.StartSizePx), 16, 800); break;
                case "duration": s.DurationMs = Clamp(Int(val, s.DurationMs), 30, 2000); break;
                case "handofffade": s.HandoffFadeMs = Clamp(Int(val, s.HandoffFadeMs), 0, 1000); break;
                case "readytimeout": s.ReadyTimeoutMs = Clamp(Int(val, s.ReadyTimeoutMs), 500, 20000); break;
                case "maxhidems": s.MaxHideMs = Clamp(Int(val, s.MaxHideMs), 500, 60000); break;
                case "excludeclasses": s.ExcludedClasses.Clear(); AddList(s.ExcludedClasses, val); break;
                case "excludeprocesses": s.ExcludedProcesses.Clear(); AddList(s.ExcludedProcesses, val); break;
            }
        }
        return s;
    }

    public void Save()
    {
        var sb = new StringBuilder();
        // The build identity on the first line, because the first thing anyone does with a settings file from
        // someone else's machine is ask which build wrote it, and it is also what the log's file name says.
        sb.AppendLine($"; Mobile Open Animation for Windows {Build.Version} - window open animation, standalone");
        sb.AppendLine("; The tray menu edits the entries marked [menu]; edit the rest here.");
        sb.AppendLine("; NOTE: Start with Windows is deliberately NOT stored in this file. The state is");
        sb.AppendLine("; whether the scheduled task exists, read from the Task Scheduler, and a copy here");
        sb.AppendLine("; could only ever disagree with it.");
        sb.AppendLine();
        sb.AppendLine("[general]");
        // This line is the whole reason settings survive a restart: Load regenerates the file from
        // defaults whenever the version it finds is older than CurrentConfigVersion, and if Save
        // does not write it the file always looks older, so every launch resets everything.
        sb.AppendLine("; file format version; do not edit. Written by the program.");
        sb.AppendLine($"version={CurrentConfigVersion}");
        sb.AppendLine("; [menu] en or zh-CN");
        sb.AppendLine($"Language={(Language == Lang.English ? "en" : "zh-CN")}");
        sb.AppendLine("; [menu] the satirical launch-ad feature (not implemented yet)");
        sb.AppendLine($"EnableAds={(EnableAds ? 1 : 0)}");
        sb.AppendLine($"CloseAnimation={(CloseAnimation ? 1 : 0)}");
        sb.AppendLine("; [menu] dynamic rounding. On, an opening card begins as a circle and squares off");
        sb.AppendLine("; into the window's own corner as it grows, and a closing card ends as a circle. Off,");
        sb.AppendLine("; the card keeps the window's own corner from start to finish and only its size");
        sb.AppendLine("; changes. Defaults to on for Windows 11 and off for Windows 10, following the corner");
        sb.AppendLine("; it settles into: 11 rounds its windows, 10 does not.");
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
        sb.AppendLine("; window classes that never animate; one per line, comma separated");
        sb.AppendLine("ExcludeClasses=" + string.Join(",", ExcludedClasses));
        sb.AppendLine("; whole processes (exe name without .exe); one per line, comma separated");
        sb.AppendLine("ExcludeProcesses=" + string.Join(",", ExcludedProcesses));
        File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
    }

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
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

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

    private static readonly Dictionary<string, (string en, string zh)> Map = new()
    {
        ["Language"]     = ("Language", "语言"),
        ["OpenConfig"]   = ("Open config directory", "打开配置目录"),
        ["AutoStart"]    = ("Start with Windows", "开机自启"),
        ["CloseAnim"]    = ("Animate windows closing", "窗口关闭动画"),
        ["DynamicCorner"] = ("Dynamic corner radius", "动态圆角"),
        // Named for the desktop because that is where it is most visible, and what it does not cover is
        // worth saying out loud: a launch from the Start menu or from search is refused, because those
        // flyouts are gone by the time the window closes and there would be nothing at that place to
        // return to. The taskbar is covered too, since it is also still there.
        ["ReturnToOrigin"] = ("Desktop return animation (experimental)",
                              "桌面回退动画（实验性）"),
        // A submenu rather than a toggle, and one entry in it so far: what belongs under it is any number
        // of separate diagnostics, each of which can be turned on without the others.
        ["DebugMode"]    = ("Debug mode", "调试模式"),
        ["DebugEnable"]  = ("Enable", "启用"),
        ["AutoStartFailed"] =
            ("Could not change the Start with Windows setting, so it has been left as it was.",
             "无法修改开机自启设置，已保持原状。"),
        ["AdminRequired"] =
            ("Mobile Open Animation for Windows must run as an administrator.\n\n" +
             "Without it, Windows' user interface privilege isolation stops this program from\n" +
             "hiding the windows of most applications, so nothing would animate.",
             "Mobile Open Animation for Windows 必须以管理员身份运行。\n\n" +
             "否则 Windows 的用户界面特权隔离(UIPI)会阻止它隐藏大多数程序的窗口，\n" +
             "动画将完全不会生效。"),
        ["About"]        = ("About", "关于"),
        ["Version"]      = ("Version", "版本"),
        ["Exit"]         = ("Exit", "退出"),
        // The About text is not decoration: AGPL-3.0 section 0 requires an interactive interface
        // to show appropriate legal notices - a copyright notice, a statement that there is no
        // warranty, that the work may be conveyed under the licence, and how to view it - and
        // section 5a requires a notice that the work was modified, with a date. The tray menu's
        // About entry is the prominent item that satisfies it.
        ["AboutText"] =
            ("Mobile Open Animation for Windows\n" +
             "Adds a smartphone-style open animation to Windows.\n\n" +
             "Built on Nico6719/windhawk-mod-mobile-open-animation (AGPL-3.0),\n" +
             "rewritten as a standalone program in 2026.\n\n" +
             "Copyright (C) 2026 CodebyGPT\n" +
             "Released under the GNU Affero General Public License, version 3 or\n" +
             "later, with NO WARRANTY.\n" +
             "License and source: https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows",
             "Mobile Open Animation for Windows\n" +
             "给 Windows 增加类似智能手机的开屏过渡动画。\n\n" +
             "以 Nico6719/windhawk-mod-mobile-open-animation (AGPL-3.0)\n" +
             "为基础，于 2026 年改写为独立程序。\n\n" +
             "Copyright (C) 2026 CodebyGPT\n" +
             "本程序按 GNU Affero 通用公共许可证第 3 版或更高版本发布，\n" +
             "不提供任何担保。\n" +
             "许可证全文与源代码：https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows"),
    };

    public static string T(string key)
    {
        if (!Map.TryGetValue(key, out var v)) return key;
        return _lang == Lang.English ? v.en : v.zh;
    }
}
