// The card an advertisement is shown on: a borderless window that plays a video inside itself, with a mute
// control, a countdown and the name of what is playing, and goes away when the countdown runs out - or when one
// of those controls is clicked.
//
// It takes over the rectangle the opening animation stopped at: the window advertisement, which is the only
// kind there is now. There used to be a logon one as well, covering the primary screen before the desktop was
// usable, and it is gone - with it went the argument the start-up launcher passed, the switch that refused to
// be switched on without a logon, and the rule that a click anywhere dismissed that card.
//
// The window itself does nothing clever. The video is Media Foundation's, drawn into a child window of its own
// (see Media.cs); what is here is the part that is ours - where it appears, what it says, and when it goes.
//
// Six decisions worth writing down, because each of them is the opposite of the obvious one:
//
//   * It does not animate. It appears where it belongs the moment it is made and leaves by fading out where it
//     stands. Both ends used to be a movement - a logon card grew out of the middle of the screen, and this
//     kind collapsed into a small square and rounded off into a disc as it went - and everything that movement
//     dragged in went with it: a region recomputed on every frame, a frame rate to report, a clock to sample, a
//     shape to arrive in that the card being covered had to agree with. An advertisement is not one of the
//     animations this program exists to draw, and it reads better as a card that is simply there and then is
//     not. The one piece of the old movement worth keeping is the fade at the end, which is now the whole of the
//     way out; see Fade.
//
//   * WS_EX_NOACTIVATE. An advertisement that takes the foreground takes the keyboard with it, so whatever the
//     user was typing into stops receiving it. The card is created visible and never activates.
//   * The controls are windows of their own, owned by the card rather than children of it, and they are drawn
//     by Capsule rather than by the system. Measured: a child is not visible over the video whatever its
//     z-order, and a BUTTON takes the click so the card never hears it - which made pressing mute dismiss the
//     advertisement. They are also transparent to the mouse, so a click on one still arrives here.
//   * The counts are worked out from where the cursor is rather than from which window received the click,
//     because which window receives it turned out to be unreliable. See ActingOn and ControlAt.
//   * A click acts when it is let go, and the mouse is captured for as long as it is held. Both halves of that
//     are forced rather than chosen: a button is asked whether the press is still on it before it does
//     anything, which is what makes the press state it shows mean something, and the release is only ever seen
//     by whoever holds the capture - see Released.
//   * The countdown is counted from a deadline rather than by decrementing on each tick. A timer is not
//     guaranteed its interval, and a countdown that drifts is a countdown that lies about how long is left.
//   * The card is shown, not asked to show itself: nothing else ever makes this window visible, and since
//     nothing about it moves there is no step with SWP_SHOWWINDOW in it either. It sat hidden for the whole
//     countdown once, with the only thing that ever made it appear being the shrink at the end of it. See the
//     WS_VISIBLE below.
//
// probe/media drives all of this for real, so it can be looked at without the elevated tray program.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Moa;

internal sealed class AdsCard
{
    /// <summary>
    /// Where a card says what it is doing. The program points this at Log.Write and a probe at the console,
    /// because this file cannot call Log itself without pulling the program's logging into every probe.
    /// </summary>
    public static Action<string>? Trace;

    /// <summary>
    /// What the two controls say, set from Strings by the program. This file has no idea what language the rest
    /// of the interface is in and cannot look it up without the same dependency.
    ///
    /// <see cref="CaptionRemaining" /> is a format with the whole seconds in it, and <see cref="CaptionSkip" />
    /// is the second half of the countdown capsule - which is not a word for how much is left but for what the
    /// button does when it is pressed, and is there because a capsule with one label in it cannot hold both of
    /// the shape rule's ends at once. See Capsule.Measure.
    /// </summary>
    public static string CaptionMute = "Mute", CaptionUnmute = "Unmute";
    public static string CaptionRemaining = "{0}s left", CaptionSkip = "Skip";

    /// <summary>
    /// What the countdown says before the length of the file is known: "--", and nothing else.
    ///
    /// There is always a moment of this - the file is opened, and how long it is is a question for the player,
    /// which answers when it is ready, hundreds of milliseconds later. What it used to say instead was the cap
    /// - "<c>360s left</c>" - and that was a number the advertisement had just made up: it is the longest a
    /// card may stay up, not the length of what is playing. It read as a countdown that started from the cap
    /// and then jumped to the real figure, which is exactly what it was. Reported as the first frame of every
    /// advertisement saying 360.
    ///
    /// Not a format, because there is no number to put in it; see <see cref="CaptionRemaining" /> for that one.
    /// </summary>
    public static string CaptionPending = "--";

    /// <summary>
    /// What the search capsule says in front of the file's own name, in bold: "搜一搜". It is the words of the
    /// control - what pressing it does - and the name after it is what it is about; the shortest word that says it
    /// rather than a sentence, because the name it introduces is the thing being shown. Set from Strings by the
    /// program like the rest.
    /// </summary>
    public static string CaptionSearch = "Search";

    /// <summary>
    /// Where a mute the user asked for is written down. Set whenever the caller means to remember it, which for
    /// the program is always: a mute control that forgets between showings is a mute control the user has to
    /// use again every time.
    /// </summary>
    public static Action<bool>? MuteRemembered;

    /// <summary>
    /// Where a search asked for by a click on the advertisement's own name is handed over. The program points
    /// this at the shell - see Strings.SearchUrl and AdsCard.SearchUrlFor, which are the two halves of what is
    /// allowed to be handed over - and a probe points it at a recorder so that no browser opens from a test.
    ///
    /// The card itself does not launch anything, and that is on purpose: everything this file can produce is the
    /// URL that <see cref="SearchUrlFor" /> returns, and the one caller of the callback is what does the
    /// launching. Nothing here can be talked into handing the shell a file name or a path.
    /// </summary>
    public static Action<string>? Searched;

    /// <summary>
    /// The engine a click on the advertisement's name searches in: an https URL ending in its own query key, so
    /// that everything after it can only ever be a search term. Set from Strings by the program, which is where
    /// the per-language choice is; the default here is the English one, which is the program's default language.
    /// </summary>
    public static string SearchUrl = "https://www.google.com/search?q=";

    /// <summary>
    /// The frame the fade is stepped at, and the corner the card wears while it is up.
    ///
    /// Both come from the program, because both are decisions it has already made about its own cards: the frame
    /// from the display, and the corner radius from the opening animation this card takes over - which is the
    /// shape the card arrived in, and therefore the shape the advertisement over it has to be. The card cannot
    /// read either of them and a second answer here would be a second thing to keep in step. probe/media sets
    /// its own.
    /// </summary>
    public static int FrameMs = 16, Radius;

    /// <summary>
    /// Called once when the card is gone. The window advertisement keeps the window it covers hidden for as long
    /// as the card is up, so this is where that window is let out again.
    /// </summary>
    public Action? Closed;

    /// <summary>
    /// Called on every one of the card's own steps, sixty times a second, for as long as it is up. The
    /// program uses it to keep the window it is covering hidden: the hide is the guard's and the guard's
    /// watchdog does not re-apply it, so without a caller that runs this often the window can come back
    /// underneath the advertisement.
    /// </summary>
    public Action? WhileUp;

    private const string ClassName = "MoaAdsCard";
    private const int StepMs = 16, HoldMs = 60, FadeMs = 160;
    private static readonly IntPtr StepTimer = (IntPtr)1;

    /// <summary>
    /// When the card's own movements measure from - the fade and the countdown, and nothing else.
    ///
    /// <see cref="Environment.TickCount" /> moves in steps of about 15.6 milliseconds - it is the system timer's
    /// tick, not a millisecond counter - and a fade sampled from it moves in thirteen steps whatever rate it is
    /// asked for: the same opacity is drawn two frames in three and then the card jumps a tick's worth, which is
    /// a fade that stutters while every frame of it is drawn on time. That is what a coarse clock does to a
    /// movement, and it was reported as the collapse being less smooth than it had been.
    ///
    /// A Stopwatch is the system's high-resolution counter and moves in microseconds, so a step that lands early
    /// lands a little early and one that lands late lands a little late. The countdown is on it too: it counts
    /// whole seconds and does not care, but one clock read by two things is one place to look rather than two.
    /// </summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long Now => Clock.ElapsedMilliseconds;

    private static readonly Native.WndProcDelegate Proc = Handle;
    private static bool _registered;
    private static AdsCard? _open;

    private IntPtr _hwnd, _videoHost;
    private Capsule.Style? _style, _nameStyle;
    private Capsule? _mute, _countdown, _name;
    private MediaPlayer? _player;
    private string[] _candidates = Array.Empty<string>();
    private int _next;

    /// <summary>The file that is playing, without its extension: what the name capsule says.</summary>
    private string _file = "";

    /// <summary>That name as it will be shown, shortened once per file rather than once per step.</summary>
    private string _label = "";

    /// <summary>How wide the name capsule may be, in pixels, or 0 before the style is known.</summary>
    private int _labelCeiling;
    private RECT _full;
    private int _capSeconds, _fadeAt, _frame = 16;
    private int _fitX = int.MinValue, _fitY, _fitW, _fitH;   // where the video window was last put
    private uint _timerMs;                                   // the interval the step timer is at
    private long _deadline;              // when the countdown reaches zero, 0 until the player has said how long
    private int _shownSeconds = -1;
    private bool _muted, _closeAnimation, _closing, _done, _controls, _fitSaid;

    /// <summary>What the handover to the last frame took, and whether that has been written down yet.</summary>
    private bool _heldFrame, _heldSaid;

    /// <summary>
    /// The control the mouse went down on, or nothing when it is not held. Kept because the click is decided
    /// when it is let go: a press that is dragged off its own control is a press that was taken back, which is
    /// the one thing a press state is for. Nothing distinguishes "the card" from "nothing at all" here - both
    /// are a press somewhere that is not a control, and both are let go the same way.
    /// </summary>
    private IntPtr _pressOn;

    /// <summary>Where the press was, and where inside the card it was, for telling a click from a drag.</summary>
    private POINT _pressAt;
    private int _grabX, _grabY;

    /// <summary>True once a press on the card itself has moved far enough to be a movement rather than a click.</summary>
    private bool _dragging;

    /// <summary>True while the mouse is captured, which is what makes this window the one the moves arrive at.</summary>
    private bool _held;

    /// <summary>
    /// Told the distance the advertisement has been moved by, so that whatever it is standing in for can be
    /// moved with it. Set by the program, which is the only thing that knows what that is; nothing happens when
    /// it is null, which is what a probe wants.
    /// </summary>
    public Action<int, int>? Moved;

    /// <summary>
    /// The player, for a caller that wants to drive or look at the file itself - which is what probe/media does
    /// with its frame checks. Nothing in the program reads it.
    /// </summary>
    internal MediaPlayer? Player => _player;

    /// <summary>Where the card is, for a caller that moves it.</summary>
    internal RECT Place => _full;

    /// <summary>The card's own window, for a caller that wants to put it somewhere in the Z-order.</summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>
    /// The mute control and the countdown, for a caller that wants to click one of them where it really is.
    /// The card decides what a click meant by where the cursor was, so nothing that cannot put the cursor on a
    /// control can test any of that - and nothing else in this file is worth testing more.
    /// </summary>
    public IntPtr MuteControl => _mute?.Hwnd ?? IntPtr.Zero;
    public IntPtr CountdownControl => _countdown?.Hwnd ?? IntPtr.Zero;

    /// <summary>The capsule that names the advertisement, for a caller that wants to look at it.</summary>
    public IntPtr NameControl => _name?.Hwnd ?? IntPtr.Zero;

    /// <summary>What that capsule is saying, shortened as far as the card needed it to be.</summary>
    internal string LabelText => _label;

    /// <summary>True while a card is on screen, so that a caller does not put up a second one.</summary>
    public static bool Showing => _open != null;

    /// <summary>
    /// The card: it takes over exactly the rectangle the opening animation stopped at, with no movement of its
    /// own.
    ///
    /// Returns null when there is nothing to show, which includes no files and a card already up. The card
    /// belongs to the caller's message loop from here: its timer arrives there, so the caller must be pumping.
    /// </summary>
    public static AdsCard? ShowOver(RECT rect, string[] files, int capSeconds, bool muted, bool closeAnimation)
    {
        if (_open != null || files == null || files.Length == 0) return null;
        if (!Register()) return null;

        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        var card = new AdsCard
        {
            _muted = muted,
            _closeAnimation = closeAnimation,
            _capSeconds = capSeconds,
            _candidates = Shuffled(files),
            _full = rect,
        };

        card._hwnd = Native.CreateWindowExW(
            // WS_EX_LAYERED from the start, at full opacity, so that the card can fade on its way out: a video
            // can only be faded by fading the window it is drawn in, and a window made layered later has its
            // surface rebuilt - a frame of flicker in the middle of the fade it was meant to smooth.
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED,
            ClassName, "Mobile Open Animation",
            // WS_VISIBLE is not decoration here, it is the whole of the card appearing: nothing else ever makes
            // this window visible, and since nothing about it moves there is no step with SWP_SHOWWINDOW in it
            // either. It sat hidden for the whole countdown once, with the only thing that ever made it appear
            // being the shrink at the end of it; the controls were the one part that appeared, because they are
            // shown one at a time below.
            Native.WS_POPUP | Native.WS_CLIPCHILDREN | Native.WS_VISIBLE,
            rect.Left, rect.Top, w, h,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (card._hwnd == IntPtr.Zero) return null;
        Native.SetLayeredWindowAttributes(card._hwnd, 0, 255, Native.LWA_ALPHA);
        Say($"ads: card 0x{card._hwnd:X} topmost=" +
            $"{(Native.GetWindowLong(card._hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0}" +
            $" visible={Native.IsWindowVisible(card._hwnd)}");

        // The video gets a child window of its own rather than the card itself. Handed the card, the player
        // presents straight into it and paints over everything in it. With the picture confined to a child the
        // controls can be its siblings and stay above it.
        card._videoHost = Native.CreateWindowExW(0, "STATIC", "", Native.WS_CHILD | Native.WS_VISIBLE,
            0, 0, w, h, card._hwnd, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (card._videoHost == IntPtr.Zero) { Native.DestroyWindow(card._hwnd); return null; }

        // The shape it wears. The opening animation's card ended at a corner radius - the window's own on
        // Windows 11, square elsewhere - and this card takes over that rectangle, so it wears the same corner: a
        // hard square appearing where a rounded card was is the one seam the fade cannot hide. The radius is the
        // program's to decide and this is only where it is applied.
        Round(card._hwnd, w, h);

        _open = card;
        // The card asks for a frame every frame while it is fading, and the system's clock has to be asked for a
        // fine enough one to give it: without that a six millisecond timer lands on the next fifteen and a half
        // millisecond tick instead, which is every other one, and a fade animating at half its intended rate is
        // what the countdown's own steps used to be before this was added. The program's own animation thread
        // raises it once and never lowers it, so inside the program this is only ever the first advertisement.
        Native.TimeBeginPeriod(1u);
        card._frame = MonitorFrameMs(card._hwnd, FrameMs);
        card.Timer(StepMs);         // nothing to move at speed yet; the countdown is whole seconds
        card.Next();
        Say($"ads: card 0x{card._hwnd:X} at {rect.Left},{rect.Top} {w}x{h}");
        return card;
    }

    private static bool Register()
    {
        if (_registered) return true;
        var cls = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            HInstance = Native.GetModuleHandleW(null),
            LpszClassName = ClassName,
            HCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
            // Black behind the card, so the frames before the video has a picture are a card rather than
            // whatever was underneath.
            HbrBackground = Native.GetStockObject(Native.BLACK_BRUSH),
        };
        _registered = Native.RegisterClassExW(ref cls) != 0;
        return _registered;
    }

    /// <summary>
    /// Opens the next candidate. A file that will not open at all is passed over here and now; one that opens
    /// but turns out to be nothing - the zero-length ones the feature is meant to ignore - is passed over
    /// later, when the player has had time to say how long it is.
    /// </summary>
    private void Next()
    {
        while (_next < _candidates.Length)
        {
            string file = _candidates[_next++];
            var player = MediaPlayer.Open(file, _videoHost, _muted);
            if (player == null || player.Failed)
            {
                Say($"ads: {System.IO.Path.GetFileName(file)} would not open ({player?.Failure}); next");
                player?.Dispose();
                continue;
            }
            _player?.Dispose();
            _player = player;
            _file = System.IO.Path.GetFileNameWithoutExtension(file);
            SetName();
            Say($"ads: playing {System.IO.Path.GetFileName(file)}");
            return;
        }

        Say("ads: nothing in Ads would play");
        Close();
    }

    private void Step()
    {
        WhileUp?.Invoke();
        long now = Now;

        if (_closing)
        {
            // The fade is given a moment before it starts, to let the last frame put up by Close actually be
            // drawn: see there.
            if (now < _fadeAt) return;
            // And what the handover to the last frame did is said here rather than where the seek was asked for.
            // mfplay's SetPosition is asynchronous, so a position read in the same breath is the position
            // playback was at and not the one it was sent to: measured, a card whose countdown cut a file short
            // logged a landing of 1726 ms where it had asked for 14978, and the whole point of the line is that
            // it can be believed.
            if (!_heldSaid)
            {
                _heldSaid = true;
                Say($"ads: the last frame is up: {_heldFrame}, landed at {_player?.PositionMs ?? -1} ms of " +
                    $"{_player?.DurationMs ?? -1}");
            }
            if (!Fade()) return;
            _done = true;
            Native.DestroyWindow(_hwnd);
            return;
        }

        if (!_controls) MakeControls();

        // The cursor is asked where it is rather than told, and this is the only place it is asked. A mouse
        // move over the video goes to the video's own window and not to this one, so a hover state that waited
        // to be sent WM_MOUSEMOVE would never change; the card is already stepping sixteen times a second for
        // the countdown, which is finer than anyone can move a mouse off a button and notice.
        if (_pressOn == IntPtr.Zero) Look();

        // The name capsule's search glyph, swinging on the card's own clock and on the card's own steps: a timer
        // of its own would be a second thing timing the same advertisement, and the card is stepping while it is
        // up anyway. What is being told is only the angle - what the capsule says is set when the file changes,
        // not here. See Capsule.Swing for the four numbers and the shape of the movement, and SetSpin, which
        // does nothing at all on the steps where the angle has not moved.
        _name?.SetSpin(Capsule.Swing(now));
        WatchPlayer();

        if (_deadline == 0) return;
        long left = _deadline - now;
        if (left <= 0) { Say("ads: the countdown reached zero"); Close(); return; }

        int seconds = (int)((left + 999) / 1000);
        if (seconds == _shownSeconds) return;
        _shownSeconds = seconds;
        _countdown?.Set(string.Format(CaptionRemaining, seconds), Capsule.Glyph.None);
    }

    /// <summary>
    /// Once the player has said the item is set, the length is known - and a file whose length is nothing is
    /// one of the invalid ones, so it is passed over rather than shown.
    /// </summary>
    private void WatchPlayer()
    {
        if (_player == null || _deadline != 0 || !_player.Ready) return;

        _player.Refresh();
        if (_player.DurationMs <= 0)
        {
            Say("ads: that file is nothing long, so it does not count as an advertisement; next");
            _player.Dispose();
            _player = null;
            Next();
            return;
        }

        // The countdown is round seconds for the eye, and the deadline is the video's own length, which is not
        // the same thing and was the whole of the black screen at the end. Rounded up - (15019 + 999) / 1000 is
        // sixteen - the card went on showing for nearly a second after the video had run out, and what mfplay
        // shows then is nothing. So: the countdown says sixteen seconds, and the card comes down in fifteen and
        // nineteen hundredths.
        int seconds = Math.Min((_player.DurationMs + 999) / 1000, _capSeconds);
        _deadline = Now + Math.Min(_player.DurationMs, _capSeconds * 1000L);
        _shownSeconds = -1;
        // The video's shape is known only now, and until it is the video window has been the card's own
        // rectangle. Put it right before the first frame rather than during one.
        Native.GetWindowRect(_hwnd, out var card);
        FitVideo(card.Right - card.Left, card.Bottom - card.Top);
        _player.Play();
        Say($"ads: {_player.DurationMs} ms long, the countdown says {seconds} s and the card goes at " +
            $"{Math.Min(_player.DurationMs, _capSeconds * 1000L)} ms");
    }

    /// <summary>
    /// Takes the card's opacity down over <see cref="FadeMs" /> and says whether it is gone. False while there is
    /// any of it left to take down.
    ///
    /// A layered window is the only way a window with a video inside it can be faded at all, and the style is
    /// set when the card is made rather than here: turning it on later makes the compositor build a new surface
    /// for the window, which is a frame of flicker in the middle of the fade it was meant to smooth. The separate
    /// windows the controls live in are hidden by the time this runs; a fade that left them floating over the
    /// desktop would be worse than no fade.
    ///
    /// It is the whole of the way out, and it is deliberately the only thing left of the movement that used to be
    /// here. A card that shrank as it went was one more thing to look at than the going away it was meant to be,
    /// and everything that shrink needed went with it - the region recomputed on every frame, the shape it had to
    /// arrive in, the curve, the frame-rate report. What is left is a card that is there and then is not.
    ///
    /// Linear, because at this length there is nothing to ease: measured at the two-hundred-and-forty
    /// millisecond animation length the ini used to give it, a fade with a curve on it and one without are the
    /// same four frames to the eye. It measures from the moment the fade started rather than from the last step,
    /// so a step that arrives late fades a little more rather than fading late.
    /// </summary>
    private bool Fade()
    {
        double t = (Now - _fadeAt) / (double)FadeMs;
        if (t > 1) t = 1;
        int alpha = (int)Math.Round(255 * (1 - t));
        if (alpha < 0) alpha = 0;
        Native.SetLayeredWindowAttributes(_hwnd, 0, (byte)alpha, Native.LWA_ALPHA);
        return t >= 1;
    }

    /// <summary>
    /// Gives the card its corners, as a window region - once, when it is made, because nothing about the card
    /// moves afterwards. A region is its shape for hit-testing as much as for painting, so this is also what the
    /// mouse clicks against. The video child is clipped by it along with everything else, which is the part that
    /// had to be measured rather than assumed.
    ///
    /// A radius of nothing means no region at all rather than a square one, so that a card that is not rounded
    /// has nothing set on it to go wrong later. Nothing here asks whether the rounding animates, because there is
    /// nothing left to animate it on: the corner is the corner of the card this one took over.
    /// </summary>
    private static void Round(IntPtr hwnd, int w, int h)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowRgn(hwnd,
            Radius <= 0 ? IntPtr.Zero : Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, Radius * 2, Radius * 2),
            true);
    }

    /// <summary>
    /// Sizes the window the video is played in so that the picture covers the card rather than sitting inside
    /// it with black bars.
    ///
    /// The video is played into a child of the card, and a child is clipped to its parent - so a video window
    /// bigger than the card is a video window whose overflow is simply not drawn, which is a crop. What is
    /// wanted is the smallest window of the video's own shape that still covers the card: that is the picture
    /// scaled until the first of its two edges reaches the card's, which is the whole of it, and the part that
    /// hangs over the other edge is what gets cut off. Centred, so both sides lose equally.
    ///
    /// Nothing here makes the player crop anything - it is asked to fill the window it was given, and it is
    /// the window that is the wrong shape for the card, on purpose.
    /// </summary>
    private void FitVideo(int w, int h)
    {
        if (_videoHost == IntPtr.Zero || w <= 0 || h <= 0) return;

        int vw = _player?.Width ?? 0, vh = _player?.Height ?? 0;
        int hw = w, hh = h;
        if (vw > 0 && vh > 0)
        {
            // Which of the two edges is the tight one: the card being wider than the video means the width is
            // what has to reach, and the height overflows.
            if ((long)w * vh >= (long)h * vw) hh = (int)((long)w * vh / vw);
            else hw = (int)((long)h * vw / vh);
        }
        // Nothing moved means nothing to do, and this is not a micro-optimisation: telling the player its window
        // has been resized is a real cost to a video that is playing, and the frame that ends the grow is drawn
        // sixty times a second by a card that has stopped growing. See Step.
        if (hw == _fitW && hh == _fitH && (w - hw) / 2 == _fitX && (h - hh) / 2 == _fitY) return;
        _fitW = hw; _fitH = hh; _fitX = (w - hw) / 2; _fitY = (h - hh) / 2;

        Native.SetWindowPos(_videoHost, IntPtr.Zero, _fitX, _fitY, hw, hh, Native.SWP_NOACTIVATE);
        // And the player told about it, which it does not work out for itself. Without this the picture keeps
        // being fitted to the size the window had when the item was set - the card's own shape - and a
        // letterboxed picture is what was seen twice after the arithmetic had already been right.
        _player?.UpdateVideo();
        if (!_fitSaid && vw > 0 && vh > 0)
        {
            _fitSaid = true;
            // The player's own window is reported as well, because the whole of this is only true if it came
            // with us: it is inside the window that was just moved and resized, and if it kept its old size
            // then the picture is a small rectangle in the corner of a big black card.
            string inner = "";
            IntPtr window = _player?.VideoWindow ?? IntPtr.Zero;
            if (window != IntPtr.Zero && Native.GetWindowRect(window, out var r))
                inner = $" - the player's own window {r.Right - r.Left}x{r.Bottom - r.Top} at {r.Left},{r.Top}";
            Say($"ads: video {vw}x{vh} in a {w}x{h} card -> {hw}x{hh} centred at {(w - hw) / 2},{(h - hh) / 2}" +
                $", so it fills and overflows{inner}");
        }
    }

    /// <summary>
    /// The mute capsule and the countdown capsule, made once the card is where it is going to stay.
    ///
    /// They are made after the player has made its own child window, which is what puts them on top of the
    /// video rather than behind it - and they are drawn by Capsule rather than by the system, which is the
    /// only way a translucent control over a video is possible at all. See Capsule, and the class comment for
    /// why a control of the system's is either invisible over the video or takes the click that dismisses the
    /// advertisement.
    ///
    /// Their size comes from the card they are on rather than from the display, which is the one thing about
    /// them that has been decided twice: it used to be the monitor's DPI, which is the same number for a card
    /// the size of a screen and for one the size of a small window. See the comment on the size below, and
    /// Capsule.Style, which no longer knows what a DPI is.
    /// </summary>
    private void MakeControls()
    {
        _controls = true;

        int w = _full.Right - _full.Left, h = _full.Bottom - _full.Top;
        Capsule.Trace = Trace;

        // The controls follow the card rather than the display, and that is the whole of this: a capsule is a
        // percentage of the card's shorter side, held between a floor and a ceiling.
        //
        // What it replaces is the monitor's scaling, which made every control the same size everywhere - right
        // for a card that is the whole screen, and wrong for one that is an application window, where the three
        // of them were as large as they are on a full screen and the name capsule could end up in the same band
        // as the mute capsule in the corner. Sizing them from the card is what makes that impossible rather than
        // unlikely: on a small card every control shrinks with it, and there is no display setting that can make
        // them grow again. Nothing here asks the DPI anything any more.
        //
        // The floor is legibility and hit-area on a card small enough that the percentage would be a sliver; the
        // ceiling is so that a card of the size of a large screen does not come out with a banner across it. The
        // padding is half a capsule, which is the same relationship the fixed padding had.
        int basis = Math.Min(w, h);
        int height = Math.Max(MinControlPx, Math.Min(MaxControlPx, basis * ControlPercent / 100));
        _style = new Capsule.Style(height);
        // And the one the advertisement's own name is drawn in, which is the same capsule at one and a half
        // times the size: the name is what the user is being told, and it is written over a picture.
        _nameStyle = new Capsule.Style(height, NameTimes);
        int pad = Math.Max(2, height / 2);

        // The countdown is the way out of an advertisement, so it goes where a way out is looked for: the top
        // right corner of the card. It used to be scattered round the four corners as a joke, which turned out
        // to mean that the one control the user needs was the one they had to hunt for.
        //
        // It is anchored by its right edge, because its width changes as the seconds run down and the corner it
        // is pinned to is the one that must not move. The mute capsule is the other way round and is pinned by
        // its left edge at the bottom.
        _countdown = Capsule.Make(_hwnd, _style, _full.Left + w - pad, _full.Top + pad,
                                 Capsule.Anchor.Right, action: CaptionSkip);
        // What it says until the player has said how long the file is: a placeholder, not the cap. The cap is
        // not a countdown - it is the longest this card may stay up - and putting it here made the first second
        // of every advertisement read "360s left" and then jump to the real figure. See CaptionPending.
        _countdown?.Set(CaptionPending, Capsule.Glyph.None);
        _mute = Capsule.Make(_hwnd, _style, _full.Left + pad, _full.Top + h - pad - _style.Height,
                             Capsule.Anchor.Left, action: null);
        _mute?.Set(_muted ? CaptionUnmute : CaptionMute, _muted ? Capsule.Glyph.Muted : Capsule.Glyph.Sound);

        // And the third one, which says which advertisement this is: the name of the file that is playing and a
        // search glyph beside it, at the lower part of the card. It is the widest thing that is allowed to be
        // arbitrary here, so it has a ceiling: half the card's width, and a name longer than that is shortened
        // with an ellipsis rather than run off the picture. A name that fits is shown as it is, with nothing on
        // the end of it. See SetName and Shorten.
        _labelCeiling = w * NameWidestPercent / 100;
        SetName();
        int nameW = Capsule.Measure(_nameStyle, _label, null, Capsule.Glyph.Search, iconRight: true, lead: CaptionSearch).Width;
        int nameY = _full.Top + h - h * NameFromBottomPercent / 100 - _nameStyle.Height / 2;
        _name = Capsule.Make(_hwnd, _nameStyle, _full.Left + w / 2, nameY, Capsule.Anchor.Centre, action: null);
        SetName();          // now that there is a capsule to say it to
        Look();

        Say($"ads: controls on a card {w}x{h} ({basis} across its shorter side): a {_style.Height}px capsule" +
            $" - mute at {_full.Left + pad},{_full.Top + h - pad - _style.Height}, countdown pinned to the" +
            $" right at {_full.Left + w - pad},{_full.Top + pad}, {pad} from the edges; name \"{_label}\" " +
            $"{nameW}px wide in a {_nameStyle.Height}px capsule at {_full.Left + w / 2 - nameW / 2},{nameY}," +
            $" centred and {NameFromBottomPercent}% of the card's height up from its bottom " +
            $"(widest {_labelCeiling}px)");
        // Said out loud because it is the only thing on the card that deliberately shows a placeholder, and
        // "the first frame said 360" is the report this exists to answer: the line is written in the same call
        // that sets it, so a log in which this comes before the countdown's real figure is one where the card
        // never showed a number it had made up. probe/media checks that order.
        Say($"ads: the countdown says \"{CaptionPending}\" until the player has said how long the file is");
    }

    /// <summary>
    /// What the three controls' size comes from: a percentage of the card's shorter side, between a floor and a
    /// ceiling. The floor is what keeps a very small window legible, and the ceiling is what keeps a very large
    /// card from carrying a banner.
    /// </summary>
    private const int ControlPercent = 5, MinControlPx = 22, MaxControlPx = 72;

    /// <summary>
    /// How many times the size of the other two controls the name capsule is drawn at, how far up from the
    /// bottom of the card its centre sits as a percentage of the card's height, and the widest it may be as a
    /// percentage of the card's width. All three are looks rather than rules, and each of them is one number.
    /// </summary>
    private const double NameTimes = 1.5;
    private const int NameFromBottomPercent = 20;
    private const int NameWidestPercent = 50;

    /// <summary>
    /// What the name capsule says: the file that is playing, without its extension, shortened until it fits the
    /// card.
    ///
    /// Called when the file changes rather than once, because a file that opens but turns out to be nothing long
    /// is passed over and the next one takes its place - and a capsule naming the file that was skipped would be
    /// a name for something that is not on screen. Nothing happens before the style exists, which is why this is
    /// safe to call from Next as well.
    /// </summary>
    private void SetName()
    {
        _label = _file;
        if (_nameStyle != null && _labelCeiling > 0)
            _label = Shorten(_nameStyle, _file, _labelCeiling);
        _name?.Set(_label, Capsule.Glyph.Search, iconRight: true, lead: CaptionSearch);
    }

    /// <summary>
    /// A file name short enough for the ceiling: the whole of it when it fits, and an ellipsis where it was cut
    /// when it does not.
    ///
    /// The whole name is tried first and is returned unmarked, because an ellipsis on a name that is all there is
    /// a lie about it: it says something was left out when nothing was. Only a name that does not fit is walked
    /// down, one character at a time, until the shortened form fits - which is what the ellipsis means.
    ///
    /// Measured rather than counted, because what fits is a fact about the face and the DPI and not about how
    /// many characters a name has: the same name is wider at 200 per cent, a Chinese name is twice the width of
    /// a Latin one, and this capsule is drawn at one and a half times the other two on top of that. It is
    /// measured with the glyph and the gap in place, so what is shortened is the width the capsule will really
    /// be.
    ///
    /// Walking the name down a character at a time is enough for this and is written that way on purpose: it
    /// runs when a file is chosen - twice per advertisement at the outside - and never while one is playing.
    /// </summary>
    private static string Shorten(Capsule.Style style, string name, int ceiling)
    {
        if (Fits(style, name, ceiling)) return name;
        for (int keep = name.Length - 1; keep > 0; keep--)
        {
            string cut = name.Substring(0, keep) + "…";
            if (Fits(style, cut, ceiling)) return cut;
        }
        // Nothing fits, not even one character and an ellipsis: the shortest form that still says which file this
        // is, and a capsule a little wider than the card rather than no name at all.
        return name.Length > 1 ? name.Substring(0, 1) + "…" : name;
    }

    /// <summary>Whether a name, as it would be drawn, is inside the ceiling.</summary>
    private static bool Fits(Capsule.Style style, string name, int ceiling)
        => Capsule.Measure(style, name, null, Capsule.Glyph.Search, iconRight: true, lead: CaptionSearch).Width <= ceiling;

    /// <summary>
    /// What the two controls are showing, given where the cursor is and whether a press is being held.
    ///
    /// A press owns the state of the control it started on until it is let go, which is what makes the pressed
    /// look mean "this is what will happen if I let go here" rather than "the mouse was over this a moment
    /// ago". A control the press has been dragged off goes back to plain hover, and Look is called again when
    /// it is dragged back on.
    /// </summary>
    private void Look()
    {
        if (_mute == null || _countdown == null) return;
        Native.GetCursorPos(out var p);
        IntPtr over = ControlAt(p);
        _mute.Set(StateOf(_mute.Hwnd, over));
        _countdown.Set(StateOf(_countdown.Hwnd, over));
        // The name capsule, which is the one control that is pressed rather than let go of: it has no state of
        // its own beyond the two a press and a hover give it, and the swing goes on underneath either of them.
        _name?.Set(StateOf(_name.Hwnd, over));
    }

    private Capsule.State StateOf(IntPtr control, IntPtr over)
        => _pressOn == control ? Capsule.State.Pressed
         : over == control ? Capsule.State.Hover
         : Capsule.State.Normal;

    /// <summary>Which of the controls a point is inside, or nothing when it is anywhere else on the card.</summary>
    private IntPtr ControlAt(POINT p)
    {
        if (On(MuteControl, out _, p)) return MuteControl;
        if (On(NameControl, out _, p)) return NameControl;
        if (On(CountdownControl, out _, p)) return CountdownControl;
        return IntPtr.Zero;
    }

    /// <summary>
    /// The mouse going down: which control it is on is remembered, and the mouse is captured for as long as it
    /// is held.
    ///
    /// The capture is not optional and is not for the drag. A click on the video is a click on a child window,
    /// and a child tells its parent that a button went down and nothing at all about it coming up - so without
    /// the capture the card would never see the release, and a click that acts on release would act never.
    ///
    /// A press that is not on a control is a press on the window advertisement itself, which is where a window
    /// is dragged from. Where the cursor is inside the card is kept rather than its place on the screen, so that
    /// the card does not jump when the drag starts: what is being moved is the thing under the cursor, and it
    /// stays under it.
    /// </summary>
    private void Pressed()
    {
        Native.GetCursorPos(out var p);
        _pressOn = ControlAt(p);
        _pressAt = p;
        _dragging = false;
        if (_pressOn == IntPtr.Zero && Native.GetWindowRect(_hwnd, out var r))
        {
            _grabX = p.X - r.Left;
            _grabY = p.Y - r.Top;
        }
        _held = true;
        Native.SetCapture(_hwnd);
        Look();
        Say($"ads: a press at {p.X},{p.Y} on " +
            $"{(_pressOn == IntPtr.Zero ? "the card itself" : "one of the controls")}");
    }

    /// <summary>
    /// The mouse moving while a button is held.
    ///
    /// A move over a control is only the control's own state: pressing a button and sliding off it takes the
    /// press back, and sliding back on takes it back again. A move with no control under the press is the window
    /// advertisement being dragged, and it becomes that once it has moved further than the system's own drag
    /// threshold - the same one it uses to tell a click from a drag - so that a click on the advertisement with a
    /// shaky hand is still a click.
    /// </summary>
    private void Dragged()
    {
        if (_pressOn != IntPtr.Zero) { Look(); return; }
        if (!Native.GetCursorPos(out var p)) return;
        if (!_dragging)
        {
            int slopX = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXDRAG));
            int slopY = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYDRAG));
            if (Math.Abs(p.X - _pressAt.X) < slopX && Math.Abs(p.Y - _pressAt.Y) < slopY) return;
            _dragging = true;
            Say("ads: the advertisement is being moved");
        }
        Move(p.X - _grabX, p.Y - _grabY);
    }

    /// <summary>
    /// Puts the whole advertisement somewhere else: the card, the video in it, the three controls that belong to
    /// it, and - through <see cref="Moved" /> - the window it is standing in for.
    ///
    /// The video needs no telling. Its window is a child of the card's, so it comes along with it, and the fit
    /// is a rectangle inside the card rather than a place on the screen: FitVideo sees the same width and height
    /// and does nothing at all, which is what keeps a drag from re-laying-out the player's scaling sixty times a
    /// second. What does need telling is the three controls, which are windows of their own, and the caller, whose
    /// hidden window has to be dragged with the advertisement: an advertisement moved away from the window it is
    /// standing in for, with the window left where it was, gives the whole thing away the moment the
    /// advertisement ends and the window appears somewhere the user did not put it.
    ///
    /// Internal so that probe/media can move a card without a mouse: this machine will not let a probe move the
    /// cursor, and everything here is arithmetic that can be checked without one.
    /// </summary>
    internal void Move(int left, int top)
    {
        int w = _full.Right - _full.Left, h = _full.Bottom - _full.Top;
        int dx = left - _full.Left, dy = top - _full.Top;
        if (dx == 0 && dy == 0) return;

        _full = new RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, w, h, Native.SWP_NOACTIVATE);
        _mute?.Move(dx, dy);
        _countdown?.Move(dx, dy);
        _name?.Move(dx, dy);
        Moved?.Invoke(dx, dy);
    }

    /// <summary>
    /// The mouse coming up, which is when a click does what it does.
    ///
    /// A press that was let go somewhere else is taken back and says so, which is the whole reason the press
    /// state exists and is what every button on the system does. A press on the card itself is not taken back
    /// this way because there is nothing to take back: the rule for where a click landed is ActingOn's, and for
    /// the card itself that rule is "nothing": only the countdown and the name capsule close it.
    ///
    /// A press that turned into a drag is not a click at all and does not reach that rule, whatever it was let
    /// go on: the advertisement was moved, and being moved is not a reason to dismiss it.
    /// </summary>
    private void Released()
    {
        Native.GetCursorPos(out var p);
        IntPtr on = ControlAt(p), pressed = _pressOn;
        bool dragged = _dragging;
        _pressOn = IntPtr.Zero;
        _dragging = false;
        _held = false;
        Native.ReleaseCapture();
        Look();

        if (dragged)
        {
            Say($"ads: the advertisement was moved to {_full.Left},{_full.Top} at {p.X},{p.Y}");
            return;
        }
        if (on != pressed)
        {
            Say($"ads: a press {Where(pressed)} was let go {Where(on)}, so it is taken back and nothing happens");
            return;
        }
        ActingOn(p);
    }

    /// <summary>A control named by the place it is at, for the one line above that has to say either.</summary>
    private string Where(IntPtr control)
        => control == IntPtr.Zero ? "on the card"
         : control == _mute?.Hwnd ? "on the mute control"
         : control == _name?.Hwnd ? "on the name control"
         : "on the countdown";

    /// <summary>The capture going somewhere else, which cancels the press the same way letting go off it does.</summary>
    private void LostCapture()
    {
        _pressOn = IntPtr.Zero;
        _dragging = false;
        _held = false;
        Look();
    }

    /// <summary>
    /// What a click means, given where it was.
    ///
    /// Split off from the mouse plumbing - a message arriving at this window, and the cursor being where the
    /// click was - because that plumbing has no way to be tested except by a hand on a mouse, while this, the
    /// rule itself, is the whole of what a click does. probe/media drives this with points of its own.
    /// </summary>
    internal void ActingOn(POINT p)
    {
        if (On(MuteControl, out var mute, p))
        {
            Say($"ads: a click at {p.X},{p.Y} is on the mute control " +
                $"({mute.Left},{mute.Top} {mute.Right - mute.Left}x{mute.Bottom - mute.Top})");
            ToggleMute();
            return;
        }

        // The name capsule is a way out of the advertisement in the other direction: it says what this is, and
        // pressing it looks that up. It closes the card as well, on either kind, and that is the one rule here
        // that was written twice before being made one: a control the user pressed to go somewhere else should
        // not leave the advertisement over what they asked for. It is checked before the rules below because it
        // is a control, and because a click anywhere else on the card does nothing. The search is asked for
        // before the close so that the URL has been handed over before the card starts taking itself down.
        if (On(NameControl, out var name, p))
        {
            Say($"ads: a click at {p.X},{p.Y} is on the name control " +
                $"({name.Left},{name.Top} {name.Right - name.Left}x{name.Bottom - name.Top})");
            SearchTheName();
            Close();
            return;
        }

        // What the card itself does when the press was not on a control: nothing. The card takes the place of a
        // window the user was about to use, so a click meant for that window must not dismiss the advertisement -
        // it is the countdown that closes it, and the name capsule when the user asked it to search.
        if (On(CountdownControl, out var count, p))
        {
            Say($"ads: a click at {p.X},{p.Y} is on the countdown " +
                $"({count.Left},{count.Top} {count.Right - count.Left}x{count.Bottom - count.Top}); closing");
            Close();
            return;
        }

        Say($"ads: a click at {p.X},{p.Y} is on an advertisement that only its countdown closes;" +
            " nothing happens");
    }

    /// <summary>Whether a point is over one of the card's own controls, which is where it really is.</summary>
    private static bool On(IntPtr control, out RECT rect, POINT p)
    {
        rect = default;
        if (control == IntPtr.Zero || !Native.GetWindowRect(control, out rect)) return false;
        return p.X >= rect.Left && p.X < rect.Right && p.Y >= rect.Top && p.Y < rect.Bottom;
    }

    /// <summary>
    /// Searches for what the name capsule says, through <see cref="Searched" />. Nothing happens when there is
    /// nothing to search for, which is the empty name of a file that had none.
    /// </summary>
    private void SearchTheName()
    {
        string url = SearchUrlFor(_label);
        if (url.Length == 0) { Say("ads: there is no name to search for"); return; }
        Say($"ads: searching for \"{_label}\" at {url}");
        if (Searched == null) { Say("ads: nothing is listening for that search"); return; }
        try { Searched(url); }
        catch (Exception ex) { Say($"ads: the search would not be handed over ({ex.GetType().Name}: {ex.Message})"); }
    }

    /// <summary>
    /// The URL that searches for <paramref name="name" />, or an empty string when there is nothing to search
    /// for.
    ///
    /// Everything about how a file in the Ads folder could get at the shell is in these four lines, so they are
    /// the ones to read. A file's *name* is the only thing this program ever takes from a file a stranger may
    /// have put there, and here it is a query value and nothing else:
    ///
    ///   * the scheme and the host are our own constant and come first, so a name cannot choose them. It cannot
    ///     arrive as file:, javascript:, search-ms:, ms-settings: or a UNC path, because nothing before the name
    ///     is under its control;
    ///   * the name is percent-encoded, so &amp; ? # % + / \ and every control character in it are inert: no
    ///     extra parameter, no end of the query, no fragment, no second URL, no line break;
    ///   * it is cut to a length first, so no name can produce a URL of a size of its own choosing;
    ///   * and what comes out of here is the only thing the shell is ever handed - see Searched. The name is
    ///     never passed as a file, a path, a parameter or a working directory.
    ///
    /// It is internal and takes its prefix from the field rather than a parameter so that probe/media can check
    /// the invariants above on strings of its own, with no browser launched.
    /// </summary>
    internal static string SearchUrlFor(string name)
    {
        if (name.Length == 0 || SearchUrl.Length == 0) return "";
        if (name.Length > SearchTextMax) name = name.Substring(0, SearchTextMax);
        return SearchUrl + Uri.EscapeDataString(name);
    }

    /// <summary>How much of a file's name is searched for, in characters of the name rather than of the URL.</summary>
    private const int SearchTextMax = 120;

    private void ToggleMute()
    {
        _muted = !_muted;
        // The caption first, so the only visible feedback there is cannot be lost to whatever the player does
        // next.
        _mute?.Set(_muted ? CaptionUnmute : CaptionMute, _muted ? Capsule.Glyph.Muted : Capsule.Glyph.Sound);
        Say($"ads: the mute control was clicked, now {(_muted ? "muted" : "audible")}");
        MuteRemembered?.Invoke(_muted);
        if (_player == null) { Say("ads: there is no player to change"); return; }
        try
        {
            _player.Muted = _muted;
            Say($"ads: the player reports muted={_player.Muted}");
        }
        catch (Exception ex)
        {
            Say($"ads: the player would not change ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Starts the going-away, however it was asked for: the countdown, or a click.</summary>
    private void Close()
    {
        if (_done || _closing) return;
        if (!_closeAnimation)
        {
            _done = true;
            Say("ads: closing at once, the close animation being off");
            Native.DestroyWindow(_hwnd);
            return;
        }
        _closing = true;

        // The controls go now rather than with the fade. They are windows of their own and they are not layered,
        // so a fading card would leave them sitting at full strength over the desktop - the one thing that would
        // give the fade away. They are made once and not remade, so this is the end of them for this card.
        _mute?.Hide();
        _countdown?.Hide();
        _name?.Hide();

        // The last frame in place of whatever mfplay leaves on screen when playback has run out, which is
        // nothing - a black card, and a black card is a worse thing to fade than the advertisement itself. The
        // last frame rather than the first, which is what this used to put up: the first frame of an
        // advertisement is the one frame of it that belongs to nothing the user has been watching. See
        // MediaPlayer.HoldLastFrame.
        //
        // And then a couple of frames of standing still before the fade starts, because the seek does not put
        // the frame on screen the instant it returns: seeking and fading in the same breath is a fade over the
        // black the seek was meant to remove. What it landed on is logged by the first step of the fade, not
        // here; see Step.
        _heldFrame = _player?.HoldLastFrame() ?? false;
        _fadeAt = (int)Now + HoldMs;

        // The steps are asked for at the display's frame length for the length of the fade, and not at the
        // countdown's sixteen milliseconds, which is too coarse to carry a hundred and sixty of them. The
        // system's clock was already asked for a fine enough one when the card was made; see Show.
        Timer(_frame);
        Say($"ads: fading out over {FadeMs} ms at a frame of {_frame} ms," +
            $" after {_fadeAt - Now} ms of standing still");
    }

    /// <summary>
    /// The card's own step timer, at the interval asked for - and only written when it changes, because
    /// SetTimer is a call into the window manager and this is called at the ends of movements rather than once.
    /// </summary>
    private void Timer(int ms)
    {
        uint want = (uint)(ms < 1 ? 1 : ms);
        if (_timerMs == want) return;
        _timerMs = want;
        Native.KillTimer(_hwnd, StepTimer);
        Native.SetTimer(_hwnd, StepTimer, want, IntPtr.Zero);
    }

    /// <summary>
    /// How long a frame lasts on the monitor this card is on, in milliseconds, or the caller's answer when the
    /// display will not say.
    ///
    /// Asked per card rather than per program, and that is the point of it. The program measures the display
    /// once, from the desktop, which is the primary monitor's cadence: a card animating on a 165 Hz monitor
    /// beside a 60 Hz primary would be paced at 60 and look worse than the hardware can, and one on the slower
    /// monitor would be asked for three frames it can never show. A window's own DC is the monitor that window
    /// is on, so this is the same question the program asks, asked about the right display.
    /// </summary>
    private static int MonitorFrameMs(IntPtr hwnd, int fallback)
    {
        try
        {
            IntPtr dc = Native.GetDC(hwnd);
            if (dc != IntPtr.Zero)
            {
                int hz = Native.GetDeviceCaps(dc, Native.VREFRESH);
                Native.ReleaseDC(hwnd, dc);
                if (hz >= 20 && hz <= 500) return Math.Max(1, (int)Math.Round(1000.0 / hz));
            }
        }
        catch { }
        return fallback < 1 ? 1 : fallback;
    }

    private static IntPtr Handle(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        var card = _open;
        try
        {
            switch (msg)
            {
                case Native.WM_TIMER:
                    card?.Step();
                    return IntPtr.Zero;

                // A click on a child - which is where the video is, and so where nearly every click lands -
                // arrives here rather than at this window's own mouse messages. The going down is all it
                // carries: the coming up, the moving and the letting go are only seen through the capture the
                // press takes, which is a decision made in Pressed and explained there.
                case Native.WM_PARENTNOTIFY:
                    if ((w.ToInt64() & 0xFFFF) == Native.WM_LBUTTONDOWN) card?.Pressed();
                    return IntPtr.Zero;

                case Native.WM_LBUTTONDOWN:
                    card?.Pressed();
                    return IntPtr.Zero;

                case Native.WM_MOUSEMOVE:
                    if (card != null && card._held) card.Dragged();
                    return IntPtr.Zero;

                case Native.WM_LBUTTONUP:
                    card?.Released();
                    return IntPtr.Zero;

                case Native.WM_CAPTURECHANGED:
                    card?.LostCapture();
                    return IntPtr.Zero;

                case Native.WM_DESTROY:
                    if (ReferenceEquals(card, _open)) _open = null;
                    card?.CleanUp();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Say($"ads: the card's window procedure threw {ex.GetType().Name}: {ex.Message}");
        }
        return Native.DefWindowProcW(hwnd, msg, w, l);
    }

    /// <summary>Lets go of everything the card made. The window is already gone by the time this runs.</summary>
    private void CleanUp()
    {
        Native.KillTimer(_hwnd, StepTimer);
        _mute?.Dispose();
        _mute = null;
        _countdown?.Dispose();
        _countdown = null;
        _name?.Dispose();
        _name = null;
        _style?.Dispose();
        _style = null;
        _nameStyle?.Dispose();
        _nameStyle = null;
        _player?.Dispose();
        _player = null;
        _hwnd = IntPtr.Zero;
        _done = true;
        // Last, because the caller may put something else up from inside it - and this is where a window that
        // was kept hidden for as long as the card was up is let out again.
        Closed?.Invoke();
    }

    private static string[] Shuffled(string[] files)
    {
        var copy = (string[])files.Clone();
        var random = new Random();
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    private static void Say(string s) => Trace?.Invoke(s);
}
