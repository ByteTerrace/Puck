using System.Numerics;
using Puck.SignedDistance.Queries;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Client;

/// <summary>
/// The world a <see cref="WorldStampPool"/> roots its stamps on: the definition it stamps, each entity's live pose and
/// facts, and the state its looks read. The local world's <see cref="WorldClient"/> is one; a session view's
/// <see cref="WorldSessionStampSource"/>, over the destination's mirror, is the other, so a session screen draws its
/// destination's animated, inhabited and attached creations through the same pool the local scene does.
/// </summary>
public interface IWorldStampSource {
    /// <summary>Gets the definition the stamps are registered from.</summary>
    WorldDefinition Definition { get; }
    /// <summary>Gets the state mirror a look's lanes and a body's live scale read.</summary>
    WorldStateMirror StateMirror { get; }
    /// <summary>Gets the static solid field an effector plants against, or <see langword="null"/> when this world
    /// carries none.</summary>
    SdfFieldEvaluator? StaticField { get; }

    /// <summary>Gets an entity's entity-and-generation address, which a follower reseeds on when it changes.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The address.</returns>
    WorldEntityAddress EntityAddress(int index);
    /// <summary>Gets an entity's latest fact mask.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The facts.</returns>
    BodyFacts Facts(int index);
    /// <summary>Determines whether an entity is active this frame.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the entity is drawn.</returns>
    bool IsActive(int index);
    /// <summary>Gets the look an entity wears.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The resolved look.</returns>
    WorldLook Look(int index);
    /// <summary>Gets an entity's presented orientation.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The orientation.</returns>
    Quaternion Orientation(int index);
    /// <summary>Gets the placement an entity inhabits.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The placement id, or <see langword="null"/> when the entity inhabits none.</returns>
    string? PlacementId(int index);
    /// <summary>Gets an entity's pose epoch, which moves when its pose is discontinuous (it became active or
    /// teleported), so a follower seeds rather than eases across the jump.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The epoch.</returns>
    int PoseEpoch(int index);
    /// <summary>Gets an entity's presented position.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The position.</returns>
    Vector3 Position(int index);
    /// <summary>Resolves the active entity a placement's first inhabited body occupies.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="index">The 0-based entity index, or -1 when no active entity inhabits it.</param>
    /// <returns><see langword="true"/> when an active entity inhabits the placement.</returns>
    bool TryInhabitantBody(string placementId, out int index);
}
