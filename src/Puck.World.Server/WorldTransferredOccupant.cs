using Puck.Maths;

namespace Puck.World.Server;

/// <summary>What a transferred occupant brings to its destination's admission: its own procedural appearance rig and
/// its accumulated arrival turn (<see cref="WorldFrameIsometry.AccumulateTurn"/>). Admission writes both onto the
/// occupant before it takes the <see cref="Puck.World.Protocol.WorldServerEvent.PeerAdmitted"/> event, so the event a tape records carries
/// what the live destination holds.</summary>
/// <param name="CatalogRig">The occupant's catalog rig.</param>
/// <param name="TravelTurn">The occupant's accumulated arrival turn.</param>
public readonly record struct WorldTransferredOccupant(byte CatalogRig, FixedQ4816 TravelTurn);
