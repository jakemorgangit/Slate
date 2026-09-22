using System.Reflection;

namespace Slate.Services;

/// <summary>
/// Who made this and when, read from the assembly rather than written out twice. The build
/// date is stamped in by MSBuild, because a single-file publish has no file on disk to ask.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    public const string Author = "Jake Morgan";
    public const string Organisation = "Blackcat Data Solutions Limited";

    public static string Name => Self.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Slate";

    public static string Version =>
        Self.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public static string BuildDate => Metadata("BuildDate") ?? "unknown";

    /// <summary>
    /// "slim" or "standalone" when build/publish.ps1 made this build, otherwise null. The
    /// two are different files on the release, and installing the wrong one over the other
    /// would either bloat a slim copy or strand a standalone one without a runtime - so
    /// anything unrecognised, including a dev build, is treated as not knowing.
    /// </summary>
    public static string? Flavour => Metadata("SlateFlavour") switch
    {
        "slim" => "slim",
        "standalone" => "standalone",
        _ => null,
    };

    /// <summary>The runtime identifier the build was published for, such as win-x64.</summary>
    public static string? Runtime => Metadata("SlateRuntime") is { } rid && rid.StartsWith("win-", StringComparison.Ordinal)
        ? rid
        : null;

    /// <summary>
    /// The release asset that is this same build at another version, following the naming
    /// the releases use: Slate-1.5.6-win-x64-standalone.exe. Null when the build does not
    /// know what it is, which is the signal not to offer an in-place install at all.
    /// </summary>
    public static string? ReleaseAssetName(string version) =>
        Flavour is { } flavour && Runtime is { } rid ? $"Slate-{version}-{rid}-{flavour}.exe" : null;

    private static string? Metadata(string key) =>
        Self.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } stamped
            ? stamped
            : null;

    /// <summary>The build date as something to read, falling back to the raw stamp.</summary>
    public static string BuildDateLong =>
        DateTime.TryParse(BuildDate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("d MMMM yyyy")
            : BuildDate;

    public static string Copyright =>
        Self.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? $"Copyright (c) {Organisation}";
}
