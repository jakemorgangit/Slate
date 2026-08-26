using System.Reflection;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Slate.Services;

/// <summary>
/// A <see cref="BlazorWebView"/> that serves its wwwroot from resources compiled into the
/// assembly rather than from files on disk. That is what lets the published app be a single
/// .exe with nothing beside it.
/// </summary>
public sealed class EmbeddedBlazorWebView : BlazorWebView
{
    public override IFileProvider CreateFileProvider(string contentRootDir) =>
        new EmbeddedWebAssetFileProvider(typeof(EmbeddedBlazorWebView).Assembly);
}

/// <summary>
/// Serves files embedded under the "webassets/" logical prefix. The csproj puts every static
/// web asset there, including Blazor's own _framework scripts.
/// </summary>
public sealed class EmbeddedWebAssetFileProvider : IFileProvider
{
    private const string Prefix = "webassets/";

    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _resourcesByPath;

    public EmbeddedWebAssetFileProvider(Assembly assembly)
    {
        _assembly = assembly;
        _resourcesByPath = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                name => Normalize(name[Prefix.Length..]),
                name => name,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The web asset paths this provider can serve - handy when diagnosing a blank window.</summary>
    public IReadOnlyCollection<string> Paths => _resourcesByPath.Keys;

    public IFileInfo GetFileInfo(string subpath)
    {
        var key = Normalize(subpath);
        return _resourcesByPath.TryGetValue(key, out var resource)
            ? new EmbeddedWebAsset(_assembly, resource, key)
            : new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        var prefix = Normalize(subpath);
        if (prefix.Length > 0) prefix += "/";

        var children = _resourcesByPath
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => (IFileInfo)new EmbeddedWebAsset(_assembly, pair.Value, pair.Key))
            .ToList();

        return children.Count == 0 ? NotFoundDirectoryContents.Singleton : new EmbeddedDirectory(children);
    }

    // Embedded assets cannot change while the app is running.
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private sealed class EmbeddedWebAsset(Assembly assembly, string resourceName, string path) : IFileInfo
    {
        /// <summary>
        /// Embedded assets change only when the executable does, so its timestamp is the honest
        /// answer. Assembly.Location is empty in a single-file app, hence ProcessPath.
        /// </summary>
        private static readonly DateTimeOffset BuildTime =
            Environment.ProcessPath is { Length: > 0 } exe && File.Exists(exe)
                ? File.GetLastWriteTimeUtc(exe)
                : DateTimeOffset.UnixEpoch;

        public bool Exists => true;
        public bool IsDirectory => false;
        public DateTimeOffset LastModified => BuildTime;
        public string Name { get; } = path[(path.LastIndexOf('/') + 1)..];
        public string? PhysicalPath => null;

        public long Length
        {
            get
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                return stream?.Length ?? 0;
            }
        }

        public Stream CreateReadStream() =>
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded web asset '{resourceName}' is missing.");
    }

    private sealed class EmbeddedDirectory(List<IFileInfo> entries) : IDirectoryContents
    {
        public bool Exists => true;
        public IEnumerator<IFileInfo> GetEnumerator() => entries.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
