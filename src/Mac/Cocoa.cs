using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Funnel;

/// <summary>The few bits of AppKit Photino doesn't wrap: a save panel with a suggested file name, and alerts.</summary>
internal static class Cocoa
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel, bool arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUtf8(IntPtr target, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    private static IntPtr Sel(string name) => sel_registerName(name);
    private static IntPtr NSString(string s) => SendUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s);

    /// <summary>Show a save panel (main thread only). The full path chosen, or null if cancelled.</summary>
    public static string? AskSavePath(string title, string defaultName, string dir, string extension)
    {
        var panel = Send(objc_getClass("NSSavePanel"), Sel("savePanel"));
        Send(panel, Sel("setTitle:"), NSString(title));
        Send(panel, Sel("setMessage:"), NSString(title));
        Send(panel, Sel("setNameFieldStringValue:"), NSString(defaultName));
        if (Directory.Exists(dir))
            Send(panel, Sel("setDirectoryURL:"), Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), NSString(dir)));
        Send(panel, Sel("setAllowedFileTypes:"), Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), NSString(extension)));
        Send(panel, Sel("setCanCreateDirectories:"), true);
        Send(panel, Sel("setExtensionHidden:"), false);
        if ((long)Send(panel, Sel("runModal")) != 1 /* NSModalResponseOK */) return null;
        var url = Send(panel, Sel("URL"));
        if (url == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(Send(Send(url, Sel("path")), Sel("UTF8String")));
    }

    /// <summary>
    /// Let pages autoplay media without a click (main thread, once the window exists). Photino
    /// doesn't pass its autoplay switch to WebKit on macOS; this sets the same WebKit preference on
    /// every web view in the app. Muted start-screen video is all Funnel autoplays.
    /// </summary>
    public static void AllowAutoplay()
    {
        var webViewClass = objc_getClass("WKWebView");
        var windows = Send(Send(objc_getClass("NSApplication"), Sel("sharedApplication")), Sel("windows"));
        for (long i = 0, n = (long)Send(windows, Sel("count")); i < n; i++)
            foreach (var web in Find(Send(Send(windows, Sel("objectAtIndex:"), (IntPtr)i), Sel("contentView")), webViewClass))
            {
                var prefs = Send(Send(web, Sel("configuration")), Sel("preferences"));
                foreach (var sel in new[] { "_setRequiresUserGestureForVideoPlayback:", "_setRequiresUserGestureForAudioPlayback:" })
                    if (Send(prefs, Sel("respondsToSelector:"), Sel(sel)) != IntPtr.Zero) Send(prefs, Sel(sel), false);
            }
    }

    private static IEnumerable<IntPtr> Find(IntPtr view, IntPtr cls)
    {
        if (view == IntPtr.Zero) yield break;
        if (Send(view, Sel("isKindOfClass:"), cls) != IntPtr.Zero) { yield return view; yield break; }
        var subs = Send(view, Sel("subviews"));
        for (long i = 0, n = (long)Send(subs, Sel("count")); i < n; i++)
            foreach (var v in Find(Send(subs, Sel("objectAtIndex:"), (IntPtr)i), cls)) yield return v;
    }

    /// <summary>A plain alert that needs no window (startup and crash messages), via AppleScript.</summary>
    public static void Alert(string title, string message, bool critical)
    {
        try
        {
            static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\n") + "\"";
            var script = $"display alert {Q(title)} message {Q(message)}{(critical ? " as critical" : "")} buttons {{\"OK\"}} default button \"OK\"";
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/osascript") { ArgumentList = { "-e", script }, UseShellExecute = false });
            p?.WaitForExit();
        }
        catch { /* nothing more we can do */ }
    }
}
