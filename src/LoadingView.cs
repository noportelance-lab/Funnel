using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Funnel;

/// <summary>
/// The loading screen, drawn natively so it's on screen the moment the window opens, while the
/// display engine (WebView2) starts and the page loads underneath it. A stock chart that doubles
/// as the progress bar: the arrow sits at x = progress along a random price path, and the right
/// edge of the chart is 100%. Progress follows real startup steps (<see cref="Done"/>, partly
/// reported by the page), but never runs faster than <see cref="MinTime"/> end to end.
/// </summary>
public sealed class LoadingView : Control
{
    private static readonly TimeSpan MinTime = TimeSpan.FromSeconds(3);
    /// <summary>If a step never reports (a page error, say), finish anyway rather than hang.</summary>
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(20);

    // Each step owns the stretch of the chart up to Until; the status text names the step the
    // arrow is crossing, so it never claims a step finished before it did.
    private sealed record Step(string Id, double Until, string Text);
    private static readonly Step[] Steps =
    {
        new("engine", 0.25, "Starting display engine…"),
        new("ui", 0.40, "Loading interface…"),
        new("settings", 0.50, "Loading settings…"),
        new("market", 0.85, "Fetching market data…"),
        new("start", 1.00, "Preparing start screen…"),
    };

    private const float UW = 600, UH = 220, PadT = 34, PadB = 18;   // chart units, as in a 600×220 viewBox
    private const int N = 56;

    private static readonly Color Ink = Color.FromArgb(14, 20, 27);
    private static readonly Color Green = Color.FromArgb(34, 197, 94);

    private readonly HashSet<string> _done = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly PointF[] _pts;
    private TimeSpan _stepStart;
    private double _shown;
    private bool _finished;

    /// <summary>Raised once, when the arrow has reached 100% and held there for a moment.</summary>
    public event EventHandler? Finished;

    public LoadingView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ink;
        _pts = RandomWalk();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        // The window has no title bar of its own (the page draws one), so this moves it meanwhile.
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || FindForm() is not { } f) return;
            ReleaseCapture();
            SendMessage(f.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Mark a startup step finished (ids as in <see cref="Steps"/>). Extra calls are harmless.</summary>
    public void Done(string id)
    {
        if (!Steps.Any(s => s.Id == id) || !_done.Add(id)) return;
        _stepStart = _clock.Elapsed;
    }

    /// <summary>A random walk that trends up from the lower left to the upper right, with real wobble.</summary>
    private static PointF[] RandomWalk()
    {
        var rnd = new Random();
        var pts = new PointF[N];
        double dev = 0;
        for (int i = 0; i < N; i++)
        {
            double f = i / (double)(N - 1);
            dev = dev * 0.8 + (rnd.NextDouble() - 0.5) * 0.24;
            double trend = 0.22 + 0.6 * f + 0.06 * Math.Sin(f * Math.PI * 2.4);
            double v = i == 0 ? 0.2 : i == N - 1 ? 0.86 : Math.Clamp(trend + dev, 0.05, 0.97);
            pts[i] = new PointF((float)(f * UW), (float)(PadT + (1 - v) * (UH - PadT - PadB)));
        }
        return pts;
    }

    /// <summary>
    /// Where the arrow is allowed to be right now: finished steps count in full; the step under way
    /// creeps toward its end (never reaching it) so a slow step still shows movement.
    /// </summary>
    private double Target()
    {
        if (_clock.Elapsed > GiveUp) return 1;
        double prev = 0;
        foreach (var s in Steps)
        {
            if (!_done.Contains(s.Id))
            {
                double creep = 1 - Math.Exp(-(_clock.Elapsed - _stepStart).TotalSeconds / 2.5);
                return prev + (s.Until - prev) * 0.85 * creep;
            }
            prev = s.Until;
        }
        return 1;
    }

    private void Tick()
    {
        double goal = Math.Min(Target(), _clock.Elapsed / MinTime);
        if (goal > _shown) _shown = Math.Min(goal, _shown + Math.Max((goal - _shown) * 0.12, 0.004));
        Invalidate();
        if (_shown >= 1 && !_finished)
        {
            _finished = true;
            var hold = new System.Windows.Forms.Timer { Interval = 350 };
            hold.Tick += (_, _) => { hold.Dispose(); _timer.Stop(); Finished?.Invoke(this, EventArgs.Empty); };
            hold.Start();
        }
    }

    private (float Y, float Slope) YAt(float u)
    {
        int i = Math.Min(N - 2, (int)(u / UW * (N - 1)));
        var (a, b) = (_pts[i], _pts[i + 1]);
        float f = (u - a.X) / (b.X - a.X);
        return (a.Y + (b.Y - a.Y) * f, (b.Y - a.Y) / (b.X - a.X));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Ink);

        float s = DeviceDpi / 96f;
        float cardW = Math.Min(620 * s, Width - 40 * s);
        float k = cardW / UW;                                   // chart units → pixels
        float titleH = 46 * s, gap = 14 * s, chartH = UH * k, statusH = 30 * s;
        float x0 = (Width - cardW) / 2, y0 = Math.Max(10 * s, (Height - (titleH + gap + chartH + statusH)) / 2);
        float cy = y0 + titleH + gap;                            // top of the chart
        PointF P(PointF u) => new(x0 + u.X * k, cy + u.Y * k);

        // Header: "Funnel" with the app logo to its right.
        using (var title = new Font("Segoe UI", 25.5f, FontStyle.Bold, GraphicsUnit.Point))
        {
            var size = g.MeasureString("Funnel", title);
            g.DrawString("Funnel", title, Brushes.White, x0 - 4 * s, y0 + (titleH - size.Height) / 2);
            float logo = 34 * s;
            DrawLogo(g, new RectangleF(x0 - 4 * s + size.Width + 4 * s, y0 + (titleH - logo) / 2, logo, logo));
        }

        // Grid, the dashed path still to come, and the 100% marker at its end.
        using (var grid = new Pen(Color.FromArgb(18, 255, 255, 255), 1))
            foreach (var f in new[] { 0.25f, 0.5f, 0.75f })
            {
                float y = cy + (PadT + f * (UH - PadT - PadB)) * k;
                g.DrawLine(grid, x0, y, x0 + cardW, y);
            }
        var line = _pts.Select(P).ToArray();
        using (var ghost = new Pen(Color.FromArgb(38, 255, 255, 255), 2 * s) { DashPattern = new[] { 2f, 2.5f } })
            g.DrawLines(ghost, line);
        var end = line[^1];
        using (var endFill = new SolidBrush(Ink)) g.FillEllipse(endFill, end.X - 4 * s, end.Y - 4 * s, 8 * s, 8 * s);
        using (var endPen = new Pen(Color.FromArgb(89, 255, 255, 255), 2 * s)) g.DrawEllipse(endPen, end.X - 4 * s, end.Y - 4 * s, 8 * s, 8 * s);

        // The part already travelled: filled area and solid line, clipped at the arrow.
        float hu = (float)(_shown * UW), hx = x0 + hu * k;
        var state = g.Save();
        g.SetClip(new RectangleF(x0 - 2 * s, cy - 4 * s, hx - x0 + 2 * s, chartH + 8 * s));
        using (var area = new GraphicsPath())
        {
            area.AddLines(line);
            area.AddLine(line[^1], new PointF(x0 + cardW, cy + chartH));
            area.AddLine(new PointF(x0 + cardW, cy + chartH), new PointF(x0, cy + chartH));
            area.CloseFigure();
            using var fill = new LinearGradientBrush(new PointF(0, cy), new PointF(0, cy + chartH + 1),
                Color.FromArgb(82, Green), Color.FromArgb(0, Green));
            g.FillPath(fill, area);
        }
        using (var pen = new Pen(Green, 2.5f * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, line);
        g.Restore(state);

        // The arrow, pointing along the line, with a pulse behind it.
        var (hyU, slope) = YAt(Math.Min(UW - 0.01f, hu));
        float hy = cy + hyU * k;
        double phase = _clock.Elapsed.TotalSeconds % 1.2 / 1.2;
        float pr = (float)(9 * s * (0.5 + 1.1 * phase));
        using (var pulse = new SolidBrush(Color.FromArgb((int)(115 * (1 - phase)), Green)))
            g.FillEllipse(pulse, hx - pr, hy - pr, pr * 2, pr * 2);
        state = g.Save();
        g.TranslateTransform(hx, hy);
        g.RotateTransform((float)(Math.Atan(slope) * 180 / Math.PI));
        using (var arrow = new SolidBrush(Green))
            g.FillPolygon(arrow, new[] { new PointF(-7 * s, -6 * s), new PointF(7 * s, 0), new PointF(-7 * s, 6 * s), new PointF(-3.5f * s, 0) });
        g.Restore(state);

        // The percentage tag riding above the arrow.
        float tw = 44 * s, th = 19 * s, tx = Math.Clamp(hx, x0 + tw / 2, x0 + cardW - tw / 2);
        var tag = new RectangleF(tx - tw / 2, hy - 27 * s, tw, th);
        using (var tagPath = Rounded(tag, th / 2))
        using (var tagFill = new SolidBrush(Green))
            g.FillPath(tagFill, tagPath);
        using (var pctFont = new Font("Segoe UI", 8.6f, FontStyle.Bold, GraphicsUnit.Point))
        using (var pctBrush = new SolidBrush(Color.FromArgb(6, 33, 15)))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString($"{Math.Round(_shown * 100)}%", pctFont, pctBrush, tag, center);

        // What's happening, right under the chart.
        var step = Steps.FirstOrDefault(st => _shown < st.Until);
        using (var statusFont = new Font("Segoe UI", 9.4f, FontStyle.Regular, GraphicsUnit.Point))
        using (var statusBrush = new SolidBrush(Color.FromArgb(166, 255, 255, 255)))
            g.DrawString(step?.Text ?? "Ready", statusFont, statusBrush, x0 - 2 * s, cy + chartH + 12 * s);
    }

    /// <summary>The app logo (the sidebar's), from the embedded image; the old vector mark if that's missing.</summary>
    private static void DrawLogo(Graphics g, RectangleF r)
    {
        if (Funnel.Engine.Assets.AppLogo() is { } img)
        {
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, r);
            g.InterpolationMode = mode;
            return;
        }
        float u = r.Width / 64;
        PointF L(float x, float y) => new(r.X + x * u, r.Y + y * u);
        using (var bg = Rounded(r, 14 * u))
        using (var grad = new LinearGradientBrush(r, Color.FromArgb(37, 99, 235), Color.FromArgb(30, 58, 138), LinearGradientMode.ForwardDiagonal))
            g.FillPath(grad, bg);
        g.FillPolygon(Brushes.White, new[] { L(12, 14), L(52, 14), L(36, 34), L(36, 50), L(28, 54), L(28, 34) });
        using var pen = new Pen(Color.FromArgb(37, 99, 235), 3.2f * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { L(18, 22), L(26, 18), L(32, 21), L(43, 16) });
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
