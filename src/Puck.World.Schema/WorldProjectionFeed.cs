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
/// delivery is the whole projection; every later composition travels as the delta of the members that changed, by the
/// one document delta (<see cref="WorldDocumentBasis.Diff"/>), and a composition that changes nothing sends nothing.
/// Its state clocks travel as anchors (<see cref="WorldClockAnchorLedger"/>): a composition carries the anchors the
/// recipient holds while their predictions hold, and <see cref="Step(WorldDefinition, ulong, ulong, StateArena?)"/> sends a timeline delta at exactly the
/// authoritative ticks a prediction misses, so a steady sky sends nothing.
/// <para>Counts its deliveries and their bytes under <see cref="WorldProjectionWork"/>. Not thread-safe: one
/// recipient's deliveries run on the thread that steps its world.</para>
/// </summary>
/// <param name="recipient">The authenticated recipient, or <see langword="null"/> for the public observer.</param>
/// <param name="seeds">The phases a late view seeds a clock from while its row holds no number
/// (<see cref="WorldClockAnchors.Seeds"/>), or <see langword="null"/> for none.</param>
/// <param name="content">The store receiving disclosed prototype bodies, or the shared store.</param>
public sealed class WorldProjectionFeed(Principal? recipient, IReadOnlyDictionary<string, ulong>? seeds = null, ContentAddressedStore? content = null) {
    private readonly WorldClockAnchorLedger m_anchors = new(seeds: seeds);

    private WorldProjectionDocument? m_held;
    private JsonObject? m_tree;
    private Puck.Abstractions.Sha256Digest? m_recipientIdentity;
    private ContentPin? m_recipientFrame;
    private StateArena? m_heldArena;
    private long m_heldDisclosureRevision = -1L;
    private string? m_heldAudienceKey;
    private WorldDefinition? m_referencedSource;
    private int[] m_referencedRowOrdinals = [];
    private ulong[] m_referencedRowVersions = [];

    /// <summary>Composes through the capability and advances only this delivery's cursor. Hidden-only changes
    /// reuse the shared row images; a read of the same view cannot seed this feed.</summary>
    public WorldProjectionDelivery Compose(WorldRecipientView view, WorldDeliveryCursor cursor) {
        if (!view.Observes) { return WorldProjectionDelivery.Nothing; }
        var identity = view.Images.DefinitionIdentity(view);
        if ((identity == m_recipientIdentity) && m_held is not null) {
            // State-clock sources need not cross as observations. Even an unchanged disclosed document can owe
            // an anchor correction, including administrative writes before another simulation tick.
            var time = view.Time;
            return Step(view, cursor, time.Tick, time.EngineTick);
        }
        var frame = view.Images.FrameIdentity(view);
        if (TryComposeStateOnly(view: view, cursor: cursor, identity: identity, frame: frame, delivery: out var stateDelivery)) {
            return stateDelivery;
        }
        m_recipientIdentity = identity;
        RecordBasis(view: view, frame: frame);
        var projection = view.Project(m_anchors, m_held?.Provenance.Revision ?? 0)!;
        var payload = WorldProjection.SerializeCompact(projection);
        var tree = WorldProjectionDelta.Tree(payload);
        tree.Remove("observations", out var nextObservations);
        var observationsChanged = !(projection.Observations ?? []).SequenceEqual(m_held?.Observations ?? []);
        var delta = m_tree is null ? null : WorldProjectionDelta.Diff(m_tree, tree);
        // Retain state only as shared typed row images, never as a JSON tree per spectator. The common document
        // merge keys arrays by name/key, so a new observation array is not a replacement: diff the transient state
        // trees too, including removal markers for clocks, traits and cells that disappeared.
        var previousObservations = observationsChanged && m_held?.Observations is { } previous
            ? WorldProjectionDelta.Tree(previous.ToArray(), WorldJsonContext.Default.WorldObservedRowArray) : null;
        JsonObject? stateDelta = null;
        if (m_tree is not null && observationsChanged) {
            var before = new JsonObject();
            var after = new JsonObject();
            if (previousObservations is not null) { before["observations"] = previousObservations.DeepClone(); }
            if (nextObservations is not null) { after["observations"] = nextObservations.DeepClone(); }
            stateDelta = WorldProjectionDelta.Diff(before, after);
        }
        // Reclaimed images can have new typed references with identical content. Only a semantic difference can
        // advance this stream; adopt the current shared rows even when their payload owes no delivery.
        if (delta is { Count: 0 } && (!observationsChanged || stateDelta is { Count: 0 })) {
            m_held = projection;
            return WorldProjectionDelivery.Nothing;
        }
        cursor.Delivered(view);
        projection = projection with { Provenance = projection.Provenance with { Revision = checked((int)cursor.Sequence) } };
        m_anchors.Retain(projection.Timeline?.Clocks ?? []);
        if (delta is null || (observationsChanged && stateDelta is null)) {
            payload = WorldProjection.SerializeCompact(projection);
            tree = WorldProjectionDelta.Tree(payload);
            tree.Remove("observations", out _);
            return Whole(projection, tree, payload);
        }
        var provenanceNode = WorldProjectionDelta.Tree(projection.Provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        tree["provenance"] = provenanceNode;
        delta["provenance"] = provenanceNode.DeepClone();
        if (stateDelta is not null) {
            foreach (var (name, value) in stateDelta) { delta[name] = value?.DeepClone(); }
        }
        var valuesOnly = WorldProjectionDelta.IsValuesOnly(delta, previousObservations, nextObservations);
        m_held = projection; m_tree = tree;
        return Partial(projection, delta, valuesOnly);
    }

    private void RecordBasis(WorldRecipientView view, ContentPin frame) {
        m_recipientFrame = frame;
        var arena = view.Arena;
        if (arena.Journal.Scopes != 0) {
            m_heldArena = null;
            m_heldAudienceKey = null;
            m_heldDisclosureRevision = -1L;
            return;
        }
        var source = view.Source;
        if (!ReferenceEquals(m_referencedSource, source) || !ReferenceEquals(m_heldArena, arena)) {
            var names = WorldStateDocumentValues.ReferencedRows(definition: source);
            if (names.Count == 0) {
                m_referencedRowOrdinals = [];
                m_referencedRowVersions = [];
            } else {
                var ordinals = new List<int>(capacity: names.Count);
                foreach (var name in names) {
                    if (arena.Catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle)) {
                        ordinals.Add(item: handle.Ordinal);
                    }
                }
                m_referencedRowOrdinals = [.. ordinals];
                m_referencedRowVersions = new ulong[m_referencedRowOrdinals.Length];
            }
            m_referencedSource = source;
        }
        for (var index = 0; (index < m_referencedRowOrdinals.Length); index++) {
            m_referencedRowVersions[index] = arena.RowVersion(rowOrdinal: m_referencedRowOrdinals[index]);
        }
        m_heldArena = arena;
        m_heldDisclosureRevision = arena.DisclosureRevision;
        m_heldAudienceKey = view.AudienceKey;
    }

    private bool ReferencedRowsStand(WorldDefinition source, StateArena arena) {
        var names = WorldStateDocumentValues.ReferencedRows(definition: source);
        if (names.Count == 0) { return true; }
        if (!ReferenceEquals(m_referencedSource, source) && (m_referencedRowOrdinals.Length != names.Count)) {
            return false;
        }
        for (var index = 0; (index < m_referencedRowOrdinals.Length); index++) {
            if (arena.RowVersion(rowOrdinal: m_referencedRowOrdinals[index]) != m_referencedRowVersions[index]) {
                return false;
            }
        }
        m_referencedSource = source;
        return true;
    }

    // Composes only observations, spaces and timeline when the disclosed frame, audience and every state cell bound by a
    // document-value reference stand: avoids re-projecting, serializing and diffing unchanged megabyte sections per tick.
    private bool TryComposeStateOnly(WorldRecipientView view, WorldDeliveryCursor cursor, Puck.Abstractions.Sha256Digest identity, ContentPin frame, out WorldProjectionDelivery delivery) {
        delivery = WorldProjectionDelivery.Nothing;
        var arena = view.Arena;
        if ((m_held is not { } held) || (m_tree is not { } tree) || (arena.Journal.Scopes != 0) ||
            (frame != m_recipientFrame) || !ReferenceEquals(arena, m_heldArena) ||
            (arena.DisclosureRevision != m_heldDisclosureRevision) ||
            !string.Equals(view.AudienceKey, m_heldAudienceKey, StringComparison.Ordinal) ||
            !ReferencedRowsStand(source: view.Source, arena: arena)) {
            return false;
        }

        WorldProjectionWork.Count(kind: WorldProjectionWork.Compositions);
        var source = view.Source;
        var observations = view.Observations(view.Images.Presented(source));
        var spaces = WorldProjection.SpacesOf(observations: observations, spaces: source.StateRaw?.Spaces);
        var time = view.Time;
        var timeline = WorldProjection.ComposeTimeline(definition: source, anchors: m_anchors, arena: arena, time: in time);

        var observationsChanged = !Puck.Maths.SequenceEqualityComparer<WorldObservedRow>.Instance.Equals(x: held.Observations, y: observations);
        var spacesChanged = !Puck.Maths.SequenceEqualityComparer<StateSpace>.Instance.Equals(x: held.Spaces, y: spaces);
        var timelineChanged = !(((timeline is null) == (held.Timeline is null)) && (timeline?.Clocks ?? []).SequenceEqual(held.Timeline?.Clocks ?? []));

        if (!observationsChanged && !spacesChanged && !timelineChanged) {
            m_recipientIdentity = identity;
            m_held = held with { Observations = observations, Spaces = spaces, Timeline = timeline };
            return true;
        }

        var delta = new JsonObject();
        JsonNode? previousObservations = null;
        JsonNode? nextObservations = null;
        JsonObject? stateDelta = null;
        var fastObservationsValuesOnly = true;
        var usedFastObservationDiff = false;

        if (observationsChanged) {
            if (TryDiffChangedObservations(previous: held.Observations, current: observations, stateDelta: out stateDelta, valuesOnly: out fastObservationsValuesOnly)) {
                usedFastObservationDiff = true;
            } else {
                previousObservations = ((held.Observations is { } previous)
                    ? WorldProjectionDelta.Tree(previous.ToArray(), WorldJsonContext.Default.WorldObservedRowArray)
                    : null);
                nextObservations = ((observations is { } current)
                    ? WorldProjectionDelta.Tree(current.ToArray(), WorldJsonContext.Default.WorldObservedRowArray)
                    : null);
                var before = new JsonObject();
                var after = new JsonObject();
                if (previousObservations is not null) { before["observations"] = previousObservations.DeepClone(); }
                if (nextObservations is not null) { after["observations"] = nextObservations.DeepClone(); }
                stateDelta = WorldProjectionDelta.Diff(held: before, next: after);
                if (stateDelta is null) { return false; }
            }
        }

        JsonNode? spacesNode = null;
        if (spacesChanged) {
            spacesNode = ((spaces is null) ? null : WorldProjectionDelta.Tree(spaces, WorldJsonContext.Default.For<StateSpace[]>()));
            var before = new JsonObject();
            var after = new JsonObject();
            if (tree.TryGetPropertyValue("spaces", out var heldSpaces) && (heldSpaces is not null)) { before["spaces"] = heldSpaces.DeepClone(); }
            if (spacesNode is not null) { after["spaces"] = spacesNode.DeepClone(); }
            var spacesDelta = WorldProjectionDelta.Diff(held: before, next: after);
            if (spacesDelta is null) { return false; }
            foreach (var (name, value) in spacesDelta) { delta[name] = value?.DeepClone(); }
        }

        JsonNode? timelineNode = null;
        if (timelineChanged) {
            timelineNode = ((timeline is null) ? null : WorldProjectionDelta.Tree(timeline: timeline));
            var before = new JsonObject();
            var after = new JsonObject();
            if (tree.TryGetPropertyValue(WorldProjectionDelta.Timeline, out var heldTimeline) && (heldTimeline is not null)) { before[WorldProjectionDelta.Timeline] = heldTimeline.DeepClone(); }
            if (timelineNode is not null) { after[WorldProjectionDelta.Timeline] = timelineNode.DeepClone(); }
            var timelineDelta = WorldProjectionDelta.Diff(held: before, next: after);
            if (timelineDelta is null) { return false; }
            foreach (var (name, value) in timelineDelta) { delta[name] = value?.DeepClone(); }
        }

        if ((delta.Count == 0) && (!observationsChanged || (stateDelta is { Count: 0 }))) {
            m_recipientIdentity = identity;
            m_held = held with { Observations = observations, Spaces = spaces, Timeline = timeline };
            return true;
        }

        cursor.Delivered(view);
        var provenance = held.Provenance with { Revision = checked((int)cursor.Sequence) };
        var provenanceNode = WorldProjectionDelta.Tree(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        tree["provenance"] = provenanceNode;
        delta["provenance"] = provenanceNode.DeepClone();
        if (spacesChanged) {
            if (spacesNode is null) { _ = tree.Remove("spaces"); } else { tree["spaces"] = spacesNode; }
        }
        if (timelineChanged) {
            if (timelineNode is null) { _ = tree.Remove(WorldProjectionDelta.Timeline); } else { tree[WorldProjectionDelta.Timeline] = timelineNode; }
        }
        if (stateDelta is not null) {
            foreach (var (name, value) in stateDelta) { delta[name] = value?.DeepClone(); }
        }
        var nextProjection = held with { Provenance = provenance, Observations = observations, Spaces = spaces, Timeline = timeline };
        m_anchors.Retain(nextProjection.Timeline?.Clocks ?? []);
        var valuesOnly = (usedFastObservationDiff
            ? (fastObservationsValuesOnly && WorldProjectionDelta.HasOnlyValueMembers(delta: delta))
            : WorldProjectionDelta.IsValuesOnly(delta, previousObservations, nextObservations));
        m_recipientIdentity = identity;
        m_held = nextProjection;
        delivery = Partial(nextProjection, delta, valuesOnly);
        return true;
    }

    private static bool TryDiffChangedObservations(IReadOnlyList<WorldObservedRow>? previous, IReadOnlyList<WorldObservedRow>? current, out JsonObject? stateDelta, out bool valuesOnly) {
        stateDelta = null;
        valuesOnly = true;
        if ((previous is null) || (current is null) || (previous.Count != current.Count) || (previous.Count == 0)) {
            return false;
        }
        List<int>? changed = null;
        for (var index = 0; (index < previous.Count); index++) {
            var beforeRow = previous[index];
            var afterRow = current[index];
            if (ReferenceEquals(objA: beforeRow, objB: afterRow)) { continue; }
            if (!string.Equals(a: beforeRow.Name, b: afterRow.Name, comparisonType: StringComparison.Ordinal)) {
                return false;
            }
            if (beforeRow == afterRow) { continue; }
            (changed ??= []).Add(item: index);
            if (valuesOnly && !WorldProjectionDelta.SameObservationRowShape(left: beforeRow, right: afterRow)) {
                valuesOnly = false;
            }
        }
        if (changed is null) {
            stateDelta = new JsonObject();
            return true;
        }
        var sliceBefore = new WorldObservedRow[changed.Count];
        var sliceAfter = new WorldObservedRow[changed.Count];
        for (var ordinal = 0; (ordinal < changed.Count); ordinal++) {
            var rowIndex = changed[ordinal];
            sliceBefore[ordinal] = previous[rowIndex];
            sliceAfter[ordinal] = current[rowIndex];
        }
        var beforeNode = WorldProjectionDelta.Tree(sliceBefore, WorldJsonContext.Default.WorldObservedRowArray);
        var afterNode = WorldProjectionDelta.Tree(sliceAfter, WorldJsonContext.Default.WorldObservedRowArray);
        stateDelta = WorldProjectionDelta.Diff(
            held: new JsonObject { ["observations"] = beforeNode },
            next: new JsonObject { ["observations"] = afterNode }
        );
        return (stateDelta is not null);
    }

    /// <summary>Advances a primed projection from changed presentation rows, falling back to a disclosure composition
    /// when state or policy also moved.</summary>
    public WorldProjectionDelivery ComposeAppearance(WorldRecipientView view, WorldDeliveryCursor cursor, WorldPresentationRows rows) {
        if (!view.Observes) { return WorldProjectionDelivery.Nothing; }
        if (rows.ChangesProjectionDependencies || m_held is not { } held || m_tree is not { } tree ||
            !view.Images.TryAdvanceAppearance(view, m_recipientFrame, m_recipientIdentity, out var frame, out var identity)) {
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
        delta["provenance"] = JsonSerializer.SerializeToNode(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        tree["provenance"] = delta["provenance"]!.DeepClone();
        var heldReferences = (JsonArray)tree["prototypes"]!;
        for (var index = 0; index < replacements.Count; index++) {
            heldReferences[replacements[index].Ordinal] = changedReferences[index]!.DeepClone();
        }
        if (!rows.HasShades) {
            tree.Remove("shades");
        } else if (delta["shades"] is JsonObject changedShades) {
            var heldShades = tree["shades"] as JsonObject;
            if (heldShades is null) { tree["shades"] = heldShades = new JsonObject(); }
            foreach (var (name, shade) in changedShades) {
                if (shade is null) { heldShades.Remove(name); } else { heldShades[name] = shade.DeepClone(); }
            }
        }

        m_held = held with { Creations = references, Provenance = provenance, Shades = view.Source.ShadesRaw };
        return Partial(m_held, delta, valuesOnly: false) with {
            Appearance = new([.. replacements], rows.Shades, HasCreations: true, rows.HasShades)
        };
    }

    /// <summary>Delivers anchor corrections at this stream's sequence.</summary>
    public WorldProjectionDelivery Step(WorldRecipientView view, WorldDeliveryCursor cursor, ulong tick, ulong engineTick) {
        var delivery = Step(view.Source, tick, engineTick, view.Arena);
        if (delivery.Kind == WorldProjectionDeliveryKind.None) { return delivery; }
        cursor.Delivered();
        var provenance = m_held!.Provenance with { Revision = checked((int)cursor.Sequence) };
        var provenanceNode = WorldProjectionDelta.Tree(provenance, WorldJsonContext.Default.For<WorldProjectionProvenance>());
        m_held = m_held with { Provenance = provenance };
        m_tree!["provenance"] = provenanceNode;
        if (delivery.Kind == WorldProjectionDeliveryKind.Document) {
            return delivery with { Projection = m_held, Payload = WorldProjection.SerializeCompact(m_held) };
        }
        var delta = WorldProjectionDelta.Tree(delivery.Payload);
        delta["provenance"] = provenanceNode.DeepClone();
        return delivery with { Projection = m_held, Payload = CanonicalJsonDocument.SerializeCompact(delta), TimelineOnly = false };
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
        var tree = WorldProjectionDelta.Tree(utf8Json: payload);

        if (m_tree is null) {
            return Whole(
                payload: payload,
                projection: projection,
                tree: tree
            );
        }

        var delta = WorldProjectionDelta.Diff(
            held: m_tree,
            next: tree
        );

        if (delta is null) {
            return Whole(
                payload: payload,
                projection: projection,
                tree: tree
            );
        }

        var valuesOnly = WorldProjectionDelta.IsValuesOnly(
            delta: delta,
            previousObservations: m_tree["observations"],
            nextObservations: tree["observations"]
        );

        m_held = projection;
        m_tree = tree;

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
        if (
            (m_held?.Timeline is not { Clocks: { } clocks } timeline) ||
            (m_tree is null) ||
            !m_anchors.Step(
            arena: arena,
            definition: definition,
            engineTick: engineTick,
            tick: tick
        )
        ) {
            return WorldProjectionDelivery.Nothing;
        }

        var carried = new WorldClock[clocks.Count];

        for (var index = 0; (index < carried.Length); index++) {
            var clock = clocks[index];

            // An anchored clock carries what the ledger holds, which is nothing once a stale anchor was dropped with
            // no seed to replace it.
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

        var next = (timeline with { Clocks = carried });
        var node = WorldProjectionDelta.Tree(timeline: next);
        var delta = WorldProjectionDelta.Diff(
            held: new JsonObject { [WorldProjectionDelta.Timeline] = m_tree[WorldProjectionDelta.Timeline]?.DeepClone() },
            next: new JsonObject { [WorldProjectionDelta.Timeline] = node.DeepClone() }
        );

        m_held = (m_held with { Timeline = next });
        m_tree[WorldProjectionDelta.Timeline] = node;

        return ((delta is null)
            ? Whole(
                payload: WorldProjection.SerializeCompact(projection: m_held),
                projection: m_held,
                tree: m_tree
            )
            : Partial(
                delta: delta,
                projection: m_held,
                valuesOnly: true
            ));
    }
    /// <summary>Releases everything held for the recipient, its anchor rows included: it left or lost disclosure, and
    /// its next delivery is a whole projection.</summary>
    public void Release() {
        m_anchors.Release();
        m_held = null;
        m_tree = null;
        m_recipientIdentity = null;
        m_recipientFrame = null;
        m_heldArena = null;
        m_heldAudienceKey = null;
        m_heldDisclosureRevision = -1L;
        m_referencedSource = null;
        m_referencedRowOrdinals = [];
        m_referencedRowVersions = [];
    }

    private WorldProjectionDelivery Partial(WorldProjectionDocument projection, JsonObject delta, bool valuesOnly) {
        var payload = CanonicalJsonDocument.SerializeCompact(node: delta);

        WorldProjectionWork.Count(kind: WorldProjectionWork.Deltas);
        WorldProjectionWork.Count(
            amount: payload.Length,
            kind: WorldProjectionWork.Bytes
        );

        return new WorldProjectionDelivery(
            Kind: WorldProjectionDeliveryKind.Delta,
            Payload: payload,
            Projection: projection,
            TimelineOnly: WorldProjectionDelta.IsTimelineOnly(delta: delta),
            ValuesOnly: valuesOnly
        );
    }
    private WorldProjectionDelivery Whole(WorldProjectionDocument projection, JsonObject tree, byte[] payload) {
        m_held = projection;
        m_tree = tree;
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
/// <summary>A recipient's side of a <see cref="WorldProjectionFeed"/>: the projection it holds, which a whole
/// delivery replaces and a delta merges into, and the definition hydrated from it.</summary>
/// <param name="content">The recipient content cache, or the shared store.</param>
/// <param name="fetch">The authorized fetch door for objects absent from the cache.</param>
/// <param name="basis">Reads the document the recipient holds for the sender's world, which a replica's manifest is
/// assembled over: its own copy of the world, then each document the stream delivered; <see langword="null"/> assembles
/// every manifest whole.</param>
/// <param name="seed">An optional initial local definition whose literal prototypes may satisfy matching pins.</param>
public sealed class WorldProjectionHold(ContentAddressedStore? content = null, Func<ContentPin, byte[]?>? fetch = null, Func<WorldDefinition?>? basis = null, WorldDefinition? seed = null) {
    private WorldProjectionDocument? m_projection;
    private JsonObject? m_tree;
    private Dictionary<string, int>? m_observationOrdinals;

    /// <summary>Assembles the document a replica's manifest names over the document the recipient holds
    /// (<see cref="WorldDocumentManifest.TryAssemble"/>).</summary>
    /// <param name="manifest">The manifest's bytes.</param>
    /// <param name="definition">The assembled, validated document on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the manifest assembled into a document this process may present.</returns>
    public bool TryHoldReplica(ReadOnlySpan<byte> manifest, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) =>
        WorldDocumentManifest.TryAssemble(
            basis: (basis?.Invoke() ?? seed),
            definition: out definition,
            held: assembled => WorldContentIndex.Fetch(definition: assembled, fetch: fetch, held: content),
            manifest: manifest,
            reason: out reason,
            resolve: WorldDocumentManifest.Resolve(content: content, fetch: fetch)
        );

    /// <summary>Gets the definition hydrated from the projection held, or <see langword="null"/> before the first
    /// whole projection.</summary>
    public WorldDefinition? Definition { get; private set; }

    /// <summary>Holds a whole projection and hydrates it.</summary>
    /// <param name="utf8Json">The projection's bytes.</param>
    /// <param name="definition">The hydrated definition on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the projection parsed and hydrated; the hold is unchanged otherwise.</returns>
    public bool TryHold(ReadOnlySpan<byte> utf8Json, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) {
        definition = null;
        var prior = (Definition ?? basis?.Invoke());

        if (
            !WorldProjection.TryDeserialize(
            projection: out var projection,
            reason: out reason,
            utf8Json: utf8Json
        ) ||
            !WorldProjection.TryToDefinition(
            content: content,
            definition: out definition,
            fallbackKnown: seed,
            fetch: fetch,
            known: prior,
            projection: projection!,
            reason: out reason
        )
        ) {
            definition = null;

            return false;
        }

        m_projection = projection;
        m_tree = WorldProjectionDelta.Tree(utf8Json: utf8Json);
        m_observationOrdinals = IndexObservations(observations: projection!.Observations);
        Definition = definition;

        return true;
    }

    private static Dictionary<string, int>? IndexObservations(IReadOnlyList<WorldObservedRow>? observations) {
        if ((observations is null) || (observations.Count == 0)) { return null; }
        var map = new Dictionary<string, int>(capacity: observations.Count, comparer: StringComparer.Ordinal);
        for (var index = 0; (index < observations.Count); index++) {
            map[observations[index].Name] = index;
        }
        return map;
    }

    private bool TryPatchObservationsInPlace(WorldProjectionDocument heldProjection, JsonObject delta, out IReadOnlyList<WorldObservedRow>? patchedObservations, out List<(int Ordinal, JsonNode MergedNode)>? treeUpdates, out bool observationsValuesOnly, out JsonObject remainingDelta) {
        patchedObservations = null;
        treeUpdates = null;
        observationsValuesOnly = true;
        remainingDelta = delta;

        if (!delta.TryGetPropertyValue(propertyName: "observations", jsonNode: out var obsPatchNode) ||
            (obsPatchNode is not JsonArray patchRows) || (patchRows.Count == 0) ||
            WorldDocumentBasis.IsReplaceMarker(node: patchRows[0]) ||
            (m_tree?["observations"] is not JsonArray heldObsTree) ||
            (heldProjection.Observations is not { Count: > 0 } heldObsList) ||
            (heldObsTree.Count != heldObsList.Count)) {
            return false;
        }
        var indexMap = (m_observationOrdinals ??= IndexObservations(observations: heldObsList));
        if ((indexMap is null) || (indexMap.Count != heldObsList.Count)) {
            return false;
        }

        var ordinals = new int[patchRows.Count];
        var basisSlice = new JsonArray();
        var overlaySlice = new JsonArray();

        for (var i = 0; (i < patchRows.Count); i++) {
            if ((patchRows[i] is not JsonObject patchObj) ||
                patchObj.ContainsKey(propertyName: WorldDocumentBasis.DropMemberName) ||
                patchObj.ContainsKey(propertyName: WorldDocumentBasis.BeforeMemberName) ||
                (patchObj["name"] is not JsonValue nameVal) ||
                !nameVal.TryGetValue<string>(value: out var rowName) ||
                !indexMap.TryGetValue(key: rowName, value: out var ordinal)) {
                return false;
            }
            for (var prior = 0; (prior < i); prior++) {
                if (ordinals[prior] == ordinal) { return false; }
            }
            ordinals[i] = ordinal;
            basisSlice.Add(value: heldObsTree[ordinal]?.DeepClone());
            overlaySlice.Add(value: patchObj.DeepClone());
        }

        if (!WorldDocumentBasis.TryMerge(
            basis: new JsonObject { ["observations"] = basisSlice },
            overlay: new JsonObject { ["observations"] = overlaySlice },
            composed: out var sliceComposed,
            reason: out _
        ) || (sliceComposed?["observations"] is not JsonArray mergedSlice) || (mergedSlice.Count != ordinals.Length)) {
            return false;
        }

        WorldObservedRow[]? deserialized;
        try {
            deserialized = JsonSerializer.Deserialize(
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: mergedSlice),
                jsonTypeInfo: WorldJsonContext.Untrusted.WorldObservedRowArray
            );
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            return false;
        }
        if ((deserialized is null) || (deserialized.Length != ordinals.Length)) {
            return false;
        }

        var updated = new WorldObservedRow[heldObsList.Count];
        for (var i = 0; (i < heldObsList.Count); i++) { updated[i] = heldObsList[i]; }
        var updates = new List<(int Ordinal, JsonNode MergedNode)>(capacity: ordinals.Length);
        for (var i = 0; (i < ordinals.Length); i++) {
            var ordinal = ordinals[i];
            if (observationsValuesOnly && !WorldProjectionDelta.SameObservationRowShape(left: heldObsList[ordinal], right: deserialized[i])) {
                observationsValuesOnly = false;
            }
            updated[ordinal] = deserialized[i];
            updates.Add(item: (ordinal, WorldProjectionDelta.Tree(value: deserialized[i], jsonTypeInfo: WorldJsonContext.Default.For<WorldObservedRow>())));
        }

        var reduced = new JsonObject();
        foreach (var (member, value) in delta) {
            if (!string.Equals(a: member, b: "observations", comparisonType: StringComparison.Ordinal)) {
                reduced[member] = value?.DeepClone();
            }
        }

        patchedObservations = updated;
        treeUpdates = updates;
        remainingDelta = reduced;
        return true;
    }
    /// <summary>Merges a delta over the projection held and hydrates the result. A delta of the timeline alone replaces
    /// the held definition's timeline and hydrates nothing else.</summary>
    /// <param name="utf8Json">The delta's bytes.</param>
    /// <param name="definition">The hydrated definition on success.</param>
    /// <param name="valuesOnly">Whether the delta changes only values (<see cref="WorldProjectionDelivery.ValuesOnly"/>).</param>
    /// <param name="timelineOnly">Whether the delta changes the timeline alone
    /// (<see cref="WorldProjectionDelivery.TimelineOnly"/>).</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the delta merged and the result hydrated; the hold is unchanged
    /// otherwise.</returns>
    public bool TryApply(ReadOnlySpan<byte> utf8Json, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out bool valuesOnly, out bool timelineOnly, out string reason) {
        definition = null;
        valuesOnly = false;
        timelineOnly = false;

        if ((m_tree is null) || (m_projection is not { } heldProjection) || (Definition is not { } held)) {
            reason = "a projection delta arrived before any whole projection it could merge over.";

            return false;
        }

        JsonObject delta;

        try {
            delta = WorldProjectionDelta.Tree(utf8Json: utf8Json);
        } catch (JsonException exception) {
            reason = $"the projection delta is not JSON: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var fastObs = TryPatchObservationsInPlace(
            heldProjection: heldProjection,
            delta: delta,
            patchedObservations: out var patchedObservations,
            treeUpdates: out var obsTreeUpdates,
            observationsValuesOnly: out var obsValuesOnly,
            remainingDelta: out var effectiveDelta
        );

        if (!WorldProjectionDelta.TryMerge(
            delta: effectiveDelta,
            held: m_tree,
            merged: out var merged,
            reason: out reason
        )) {
            return false;
        }

        valuesOnly = (fastObs
            ? (obsValuesOnly && WorldProjectionDelta.HasOnlyValueMembers(delta: delta))
            : WorldProjectionDelta.IsValuesOnly(
                delta: delta,
                previousObservations: m_tree["observations"],
                nextObservations: (merged.TryGetValue(key: "observations", value: out var observations) ? observations : m_tree["observations"])
            ));
        timelineOnly = WorldProjectionDelta.IsTimelineOnly(delta: delta);
        WorldProjectionDocument? nextProjection;

        if (timelineOnly) {
            WorldTimelineSection? timeline;

            try {
                timeline = merged[WorldProjectionDelta.Timeline]?.Deserialize(jsonTypeInfo: WorldJsonContext.Untrusted.WorldTimelineSection);
            } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
                reason = $"the projection delta's timeline is not a timeline: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }

            nextProjection = (heldProjection with { Timeline = timeline });
            definition = (held with { TimelineRaw = timeline });

            if (WorldProjection.Uncarried(definition: definition) is { } uncarried) {
                definition = null;
                reason = uncarried;

                return false;
            }
        } else {
            if (!TryMergeProjection(
                current: (fastObs ? (heldProjection with { Observations = patchedObservations }) : heldProjection),
                delta: effectiveDelta,
                heldTree: m_tree,
                merged: merged,
                next: out nextProjection,
                reason: out reason
            ) || !WorldProjection.TryToDefinition(
                content: content,
                definition: out definition,
                fallbackKnown: (basis?.Invoke() ?? seed),
                fetch: fetch,
                known: held,
                previousProjection: heldProjection,
                projection: nextProjection!,
                reason: out reason
            )) {
                definition = null;

                return false;
            }
        }

        foreach (var (member, node) in merged) {
            if (node is null) {
                _ = m_tree.Remove(propertyName: member);
            } else {
                m_tree[member] = node.DeepClone();
            }
        }
        if (fastObs) {
            var heldObsArray = (JsonArray)m_tree["observations"]!;
            foreach (var (ordinal, mergedNode) in obsTreeUpdates!) {
                heldObsArray[ordinal] = mergedNode.DeepClone();
            }
        } else if (merged.ContainsKey(key: "observations")) {
            m_observationOrdinals = IndexObservations(observations: nextProjection!.Observations);
        }

        m_projection = nextProjection;
        Definition = definition;
        reason = string.Empty;

        return true;
    }

    // Deserializes only the top-level members the delta touched and shares unchanged rows inside model sections
    // (such as placements), keeping every untouched member of the held projection by reference.
    private static bool TryMergeProjection(WorldProjectionDocument current, JsonObject heldTree, JsonObject delta, Dictionary<string, JsonNode?> merged, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldProjectionDocument? next, out string reason) {
        next = current;
        Dictionary<string, JsonElement>? extensions = null;

        T ReadRequired<T>(string member, JsonNode? node) {
            if (node is null) {
                throw new JsonException(message: $"projection member '{member}' cannot be null.");
            }
            var value = JsonSerializer.Deserialize(
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: node),
                jsonTypeInfo: (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)WorldJsonContext.Untrusted.Options.GetTypeInfo(type: typeof(T))
            );

            return (value ?? throw new JsonException(message: $"projection member '{member}' deserialized to null."));
        }

        T? ReadOptional<T>(JsonNode? node) => ((node is null)
            ? default
            : JsonSerializer.Deserialize(
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: node),
                jsonTypeInfo: (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)WorldJsonContext.Untrusted.Options.GetTypeInfo(type: typeof(T))
            ));

        T ShareSection<T>(string member, T value, T? basis) where T : class => (
            (basis is not null) &&
            heldTree.TryGetPropertyValue(propertyName: member, jsonNode: out var basisNode) &&
            (basisNode is not null) &&
            merged.TryGetValue(key: member, value: out var valueNode) &&
            (valueNode is not null) &&
            delta.TryGetPropertyValue(propertyName: member, jsonNode: out var patchNode) &&
            (patchNode is not null)
                ? WorldDocumentWalker.Share(basis: basis, basisTree: basisNode, patch: patchNode, value: value, valueTree: valueNode)
                : value
        );

        try {
            foreach (var (member, node) in merged) {
                switch (member) {
                    case "schema":
                        next = (next with { Schema = ReadRequired<string>(member: member, node: node) });
                        break;
                    case "provenance":
                        next = (next with { Provenance = ReadRequired<WorldProjectionProvenance>(member: member, node: node) });
                        break;
                    case "motion":
                        next = (next with { Motion = ReadRequired<WorldMotionDefaults>(member: member, node: node) });
                        break;
                    case "spawnPoints":
                        next = (next with { SpawnPoints = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldSpawnPoint>>(member: member, node: node), basis: next.SpawnPoints) });
                        break;
                    case "render":
                        next = (next with { Render = ShareSection(member: member, value: ReadRequired<WorldRenderDefaults>(member: member, node: node), basis: next.Render) });
                        break;
                    case "screens":
                        next = (next with { Screens = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldScreen>>(member: member, node: node), basis: next.Screens) });
                        break;
                    case "screenMaterials":
                        next = (next with { ScreenMaterials = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldScreenMaterial>>(member: member, node: node), basis: next.ScreenMaterials) });
                        break;
                    case "cameras":
                        next = (next with { Cameras = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldCamera>>(member: member, node: node), basis: next.Cameras) });
                        break;
                    case "population":
                        next = (next with { Population = ReadRequired<WorldBodiesDefaults>(member: member, node: node) });
                        break;
                    case "playerDefaults":
                        next = (next with { PlayerDefaults = ShareSection(member: member, value: ReadRequired<WorldPlayerDefaults>(member: member, node: node), basis: next.PlayerDefaults) });
                        break;
                    case "channels":
                        next = (next with { Channels = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldChannel>>(member: member, node: node), basis: next.Channels) });
                        break;
                    case "kits":
                        next = (next with { Kits = ReadRequired<IReadOnlyList<WorldProjectedKit>>(member: member, node: node) });
                        break;
                    case "defaultSeatKit":
                        next = (next with { DefaultSeatKit = ReadRequired<string>(member: member, node: node) });
                        break;
                    case "assignment":
                        next = (next with { Assignment = ShareSection(member: member, value: ReadRequired<WorldRowAssignment>(member: member, node: node), basis: next.Assignment) });
                        break;
                    case "bindingOverlays":
                        next = (next with { BindingOverlays = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldBindingOverlay>>(member: member, node: node), basis: next.BindingOverlays) });
                        break;
                    case "prototypes":
                        next = (next with { Creations = ReadRequired<IReadOnlyList<WorldPrototypeReference>>(member: member, node: node) });
                        break;
                    case "placements":
                        next = (next with { Placements = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldPlacement>>(member: member, node: node), basis: next.Placements) });
                        break;
                    case "speakers":
                        next = (next with { Speakers = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldSpeaker>>(member: member, node: node), basis: next.Speakers) });
                        break;
                    case "tunes":
                        next = (next with { Tunes = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldTune>>(member: member, node: node), basis: next.Tunes) });
                        break;
                    case "patches":
                        next = (next with { Patches = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldPatch>>(member: member, node: node), basis: next.Patches) });
                        break;
                    case "audio":
                        next = (next with { Audio = ShareSection(member: member, value: ReadRequired<WorldAudioDefaults>(member: member, node: node), basis: next.Audio) });
                        break;
                    case "collision":
                        next = (next with { Collision = ShareSection(member: member, value: ReadRequired<WorldCollision>(member: member, node: node), basis: next.Collision) });
                        break;
                    case "views": {
                            var views = ReadOptional<WorldViewDefaults>(node: node);
                            next = (next with { Views = ((views is null) ? null : ShareSection(member: member, value: views, basis: next.Views)) });
                            break;
                        }
                    case "looks":
                        next = (next with { Looks = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldLook>>(member: member, node: node), basis: next.Looks) });
                        break;
                    case "lookAssignment":
                        next = (next with { LookAssignment = ShareSection(member: member, value: ReadRequired<WorldRowAssignment>(member: member, node: node), basis: next.LookAssignment) });
                        break;
                    case "dynamics":
                        next = (next with { Dynamics = ShareSection(member: member, value: ReadRequired<IReadOnlyList<DynamicsRow>>(member: member, node: node), basis: next.Dynamics) });
                        break;
                    case "hud":
                        next = (next with { Hud = ShareSection(member: member, value: ReadRequired<WorldHudSection>(member: member, node: node), basis: next.Hud) });
                        break;
                    case "fieldRegions":
                        next = (next with { FieldRegions = ReadOptional<IReadOnlyList<WorldFieldsSection>>(node: node) });
                        break;
                    case "simulation": {
                            var simulation = ReadOptional<WorldSimulationDefaults>(node: node);
                            next = (next with { Simulation = ((simulation is null) ? null : ShareSection(member: member, value: simulation, basis: next.Simulation)) });
                            break;
                        }
                    case "interactions": {
                            var interactions = ReadOptional<WorldInteractionsSection>(node: node);
                            next = (next with { Interactions = ((interactions is null) ? null : ShareSection(member: member, value: interactions, basis: next.Interactions)) });
                            break;
                        }
                    case "references": {
                            var references = ReadOptional<IReadOnlyList<WorldReference>>(node: node);
                            next = (next with { References = ((references is null) ? null : ShareSection(member: member, value: references, basis: next.References)) });
                            break;
                        }
                    case "destinations": {
                            var destinations = ReadOptional<IReadOnlyList<WorldDestination>>(node: node);
                            next = (next with { Destinations = ((destinations is null) ? null : ShareSection(member: member, value: destinations, basis: next.Destinations)) });
                            break;
                        }
                    case "adjacencies": {
                            var adjacencies = ReadOptional<IReadOnlyList<WorldAdjacency>>(node: node);
                            next = (next with { Adjacencies = ((adjacencies is null) ? null : ShareSection(member: member, value: adjacencies, basis: next.Adjacencies)) });
                            break;
                        }
                    case "metadata":
                        next = (next with { Metadata = ReadOptional<WorldProjectedMetadata>(node: node) });
                        break;
                    case "observations":
                        next = (next with { Observations = ReadOptional<IReadOnlyList<WorldObservedRow>>(node: node) });
                        break;
                    case "spaces":
                        next = (next with { Spaces = ReadOptional<IReadOnlyList<StateSpace>>(node: node) });
                        break;
                    case "timeline": {
                            var timeline = ReadOptional<WorldTimelineSection>(node: node);
                            next = (next with { Timeline = ((timeline is null) ? null : ShareSection(member: member, value: timeline, basis: next.Timeline)) });
                            break;
                        }
                    case "music": {
                            var music = ReadOptional<IReadOnlyList<WorldMusicRow>>(node: node);
                            next = (next with { Music = ((music is null) ? null : ShareSection(member: member, value: music, basis: next.Music)) });
                            break;
                        }
                    case "haptics": {
                            var haptics = ReadOptional<IReadOnlyList<WorldHapticRow>>(node: node);
                            next = (next with { Haptics = ((haptics is null) ? null : ShareSection(member: member, value: haptics, basis: next.Haptics)) });
                            break;
                        }
                    case "keyLights": {
                            var keyLights = ReadOptional<WorldKeyLightSection>(node: node);
                            next = (next with { KeyLights = ((keyLights is null) ? null : ShareSection(member: member, value: keyLights, basis: next.KeyLights)) });
                            break;
                        }
                    case "highlights": {
                            var highlights = ReadOptional<IReadOnlyList<WorldHighlightRow>>(node: node);
                            next = (next with { Highlights = ((highlights is null) ? null : ShareSection(member: member, value: highlights, basis: next.Highlights)) });
                            break;
                        }
                    case "shades":
                        next = (next with { Shades = ReadOptional<IReadOnlyDictionary<string, Puck.SignedDistance.SdfShadeDefinition>>(node: node) });
                        break;
                    case "text": {
                            var text = ReadOptional<Puck.Text.TextFontCatalogDefinition>(node: node);
                            next = (next with { Text = ((text is null) ? null : ShareSection(member: member, value: text, basis: next.Text)) });
                            break;
                        }
                    case "machines":
                        next = (next with { Machines = ReadOptional<IReadOnlyList<WorldProjectedMachine>>(node: node) });
                        break;
                    case "navigation": {
                            var navigation = ReadOptional<WorldNavigationSection>(node: node);
                            next = (next with { Navigation = ((navigation is null) ? null : ShareSection(member: member, value: navigation, basis: next.Navigation)) });
                            break;
                        }
                    case "inputModes":
                        next = (next with { InputModes = ReadOptional<WorldProjectedInputModes>(node: node) });
                        break;
                    case "seatModes": {
                            var seatModes = ReadOptional<IReadOnlyList<WorldSeatModeFamily>>(node: node);
                            next = (next with { SeatModes = ((seatModes is null) ? null : ShareSection(member: member, value: seatModes, basis: next.SeatModes)) });
                            break;
                        }
                    case "inputEventsPerStep":
                        next = (next with { InputEventsPerStep = ((node is null) ? null : JsonSerializer.Deserialize(utf8Json: CanonicalJsonDocument.SerializeCompact(node: node), jsonTypeInfo: WorldJsonContext.Untrusted.For<int>())) });
                        break;
                    case "bodyState": {
                            var bodyState = ReadOptional<IReadOnlyList<WorldProjectedBodyRegister>>(node: node);
                            next = (next with { BodyState = ((bodyState is null) ? null : ShareSection(member: member, value: bodyState, basis: next.BodyState)) });
                            break;
                        }
                    case "identityState": {
                            var identityState = ReadOptional<IReadOnlyList<WorldProjectedBodyRegister>>(node: node);
                            next = (next with { IdentityState = ((identityState is null) ? null : ShareSection(member: member, value: identityState, basis: next.IdentityState)) });
                            break;
                        }
                    default:
                        if (!DocumentExtensionsPolicy.IsReservedKey(key: member)) {
                            reason = $"projection contains unrecognized top-level member '{member}'.";
                            next = null;

                            return false;
                        }
                        extensions ??= ((next.Extensions is { } heldExtensions)
                            ? new Dictionary<string, JsonElement>(dictionary: heldExtensions, comparer: StringComparer.Ordinal)
                            : new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal));
                        if (node is null) {
                            _ = extensions.Remove(key: member);
                        } else {
                            extensions[member] = JsonSerializer.Deserialize<JsonElement>(utf8Json: CanonicalJsonDocument.SerializeCompact(node: node));
                        }
                        break;
                }
            }
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            reason = $"the projection is not a valid {WorldProjectionDocument.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            next = null;

            return false;
        }

        if (extensions is not null) {
            next = (next with { Extensions = ((extensions.Count == 0) ? null : extensions) });
        }
        if (!string.Equals(a: next.Schema, b: WorldProjectionDocument.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            reason = $"projection schema '{next.Schema}' is not {WorldProjectionDocument.SchemaVersion}.";
            next = null;

            return false;
        }
        if (next.Provenance.Tier != WorldDisclosureTier.Presentation) {
            reason = $"projection provenance names tier '{next.Provenance.Tier}'; only '{WorldDisclosureTier.Presentation}' composes a projection document.";
            next = null;

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
/// <summary>The member delta a projection travels in: the one document delta (<see cref="WorldDocumentBasis"/>)
/// applied to a projection's top-level members.</summary>
public static class WorldProjectionDelta {
    /// <summary>The projection member a timeline travels in.</summary>
    public const string Timeline = "timeline";

    // The members whose change moves values only: what a recipient re-reads without rebuilding anything shaped.
    private static readonly HashSet<string> ValueMembers = new(comparer: StringComparer.Ordinal) { Timeline, "observations", "provenance" };

    /// <summary>Returns a serialized projection's tree.</summary>
    /// <param name="utf8Json">The projection's bytes.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="JsonException">The bytes are not a JSON object.</exception>
    public static JsonObject Tree(ReadOnlySpan<byte> utf8Json) => CanonicalJsonDocument.ParseObject(utf8Json);
    /// <summary>Returns a typed member's canonical tree, as a projection carries it.</summary>
    public static JsonNode Tree<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo) where T : class =>
        JsonNode.Parse(utf8Json: CanonicalJsonDocument.SerializeCompact(jsonTypeInfo: jsonTypeInfo, value: value))!;
    /// <summary>Returns a timeline's canonical tree, as a projection carries it.</summary>
    /// <param name="timeline">The timeline.</param>
    /// <returns>The tree.</returns>
    public static JsonNode Tree(WorldTimelineSection timeline) => Tree(
        value: timeline,
        jsonTypeInfo: WorldJsonContext.Default.WorldTimelineSection
    );
    /// <summary>Returns the delta whose merge over <paramref name="held"/> reproduces <paramref name="next"/>, checked
    /// by merging it.</summary>
    /// <param name="held">The tree the recipient holds.</param>
    /// <param name="next">The tree it is owed.</param>
    /// <returns>The delta, empty when the trees are equal, or <see langword="null"/> when no delta reproduces
    /// <paramref name="next"/>, which is then owed whole.</returns>
    public static JsonObject? Diff(JsonObject held, JsonObject next) {
        ArgumentNullException.ThrowIfNull(argument: held);
        ArgumentNullException.ThrowIfNull(argument: next);

        var delta = WorldDocumentBasis.Diff(
            basis: held,
            target: next
        );

        if (delta.Count == 0) {
            return delta;
        }

        if (!TryMerge(
            delta: delta,
            held: held,
            merged: out var merged,
            reason: out _
        )) {
            return null;
        }

        foreach (var (member, node) in merged) {
            if (!JsonNode.DeepEquals(
                node1: node,
                node2: (next.TryGetPropertyValue(jsonNode: out var expected, propertyName: member) ? expected : null)
            ) || ((node is null) && next.ContainsKey(propertyName: member))) {
                return null;
            }
        }

        return delta;
    }
    /// <summary>Merges a delta over the members of a held tree it names, without touching the held tree.</summary>
    /// <param name="held">The tree held.</param>
    /// <param name="delta">The delta.</param>
    /// <param name="merged">Each member the delta names, merged; a member the delta removes maps to
    /// <see langword="null"/>.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every member merged.</returns>
    public static bool TryMerge(JsonObject held, JsonObject delta, out Dictionary<string, JsonNode?> merged, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: held);
        ArgumentNullException.ThrowIfNull(argument: delta);

        merged = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        foreach (var (member, change) in delta) {
            var basis = new JsonObject();

            if (held.TryGetPropertyValue(jsonNode: out var current, propertyName: member)) {
                basis[member] = current?.DeepClone();
            }

            if (!WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: new JsonObject { [member] = change?.DeepClone() },
                reason: out reason
            )) {
                return false;
            }

            merged[member] = (composed!.TryGetPropertyValue(jsonNode: out var node, propertyName: member)
                ? node?.DeepClone()
                : null);
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Returns whether a delta changes the timeline and nothing else.</summary>
    /// <param name="delta">The delta.</param>
    /// <returns><see langword="true"/> when the timeline is the one member the delta names.</returns>
    public static bool IsTimelineOnly(JsonObject delta) {
        ArgumentNullException.ThrowIfNull(argument: delta);

        return ((delta.Count == 1) && delta.ContainsKey(propertyName: Timeline));
    }
    /// <summary>Returns whether every member a delta names is a value-only projection member.</summary>
    public static bool HasOnlyValueMembers(JsonObject delta) {
        ArgumentNullException.ThrowIfNull(argument: delta);

        foreach (var (member, _) in delta) {
            if (!ValueMembers.Contains(item: member)) {
                return false;
            }
        }
        return true;
    }
    /// <summary>Returns whether two typed observation rows have the same disclosed shape and cell keys.</summary>
    public static bool SameObservationRowShape(WorldObservedRow left, WorldObservedRow right) {
        if (!string.Equals(a: left.Name, b: right.Name, comparisonType: StringComparison.Ordinal) ||
            (left.Kind != right.Kind) ||
            !string.Equals(a: left.Space, b: right.Space, comparisonType: StringComparison.Ordinal) ||
            (left.Cells.Count != right.Cells.Count)) {
            return false;
        }
        for (var index = 0; (index < left.Cells.Count); index++) {
            var leftCell = left.Cells[index];
            var rightCell = right.Cells[index];
            if ((leftCell.Hidden != rightCell.Hidden) ||
                !string.Equals(a: leftCell.Key, b: rightCell.Key, comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }
        return true;
    }
    /// <summary>Returns whether a delta changes only values, preserving the observed row ordinals and cell layout.</summary>
    /// <param name="delta">The delta.</param>
    /// <param name="previousObservations">The observations held before the delta.</param>
    /// <param name="nextObservations">The observations after merging the delta.</param>
    /// <returns><see langword="true"/> when the recipient can refresh its existing bindings.</returns>
    public static bool IsValuesOnly(JsonObject delta, JsonNode? previousObservations, JsonNode? nextObservations) {
        if (!HasOnlyValueMembers(delta: delta)) {
            return false;
        }

        return (!delta.ContainsKey(propertyName: "observations") || JsonNode.DeepEquals(
            node1: ObservationShape(observations: previousObservations),
            node2: ObservationShape(observations: nextObservations)
        ));
    }

    private static JsonArray? ObservationShape(JsonNode? observations) {
        if (observations is not JsonArray rows) {
            return null;
        }

        var shape = new JsonArray();

        foreach (var row in rows) {
            if (row is not JsonObject observed) {
                continue; // Hydration refuses malformed rows.
            }

            var keys = new JsonArray();

            if (observed["cells"] is JsonArray cells) {
                foreach (var cell in cells) {
                    if (cell is JsonObject value) {
                        keys.Add(item: new JsonObject {
                            ["key"] = value["key"]?.DeepClone(),
                            ["hidden"] = value["hidden"]?.DeepClone(),
                        });
                    }
                }
            }

            shape.Add(item: new JsonObject {
                ["name"] = observed["name"]?.DeepClone(),
                ["kind"] = observed["kind"]?.DeepClone(),
                ["space"] = observed["space"]?.DeepClone(),
                ["cells"] = keys,
            });
        }

        return shape;
    }
}
