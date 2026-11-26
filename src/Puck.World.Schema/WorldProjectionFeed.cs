using System.Buffers;
using Puck.Abstractions.Memory;
using Puck.Assets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>What a presentation-tier recipient is owed by one delivery.</summary>
public enum WorldProjectionDeliveryKind : byte {
    /// <summary>Nothing: the recipient already holds every member as composed.</summary>
    None,
    /// <summary>The whole projection: the recipient holds none, or a delta could not reproduce it.</summary>
    Document,
    /// <summary>A delta of the members that changed, which <see cref="WorldProjectionHold.TryApply"/> merges over the
    /// projection the recipient holds.</summary>
    Delta,
}
/// <summary>One delivery a <see cref="WorldProjectionFeed"/> owes its recipient.</summary>
/// <param name="Kind">What is owed.</param>
/// <param name="Projection">The projection the recipient holds once it takes the delivery, or <see langword="null"/>
/// when nothing is owed.</param>
/// <param name="Payload">The compact canonical bytes to send: the whole projection, or the delta; empty when nothing is
/// owed.</param>
/// <param name="ValuesOnly">Whether the delivery changes only values, never a recipient's shape: a delta of the
/// timeline, observation values or provenance that preserves observed row order and cell layout.</param>
/// <param name="TimelineOnly">Whether the delivery is a delta of the timeline alone: anchors, which a recipient takes
/// into the definition it holds without hydrating anything else.</param>
public readonly record struct WorldProjectionDelivery(WorldProjectionDeliveryKind Kind, WorldProjectionDocument? Projection, byte[] Payload, bool ValuesOnly, bool TimelineOnly = false) {
    /// <summary>Hydrated replacements for an in-process recipient that already holds the projection.</summary>
    public WorldPresentationRows? Appearance { get; init; }
    /// <summary>Gets the delivery that owes nothing.</summary>
    public static WorldProjectionDelivery Nothing => new(
        Kind: WorldProjectionDeliveryKind.None,
        Payload: [],
        Projection: null,
        ValuesOnly: true
    );
}
/// <summary>
/// One presentation-tier recipient's feed: what the authority composes for it and what it already holds. Its first
/// delivery is the whole projection, composed from the whole document. Every later delivery is a delta: a composition
/// whose document frame (<see cref="WorldDisclosureImages"/>), audience and referenced rows stand is composed from the
/// rows the arena's journal names as moved alone (<see cref="StateArena.TryReadMoves"/>), each read as the shared
/// image its audience is disclosed of it, and travels as the observation rows and cells that changed, written
/// directly; anything else is composed whole again and travels as the one document delta
/// (<see cref="WorldDocumentBasis.Diff"/>). A composition that changes nothing sends nothing.
/// Its state clocks travel as anchors (<see cref="WorldClockAnchorLedger"/>): a composition carries the anchors the
/// recipient holds while their predictions hold, and <see cref="Step(WorldDefinition, ulong, ulong, StateArena?)"/> sends a timeline delta at exactly the
/// authoritative ticks a prediction misses, so a steady sky sends nothing.
/// <para>Invariant: the projection the feed holds (<see cref="Held"/>) is the projection a whole composition of the
/// recipient's view composes at the same tick (<see cref="WorldRecipientView.Project"/>), its provenance revision and
/// its anchors aside, and every delta merges over what the recipient holds into it. What enforces it: every value
/// delivery is composed in one place, the value composition, which runs only while the frame identity, the audience key,
/// the arena, the row layout and every row a document value references (with the rows its policies read) stand as the
/// last whole composition recorded them, and which reads every row the journal names as moved since. A policy move
/// re-asks the composition's own refusals of hidden clocks and bindings. <see cref="Verify"/> composes the view whole
/// and compares, the check a law runs after every delivery.</para>
/// <para>Counts its deliveries and their bytes under <see cref="WorldProjectionWork"/>. Not thread-safe: one
/// recipient's deliveries run on the thread that steps its world.</para>
/// </summary>
/// <param name="recipient">The authenticated recipient, or <see langword="null"/> for the public observer.</param>
/// <param name="seeds">The phases a late view seeds a clock from while its row holds no number
/// (<see cref="WorldClockAnchors.Seeds"/>), or <see langword="null"/> for none.</param>
/// <param name="content">The store receiving disclosed prototype bodies, or the shared store.</param>
public sealed class WorldProjectionFeed(Principal? recipient, IReadOnlyDictionary<string, ulong>? seeds = null, ContentAddressedStore? content = null) {
    private const string ObservationsMember = "observations";
    private const string ProvenanceMember = "provenance";
    private const string SpacesMember = "spaces";

    private readonly WorldClockAnchorLedger m_anchors = new(seeds: seeds);

    private WorldProjectionDocument? m_held;
    private Puck.Abstractions.Sha256Digest? m_recipientIdentity;
    private ContentPin? m_recipientFrame;
    // What the last whole composition was composed under, which a value composition requires to stand: the arena, the
    // source, the audience key and the policy revision its refusals were asked at.
    private StateArena? m_heldArena;
    private WorldDefinition? m_heldSource;
    private string? m_heldAudienceKey;
    private long m_heldDisclosureRevision = -1L;
    // Every row a document value references, with its version and its dependencies' versions then.
    private int[] m_referencedRowOrdinals = [];
    private ulong[] m_referencedRowVersions = [];
    private ulong[] m_referencedDependencies = [];
    // Per authored row: the digest of the image its observation was last composed from (the default for a row that
    // cannot cross as one), and its index in the held observations, or -1.
    private Puck.Abstractions.Sha256Digest[] m_rowDigests = [];
    private int[] m_observedAt = [];
    // The journal place the rows were last read to.
    private long m_movesRead = -1L;
    // The rows that moved since the cursor last recorded the stream's rows, or every row when the cursor must record
    // them all (a whole composition that delivered nothing read past the moves).
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_undelivered = new();
    private bool m_cursorWhole = true;
    private readonly List<int> m_moved = [];
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_affected = new();
    private int[] m_ordered = [];
    private readonly System.Buffers.ArrayBufferWriter<byte> m_delta = new();

    /// <summary>Composes through the capability and advances only this delivery's cursor. Hidden-only changes
    /// reuse the shared row images; a read of the same view cannot seed this feed.</summary>
    /// <param name="view">The recipient's view.</param>
    /// <param name="cursor">The recipient's delivery cursor.</param>
    /// <returns>The delivery owed.</returns>
    /// <exception cref="WorldDisclosureException">The projection refuses for this recipient
    /// (<see cref="WorldProjection.Compose"/>).</exception>
    public WorldProjectionDelivery Compose(WorldRecipientView view, WorldDeliveryCursor cursor) {
        var walked = WorldDocumentWalker.MembersVisited;
        var allocated = GC.GetAllocatedBytesForCurrentThread();

        try {
            using (view.Admit()) {
                return ComposeCore(view: view, cursor: cursor);
            }
        } finally {
            WorldProjectionWork.Count(amount: (WorldDocumentWalker.MembersVisited - walked), kind: WorldProjectionWork.WalkedMembers);
            WorldProjectionWork.Count(amount: (GC.GetAllocatedBytesForCurrentThread() - allocated), kind: WorldProjectionWork.ComposedBytes);
        }
    }
    private WorldProjectionDelivery ComposeCore(WorldRecipientView view, WorldDeliveryCursor cursor) {
        if (!view.Observes) { return WorldProjectionDelivery.Nothing; }
        var identity = view.Images.DefinitionIdentity(view);
        if ((identity == m_recipientIdentity) && m_held is not null) {
            // State-clock sources need not cross as observations. Even an unchanged disclosed document can owe
            // an anchor correction, including administrative writes before another simulation tick.
            var time = view.Time;
            return Step(view, cursor, time.Tick, time.EngineTick);
        }
        var frame = view.Images.FrameIdentity(view);
        if (TryComposeValues(view: view, cursor: cursor, identity: identity, frame: frame, delivery: out var delivery)) {
            return delivery;
        }
        return ComposeWhole(view: view, cursor: cursor, identity: identity, frame: frame);
    }

    // Composes the recipient's whole projection from the whole document and diffs it against what the recipient holds:
    // its first delivery, and every one whose frame, audience, arena, layout or referenced rows moved.
    private WorldProjectionDelivery ComposeWhole(WorldRecipientView view, WorldDeliveryCursor cursor, Puck.Abstractions.Sha256Digest identity, ContentPin frame) {
        WorldProjectionWork.Count(kind: WorldProjectionWork.WholeCompositions);
        var projection = view.Project(m_anchors, m_held?.Provenance.Revision ?? 0)!;
        m_recipientIdentity = identity;
        RecordBasis(view: view, frame: frame, observations: projection.Observations);
        var payload = WorldProjection.SerializeCompact(projection);
        var tree = WorldProjectionDelta.Tree(payload);
        tree.Remove(ObservationsMember, out var nextObservations);
        var observationsChanged = !(projection.Observations ?? []).SequenceEqual(m_held?.Observations ?? []);
        JsonObject? delta = null;
        JsonNode? previousObservations = null;
        if (m_held is { } held) {
            var heldTree = HeldTree(held);
            heldTree.Remove(ObservationsMember, out previousObservations);
            delta = WorldProjectionDelta.Diff(heldTree, tree);
        }
        // The common document merge keys arrays by name/key, so a new observation array is not a replacement: diff the
        // observation trees apart, including removal markers for clocks, traits and cells that disappeared.
        var stateDelta = ((m_held is not null) && observationsChanged) ? DiffObservations(previousObservations, nextObservations) : null;
        // Reclaimed images can have new typed references with identical content. Only a semantic difference can
        // advance this stream; adopt the current shared rows even when their payload owes no delivery.
        if (delta is { Count: 0 } && (!observationsChanged || stateDelta is { Count: 0 })) {
            m_held = projection;
            m_cursorWhole = true;
            return WorldProjectionDelivery.Nothing;
        }
        cursor.Delivered(view);
        m_undelivered.Clear();
        m_cursorWhole = false;
        projection = projection with { Provenance = projection.Provenance with { Revision = checked((int)cursor.Sequence) } };
        m_anchors.Retain(projection.Timeline?.Clocks ?? []);
        if (delta is null || (observationsChanged && stateDelta is null)) {
            return Whole(projection, WorldProjection.SerializeCompact(projection));
        }
        delta[ProvenanceMember] = WorldProjectionDelta.Tree(projection.Provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        if (stateDelta is not null) {
            foreach (var (name, value) in stateDelta) { delta[name] = value?.DeepClone(); }
        }
        var valuesOnly = WorldProjectionDelta.IsValuesOnly(delta, previousObservations, nextObservations);
        m_held = projection;
        return Partial(projection, delta, valuesOnly);
    }
    // The held projection as the canonical tree the recipient holds: what a whole composition is diffed against. Only a
    // whole composition asks for it, so no tree is kept between deliveries.
    private static JsonObject HeldTree(WorldProjectionDocument held) => WorldProjectionDelta.Tree(WorldProjection.SerializeCompact(held));

    // Records what a whole composition was composed under, so that a later value composition can tell it still stands,
    // and every row's digest and observation index as the composition read them.
    private void RecordBasis(WorldRecipientView view, ContentPin frame, IReadOnlyList<WorldObservedRow>? observations) {
        m_recipientFrame = frame;
        var arena = view.Arena;
        if (arena.Journal.Scopes != 0) {
            // A composition inside an open scope reads a store a rewind may take back: no value composition follows it.
            m_heldArena = null;
            m_heldAudienceKey = null;
            return;
        }
        var source = view.Source;
        var images = view.Images;
        var audience = images.Audience(view);
        m_heldArena = arena;
        m_heldSource = source;
        m_heldAudienceKey = audience.Text;
        m_heldDisclosureRevision = arena.DisclosureRevision;
        m_movesRead = arena.MoveSequence;
        RecordReferences(view: view, source: source);
        var count = source.AuthoredState.Count;
        m_rowDigests = new Puck.Abstractions.Sha256Digest[count];
        m_observedAt = new int[count];
        var presented = images.Presented(source);
        var next = 0;
        for (var row = 0; row < count; row++) {
            m_observedAt[row] = -1;
            if (!view.ObservesRow(row, presented)) { continue; }
            m_rowDigests[row] = images.RowDigest(audience, row);
            // The composition's observations are in row order, each a row ObservesRow admits whose image observes.
            if ((observations is not null) && (next < observations.Count) &&
                string.Equals(observations[next].Name, arena.Rows[row].Name.Value, StringComparison.Ordinal)) {
                m_observedAt[row] = next++;
            }
        }
    }
    private void RecordReferences(WorldRecipientView view, WorldDefinition source) {
        var arena = view.Arena;
        var names = WorldStateDocumentValues.ReferencedRows(definition: source);
        var ordinals = new List<int>(capacity: names.Count);
        foreach (var name in names) {
            if (arena.Catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle)) { ordinals.Add(item: handle.Ordinal); }
        }
        ordinals.Sort();
        m_referencedRowOrdinals = [.. ordinals];
        m_referencedRowVersions = new ulong[m_referencedRowOrdinals.Length];
        m_referencedDependencies = new ulong[m_referencedRowOrdinals.Length];
        for (var index = 0; index < m_referencedRowOrdinals.Length; index++) {
            (m_referencedRowVersions[index], m_referencedDependencies[index]) = view.Images.Versions(view, m_referencedRowOrdinals[index]);
        }
    }
    // Whether every row a document value of the source references, and every row its policies read, stands as the last
    // whole composition flattened and checked them: a referenced row's value is flattened into the frame, and its
    // readability gates the composition.
    private bool ReferencedRowsStand(WorldRecipientView view, WorldDefinition source) {
        if (!ReferenceEquals(source, m_heldSource)) {
            var names = WorldStateDocumentValues.ReferencedRows(definition: source);
            if (names.Count != m_referencedRowOrdinals.Length) { return false; }
            foreach (var name in names) {
                if (!view.Arena.Catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle) ||
                    (Array.BinarySearch(m_referencedRowOrdinals, handle.Ordinal) < 0)) { return false; }
            }
        }
        for (var index = 0; index < m_referencedRowOrdinals.Length; index++) {
            var (version, dependencies) = view.Images.Versions(view, m_referencedRowOrdinals[index]);
            if ((version != m_referencedRowVersions[index]) || (dependencies != m_referencedDependencies[index])) { return false; }
        }
        return true;
    }

    // The value composition: composes the observations, spaces and timeline of a projection whose frame, audience,
    // arena, row layout and referenced rows stand, from the rows the journal names as moved since the last composition,
    // and delivers the rows and cells that changed. False when anything else moved, which only a whole composition
    // answers.
    private bool TryComposeValues(WorldRecipientView view, WorldDeliveryCursor cursor, Puck.Abstractions.Sha256Digest identity, ContentPin frame, out WorldProjectionDelivery delivery) {
        delivery = WorldProjectionDelivery.Nothing;
        var arena = view.Arena;
        var source = view.Source;
        if ((m_held is not { } held) || (arena.Journal.Scopes != 0) || !ReferenceEquals(arena, m_heldArena) ||
            (frame != m_recipientFrame) || (source.AuthoredState.Count != m_rowDigests.Length)) {
            return false;
        }
        var images = view.Images;
        var audience = images.Audience(view);
        if (!string.Equals(audience.Text, m_heldAudienceKey, StringComparison.Ordinal) || !ReferencedRowsStand(view: view, source: source)) {
            return false;
        }
        WorldProjectionWork.Count(kind: WorldProjectionWork.Compositions);
        if (arena.DisclosureRevision != m_heldDisclosureRevision) {
            // A policy, membership or audience write: what the frame and the referenced rows decide stands, but whether
            // the recipient may still read a keyed clock or a bound cell is asked again, as a composition asks it.
            WorldProjection.RefuseHidden(definition: source, arena: arena, recipient: view.Recipient, unrestricted: view.Unrestricted);
            m_heldDisclosureRevision = arena.DisclosureRevision;
        }

        // The rows that can have moved: every row the journal names since the last read and every row whose policies
        // read one, or every row when the journal cannot name them or the source was replaced.
        var count = m_rowDigests.Length;
        m_moved.Clear();
        m_affected.Clear();
        var named = arena.TryReadMoves(ref m_movesRead, m_moved);
        var every = !named || !ReferenceEquals(source, m_heldSource);
        m_heldSource = source;
        ReadOnlySpan<int> rows;
        if (every) {
            ArrayCapacity.EnsureScratch(ref m_ordered, count);
            for (var row = 0; row < count; row++) { m_ordered[row] = row; }
            rows = m_ordered.AsSpan(0, count);
        } else {
            images.Affected(view, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(m_moved), m_affected);
            ArrayCapacity.EnsureScratch(ref m_ordered, m_affected.Count);
            m_affected.Members.CopyTo(m_ordered);
            var ordered = m_ordered.AsSpan(0, m_affected.Count);
            ordered.Sort();
            rows = ordered;
        }
        WorldProjectionWork.Count(amount: rows.Length, kind: WorldProjectionWork.RowsRead);

        var presented = images.Presented(source);
        var observations = held.Observations ?? [];
        WorldObservedRow[]? next = null;
        List<int>? replaced = null;
        var reshaped = false;
        foreach (var row in rows) {
            if (row >= count) { break; }
            if (!m_cursorWhole) { m_undelivered.Add(row); }
            var observes = view.ObservesRow(row, presented);
            var digest = observes ? images.RowDigest(audience, row) : default;
            if (digest == m_rowDigests[row]) { continue; }
            m_rowDigests[row] = digest;
            var observation = observes ? images.Image(audience, row).Observation : null;
            var at = m_observedAt[row];
            if ((at >= 0) && (observation is not null) && string.Equals(observation.Space, observations[at].Space, StringComparison.Ordinal)) {
                if (SameObservation(observation, observations[at])) { continue; }
                next ??= [.. observations];
                next[at] = observation;
                (replaced ??= []).Add(at);
            } else if ((at >= 0) || (observation is not null)) {
                reshaped = true;
                (m_reshapedRows ??= []).Add((row, observation));
            }
        }
        if (reshaped) {
            return TryComposeReshaped(view, cursor, identity, held, next ?? [.. observations], out delivery);
        }

        var time = view.Time;
        var timeline = WorldProjection.ComposeTimeline(definition: source, anchors: m_anchors, arena: arena, time: in time);
        var timelineChanged = !SameTimeline(timeline, held.Timeline);
        var nextObservations = ((next is null) ? held.Observations : next);
        if ((replaced is null) && !timelineChanged) {
            m_recipientIdentity = identity;
            m_held = held with { Observations = nextObservations, Timeline = timeline };
            return true;
        }

        DeliverRows(view: view, cursor: cursor);
        var provenance = held.Provenance with { Revision = checked((int)cursor.Sequence) };
        var valuesOnly = true;
        m_delta.ResetWrittenCount();
        m_delta.Write("{"u8);
        if (replaced is not null) {
            m_delta.Write("\"observations\":["u8);
            for (var index = 0; index < replaced.Count; index++) {
                if (index != 0) { m_delta.Write(","u8); }
                var at = replaced[index];
                valuesOnly &= WriteObservationPatch(before: observations[at], after: next![at]);
            }
            m_delta.Write("],"u8);
        }
        m_delta.Write("\"provenance\":"u8);
        m_delta.Write(CanonicalJsonDocument.SerializeCompact(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>()));
        if (timelineChanged) {
            m_delta.Write(",\"timeline\":"u8);
            WriteTimeline(held.Timeline, timeline);
        }
        m_delta.Write("}"u8);

        var nextProjection = held with { Provenance = provenance, Observations = nextObservations, Timeline = timeline };
        m_anchors.Retain(nextProjection.Timeline?.Clocks ?? []);
        m_recipientIdentity = identity;
        m_held = nextProjection;
        delivery = Partial(projection: nextProjection, payload: m_delta.WrittenSpan.ToArray(), valuesOnly: valuesOnly, timelineOnly: (replaced is null));
        return true;
    }
    // The rows a value composition found entering or leaving the observations, by row, with the observation entering.
    private List<(int Row, WorldObservedRow? Observation)>? m_reshapedRows;

    // A value composition whose observed rows changed in number or order (a row turning readable, or no longer
    // crossing): the observations are composed again in row order and travel as the document delta of the observation
    // trees, with the spaces they name. The frame still stands, so nothing else is composed.
    private bool TryComposeReshaped(WorldRecipientView view, WorldDeliveryCursor cursor, Puck.Abstractions.Sha256Digest identity,
        WorldProjectionDocument held, WorldObservedRow[] current, out WorldProjectionDelivery delivery) {
        delivery = WorldProjectionDelivery.Nothing;
        var source = view.Source;
        var arena = view.Arena;
        var count = m_rowDigests.Length;
        var entering = new Dictionary<int, WorldObservedRow?>();
        foreach (var (row, observation) in m_reshapedRows!) { entering[row] = observation; }
        m_reshapedRows.Clear();
        var rebuilt = new List<WorldObservedRow>();
        for (var row = 0; row < count; row++) {
            WorldObservedRow? observation;
            if (entering.TryGetValue(row, out var moved)) { observation = moved; }
            else if (m_observedAt[row] >= 0) { observation = current[m_observedAt[row]]; }
            else { continue; }
            m_observedAt[row] = -1;
            if (observation is null) { continue; }
            m_observedAt[row] = rebuilt.Count;
            rebuilt.Add(observation);
        }
        IReadOnlyList<WorldObservedRow>? observations = (rebuilt.Count == 0) ? null : rebuilt;
        var spaces = WorldProjection.SpacesOf(observations: observations, spaces: source.StateRaw?.Spaces);
        var time = view.Time;
        var timeline = WorldProjection.ComposeTimeline(definition: source, anchors: m_anchors, arena: arena, time: in time);

        var previousObservations = ((held.Observations is { } previous) ? WorldProjectionDelta.Tree(previous.ToArray(), WorldJsonContext.Default.WorldObservedRowArray) : null);
        var nextObservations = ((observations is { } next) ? WorldProjectionDelta.Tree(next.ToArray(), WorldJsonContext.Default.WorldObservedRowArray) : null);
        var delta = DiffObservations(previousObservations, nextObservations);
        if (delta is null) { return false; }
        if (!Puck.Maths.SequenceEqualityComparer<StateSpace>.Instance.Equals(x: held.Spaces, y: spaces)) {
            var spacesDelta = WorldProjectionDelta.Diff(
                held: (held.Spaces is { } heldSpaces) ? new JsonObject { [SpacesMember] = WorldProjectionDelta.Tree(heldSpaces, WorldJsonContext.Default.For<IReadOnlyList<StateSpace>>()) } : [],
                next: (spaces is { } nextSpaces) ? new JsonObject { [SpacesMember] = WorldProjectionDelta.Tree(nextSpaces, WorldJsonContext.Default.For<StateSpace[]>()) } : []);
            if (spacesDelta is null) { return false; }
            foreach (var (name, value) in spacesDelta) { delta[name] = value?.DeepClone(); }
        }
        if (!SameTimeline(timeline, held.Timeline)) {
            delta[WorldProjectionDelta.Timeline] = ((timeline is null) ? null : WorldProjectionDelta.Tree(timeline: timeline));
        }

        DeliverRows(view: view, cursor: cursor);
        var provenance = held.Provenance with { Revision = checked((int)cursor.Sequence) };
        delta[ProvenanceMember] = WorldProjectionDelta.Tree(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        var nextProjection = held with { Provenance = provenance, Observations = observations, Spaces = spaces, Timeline = timeline };
        m_anchors.Retain(nextProjection.Timeline?.Clocks ?? []);
        m_recipientIdentity = identity;
        m_held = nextProjection;
        delivery = Partial(nextProjection, delta, WorldProjectionDelta.IsValuesOnly(delta, previousObservations, nextObservations));
        return true;
    }

    // Records on the cursor the rows this delivery's stream holds now: the rows that moved since it last recorded them,
    // or every row when it must record them all.
    private void DeliverRows(WorldRecipientView view, WorldDeliveryCursor cursor) {
        if (m_cursorWhole) {
            cursor.Delivered(view);
        } else {
            ArrayCapacity.EnsureScratch(ref m_ordered, m_undelivered.Count);
            m_undelivered.Members.CopyTo(m_ordered);
            var rows = m_ordered.AsSpan(0, m_undelivered.Count);
            rows.Sort();
            cursor.Delivered(view, rows);
        }
        m_undelivered.Clear();
        m_cursorWhole = false;
    }

    private static JsonObject? DiffObservations(JsonNode? previous, JsonNode? next) {
        var before = new JsonObject();
        var after = new JsonObject();
        if (previous is not null) { before[ObservationsMember] = previous.DeepClone(); }
        if (next is not null) { after[ObservationsMember] = next.DeepClone(); }
        return WorldProjectionDelta.Diff(held: before, next: after);
    }

    private static bool SameTimeline(WorldTimelineSection? left, WorldTimelineSection? right) =>
        ((left is null) == (right is null)) && (left?.Clocks ?? []).SequenceEqual(right?.Clocks ?? []);

    // Writes one observation row's patch: its changed cells, each replacing the cell of its key, when the row kept its
    // shape and every member but its cells; otherwise the whole row, replacing the row of its name. Answers whether the
    // patch moves values only.
    private bool WriteObservationPatch(WorldObservedRow before, WorldObservedRow after) {
        var sameShape = WorldProjectionDelta.SameObservationRowShape(left: before, right: after);
        if (!sameShape || (before.HiddenCount != 0) || (after.HiddenCount != 0) || HoldsHidden(after) ||
            ((after with { Cells = before.Cells }) != before)) {
            WriteReplaced(CanonicalJsonDocument.SerializeCompact(after, WorldJsonContext.Default.For<WorldObservedRow>()));
            return sameShape;
        }
        m_delta.Write("{\"name\":"u8);
        m_delta.Write(CanonicalJsonDocument.SerializeCompact(after.Name, WorldJsonContext.Default.For<string>()));
        m_delta.Write(",\"cells\":["u8);
        var first = true;
        for (var index = 0; index < after.Cells.Count; index++) {
            if (after.Cells[index] == before.Cells[index]) { continue; }
            if (!first) { m_delta.Write(","u8); }
            first = false;
            WriteReplaced(CanonicalJsonDocument.SerializeCompact(after.Cells[index], WorldJsonContext.Default.For<WorldObservedCell>()));
        }
        m_delta.Write("]}"u8);
        return true;
    }
    // Whether two observations of one row carry the same content: a digest also moves with replica cells an
    // observation does not carry.
    private static bool SameObservation(WorldObservedRow left, WorldObservedRow right) =>
        ((left with { Cells = right.Cells }) == right) && left.Cells.SequenceEqual(right.Cells);
    private static bool HoldsHidden(WorldObservedRow row) {
        foreach (var cell in row.Cells) { if (cell.Hidden) { return true; } }
        return false;
    }
    // Writes an object as a delta that replaces its target whole (WorldDocumentBasis.ReplaceMemberName).
    private void WriteReplaced(byte[] value) {
        if (value.Length <= 2) { m_delta.Write("{\"$replace\":true}"u8); return; }
        m_delta.Write("{\"$replace\":true,"u8);
        m_delta.Write(value.AsSpan(1));
    }
    // Writes a timeline's delta: the clocks that changed, each replacing the clock of its name, when the timeline kept
    // its clocks in number, name and order and every member but its clocks; otherwise the whole timeline.
    private void WriteTimeline(WorldTimelineSection? previous, WorldTimelineSection? timeline) {
        if (timeline is null) { m_delta.Write("null"u8); return; }
        if ((previous?.Clocks is { } before) && (timeline.Clocks is { } after) && (before.Count == after.Count) &&
            ((previous with { Clocks = after }) == timeline)) {
            var changed = 0;
            for (var index = 0; index < after.Count; index++) {
                if ((before[index] is not { } held) || (after[index] is not { } clock) || !string.Equals(held.Name, clock.Name, StringComparison.Ordinal)) {
                    changed = -1;
                    break;
                }
                if (held != clock) { changed++; }
            }
            if (changed > 0) {
                m_delta.Write("{\"clocks\":["u8);
                var first = true;
                for (var index = 0; index < after.Count; index++) {
                    var (held, clock) = (before[index]!, after[index]!);
                    if (held == clock) { continue; }
                    if (!first) { m_delta.Write(","u8); }
                    first = false;
                    if ((clock with { Anchor = held.Anchor }) == held) {
                        // An anchor correction, the common step: the clock's anchor alone, replacing the one held.
                        m_delta.Write("{\"name\":"u8);
                        m_delta.Write(CanonicalJsonDocument.SerializeCompact(clock.Name, WorldJsonContext.Default.For<string>()));
                        m_delta.Write(",\"anchor\":"u8);
                        if (clock.Anchor is { } anchor) {
                            WriteReplaced(CanonicalJsonDocument.SerializeCompact(anchor, WorldJsonContext.Default.For<WorldClockAnchor>()));
                        } else {
                            m_delta.Write("null"u8);
                        }
                        m_delta.Write("}"u8);
                        continue;
                    }
                    WriteReplaced(CanonicalJsonDocument.SerializeCompact(clock, WorldJsonContext.Default.For<WorldClock>()));
                }
                m_delta.Write("]}"u8);
                return;
            }
        }
        WriteReplaced(CanonicalJsonDocument.SerializeCompact(timeline, WorldJsonContext.Default.WorldTimelineSection));
    }

    /// <summary>Composes the view's whole projection and compares it with the projection the feed holds, provenance
    /// and timeline aside: the feed's invariant (a value composition holds what a whole composition composes), which a
    /// law asks after every delivery. Changes no state of the feed or its cursor.</summary>
    /// <param name="view">The recipient's view.</param>
    /// <returns>The first member that differs, or <see langword="null"/> when the held projection is the whole
    /// composition's, or when nothing is held.</returns>
    public string? Verify(WorldRecipientView view) {
        if ((m_held is not { } held) || !view.Observes) { return null; }
        var whole = view.Project(anchors: null, sequence: held.Provenance.Revision)!;
        var expected = WorldProjectionDelta.Tree(WorldProjection.SerializeCompact(whole with { Provenance = held.Provenance, Timeline = null }));
        var actual = WorldProjectionDelta.Tree(WorldProjection.SerializeCompact(held with { Timeline = null }));
        foreach (var (name, value) in expected) {
            if (!JsonNode.DeepEquals(value, actual[name])) { return name; }
        }
        foreach (var (name, _) in actual) {
            if (!expected.ContainsKey(name)) { return name; }
        }
        return null;
    }

    /// <summary>Advances a primed projection from changed presentation rows, falling back to a disclosure composition
    /// when state or policy also moved.</summary>
    /// <param name="view">The recipient's view.</param>
    /// <param name="cursor">The recipient's delivery cursor.</param>
    /// <param name="rows">The presentation rows the edit changed.</param>
    /// <returns>The delivery owed.</returns>
    public WorldProjectionDelivery ComposeAppearance(WorldRecipientView view, WorldDeliveryCursor cursor, WorldPresentationRows rows) {
        using var hold = view.Admit();
        if (!view.Observes) { return WorldProjectionDelivery.Nothing; }
        if (rows.ChangesProjectionDependencies || m_held is not { } held ||
            !view.Images.TryAdvanceAppearance(view, m_recipientFrame, m_recipientIdentity, out var frame, out _, out var identity)) {
            return Compose(view, cursor);
        }
        if (frame == m_recipientFrame) { return WorldProjectionDelivery.Nothing; }

        var changedReferences = new JsonArray();
        var references = held.Creations.ToArray();
        var replacements = new List<WorldPresentationCreation>();
        var store = content ?? WorldProjectionContent.Shared;
        foreach (var (ordinal, row) in rows.Creations) {
            var flat = WorldProjection.FlattenPrototype(view.Source, view.Arena, view.Recipient, view.Unrestricted, row);
            var reference = WorldProjectionContent.Put(flat, store);
            if (references[ordinal] == reference) { continue; }
            references[ordinal] = reference;
            replacements.Add(new(ordinal, flat));
            changedReferences.Add(JsonSerializer.SerializeToNode(reference, WorldJsonContext.Default.For<WorldPrototypeReference>()));
        }

        var delta = new JsonObject();
        if (changedReferences.Count != 0) { delta["prototypes"] = changedReferences; }
        if (rows.Shades.Length != 0 || rows.HasShades != (held.Shades is not null)) {
            if (!rows.HasShades) {
                delta["shades"] = null;
            } else {
                var shades = new JsonObject();
                foreach (var (name, shade) in rows.Shades) {
                    shades[name] = shade is null ? null : JsonSerializer.SerializeToNode(shade, WorldJsonContext.Default.For<Puck.SignedDistance.SdfShadeDefinition>());
                }
                delta["shades"] = shades;
            }
        }
        m_recipientFrame = frame;
        m_recipientIdentity = identity;
        if (delta.Count == 0) { return WorldProjectionDelivery.Nothing; }

        cursor.Delivered();
        var provenance = held.Provenance with { Revision = checked((int)cursor.Sequence) };
        delta[ProvenanceMember] = JsonSerializer.SerializeToNode(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        m_held = held with { Creations = references, Provenance = provenance, Shades = view.Source.ShadesRaw };
        return Partial(m_held, delta, valuesOnly: false) with {
            Appearance = new([.. replacements], rows.Shades, HasCreations: true, rows.HasShades)
        };
    }

    /// <summary>Delivers anchor corrections at this stream's sequence.</summary>
    /// <param name="view">The recipient's view.</param>
    /// <param name="cursor">The recipient's delivery cursor.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns>The delivery owed: a delta of the timeline and the provenance, or nothing.</returns>
    public WorldProjectionDelivery Step(WorldRecipientView view, WorldDeliveryCursor cursor, ulong tick, ulong engineTick) {
        if (StepTimeline(view.Source, tick, engineTick, view.Arena) is not { } timeline) { return WorldProjectionDelivery.Nothing; }
        cursor.Delivered();
        return StepDelta(timeline: timeline, provenance: m_held!.Provenance with { Revision = checked((int)cursor.Sequence) });
    }
    // The delta a step owes: the timeline's changed clocks, and the provenance that numbers the delivery when a cursor
    // numbered it.
    private WorldProjectionDelivery StepDelta(WorldTimelineSection timeline, WorldProjectionProvenance? provenance) {
        var previous = m_held!.Timeline;
        m_held = m_held with { Provenance = provenance ?? m_held.Provenance, Timeline = timeline };
        m_delta.ResetWrittenCount();
        m_delta.Write("{"u8);
        if (provenance is not null) {
            m_delta.Write("\"provenance\":"u8);
            m_delta.Write(CanonicalJsonDocument.SerializeCompact(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>()));
            m_delta.Write(","u8);
        }
        m_delta.Write("\"timeline\":"u8);
        WriteTimeline(previous, timeline);
        m_delta.Write("}"u8);
        return Partial(projection: m_held, payload: m_delta.WrittenSpan.ToArray(), valuesOnly: true, timelineOnly: true);
    }

    /// <summary>Gets the anchors the recipient holds.</summary>
    public WorldClockAnchorLedger Anchors => m_anchors;
    /// <summary>Gets the projection the recipient holds, or <see langword="null"/> before its first delivery and after
    /// <see cref="Release"/>.</summary>
    public WorldProjectionDocument? Held => m_held;

    /// <summary>Composes the recipient's projection of a delivered document and answers what it is owed.</summary>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="authority">The composing authority's addressable namespace.</param>
    /// <param name="revision">The document revision this composition names.</param>
    /// <param name="arena">The authority's live store.</param>
    /// <param name="time">The authoritative tick the composition stands at.</param>
    /// <returns>The delivery owed.</returns>
    /// <exception cref="WorldDisclosureException">The projection refuses for this recipient
    /// (<see cref="WorldProjection.Compose"/>).</exception>
    /// <exception cref="InvalidOperationException">The composed projection does not round-trip or flatten.</exception>
    public WorldProjectionDelivery Compose(WorldDefinition definition, string authority, int revision, StateArena arena, in ArenaTime time) {
        var projection = WorldProjection.Compose(
            content: content,
            anchors: m_anchors,
            arena: arena,
            authority: authority,
            definition: definition,
            recipient: recipient,
            revision: revision,
            tier: WorldDisclosureTier.Presentation,
            time: in time
        )!;

        m_anchors.Retain(clocks: (projection.Timeline?.Clocks ?? []));
        var payload = WorldProjection.SerializeCompact(projection: projection);

        if (m_held is not { } heldProjection) {
            return Whole(projection: projection, payload: payload);
        }

        var held = HeldTree(heldProjection);
        var tree = WorldProjectionDelta.Tree(utf8Json: payload);
        var delta = WorldProjectionDelta.Diff(held: held, next: tree);

        if (delta is null) {
            return Whole(projection: projection, payload: payload);
        }

        var valuesOnly = WorldProjectionDelta.IsValuesOnly(
            delta: delta,
            previousObservations: held[ObservationsMember],
            nextObservations: tree[ObservationsMember]
        );

        m_held = projection;

        return ((delta.Count == 0)
            ? WorldProjectionDelivery.Nothing
            : Partial(
                delta: delta,
                projection: projection,
                valuesOnly: valuesOnly
            ));
    }
    /// <summary>Steps the recipient's anchors at an authoritative tick: a timeline delta when a held prediction misses
    /// the authority's phase, and nothing otherwise. Observations need no step: each carries the trait that moves it,
    /// which the recipient evaluates, and only a write, which composes, changes one.</summary>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="arena">The live arena, or null at a seed boundary.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns>The delivery owed.</returns>
    public WorldProjectionDelivery Step(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena = null) {
        return (StepTimeline(definition, tick, engineTick, arena) is { } timeline) ? StepDelta(timeline: timeline, provenance: null) : WorldProjectionDelivery.Nothing;
    }
    // The timeline a step owes the recipient, or null when every held prediction stands: each anchored clock carries
    // what the ledger holds, which is nothing once a stale anchor was dropped with no seed to replace it.
    private WorldTimelineSection? StepTimeline(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena) {
        if (
            (m_held?.Timeline is not { Clocks: { } clocks } timeline) ||
            !m_anchors.Step(
            arena: arena,
            definition: definition,
            engineTick: engineTick,
            tick: tick
        )
        ) {
            return null;
        }

        var carried = new WorldClock[clocks.Count];

        for (var index = 0; (index < carried.Length); index++) {
            var clock = clocks[index];

            carried[index] = (clock.IsAnchored
                ? (clock with {
                    Anchor = (m_anchors.TryHeld(
                        anchor: out var anchor,
                        clock: clock.Name
                    )
                        ? anchor
                        : null),
                })
                : clock);
        }

        return (timeline with { Clocks = carried });
    }
    /// <summary>Releases everything held for the recipient, its anchor rows included: it left or lost disclosure, and
    /// its next delivery is a whole projection.</summary>
    public void Release() {
        m_anchors.Release();
        m_held = null;
        m_recipientIdentity = null;
        m_recipientFrame = null;
        m_heldArena = null;
        m_heldSource = null;
        m_heldAudienceKey = null;
        m_heldDisclosureRevision = -1L;
        m_referencedRowOrdinals = [];
        m_referencedRowVersions = [];
        m_referencedDependencies = [];
        m_rowDigests = [];
        m_observedAt = [];
        m_movesRead = -1L;
        m_undelivered.Clear();
        m_cursorWhole = true;
        m_reshapedRows?.Clear();
    }

    private WorldProjectionDelivery Partial(WorldProjectionDocument projection, JsonObject delta, bool valuesOnly) =>
        Partial(projection: projection, payload: CanonicalJsonDocument.SerializeCompact(node: delta), valuesOnly: valuesOnly, timelineOnly: WorldProjectionDelta.IsTimelineOnly(delta: delta));
    private static WorldProjectionDelivery Partial(WorldProjectionDocument projection, byte[] payload, bool valuesOnly, bool timelineOnly) {
        WorldProjectionWork.Count(kind: WorldProjectionWork.Deltas);
        WorldProjectionWork.Count(
            amount: payload.Length,
            kind: WorldProjectionWork.Bytes
        );

        return new WorldProjectionDelivery(
            Kind: WorldProjectionDeliveryKind.Delta,
            Payload: payload,
            Projection: projection,
            TimelineOnly: timelineOnly,
            ValuesOnly: valuesOnly
        );
    }
    private WorldProjectionDelivery Whole(WorldProjectionDocument projection, byte[] payload) {
        m_held = projection;
        WorldProjectionWork.Count(kind: WorldProjectionWork.Documents);
        WorldProjectionWork.Count(
            amount: payload.Length,
            kind: WorldProjectionWork.Bytes
        );

        return new WorldProjectionDelivery(
            Kind: WorldProjectionDeliveryKind.Document,
            Payload: payload,
            Projection: projection,
            ValuesOnly: false
        );
    }
}
