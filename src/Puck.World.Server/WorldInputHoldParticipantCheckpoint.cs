using Puck.Commands;

namespace Puck.World.Server;

/// <summary>One participant slot's checkpointed hold state — see <see cref="WorldInputHoldRuntime.Capture"/>.</summary>
public readonly record struct WorldInputHoldParticipantCheckpoint(
    bool Active,
    Principal Principal,
    int Measured,
    int Target,
    int Applied,
    int LowerTarget,
    int LowerStableTicks,
    int HistoryStart,
    IReadOnlyList<WorldSubmittedInput> History
);
