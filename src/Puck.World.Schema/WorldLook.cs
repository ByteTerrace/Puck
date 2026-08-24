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
    public sealed record Creation([property: System.Text.Json.Serialization.JsonPropertyName("prototypeId")] DocumentIdentifier CreationId) : WorldLookSource;
}
/// <summary>One cue of a creation look: a named timeline frame the body holds for <paramref name="HoldSeconds"/>
/// when the cue fires — a blink, a twitch, a tail flick. A cue fires by itself on a semi-random interval drawn
/// uniformly from <paramref name="MinSeconds"/>..<paramref name="MaxSeconds"/> (a deterministic presentation draw
/// seeded by the body, never simulation state), and fires on demand through the stamp pool's
/// <c>TriggerCue</c> door, which is how a face probe reading the player's own camera blinks the avatar in sync; a
/// triggered cue re-arms the interval, so a driven cue never double-fires with the self schedule. Both interval
/// bounds absent is a cue that fires only on demand.</summary>
/// <param name="Frame">The creation timeline frame's name.</param>
/// <param name="HoldSeconds">How long the frame holds, seconds.</param>
/// <param name="MinSeconds">The shortest rest between self-fires, seconds, or <see langword="null"/> with
/// <paramref name="MaxSeconds"/> for a cue that never self-fires.</param>
/// <param name="MaxSeconds">The longest rest between self-fires, seconds.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldLookCue(
    string Frame,
    float HoldSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? MinSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? MaxSeconds = null
);
/// <summary>How a look animates with the body it clothes. presentation-only: read by the client's stamp pool and the
/// catalog packer, never by <c>WorldBody</c>. Catalog looks read <see cref="GaitAmplitude"/>; creation looks read
/// <see cref="ReplayFrames"/>, <see cref="SecondsPerFrame"/>, and <see cref="Cues"/>.</summary>
/// <param name="GaitAmplitude">The catalog rig's limb-swing scale (1 = the pre-look default; 0 stills the gait).</param>
/// <param name="ReplayFrames">Whether a creation look replays its authored timeline on the render clock.</param>
/// <param name="SecondsPerFrame">The creation timeline cadence when <see cref="ReplayFrames"/> is set.</param>
/// <param name="Cues">The creation look's cues (see <see cref="WorldLookCue"/>); a firing cue's frame overrides the
/// replay cursor for its hold.</param>
/// <param name="Dynamics">The <c>dynamics</c> row a second-order follower drives the stamped ROOT through — the
/// client's interpolated body pose is the target, the follower's output is what renders. <see langword="null"/>
/// (the default) is today's behavior: the root follows the interpolated pose exactly, no lag.</param>
/// <param name="PartDynamics">A creation part id to <c>dynamics</c> row map — each named part follows the root's own
/// resolved transform with its own personality, layered ON TOP of <see cref="Dynamics"/>. Legitimate only on a
/// creation source (a catalog rig exports no parts); each key must name a part the creation's own part table
/// declares.</param>
public readonly record struct WorldLookMotion(
    float GaitAmplitude,
    bool ReplayFrames,
    float SecondsPerFrame,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldLookCue>? Cues = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dynamics = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? PartDynamics = null
) {
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
