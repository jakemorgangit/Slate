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

    public static string BuildDate =>
        Self.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value is { Length: > 0 } stamped
            ? stamped
            : "unknown";

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
