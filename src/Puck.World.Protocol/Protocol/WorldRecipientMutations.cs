namespace Puck.World.Protocol;

/// <summary>The mutation vocabulary's adapter to the recipient capability. Policy is decided only by the view.</summary>
public static class WorldRecipientMutations {
    /// <summary>The actual typed mutation refusal for a row withheld by policy.</summary>
    public const string NotDisclosedCode = "world.mutation.undisclosed";
    /// <summary>The typed mutation refusal for a submission naming a card back (<see cref="WorldPlacementDeal.PlaceholderId"/>).</summary>
    public const string PlaceholderCode = "world.mutation.placeholder";

    /// <summary>Finds a card back a mutation names as a placement, its parent, or a guarded input: an id only a
    /// recipient's view holds, which no authority row stands for. A function of the mutation alone, so it is decided
    /// before policy and answers every principal alike.</summary>
    /// <param name="mutation">The submitted mutation.</param>
    /// <param name="id">The first placeholder id named, or empty.</param>
    /// <returns><see langword="true"/> when the mutation names a placeholder.</returns>
    public static bool NamesPlaceholder(WorldMutation mutation, out string id) {
        id = string.Empty;
        bool Is(string? candidate, out string found) {
            found = candidate ?? string.Empty;
            return WorldPlacementDeal.IsPlaceholderId(id: candidate, template: out _);
        }
        switch (mutation) {
            case WorldMutation.UpsertPlacement upsert:
                return Is(upsert.Placement.Id, out id) || Is(upsert.Placement.Parent, out id);
            case WorldMutation.RemovePlacement remove:
                return Is(remove.Id, out id);
            case WorldMutation.Batch batch:
                foreach (var input in batch.ExpectedInputs?.PlacementIds ?? []) { if (Is(input, out id)) { return true; } }
                foreach (var member in batch.Mutations) { if (NamesPlaceholder(member, out id)) { return true; } }
                return false;
            default:
                return false;
        }
    }
    /// <summary>The refusal for a submission naming a card back, worded from the id alone.</summary>
    /// <param name="id">The placeholder id.</param>
    public static string PlaceholderRefusal(string id) => $"'{id}' is a placeholder, the back a reader is shown for a withheld cell; no placement stands for it";
    /// <summary>Refuses every write or guard whose footprint holds a row the recipient cannot read whole, before
    /// grants, membership, capacity, fingerprints or composition can disclose anything about that row, and every
    /// mutation of a program section (<see cref="WorldSectionFacts.IsProgram"/>) by its kind alone, before it is
    /// compiled: a rule or interaction reads any row and fires as the world, so authoring one is authoring the
    /// world.</summary>
    /// <remarks>The footprint is derived, never listed: every name the submission spells, wherever it spells it
    /// (<see cref="WorldMutationNames"/>, which reads document values through the name registry), each admitted by
    /// <see cref="WorldRecipientView.Admits(string)"/>, which refuses a name no row holds exactly as it refuses a
    /// withheld one; the installed value the write replaces or removes; a placement's chain; and the rows a transform
    /// resolves to. The door's two invariants hold for every recipient door (complete admission, live state commands,
    /// detached composition and its batch members, guards): the verdict reads the recipient's declared policy alone,
    /// never its delivery ceiling or whether it observes now, except that a guard over the document is a read of it;
    /// and <paramref name="row"/>, the name the refusal carries, is always a name the submission spelled (the name
    /// refused, or for a footprint the document derived, the submission's subject: the identity of the value it
    /// writes, a transform's first subject, or the section a guard reads), never a row the footprint derived from the
    /// document, so a refusal names no declaration the submitter did not.</remarks>
    /// <param name="view">The recipient's capability.</param>
    /// <param name="mutation">The submitted mutation.</param>
    /// <param name="row">The submitted name the refusal names, or the program section's document name; empty when admitted.</param>
    /// <returns><see langword="true"/> when the recipient may submit the mutation to grants and composition.</returns>
    public static bool Admits(this WorldRecipientView view, WorldMutation mutation, out string row) {
        row = string.Empty;
        if (view.IsAuthority) { return true; }
        var section = WorldMutationKindCatalog.SectionOf(mutation);
        if (section.IsProgram()) {
            row = SectionName(section);
            return false;
        }
        if (mutation is WorldMutation.Batch batch) { return AdmitsBatch(view, batch, out row); }
        string? refused = null;
        WorldMutationNames.Collect(mutation, (_, kind, name) => {
            if ((refused is null) && WorldSpelledNames.NamesStateRow(kind, name) && !view.Admits(name)) { refused = name; }
        }, view.MachineCatalog);
        if (refused is not null) {
            row = refused;
            return false;
        }
        if (!(Replaced(mutation)?.Invoke(view) ?? true) || !AdmitsDerived(view, mutation)) {
            row = RefusalSubject(mutation);
            return false;
        }
        return true;
    }
    /// <summary>Returns the name a refusal over a write's derived footprint carries, for every door that refuses a
    /// mutation by what its footprint reads rather than by a name it spells (this door's installed values and
    /// derived rows, and a state transaction's binding validation): the subject the submission spelled for the value
    /// it writes, else the first name it spells, else its section's document name. It is always a name the submitter
    /// already holds, so every such refusal carries the same text whatever the withheld row is.</summary>
    /// <param name="mutation">The submitted mutation.</param>
    /// <returns>The name.</returns>
    public static string RefusalSubject(WorldMutation mutation) {
        ArgumentNullException.ThrowIfNull(mutation);
        if (Subject(mutation) is { } subject) { return subject; }
        string? spelled = null;
        WorldMutationNames.Collect(mutation, (_, _, name) => spelled ??= name);
        return spelled ?? SectionName(WorldMutationKindCatalog.SectionOf(mutation));
    }
    private static bool AdmitsBatch(WorldRecipientView view, WorldMutation.Batch batch, out string row) {
        row = string.Empty;
        if (!batch.TryValidateShape(out _)) { return true; }
        foreach (var cell in batch.ExpectedCells ?? []) {
            if (!view.Admits(cell.Row)) { row = cell.Row; return false; }
        }
        if (batch.ExpectedDefinition is not null && !view.AdmitsDefinition(batch.ExpectedStateRows, out row)) { return false; }
        if (batch.ExpectedInputs is { } inputs) {
            if (!view.AdmitsDefinition(inputs.StateRows, out row)) { return false; }
            foreach (var id in inputs.PlacementIds) { if (!view.AdmitsPlacement(id)) { row = id; return false; } }
        }
        if (batch.ExpectedSpatialReads is { Count: > 0 } && !view.AdmitsSpatial()) { row = SectionName(WorldSection.Placements); return false; }
        foreach (var member in batch.Mutations) { if (!view.Admits(member, out row)) { return false; } }
        return true;
    }
    // The footprint a write derives from the document rather than spells: a placement's installed chain and the
    // template its dealt id spells, and every row a transform resolves to.
    private static bool AdmitsDerived(WorldRecipientView view, WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertPlacement upsert => view.AdmitsPlacement(upsert.Placement.Id) &&
            ((upsert.Placement.Parent is not { } parent) || view.AdmitsPlacement(parent)),
        WorldMutation.RemovePlacement remove => view.AdmitsPlacement(remove.Id),
        WorldMutation.TransformState transform => view.AdmitsTransform(transform.Transform),
        _ => true,
    };

    /// <summary>Returns whether the write door reads the installed value a mutation kind replaces or removes, whose
    /// state names are the write's footprint whether or not the submission names them.</summary>
    /// <param name="kind">A concrete mutation kind.</param>
    /// <returns><see langword="true"/> when the door resolves the kind's installed value.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not a concrete mutation kind.</exception>
    public static bool ResolvesReplaced(Type kind) {
        ArgumentNullException.ThrowIfNull(kind);
        if (!typeof(WorldMutation).IsAssignableFrom(kind) || kind.IsAbstract) { throw new ArgumentException("not a concrete mutation kind", nameof(kind)); }
        // A resolver captures its mutation without reading it, so an uninitialized instance selects its arm.
        return Replaced((WorldMutation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(kind)) is not null;
    }
    /// <summary>The mutation kinds whose footprint is not an installed value the door resolves through
    /// <see cref="ResolvesReplaced"/>, each with where its footprint is judged instead. Every concrete kind outside a
    /// program section either resolves its installed value or is listed here, so a new kind is classified when it is
    /// written.</summary>
    public static IReadOnlyDictionary<Type, string> JudgedElsewhere { get; } = new Dictionary<Type, string> {
        [typeof(WorldMutation.UpsertStateRow)] = "a state row's own declaration is admitted with its name, its relations with it",
        [typeof(WorldMutation.RemoveStateRow)] = "a state row's own declaration is admitted with its name, its relations with it",
        [typeof(WorldMutation.UpsertStateCell)] = "a cell write is admitted by its row's name and declared relations",
        [typeof(WorldMutation.RemoveStateCell)] = "a cell removal is admitted by its row's name and declared relations",
        [typeof(WorldMutation.Generate)] = "a draw is admitted by its row's name and declared relations",
        [typeof(WorldMutation.SetProperty)] = "a property's tags live in the keyed row of its name, admitted with the name",
        [typeof(WorldMutation.UpsertPlacement)] = "a placement is admitted with its installed chain and the template its id spells",
        [typeof(WorldMutation.RemovePlacement)] = "a placement is admitted with its installed chain and the template its id spells",
        [typeof(WorldMutation.TransformState)] = "a transform is admitted with every row it resolves to",
        [typeof(WorldMutation.Batch)] = "each member and each guard is admitted on its own",
        [typeof(WorldMutation.SetDefaultSeatKit)] = "it replaces a kit's name, which spells no state row",
        [typeof(WorldMutation.UpsertGrant)] = "a grant change is the grants' own transition",
        [typeof(WorldMutation.RemoveGrant)] = "a grant change is the grants' own transition",
        [typeof(WorldMutation.UpsertGroupKind)] = "a group change is the grants' own transition",
        [typeof(WorldMutation.RemoveGroupKind)] = "a group change is the grants' own transition",
        [typeof(WorldMutation.FormGroup)] = "a group change is the grants' own transition",
        [typeof(WorldMutation.JoinGroup)] = "a group change is the grants' own transition",
        [typeof(WorldMutation.LeaveGroup)] = "a group change is the grants' own transition",
        [typeof(WorldMutation.KickMember)] = "a group change is the grants' own transition",
        [typeof(WorldMutation.OfferOwnership)] = "an ownership change is the grants' own transition",
        [typeof(WorldMutation.SettleOwnership)] = "an ownership change is the grants' own transition",
    };
    // The one table of installed values a write replaces or removes: a keyed section's rows by the key composition
    // matches them by (WorldRowKeys), and a section a write replaces whole, as composition replaces it.
    private static Func<WorldRecipientView, bool>? Replaced(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertMachine upsert => view => view.AdmitsReplacing(WorldRowKeys.Machines, static document => document.Machines, upsert.Machine),
        WorldMutation.RemoveMachine remove => view => view.AdmitsReplaced(WorldRowKeys.Machines, static document => document.Machines, remove.Name),
        WorldMutation.UpsertKit upsert => view => view.AdmitsReplacing(WorldRowKeys.Kits, static document => document.Kits, upsert.Kit),
        WorldMutation.RemoveKit remove => view => view.AdmitsReplaced(WorldRowKeys.Kits, static document => document.Kits, remove.Name),
        WorldMutation.UpsertCreation upsert => view => view.AdmitsReplacing(WorldRowKeys.Creations, static document => document.Creations, upsert.Creation),
        WorldMutation.RemoveCreation remove => view => view.AdmitsReplaced(WorldRowKeys.Creations, static document => document.Creations, remove.Id),
        WorldMutation.UpsertAddon upsert => view => view.AdmitsReplacing(WorldRowKeys.Addons, static document => document.Addons, upsert.Addon),
        WorldMutation.RemoveAddon remove => view => view.AdmitsReplaced(WorldRowKeys.Addons, static document => document.Addons, remove.Name),
        WorldMutation.UpsertCamera upsert => view => view.AdmitsReplacing(WorldRowKeys.Cameras, static document => document.Cameras, upsert.Camera),
        WorldMutation.RemoveCamera remove => view => view.AdmitsReplaced(WorldRowKeys.Cameras, static document => document.Cameras, remove.Name),
        WorldMutation.UpsertScreen upsert => view => view.AdmitsReplacing(WorldRowKeys.Screens, static document => document.Screens, upsert.Screen),
        WorldMutation.RemoveScreen remove => view => view.AdmitsReplaced(WorldRowKeys.Screens, static document => document.Screens, remove.Index),
        WorldMutation.UpsertSpeaker upsert => view => view.AdmitsReplacing(WorldRowKeys.Speakers, static document => document.Speakers, upsert.Speaker),
        WorldMutation.RemoveSpeaker remove => view => view.AdmitsReplaced(WorldRowKeys.Speakers, static document => document.Speakers, remove.Name),
        WorldMutation.UpsertTune upsert => view => view.AdmitsReplacing(WorldRowKeys.Tunes, static document => document.Tunes, upsert.Tune),
        WorldMutation.RemoveTune remove => view => view.AdmitsReplaced(WorldRowKeys.Tunes, static document => document.Tunes, remove.Name),
        WorldMutation.UpsertPatch upsert => view => view.AdmitsReplacing(WorldRowKeys.Patches, static document => document.Patches, upsert.Patch),
        WorldMutation.RemovePatch remove => view => view.AdmitsReplaced(WorldRowKeys.Patches, static document => document.Patches, remove.Name),
        WorldMutation.UpsertLook upsert => view => view.AdmitsReplacing(WorldRowKeys.Looks, static document => document.Looks, upsert.Look),
        WorldMutation.RemoveLook remove => view => view.AdmitsReplaced(WorldRowKeys.Looks, static document => document.Looks, remove.Name),
        WorldMutation.UpsertBindingOverlay upsert => view => view.AdmitsReplacing(WorldRowKeys.BindingOverlays, static document => document.BindingOverlays, upsert.Overlay),
        WorldMutation.RemoveBindingOverlay remove => view => view.AdmitsReplaced(WorldRowKeys.BindingOverlays, static document => document.BindingOverlays, remove.Id),
        WorldMutation.UpsertViewLayout upsert => view => view.AdmitsReplacing(WorldRowKeys.ViewLayouts, static document => document.Views.Layouts, upsert.Layout),
        WorldMutation.RemoveViewLayout remove => view => view.AdmitsReplaced(WorldRowKeys.ViewLayouts, static document => document.Views.Layouts, remove.Name),
        WorldMutation.UpsertViewGraph upsert => view => view.AdmitsReplacing(WorldRowKeys.ViewGraphs, static document => document.Views.Graphs, upsert.Graph),
        WorldMutation.RemoveViewGraph remove => view => view.AdmitsReplaced(WorldRowKeys.ViewGraphs, static document => document.Views.Graphs, remove.Name),
        WorldMutation.CommitViewGraph commit => view => view.AdmitsReplaced(WorldRowKeys.ViewGraphs, static document => document.Views.Graphs, commit.Name),
        WorldMutation.UpsertHudPanel upsert => view => view.AdmitsReplacing(WorldRowKeys.HudPanels, static document => document.Hud.Panels, upsert.Panel),
        WorldMutation.RemoveHudPanel remove => view => view.AdmitsReplaced(WorldRowKeys.HudPanels, static document => document.Hud.Panels, remove.Id),
        WorldMutation.UpsertHudElement upsert => view => view.AdmitsReplacing(WorldRowKeys.HudElements,
            document => WorldRowKeys.HudPanels.Find(document.Hud.Panels, upsert.PanelId)?.Elements, upsert.Element),
        WorldMutation.RemoveHudElement remove => view => view.AdmitsReplaced(WorldRowKeys.HudElements,
            document => WorldRowKeys.HudPanels.Find(document.Hud.Panels, remove.PanelId)?.Elements, remove.ElementId),
        WorldMutation.UpsertDynamics upsert => view => view.AdmitsReplacing(WorldRowKeys.Dynamics, static document => document.Dynamics, upsert.Row),
        WorldMutation.RemoveDynamics remove => view => view.AdmitsReplaced(WorldRowKeys.Dynamics, static document => document.Dynamics, remove.Name),
        WorldMutation.UpsertCurve upsert => view => view.AdmitsReplacing(WorldRowKeys.Curves, static document => document.Curves, upsert.Row),
        WorldMutation.RemoveCurve remove => view => view.AdmitsReplaced(WorldRowKeys.Curves, static document => document.Curves, remove.Name),
        // A section a write replaces whole; an assignment's selector, which composition carries forward, is part of it.
        WorldMutation.SetInputModes => static view => view.AdmitsInstalled(static document => document.InputModes),
        WorldMutation.SetInputHold => static view => view.AdmitsInstalled(static document => document.InputHoldRaw),
        WorldMutation.SetKitAssignment => static view => view.AdmitsInstalled(static document => document.Assignment),
        WorldMutation.SetLookAssignment => static view => view.AdmitsInstalled(static document => document.LookAssignment),
        WorldMutation.SetSpawns => static view => view.AdmitsInstalledRows(static document => document.SpawnPointsRaw),
        WorldMutation.SetMotion => static view => view.AdmitsInstalled(static document => document.Motion),
        WorldMutation.SetPopulationDefaults => static view => view.AdmitsInstalled(static document => document.Population),
        WorldMutation.SetPopulationDistribution => static view => view.AdmitsInstalled(static document => document.Population.DistributionRaw),
        WorldMutation.SetPopulationCensus => static view => view.AdmitsInstalledRows(static document => document.Population.SeatActivationRaw),
        WorldMutation.SetRenderDefaults => static view => view.AdmitsInstalled(static document => document.Render),
        WorldMutation.SetAudioDefaults => static view => view.AdmitsInstalled(static document => document.Audio),
        WorldMutation.SetAuthoringDefaults => static view => view.AdmitsInstalled(static document => document.AuthoringRaw),
        WorldMutation.SetCollision => static view => view.AdmitsInstalled(static document => document.Collision),
        WorldMutation.SetHostDefaults => static view => view.AdmitsInstalled(static document => document.Host),
        WorldMutation.SetViewDefaults => static view => view.AdmitsInstalled(static document => document.Views),
        WorldMutation.SetViewSeatRig => static view => view.AdmitsInstalled(static document => document.Views.SeatRigRaw),
        WorldMutation.SetViewSeatControl => static view => view.AdmitsInstalled(static document => document.Views.SeatControlRaw),
        WorldMutation.SetViewPost => static view => view.AdmitsInstalledRows(static document => document.Views.Post),
        WorldMutation.SetPlayerDefaults => static view => view.AdmitsInstalled(static document => document.PlayerDefaults),
        WorldMutation.SetPlayerSeatLook => static view => view.AdmitsInstalled(static document => document.PlayerDefaults.SeatLookRaw),
        WorldMutation.SetHudDefaults => static view => view.AdmitsInstalled(static document => document.Hud.Defaults),
        _ => null,
    };
    // The name a refusal over a derived footprint carries: what the submission spelled for the value it writes.
    private static string? Subject(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertPlacement upsert => upsert.Placement.Id,
        WorldMutation.RemovePlacement remove => remove.Id,
        WorldMutation.TransformState transform => transform.Transform.Subjects() is { Count: > 0 } subjects ? subjects[0].Name : SectionName(WorldSection.State),
        WorldMutation.RemoveScreen => SectionName(WorldSection.Screens),
        WorldMutation.UpsertMachine upsert => upsert.Machine.Name,
        WorldMutation.UpsertKit upsert => upsert.Kit.Name,
        _ => null,
    };
    private static string SectionName(WorldSection section) => section.ToString().ToLowerInvariant();

    /// <summary>One guard implementation for live state batches and detached document composition.</summary>
    public static bool CheckGuards(this WorldRecipientView view, WorldMutation.Batch batch, out string reason) {
        reason = string.Empty;
        if (!view.Admits(batch, out var row)) { reason = view.Refusal("batch", row); return false; }
        if (batch.ExpectedDefinition is { } expected && expected != view.Fingerprint(batch.ExpectedStateRows)) {
            reason = "the proposal is stale: its base definition changed; preview again"; return false;
        }
        if (batch.ExpectedInputs is { } inputs && inputs.Fingerprint != view.Fingerprint(inputs)) {
            reason = "the proposal is stale: its named inputs changed; preview again"; return false;
        }
        foreach (var spatial in batch.ExpectedSpatialReads ?? []) {
            if (spatial.Fingerprint != view.Fingerprint(spatial.Region)) { reason = "the proposal is stale: its spatial read changed; preview again"; return false; }
        }
        foreach (var cell in batch.ExpectedCells ?? []) {
            if (!view.Fingerprint(cell)) { reason = $"the proposal is stale: state '{cell.Row}' changed; preview again"; return false; }
        }
        return true;
    }
}
