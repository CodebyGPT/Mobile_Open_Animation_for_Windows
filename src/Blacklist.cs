// The blacklist, and where the panel's list of recent records comes from.
//
// The records are read out of the log rather than picked off the screen, and that is what makes this possible
// without any new bookkeeping: the log already records every window that reached the animation decision, with
// its class name and the process it belongs to, and those two names are exactly what the blacklist is made
// of. Reading it also makes the list a record of what actually happened rather than of what happened to be on
// screen while the panel was open, and it cannot be fooled by a window handle that has been reused since -
// the reason a handle is not what gets stored.
//
// Nothing here takes typed input. A window class is a name nobody remembers, and a mistyped one would
// blacklist nothing while looking like it should.

using System.Text.RegularExpressions;

namespace Moa;

internal static class Blacklist
{
    /// <summary>One window out of the log: what the panel shows, and what a rule is made of.</summary>
    internal sealed class Candidate
    {
        public string Time = "", Class = "", Process = "";
        public int Width, Height;
    }

    /// <summary>
    /// One rule: which list it came from, its text as the ini holds it, and whether it is one of the defaults
    /// that travel with the program. Built-in rules can be switched off but not deleted - a rule that is part
    /// of the program's own judgement should come back when the user has forgotten why they removed it - and
    /// they cannot be turned into the other kind either, for the same reason.
    /// </summary>
    internal sealed class Rule
    {
        public bool ByProcess;
        public bool BuiltIn;
        public string Raw = "";

        public string Name => Raw.Length > 0 && Raw[0] == Settings.DisabledMark ? Raw.Substring(1) : Raw;
        public bool Enabled => Raw.Length == 0 || Raw[0] != Settings.DisabledMark;
    }

    /// <summary>
    /// How many records the log is scanned for. A debug session can write tens of thousands of lines and the
    /// panel reads them on the message loop, so the scan is bounded by records rather than by bytes: the last
    /// few hundred is more than anyone looks through, and stops a long-running log from turning a menu click
    /// into something that stalls the program.
    /// </summary>
    private const int MaxRecords = 250;

    /// <summary>
    /// The line Animator writes for every window it considers, matched from the end because the two names in
    /// the middle can contain spaces and nothing after them can.
    /// </summary>
    private static readonly Regex CandidateLine = new(
        @"candidate hwnd=\d+ pid=\d+ proc=(?<proc>.*?) cls=(?<cls>.*?) (?<w>\d+)x(?<h>\d+) at -?\d+,-?\d+$",
        RegexOptions.Compiled);

    /// <summary>
    /// What the log said, read once and kept: every distinct record, newest first, whether or not a rule
    /// covers it, and the two maps that pair class names with process names.
    ///
    /// This is the expensive half and it does not belong in the path a button press takes. Reading and parsing
    /// the log files measures at around six milliseconds on a few hundred kilobytes of them - which is most of
    /// a frame, on the same thread that is dispatching the animation's frames, so a click in the panel was
    /// making the animation behind it stutter. Nothing here changes while the panel is open, so it is read
    /// when the panel opens and when the user asks for it, and every other rebuild filters this instead.
    /// </summary>
    private static readonly List<Candidate> Cached = new();

    /// <summary>Reads the log files and rebuilds everything that comes from them, once per open and refresh.</summary>
    public static void Reload()
    {
        // The newest records are still in memory: without this the list would lag behind by whatever the
        // buffer and the flush interval decided.
        Log.Flush(force: true);
        Cached.Clear();
        ClassOwner.Clear();
        ProcessClass.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int records = 0;
        foreach (var file in LogFiles())
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (int i = lines.Length - 1; i >= 0 && records < MaxRecords; i--)
            {
                var m = CandidateLine.Match(lines[i]);
                if (!m.Success) continue;
                records++;

                string cls = m.Groups["cls"].Value, proc = m.Groups["proc"].Value;
                // Newest first, so the first sighting of a class is the most recent one and later, older ones
                // do not overwrite it.
                if (!ClassOwner.ContainsKey(cls)) ClassOwner[cls] = proc;
                if (!ProcessClass.ContainsKey(proc)) ProcessClass[proc] = cls;
                if (!seen.Add(cls + "\n" + proc)) continue;

                Cached.Add(new Candidate
                {
                    Time = lines[i].Length >= 12 ? lines[i].Substring(0, 12) : "",
                    Class = cls,
                    Process = proc,
                    Width = int.Parse(m.Groups["w"].Value),
                    Height = int.Parse(m.Groups["h"].Value),
                });
            }
            if (records >= MaxRecords) break;
        }
    }

    /// <summary>
    /// The cached records that no rule covers, newest first, which is what the panel's left list shows and
    /// what every button press rebuilds.
    ///
    /// A record that any rule matches is not shown at all: that is what makes a rule feel like it did
    /// something. It is matched here rather than marked in the panel so that the same record comes back the
    /// moment the rule is switched off, and so that nothing has to be written back into the log - the log
    /// stays a record of what happened, which is the only reason it can be trusted. Filtering a few hundred
    /// cached records is a string comparison each, which is why doing it on every click is free.
    /// </summary>
    public static List<Candidate> Recent()
    {
        var found = new List<Candidate>();
        foreach (var c in Cached)
            if (!Blocked(c)) found.Add(c);
        return found;
    }

    /// <summary>
    /// Every build writes its own log, named after itself, so "the log" is several files once the program has
    /// been updated. Newest first, by the order they were written in rather than by name, and the older ones
    /// are still read: a window that annoyed someone yesterday is worth offering today, and the log of the
    /// build that is running may not have caught it yet.
    /// </summary>
    private static IEnumerable<string> LogFiles() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "MobileOpenAnimation*.log")
                 .Select(f => new FileInfo(f))
                 .OrderByDescending(f => f.LastWriteTimeUtc)
                 .Select(f => f.FullName);

    /// <summary>How many log files the records could have come from, for the panel's own log line.</summary>
    public static int LogFileCount() => LogFiles().Count();

    /// <summary>
    /// Which process each window class was last seen in, filled while the log is read.
    ///
    /// A rule is stored as one name - a class or a process - and a class name like
    /// WindowsForms10.Window.8.app.0.226dcf_r3_ad1 says nothing about who owns it. The log does: every record
    /// names both, so the panel can show the process a class rule came from. It is the record's own account of
    /// itself rather than a guess at the class name.
    ///
    /// Filled from every record the scan saw, before the rules are allowed to hide any of them: the case this
    /// exists for is exactly the record that is now covered by a rule, so it cannot be searching the list that
    /// rule removed it from.
    /// </summary>
    private static readonly Dictionary<string, string> ClassOwner = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The other half of the same pairing: the class each process was last seen with.
    ///
    /// One process can own several classes - a window, a tool palette, a splash - so this is the newest of
    /// them rather than all of them, and that is the one the conversion should follow: the rule being turned
    /// into a class rule was made from a record, and the newest class of that process is that record's class
    /// unless something has happened since.
    /// </summary>
    private static readonly Dictionary<string, string> ProcessClass = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The process a class was last seen in, or "" when the log has nothing to say about it.</summary>
    public static string OwnerOfClass(string cls) =>
        ClassOwner.TryGetValue(cls, out var proc) ? proc : "";

    /// <summary>The class a process was last seen with, or "" when the log has nothing to say about it.</summary>
    public static string ClassOfProcess(string proc) =>
        ProcessClass.TryGetValue(proc, out var cls) ? cls : "";

    /// <summary>
    /// What this rule would be called if it were the other kind: a class rule's process, or a process rule's
    /// class. "" means the log knows of no such pair, and then there is nothing to convert to - a rule typed
    /// into the ini by hand may be the only thing that ever used it.
    /// </summary>
    public static string Counterpart(Rule r) =>
        r.ByProcess ? ClassOfProcess(r.Name) : OwnerOfClass(r.Name);

    /// <summary>
    /// Whether an enabled process rule already covers this rule. Only a class rule can be covered: a process
    /// rule is the wider of the two, so blocking the process already blocks everything its classes did.
    /// Shown rather than prevented, because the class rule is not redundant if the process rule is later
    /// removed or switched off - it is doing its job quietly.
    /// </summary>
    public static bool Covered(Rule r)
    {
        if (r.ByProcess) return false;
        if (!r.Enabled) return false;
        string proc = OwnerOfClass(r.Name);
        if (proc.Length == 0) return false;
        foreach (var p in Program.S.ExcludedProcesses)
        {
            int off = p.Length > 0 && p[0] == Settings.DisabledMark ? 1 : 0;
            if (off != 0) continue;   // a switched-off process rule covers nothing
            if (string.Compare(p, off, proc, 0, proc.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        }
        return false;
    }

    /// <summary>Whether a rule already covers this record, by either of its names.</summary>
    public static bool Blocked(Candidate c) =>
        Settings.Matches(Program.S.ExcludedClasses, c.Class) ||
        Settings.Matches(Program.S.ExcludedProcesses, c.Process);

    /// <summary>
    /// The rules, oldest first: the order they were added in, which the panel shows with the newest at the
    /// bottom. A rule the ledger does not mention is treated as the oldest, which is what it is - an entry
    /// from an ini written before any of this existed, or one typed in by hand.
    /// </summary>
    public static List<Rule> Rules()
    {
        var rules = new List<Rule>();
        foreach (var raw in Program.S.ExcludedClasses)
            rules.Add(new Rule { ByProcess = false, BuiltIn = IsBuiltIn(raw, false), Raw = raw });
        foreach (var raw in Program.S.ExcludedProcesses)
            rules.Add(new Rule { ByProcess = true, BuiltIn = IsBuiltIn(raw, true), Raw = raw });

        var order = Program.S.ExcludeOrder;
        int Rank(Rule r)
        {
            string want = (r.ByProcess ? "process:" : "class:") + r.Name;
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i], want, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        // OrderByDescending on the rank would put the rules with no entry last; -1 sorts them first, so the
        // sort is by rank with the unranked treated as older than everything.
        return rules.OrderBy(r => Rank(r) < 0 ? int.MinValue : Rank(r)).ToList();
    }

    /// <summary>Whether a name is one of the defaults the program ships with.</summary>
    public static bool IsBuiltIn(string raw, bool byProcess)
    {
        int off = raw.Length > 0 && raw[0] == Settings.DisabledMark ? 1 : 0;
        string name = raw.Substring(off);
        var defaults = byProcess ? Settings.DefaultExcludedProcesses : Settings.DefaultExcludedClasses;
        foreach (var d in defaults)
            if (string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Adds a name to the list it belongs in, at the end of the ledger, which is where the newest rule goes. A
    /// name that is already there - switched off, most likely - is switched back on in place: it is the same
    /// decision, and two entries that differ only by a mark would be a blacklist nobody could read.
    /// </summary>
    public static void Add(string name, bool byProcess)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var list = ListFor(byProcess);
        int at = IndexOf(list, name);
        if (at >= 0) list[at] = name;
        else
        {
            list.Add(name);
            Program.S.ExcludeOrder.Add($"{(byProcess ? "process" : "class")}:{name}");
        }
        Program.S.Save();
        Log.Write($"blacklist: {(byProcess ? "process" : "class")} '{name}' blacklisted");
    }

    /// <summary>Switches one rule off, or back on. It keeps its place in the ledger: a rule that is switched
    /// off is still the rule it was, and the panel greys it where it stands.</summary>
    public static void Toggle(Rule r)
    {
        var list = ListFor(r.ByProcess);
        int at = IndexOf(list, r.Name);
        if (at < 0) return;
        list[at] = r.Enabled ? Settings.DisabledMark + r.Name : r.Name;
        Program.S.Save();
        Log.Write($"blacklist: '{r.Name}' switched {(r.Enabled ? "off" : "on")}");
    }

    /// <summary>
    /// Turns a rule into the other kind: a window class becomes the whole process, or the other way round.
    ///
    /// The name is *translated*, not moved. "Class" and "process" rules are named by two different things -
    /// WindowsForms10.Window.8.app.0.226dcf_r3_ad1 and ShareX are the same window seen two ways - so carrying
    /// the string across would leave a class name in the process list, where it matches nothing at all. The
    /// pairing comes from the log, which records both names on every line; and where the log knows no
    /// counterpart the conversion does not happen, which is why the panel greys that button out.
    ///
    /// In place, which is the point of the ledger: the rule is not added again, so it does not move to the
    /// bottom of the panel and does not look like a new rule. Its switched-off state comes along with it.
    /// </summary>
    public static void Convert(Rule r)
    {
        string to = Counterpart(r);
        if (to.Length == 0) return;
        bool toProcess = !r.ByProcess;
        var from = ListFor(r.ByProcess);
        var into = ListFor(toProcess);
        int at = IndexOf(from, r.Name);
        if (at < 0) return;

        from.RemoveAt(at);
        int there = IndexOf(into, to);
        if (there >= 0) into[there] = r.Enabled ? to : Settings.DisabledMark + to;
        else into.Add(r.Enabled ? to : Settings.DisabledMark + to);

        for (int i = 0; i < Program.S.ExcludeOrder.Count; i++)
        {
            string want = $"{(r.ByProcess ? "process" : "class")}:{r.Name}";
            if (string.Equals(Program.S.ExcludeOrder[i], want, StringComparison.OrdinalIgnoreCase))
            {
                Program.S.ExcludeOrder[i] = $"{(toProcess ? "process" : "class")}:{to}";
                break;
            }
        }

        Program.S.Save();
        Log.Write($"blacklist: '{r.Name}' is now a {(toProcess ? "process" : "class")} rule named '{to}'");
    }

    public static void Remove(Rule r)
    {
        var list = ListFor(r.ByProcess);
        int at = IndexOf(list, r.Name);
        if (at < 0) return;
        list.RemoveAt(at);
        string want = $"{(r.ByProcess ? "process" : "class")}:{r.Name}";
        for (int i = Program.S.ExcludeOrder.Count - 1; i >= 0; i--)
            if (string.Equals(Program.S.ExcludeOrder[i], want, StringComparison.OrdinalIgnoreCase))
                Program.S.ExcludeOrder.RemoveAt(i);
        Program.S.Save();
        Log.Write($"blacklist: '{r.Name}' deleted");
    }

    private static List<string> ListFor(bool byProcess) =>
        byProcess ? Program.S.ExcludedProcesses : Program.S.ExcludedClasses;

    /// <summary>The index of an entry by name, whether or not it is switched off.</summary>
    private static int IndexOf(List<string> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            string raw = list[i];
            int off = raw.Length > 0 && raw[0] == Settings.DisabledMark ? 1 : 0;
            if (raw.Length - off == name.Length &&
                string.Compare(raw, off, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return i;
        }
        return -1;
    }
}
