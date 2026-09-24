namespace Puck.World.Server;

/// <summary>The runtime's own checkpointed state — see <see cref="WorldInputHoldRuntime.Capture"/>.</summary>
public sealed record WorldInputHoldCheckpoint(int MaximumSetter, IReadOnlyList<WorldInputHoldParticipantCheckpoint> Participants);
