using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A stateless capability for a recipient's live document. Its ceiling and live Observe admission apply
/// to every read. Delivery history belongs exclusively to a <see cref="WorldDeliveryCursor"/>.</summary>
public sealed partial class WorldRecipientView(Func<WorldDefinition> definition, Func<StateArena> arena, Principal? recipient,
    WorldDisclosureTier ceiling, Func<bool> observes, WorldDisclosureImages images, string authority,
    Func<ArenaTime> time, Func<bool>? operates = null) {
    internal WorldDefinition Source => definition();
    internal StateArena Arena => arena();
    internal Principal? Recipient => recipient;
    internal WorldDisclosureImages Images => images;
    internal ArenaTime Time => time();
    internal bool Unrestricted => operates?.Invoke() == true;
    internal WorldStateDisclosure.Observer Observer => new(Arena, recipient, Unrestricted, m_recipientName ??= recipient?.Describe());
    private static readonly WorldWithheldRow WithheldWhole = new(0, HiddenCells.Omit, WholeRow: true);
    // The recipient's canonical token, formatted once for every policy check this view makes.
    private string? m_recipientName;
    // The last audience key Images composed for this view, reused while the policies it reads answer the same.
    internal string? AudienceKey { get; set; }

    /// <summary>The admission tier; no operation can raise it.</summary>
    public WorldDisclosureTier Ceiling => ceiling;
    /// <summary>Whether the capability currently discloses a document.</summary>
    public bool Observes => observes() && ceiling != WorldDisclosureTier.Frames;
    /// <summary>The live body policy and the recipient's authority-assigned body, when embodied.</summary>
    public WorldSinkDisclosure Bodies => Observes
        ? new(Source.Population.ObserverDisclosure, recipient is { Kind: PrincipalKind.Seat or PrincipalKind.Peer } body ? body.Index : -1)
        : new(WithheldBodies, -1);
    private static readonly WorldObserverDisclosure WithheldBodies = new(Mode: WorldObserverDisclosureMode.SelfOnly);

    /// <summary>The document the recipient may hold at its ceiling.</summary>
    public WorldDefinition Definition {
        get {
            if (!Observes) { return WorldProjection.Undisclosed; }
            if (ceiling == WorldDisclosureTier.Replica) { return images.Definition(this); }
            var projection = Project();
            return WorldProjection.TryToDefinition(projection!, out var hydrated, out var reason)
                ? hydrated : throw new InvalidOperationException(reason);
        }
    }
    /// <summary>Whether the declaration can withhold any member of this row.</summary>
    public bool ReadsWhole(int row) => Includes(row) && Observer.ReadsWhole(row);
    /// <summary>Whether a named row is carried whole at this ceiling.</summary>
    public bool ReadsWhole(string row) => Arena.Catalog.TryResolve(StateLane.Document, row, out var handle) && ReadsWhole(handle.Ordinal);
    /// <summary>Gets the content identity of the scene inputs a replica stream through this view is handed now: the
    /// disclosed document less its state rows, and for each row a scene reads (<see cref="WorldSceneInputs"/> and the
    /// rows the placements read) whether this view reads it whole and otherwise its image. Two views with equal
    /// identities are handed the same scene inputs, body redaction aside, which the snapshot's body table carries
    /// under the world's observer disclosure. A row no scene reads never moves it. Read on the thread that steps the
    /// world; <see langword="null"/> for a view that does not observe at <see cref="WorldDisclosureTier.Replica"/>.</summary>
    public Puck.Assets.ContentPin? SceneIdentity => (Observes && (ceiling == WorldDisclosureTier.Replica)) ? images.SceneIdentity(this) : null;
    /// <summary>Gets the first row a scene reads that this view does not read whole, by name, or <see langword="null"/>
    /// when it reads every one whole: an operator's account of why a stream's scene differs from a whole reader's.</summary>
    public string? FirstSceneRowWithheld => Observes ? images.FirstSceneRowWithheld(this) : null;
    /// <summary>Admits a transient derived from a whole row under a stable audience.</summary>
    /// <param name="row">The row's catalog ordinal.</param>
    /// <returns><see langword="true"/> when the row is carried whole at this ceiling and no audience on its path
    /// follows another row's members.</returns>
    public bool ReadsCueDependency(int row) => ReadsWhole(row) && (Unrestricted || ReadsUnderStableAudience(row));
    /// <summary>Admits a transient derived from the physical field regions: every field row of every region, and every
    /// row a region's travel resistance reads, is read whole under a stable audience, as the field projection carries
    /// a field only when its row reads whole.</summary>
    /// <returns><see langword="true"/> when the recipient reads every field region whole.</returns>
    public bool ReadsEveryFieldRegion() {
        if (!Observes) { return false; }
        if (Unrestricted) { return true; }
        var arena = Arena;
        var observer = Observer;
        bool Reads(string name) => arena.Catalog.TryResolve(StateLane.Document, name, out var handle) &&
            observer.ReadsWhole(handle.Ordinal) && ReadsUnderStableAudience(handle.Ordinal);
        foreach (var region in Source.FieldRegions) {
            foreach (var field in region.Fields) {
                if (!Reads(field.Name)) { return false; }
            }
            if (region.TravelResistance?.Row is { } resistance && !Reads(resistance)) { return false; }
        }
        return true;
    }
    /// <summary>Admits a transient derived from a screen's machine memory: the recipient holds the machine (the replica
    /// document carries machines; a presentation projection does not) and reads whole every row the machine's memory
    /// bindings exchange with its hardware.</summary>
    /// <param name="screen">The screen index.</param>
    /// <returns><see langword="true"/> when the recipient holds the screen's machine and every bound row.</returns>
    public bool HoldsMachine(int screen) {
        if (!Observes) { return false; }
        if (Unrestricted) { return true; }
        if (ceiling != WorldDisclosureTier.Replica) { return false; }
        var source = Source;
        WorldScreen? row = null;
        foreach (var candidate in source.Screens) {
            if (candidate.Index == screen) { row = candidate; break; }
        }
        if ((row?.Source is not WorldScreenSource.Machine machineSource) ||
            (WorldDefinitionRows.FindMachine(source.Machines, machineSource.Instance) is not { } machine)) { return false; }
        foreach (var binding in machine.Memory ?? []) {
            if ((binding?.Row is { } bound) &&
                !(Arena.Catalog.TryResolve(StateLane.Document, bound, out var handle) && ReadsCueDependency(handle.Ordinal))) { return false; }
        }
        return true;
    }
    // Whether no audience on the row's path follows another row's members, which a transient cannot track as they move.
    private bool ReadsUnderStableAudience(int row) {
        var arena = Arena;
        if (arena.Rows[row].Visibility?.ReadersFrom is not null) { return false; }
        foreach (var zone in arena.Catalog.ContainingZones(row)) {
            if (arena.Rows[zone].Visibility?.ReadersFrom is not null) { return false; }
        }
        return true;
    }
    /// <summary>The policy's withheld metadata, never an undisclosed member count. A view that does not observe
    /// withholds every name whole, declared or not, so its answer never tells which rows exist.</summary>
    public WorldWithheldRow? Withheld(string row) {
        if (!Observes) { return WithheldWhole; }
        if (!Arena.Catalog.TryResolve(StateLane.Document, row, out var handle) || ReadsWhole(handle.Ordinal)) { return null; }
        var observation = Image(handle.Ordinal).Observation;
        return new WorldWithheldRow(observation?.HiddenCount ?? 0,
            observation is null ? HiddenCells.Omit : Arena.Rows[handle.Ordinal].Visibility?.Hidden ?? HiddenCells.Omit,
            observation is null);
    }
    /// <summary>Whether this cell is disclosed; absent and withheld cells answer alike.</summary>
    public bool Discloses(int row, string? key) => Includes(row) && Observer.Discloses(row, key);
    /// <summary>Resolves a named row inside the capability, exposing no arena.</summary>
    public bool Discloses(string row, string? key) => Arena.Catalog.TryResolve(StateLane.Document, row, out var handle) && Discloses(handle.Ordinal, key);
    // Whether the row's content reaches the recipient at its ceiling as rows do: every row at a replica, and below it a
    // presented row and every row that can withhold, whether by its own policy, a cell policy or a containing zone's. A
    // row restricted only by its zone is a restricted row like any other: what is dealt, varied or sized from it reaches
    // a presentation recipient, so its image, the recipient's document identity and its stream's cuts follow it.
    internal bool Includes(int row) => Observes && (ceiling == WorldDisclosureTier.Replica ||
        (Arena.Rows[row].HostOwned || Arena.Rows[row].Visibility is not null || Arena.DeclaresCellVisibility(row) || ZoneRestricted(row) ||
        images.Presented(Source).Contains(Arena.Rows[row].Name.Value)));
    // Whether nothing on the row's path can withhold a member from the recipient, whatever its ceiling carries of the row:
    // a delivery cursor holds such a row whole, so a row that stops crossing (a zone removed from a row nobody presents)
    // is no loss. At a replica it answers as ReadsWhole does.
    internal bool HoldsWhole(int row) => Observes && Observer.ReadsWhole(row);
    // Whether a zone containing the row declares a policy of its own or per cell, so that membership can withhold the
    // row's members.
    private bool ZoneRestricted(int row) {
        var arena = Arena;
        foreach (var zone in arena.Catalog.ContainingZones(row)) {
            if ((arena.Rows[zone].Visibility is not null) || arena.DeclaresCellVisibility(zone)) { return true; }
        }
        return false;
    }
    /// <summary>Reads a stored value at this ceiling; absent and withheld values return the same empty carrier.</summary>
    public bool TryRead(string row, string? key, out CellValue value) {
        value = default;
        if (!Arena.Catalog.TryResolve(StateLane.Document, row, out var handle) || !Discloses(handle.Ordinal, key) ||
            !Arena.Keys.TryResolve(CellName.Parse(key ?? StateRow.SlotKey.Value), out var cell)) { return false; }
        return Arena.TryRead(handle.Ordinal, cell, out value);
    }
    /// <summary>One immutable observable row, shared by content and audience.</summary>
    public WorldRecipientImage Image(int row) => images.Image(this, row);
    /// <summary>The live observations, including rows a presentation explicitly reads.</summary>
    public IReadOnlyList<WorldObservedRow>? Observations(IReadOnlySet<string>? presented = null) {
        if (!Observes) { return null; }
        var result = new List<WorldObservedRow>();
        for (var row = 0; row < Source.AuthoredState.Count; row++) {
            var declaration = Arena.Rows[row];
            if (declaration.HostOwned || (declaration.Visibility is null && !Arena.DeclaresCellVisibility(row) &&
                presented?.Contains(declaration.Name.Value) != true)) { continue; }
            if (Image(row).Observation is { } observation) { result.Add(observation); }
        }
        return result.Count == 0 ? null : result;
    }
    /// <summary>The recipient's placement projection.</summary>
    public IReadOnlyList<WorldPlacement> Placements => Observes
        ? WorldStateDisclosure.PlacementProjection(Source, Arena, recipient, Unrestricted) : [];
    /// <summary>Gets the authored spawn points the recipient's document carries at every observing ceiling: disclosure
    /// withholds state, never a spawn point, so the read composes no disclosed document.</summary>
    public IReadOnlyList<WorldSpawnPoint> SpawnPoints => Observes ? Source.SpawnPoints : [];
    /// <summary>The recipient's physical field regions.</summary>
    public IReadOnlyList<WorldFieldsSection> FieldRegions => Observes
        ? WorldStateDisclosure.FieldProjection(Source, Arena, recipient, Unrestricted) : [];
    /// <summary>A one-off presentation projection. It changes no delivery cursor.</summary>
    public WorldProjectionDocument? Project(WorldClockAnchorLedger? anchors = null, int sequence = 0, Puck.Assets.ContentAddressedStore? content = null) => !Observes ? null :
        WorldProjection.Compose(Source, WorldDisclosureTier.Presentation, authority, sequence, Arena, time(),
            recipient, Unrestricted, anchors, content: content, view: this);
    /// <summary>Advances a delivery's row images, never a read's history.</summary>
    public IReadOnlyList<WorldRowReplacement> Advance(WorldDeliveryCursor cursor, ReadOnlySpan<int> rows) => cursor.Advance(this, rows);
}
