// Playing a video by asking Media Foundation to do it.
//
// This is the whole of the launch-ad feature's rendering: a window handle, a file path, and four calls. The
// decoding, the scaling, the colour conversion and the audio output are Media Foundation's, and nothing in
// this file touches a pixel or a sample.
//
// Why Media Foundation rather than anything else, measured on the machine this was written on rather than
// assumed:
//
//   * DirectShow is out. It is the easiest thing in Windows to drive - CoCreateInstance, RenderFile, put it
//     in a window - but Microsoft has never shipped an MP4 demultiplexer for it. Its built-in source filters
//     are AVI, WAV, ASF and MPEG-1, and MP4 has always come from a third-party splitter (LAV, Haali, MPC).
//     This machine has none of those installed, so RenderFile on an .mp4 has no source filter to find.
//   * Windows Media Player's control is out: wmp.dll is not installed any more.
//   * Media Foundation has mfmp4srcsnk.dll and mfmkvsrcsnk.dll, which are its own MP4 and Matroska readers,
//     plus msmpeg2vdec.dll for H.264. Both of the files this feature is pointed at are H.264 in MP4.
//
// Inside Media Foundation there were two ways to get a video into a window, and the choice is worth writing
// down because the obvious answer is the wrong one:
//
//   * IMFMediaEngine is the modern, supported engine - it is what Edge and WebView2 play video with. But it
//     does not draw into a window: it hands out decoded frames and expects the caller to own the surface,
//     and its TransferVideoFrame copies into a DXGI surface or a WIC bitmap, not an HDC. That makes us the
//     presenters, which is more than this feature wants to own.
//   * IMFPMediaPlayer (mfplay) is the older, deprecated one, and it takes an HWND and does the drawing and
//     the sound itself. Microsoft's boilerplate recommends against it for new code, and the recommendation
//     is noted - but the reason it exists here is that it is the only in-box option that plays a video into a
//     window we own without us writing a renderer. The whole file is the four calls below.
//
// The vtable order in MfPlay is the part that cannot be guessed. COM methods are reached by slot, so one
// declaration missing or out of order is a call to the wrong function - a wrong result at best and a crash
// at worst, with nothing to say which. Every declaration below is in mfplay.h's order and was read out of
// the MIDL-generated header rather than from memory; probe/media checks the result against the file's own
// header, which is an answer that can be wrong in a way that shows.

using System;
using System.Runtime.InteropServices;

namespace Moa;

/// <summary>mfplay.h, as declarations.</summary>
internal static class MfPlay
{
    public const uint OptionNone = 0;

    /// <summary>MFP_POSITIONTYPE_100NS, whose GUID is all zeroes - not a typo.</summary>
    public static readonly Guid PositionType100ns = Guid.Empty;

    /// <summary>MFP_MEDIAITEM_CHARACTERISTICS_CAN_SEEK.</summary>
    public const uint MediaItemCanSeek = 0x2;

    public enum MediaState { Empty = 0, Stopped = 1, Playing = 2, Paused = 3, Shutdown = 4 }

    public enum EventType
    {
        Play = 0, Pause = 1, Stop = 2, PositionSet = 3, RateSet = 4,
        MediaItemCreated = 5, MediaItemSet = 6, FrameStep = 7, MediaItemCleared = 8,
        Mf = 9, Error = 10, PlaybackEnded = 11, AcquireUserCredential = 12,
    }

    /// <summary>
    /// The first member of every event the callback is handed. The rest of the struct depends on the event
    /// type, which is why this is the only part that is declared: everything this file needs to know is
    /// either in here or reached through the player.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EventHeader
    {
        public EventType Type;
        public int Hr;
        public IntPtr Player;
        public MediaState State;
        public IntPtr PropertyStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size { public int Cx, Cy; }

    [ComImport, Guid("766C8FFB-5FDB-4FEA-A28D-B912996F51BD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFPMediaPlayerCallback
    {
        /// <summary>The header is an MFP_MEDIAITEM_SET_EVENT and its relatives, typed as IMFPMediaPlayer in the header.</summary>
        [PreserveSig] void OnMediaPlayerEvent(IntPtr header);
    }

    [ComImport, Guid("90EB3E6B-ECBF-45CC-B1DA-C6FE3EA70D57"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFPMediaItem
    {
        [PreserveSig] int GetMediaPlayer(out IntPtr player);
        [PreserveSig] int GetURL(out IntPtr url);
        [PreserveSig] int GetObject(out IntPtr unknown);
        [PreserveSig] int GetUserData(out IntPtr userData);
        [PreserveSig] int SetUserData(IntPtr userData);
        [PreserveSig] int GetStartStopPosition(out Guid startType, IntPtr startValue,
                                               out Guid stopType, IntPtr stopValue);
        [PreserveSig] int SetStartStopPosition([In] ref Guid startType, IntPtr startValue,
                                               [In] ref Guid stopType, IntPtr stopValue);
        [PreserveSig] int HasVideo([MarshalAs(UnmanagedType.Bool)] out bool hasVideo,
                                   [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int HasAudio([MarshalAs(UnmanagedType.Bool)] out bool hasAudio,
                                   [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int IsProtected([MarshalAs(UnmanagedType.Bool)] out bool isProtected);
        [PreserveSig] int GetDuration([In] ref Guid positionType, IntPtr durationValue);
        [PreserveSig] int GetNumberOfStreams(out int count);
        [PreserveSig] int GetStreamSelection(int index, [MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [PreserveSig] int SetStreamSelection(int index, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int GetStreamAttribute(int index, [In] ref Guid key, IntPtr value);
        [PreserveSig] int GetPresentationAttribute([In] ref Guid key, IntPtr value);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
        [PreserveSig] int SetStreamSink(int index, IntPtr mediaSink);
        [PreserveSig] int GetMetadata(out IntPtr propertyStore);
    }

    [ComImport, Guid("A714590A-58AF-430A-85BF-44F5EC838D85"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFPMediaPlayer
    {
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int FrameStep();
        [PreserveSig] int SetPosition([In] ref Guid positionType, IntPtr positionValue);
        [PreserveSig] int GetPosition([In] ref Guid positionType, IntPtr positionValue);
        [PreserveSig] int GetDuration([In] ref Guid positionType, IntPtr durationValue);
        [PreserveSig] int SetRate(float rate);
        [PreserveSig] int GetRate(out float rate);
        [PreserveSig] int GetSupportedRates([MarshalAs(UnmanagedType.Bool)] bool forward,
                                            out float slowest, out float fastest);
        [PreserveSig] int GetState(out MediaState state);
        [PreserveSig] int CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url,
                                                 [MarshalAs(UnmanagedType.Bool)] bool synchronous,
                                                 IntPtr userData, out IMFPMediaItem item);
        [PreserveSig] int CreateMediaItemFromObject(IntPtr unknown,
                                                    [MarshalAs(UnmanagedType.Bool)] bool synchronous,
                                                    IntPtr userData, out IMFPMediaItem item);
        [PreserveSig] int SetMediaItem(IMFPMediaItem item);
        [PreserveSig] int ClearMediaItem();
        [PreserveSig] int GetMediaItem(out IMFPMediaItem item);
        [PreserveSig] int GetVolume(out float volume);
        [PreserveSig] int SetVolume(float volume);
        [PreserveSig] int GetBalance(out float balance);
        [PreserveSig] int SetBalance(float balance);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted);
        [PreserveSig] int GetNativeVideoSize(out Size video, out Size aspectRatio);
        [PreserveSig] int GetIdealVideoSize(out Size min, out Size max);
        [PreserveSig] int SetVideoSourceRect(IntPtr rect);
        [PreserveSig] int GetVideoSourceRect(IntPtr rect);
        [PreserveSig] int SetAspectRatioMode(uint mode);
        [PreserveSig] int GetAspectRatioMode(out uint mode);
        [PreserveSig] int GetVideoWindow(out IntPtr hwnd);
        [PreserveSig] int UpdateVideo();
        [PreserveSig] int SetBorderColor(uint color);
        [PreserveSig] int GetBorderColor(out uint color);
        [PreserveSig] int InsertEffect(IntPtr effect, [MarshalAs(UnmanagedType.Bool)] bool optional);
        [PreserveSig] int RemoveEffect(IntPtr effect);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int Shutdown();
    }

    /// <summary>
    /// The one call that makes a player. It takes the window the video goes in, and it starts Media
    /// Foundation itself - so nothing else in the program has to call MFStartup.
    /// </summary>
    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int MFPCreateMediaPlayer(
        [MarshalAs(UnmanagedType.LPWStr)] string? url,
        [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
        uint creationOptions,
        IMFPMediaPlayerCallback? callback,
        IntPtr hwnd,
        out IMFPMediaPlayer player);
}

/// <summary>
/// One video in one window, opened and closed. The caller drives it; nothing here pumps messages or creates
/// windows, because the message loop a player needs is the one the program already has.
/// </summary>
internal sealed class MediaPlayer : IDisposable
{
    private MfPlay.IMFPMediaPlayer? _player;
    private MfPlay.IMFPMediaItem? _item;
    private Callback? _callback;         // kept alive: the native side holds an interface pointer to it
    private bool _mediaItemSet;
    private bool _ended;
    private bool _failed;

    /// <summary>True once the item has been handed to the player and it has said so.</summary>
    public bool Ready => _mediaItemSet;

    /// <summary>True once playback has run to the end of the file.</summary>
    public bool Ended => _ended;

    /// <summary>Set when the player reported a failure instead of playing.</summary>
    public string Failure { get; private set; } = "";

    /// <summary>How long the video is, in milliseconds, or -1 when it is not known yet.</summary>
    public int DurationMs { get; private set; } = -1;

    public bool HasVideo { get; private set; }
    public bool HasAudio { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>How long the player took to accept the item, measured from Open.</summary>
    public int OpenedMs { get; private set; } = -1;

    /// <summary>
    /// Opens <paramref name="path"/> in <paramref name="hwnd"/> and leaves it ready to play.
    ///
    /// The file is not passed to MFPCreateMediaPlayer, because creating the player with a URL starts it and by
    /// then it is too late to be quiet: the silence has to be in place before anything is given to it to play.
    /// </summary>
    public static MediaPlayer? Open(string path, IntPtr hwnd, bool muted)
    {
        var p = new MediaPlayer();
        p._callback = new Callback(p);

        // Stopwatch, not Environment.TickCount: the latter steps in 15.6 ms, and the number this produces is a
        // budget the opening animation has to fit inside.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int hr = MfPlay.MFPCreateMediaPlayer(null, false, MfPlay.OptionNone, p._callback, hwnd, out var player);
        if (hr < 0) { p.Fail($"MFPCreateMediaPlayer 0x{hr:X8}"); return p; }
        p._player = player;

        // Silent before the file is handed over, because a player created with a URL starts playing and there
        // would be no moment after that to be quiet in.
        player.SetMute(muted);
        hr = player.CreateMediaItemFromURL(path, true, IntPtr.Zero, out var item);
        if (hr < 0) { p.Fail($"CreateMediaItemFromURL 0x{hr:X8}"); return p; }
        p._item = item;

        item.HasVideo(out bool hasVideo, out _);
        item.HasAudio(out bool hasAudio, out _);
        p.HasVideo = hasVideo;
        p.HasAudio = hasAudio;

        hr = player.SetMediaItem(item);
        if (hr < 0) { p.Fail($"SetMediaItem 0x{hr:X8}"); return p; }

        p.OpenedMs = (int)clock.ElapsedMilliseconds;
        return p;
    }

    /// <summary>
    /// What the player only knows once it has the item: how long it is and how big the picture is.
    ///
    /// Called after it has said the item is set. Asking earlier answers -1 and 0x0 - a duration of "unknown"
    /// for a fifteen second video - because the source has not been parsed yet, which is how the first
    /// version of this file got it wrong.
    /// </summary>
    public void Refresh()
    {
        if (_player == null) return;

        if (DurationMs < 0 && _item != null)
        {
            IntPtr pv = Marshal.AllocCoTaskMem(32);
            try
            {
                Marshal.WriteInt16(pv, 0);
                var key = MfPlay.PositionType100ns;
                if (_item.GetDuration(ref key, pv) >= 0)
                {
                    VarType = Marshal.ReadInt16(pv);
                    // VT_I8 or VT_UI8, in hundreds of nanoseconds.
                    if (VarType == 20 || VarType == 21)
                    {
                        long hns = Marshal.ReadInt64(pv, 8);
                        DurationMs = hns <= 0 ? 0 : (int)(hns / 10000);
                    }
                }
            }
            catch { }
            finally { Marshal.FreeCoTaskMem(pv); }
        }

        _player.GetNativeVideoSize(out var size, out _);
        Width = size.Cx;
        Height = size.Cy;
    }

    /// <summary>The PROPVARIANT type the duration came back as, or 0 when it has not been asked.</summary>
    public short VarType { get; private set; }

    public void Play()
    {
        if (_player == null) return;
        int hr = _player.Play();
        if (hr < 0) Fail($"Play 0x{hr:X8}");
    }

    public void Stop()
    {
        try { _player?.Stop(); } catch { }
    }

    /// <summary>
    /// Back to the beginning and held there, so that what is on screen is the first frame. True when the player
    /// took it, and the caller says so in the log: a seek that quietly did nothing would leave the black card
    /// this exists to remove.
    ///
    /// For the moment an advertisement ends: mfplay leaves the video window showing nothing once playback has
    /// run out, which is a black card, and a black card is a worse thing to collapse than the advertisement's
    /// own first frame. Seeking back and pausing leaves that frame up instead, and it costs two calls rather
    /// than the bitmap a poster frame would need.
    /// </summary>
    public bool Rewind()
    {
        if (_player == null) return false;
        try
        {
            IntPtr pv = Marshal.AllocCoTaskMem(32);
            try
            {
                Marshal.WriteInt16(pv, 20 /* VT_I8 */);
                Marshal.WriteInt64(pv, 8, 0);
                var key = MfPlay.PositionType100ns;
                int hr = _player.SetPosition(ref key, pv);
                if (hr < 0) { Fail($"SetPosition 0x{hr:X8}"); return false; }
            }
            finally { Marshal.FreeCoTaskMem(pv); }
            _player.Pause();
            return true;
        }
        catch { return false; }
    }

    public bool Muted
    {
        get => _player != null && _player.GetMute(out bool m) >= 0 && m;
        set => _player?.SetMute(value);
    }

    /// <summary>The window the player drew its own video window into, for a caller that wants to look at it.</summary>
    public IntPtr VideoWindow => _player != null && _player.GetVideoWindow(out var h) >= 0 ? h : IntPtr.Zero;

    /// <summary>
    /// Tells the player that the window it was given has been moved or resized, so that the thing that actually
    /// draws does the same.
    ///
    /// This is not a hint and leaving it out is not a small mistake: measured, a window resized around a
    /// running player keeps drawing the picture at the size the window was when the item was set. The
    /// advertisement's crop is entirely a matter of the window being the wrong shape for the card, so without
    /// this the picture goes on being fitted to the old one - which is a letterboxed picture, and exactly the
    /// black bars that were reported twice after the arithmetic had already been got right.
    /// </summary>
    public void UpdateVideo()
    {
        try { _player?.UpdateVideo(); } catch { }
    }

    private void Fail(string why)
    {
        _failed = true;
        Failure = why;
    }

    /// <summary>True when the player did not get as far as being given something to play.</summary>
    public bool Failed => _failed;

    public void Dispose()
    {
        try { _player?.Shutdown(); } catch { }
        _player = null;
        _item = null;
        _callback = null;
    }

    /// <summary>
    /// The player's events, on the thread that made the player - so on the one that owns the message loop,
    /// which is what lets this touch the object above without any of it being thread-safe.
    /// </summary>
    private sealed class Callback : MfPlay.IMFPMediaPlayerCallback
    {
        private readonly MediaPlayer _owner;
        public Callback(MediaPlayer owner) => _owner = owner;

        public void OnMediaPlayerEvent(IntPtr header)
        {
            var e = Marshal.PtrToStructure<MfPlay.EventHeader>(header);
            switch (e.Type)
            {
                case MfPlay.EventType.MediaItemSet: _owner._mediaItemSet = true; break;
                case MfPlay.EventType.PlaybackEnded: _owner._ended = true; break;
                case MfPlay.EventType.Error: _owner.Fail($"the player reported 0x{e.Hr:X8}"); break;
            }
        }
    }
}
