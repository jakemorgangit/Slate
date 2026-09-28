namespace Slate.Models;

/// <summary>
/// One value read off the work item being migrated, kept together with how it reads on screen
/// so the wizard can show exactly what the create will carry before anything is written.
/// <see cref="Value"/> is what goes into the JSON patch: a string, a number, or an identity
/// reduced to the one piece of text Azure DevOps can resolve.
///
/// <see cref="IsIdentity"/> is kept because a person is the field most likely to be refused by a
/// project they have no access to, and the only one worth dropping on its own to save the rest.
/// </summary>
public sealed record MigrationFieldValue(
    string Name, string Label, object? Value, string Display, bool IsIdentity = false);

/// <summary>
/// The work item a migration raised, and the fields the target project would not take. Azure
/// DevOps turning a field down is not a reason to lose the copy, but it is something the user
/// has to be told, so the names come back rather than being swallowed.
/// </summary>
public sealed record MigrationCreated(WorkItem Item, IReadOnlyList<string> DroppedFields);

/// <summary>
/// Something the source carries that the new work item will not, and the reason. Every one of
/// these is shown in the wizard before the first write, so nothing is quietly left behind.
/// </summary>
public sealed record MigrationSkip(string Label, string Why);

/// <summary>A file attached to the source work item, as its relation describes it.</summary>
public sealed record MigrationAttachment(string Name, string Url, long Size, string Comment)
{
    /// <summary>Roughly how big, for a list the user is about to wait on.</summary>
    public string SizeLabel => Size switch
    {
        <= 0 => "size unknown",
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / (1024.0 * 1024.0):0.#} MB",
    };
}

/// <summary>
/// One entry in the source's discussion, with its markup exactly as Azure DevOps stores it.
/// Deliberately not the sanitised, image-inlined form the work item window renders: what is
/// copied onto the new item has to be the original markup, and the pictures in it stay valid
/// because the source work item is closed rather than deleted.
/// </summary>
public sealed record MigrationComment(int Id, string Author, DateTimeOffset? CreatedDate, string RawHtml);

/// <summary>
/// The work item being migrated, read once and in full before anything is written. Held as raw
/// field values rather than the display shapes the details window uses, so what is copied is a
/// copy rather than a rendering of one.
///
/// <see cref="LooksMigratedTo"/> is the work item a previous migration of this one said it had
/// gone to, read back out of the discussion. It is only ever a warning: a comment is evidence,
/// not a record.
/// </summary>
public sealed record MigrationSource(
    int Id,
    string Title,
    string WorkItemType,
    string State,
    string Project,
    string AreaPath,
    string IterationPath,
    string AssignedTo,
    string Url,
    IReadOnlyList<MigrationFieldValue> Fields,
    IReadOnlyList<MigrationSkip> Skipped,
    int? ParentId,
    IReadOnlyList<int> ChildIds,
    IReadOnlyList<MigrationAttachment> Attachments,
    IReadOnlyList<MigrationComment> Comments,
    int? LooksMigratedTo);

/// <summary>How one step of a migration ended.</summary>
public enum MigrationStepState
{
    /// <summary>Not started.</summary>
    Waiting,

    /// <summary>Under way.</summary>
    Running,

    /// <summary>Finished, and did what it said.</summary>
    Done,

    /// <summary>Tried and failed. <see cref="MigrationStep.Detail"/> says how.</summary>
    Failed,

    /// <summary>There was nothing to do, or it was deliberately not attempted.</summary>
    Skipped,
}

/// <summary>
/// One line in the wizard's running account of a migration. Mutable, and watched by the dialog
/// while the run is going: a step's wording changes as it works through a list of comments or
/// attachments, which is the only progress a copy of unknown length can show.
/// </summary>
public sealed class MigrationStep(string name)
{
    public string Name { get; } = name;
    public MigrationStepState State { get; set; } = MigrationStepState.Waiting;

    /// <summary>What happened, or where it has got to. Empty until there is something to say.</summary>
    public string Detail { get; set; } = "";

    public bool IsTrouble => State == MigrationStepState.Failed;
}
