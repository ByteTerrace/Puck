using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Exact body input continuation at a checkpoint or refused-step boundary. A transfer's last applied held
/// image is distinct from these pending one-tick publications and cannot stand in for them on restore.</summary>
public readonly record struct WorldBodyInputState(PlayerIntent Submitted, bool HasSubmitted, PlayerIntent Held,
    PlayerIntent Producer, bool HasProducer, PlayerIntent TransferHeld, bool HasTransferHeld,
    PlayerIntent ReadHeld, PlayerIntent ReadComposed, PlayerIntent ReadScalar, ushort Mapping, ulong Epoch);
