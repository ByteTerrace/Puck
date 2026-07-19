using System.Text.Json.Serialization;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// WHERE a placeable thing rides — the shared pose-target vocabulary a placeable camera
/// (<see cref="WorldCamera.Anchored"/>) and a future placeable speaker both consume, distinct from HOW the thing
/// looks at or emits from that pose (a rig, a feed). The <c>$type</c> string is the JSON discriminator, matching
/// <see cref="WorldCamera"/>'s convention; a new anchor kind is a new derived record plus its
/// <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldAnchor.Entity), typeDiscriminator: "entity")]
[JsonDerivedType(typeof(WorldAnchor.EntityLeaf), typeDiscriminator: "entityLeaf")]
[JsonDerivedType(typeof(WorldAnchor.Placement), typeDiscriminator: "placement")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldAnchor {
    private WorldAnchor() {
    }

    /// <summary>Rides one population entity's ROOT pose — a walking avatar's whole-body position and orientation.</summary>
    /// <param name="Index">The 0-based entity index (validated 0..<see cref="WorldPopulation.MaxPopulation"/>-1).</param>
    internal sealed record Entity(int Index) : WorldAnchor;

    /// <summary>Rides one entity's LEAF pose — a single bone of its humanoid rig (a held brick, a shoulder-mounted
    /// eye) rather than the whole-body root. <paramref name="Leaf"/> is a closed role TOKEN
    /// (<see cref="WorldAvatarCatalog.HumanoidAnchorRoles"/>) — the data vocabulary is the ROLE, never the engine's
    /// dynamic-transform slot (a packing detail, <c>AvatarRange.First</c> + role) or a raw bone ordinal (leaf counts
    /// vary 12..20 per avatar; only the first 12 — one per role — are guaranteed present). See that catalog's
    /// remarks for the leaf-pose resolution this union's consumers can reach today.</summary>
    /// <param name="Index">The 0-based entity index.</param>
    /// <param name="Leaf">The humanoid role token, kebab-case (e.g. <c>"left-hand"</c>).</param>
    internal sealed record EntityLeaf(int Index, string Leaf) : WorldAnchor;

    /// <summary>Rides a placement INSTANCE's stamped transform — a creation stamped into the world by reference (the
    /// same placement-reference shape <see cref="Puck.Authoring.CreationCameraDocument"/> uses), optionally narrowed
    /// to one of its own authored shapes rather than the stamp's root.</summary>
    /// <param name="PlacementId">The referenced <see cref="WorldPlacement.Id"/> (must resolve).</param>
    /// <param name="ShapeId">The referenced creation's <c>ShapeDocument.Id</c> to ride, or <see langword="null"/> for
    /// the placement's own stamped root transform.</param>
    internal sealed record Placement(string PlacementId, int? ShapeId) : WorldAnchor;
}
