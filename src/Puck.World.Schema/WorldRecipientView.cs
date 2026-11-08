using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A stateless capability for a recipient's live document. Its ceiling and live Observe admission apply
/// to every read. Delivery history belongs exclusively to a <see cref="WorldDeliveryCursor"/>.</summary>
public sealed class WorldRecipientView(Func<WorldDefinition> definition, Func<StateArena> arena, Principal? recipient,
    WorldDisclosureTier ceiling, Func<bool> observes, WorldDisclosureImages images, string authority,
    Func<ArenaTime> time) {
    internal WorldDefinition Source => definition();
    internal StateArena Arena => arena();
    internal Principal? Recipient => recipient;
    internal WorldDisclosureImages Images => images;
    internal bool Unrestricted => recipient?.IsAuthority == true;
    internal WorldStateDisclosure.Observer Observer => new(Arena, recipient, Unrestricted);

    /// <summary>The admission tier; no operation can raise it.</summary>
    public WorldDisclosureTier Ceiling => ceiling;
    /// <summary>Whether the capability currently discloses a document.</summary>
    public bool Observes => observes() && ceiling != WorldDisclosureTier.Frames;
    /// <summary>The document the recipient may hold at its ceiling.</summary>
    public WorldDefinition Definition {
        get {
            if (!Observes) { return WorldProjection.Undisclosed; }
            if (Unrestricted && ceiling == WorldDisclosureTier.Replica) { return WorldStateReader.Export(Source, Arena); }
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
    /// <summary>The policy's withheld metadata, never an undisclosed member count.</summary>
    public WorldWithheldRow? Withheld(string row) {
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
    internal bool Includes(int row) => Observes && (ceiling == WorldDisclosureTier.Replica ||
        (Arena.Rows[row].Visibility is not null || Arena.DeclaresCellVisibility(row) || images.Presented(Source).Contains(Arena.Rows[row].Name.Value)));
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
