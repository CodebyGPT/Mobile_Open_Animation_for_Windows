// When a program that has already shown an advertisement may show another one.
//
// The rule is arithmetic: how long it has been since that program's last advertisement, and the three numbers
// the user sets - the unit of time, how much the chance grows per unit, and what it is reset to when an
// advertisement plays. It lives in its own file, with no window and no interop in it, so that the arithmetic
// can be put through every case in probe/adrule without a screen.
//
// The same file holds the two masked fields the rule's window edits, as functions over text: what a keystroke
// is allowed to make of them, and whether what is there yet is a number. A field that is not complete is a
// field somebody is still typing in, and that is what the defaults are for - see Amount.

using System.Globalization;
using System.Text;

namespace Moa;

internal static class AdRule
{
    /// <summary>The unit the chance grows per: the A of the rule's sentence.</summary>
    internal enum Unit { Second, Minute, Hour, Day }

    internal const Unit DefaultUnit = Unit.Minute;
    internal const double DefaultIncrease = 1.0;
    internal const double DefaultReset = 0.0;

    /// <summary>The unit's name, which is how it is written in the ini and said in the log.</summary>
    internal static string Name(Unit unit) => unit switch
    {
        Unit.Second => "second",
        Unit.Hour => "hour",
        Unit.Day => "day",
        _ => "minute",
    };

    /// <summary>
    /// The unit a name means, false when it is not one of the four. False rather than an exception because the
    /// name comes out of a text file a user may have edited, and an ini with a word that is not a unit in it has
    /// to behave like one that has no rule in it at all.
    /// </summary>
    internal static bool TryParse(string name, out Unit unit)
    {
        foreach (Unit u in new[] { Unit.Second, Unit.Minute, Unit.Hour, Unit.Day })
        {
            if (string.Equals(Name(u), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                unit = u;
                return true;
            }
        }
        unit = DefaultUnit;
        return false;
    }

    internal static long UnitMs(Unit unit) => unit switch
    {
        Unit.Second => 1000L,
        Unit.Hour => 3600000L,
        Unit.Day => 86400000L,
        _ => 60000L,
    };

    /// <summary>
    /// The chance, in percent, that this program may have another advertisement, from how long it has been since
    /// its last one.
    ///
    /// Whole units only, which is what "every minute add one percent" means: the chance steps rather than
    /// creeping, so the number the window shows is the number in force. The elapsed time is wall clock rather
    /// than a tick count, on purpose - a user who moves the system clock an hour forward is asking for the hour
    /// to have passed, and one who moves it back gets a negative chance, which never plays. That is the "less
    /// than or equal to nought" end of the rule and it is reached by nothing else.
    /// </summary>
    internal static double Chance(Unit unit, double increase, double reset, double elapsedMs)
        => reset + increase * Math.Floor(elapsedMs / UnitMs(unit));

    /// <summary>
    /// Whether that chance says yes this time. Nought or less never, a hundred or more always, and in between a
    /// roll - so both ends of the range are not special cases of the arithmetic but of what the number means,
    /// and a rule the user dragged out to a certainty or down to nothing does exactly that.
    /// </summary>
    internal static bool SaysYes(double chancePercent, double roll)
        => chancePercent >= 100 || (chancePercent > 0 && roll * 100 < chancePercent);

    // ---- the two fields the window edits ------------------------------------------------------------------
    //
    // The masks are "00.00" and "000", and neither of them is a WinForms control: this program has no WinForms
    // and draws its own windows. So the mask is a function over text - everything a keystroke can make of the
    // field passes through Fix, and what comes out is what the field is set to. Complete is the same question
    // WinForms' MaskCompleted asks: is every position of the mask filled, which is the difference between a
    // number and somebody halfway through typing one.

    /// <summary>
    /// The increase, mask "00.00": digits with one point, at most two of them either side of it, and never more
    /// than 99.99. A letter, a second point or a third digit before the point simply does not appear.
    /// </summary>
    internal static string FixIncrease(string text)
    {
        var sb = new StringBuilder();
        bool point = false;
        int before = 0, after = 0;

        foreach (char c in text ?? "")
        {
            if (c >= '0' && c <= '9')
            {
                if (!point) { if (before == 2) continue; before++; }
                else { if (after == 2) continue; after++; }
                sb.Append(c);
            }
            else if ((c == '.' || c == ',') && !point && before > 0)
            {
                point = true;
                sb.Append('.');
            }
        }

        string s = sb.ToString();
        // The largest the field holds. Only a whole five-character field can be over it, so the comparison is
        // safe on the string itself.
        return s.Length == 5 && string.CompareOrdinal(s, "99.99") > 0 ? "99.99" : s;
    }

    /// <summary>Whether the increase field holds the whole mask, which is what makes it a number.</summary>
    internal static bool IncreaseComplete(string text)
        => text != null && text.Length == 5 && text[2] == '.' && Digits(text, 0, 2) && Digits(text, 3, 2);

    /// <summary>
    /// The reset, mask "000": up to three digits, and anything over 100 is written down as 100 - a chance
    /// greater than certain is not a thing to be typed in, and correcting it is friendlier than refusing it.
    /// </summary>
    internal static string FixReset(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text ?? "")
            if (c >= '0' && c <= '9' && sb.Length < 3) sb.Append(c);

        string s = sb.ToString();
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 100
            ? "100" : s;
    }

    internal static bool ResetComplete(string text)
        => text != null && text.Length == 3 && Digits(text, 0, 3);

    /// <summary>
    /// The number a field means, or the default when the field is not complete. This is the rule for a field
    /// somebody is still typing in: an unfinished number is not a number, and half of "15.00" must not be read
    /// as one percent or fifteen.
    /// </summary>
    internal static double IncreaseOf(string text, double fallback)
        => IncreaseComplete(text) &&
           double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

    internal static double ResetOf(string text, double fallback)
        => ResetComplete(text) &&
           int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    /// <summary>A number as the increase field shows it: one is "01.00", because the mask is five wide.</summary>
    internal static string ShowIncrease(double value)
    {
        int hundredths = (int)Math.Round(Bound(value, 0, 99.99) * 100);
        return (hundredths / 100).ToString("00", CultureInfo.InvariantCulture) + "." +
               (hundredths % 100).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>And as the reset field shows it: nought is "000".</summary>
    internal static string ShowReset(double value)
        => ((int)Math.Round(Bound(value, 0, 100))).ToString("000", CultureInfo.InvariantCulture);

    /// <summary>Kept here rather than Math.Clamp, which .NET Framework 4.8 does not have.</summary>
    private static double Bound(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    private static bool Digits(string s, int from, int count)
    {
        if (s.Length < from + count) return false;
        for (int i = from; i < from + count; i++)
            if (s[i] < '0' || s[i] > '9') return false;
        return true;
    }
}
