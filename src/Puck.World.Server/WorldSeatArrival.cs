using System.Numerics;
using Puck.Commands;

namespace Puck.World.Server;

/// <summary>One local seat a committed transfer lands at its reserved slot: the session join and everything the
/// destination writes onto the occupant after it. <see cref="WorldTransferEscrow.LandSeat"/> applies it, for a live commit
/// and for a replay's re-drive alike, and <see cref="WorldServer.ArrivalTap"/> reports each one a live commit landed.</summary>
/// <param name="Slot">The reserved local seat.</param>
/// <param name="Principal">The principal the seat joins under.</param>
/// <param name="Profile">The profile the occupant is seated on, or <see langword="null"/>.</param>
/// <param name="BodyColor">The occupant's body color.</param>
/// <param name="CatalogRig">The occupant's catalog rig.</param>
/// <param name="Mobility">The occupant's committed mobility identity.</param>
/// <param name="Border">The border the occupant was admitted across.</param>
/// <param name="Member">The commit member: the arrival's motion and accumulated arrival turn. Its own profile is not
/// read; <paramref name="Profile"/> is the one the occupant is seated on.</param>
public sealed record WorldSeatArrival(int Slot, Principal Principal, WorldIdentity? Profile, Vector3 BodyColor, byte CatalogRig, WorldMobilityIdentity Mobility, string Border, WorldTransferCommitMember Member);
