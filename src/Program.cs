using System.Runtime.InteropServices;

namespace Funnel;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    // The licence runs out at the start of this day (local time); from then on the app won't start.
    private static readonly DateTime LicenceExpires = new(2027, 1, 1);
    private const string LicenceExpiredText =
        "App licence expired.\r\n\r\nContact Noé for further assistance: no.portelance@gmail.com";

    [STAThread]
    private static int Main(string[] args)
    {
        var selfTest = args.Length > 0 && args[0] == "--selftest";

        if (DateTime.Now >= LicenceExpires)
        {
            if (selfTest) { Console.Error.WriteLine(LicenceExpiredText.Replace("\r\n\r\n", " ")); return 1; }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            // Blocks until OK or the close button; either way Main returns and the app exits.
            MessageBox.Show(LicenceExpiredText, "Funnel", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            return 1;
        }

        // Headless check of every provider call + an Excel export, for use after a build.
        if (selfTest)
            return SelfTest.Run(args);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash("background thread", e.ExceptionObject as Exception);
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }
        Application.ThreadException += (_, e) => ReportCrash("UI thread", e.Exception);

        // Launched as `dotnet Funnel.dll`, the dotnet host owns a console window — hide it.
        try { var c = GetConsoleWindow(); if (c != IntPtr.Zero) ShowWindow(c, SW_HIDE); } catch { }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FunnelContext());
            return 0;
        }
        catch (Exception ex)
        {
            ReportCrash("startup", ex);
            return 1;
        }
    }

    // Write the crash to Funnel-crash.log (Desktop first, then TEMP, then app folder) and show it.
    private static void ReportCrash(string where, Exception? ex)
    {
        var text = $"Funnel failed ({where}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n\r\n{ex}";
        foreach (var dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.GetTempPath(),
            AppContext.BaseDirectory,
        })
        {
            try { if (!string.IsNullOrEmpty(dir)) { File.WriteAllText(Path.Combine(dir, "Funnel-crash.log"), text); break; } }
            catch { /* try the next location */ }
        }
        try { MessageBox.Show(text, "Funnel crashed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { /* nothing more we can do */ }
    }
}
