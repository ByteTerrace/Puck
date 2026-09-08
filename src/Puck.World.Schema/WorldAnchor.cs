using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>
/// WHERE a placeable thing rides — the one shared pose-target vocabulary a placeable <see cref="WorldCamera"/> and a
/// placeable <see cref="WorldSpeaker"/> both consume through the SAME resolver, distinct from HOW the thing looks at or
/// emits from that pose (a <see cref="WorldCameraProgram"/>, a feed). The <c>$type</c> string is the JSON discriminator; a new
/// anchor kind is a new derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldAnchor.Entity), typeDiscriminator: "entity")]
[JsonDerivedType(typeof(WorldAnchor.EntityPart), typeDiscriminator: "entityPart")]
[JsonDerivedType(typeof(WorldAnchor.Placement), typeDiscriminator: "placement")]
[JsonDerivedType(typeof(WorldAnchor.Group), typeDiscriminator: "group")]
[JsonDerivedType(typeof(WorldAnchor.Seat), typeDiscriminator: "seat")]
[JsonDerivedType(typeof(WorldAnchor.RecentSpeaker), typeDiscriminator: "recentSpeaker")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldAnchor {
    private WorldAnchor() {
    }

    /// <summary>Rides one population entity's ROOT pose — a walking avatar's whole-body position and orientation.</summary>
    /// <param name="Index">The 0-based entity index, bounded by the world's authored population capacity.</param>
    public sealed record Entity(int Index) : WorldAnchor;
    /// <summary>Rides one entity look's authored part pose rather than its whole-body root. The active look publishes
    /// the mapping from <paramref name="PartId"/> to its packed transform slot; the slot remains an engine detail.</summary>
    /// <param name="Index">The 0-based entity index.</param>
    /// <param name="PartId">The ordinal, case-sensitive part identifier published by the entity's active look.</param>
    public sealed record EntityPart(int Index, string PartId) : WorldAnchor;
    /// <summary>Rides a placement INSTANCE's stamped transform — a creation stamped into the world by reference (the
    /// same placement-reference shape <see cref="Puck.World.Authoring.CreationCameraDocument"/> uses), optionally narrowed
    /// to one of its own authored shapes rather than the stamp's root.</summary>
    /// <param name="PlacementId">The referenced <see cref="WorldPlacement.Id"/> (must resolve).</param>
    /// <param name="ShapeId">The referenced creation's <c>ShapeDocument.Id</c> to ride, or <see langword="null"/> for
    /// the placement's own stamped root transform.</param>
    public sealed record Placement(string PlacementId, int? ShapeId) : WorldAnchor;
    /// <summary>Rides the smoothed CENTROID of a set of population entities — the establishing-shot anchor. Also
    /// publishes the set's SPREAD (mean distance from the centroid), which a camera program's
    /// <see cref="WorldCameraProgramOp.Offset"/> consumes through its
    /// <see cref="WorldCameraProgramOp.Offset.SpreadPullback"/>. A group has no facing, so its orientation resolves to
    /// identity.</summary>
    /// <param name="Indices">The 0-based entity indices in the set, or <see langword="null"/> for the whole live
    /// population (every active entity). Each index is validated against the authored population capacity.</param>
    /// <param name="SmoothRate">The exponential smoothing rate (per second) the centroid/spread ease at (validated
    /// positive and finite) — seeded un-smoothed on first resolve so a camera does not fly in from the origin.</param>
    public sealed record Group(IReadOnlyList<int>? Indices, float SmoothRate) : WorldAnchor;
    /// <summary>Rides a local seat's avatar — the body the seat perceives as its own, so possession follows — at its
    /// root or at a named part. <paramref name="Number"/> <see langword="null"/> is the enclosing seat scope (the seat
    /// a HUD frame or view is being resolved for), an explicit number is 1-based. Presentation-only: a camera or
    /// speaker on this anchor is resolved per seat.</summary>
    public sealed record Seat(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Number = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PartId = null
    ) : WorldAnchor;
    /// <summary>Rides the body that most recently spoke (see <c>OverlayPredicate.Speaking</c>), at its root or a named
    /// part; resolves nothing until something has spoken.</summary>
    public sealed record RecentSpeaker([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PartId = null) : WorldAnchor;
}
