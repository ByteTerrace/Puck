using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One federated entity slot's replicated device image and the stream lease that owns it.</summary>
/// <param name="LeaseId">The server-minted stream lease; an older socket's teardown can never clear a replacement
/// socket's state after a reconnect.</param>
/// <param name="Principal">The authenticated principal driving the slot.</param>
/// <param name="Submission">The accepted held image, with transient events and relative displacement consumed.</param>
/// <param name="Active">Whether the slot currently holds a live stream.</param>
/// <param name="Pending">Ordered, owned packets awaiting the next authoritative interval.</param>
public readonly record struct WorldFederatedIntentState(long LeaseId, Principal Principal, IntentSubmission? Submission, bool Active,
    ReadOnlyMemory<IntentSubmission> Pending = default);

/// <summary>Replay/checkpoint state for one federated owner. Socket lease identities remain transport-local.</summary>
public readonly record struct WorldFederatedIntentCheckpoint(int Index, Principal Principal, IntentSubmission? Submission,
    ReadOnlyMemory<IntentSubmission> Pending);
