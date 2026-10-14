namespace Funnel;

/// <summary>The macOS entry point (Program.cs is the Windows one).</summary>
internal static class Program
{
    // The licence runs out at the start of this day (local time); from then on the app won't start.
    private static readonly DateTime LicenceExpires = new(2027, 1, 1);
    private const string LicenceExpiredText =
        "App licence expired.\n\nContact Noé for further assistance: no.portelance@gmail.com";

    [STAThread]
    private static int Main(string[] args)
    {
        var selfTest = args.Length > 0 && args[0] == "--selftest";

        if (DateTime.Now >= LicenceExpires)
        {
            if (selfTest) { Console.Error.WriteLine(LicenceExpiredText.Replace("\n\n", " ")); return 1; }
            Cocoa.Alert("Funnel", LicenceExpiredText, critical: true);
            return 1;
        }

        // Headless check of every provider call + an Excel export, for use after a build.
        if (selfTest)
            return SelfTest.Run(args);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash("background thread", e.ExceptionObject as Exception);
        try
        {
            new MacWindow(AppWindows.Config).Run();
            return 0;
        }
        catch (Exception ex)
        {
            ReportCrash("startup", ex);
            return 1;
        }
    }

    // Write the crash to Funnel-crash.log (Desktop first, then TMPDIR, then app folder) and show it.
    private static void ReportCrash(string where, Exception? ex)
    {
        var text = $"Funnel failed ({where}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{ex}";
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
        Cocoa.Alert("Funnel crashed", text.Length > 1500 ? text[..1500] + "…" : text, critical: true);
    }
}
