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
/// timeline, the observations or the provenance alone.</param>
/// <param name="TimelineOnly">Whether the delivery is a delta of the timeline alone: anchors, which a recipient takes
/// into the definition it holds without hydrating anything else.</param>
public readonly record struct WorldProjectionDelivery(WorldProjectionDeliveryKind Kind, WorldProjectionDocument? Projection, byte[] Payload, bool ValuesOnly, bool TimelineOnly = false) {
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
/// recipient holds while their predictions hold, and <see cref="Step"/> sends a timeline delta at exactly the
/// authoritative ticks a prediction misses, so a steady sky sends nothing.
/// <para>Counts its deliveries and their bytes under <see cref="WorldProjectionWork"/>. Not thread-safe: one
/// recipient's deliveries run on the thread that steps its world.</para>
/// </summary>
/// <param name="recipient">The authenticated recipient, or <see langword="null"/> for the public observer.</param>
/// <param name="seeds">The phases a late view seeds a clock from while its row holds no number
/// (<see cref="WorldClockAnchors.Seeds"/>), or <see langword="null"/> for none.</param>
public sealed class WorldProjectionFeed(Principal? recipient, IReadOnlyDictionary<string, ulong>? seeds = null) {
    private readonly WorldClockAnchorLedger m_anchors = new(seeds: seeds);

    private WorldProjectionDocument? m_held;
    private JsonObject? m_tree;

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
    /// <exception cref="InvalidOperationException">The projection refuses for this recipient
    /// (<see cref="WorldProjection.Compose"/>).</exception>
    public WorldProjectionDelivery Compose(WorldDefinition definition, string authority, int revision, StateArena arena, in ArenaTime time) {
        var projection = WorldProjection.Compose(
            anchors: m_anchors,
            arena: arena,
            authority: authority,
            definition: definition,
            recipient: recipient,
            revision: revision,
            tier: WorldDisclosureTier.Presentation,
            time: in time
        )!;
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

        m_held = projection;
        m_tree = tree;

        return ((delta.Count == 0)
            ? WorldProjectionDelivery.Nothing
            : Partial(
                delta: delta,
                projection: projection
            ));
    }
    /// <summary>Steps the recipient's anchors at an authoritative tick: a timeline delta when a held prediction misses
    /// the authority's phase, and nothing otherwise.</summary>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns>The delivery owed.</returns>
    public WorldProjectionDelivery Step(WorldDefinition definition, ulong tick, ulong engineTick) {
        if (
            (m_held?.Timeline is not { Clocks: { } clocks } timeline) ||
            (m_tree is null) ||
            !m_anchors.Step(
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

            carried[index] = ((clock.IsAnchored && m_anchors.TryHeld(
                anchor: out var anchor,
                clock: clock.Name
            ))
                ? (clock with { Anchor = anchor })
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
                projection: m_held
            ));
    }
    /// <summary>Releases everything held for the recipient, its anchor rows included: it left or lost disclosure, and
    /// its next delivery is a whole projection.</summary>
    public void Release() {
        m_anchors.Release();
        m_held = null;
        m_tree = null;
    }

    private WorldProjectionDelivery Partial(WorldProjectionDocument projection, JsonObject delta) {
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
            ValuesOnly: WorldProjectionDelta.IsValuesOnly(delta: delta)
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
public sealed class WorldProjectionHold {
    private JsonObject? m_tree;

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

        if (
            !WorldProjection.TryDeserialize(
            projection: out var projection,
            reason: out reason,
            utf8Json: utf8Json
        ) ||
            !WorldProjection.TryToDefinition(
            definition: out definition,
            projection: projection!,
            reason: out reason
        )
        ) {
            definition = null;

            return false;
        }

        m_tree = WorldProjectionDelta.Tree(utf8Json: utf8Json);
        Definition = definition;

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

        if ((m_tree is null) || (Definition is not { } held)) {
            reason = "a projection delta arrived before any whole projection it could merge over.";

            return false;
        }

        JsonObject delta;

        try {
            delta = ((JsonNode.Parse(utf8Json: utf8Json) as JsonObject) ?? throw new JsonException(message: "a projection delta is a JSON object."));
        } catch (JsonException exception) {
            reason = $"the projection delta is not JSON: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (!WorldProjectionDelta.TryMerge(
            delta: delta,
            held: m_tree,
            merged: out var merged,
            reason: out reason
        )) {
            return false;
        }

        valuesOnly = WorldProjectionDelta.IsValuesOnly(delta: delta);
        timelineOnly = WorldProjectionDelta.IsTimelineOnly(delta: delta);

        if (timelineOnly) {
            WorldTimelineSection? timeline;

            try {
                timeline = merged[WorldProjectionDelta.Timeline]?.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldTimelineSection);
            } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
                reason = $"the projection delta's timeline is not a timeline: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }

            definition = (held with { TimelineRaw = timeline });

            if (WorldProjection.Uncarried(definition: definition) is { } uncarried) {
                definition = null;
                reason = uncarried;

                return false;
            }
        } else {
            var whole = ((JsonObject)m_tree.DeepClone());

            foreach (var (member, node) in merged) {
                whole[member] = node?.DeepClone();
            }

            if (!WorldProjection.TryDeserialize(
                projection: out var projection,
                reason: out reason,
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: whole)
            ) || !WorldProjection.TryToDefinition(
                definition: out definition,
                projection: projection!,
                reason: out reason
            )) {
                definition = null;

                return false;
            }
        }

        foreach (var (member, node) in merged) {
            m_tree[member] = node?.DeepClone();
        }

        Definition = definition;
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
    public static JsonObject Tree(ReadOnlySpan<byte> utf8Json) => ((JsonNode.Parse(utf8Json: utf8Json) as JsonObject) ?? throw new JsonException(message: "a projection is a JSON object."));
    /// <summary>Returns a timeline's canonical tree, as a projection carries it.</summary>
    /// <param name="timeline">The timeline.</param>
    /// <returns>The tree.</returns>
    public static JsonNode Tree(WorldTimelineSection timeline) => JsonNode.Parse(utf8Json: CanonicalJsonDocument.Serialize(
        jsonTypeInfo: WorldJsonContext.Default.WorldTimelineSection,
        value: timeline
    ))!;
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
    /// <summary>Returns whether a delta changes only values: the timeline, the observations or the provenance.</summary>
    /// <param name="delta">The delta.</param>
    /// <returns><see langword="true"/> when every member the delta names is one of those.</returns>
    public static bool IsValuesOnly(JsonObject delta) {
        ArgumentNullException.ThrowIfNull(argument: delta);

        foreach (var (member, _) in delta) {
            if (!ValueMembers.Contains(item: member)) {
                return false;
            }
        }

        return true;
    }
}
