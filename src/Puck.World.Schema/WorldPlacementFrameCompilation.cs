using System.Numerics;
using System.Runtime.CompilerServices;
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
        : this(FixedVector3.FromVector3(Position), FixedQ4816.FromDegrees(YawDegrees % 360f), FixedQ4816.FromDouble(Scale)) { }
    /// <summary>Gets the presentation widening of the world origin.</summary>
    public Vector3 Position => FixedPosition.ToVector3();
    /// <summary>Gets the presentation heading in degrees.</summary>
    public float YawDegrees => (float)((double)FixedYawRadians * (180d / Math.PI));
    /// <summary>Gets the presentation widening of the world scale.</summary>
    public float Scale => (float)(double)FixedScale;
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
    /// <summary>Quantizes one row's local authored transform.</summary>
    /// <param name="placement">The authored row.</param><returns>Its local frame.</returns>
    public static CompiledPlacementFrame FromAuthored(WorldPlacement placement) =>
        new(placement.Position, placement.YawDegrees, placement.Scale);
}

/// <summary>Compiles placement parent chains through one deterministic transform composition. Canonical surface
/// frames reuse <see cref="Compose"/> after resolving their selected ground and parent dependencies.</summary>
public static class WorldPlacementFrameCompilation {
    private static readonly IReadOnlyDictionary<string, CompiledPlacementFrame> Empty = new Dictionary<string, CompiledPlacementFrame>(StringComparer.Ordinal);
    private static readonly CodeDerivedTable<WorldPlacementsSection, StrongBox<IReadOnlyDictionary<string, CompiledPlacementFrame>>> Cache = new();

    /// <summary>Composes a child's authored transform over its parent's canonical fixed frame.</summary>
    /// <param name="parentFrame">The already resolved parent.</param><param name="childRow">The child's local transform.</param>
    /// <param name="inheritParentScale">False for dealt children that already carry their template's scale and metre offsets.</param>
    /// <returns>The exact world frame.</returns>
    public static CompiledPlacementFrame Compose(CompiledPlacementFrame parentFrame, WorldPlacement childRow, bool inheritParentScale = true) =>
        ComposeCore(parentFrame, CompiledPlacementFrame.FromAuthored(childRow), inheritParentScale, null);

    private static CompiledPlacementFrame ComposeCore(CompiledPlacementFrame parent, CompiledPlacementFrame local,
        bool inheritScale, Func<FixedVector3, bool>? rotationRange) {
        var inherited = inheritScale ? parent.FixedScale : FixedQ4816.One;
        var offset = checked(local.FixedPosition * inherited);
        if (rotationRange is not null && !rotationRange(offset)) { throw new OverflowException("Placement rotation input exceeds the query range."); }
        return new CompiledPlacementFrame(
            checked(parent.FixedPosition + parent.FixedRotation.Rotate(offset)),
            checked(parent.FixedYawRadians + local.FixedYawRadians), checked(local.FixedScale * inherited));
    }

    // Shared strict resolver. Spatial compilation supplies its representability envelope; the general document
    // read retains best-effort authored roots for malformed chains until validation reports their named error.
    internal static bool TryResolveFrame(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> byId,
        Dictionary<string, CompiledPlacementFrame> frames, HashSet<string> visiting,
        out CompiledPlacementFrame frame, out string reason, Func<FixedVector3, bool>? rotationRange = null) {
        if (frames.TryGetValue(placement.Id, out frame)) { reason = string.Empty; return true; }
        if (!visiting.Add(placement.Id)) { frame = default; reason = $"placement '{placement.Id}' participates in a parent cycle"; return false; }
        try {
            frame = CompiledPlacementFrame.FromAuthored(placement);
            if (placement.Parent is { Length: > 0 } parentId) {
                if (!byId.TryGetValue(parentId, out var parent) ||
                    (parent.Distribution is not null && parent.Deal is null) || parent.Mirror is not null ||
                    parent.Attach is not null || parent.Inhabit is not null) {
                    frame = default; reason = $"placement '{placement.Id}' requires a declared static single parent frame '{parentId}'"; return false;
                }
                if (!TryResolveFrame(parent, byId, frames, visiting, out var parentFrame, out reason, rotationRange)) {
                    frame = default; return false;
                }
                frame = ComposeCore(parentFrame, frame, !WorldPlacementDeal.IsChild(parent, placement), rotationRange);
            }
            frames[placement.Id] = frame;
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            frame = default; reason = $"placement '{placement.Id}' transform exceeds fixed-point range"; return false;
        } finally { visiting.Remove(placement.Id); }
    }
    /// <summary>Compiles rows without retaining a section cache. Invalid parent chains keep their own authored root;
    /// <see cref="TryValidate"/> remains the named refusal door.</summary>
    /// <param name="placements">The authored rows.</param><returns>The world frames.</returns>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> Compile(IReadOnlyList<WorldPlacement>? placements) {
        if (placements is not { Count: > 0 }) { return Empty; }
        var byId = new Dictionary<string, WorldPlacement>(placements.Count, StringComparer.Ordinal);
        foreach (var row in placements) { byId[row.Id] = row; }
        var frames = new Dictionary<string, CompiledPlacementFrame>(placements.Count, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in placements) {
            if (!TryResolveFrame(row, byId, frames, visiting, out _, out _)) { frames[row.Id] = CompiledPlacementFrame.FromAuthored(row); }
        }
        return frames;
    }
    /// <summary>Gets a code-aware parent-frame derivation for one immutable placement section.</summary>
    /// <param name="section">The section, or null.</param><returns>The shared frame table.</returns>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> Resolve(WorldPlacementsSection? section) => section is null ? Empty :
        Cache.GetValue(section, static s => new StrongBox<IReadOnlyDictionary<string, CompiledPlacementFrame>>(Compile(s.Rows))).Value!;
    // A holding-only replacement has the same authored transforms.
    internal static void CarryAcrossHolding(WorldPlacementsSection source, WorldPlacementsSection target) {
        if (Cache.TryGetValue(source, out var table)) { Cache.AddOrUpdate(target, table); }
    }
    /// <summary>Rotates an authored vector through the same fixed rotation used by parent composition.</summary>
    /// <param name="vector">The local vector.</param><param name="degrees">The yaw in degrees.</param>
    /// <returns>The widened fixed result.</returns>
    public static Vector3 RotateY(Vector3 vector, float degrees) => new CompiledPlacementFrame(Vector3.Zero, degrees)
        .FixedRotation.Rotate(FixedVector3.FromVector3(vector)).ToVector3();
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
