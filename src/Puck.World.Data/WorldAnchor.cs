using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>
/// WHERE a placeable thing rides — the one shared pose-target vocabulary a placeable <see cref="WorldCamera"/> and a
/// placeable <see cref="WorldSpeaker"/> both consume through the SAME resolver, distinct from HOW the thing looks at or
/// emits from that pose (a <see cref="WorldCameraRig"/>, a feed). The <c>$type</c> string is the JSON discriminator; a new
/// anchor kind is a new derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldAnchor.Entity), typeDiscriminator: "entity")]
[JsonDerivedType(typeof(WorldAnchor.EntityPart), typeDiscriminator: "entityPart")]
[JsonDerivedType(typeof(WorldAnchor.Placement), typeDiscriminator: "placement")]
[JsonDerivedType(typeof(WorldAnchor.Group), typeDiscriminator: "group")]
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
    /// same placement-reference shape <see cref="Puck.Forge.Authoring.CreationCameraDocument"/> uses), optionally narrowed
    /// to one of its own authored shapes rather than the stamp's root.</summary>
    /// <param name="PlacementId">The referenced <see cref="WorldPlacement.Id"/> (must resolve).</param>
    /// <param name="ShapeId">The referenced creation's <c>ShapeDocument.Id</c> to ride, or <see langword="null"/> for
    /// the placement's own stamped root transform.</param>
    public sealed record Placement(string PlacementId, int? ShapeId) : WorldAnchor;

    /// <summary>Rides the smoothed CENTROID of a set of population entities — the establishing-shot anchor. Also
    /// publishes the set's SPREAD (mean distance from the centroid), which <see cref="WorldCameraMotion.Follow"/> consumes
    /// through its <c>SpreadPullback</c>. A group has no facing, so its orientation resolves to identity.</summary>
    /// <param name="Indices">The 0-based entity indices in the set, or <see langword="null"/> for the whole live
    /// population (every active entity). Each index is validated 0..127.</param>
    /// <param name="SmoothRate">The exponential smoothing rate (per second) the centroid/spread ease at (validated
    /// positive and finite) — seeded un-smoothed on first resolve so a camera does not fly in from the origin.</param>
    public sealed record Group(IReadOnlyList<int>? Indices, float SmoothRate) : WorldAnchor;
}
