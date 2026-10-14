namespace Funnel;

/// <summary>
/// What the <see cref="Bridge"/> needs from the window it serves: the WinForms + WebView2 window on
/// Windows (<c>MainForm</c>), the Photino window on macOS (<c>Mac/MacWindow</c>). Every member may
/// be called from any thread; the window marshals to its UI thread.
/// </summary>
public interface IAppWindow
{
    /// <summary>Send the page a message (a JSON string).</summary>
    void PostJson(string json);
    void SetTitle(string title);
    void CloseWindow();
    /// <summary>Bring the window to the front.</summary>
    void Raise();
    /// <summary>Ask where to save an Excel workbook; null if the user cancelled. Blocks until answered.</summary>
    string? AskSavePath(string title, string defaultName, string dir);
    bool IsClosed { get; }
}
