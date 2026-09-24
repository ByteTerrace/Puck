using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One outstanding (reserved, not yet committed) lease's checkpointed state.</summary>
public sealed record WorldTransferLeaseCheckpoint(WorldTransferKey Key, WorldTransferReservationRequest Request, ulong DeadlineTick, int[] Slots, byte[] DestinationDefinitionJson, WorldAdmissionVerdict? Arrival);
