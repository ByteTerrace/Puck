using System.Numerics;
using Puck.Commands;

namespace Puck.World.Server;

/// <summary>One traveler a transfer commit lands at its reserved index: its admission, then everything the destination
/// writes onto the occupant after it. A local seat is admitted by its session join under <paramref name="Principal"/>; a
/// transferred peer or entity is admitted by the escrow's verified admission, whose
/// <see cref="Puck.World.Protocol.WorldServerEvent.PeerAdmitted"/> event a recording holds ahead of the arrival.
/// <see cref="WorldTransferEscrow.LandArrival"/> and <see cref="WorldTransferEscrow.RollBackArrival"/> are the landing and
/// the rollback a live commit and a replay's re-drive both apply, and <see cref="WorldServer.ArrivalTap"/> reports each
/// landing once the commit decides.</summary>
/// <param name="Slot">The reserved index.</param>
/// <param name="Generation">The destination occupant generation minted by this admission.</param>
/// <param name="Principal">The principal a local seat joins under.</param>
/// <param name="Peer">Whether a transferred peer or entity arrives, admitted by its event, rather than a local
/// seat.</param>
/// <param name="Profile">The profile the occupant is seated on, or <see langword="null"/>.</param>
/// <param name="BodyColor">The occupant's body color.</param>
/// <param name="CatalogRig">The occupant's catalog rig.</param>
/// <param name="Mobility">The occupant's committed mobility identity.</param>
/// <param name="Border">The border the occupant was admitted across.</param>
/// <param name="Member">The commit member: the arrival's motion and accumulated arrival turn. Its own profile is not
/// read; <paramref name="Profile"/> is the one the occupant is seated on.</param>
public sealed record WorldArrival(int Slot, int Generation, Principal Principal, bool Peer, WorldIdentity? Profile, Vector3 BodyColor, byte CatalogRig, WorldMobilityIdentity Mobility, string Border, WorldTransferCommitMember Member);
