using System.Drawing;
using Photino.NET;
using Funnel.Engine;

namespace Funnel;

/// <summary>
/// The macOS window: a Photino window (the system WebKit view) hosting the same HTML/CSS/JS UI as
/// the Windows build. The page is served from the app's embedded resources on the app:// scheme,
/// and talks to the <see cref="Bridge"/> through Photino's web messages (wwwroot/bridge.js gives
/// the page the WebView2 shape). One window: tabs live in it, but aren't dragged out into others.
/// </summary>
internal sealed class MacWindow : IAppWindow
{
    private const string Origin = "app://localhost/";
    private readonly PhotinoWindow _w;
    private volatile bool _closed;
    private bool _revealed;

    public Bridge Bridge { get; }

    public MacWindow(AppConfig config)
    {
        Bridge = new Bridge(this, config) { LoadStep = Step, WindowId = 1 };
        _w = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTitle($"Funnel  v{Bridge.AppVersion}")
            .SetUseOsDefaultSize(false)
            .SetSize(1360, 840)
            .SetMinSize(640, 440)
            .Center()
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(Environment.GetEnvironmentVariable("FUNNEL_DEVTOOLS") == "1")
            .SetMediaAutoplayEnabled(true)
            .RegisterCustomSchemeHandler("app", Serve)
            .RegisterWebMessageReceivedHandler((_, msg) => { try { Bridge.Handle(msg); } catch { } })
            .RegisterWindowCreatedHandler((_, _) => { FitToScreen(); try { Cocoa.AllowAutoplay(); } catch { } })
            .RegisterWindowClosingHandler((_, _) => { _closed = true; return false; });

        var q = "";
#if DEBUG
        // Development only: open past the start screen, for screenshots of the running app.
        if (Environment.GetEnvironmentVariable("FUNNEL_DEV_NOGATE") == "1") q = "?dev=nogate";
#endif
        _w.Load(new Uri(Origin + "index.html" + q));
        AppWindows.Active = this;
    }

    /// <summary>Runs the window until it's closed.</summary>
    public void Run() => _w.WaitForClose();

    // Smaller screens (a 13" MacBook) get a window that fits, centred.
    private void FitToScreen()
    {
        try
        {
            var wa = _w.MainMonitor.WorkArea;
            int w = Math.Min(1360, wa.Width - 40), h = Math.Min(840, wa.Height - 40);
            if (w < 1360 || h < 840) _w.SetSize(w, h).Center();
        }
        catch { /* keep the default size */ }
    }

    /// <summary>
    /// There's no native loading screen on macOS: the page shows its start screen as soon as it has
    /// its settings, so the start screen's entrance plays then.
    /// </summary>
    private void Step(string id)
    {
        if (_revealed || id is not ("settings" or "market" or "start")) return;
        _revealed = true;
        Bridge.Notify("revealed", new { });
    }

    // ---------- the page, from the embedded resources ----------

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".css"] = "text/css", [".js"] = "text/javascript",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".webm"] = "video/webm",
    };

    private static Stream? Serve(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        if (!url.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return null;
        var name = url[Origin.Length..];
        var cut = name.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) name = name[..cut];
        if (name.Length == 0) name = "index.html";
        // Only flat names of files the page is built from.
        if (name.Contains('/') || name.Contains('\\') || !Types.TryGetValue(Path.GetExtension(name), out var type)) return null;
        contentType = type;
        return Assets.WebFile(name);
    }

    // ---------- IAppWindow ----------

    public bool IsClosed => _closed;

    public void PostJson(string json)
    {
        if (_closed) return;
        try { _w.SendWebMessage(json); } catch { /* window closing */ }
    }

    public void SetTitle(string title)
    {
        if (_closed) return;
        try { _w.Invoke(() => _w.SetTitle(title)); } catch { }
    }

    public void CloseWindow()
    {
        if (_closed) return;
        try { _w.Invoke(_w.Close); } catch { }
    }

    public void Raise() { /* the only window is already in front */ }

    public string? AskSavePath(string title, string defaultName, string dir)
    {
        string? path = null;
        _w.Invoke(() => path = Cocoa.AskSavePath(title, defaultName, dir, "xlsx"));
        return path;
    }
}
