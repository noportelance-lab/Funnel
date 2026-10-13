namespace Funnel.Engine;

/// <summary>Where Funnel keeps its own state: %LOCALAPPDATA%\Funnel.</summary>
public static class AppPaths
{
    public static string Home
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(root, "Funnel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Default folder for Excel exports: Documents\Funnel.</summary>
    public static string DefaultExportDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Funnel");
}
