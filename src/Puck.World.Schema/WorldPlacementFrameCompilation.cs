using System.Numerics;
using Puck.Maths;
using Puck.Abstractions;

namespace Puck.World;

/// <summary>A placement's canonical world transform, retained in fixed point. Presentation reads the widening
/// getters; surface projection, parent composition and spatial queries retain the same exact fixed values.</summary>
/// <param name="FixedPosition">The world origin.</param>
/// <param name="FixedYawRadians">The world yaw about +Y in radians.</param>
/// <param name="FixedScale">The world scale.</param>
public readonly record struct CompiledPlacementFrame(FixedVector3 FixedPosition, FixedQ4816 FixedYawRadians, FixedQ4816 FixedScale) {
    /// <summary>Quantizes authored or presentation inputs once at the frame boundary.</summary>
    /// <param name="Position">The authored origin.</param><param name="YawDegrees">The authored yaw.</param>
    /// <param name="Scale">The authored scale.</param>
    public CompiledPlacementFrame(Vector3 Position, float YawDegrees, float Scale = 1f)
        : this(FixedVector3.FromVector3(value: Position), FixedQ4816.FromDegrees(degrees: (YawDegrees % 360f)), FixedQ4816.FromDouble(value: Scale)) { }

    /// <summary>Gets the presentation widening of the world origin.</summary>
    public Vector3 Position => FixedPosition.ToVector3();
    /// <summary>Gets the presentation heading in degrees.</summary>
    public float YawDegrees => ((float)(((double)FixedYawRadians) * (180d / Math.PI)));
    /// <summary>Gets the presentation widening of the world scale.</summary>
    public float Scale => ((float)((double)FixedScale));
    /// <summary>Gets the deterministic world rotation.</summary>
    public FixedQuaternion FixedRotation => FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, FixedYawRadians);
    /// <summary>Gets the presentation widening of that same rotation.</summary>
    public Quaternion Rotation => FixedRotation.ToQuaternion();

    /// <summary>Replaces the world origin without narrowing through presentation.</summary>
    /// <param name="position">The new exact origin.</param><returns>The updated frame.</returns>
    public CompiledPlacementFrame WithFixedPosition(FixedVector3 position) => this with { FixedPosition = position };
    /// <summary>Replaces the world scale without narrowing through presentation.</summary>
    /// <param name="scale">The new exact scale.</param><returns>The updated frame.</returns>
    public CompiledPlacementFrame WithFixedScale(FixedQ4816 scale) => this with { FixedScale = scale };
}
/// <summary>Compiles placement parent chains through one deterministic transform composition: the frame table, the
/// spatial query index and the canonical surface frames all resolve a parent chain through <see cref="Compose"/>
/// and its <see cref="LocalFrame"/>, the one place a dealt child's slot offset meets its displacement.</summary>
public static class WorldPlacementFrameCompilation {
    private static readonly IReadOnlyDictionary<string, CompiledPlacementFrame> Empty = new Dictionary<string, CompiledPlacementFrame>(comparer: StringComparer.Ordinal);

    private sealed record FrameTable(ulong WorldSeed, IReadOnlyDictionary<string, CompiledPlacementFrame> Frames);

    private static readonly CodeDerivedTable<WorldPlacementsSection, FrameTable> Cache = new();

    private sealed record FrameInputs(bool Live);

    private static readonly CodeDerivedTable<WorldPlacementsSection, FrameInputs> Inputs = new();

    // Reference spellings are immutable even when the values they read move. Share this derivation across every
    // section cache that otherwise retains a resolved frame, without walking all placements on every lookup.
    internal static bool HasLiveInputs(WorldPlacementsSection section) => Inputs.GetValue(section, static rows =>
        new FrameInputs(Live: (rows.Rows ?? []).Any(predicate: row => ((row.Position.Reference is not null) ||
            ((row.Distribution?.Region is WorldDistributionRegion.Lattice lattice) &&
            ((lattice.StepA.Reference is not null) || (lattice.StepB.Reference is not null))))))).Live;

    /// <summary>Returns a row's local frame in its parent's frame and whether its offset takes the parent's scale. A
    /// dealt child (<see cref="WorldPlacementDeal.IsChild"/>) stands at its slot's fixed-point offset
    /// (<see cref="WorldPlacementDeal.SlotOffset"/>) plus its <see cref="WorldPlacement.Position"/>, which is its
    /// displacement from that slot; it carries its template's scale already and its offsets are metres in the template's
    /// frame, so it inherits no scale. Every other row's local frame is its authored transform, scaled by its parent.
    /// The slot offset never passes through a float, so a child moved to another slot (a recipient's re-deal) stands at
    /// that slot's exact offset plus the same displacement.</summary>
    /// <param name="parent">The row's resolved parent row, or <see langword="null"/> for a root.</param>
    /// <param name="row">The row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>), which a noise or scatter region's
    /// offsets read.</param>
    /// <param name="inheritScale">Whether the parent's scale multiplies the local offset and scale.</param>
    /// <returns>The local frame.</returns>
    public static CompiledPlacementFrame LocalFrame(WorldPlacement? parent, WorldPlacement row, ulong worldSeed, out bool inheritScale) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var local = new CompiledPlacementFrame(row.Position.FixedValue,
            FixedQ4816.FromDegrees(degrees: (row.YawDegrees % 360f)), FixedQ4816.FromDouble(value: row.Scale));

        inheritScale = !WorldPlacementDeal.IsChild(parent: parent, placement: row);
        return (inheritScale ? local : local.WithFixedPosition(position: checked((WorldPlacementDeal.SlotOffset(template: parent!, slot: row.DealSlot, worldSeed: worldSeed) + local.FixedPosition))));
    }
    /// <summary>Returns the value a row's <see cref="WorldPlacement.Position"/> holds for it to stand at
    /// <paramref name="localPosition"/> in its parent's frame: the inverse of <see cref="LocalFrame"/>'s position, so a
    /// dealt child's is its displacement from its slot's offset, taken in fixed point.</summary>
    /// <param name="parent">The row's parent row, or <see langword="null"/> for a root.</param>
    /// <param name="row">The row.</param>
    /// <param name="localPosition">The position in the parent's frame, before the parent's scale.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    /// <returns>The stored position, in fixed point.</returns>
    public static FixedVector3 StoredPosition(WorldPlacement? parent, WorldPlacement row, FixedVector3 localPosition, ulong worldSeed) {
        ArgumentNullException.ThrowIfNull(argument: row);

        return (WorldPlacementDeal.IsChild(parent: parent, placement: row)
            ? checked((localPosition - WorldPlacementDeal.SlotOffset(template: parent!, slot: row.DealSlot, worldSeed: worldSeed)))
            : localPosition);
    }
    /// <summary>Writes a world-space edit back into the row's exact stored translation, undoing its parent frame
    /// and its dealt slot through <see cref="StoredPosition"/>.</summary>
    public static WorldPlacement WithWorldPosition(WorldDefinition definition, WorldPlacement row, FixedVector3 position) {
        var parent = ((row.Parent is { } id) ? WorldDefinitionRows.FindPlacement(definition.Placements, id) : null);

        if (parent is not null) {
            var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: parent);

            position = frame.FixedRotation.RotateInverse(vector: checked((position - frame.FixedPosition)));
            if (!WorldPlacementDeal.IsChild(parent: parent, placement: row)) { position /= frame.FixedScale; }
        }
        return row with { Position = StoredPosition(parent, row, position, (definition.Generation?.WorldSeed ?? 0UL)) };
    }
    /// <summary>Composes a child's local transform (<see cref="LocalFrame"/>) over its parent's canonical fixed frame.</summary>
    /// <param name="parentFrame">The already resolved parent frame.</param>
    /// <param name="parent">The parent row.</param>
    /// <param name="child">The child row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    /// <returns>The exact world frame.</returns>
    /// <exception cref="OverflowException">The composition exceeds the fixed-point range.</exception>
    public static CompiledPlacementFrame Compose(CompiledPlacementFrame parentFrame, WorldPlacement parent, WorldPlacement child, ulong worldSeed) =>
        ComposeCore(parentFrame, LocalFrame(inheritScale: out var inherit, parent: parent, row: child, worldSeed: worldSeed), inherit, null);

    private static CompiledPlacementFrame ComposeCore(CompiledPlacementFrame parent, CompiledPlacementFrame local,
        bool inheritScale, Func<FixedQ4816, bool>? within) {
        var inherited = (inheritScale ? parent.FixedScale : FixedQ4816.One);
        var offset = checked((local.FixedPosition * inherited));

        if ((within is not null) && !Within(value: offset, within: within)) { throw new OverflowException(message: "Placement rotation input exceeds the query range."); }
        return new CompiledPlacementFrame(
            checked((parent.FixedPosition + parent.FixedRotation.Rotate(vector: offset))),
            checked((parent.FixedYawRadians + local.FixedYawRadians)), checked((local.FixedScale * inherited)));
    }
    private static bool Within(Func<FixedQ4816, bool> within, FixedVector3 value) => (within(value.X) && within(value.Y) && within(value.Z));

    // The one parent-chain resolver. A caller with a representability envelope (spatial compilation) passes it as
    // within, which every rotation input, resolved position and scale on the chain must satisfy; the general document
    // read keeps best-effort authored roots for malformed chains until validation reports their named error.
    internal static bool TryResolveFrame(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> byId,
        Dictionary<string, CompiledPlacementFrame> frames, HashSet<string> visiting, ulong worldSeed,
        out CompiledPlacementFrame frame, out string reason, Func<FixedQ4816, bool>? within = null) {
        if (frames.TryGetValue(key: placement.Id, value: out frame)) { reason = string.Empty; return true; }
        if (!visiting.Add(item: placement.Id)) { frame = default; reason = $"placement '{placement.Id}' participates in a parent cycle"; return false; }
        try {
            frame = LocalFrame(inheritScale: out _, parent: null, row: placement, worldSeed: worldSeed);
            if (placement.Parent is { Length: > 0 } parentId) {
                if (!byId.TryGetValue(key: parentId, value: out var parent) ||
                    ((parent.Distribution is not null) && (parent.Deal is null)) || (parent.Mirror is not null) ||
                    (parent.Attach is not null) || (parent.Inhabit is not null)) {
                    frame = default; reason = $"placement '{placement.Id}' requires a declared static single parent frame '{parentId}'"; return false;
                }
                if (!TryResolveFrame(byId: byId, frame: out var parentFrame, frames: frames, placement: parent, reason: out reason, visiting: visiting, within: within, worldSeed: worldSeed)) {
                    frame = default; return false;
                }
                var local = LocalFrame(inheritScale: out var inherit, parent: parent, row: placement, worldSeed: worldSeed);

                frame = ComposeCore(inheritScale: inherit, local: local, parent: parentFrame, within: within);
            }
            if ((within is not null) && (!Within(within, frame.FixedPosition) || !within(frame.FixedScale))) {
                frame = default; reason = $"placement '{placement.Id}' transform exceeds the fixed-point query range"; return false;
            }
            frames[placement.Id] = frame;
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            frame = default; reason = $"placement '{placement.Id}' transform exceeds fixed-point range"; return false;
        } finally { visiting.Remove(item: placement.Id); }
    }

    /// <summary>Compiles rows without retaining a section cache. Invalid parent chains keep their own authored root;
    /// <see cref="TryValidate"/> remains the named refusal door.</summary>
    /// <param name="placements">The authored rows.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>), which a dealt child's slot offset
    /// reads under a noise or scatter region.</param>
    /// <returns>The world frames.</returns>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> Compile(IReadOnlyList<WorldPlacement>? placements, ulong worldSeed) {
        if (placements is not { Count: > 0 }) { return Empty; }
        var byId = new Dictionary<string, WorldPlacement>(capacity: placements.Count, comparer: StringComparer.Ordinal);

        foreach (var row in placements) { byId[row.Id] = row; }
        var frames = new Dictionary<string, CompiledPlacementFrame>(capacity: placements.Count, comparer: StringComparer.Ordinal);
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in placements) {
            if (!TryResolveFrame(row, byId, frames, visiting, worldSeed, out _, out _)) { frames[row.Id] = LocalFrame(inheritScale: out _, parent: null, row: row, worldSeed: worldSeed); }
        }
        return frames;
    }
    /// <summary>Gets a code-aware parent-frame derivation for one immutable placement section under one seed.</summary>
    /// <param name="section">The section, or null.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    /// <returns>The shared frame table.</returns>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> Resolve(WorldPlacementsSection? section, ulong worldSeed) {
        if (section is null) { return Empty; }
        // A bound translation or lattice step changes without replacing its section. Such frames are live views,
        // never immutable-section cache entries.
        if (HasLiveInputs(section: section)) { return Compile(placements: section.Rows, worldSeed: worldSeed); }
        if (Cache.TryGetValue(key: section, value: out var held) && (held.WorldSeed == worldSeed)) { return held.Frames; }
        var table = new FrameTable(worldSeed, Compile(placements: section.Rows, worldSeed: worldSeed));

        Cache.AddOrUpdate(key: section, value: table);
        return table.Frames;
    }

    // A holding-only replacement has the same authored transforms.
    internal static void CarryAcrossHolding(WorldPlacementsSection source, WorldPlacementsSection target) {
        if (Cache.TryGetValue(key: source, value: out var table)) { Cache.AddOrUpdate(key: target, value: table); }
    }

    /// <summary>Rotates an authored vector through the same fixed rotation used by parent composition.</summary>
    /// <param name="vector">The local vector.</param><param name="degrees">The yaw in degrees.</param>
    /// <returns>The widened fixed result.</returns>
    public static Vector3 RotateY(Vector3 vector, float degrees) => new CompiledPlacementFrame(Vector3.Zero, degrees)
        .FixedRotation.Rotate(vector: FixedVector3.FromVector3(value: vector)).ToVector3();
    /// <summary>Validates every placement's <see cref="WorldPlacement.Parent"/> chain, refusing BY NAME the first bad
    /// edge found in authored order: an unknown parent, a self-parent, a parent cycle, a distributed/mirrored parent
    /// (an expanded row has no single frame), or a non-unit-scale parent (composition rotates and translates only)
    /// other than a deal template parenting its own dealt children, whose scale each child carries.</summary>
    public static bool TryValidate(IReadOnlyList<WorldPlacement>? placements, out string reason) {
        reason = string.Empty;

        if (placements is not { Count: > 0 }) {
            return true;
        }

        var byId = new Dictionary<string, WorldPlacement>(
            capacity: placements.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var placement in placements) {
            byId[placement.Id] = placement;
        }

        foreach (var placement in placements) {
            if (placement.Parent is not { Length: > 0 } parentId) {
                continue;
            }

            if (string.Equals(
                a: parentId,
                b: placement.Id,
                comparisonType: StringComparison.Ordinal
            )) {
                reason = $"placement '{placement.Id}' names itself as its own parent.";

                return false;
            }

            if (!byId.TryGetValue(
                key: parentId,
                value: out var parent
            )) {
                reason = $"placement '{placement.Id}' names parent '{parentId}', which is not a declared placement.";

                return false;
            }

            // A dealt template's distribution is the layout its children are dealt over, never an expansion of the
            // template itself, so it stays a single frame to compose against.
            if (
                ((parent.Distribution is not null) && (parent.Deal is null)) ||
                (parent.Mirror is not null)
            ) {
                reason = $"placement '{placement.Id}' names parent '{parentId}', which is distributed/mirrored — a child of an expanded row has no single frame.";

                return false;
            }

            // A dealt template's scale is stamped on its children as its prototype is (each child carries it), and
            // their offsets are metres in its frame, so it scales no child's offset.
            if ((parent.Scale != 1f) && !WorldPlacementDeal.IsChild(parent: parent, placement: placement)) {
                reason = $"placement '{placement.Id}' names parent '{parentId}', whose scale ({parent.Scale}) is not 1 — composition rotates and translates only, never a parent's scale into a child's local offset.";

                return false;
            }

            var chain = new HashSet<string>(comparer: StringComparer.Ordinal) { placement.Id, parent.Id };
            var current = parent;

            while (current.Parent is { Length: > 0 } nextParentId) {
                if (!byId.TryGetValue(
                    key: nextParentId,
                    value: out var next
                )) {
                    // A dangling further ancestor is reported when THAT row is visited directly by the outer loop.
                    break;
                }

                if (!chain.Add(item: next.Id)) {
                    reason = $"placement '{placement.Id}' participates in a parent cycle through '{next.Id}'.";

                    return false;
                }

                current = next;
            }
        }

        return true;
    }
}
