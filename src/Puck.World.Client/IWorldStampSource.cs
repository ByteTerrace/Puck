using System.Numerics;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Client;

/// <summary>
/// The world a <see cref="WorldCreationInstances"/> roots its stamps on: the definition it stamps, each entity's live pose and
/// facts, and the state its looks read. The local world's <see cref="WorldClient"/> is one; a session view's
/// <see cref="WorldSessionStampSource"/>, over the destination's mirror, is the other, so a session screen draws its
/// destination's animated, inhabited and attached creations through the same pool the local scene does.
/// </summary>
public interface IWorldStampSource {
    /// <summary>Gets the definition the stamps are registered from.</summary>
    WorldDefinition Definition { get; }
    /// <summary>Gets the state mirror a look's lanes and a body's live scale read.</summary>
    WorldStateMirror StateMirror { get; }

    /// <summary>The contact-sampled size of a solid placement, when the authority publishes one.</summary>
    float? SolidScale(string placementId);

    /// <summary>Gets the static solid field an effector plants against, or <see langword="null"/> when this world
    /// carries none.</summary>
    WorldQueryField? StaticField { get; }
    /// <summary>Gets the presenting seat's guess on its own input for the world this source presents, or
    /// <see langword="null"/> unless the client presents exactly one seat and that seat's body is in this world
    /// (<see cref="WorldClient.SeatGuessAt"/>). A stamp pool reads it into its guess layer alone
    /// (<see cref="WorldCreationInstances.GuessOverlay"/>).</summary>
    IWorldSeatGuess? SeatGuess => null;
    /// <summary>Gets the journal of the entities whose presented inputs moved, a frame at a time, its rows the entity
    /// indices: an entity's <see cref="Position"/>, <see cref="Orientation"/>, <see cref="PoseEpoch"/>,
    /// <see cref="EntityAddress"/>, <see cref="Carries"/>, <see cref="Facts"/> or <see cref="IsActive"/> answers
    /// differently only on a frame that names it, so a consumer holding a cursor visits only the entities that changed
    /// since it last looked. <see langword="null"/>, the default, for a source that keeps none: its consumer asks every
    /// entity it draws each frame.</summary>
    Puck.Abstractions.Gpu.GpuRowChanges? BodyChanges => null;
    /// <summary>Gets the serial of the latest frame <see cref="BodyChanges"/> holds, which a consumer collects
    /// through; zero for a source that keeps no journal.</summary>
    long BodyChangesSerial => 0L;
    /// <summary>Gets the number of entity slots the source holds: the highest body a delivery named, rounded up as the
    /// source grows with its world. An entity past it is inactive.</summary>
    int EntityCapacity { get; }

    /// <summary>Appends the index of every active entity, in ascending order, so a consumer that visits each active
    /// entity never asks every index of the capacity. The default, for a source that keeps no list of them, asks every
    /// index below <see cref="EntityCapacity"/>.</summary>
    /// <param name="bodies">The list the indices are appended to.</param>
    void CopyActiveBodies(List<int> bodies) {
        ArgumentNullException.ThrowIfNull(argument: bodies);

        for (var index = 0; (index < EntityCapacity); index++) {
            if (IsActive(index: index)) {
                bodies.Add(item: index);
            }
        }
    }

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

    /// <summary>Gets the in-place portal crossings each entity has made
    /// (<see cref="EntityContinuityKind.Carry"/>): a consumer that latched a world-space pose for an entity at one
    /// crossing count carries it to the current count rather than seeing a jump.</summary>
    WorldPoseCarries Carries { get; }

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
