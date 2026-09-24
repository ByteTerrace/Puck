using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One federated entity slot's replicated device image and the stream lease that owns it.</summary>
/// <param name="LeaseId">The server-minted stream lease; an older socket's teardown can never clear a replacement
/// socket's state after a reconnect.</param>
/// <param name="Principal">The authenticated principal driving the slot.</param>
/// <param name="Submission">The latest image, republished on every authority tick until the stream changes it or
/// disconnects.</param>
/// <param name="Active">Whether the slot currently holds a live stream.</param>
public readonly record struct WorldFederatedIntentState(long LeaseId, Principal Principal, IntentSubmission Submission, bool Active);
