using System.Text.Json;
using Slate.Models;

namespace Slate.Services.Storage;

/// <summary>
/// The last work item list that loaded successfully, so the sidebar has something to show
/// the instant the app opens rather than sitting on "Loading work items…" until Azure
/// DevOps answers - which after a reboot, on waking, or on a new release, can be a while.
///
/// Purely a convenience: nothing here is ever trusted over a fresh load, and a failure to
/// read or write it is no different from there being no cache at all - it must never
/// surface to the user or take the app down.
/// </summary>
public sealed class WorkItemsCacheStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record CacheContents(string Stamp, DateTimeOffset LoadedAt, List<WorkItem> Items);

    /// <summary>
    /// What was last saved for this connection, or null when there is nothing usable - no
    /// file, a corrupt one, or one saved against a different organization, project or sign-in
    /// mode than <paramref name="stamp"/> names. A cache from another connection is somebody
    /// else's data and must never be shown as if it were this one's.
    /// </summary>
    public (List<WorkItem> Items, DateTimeOffset LoadedAt)? TryLoad(string stamp)
    {
        try
        {
            if (!File.Exists(AppPaths.WorkItemsCacheFile)) return null;

            var contents = JsonSerializer.Deserialize<CacheContents>(
                File.ReadAllText(AppPaths.WorkItemsCacheFile), Json);

            return contents is null || contents.Stamp != stamp
                ? null
                : (contents.Items, contents.LoadedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the list that just loaded, atomically like <see cref="PlanStore"/> - a temp file
    /// then a rename, so a crash or a second launch mid-write can never leave a half-written
    /// cache behind for the next read to trip over.
    /// </summary>
    public void Save(string stamp, DateTimeOffset loadedAt, IReadOnlyList<WorkItem> items)
    {
        try
        {
            AppPaths.EnsureCreated();

            var contents = new CacheContents(stamp, loadedAt, [.. items]);
            var temp = AppPaths.WorkItemsCacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(contents, Json));
            File.Move(temp, AppPaths.WorkItemsCacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next launch simply falls back to the loading spinner it would have shown
            // anyway - worse luck, not a broken app.
        }
    }
}
