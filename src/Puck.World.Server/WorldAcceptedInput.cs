using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The authority's derived body fold after timed admission or an already admitted body route.
/// This is not a transport or replay submission; held values are derived from the semantic input mapping.</summary>
public readonly record struct WorldAcceptedInput(ulong Tick, int EntityIndex, PlayerIntent Intent, Principal Principal,
    PlayerIntent HeldChannels = default, int MeasuredHoldTicks = 0);
