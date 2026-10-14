using Funnel.Engine;

namespace Funnel;

/// <summary>The macOS app's one window and the settings it shares with the engine.</summary>
internal static class AppWindows
{
    public static readonly AppConfig Config = AppConfig.Load();
    public static MacWindow? Active;

    /// <summary>Tell the window (the Windows build tells every window but the sender).</summary>
    public static void Broadcast(Bridge? except, string name, object data)
    {
        if (Active is { } w && w.Bridge != except) w.Bridge.Notify(name, data);
    }
}
