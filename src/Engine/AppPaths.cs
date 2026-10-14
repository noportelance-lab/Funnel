namespace Funnel.Engine;

/// <summary>Where Funnel keeps its own state: %LOCALAPPDATA%\Funnel on Windows, ~/Library/Application Support/Funnel on macOS.</summary>
public static class AppPaths
{
    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Home
    {
        get
        {
            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(UserHome, "Library", "Application Support")
                : Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? UserHome;
            var dir = Path.Combine(root, "Funnel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Default folder for Excel exports: Documents\Funnel.</summary>
    public static string DefaultExportDir => OperatingSystem.IsMacOS()
        ? Path.Combine(UserHome, "Documents", "Funnel")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Funnel");
}
