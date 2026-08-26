namespace Slate.Models;

/// <summary>
/// One node of a project's area tree. <see cref="Path"/> is the value Azure DevOps wants in
/// System.AreaPath - the project name followed by each ancestor, backslash separated - which
/// is built from the node names rather than the API's own path, because that one carries an
/// extra "\Area" segment the field does not use.
/// </summary>
public sealed record AreaNode(string Name, string Path, IReadOnlyList<AreaNode> Children)
{
    public bool HasChildren => Children.Count > 0;

    /// <summary>Depth-first walk, this node included.</summary>
    public IEnumerable<AreaNode> Flatten()
    {
        yield return this;

        foreach (var child in Children)
            foreach (var descendant in child.Flatten())
                yield return descendant;
    }

    /// <summary>
    /// The chain of nodes from the root down to <paramref name="path"/>, or an empty list if
    /// that path is not in this tree. Used to put the dropdowns back where they were.
    /// </summary>
    public IReadOnlyList<AreaNode> Trail(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        if (string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)) return [this];

        foreach (var child in Children)
        {
            if (child.Trail(path) is { Count: > 0 } found) return [this, .. found];
        }

        return [];
    }
}
