using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>Where a <see cref="WorldLook"/> resolves an entity's appearance from — a pinned catalog rig or a sculpted
/// creation. The appearance peer of a way of moving: a new way of looking is a row, never a new renderer.</summary>
[JsonDerivedType(typeof(WorldLookSource.Catalog), typeDiscriminator: "catalog")]
[JsonDerivedType(typeof(WorldLookSource.Creation), typeDiscriminator: "creation")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldLookSource {
    private WorldLookSource() { }

    /// <summary>The procedural humanoid catalog (<c>WorldAvatarCatalog</c>) — one look source among others.</summary>
    /// <param name="Index">The procedural renderer catalog rig to pin, or
    /// <see langword="null"/> for the occupant-owned pick. A fresh occupant seeds that pick from its first local
    /// slot and carries it across authority transfers, so ordinary admission does not restyle it.</param>
    public sealed record Catalog(int? Index) : WorldLookSource {
        /// <summary>The procedural renderer's fixed rig count.</summary>
        public const int RigCount = 128;
    }
    /// <summary>A sculpted creation worn by the body — resolved against the world's <see cref="WorldCreation"/> rows.</summary>
    /// <param name="CreationId">The referenced <see cref="WorldCreation.Id"/>, authored literally or through a Text
    /// state cell; it must resolve at validation.</param>
    public sealed record Creation(DocumentIdentifier CreationId) : WorldLookSource;
}
/// <summary>How a look animates with the body it clothes. presentation-only: read by the client's stamp pool and the
/// catalog packer, never by <c>WorldBody</c>. Catalog looks read <see cref="GaitAmplitude"/>; creation looks read
/// <see cref="ReplayFrames"/> and <see cref="SecondsPerFrame"/>.</summary>
/// <param name="GaitAmplitude">The catalog rig's limb-swing scale (1 = the pre-look default; 0 stills the gait).</param>
/// <param name="ReplayFrames">Whether a creation look replays its authored timeline on the render clock.</param>
/// <param name="SecondsPerFrame">The creation timeline cadence when <see cref="ReplayFrames"/> is set.</param>
public readonly record struct WorldLookMotion(float GaitAmplitude, bool ReplayFrames, float SecondsPerFrame) {
    /// <summary>Gets the implicit look motion — full gait, no timeline replay — every body wore before this arc.</summary>
    public static WorldLookMotion Default { get; } = new WorldLookMotion(
        GaitAmplitude: 1f,
        ReplayFrames: false,
        SecondsPerFrame: 0f
    );
}
/// <summary>One look row — the appearance peer of <see cref="WorldKit"/>'s way of moving. Every appearance a world
/// offers is a row of this data, never a renderer branch; <c>world.looks</c> prints these names.</summary>
/// <param name="Name">The look's stable kebab-case name, authored literally or through a Text state cell; it is unique
/// within the definition and assignable by the look table.</param>
/// <param name="Source">Where the appearance resolves from (a catalog rig or a creation).</param>
/// <param name="Scale">The uniform render scale. Appearance only — it does not resize the body's motion tuning or its
/// collision volume.</param>
/// <param name="Motion">How the look animates with the body (see <see cref="WorldLookMotion"/>).</param>
public sealed record WorldLook(DocumentIdentifier Name, WorldLookSource Source, float Scale, WorldLookMotion Motion) {
    /// <summary>Gets the implicit single look every body wears when a world authors no <c>looks</c> section — the
    /// occupant-owned catalog pick at full gait.</summary>
    public static WorldLook Implicit { get; } = new WorldLook(
        Name: "catalog",
        Source: new WorldLookSource.Catalog(Index: null),
        Scale: 1f,
        Motion: WorldLookMotion.Default
    );
}
