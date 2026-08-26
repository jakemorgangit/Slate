namespace Slate.Models;

/// <summary>
/// Somebody who can be @-mentioned in a discussion. <paramref name="Id"/> is the Azure DevOps
/// identity GUID; without it a mention still reads correctly but is not a live link, so the
/// people sources that can supply one are preferred.
/// </summary>
public sealed record OrgMember(string DisplayName, string UniqueName, string Id)
{
    public bool CanLink => Guid.TryParse(Id, out _);

    /// <summary>Initials for the little avatar bubble in the picker.</summary>
    public string Initials
    {
        get
        {
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
            };
        }
    }

    public bool Matches(string term) =>
        term.Length == 0
        || DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || UniqueName.Contains(term, StringComparison.OrdinalIgnoreCase);
}
