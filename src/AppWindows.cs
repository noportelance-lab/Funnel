using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Funnel.Engine;

namespace Funnel;

/// <summary>
/// Every Funnel window, and what they share. Each window is its own web page, but they share one
/// settings file, one display engine and one set of data sources (with their caches).
///
/// Tabs move between windows the way they do in a browser. While a tab is dragged, a timer here
/// follows the pointer: once it leaves the tab strip, a window carrying the tab appears under it
/// and moves with it (a spare window, loaded ahead of time and hidden, so it's instant); over a
/// strip again, that window folds away and the strip takes the tab. A window's only tab moves its
/// window instead.
/// </summary>
internal static class AppWindows
{
    public static readonly AppConfig Config = AppConfig.Load();
    public static readonly List<MainForm> All = new();
    /// <summary>The window the user last had in front, for messages that belong to no window in particular.</summary>
    public static MainForm? Active;
    private static int _seq;
    private static Task<CoreWebView2Environment>? _env;

    /// <summary>One display engine for every window (UI thread only).</summary>
    public static Task<CoreWebView2Environment> EnvAsync() => _env ??= CreateEnv();

    private static Task<CoreWebView2Environment> CreateEnv()
    {
        var userData = Path.Combine(AppPaths.Home, "WebView2");
        Directory.CreateDirectory(userData);
        // Let the muted start-screen background video autoplay without a user gesture.
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" };
        return CoreWebView2Environment.CreateAsync(null, userData, options);
    }

    public static int NextId() => ++_seq;

    public static void Opened(MainForm f)
    {
        All.Add(f);
        f.Activated += (_, _) => Active = f;
        f.FormClosed += (_, _) =>
        {
            All.Remove(f);
            if (Active == f) Active = All.LastOrDefault();
            if (All.Count == 0)
            {
                _spare?.Close();
                Application.ExitThread();
            }
        };
    }

    /// <summary>Tell every other window (settings changed, a tab drag started or ended).</summary>
    public static void Broadcast(Bridge? except, string name, object data)
    {
        foreach (var w in All.ToList())
            if (w.Bridge is { } b && b != except) b.Notify(name, data);
    }

    // ---------- the spare window ----------
    private static MainForm? _spare;
    private static bool _spareReady;
    private static int _spareFails;

    /// <summary>Load a spare window in the background, if there isn't one (after the first window has settled).</summary>
    public static void WantSpare(int delayMs)
    {
        if (_spare != null || _spareFails >= 3) return;
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, delayMs) };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (_spare != null || All.Count == 0) return;
            _spareReady = false;
            _spare = new MainForm(MainForm.Kind.Spare);
        };
        t.Start();
    }

    /// <summary>The spare's page has started and is waiting for a tab.</summary>
    public static void SpareLoaded(MainForm f) { if (f == _spare) _spareReady = true; }

    public static void SpareFailed(MainForm f)
    {
        if (f != _spare) return;
        _spare = null; _spareFails++;
        try { f.Close(); } catch { }
    }

    private static MainForm? ReadySpare => _spare is { IsDisposed: false, Bridge: not null } s && _spareReady ? s : null;

    // ---------- dragging a tab ----------
    private sealed class Session
    {
        public required Bridge Source;
        public required MainForm From;
        public required string Id, Payload;
        public PointF GrabCss;          // the grabbed point, in page pixels, as if the tab were the strip's first
        public bool Single;
        public Point Start;
        public MainForm? Host;          // the window following the pointer (the spare, or From for a window's only tab)
        public MainForm? Over;          // the window whose strip the tab is on, if it's on one
        public bool Detached, Escaped, Adopted;
        public bool Gone;               // the tab is out of its own window's strip
        public DateTime? UpSince;
    }
    private static Session? _s;
    // The last dragged tab's page, for a window it's dropped on. Kept past the end of the drag: the
    // drop and the end of the drag reach C# from two pages, in either order.
    private static (Bridge Source, string Id, string Payload)? _last;
    private static readonly System.Windows.Forms.Timer Follow = new() { Interval = 8 };
    private static readonly Point OffScreen = new(-32000, -32000);

    static AppWindows() => Follow.Tick += (_, _) => Tick();

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static bool ButtonDown() => (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0;
    private static Size NormalSize(MainForm f) => f.WindowState == FormWindowState.Normal ? f.Size : f.RestoreBounds.Size;

    public static void DragStarted(Bridge source, MainForm from, string id, string payload, string title, string sub, PointF grabCss, bool single)
    {
        EndSession();
        _s = new Session { Source = source, From = from, Id = id, Payload = payload, GrabCss = grabCss, Single = single, Start = Cursor.Position, Over = from };
        _last = (source, id, payload);
        Broadcast(source, "tabDragging", new { on = true, id, title, sub });
        // The spare starts drawing the tab now, at the size it'll be shown at, so it's ready the
        // moment the tab leaves the strip.
        if (!single && ReadySpare is { } spare)
        {
            spare.Size = NormalSize(from);
            spare.Bridge!.Adopt(payload, id);
            _s.Adopted = true;
        }
        Follow.Start();
    }

    private static void Tick()
    {
        var s = _s;
        if (s == null || s.From.IsDisposed) { Follow.Stop(); _s = null; return; }
        if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) s.Escaped = true;
        if (!ButtonDown())
        {
            // The page reports the end of the drag; this is only in case that never comes.
            s.UpSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - s.UpSince > TimeSpan.FromMilliseconds(900)) Ended(s.Source, s.Id, dropped: false);
            return;
        }
        if (s.Escaped) return;
        var cur = Cursor.Position;

        if (s.Single)
        {
            // A window's only tab carries the window. Over another window's strip the window steps
            // off screen, so that strip can take the tab; off it again, the window comes back.
            if (!s.Detached)
            {
                if (Math.Abs(cur.X - s.Start.X) + Math.Abs(cur.Y - s.Start.Y) < 8 * s.From.DpiScale) return;
                s.Detached = true; s.Host = s.From;
                if (s.From.WindowState != FormWindowState.Normal)
                {
                    var size = s.From.RestoreBounds.Size;
                    s.From.WindowState = FormWindowState.Normal;
                    s.From.Size = size;
                }
            }
            s.Over = StripUnder(cur, s.From);
            if (s.Over != null) { if (s.From.Location != OffScreen) s.From.Location = OffScreen; }
            else Place(s.From, s, cur);
            return;
        }

        // The tab stays in a strip until it's clearly off it (a margin around the strip), and goes
        // back into one only once it's on it, so it doesn't flicker in and out at the edge.
        var over = StripUnder(cur, s.Host);
        if (over != null) s.Over = over;
        else if (s.Over != null && !Loose(s.Over).Contains(cur)) s.Over = null;
        if (!s.Detached && s.Over == null) Detach(s, cur);
        else if (s.Detached && s.Over != null) Attach(s);
        if (s.Detached && s.Host != null) Place(s.Host, s, cur);
    }

    private static Rectangle Loose(MainForm w)
    {
        var r = w.StripOnScreen();
        r.Inflate((int)(24 * w.DpiScale), (int)(30 * w.DpiScale));
        return r;
    }

    private static void Place(MainForm w, Session s, Point cur)
    {
        var off = w.PageOffset(s.GrabCss.X, s.GrabCss.Y);
        var at = new Point(cur.X - off.X, cur.Y - off.Y);
        if (w.Location != at) w.Location = at;
    }

    // The tab leaves the strip: the spare, already showing it, appears under the pointer.
    private static void Detach(Session s, Point cur)
    {
        if (ReadySpare is not { } spare) return;   // none loaded: the tab opens in a new window when it's let go
        if (!s.Adopted) { spare.Size = NormalSize(s.From); spare.Bridge!.Adopt(s.Payload, s.Id); s.Adopted = true; }
        Place(spare, s, cur);
        if (!spare.Visible) { spare.ShowQuietly = true; spare.Show(); }
        SetWindowPos(spare.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // on top of the others, without the focus
        s.Host = spare; s.Detached = true;
        Gone(s, true);
    }

    // The source window's strip shows the tab only while it's in that strip (or wasn't moved).
    private static void Gone(Session s, bool gone)
    {
        if (s.Gone == gone) return;
        s.Gone = gone;
        s.Source.Notify(gone ? "tabDetached" : "tabAttached", new { id = s.Id });
    }

    // On a strip again: the window steps off screen (shown still, so it's instant if the tab leaves
    // again) and the strip shows where the tab will go.
    private static void Attach(Session s)
    {
        if (s.Host != null) s.Host.Location = OffScreen;
        s.Detached = false;
        if (s.Over == s.From) Gone(s, false);
    }

    /// <summary>The topmost window under the pointer, if it's a Funnel window and the pointer is on its tab strip.</summary>
    private static MainForm? StripUnder(Point p, MainForm? skip)
    {
        var mine = All.Where(w => w != skip && w.Visible && !w.IsDisposed && w.WindowState != FormWindowState.Minimized).ToDictionary(w => w.Handle);
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
        {
            if (skip != null && h == skip.Handle) continue;
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r) || p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) continue;
            if (!mine.TryGetValue(h, out var w)) return null;   // someone else's window is on top here
            return w.StripOnScreen().Contains(p) ? w : null;
        }
        return null;
    }

    /// <summary>The page says the drag ended: dropped = on a strip (its own, or another window's, which takes it).</summary>
    public static void Ended(Bridge source, string id, bool dropped)
    {
        var s = _s;
        if (s == null || s.Source != source || s.Id != id) return;
        Follow.Stop();
        _s = null;
        bool esc = s.Escaped || (GetAsyncKeyState(0x1B) & 0x8001) != 0;
        Broadcast(source, "tabDragging", new { on = false, id });

        if (s.Single)
        {
            // Let go over another window's strip, that window takes the tab and this one closes;
            // anywhere else, the window stays where it was carried (or comes back).
            if (s.Over != null && dropped) return;   // taken: this window closes
            if (s.From.Location == OffScreen) Place(s.From, s, Cursor.Position);
            s.From.Activate();
            return;
        }
        if (s.Detached && s.Host is { } host && !esc)
        {
            // Let go: the window that followed the pointer is the tab's window now.
            _last = null;
            _spare = null;
            Opened(host);
            host.ShowQuietly = false;
            host.Bridge!.Notify("adopted", new { });
            host.Activate();
            s.Source.Notify("tabMoved", new { id });
            WantSpare(1500);
            return;
        }
        // Taken by another window's strip: it stays out of this strip until this window lets it go.
        Release(s, keepGone: dropped && s.Over != null && s.Over != s.From);
        if (!dropped && !esc && s.Over == null && StripUnder(Cursor.Position, null) == null)
        {
            // Off every strip with no spare ready: open the tab in a new window the slow way.
            var size = NormalSize(s.From);
            var off = s.From.PageOffset(s.GrabCss.X, s.GrabCss.Y);
            var cur = Cursor.Position;
            var w = new MainForm(MainForm.Kind.Seeded, s.Payload, new Rectangle(cur.X - off.X, cur.Y - off.Y, size.Width, size.Height));
            w.Show();
            _last = null;
            s.Source.Notify("tabMoved", new { id });
            WantSpare(1500);
        }
    }

    // The tab stays where it was (or went to another window's strip): the spare goes back to waiting.
    private static void Release(Session s, bool keepGone = false)
    {
        if (!keepGone) Gone(s, false);
        if (s.Host is { } h && h != s.From) { h.Hide(); h.ShowQuietly = false; }
        if (s.Adopted && ReadySpare is { } spare) spare.Bridge!.Notify("unadopt", new { });
    }

    private static void EndSession()
    {
        if (_s is not { } s) return;
        Follow.Stop(); _s = null;
        if (s.Single) { if (s.From.Location == OffScreen) Place(s.From, s, Cursor.Position); return; }
        Release(s);
    }

    /// <summary>Another window took the tab: hand it the page, and tell its old window to let it go.</summary>
    public static string? Take(Bridge taker, string id)
    {
        if (_last is not { } d || d.Id != id || d.Source == taker) return null;
        _last = null;
        d.Source.Notify("tabMoved", new { id });
        Broadcast(null, "tabDragging", new { on = false, id });
        return d.Payload;
    }
}

/// <summary>Runs the app until the last window closes.</summary>
internal sealed class FunnelContext : ApplicationContext
{
    public FunnelContext() => new MainForm().Show();
}
