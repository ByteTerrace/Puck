using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The complete observable head of a committed traveler route. A forwarding chain returns the final
/// writer's address, clock, and pose in one answer so input and presentation can advance to the same authority
/// epoch without an inactive-body interval.</summary>
public readonly record struct WorldAuthorityRouteDescription(
    string Endpoint,
    WorldEntityAddress Entity,
    ulong Tick,
    FixedVector3 Position,
    FixedQuaternion Orientation,
    Vector3 BodyColor,
    byte Kit,
    byte Look,
    byte CatalogRig,
    string? PlacementId,
    WorldDefinition Definition
);
