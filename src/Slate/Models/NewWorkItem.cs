namespace Slate.Models;

/// <summary>
/// A work item about to be raised. Only the project, type and title are needed; the rest is
/// there so an item can be filled in properly without a trip to the browser.
/// </summary>
public sealed class NewWorkItem
{
    public string Project { get; set; } = "";
    public string WorkItemType { get; set; } = "Task";
    public string Title { get; set; } = "";

    /// <summary>Already converted to HTML - the form decides whether it was typed as Markdown.</summary>
    public string DescriptionHtml { get; set; } = "";

    public string AssignedTo { get; set; } = "";
    public string AreaPath { get; set; } = "";
    public string IterationPath { get; set; } = "";
    public string Tags { get; set; } = "";

    /// <summary>Azure DevOps' own priority, 1 to 4. Zero leaves the field at its default.</summary>
    public int Priority { get; set; }

    public double? EstimateHours { get; set; }

    /// <summary>Set to raise this underneath an existing work item.</summary>
    public int? ParentId { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(WorkItemType);
}
