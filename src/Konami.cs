// The advertisement code: the thirteen keys that reveal the tray menu's 播放广告 entry, and the three
// seconds allowed between one of them and the next.
//
// The sequence and the clock live here, away from the dialog they drive, because the part worth
// testing is the edge cases - what a wrong key does as against what a slow one does - and testing
// those should not need a task dialog on screen. See probe/konami, which compiles this file.
//
// The sequence is the old arcade one, read left to right:
//
//   Up Up Down Down Left Left Right Right A B A B Enter
//
// and every key that lands plays one step of the box's reaction, which is why the keys and the
// steps are numbered the same way: key 1 is "put the text back and step up", key 13 is "close and
// open the menu". See AboutDialog for what each step does.

namespace Moa;

internal static class Konami
{
    /// <summary>How many keys there are, and therefore how many steps of feedback.</summary>
    public const int Steps = 13;

    /// <summary>
    /// How long a key may be followed by nothing before the attempt is over.
    ///
    /// Between one key and the next, not over the whole sequence. Three seconds for all thirteen was the
    /// first reading and it is not one a person can hit: it averages 230 ms a key, and the natural thing
    /// to do after one of the steps that is worth looking at - the text going to A - is to stop and look
    /// at it. Measured, that is exactly where the sequence died: the key after the pause was refused.
    /// </summary>
    public const int WindowMs = 3000;

    private const uint Up = 0x26, Down = 0x28, Left = 0x25, Right = 0x27, Enter = 0x0D;

    private static readonly uint[] Code =
    {
        Up, Up, Down, Down, Left, Left, Right, Right,
        (uint)'A', (uint)'B', (uint)'A', (uint)'B', Enter,
    };

    /// <summary>What ended the last attempt, for the log. Nothing did, unless this says so.</summary>
    public const int DropNone = 0, DropTooLate = 1, DropWrongKey = 2;

    /// <summary>
    /// Why the last key ended an attempt instead of advancing one, or <see cref="DropNone" />. Written on
    /// every press and read only when the press earned nothing, which is the one case where "nothing
    /// happened" is a question - a sequence that stops without saying why is the whole reason this exists.
    /// </summary>
    public static int LastDrop;

    /// <summary>How many keys of the code are in. 0 means no attempt is running.</summary>
    private static int _at;

    /// <summary>When the last key of the current attempt was seen, on the caller's clock.</summary>
    private static long _last;

    /// <summary>Throws away a half-typed attempt, for a dialog that is opening or closing.</summary>
    public static void Reset()
    {
        _at = 0;
        LastDrop = DropNone;
    }

    /// <summary>
    /// Feeds one key press and says which step of the feedback it earned: 1 to <see cref="Steps" />, or 0
    /// when nothing should happen.
    ///
    /// A wrong key and a key that arrives too late do the same thing, which is the point of there being
    /// one method: the attempt stops where it is, and the key that ended it is then judged again as a
    /// first key. So retrying with the code's own first key starts a fresh attempt and plays step 1 -
    /// and step 1 is the one that puts the box's text back, which is why a half-finished attempt cannot
    /// leave the box looking wrong.
    /// </summary>
    public static int Press(uint key, long nowMs)
    {
        LastDrop = DropNone;

        if (_at > 0 && nowMs - _last > WindowMs)
        {
            _at = 0;
            LastDrop = DropTooLate;
        }
        else if (_at > 0 && key != Code[_at])
        {
            _at = 0;
            LastDrop = DropWrongKey;
        }

        if (_at == 0)
        {
            if (key != Code[0]) return 0;
            _at = 1;
            _last = nowMs;
            return 1;
        }

        _at++;
        _last = nowMs;
        if (_at < Steps) return _at;
        _at = 0;
        return Steps;
    }
}
