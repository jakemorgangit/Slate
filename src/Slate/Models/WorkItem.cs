namespace Slate.Models;

/// <summary>A flattened Azure DevOps work item - only the fields the planner actually renders.</summary>
public sealed record WorkItem
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string WorkItemType { get; init; } = "";
    public string State { get; init; } = "";
    public string AssignedTo { get; init; } = "";
    public string AreaPath { get; init; } = "";
    public string IterationPath { get; init; } = "";
    public string Project { get; init; } = "";
    /// <summary>Azure DevOps' own priority field. Zero when the work item does not carry one.</summary>
    public int Priority { get; init; }
    public string[] Tags { get; init; } = [];
    public string Url { get; init; } = "";
    public DateTimeOffset? ChangedDate { get; init; }
    public DateTimeOffset? CreatedDate { get; init; }

    /// <summary>Whole days since the work item was raised, or null if that is not known.</summary>
    public int? AgeDays =>
        CreatedDate is { } created ? Math.Max(0, (int)(DateTimeOffset.Now - created).TotalDays) : null;

    public double? RemainingWork { get; init; }
    public double? CompletedWork { get; init; }
    public double? OriginalEstimate { get; init; }
    public double? StoryPoints { get; init; }

    public string ParentTitle { get; init; } = "";
    public int? ParentId { get; init; }

    /// <summary>
    /// True when this item carries the scheduling fields time can be booked against.
    /// The batch API omits empty fields, so the work item type is used as a backstop.
    /// </summary>
    public bool TracksTime { get; init; }

    /// <summary>Other work items this one is linked to: parent, children, related and so on.</summary>
    public IReadOnlyList<WorkItemLink> Links { get; init; } = [];

    public bool HasLinks => Links.Count > 0;

    /// <summary>Estimate in minutes, from Remaining Work then Original Estimate (both are hours in ADO).</summary>
    public int? EstimateMinutes =>
        RemainingWork is > 0 ? (int)Math.Round(RemainingWork.Value * 60)
        : OriginalEstimate is > 0 ? (int)Math.Round(OriginalEstimate.Value * 60)
        : null;
}

/// <summary>A link from one work item to another.</summary>
public sealed record WorkItemLink(string Kind, int WorkItemId, string Title, string WorkItemType)
{
    /// <summary>Parents and children first, then everything else, then by id.</summary>
    public int SortOrder => Kind switch
    {
        "Parent" => 0,
        "Child" => 1,
        "Related" => 2,
        _ => 3,
    };
}

/// <summary>A saved query from the Azure DevOps "Queries" hub.</summary>
public sealed record AdoQuery(string Id, string Name, string Path, bool IsFolder);

public sealed record AdoProject(string Id, string Name);

/// <summary>A rich-text field such as repro steps or acceptance criteria.</summary>
public sealed record WorkItemRichField(string Name, string DisplayName, string Html);

/// <summary>Everything about one work item, fetched on demand for the details modal.</summary>
public sealed record WorkItemDetail(
    int Id,
    int Rev,
    string Title,
    string WorkItemType,
    string State,
    string Reason,
    string AssignedTo,
    string CreatedBy,
    string Project,
    string AreaPath,
    string IterationPath,
    string[] Tags,
    string Url,
    string DescriptionHtml,
    IReadOnlyList<WorkItemRichField> RichFields,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? ChangedDate,
    IReadOnlyList<WorkItemFieldValue> Fields,
    IReadOnlyList<WorkItemRelation> Relations);

public sealed record WorkItemFieldValue(string Name, string DisplayName, string Value);

public sealed record WorkItemRelation(string Kind, string Title, string Url, int? WorkItemId);

/// <summary>Outcome of writing time back to Azure DevOps.</summary>
/// <summary>
/// What a work item's time fields now read, and what was actually applied to get there -
/// which is not always what was asked for, because both fields clamp at zero.
/// </summary>
public sealed record TimeRecordResult(
    double CompletedWork,
    double RemainingWork,
    double AppliedCompleted = 0,
    double AppliedRemaining = 0);

/// <summary>One entry in a work item's discussion.</summary>
public sealed record WorkItemComment(
    int Id,
    string Author,
    string Html,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? ModifiedDate)
{
    public bool WasEdited => ModifiedDate is not null && CreatedDate is not null
                             && ModifiedDate.Value - CreatedDate.Value > TimeSpan.FromSeconds(5);

    /// <summary>Initials for the little avatar bubble.</summary>
    public string Initials
    {
        get
        {
            var parts = Author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            return parts.Length == 1
                ? parts[0][..1].ToUpperInvariant()
                : (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
        }
    }
}
