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
/// copy is raised first, so nothing else can happen without somewhere to put it. The discussion,
/// the attachments and the links follow, each one reported on its own so a single refusal costs
/// only itself. The original is touched last and in one order - the note naming the copy, then
/// the state - because a work item closed with nothing on it to say where the work went is worse
/// than one left open.
///
/// Nothing here is ever tried twice in a way that could raise a second copy. What has been done
/// is written down as it happens, and a run that failed part way picks up from there rather than
/// starting again; only a rejection - which means nothing happened - is retried at all.
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

        public WorkItem? Created { get; set; }
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

    /// <summary>Whether anything at all went wrong, which decides how the last page reads.</summary>
    public bool AnythingFailed => Steps.Any(s => s.IsTrouble);

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
        IsLoading = true;

        // Picked up rather than started afresh: a copy this session already raised must never be
        // raised a second time, so the wizard opens on the account of that run and finishes it.
        // A record with no copy in it is nothing to resume, and is dropped so the target chosen
        // this time is the one the run reports.
        _run = _runs.TryGetValue(workItemId, out var existing) && existing.Created is not null ? existing : null;
        if (_run is not null) Stage = MigrationStage.Finished;

        Changed?.Invoke();

        try
        {
            Source = await ado.GetMigrationSourceAsync(workItemId);

            Blocks = [.. planner.Allocations.Where(a => a.WorkItemId == workItemId).OrderBy(a => a.Start)];
            Repointing.Clear();
            foreach (var block in Blocks) Repointing.Add(block.Id);

            if (_run is not null)
            {
                // Resuming: the target is whatever the copy was actually raised in.
                TargetProject = _run.Project;
                TargetType = _run.WorkItemType;
            }
            else
            {
                await LoadProjectsAsync();
                await ChooseProjectAsync(Source.Project);
            }

            await LoadClosingStatesAsync(Source);
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
    /// </summary>
    public void Restart()
    {
        if (IsRunning || _run?.Created is not null) return;

        _runs.Remove(SourceId);
        _run = null;
        RunError = null;
        WasCancelled = false;
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
    /// Points the wizard at a project and reads everything that depends on it. The area, the
    /// iteration and the type are all re-derived: a path or a type from the old project means
    /// nothing in the new one.
    /// </summary>
    public async Task ChooseProjectAsync(string project)
    {
        if (Source is not { } source) return;

        TargetProject = project?.Trim() ?? "";
        IsLoadingTarget = true;
        TargetNote = "";
        Changed?.Invoke();

        var trouble = new List<string>();

        try
        {
            TargetTypes = await ado.GetWorkItemTypesAsync(TargetProject);
        }
        catch (Exception ex)
        {
            TargetTypes = [];
            trouble.Add("work item types (" + ex.Message + ")");
        }

        try
        {
            AreaTree = await ado.GetAreaTreeAsync(TargetProject);
        }
        catch (Exception ex)
        {
            AreaTree = null;
            trouble.Add("area paths (" + ex.Message + ")");
        }

        try
        {
            _iterationTree = await ado.GetIterationTreeAsync(TargetProject);
        }
        catch (Exception ex)
        {
            _iterationTree = null;
            trouble.Add("iterations (" + ex.Message + ")");
        }

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

        IsLoadingTarget = false;
        await LoadTargetFieldsAsync();
    }

    public void ChooseArea(string area)
    {
        TargetArea = area ?? "";
        RecomputePlan();
        Changed?.Invoke();
    }

    public async Task ChooseTypeAsync(string workItemType)
    {
        TargetType = workItemType?.Trim() ?? "";
        await LoadTargetFieldsAsync();
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
    private async Task LoadTargetFieldsAsync()
    {
        _targetFields = [];
        FieldNote = "";

        if (!string.IsNullOrWhiteSpace(TargetType) && !string.IsNullOrWhiteSpace(TargetProject))
        {
            try
            {
                _targetFields = await ado.GetWorkItemTypeFieldsAsync(TargetProject, TargetType);
            }
            catch (Exception ex)
            {
                // Not fatal. Without the list every field is sent and Azure DevOps decides,
                // which is what the create's narrowing retries are there for.
                FieldNote =
                    $"Slate could not read which fields a {TargetType} has in {TargetProject} ({ex.Message}), " +
                    "so every field will be sent and any Azure DevOps refuses will be dropped.";
            }
        }

        RecomputePlan();
        Changed?.Invoke();
    }

    private async Task LoadClosingStatesAsync(MigrationSource source)
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
                + $"#{source.Id} keeps its own, and stays readable."),
            new("Time already recorded",
                $"Completed Work booked against #{source.Id} stays on #{source.Id}, so no report counts the same hours twice. "
                + "Only the estimate comes over."),
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

        if (!writes.TryEnter())
        {
            RunError = AppState.RestartingForUpdate;
            toasts.Error("Could not migrate that work item", AppState.RestartingForUpdate);
            Changed?.Invoke();
            return;
        }

        // A record with no copy in it is not something to resume - the target may have been
        // changed since - and its account belongs to that attempt, not this one.
        if (_run is { Created: null }) _run = null;

        _run ??= new RunRecord(TargetProject, TargetType);
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

            // Nothing else may run without somewhere to put it, and the original must never be
            // closed when there is no copy.
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
        }
        catch (Exception ex)
        {
            // Every step already answers for itself, so nothing should reach here. If anything
            // does, it is said out loud beside the steps rather than thrown at a page that
            // would simply go blank - leaving a half-done migration with no account of it.
            RunError = ex.Message;
        }
        finally
        {
            IsRunning = false;
            Stage = MigrationStage.Finished;

            _cancel?.Dispose();
            _cancel = null;

            writes.Exit();
            Report(run, source);
            Changed?.Invoke();
        }
    }

    private void Report(RunRecord run, MigrationSource source)
    {
        if (run.Created is not { } created)
        {
            if (!WasCancelled) toasts.Error($"Could not migrate #{source.Id}", run.Create.Detail);
            return;
        }

        if (WasCancelled)
        {
            toasts.Error($"Migration of #{source.Id} stopped part way",
                $"#{created.Id} was already raised in {run.Project}. Nothing on #{source.Id} was changed unless the steps say so.");
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

    private async Task RaiseCopyAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Create;

        if (run.Created is { } already)
        {
            step.State = MigrationStepState.Done;
            step.Detail = $"#{already.Id} was already raised; it is not raised again.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        try
        {
            var created = await ado.CreateMigratedAsync(TargetProject, TargetType, Carrying, ct);

            run.Created = created.Item;
            state.AdoptWorkItem(created.Item);

            step.State = MigrationStepState.Done;
            step.Detail = created.DroppedFields.Count == 0
                ? $"#{created.Item.Id} raised as a {TargetType} in {TargetProject}."
                : $"#{created.Item.Id} raised as a {TargetType} in {TargetProject}, but Azure DevOps would not take "
                  + string.Join(", ", created.DroppedFields)
                  + " — most often an assignee with no access to that project. Fill those in by hand.";
        }
        catch (AzureDevOpsException ex) when (ex.Unanswered)
        {
            // The one failure that must not be retried: the copy may exist.
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message + " Azure DevOps did not say whether the copy was raised, so look in "
                          + TargetProject + " before running this again — trying again could raise a second one.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message + $" Nothing was changed on #{source.Id}.";
        }
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
            await ado.AddRelatedLinkAsync(run.Created!.Id, source.Id, $"Migrated from #{source.Id}", ct);

            run.LinkedToSource = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{run.Created.Id} and #{source.Id} are Related to each other.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step.State = MigrationStepState.Failed;
            step.Detail = ex.Message + " The note on the original still names the copy, so the trail is not lost.";
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

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

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
                await ado.AddRelatedLinkAsync(run.Created!.Id, id,
                    $"{char.ToUpperInvariant(what[0])}{what[1..]} of #{source.Id} before it was migrated", ct);

                run.Referenced.Add(id);
                done++;
            }
            catch (AzureDevOpsException ex) when (ex.Status == HttpStatusCode.BadRequest)
            {
                // Almost always a link that is already there, from a run that got this far
                // before. Counted as done rather than reported: it is what was asked for.
                run.Referenced.Add(id);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"#{id} ({ex.Message})");
            }
        }

        step.State = failed.Count == 0 ? MigrationStepState.Done : MigrationStepState.Failed;
        step.Detail = failed.Count == 0
            ? $"{done} referenced as Related. None of them was moved or changed otherwise."
            : $"{done} of {ids.Count} referenced. Could not reference " + string.Join("; ", failed) + ".";
    }

    private async Task CopyDiscussionAsync(RunRecord run, MigrationSource source, CancellationToken ct)
    {
        var step = run.Discussion;

        if (source.Comments.Count == 0)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = "There is no discussion to copy.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        var done = 0;
        var failed = new List<string>();

        foreach (var comment in source.Comments)
        {
            ct.ThrowIfCancellationRequested();

            if (run.CommentsCopied.Contains(comment.Id))
            {
                done++;
                continue;
            }

            step.Detail = $"Copying comment {done + failed.Count + 1} of {source.Comments.Count}…";
            Changed?.Invoke();

            try
            {
                await ado.AddCommentAsync(run.Created!.Id, TargetProject, QuotedComment(source, comment), ct);

                run.CommentsCopied.Add(comment.Id);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"the one from {comment.Author} ({ex.Message})");
            }
        }

        step.State = failed.Count == 0 ? MigrationStepState.Done : MigrationStepState.Failed;
        step.Detail = failed.Count == 0
            ? $"{done} copied, each one naming who wrote it and when. Azure DevOps records you as the author."
            : $"{done} of {source.Comments.Count} copied. Could not copy " + string.Join("; ", failed) + ".";
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

        foreach (var file in source.Attachments)
        {
            ct.ThrowIfCancellationRequested();

            if (run.FilesCopied.Contains(file.Url))
            {
                done++;
                continue;
            }

            var position = done + failed.Count + 1;
            step.Detail = $"Fetching {file.Name} ({file.SizeLabel}) — {position} of {source.Attachments.Count}…";
            Changed?.Invoke();

            try
            {
                var bytes = await ado.DownloadAttachmentAsync(file.Url, ct);

                ct.ThrowIfCancellationRequested();
                step.Detail = $"Uploading {file.Name} ({file.SizeLabel}) — {position} of {source.Attachments.Count}…";
                Changed?.Invoke();

                var uploaded = await ado.UploadAttachmentAsync(TargetProject, file.Name, bytes, ct);
                await ado.AttachFileAsync(run.Created!.Id, uploaded, file.Comment, ct);

                run.FilesCopied.Add(file.Url);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One file that will not come over is not a reason to lose the rest of it.
                failed.Add($"{file.Name} ({ex.Message})");
            }
        }

        step.State = failed.Count == 0 ? MigrationStepState.Done : MigrationStepState.Failed;
        step.Detail = failed.Count == 0
            ? $"{done} file{(done == 1 ? "" : "s")} attached to the copy. The originals stay on #{source.Id} as well."
            : $"{done} of {source.Attachments.Count} copied. Could not copy " + string.Join("; ", failed)
              + $". They are still on #{source.Id}.";
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
            await ado.AddCommentAsync(source.Id, source.Project, MigrationNote(run, source), ct);

            run.SourceNoted = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{source.Id} now names #{run.Created!.Id} in its discussion.";
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
    private string MigrationNote(RunRecord run, MigrationSource source)
    {
        var created = run.Created!;
        var where = string.IsNullOrWhiteSpace(created.AreaPath) ? run.Project : created.AreaPath;

        var closing = string.IsNullOrWhiteSpace(ClosingState)
            ? "It is being left in its current state."
            : $"It is to be closed as <b>{WebUtility.HtmlEncode(ClosingState)}</b> and should not be worked on further.";

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

        if (string.IsNullOrWhiteSpace(ClosingState))
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = $"You chose to leave #{source.Id} as it is.";
            return;
        }

        // The one rule that is not about tidiness: an original closed with nothing on it to say
        // where the work went is worse than one left open.
        if (!run.LinkedToSource && !run.SourceNoted)
        {
            step.State = MigrationStepState.Skipped;
            step.Detail = $"Not attempted: neither the link nor the note went on, so nothing on #{source.Id} "
                          + $"would point at #{run.Created!.Id}. Close it by hand once you have looked at both.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        step.State = MigrationStepState.Running;
        Changed?.Invoke();

        try
        {
            var updated = await ado.SetStateAsync(source.Id, ClosingState, ct);
            if (updated is not null) state.AdoptWorkItem(updated);

            run.SourceClosed = true;
            step.State = MigrationStepState.Done;
            step.Detail = $"#{source.Id} is now {ClosingState}.";
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
        var chosen = Blocks.Where(b => Repointing.Contains(b.Id)).ToList();

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
