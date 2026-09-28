using System.Net;
using Slate.Models;
using Slate.Services.AzureDevOps;

namespace Slate.Services.Planning;

/// <summary>Which page of the migration wizard is showing.</summary>
public enum MigrationStage
{
    /// <summary>Choosing the project, area and work item type to migrate into.</summary>
    Target,

    /// <summary>Everything that is about to happen, before the first write.</summary>
    Review,

    /// <summary>The run itself, step by step.</summary>
    Running,

    /// <summary>What was and was not done.</summary>
    Finished,
}

/// <summary>
/// Migrating a work item to another project or area: the wizard's state, and the run itself.
///
/// The order is not negotiable, and is the reason this is not a loop over a list of jobs. The
/// copy is raised first, so nothing else can happen without somewhere to put it. Then the link
/// between the two, so the trail exists before anything slow does; then the parent and children
/// referenced, the discussion, and the attachments - each reported on its own so a single
/// refusal costs only itself. The original is touched last and in one order - the note naming
/// the copy, then the state - because a work item closed with nothing on it to say where the
/// work went is worse than one left open. The calendar blocks, which are local, come after all
/// of it: the plan may safely follow Azure DevOps, never lead it.
///
/// Nothing here is ever tried twice in a way that could raise a second copy, or post the same
/// comment twice. What has been done is written down as it happens, and a run that failed part
/// way picks up from there rather than starting again. Only a rejection - which means nothing
/// happened - is retried; a write that went out and got no answer back is written down as done
/// and said out loud, because a duplicate comment or attachment cannot be told apart afterwards
/// and a missing one can at least be named.
///
/// The original's state is only ever changed when the copy exists and something says where the
/// work went: either the two are linked to each other, or the original's discussion names the
/// copy. One of the two, not both - see <see cref="CloseSourceAsync"/>.
/// </summary>
public sealed class WorkItemMigrator(
    AzureDevOpsClient ado,
    PlannerService planner,
    AppState state,
    ToastService toasts,
    WriteGate writes)
{
    public event Action? Changed;

    public bool IsOpen { get; private set; }
    public MigrationStage Stage { get; private set; } = MigrationStage.Target;

    public int SourceId { get; private set; }
    public MigrationSource? Source { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LoadError { get; private set; }

    // ---------------------------------------------------------------- the target

    public IReadOnlyList<AdoProject> Projects { get; private set; } = [];
    public string TargetProject { get; private set; } = "";
    public AreaNode? AreaTree { get; private set; }
    public string TargetArea { get; private set; } = "";
    public IReadOnlyList<string> TargetTypes { get; private set; } = [];
    public string TargetType { get; private set; } = "";
    public bool IsLoadingTarget { get; private set; }

    /// <summary>Why one of the target dropdowns came back empty, if it did.</summary>
    public string TargetNote { get; private set; } = "";

    /// <summary>What became of the source's iteration, which often has nowhere to go.</summary>
    public string TargetIteration { get; private set; } = "";
    public string IterationNote { get; private set; } = "";

    /// <summary>Said only when the target type's own field list could not be read.</summary>
    public string FieldNote { get; private set; } = "";

    private AreaNode? _iterationTree;
    private HashSet<string> _targetFields = [];

    /// <summary>
    /// True when the source's own type does not exist in the target project, which is the one
    /// case the user has to make a decision about rather than accepting a default.
    /// </summary>
    public bool TypeMissingInTarget =>
        Source is { } source && TargetTypes.Count > 0
        && !TargetTypes.Contains(source.WorkItemType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Migrating a work item to exactly where it already is would raise a duplicate and cancel
    /// the original, which is nobody's intention.
    /// </summary>
    public bool TargetIsWhereItAlreadyIs =>
        Source is { } source
        && string.Equals(TargetProject, source.Project, StringComparison.OrdinalIgnoreCase)
        && string.Equals(TargetArea, source.AreaPath, StringComparison.OrdinalIgnoreCase);

    public bool CanReview =>
        Source is not null && !IsLoading && !IsLoadingTarget
        && TargetProject.Length > 0 && TargetType.Length > 0 && !TargetIsWhereItAlreadyIs;

    // ---------------------------------------------------------------- what travels

    /// <summary>Exactly the fields the create will carry - the list the review page shows.</summary>
    public IReadOnlyList<MigrationFieldValue> Carrying { get; private set; } = [];

    /// <summary>Everything the copy will not have, with the reason for each.</summary>
    public IReadOnlyList<MigrationSkip> LeavingBehind { get; private set; } = [];

    // ---------------------------------------------------------------- closing the original

    public IReadOnlyList<WorkItemStateOption> ClosingStates { get; private set; } = [];

    /// <summary>
    /// The state the original is moved to. Empty means leaving it exactly as it is, which is
    /// offered because the process decides what "cancelled" is called and some do not have one.
    /// </summary>
    public string ClosingState { get; private set; } = "";

    public string ClosingStatesNote { get; private set; } = "";

    // ---------------------------------------------------------------- the plan's blocks

    public IReadOnlyList<Allocation> Blocks { get; private set; } = [];

    /// <summary>The blocks to point at the copy. Every one of them, until the user says otherwise.</summary>
    public HashSet<Guid> Repointing { get; } = [];

    public void ToggleBlock(Guid id, bool on)
    {
        if (on) Repointing.Add(id);
        else Repointing.Remove(id);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- acknowledgements

    /// <summary>
    /// Set by the user when Slate could not read the source's discussion and they have looked at
    /// it themselves. Without it the original is left open: closing a work item whose discussion
    /// may hold comments the copy never got is exactly what must not happen by default, and
    /// refusing outright would leave nobody on a server without the comments endpoint able to
    /// migrate anything at all.
    /// </summary>
    public bool DiscussionAcknowledged { get; private set; }

    public void AcknowledgeDiscussion(bool acknowledged)
    {
        DiscussionAcknowledged = acknowledged;
        Changed?.Invoke();
    }

    /// <summary>
    /// True when the only thing standing between a migration and a closed original is somebody
    /// saying they have read the discussion Slate could not. Shown on the last page as well as
    /// the review page: a run that has already raised its copy never goes back to the review
    /// page, and without this the original could not be closed from here at all.
    /// </summary>
    public bool AwaitingDiscussionAcknowledgement =>
        Source?.DiscussionError is not null
        && _run is { Created: not null, DiscussionAcknowledged: false, SourceClosed: false }
        && _run.ClosingState.Length > 0;

    /// <summary>
    /// Set by the user when a create went unanswered and they have looked in the target project
    /// and found no copy. <see cref="Restart"/> will not go back to the first page without it,
    /// because starting again over a copy that was in fact raised makes two.
    /// </summary>
    public bool RestartAcknowledged { get; private set; }

    public void AcknowledgeRestart(bool acknowledged)
    {
        RestartAcknowledged = acknowledged;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- the run

    /// <summary>
    /// What a migration has actually done. Kept for as long as Slate is running, and keyed by
    /// the work item it was a migration of, so re-opening the wizard on an item whose copy has
    /// already been raised finishes that migration instead of starting a second one.
    /// </summary>
    private sealed class RunRecord(string project, string workItemType)
    {
        public string Project { get; } = project;
        public string WorkItemType { get; } = workItemType;

        /// <summary>
        /// What the user chose on the review page, kept here because the run acts on the choices
        /// it started with. Finishing the rest of a migration goes straight to the last page,
        /// where neither control is on screen, so re-deriving them would quietly cancel a work
        /// item somebody said to leave open and repoint blocks they had unticked.
        /// </summary>
        public string ClosingState { get; init; } = "";

        public HashSet<Guid> RepointIds { get; init; } = [];

        /// <summary>
        /// Whether the user took responsibility for a discussion Slate could not read. The one
        /// choice a resume may still change, because it is about what they have done outside
        /// Slate since - and because a discussion that will never read would otherwise leave the
        /// original unclosable from here for good.
        /// </summary>
        public bool DiscussionAcknowledged { get; set; }

        public WorkItem? Created { get; set; }

        /// <summary>
        /// True when the create went out and no answer came back that says whether it landed - a
        /// timeout, a dropped connection, a 5xx, an answer that could not be read, or the user
        /// cancelling once the request was already on its way. There may be a copy in the target
        /// project and there may not, so this record is kept rather than dropped: it is the only
        /// thing standing between the next attempt and a second copy.
        /// </summary>
        public bool CreateInDoubt { get; set; }

        public bool LinkedToSource { get; set; }
        public bool SourceNoted { get; set; }
        public bool SourceClosed { get; set; }
        public bool BlocksRepointed { get; set; }

        public HashSet<int> Referenced { get; } = [];
        public HashSet<int> CommentsCopied { get; } = [];
        public HashSet<string> FilesCopied { get; } = [];

        public MigrationStep Create { get; } = new("Raise the copy");
        public MigrationStep Link { get; } = new("Link the two work items");
        public MigrationStep Tree { get; } = new("Reference the parent and children");
        public MigrationStep Discussion { get; } = new("Copy the discussion");
        public MigrationStep Files { get; } = new("Copy the attachments");
        public MigrationStep Note { get; } = new("Note the migration on the original");
        public MigrationStep Closing { get; } = new("Close the original");
        public MigrationStep Blocks { get; } = new("Point the calendar blocks at the copy");

        public IReadOnlyList<MigrationStep> Steps =>
            [Create, Link, Tree, Discussion, Files, Note, Closing, Blocks];
    }

    private readonly Dictionary<int, RunRecord> _runs = [];
    private RunRecord? _run;
    private CancellationTokenSource? _cancel;

    public bool IsRunning { get; private set; }
    public bool WasCancelled { get; private set; }

    /// <summary>Set when a migration could not even start, rather than a step failing inside one.</summary>
    public string? RunError { get; private set; }

    public IReadOnlyList<MigrationStep> Steps => _run?.Steps ?? [];

    /// <summary>The copy, once it exists. Named everywhere a run is reported, so it is never lost.</summary>
    public int? NewItemId => _run?.Created?.Id;
    public string NewItemUrl => _run?.Created?.Url ?? "";

    /// <summary>
    /// True when a copy has already been raised for this work item, so the wizard is finishing
    /// a migration rather than offering a fresh one.
    /// </summary>
    public bool IsResuming => _run?.Created is not null;

    /// <summary>
    /// True when the copy may exist and Slate cannot say: the create went out unanswered, or was
    /// cancelled after the request had gone. Not the same as a failure, and the one state the
    /// last page must never describe as "nothing at all was changed".
    /// </summary>
    public bool CopyMayExist => _run is { Created: null, CreateInDoubt: true };

    /// <summary>Whether anything at all went wrong, which decides how the last page reads.</summary>
    public bool AnythingFailed => Steps.Any(s => s.IsTrouble);

    /// <summary>
    /// True once every step has been tried and come to an answer. A run stopped part way leaves
    /// the steps it never reached Waiting, and it is those rather than a flag that say the
    /// migration is unfinished - which is what makes it still say so after the wizard has been
    /// closed and opened again on the same work item, where a cancellation flag has been forgotten.
    /// </summary>
    public bool EveryStepSettled =>
        Steps.Count > 0 && Steps.All(s => s.State is not (MigrationStepState.Waiting or MigrationStepState.Running));

    /// <summary>
    /// True when the migration has nothing left to do: every step reached an answer and none of
    /// them is trouble. What the last page's "has been migrated" banner rests on, and the absence
    /// of <c>Finish the rest</c> with it.
    /// </summary>
    public bool IsFinished => EveryStepSettled && !AnythingFailed;

    /// <summary>
    /// The copy a previous migration of this work item raised, from this session's own record
    /// where there is one and otherwise from the note left on the discussion. The second is
    /// evidence rather than a record, which is why the wizard says "looks as though".
    /// </summary>
    public int? AlreadyCopiedTo => _run?.Created?.Id ?? Source?.LooksMigratedTo;

    // ---------------------------------------------------------------- opening

    /// <summary>
    /// Opens the wizard on a work item, reading it and the target project's options up front so
    /// the first page can show what will happen rather than a spinner over an empty form.
    /// </summary>
    public async Task BeginAsync(int workItemId)
    {
        // Migrating raises a work item and edits another, neither of which Basic mode does.
        if (!state.CanCreateWorkItems) return;

        // A run under way holds a write count and a half-written migration; opening another
        // over the top of it would lose the only account of what had been done.
        if (IsRunning) return;

        IsOpen = true;
        state.MigrationOpen = true;
        SourceId = workItemId;
        Source = null;
        LoadError = null;
        RunError = null;
        WasCancelled = false;
        Stage = MigrationStage.Target;
        Carrying = [];
        LeavingBehind = [];
        _targetFields = [];
        FieldNote = "";
        DiscussionAcknowledged = false;
        RestartAcknowledged = false;
        IsLoading = true;

        // Picked up rather than started afresh: a copy this session already raised must never be
        // raised a second time, and neither must one that may have been raised without Slate
        // hearing so, so the wizard opens on the account of that run. A record with nothing in
        // it either way is nothing to resume, and is dropped so the target chosen this time is
        // the one the run reports.
        _run = _runs.TryGetValue(workItemId, out var existing)
               && (existing.Created is not null || existing.CreateInDoubt)
            ? existing
            : null;

        if (_run is not null) Stage = MigrationStage.Finished;

        Changed?.Invoke();

        try
        {
            Source = await ado.GetMigrationSourceAsync(workItemId);

            Blocks = [.. planner.Allocations.Where(a => a.WorkItemId == workItemId).OrderBy(a => a.Start)];
            Repointing.Clear();

            if (_run is { } resumed)
            {
                // Resuming: the target is whatever the copy was actually raised in, and the
                // closing state and the ticked blocks are the ones chosen when the run started.
                // Neither control is on screen from here, so anything re-derived now would be a
                // choice nobody made - see RunRecord.ClosingState.
                TargetProject = resumed.Project;
                TargetType = resumed.WorkItemType;
                ClosingState = resumed.ClosingState;
                DiscussionAcknowledged = resumed.DiscussionAcknowledged;
                foreach (var id in resumed.RepointIds) Repointing.Add(id);
            }
            else
            {
                foreach (var block in Blocks) Repointing.Add(block.Id);

                await LoadProjectsAsync();
                await ChooseProjectAsync(Source.Project);
            }

            await LoadClosingStatesAsync(Source, resuming: _run is not null);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Shuts the wizard. Refused mid-run: Cancel is the way out of one of those.</summary>
    public void Close()
    {
        if (IsRunning) return;

        IsOpen = false;
        state.MigrationOpen = false;
        Changed?.Invoke();
    }

    public void Back()
    {
        if (Stage == MigrationStage.Review) Stage = MigrationStage.Target;
        Changed?.Invoke();
    }

    /// <summary>
    /// Back to the first page after a run that raised nothing, so the target can be changed and
    /// the migration tried again. Only ever offered when there is no copy: a run that raised one
    /// is resumed, never restarted, because restarting it would raise a second.
    ///
    /// A create left in doubt is the case in between, and it is refused until the user has said
    /// they looked in the target project and found nothing. Slate cannot look for them: the
    /// create is the one step that leaves no mark on the original, so there is nothing on it to
    /// read back, and the copy - if there is one - is a work item nobody has the id of.
    /// </summary>
    public void Restart()
    {
        if (IsRunning || _run?.Created is not null) return;
        if (CopyMayExist && !RestartAcknowledged) return;

        _runs.Remove(SourceId);
        _run = null;
        RunError = null;
        WasCancelled = false;
        RestartAcknowledged = false;
        Stage = MigrationStage.Target;
        Changed?.Invoke();
    }

    public void Review()
    {
        if (!CanReview) return;
        Stage = MigrationStage.Review;
        Changed?.Invoke();
    }

    /// <summary>Why the project dropdown came back empty. Kept apart from <see cref="TargetNote"/>,
    /// which is re-derived every time a project is chosen and would otherwise wipe it.</summary>
    public string ProjectsNote { get; private set; } = "";

    private async Task LoadProjectsAsync()
    {
        try
        {
            Projects = await ado.GetProjectsAsync();
            ProjectsNote = "";
        }
        catch (Exception ex)
        {
            Projects = [];
            ProjectsNote = "Could not list the projects (" + ex.Message + "). Type the name in instead.";
        }
    }

    // ---------------------------------------------------------------- choosing the target

    /// <summary>
    /// Counts the calls that read the target's options, so only the newest one's answers are
    /// written down. Two quick changes of project leave two of these in flight over four reads
    /// each, and the slower, earlier one finishing last would leave the types, the area, the
    /// iteration and the field list describing one project while <see cref="TargetProject"/>
    /// names another - and <see cref="CanReview"/> true over the pair of them.
    /// </summary>
    private int _targetRequest;

    /// <summary>
    /// Points the wizard at a project and reads everything that depends on it. The area, the
    /// iteration and the type are all re-derived: a path or a type from the old project means
    /// nothing in the new one.
    /// </summary>
    public async Task ChooseProjectAsync(string project)
    {
        if (Source is not { } source) return;

        var request = ++_targetRequest;
        var wanted = project?.Trim() ?? "";

        TargetProject = wanted;
        IsLoadingTarget = true;
        TargetNote = "";
        Changed?.Invoke();

        var trouble = new List<string>();

        // Read into locals and written down in one go below, so a stale answer cannot leave half
        // of this describing one project and half another.
        IReadOnlyList<string> types = [];
        AreaNode? areas = null;
        AreaNode? iterations = null;

        try
        {
            types = await ado.GetWorkItemTypesAsync(wanted);
        }
        catch (Exception ex)
        {
            trouble.Add("work item types (" + ex.Message + ")");
        }

        try
        {
            areas = await ado.GetAreaTreeAsync(wanted);
        }
        catch (Exception ex)
        {
            trouble.Add("area paths (" + ex.Message + ")");
        }

        try
        {
            iterations = await ado.GetIterationTreeAsync(wanted);
        }
        catch (Exception ex)
        {
            trouble.Add("iterations (" + ex.Message + ")");
        }

        if (request != _targetRequest) return;

        TargetTypes = types;
        AreaTree = areas;
        _iterationTree = iterations;

        TargetNote = trouble.Count == 0
            ? ""
            : "Could not read " + string.Join(" or ", trouble) + ". Type it in by hand instead.";

        // The same area under the new project where one exists, otherwise the project root -
        // which is what Azure DevOps would use anyway, and is at least a path that is real.
        TargetArea = SamePathUnder(source.AreaPath, source.Project, TargetProject, AreaTree)
                     ?? AreaTree?.Path
                     ?? TargetProject;

        MapIteration();

        // Default to the type it already is; where that does not exist, nothing is chosen for
        // the user, because guessing which type a Bug becomes is not Slate's decision to make.
        TargetType = TargetTypes.Count == 0
            ? source.WorkItemType
            : TargetTypes.FirstOrDefault(t =>
                string.Equals(t, source.WorkItemType, StringComparison.OrdinalIgnoreCase)) ?? "";

        await LoadTargetFieldsAsync(request);
        FinishLoadingTarget(request);
    }

    public void ChooseArea(string area)
    {
        TargetArea = area ?? "";
        RecomputePlan();
        Changed?.Invoke();
    }

    public async Task ChooseTypeAsync(string workItemType)
    {
        var request = ++_targetRequest;

        TargetType = workItemType?.Trim() ?? "";
        IsLoadingTarget = true;
        Changed?.Invoke();

        await LoadTargetFieldsAsync(request);
        FinishLoadingTarget(request);
    }

    /// <summary>
    /// Clears the loading flag, and only for the call that is still the current one. Done here
    /// and not before the field list has landed: <see cref="CanReview"/> goes true the moment
    /// this clears, and the review page's whole promise is that the list of fields it shows is
    /// the list that will be sent.
    /// </summary>
    private void FinishLoadingTarget(int request)
    {
        if (request != _targetRequest) return;

        IsLoadingTarget = false;
        Changed?.Invoke();
    }

    public void ChooseClosingState(string state)
    {
        ClosingState = state ?? "";
        Changed?.Invoke();
    }

    /// <summary>
    /// The same path under another project: "Old\Platform\Api" becomes "New\Platform\Api" when
    /// the new project really has that branch, and nothing at all when it does not. Returning
    /// null rather than the guess is the point - a path Azure DevOps does not know is refused
    /// outright, and losing the whole create to it would be a poor trade for an area.
    /// </summary>
    private static string? SamePathUnder(string path, string fromProject, string toProject, AreaNode? tree)
    {
        if (tree is null || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(toProject)) return null;

        var tail = path.StartsWith(fromProject + "\\", StringComparison.OrdinalIgnoreCase)
            ? path[(fromProject.Length + 1)..]
            : string.Equals(path, fromProject, StringComparison.OrdinalIgnoreCase)
                ? ""
                : path;

        var candidate = tail.Length == 0 ? toProject : toProject + "\\" + tail;
        return tree.Trail(candidate).Count > 0 ? candidate : null;
    }

    private void MapIteration()
    {
        if (Source is not { } source) return;

        if (string.IsNullOrWhiteSpace(source.IterationPath))
        {
            TargetIteration = "";
            IterationNote = "";
            return;
        }

        if (_iterationTree is null)
        {
            TargetIteration = "";
            IterationNote =
                $"The iterations in {TargetProject} could not be read, so the copy goes to that project's default iteration.";
            return;
        }

        if (SamePathUnder(source.IterationPath, source.Project, TargetProject, _iterationTree) is { } mapped)
        {
            TargetIteration = mapped;
            IterationNote = "";
            return;
        }

        TargetIteration = "";
        IterationNote =
            $"{TargetProject} has no iteration matching {source.IterationPath}, so the copy goes to that project's default iteration.";
    }

    /// <summary>
    /// Asks the target project which fields its chosen type actually has, which is what lets the
    /// review page name the fields that have nowhere to go before anything is written.
    /// </summary>
    private async Task LoadTargetFieldsAsync(int request)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var note = "";
        var project = TargetProject;
        var type = TargetType;

        if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(project))
        {
            try
            {
                fields = await ado.GetWorkItemTypeFieldsAsync(project, type);
            }
            catch (Exception ex)
            {
                // Not fatal. Without the list every field is sent and Azure DevOps decides,
                // which is what the create's narrowing retries are there for.
                note =
                    $"Slate could not read which fields a {type} has in {project} ({ex.Message}), " +
                    "so every field will be sent and any Azure DevOps refuses will be dropped.";
            }
        }

        // The answer describes a project and type nobody is looking at any more. Writing it down
        // would leave the field list and TargetProject disagreeing, which is the one thing the
        // review page cannot afford.
        if (request != _targetRequest) return;

        _targetFields = fields;
        FieldNote = note;

        RecomputePlan();
        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the states the source's own type allows, for the choice of what the original is
    /// moved to. <paramref name="resuming"/> leaves the choice alone: a migration being finished
    /// acts on the state chosen when it started, and the control is not on screen to change it.
    /// </summary>
    private async Task LoadClosingStatesAsync(MigrationSource source, bool resuming)
    {
        try
        {
            ClosingStates = await ado.GetStatesAsync(source.Project, source.WorkItemType);
            ClosingStatesNote = "";
        }
        catch (Exception ex)
        {
            ClosingStates = [];
            ClosingStatesNote = "Could not read the states this type allows (" + ex.Message + ").";
        }

        if (resuming) return;

        // Agile and Scrum call it Removed, CMMI calls it Cancelled, and a custom process calls
        // it whatever it likes - so the category is what is gone by, and the name is the
        // process's own. Nothing is assumed when there is no such category: an empty choice
        // leaves the original open, which is the only safe default.
        ClosingState = ClosingStates.FirstOrDefault(s => s.Category == "Removed")?.Name ?? "";
    }

    /// <summary>
    /// Works out exactly what will and will not go onto the copy, from the source in hand and
    /// the target chosen. Called whenever either changes, because the answer depends on both.
    /// </summary>
    private void RecomputePlan()
    {
        if (Source is not { } source)
        {
            Carrying = [];
            LeavingBehind = [];
            return;
        }

        var carrying = new List<MigrationFieldValue>();
        var leaving = new List<MigrationSkip>
        {
            new("The revision history",
                "Azure DevOps builds a work item's history from the changes made to it, and it cannot be written. "
                + $"#{source.Id} keeps its own, and stays readable to anybody who can see {source.Project}."),
            new("Time already recorded",
                $"The hours stay booked against #{source.Id}: Slate's own time entries, the Time tab and Undo all "
                + $"still point there. Completed Work does come over as a number - the copy should say the work was "
                + $"done - so both work items state those hours, and a report adding the two together would count "
                + $"them twice."),
            new("Parent and child links",
                "A parent link cannot cross a project, which is why the parent and each child are referenced as Related "
                + "instead. The originals keep their own hierarchy."),
            new("Build, pull request and other artefact links",
                "Those point at work that was done on the original and belong to its history."),
        };

        foreach (var field in source.Fields)
        {
            // An empty set means the question could not be asked, so everything is sent and
            // Azure DevOps decides - see FieldNote.
            if (_targetFields.Count > 0 && !_targetFields.Contains(field.Name))
            {
                leaving.Add(new MigrationSkip($"{field.Label} — {field.Display}",
                    $"a {TargetType} in {TargetProject} has no such field"));
                continue;
            }

            carrying.Add(field);
        }

        leaving.AddRange(source.Skipped);

        if (!string.IsNullOrWhiteSpace(TargetArea))
            carrying.Add(new MigrationFieldValue("System.AreaPath", "Area Path", TargetArea, TargetArea));

        if (!string.IsNullOrWhiteSpace(TargetIteration))
            carrying.Add(new MigrationFieldValue(
                "System.IterationPath", "Iteration Path", TargetIteration, TargetIteration));

        Carrying = carrying;
        LeavingBehind = leaving;
    }

    // ---------------------------------------------------------------- running

    public void Cancel() => _cancel?.Cancel();

    /// <summary>
    /// Does the migration, or finishes one that stopped part way. Held open as one counted write
    /// from the first call to the last: an update that took over halfway through would leave a
    /// copy nobody linked to and an original nobody closed, and the new copy of Slate would have
    /// no idea either had happened. A migration slower than the handover's patience abandons the
    /// update rather than being cut off, which is the trade that gate exists to make.
    /// </summary>
    public async Task RunAsync()
    {
        if (Source is not { } source || IsRunning) return;
        if (string.IsNullOrWhiteSpace(TargetProject) || string.IsNullOrWhiteSpace(TargetType)) return;

        // A create nobody got an answer to is not something to run over the top of: there may
        // already be a copy, and this would make a second. Restart is the only way past it, and
        // only once the user has said they looked.
        if (CopyMayExist) return;

        if (!writes.TryEnter())
        {
            RunError = AppState.RestartingForUpdate;
            toasts.Error("Could not migrate that work item", AppState.RestartingForUpdate);
            Changed?.Invoke();
            return;
        }

        // Everything from here is inside the try, so the count taken above is always given back:
        // a subscriber to Changed that throws would otherwise skip the finally, leave the count
        // stuck above zero - and with it an update that can never take over - and leave IsRunning
        // true, which blocks the wizard for the rest of the session. Same reason as
        // AppState.SpawnTaskAsync.
        try
        {
            // A record with no copy in it is not something to resume - the target may have been
            // changed since - and its account belongs to that attempt, not this one.
            if (_run is { Created: null }) _run = null;

            // The choices go on the record as the run starts, and the run reads them from there
            // rather than from the wizard: finishing the rest goes straight to the last page,
            // where neither control is shown.
            _run ??= new RunRecord(TargetProject, TargetType)
            {
                ClosingState = ClosingState,
                RepointIds = [.. Repointing],
            };

            // The one of them a resume may add to, never take back: see
            // RunRecord.DiscussionAcknowledged.
            _run.DiscussionAcknowledged |= DiscussionAcknowledged;

            _runs[source.Id] = _run;
            var run = _run;

            _cancel = new CancellationTokenSource();
            var ct = _cancel.Token;

            IsRunning = true;
            WasCancelled = false;
            RunError = null;
            Stage = MigrationStage.Running;
            Changed?.Invoke();

            try
            {
                await RaiseCopyAsync(run, source, ct);

                // Nothing else may run without somewhere to put it, and the original must never
                // be closed when there is no copy.
                if (run.Created is not null)
                {
                    await LinkBackAsync(run, source, ct);
                    await ReferenceTreeAsync(run, source, ct);
                    await CopyDiscussionAsync(run, source, ct);
                    await CopyAttachmentsAsync(run, source, ct);
                    await NoteOnSourceAsync(run, source, ct);
                    await CloseSourceAsync(run, source, ct);
                    RepointBlocks(run, source);
                }
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                SettleInterruptedSteps(run,
                    "Stopped when the migration was cancelled. What had already gone over is written down and "
                    + "will not be sent again; Finish the rest carries on from there.");
            }
            catch (Exception ex)
            {
                // Every step already answers for itself, so nothing should reach here. If
                // anything does, it is said out loud beside the steps rather than thrown at a
                // page that would simply go blank - leaving a half-done migration with no
                // account of it.
                RunError = ex.Message;
                SettleInterruptedSteps(run, "Stopped by an unexpected failure; see the message above the steps.");
            }
        }
        finally
        {
            IsRunning = false;
            Stage = MigrationStage.Finished;

            _cancel?.Dispose();
            _cancel = null;

            writes.Exit();
            if (_run is { } ended) Report(ended, source);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// A step that was under way when the run stopped. Whether what it was doing landed is not
    /// known, so it is reported as trouble rather than left with a spinner beside it on a page
    /// that has finished - a spinner under the heading "Migrated" is the one thing that page must
    /// never show. The steps it never reached stay Waiting, which is what
    /// <see cref="EveryStepSettled"/> reads to know the migration is unfinished.
    /// </summary>
    private static void SettleInterruptedSteps(RunRecord run, string why)
    {
        foreach (var step in run.Steps.Where(s => s.State == MigrationStepState.Running))
        {
            step.State = MigrationStepState.Failed;
            step.Detail = string.IsNullOrWhiteSpace(step.Detail) ? why : step.Detail.TrimEnd() + " " + why;
        }
    }

    private void Report(RunRecord run, MigrationSource source)
    {
        if (run.Created is not { } created)
        {
            // Said even when the user was the one who cancelled: cancelling after the request had
            // gone out is exactly as uncertain as a timeout, and this is the one outcome that
            // must never pass in silence.
            if (run.CreateInDoubt)
                toasts.Error($"#{source.Id} may already have been copied",
                    $"Azure DevOps did not say whether the copy was raised in {run.Project}. Look there before "
                    + "migrating it again - another attempt could make a second copy.");
            else if (!WasCancelled)
                toasts.Error($"Could not migrate #{source.Id}", run.Create.Detail);

            return;
        }

        if (!EveryStepSettled)
        {
            toasts.Error($"Migration of #{source.Id} stopped part way",
                $"#{created.Id} was raised in {run.Project} and is still there. The wizard lists what was and was "
                + "not done; Finish the rest carries on from there.");
            return;
        }

        if (AnythingFailed)
        {
            toasts.Error($"#{created.Id} was raised, but the migration did not finish",
                "The wizard lists exactly what was and was not done.");
            return;
        }

        toasts.Success($"Migrated #{source.Id} to #{created.Id}",
            $"{created.WorkItemType} in {(string.IsNullOrWhiteSpace(created.AreaPath) ? run.Project : created.AreaPath)}.");
    }

    /// <summary>
    /// Puts a work item Azure DevOps has just confirmed into the loaded list, and says so if it
    /// could not. Guarded rather than thrown: the plan is a file like any other and a scanner or
    /// a backup can hold it open, and by the time this runs the change in Azure DevOps has already
    /// happened - a local save that failed must never be allowed to report it as not having.
    /// Empty when it worked, so the caller can append it to a step's detail either way.
    /// </summary>
    private string Adopt(WorkItem item)
    {
        try
        {
            state.AdoptWorkItem(item);
            return "";
        }
        catch (Exception ex)
        {
            return $" Slate could not update its own copy of the plan ({ex.Message}), so the sidebar and the "
                   + "calendar may be out of date until it is reloaded; Azure DevOps is as this says.";
        }
    }

    private async Task RaiseCopyAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Create;

        if (run.Created is { } already)
        {
            step.State = MigrationStepState.Done;
            step.Detail = $"#{already.Id} was already raised; it is not raised again.";
            return;
        }

        // Before anything is sent, so a cancellation here really does mean nothing happened -
        // which is what lets the catch below treat one from inside the send as the opposite.
        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        try
        {
            var created = await ado.CreateMigratedAsync(run.Project, run.WorkItemType, Carrying, ct);

            // Written down before the local adoption, which is a file write and can be refused.
            run.Created = created.Item;

            step.State = MigrationStepState.Done;
            step.Detail = (created.DroppedFields.Count == 0
                    ? $"#{created.Item.Id} raised as a {run.WorkItemType} in {run.Project}."
                    : $"#{created.Item.Id} raised as a {run.WorkItemType} in {run.Project}, but Azure DevOps would "
                      + "not take " + string.Join(", ", created.DroppedFields)
                      + " — most often an assignee with no access to that project. Fill those in by hand.")
                + Adopt(created.Item);
        }
        catch (AzureDevOpsException ex) when (ex.Unanswered)
        {
            // The one failure that must not be retried: the copy may exist.
            InDoubt(run, step, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Cancelled with the request already on its way - the token is passed into the send,
            // so Azure DevOps may have raised the copy before it was cut off. Exactly the same
            // uncertainty as an unanswered create, and reported the same way.
            InDoubt(run, step, "The migration was cancelled while the copy was being raised.");
            throw;
        }
        catch (Exception ex)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message + $" Nothing was changed on #{source.Id}.";
        }
    }

    /// <summary>
    /// Records a create nobody got an answer to. The wording names the project because looking
    /// in it is the only way anybody can settle it, and the record keeps the doubt so the wizard
    /// will not run over the top of it - see <see cref="RunRecord.CreateInDoubt"/>.
    /// </summary>
    private static void InDoubt(RunRecord run, MigrationStep step, string why)
    {
        run.CreateInDoubt = true;
        step.State = MigrationStepState.Failed;
        step.Detail = why + $" Azure DevOps did not say whether the copy was raised, so look in {run.Project} "
                      + "before running this again — trying again could raise a second one.";
    }

    private async Task LinkBackAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Link;
        if (run.LinkedToSource)
        {
            step.State = MigrationStepState.Done;
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        try
        {
            // Not cancellable once it is on its way. A link half sent would have to be treated as
            // possibly there, and this is the link the already-migrated warning is read back from,
            // so the wizard waits the moment it takes rather than leaving that in doubt.
            await ado.AddRelatedLinkAsync(
                run.Created!.Id, source.Id, AzureDevOpsClient.MigratedFromMarker + source.Id,
                CancellationToken.None);

            run.LinkedToSource = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{run.Created.Id} and #{source.Id} are Related to each other.";
        }
        catch (AzureDevOpsException ex) when (AzureDevOpsClient.IsLinkAlreadyThere(ex))
        {
            // Already there, from a run that got this far before. What was asked for, so it is
            // recorded as done rather than reported - the same tolerance the tree step has.
            run.LinkedToSource = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{run.Created!.Id} and #{source.Id} were already Related to each other.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message + (run.SourceNoted
                ? $" The note on #{source.Id} names #{run.Created!.Id}, so the trail is not lost."
                : "");
        }
    }

    private async Task ReferenceTreeAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Tree;

        var ids = new List<int>();
        if (source.ParentId is int parent) ids.Add(parent);
        ids.AddRange(source.ChildIds);

        if (ids.Count == 0)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = "It has no parent and no children.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        var done = 0;
        var failed = new List<string>();
        var stopped = false;

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            if (run.Referenced.Contains(id))
            {
                done++;
                continue;
            }

            var what = id == source.ParentId ? "parent" : "child";
            step.Detail = $"Referencing {what} #{id} ({done + failed.Count + 1} of {ids.Count})…";
            Changed?.Invoke();

            try
            {
                // Not cancellable once sent: see LinkBackAsync. Cancelling stops between one of
                // these and the next, which is what keeps the ledger of what is done honest.
                await ado.AddRelatedLinkAsync(run.Created!.Id, id,
                    $"{char.ToUpperInvariant(what[0])}{what[1..]} of #{source.Id} before it was migrated",
                    CancellationToken.None);

                run.Referenced.Add(id);
                done++;
            }
            catch (AzureDevOpsException ex) when (AzureDevOpsClient.IsLinkAlreadyThere(ex))
            {
                // A link that is already there, from a run that got this far before. Counted as
                // done rather than reported: it is what was asked for. Every *other* 400 - a
                // target deleted or never there, a rule or a permission refusing it, a relation
                // Azure DevOps would not parse - means the link was not made, and falls through
                // to be reported, so Finish the rest can try it again.
                run.Referenced.Add(id);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"#{id} ({ex.Message})");
            }
        }

        step.State = failed.Count == 0 && !stopped ? MigrationStepState.Done : MigrationStepState.Failed;
        step.Detail = failed.Count == 0
            ? stopped
                ? $"Stopped part way: {done} of {ids.Count} referenced. Finish the rest does the others."
                : $"{done} referenced as Related. None of them was moved or changed otherwise."
            : $"{done} of {ids.Count} referenced. Could not reference " + string.Join("; ", failed) + ".";

        if (stopped) ct.ThrowIfCancellationRequested();
    }

    private async Task CopyDiscussionAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Discussion;

        // A discussion that could not be read is a failure, not an empty discussion. Reported as
        // one so nobody reads "nothing to copy" off a question that was never answered - and so
        // the closing step knows to leave the original alone. See CloseSourceAsync.
        if (source.DiscussionError is { } why)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = $"#{source.Id}'s discussion could not be read, so none of it was copied. {why} "
                          + "Look at it in Azure DevOps and copy anything that matters by hand.";
            return;
        }

        if (source.Comments.Count == 0)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = $"There is nothing in #{source.Id}'s discussion to copy.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        var done = 0;
        var failed = new List<string>();
        var unsure = new List<string>();
        var stopped = false;

        foreach (var comment in source.Comments)
        {
            if (ct.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            if (run.CommentsCopied.Contains(comment.Id))
            {
                done++;
                continue;
            }

            step.Detail = $"Copying comment {done + failed.Count + unsure.Count + 1} of {source.Comments.Count}…";
            Changed?.Invoke();

            try
            {
                // Not cancellable once sent. A comment cut off in flight may be on the copy, and
                // a second one posted on a resume could never be told from the first or taken
                // back, so the wizard waits the moment it takes and stops before the next one.
                await ado.AddCommentAsync(
                    run.Created!.Id, run.Project, QuotedComment(source, comment), CancellationToken.None);

                run.CommentsCopied.Add(comment.Id);
                done++;
            }
            catch (AzureDevOpsException ex) when (ex.Unanswered)
            {
                // It may be on the copy already. Written down as copied so finishing the rest
                // does not post it a second time: a duplicate comment cannot be told from the
                // original afterwards, while one that is missing is named right here.
                run.CommentsCopied.Add(comment.Id);
                unsure.Add($"the one from {comment.Author} ({ex.Message})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"the one from {comment.Author} ({ex.Message})");
            }
        }

        step.State = failed.Count == 0 && unsure.Count == 0 && !stopped
            ? MigrationStepState.Done
            : MigrationStepState.Failed;

        step.Detail =
            (stopped
                ? $"Stopped part way: {done} of {source.Comments.Count} copied. Finish the rest does the others. "
                : $"{done} of {source.Comments.Count} copied, each one naming who wrote it and when. "
                  + "Azure DevOps records you as the author. ")
            + (failed.Count == 0 ? "" : "Could not copy " + string.Join("; ", failed) + ". ")
            + (unsure.Count == 0
                ? ""
                : "Azure DevOps did not say whether it took " + string.Join("; ", unsure)
                  + ", so they are not sent again — look at the copy's discussion and add anything missing. ")
            + (source.CommentsNotCarried == 0
                ? ""
                : $"{source.CommentsNotCarried} more are on #{source.Id} than Slate reads in one pass and were "
                  + "not copied.");

        step.Detail = step.Detail.TrimEnd();

        if (stopped) ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// A copied comment with the original author and date written into it. Azure DevOps has no
    /// way to post as somebody else, so without this the whole discussion would read as having
    /// been written by whoever ran the migration, today.
    /// </summary>
    private static string QuotedComment(MigrationSource source, MigrationComment comment)
    {
        var author = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(comment.Author) ? "an author Azure DevOps no longer names" : comment.Author);

        var when = comment.CreatedDate is { } date
            ? date.ToLocalTime().ToString("d MMM yyyy, HH:mm")
            : "a date Azure DevOps no longer records";

        return $"<p><i>From the discussion on #{source.Id} — <b>{author}</b>, {when}:</i></p><hr>{comment.RawHtml}";
    }

    private async Task CopyAttachmentsAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Files;

        if (source.Attachments.Count == 0)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = "There are no attachments to copy.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        var done = 0;
        var failed = new List<string>();
        var unsure = new List<string>();
        var stopped = false;

        foreach (var file in source.Attachments)
        {
            if (ct.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            if (run.FilesCopied.Contains(file.Url))
            {
                done++;
                continue;
            }

            var position = done + failed.Count + unsure.Count + 1;
            step.Detail = $"Fetching {file.Name} ({file.SizeLabel}) — {position} of {source.Attachments.Count}…";
            Changed?.Invoke();

            try
            {
                // The two transfers do take the token: they are the long part of a migration, and
                // neither of them changes a work item - an upload nobody links to is collected by
                // Azure DevOps on its own - so stopping in the middle of either leaves nothing
                // behind and nothing in doubt.
                var bytes = await ado.DownloadAttachmentAsync(file.Url, ct);

                step.Detail = $"Uploading {file.Name} ({file.SizeLabel}) — {position} of {source.Attachments.Count}…";
                Changed?.Invoke();

                var uploaded = await ado.UploadAttachmentAsync(run.Project, file.Name, bytes, ct);

                try
                {
                    // Attaching is the write, and is not cancellable once sent: the same file on
                    // the copy twice is worse than waiting the moment this takes.
                    await ado.AttachFileAsync(run.Created!.Id, uploaded, file.Comment, CancellationToken.None);
                }
                catch (AzureDevOpsException ex) when (ex.Unanswered)
                {
                    // The file may be on the copy already. Written down as copied so finishing
                    // the rest does not attach it a second time - the same file twice on one work
                    // item cannot be told apart, while one that is missing is named right here.
                    //
                    // Only the attach, not the upload above it: an upload nobody got an answer to
                    // leaves no relation on the copy at all, and the bytes nothing links to are
                    // collected by Azure DevOps on its own, so that one is safe to do again and
                    // falls through to be reported as the ordinary failure it is.
                    run.FilesCopied.Add(file.Url);
                    unsure.Add($"{file.Name} ({ex.Message})");
                    continue;
                }

                run.FilesCopied.Add(file.Url);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One file that will not come over is not a reason to lose the rest of it.
                failed.Add($"{file.Name} ({ex.Message})");
            }
            catch (OperationCanceledException)
            {
                // Cancelled inside a transfer, which has left nothing behind.
                stopped = true;
                break;
            }
        }

        step.State = failed.Count == 0 && unsure.Count == 0 && !stopped
            ? MigrationStepState.Done
            : MigrationStepState.Failed;

        step.Detail =
            (stopped
                ? $"Stopped part way: {done} of {source.Attachments.Count} attached to the copy. Finish the rest "
                  + "does the others. "
                : $"{done} of {source.Attachments.Count} attached to the copy. "
                  + $"The originals stay on #{source.Id} as well. ")
            + (failed.Count == 0
                ? ""
                : "Could not copy " + string.Join("; ", failed) + $". They are still on #{source.Id}. ")
            + (unsure.Count == 0
                ? ""
                : "Azure DevOps did not say whether it took " + string.Join("; ", unsure)
                  + ", so they are not sent again — look at the copy's attachments and add anything missing.");

        step.Detail = step.Detail.TrimEnd();

        if (stopped) ct.ThrowIfCancellationRequested();
    }

    private async Task NoteOnSourceAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Note;
        if (run.SourceNoted)
        {
            step.State = MigrationStepState.Done;
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        try
        {
            // Not cancellable once sent: the note is what the closing step reads to know there is
            // a trail, and a note in doubt is a trail in doubt.
            await ado.AddCommentAsync(
                source.Id, source.Project, MigrationNote(run, source), CancellationToken.None);

            run.SourceNoted = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{source.Id} now names #{run.Created!.Id} in its discussion.";

            // Said here rather than by the link step, which runs earlier and could only have
            // guessed that this one would work. It is on the step that failed because that is
            // where somebody reads what the failure cost.
            if (run.Link.State == MigrationStepState.Failed)
                run.Link.Detail = run.Link.Detail.TrimEnd()
                                  + $" The note on #{source.Id} names #{run.Created!.Id}, so the trail is not lost.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message;
        }
    }

    /// <summary>
    /// The note left on the original. It leads with the marker a later migration of the same
    /// work item goes looking for, and names the copy in words as well as in a link, so the
    /// trail survives a link that failed to go on.
    /// </summary>
    private static string MigrationNote(RunRecord run, MigrationSource source)
    {
        var created = run.Created!;
        var where = string.IsNullOrWhiteSpace(created.AreaPath) ? run.Project : created.AreaPath;

        var closing = string.IsNullOrWhiteSpace(run.ClosingState)
            ? "It is being left in its current state."
            : $"It is to be closed as <b>{WebUtility.HtmlEncode(run.ClosingState)}</b> and should not be worked on further.";

        return $"<p><b>{AzureDevOpsClient.MigratedMarker}{created.Id}</b> — this work item was copied to "
               + $"<a href=\"{WebUtility.HtmlEncode(created.Url)}\">#{created.Id}</a> "
               + $"({WebUtility.HtmlEncode(created.WorkItemType)} in {WebUtility.HtmlEncode(where)}) by Slate on "
               + $"{DateTime.Now:d MMM yyyy}. Work carries on there. {closing}</p>"
               + $"<p><i>Hours already recorded against #{source.Id} stay here.</i></p>";
    }

    private async Task CloseSourceAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Closing;

        if (run.SourceClosed)
        {
            step.State = MigrationStepState.Done;
            return;
        }

        if (string.IsNullOrWhiteSpace(run.ClosingState))
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = $"You chose to leave #{source.Id} as it is.";
            return;
        }

        // The one rule that is not about tidiness: an original closed with nothing on it to say
        // where the work went is worse than one left open. One of the two is enough - a Related
        // link, or the note naming the copy - and neither is not.
        if (!run.LinkedToSource && !run.SourceNoted)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = $"Not attempted: neither the link nor the note went on, so nothing on #{source.Id} "
                          + $"would point at #{run.Created!.Id}. Close it by hand once you have looked at both.";
            return;
        }

        // A discussion Slate could not read may hold comments the copy never got, and closing the
        // original on the strength of one is how they would be lost. Refused unless the user said
        // on the review page that they had looked at it themselves.
        if (source.DiscussionError is not null && !run.DiscussionAcknowledged)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = $"Not attempted: #{source.Id}'s discussion could not be read, so Slate cannot say the "
                          + $"copy has all of it. Compare the two and close #{source.Id} by hand.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        try
        {
            // Not cancellable once sent: this is the write that cancels somebody's work item, and
            // one left in doubt would have the wizard unable to say whether it is open or closed.
            var updated = await ado.SetStateAsync(source.Id, run.ClosingState, CancellationToken.None);

            // Written down, and said, before the local adoption: that is a file write and can be
            // refused, and a refusal must not turn "#101 is now Removed" into "#101 is still
            // Active" when Azure DevOps has already moved it.
            run.SourceClosed = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{source.Id} is now {run.ClosingState}."
                          + (updated is null ? "" : Adopt(updated));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message + $" #{source.Id} is still {source.State}; everything else is done.";
        }
    }

    /// <summary>
    /// Points the chosen calendar blocks at the copy. Local only, and last: the plan may safely
    /// follow Azure DevOps, never lead it.
    /// </summary>
    private void RepointBlocks(RunRecord run, MigrationSource source)
    {
        var step = run.Blocks;

        // The blocks ticked when the run started, off the record: finishing the rest never shows
        // that list again, so re-reading the live one would repoint blocks somebody had unticked.
        var chosen = Blocks.Where(b => run.RepointIds.Contains(b.Id)).ToList();

        if (run.BlocksRepointed)
        {
            step.State = MigrationStepState.Done;
            return;
        }

        if (chosen.Count == 0)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = Blocks.Count == 0
                ? "No block in the plan points at this work item."
                : $"You left all {Blocks.Count} block(s) pointing at #{source.Id}.";
            return;
        }

        try
        {
            foreach (var block in chosen) planner.Repoint(block.Id, run.Created!);
        }
        catch (Exception ex)
        {
            // The plan is a file like any other, and a file can refuse to be written. Azure
            // DevOps is already done by here, so this is reported, not thrown.
            step.State = MigrationStepState.Failed;
            step.Detail = $"The plan could not be saved. {ex.Message} #{run.Created!.Id} is still the work item "
                          + "to book against; point the blocks at it by hand.";
            return;
        }

        run.BlocksRepointed = true;
        step.State = MigrationStepState.Done;
        step.Detail = (chosen.Count == 1 ? "1 block now points" : $"{chosen.Count} blocks now point")
                      + $" at #{run.Created!.Id}. Time already recorded stays booked against #{source.Id}.";
    }
}
