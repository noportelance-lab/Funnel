using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Funnel.Engine;

namespace Funnel;

/// <summary>
/// An app window: a full-window WebView2 hosting the HTML/CSS/JS UI. The first window opens behind
/// the loading screen and the start screen. Later ones are tabs dragged out of a window
/// (<see cref="AppWindows"/>): a spare window is kept loaded and hidden, so one can appear under
/// the pointer the moment a tab leaves its strip.
/// </summary>
public sealed class MainForm : Form, IAppWindow
{
    public enum Kind { First, Seeded, Spare }

    private static readonly Color Ink = Color.FromArgb(14, 20, 27);
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Ink };
    private readonly AppConfig _config = AppWindows.Config;
    private Bridge? _bridge;
    // Covers the web view from the first frame until startup is done; the page loads underneath.
    private LoadingView? _loading;
    // A dragged-out tab's page, which a window opened the slow way (no spare ready) starts on.
    private readonly string? _seed;
    public readonly Kind WindowKind;
    public readonly int Id = AppWindows.NextId();

    public Bridge? Bridge => _bridge;
    public double DpiScale => DeviceDpi / 96.0;
    /// <summary>Where the page's tab strip is, in page pixels (the page reports it).</summary>
    public RectangleF StripCss { get; set; }

    public MainForm(Kind kind = Kind.First, string? seed = null, Rectangle? bounds = null)
    {
        WindowKind = kind;
        _seed = seed;
        Text = $"Funnel  v{Bridge.AppVersion}";
        // The first window opens at the loading screen's size; the user makes it bigger from there.
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var start = new Size(Math.Min(1500, wa.Width - 40), Math.Min(940, wa.Height - 48));
        MinimumSize = new Size(Math.Min((int)(640 * DpiScale), start.Width), Math.Min((int)(440 * DpiScale), start.Height));
        if (bounds is { } b) { StartPosition = FormStartPosition.Manual; Bounds = b; }
        else if (kind == Kind.Spare) { StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(new Point(-32000, -32000), AppWindows.Active?.Size ?? Size); }
        else { StartPosition = FormStartPosition.CenterScreen; ClientSize = start; }
        BackColor = Ink;   // matches the sidebar, so there's no white flash
        try { Icon = Assets.AppIcon() ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(_web);
        if (kind == Kind.First)
        {
            _loading = new LoadingView { Dock = DockStyle.Fill };
            Controls.Add(_loading);
            _loading.BringToFront();
            _loading.Finished += (_, _) => Reveal();
        }
        if (kind != Kind.Spare) AppWindows.Opened(this);
        // A spare starts its page while hidden (below); being shown later mustn't start it again.
        Load += (_, _) => { if (WindowKind != Kind.Spare) StartWeb(); };
        if (kind == Kind.Spare) { _ = Handle; _ = _web.Handle; StartWeb(); }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: the title bar takes the tab strip's grey, so the top of the window reads as one.
        int caption = 0xE3 | 0xE8 << 8 | 0xEE << 16;   // #e3e8ee as a COLORREF (0x00BBGGRR)
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    /// <summary>A spare is shown under the pointer mid-drag without taking the focus from the drag.</summary>
    public bool ShowQuietly { get; set; }
    protected override bool ShowWithoutActivation => ShowQuietly;

    /// <summary>Where a point of the page (CSS pixels) is, from the window's top-left corner.</summary>
    public Point PageOffset(double cssX, double cssY)
    {
        var client0 = PointToScreen(Point.Empty);
        return new Point(client0.X - Bounds.X + (int)Math.Round(cssX * DpiScale), client0.Y - Bounds.Y + (int)Math.Round(cssY * DpiScale));
    }

    /// <summary>The tab strip, on screen (empty until the page has reported it).</summary>
    public Rectangle StripOnScreen()
    {
        var c = PointToScreen(Point.Empty);
        double s = DpiScale;
        return new Rectangle(c.X + (int)(StripCss.X * s), c.Y + (int)(StripCss.Y * s), (int)(StripCss.Width * s), (int)(StripCss.Height * s));
    }

    // ---------- IAppWindow (any thread) ----------

    public bool IsClosed => IsDisposed;
    public void PostJson(string json) => BeginInvoke(() => { try { _web.CoreWebView2?.PostWebMessageAsString(json); } catch { } });
    public void SetTitle(string title) => BeginInvoke(() => Text = title);
    public void CloseWindow() => BeginInvoke(Close);
    public void Raise() => BeginInvoke(Activate);

    public string? AskSavePath(string title, string defaultName, string dir) => (string?)Invoke(new Func<string?>(() =>
    {
        using var d = new SaveFileDialog
        {
            Title = title,
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = defaultName,
            InitialDirectory = dir,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "xlsx",
        };
        return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
    }));

    // ---------- the page ----------

    /// <summary>Lift the loading screen off the (already painted) start screen.</summary>
    private void Reveal()
    {
        if (_loading == null) return;
        Controls.Remove(_loading);
        _loading.Dispose();
        _loading = null;
        _bridge?.Notify("revealed", new { });
    }

    private async void StartWeb()
    {
        try { await CreateWebAsync(); }
        catch (Exception ex)
        {
            if (WindowKind == Kind.Spare) { AppWindows.SpareFailed(this); return; }
            MessageBox.Show(
                "Funnel could not start its display engine (WebView2).\n\n" +
                "WebView2 Runtime is required. It ships with Windows 11; if missing, install the " +
                "Evergreen WebView2 Runtime from Microsoft.\n\nTechnical detail: " + ex.Message,
                "Startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CreateWebAsync()
    {
        // Off the UI thread: the first run copies the 14 MB start video out, and the loading
        // screen should keep animating meanwhile.
        var webRoot = Task.Run(Assets.WebRoot);
        var env = await AppWindows.EnvAsync();
        await _web.EnsureCoreWebView2Async(env);
        _loading?.Done("engine");

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        core.SetVirtualHostNameToFolderMapping("app.local", await webRoot, CoreWebView2HostResourceAccessKind.Allow);

        _bridge = new Bridge(this, _config) { LoadStep = id => _loading?.Done(id), Seed = _seed, WindowId = Id };
        core.NavigationCompleted += (_, _) => _loading?.Done("ui");

        // Only our own page may talk to the Bridge, and the top frame may not leave app.local —
        // the Bridge saves files and opens paths on the strength of a JSON "cmd" field.
        core.WebMessageReceived += (_, ev) =>
        {
            if (!(ev.Source?.StartsWith("https://app.local/", StringComparison.Ordinal) ?? false)) return;
            try { _bridge!.Handle(ev.TryGetWebMessageAsString()); } catch { }
        };
        core.NavigationStarting += (_, ev) =>
        {
            if (!(ev.Uri?.StartsWith("https://app.local/", StringComparison.Ordinal) ?? false))
            {
                ev.Cancel = true;
                // An external link (a company website) opens in the real browser instead.
                if (ev.Uri is { } u && (u.StartsWith("https://") || u.StartsWith("http://"))) Bridge.OpenExternal(u);
            }
        };
        core.NewWindowRequested += (_, ev) => { ev.Handled = true; Bridge.OpenExternal(ev.Uri); };

        // Later windows skip the start screen: whoever dragged a tab out is already past it.
        var q = WindowKind switch { Kind.Seeded => "?win=tab", Kind.Spare => "?win=spare", _ => "" };
#if DEBUG
        // Development only: open past the start screen, for screenshots of the running app.
        if (WindowKind == Kind.First && Environment.GetEnvironmentVariable("FUNNEL_DEV_NOGATE") == "1") q = "?dev=nogate";
#endif
        core.Navigate("https://app.local/index.html" + q);
    }
}
