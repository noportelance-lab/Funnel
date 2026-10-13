using System.Reflection;

namespace Funnel.Engine;

/// <summary>
/// Extracts resources embedded in the single-file .exe (the web UI and the icon) to a temp
/// folder so the published .exe needs no side files.
/// </summary>
public static class Assets
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "Funnel_runtime");
        Directory.CreateDirectory(dir);
        return dir;
    });

    private static string Extract(string logicalName, string outPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException($"Embedded resource missing: {logicalName}");
        using var fs = File.Create(outPath);
        stream.CopyTo(fs);
        return outPath;
    }

    private static readonly Lazy<System.Drawing.Icon?> AppIconCache = new(() =>
    {
        try { return new System.Drawing.Icon(Extract("data.app.ico", Path.Combine(Root.Value, "app.ico"))); }
        catch { return null; }
    });

    public static System.Drawing.Icon? AppIcon() => AppIconCache.Value;

    private static readonly Lazy<System.Drawing.Image?> AppLogoCache = new(() =>
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web.logo.png");
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms); ms.Position = 0;
            using var tmp = System.Drawing.Image.FromStream(ms);
            return new System.Drawing.Bitmap(tmp);   // a copy that doesn't hold on to the stream
        }
        catch { return null; }
    });

    /// <summary>The app logo as an image (512 × 512, transparent corners) for native drawing.</summary>
    public static System.Drawing.Image? AppLogo() => AppLogoCache.Value;

    /// <summary>Extract the web UI (index.html + styles.css + app.js + explain.js + loader.js + app and club logos + start video) and return its folder.
    /// Once per run: later windows reuse the files the first one is already reading.</summary>
    public static string WebRoot() => WebRootOnce.Value;

    private static readonly Lazy<string> WebRootOnce = new(ExtractWebRoot);

    private static string ExtractWebRoot()
    {
        var dir = Path.Combine(Root.Value, "web");
        Directory.CreateDirectory(dir);
        foreach (var file in new[] { "index.html", "styles.css", "app.js", "explain.js", "loader.js", "logo.png", "logo-mic.webp", "logo-dmcc.jpg" })
            Extract("web." + file, Path.Combine(dir, file));

        // The video is 14 MB and never changes between runs of the same build — only rewrite it
        // when the copy on disk doesn't match the embedded one.
        var video = Path.Combine(dir, "start-bg.webm");
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web.start-bg.webm"))
        {
            if (s != null && (!File.Exists(video) || new FileInfo(video).Length != s.Length))
                Extract("web.start-bg.webm", video);
        }
        return dir;
    }
}
