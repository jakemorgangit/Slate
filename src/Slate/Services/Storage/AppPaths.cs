namespace Slate.Services.Storage;

public static class AppPaths
{
    /// <summary>The folder this app used before it was called Slate.</summary>
    private const string FormerName = "WorkItemPlanner";

    /// <summary>
    /// Where settings, the plan and the token cache live. Normally under LocalAppData, but
    /// SLATE_DATA moves it - which is what makes a portable copy on a stick possible, and
    /// what lets a second copy run without touching the first one's data.
    /// </summary>
    public static string DataDirectory { get; } = Resolve();

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("SLATE_DATA");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden.Trim());

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(local, "Slate");

        AdoptFormerFolder(Path.Combine(local, FormerName), directory);
        return directory;
    }

    /// <summary>
    /// Brings settings, the plan and the sign-in cache across from the folder the app used
    /// under its old name, so a rename does not read as having lost everything.
    ///
    /// Copied rather than moved, and only when there is nothing here yet: if this ever runs
    /// against a half-migrated folder, or alongside an older build that is still writing to
    /// the old one, the original is still sitting there intact.
    /// </summary>
    private static void AdoptFormerFolder(string former, string current)
    {
        try
        {
            if (!Directory.Exists(former)) return;
            if (Directory.Exists(current) && Directory.EnumerateFileSystemEntries(current).Any()) return;

            Directory.CreateDirectory(current);

            foreach (var source in Directory.EnumerateFiles(former))
            {
                var destination = Path.Combine(current, Path.GetFileName(source));
                if (!File.Exists(destination)) File.Copy(source, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting fresh is a far better outcome than refusing to start at all.
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string PlanFile => Path.Combine(DataDirectory, "plan.json");
    public static string TokenCacheFile => Path.Combine(DataDirectory, "msal.cache");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
