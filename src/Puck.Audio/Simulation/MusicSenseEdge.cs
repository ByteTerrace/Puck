namespace Puck.Audio.Simulation;

/// <summary>The world-events vocabulary a <see cref="MusicDirector"/> can key a transition on — an audio-owned
/// mirror of the eight edge kinds a world's own event feed computes, kept as an independent enum so this project
/// never references the project that declares the original (a lower-rank engine-services project cannot reference
/// the world-server rank above it).</summary>
public enum MusicSenseFamily : byte {
    /// <summary>A body entered a named region.</summary>
    RegionEnter,
    /// <summary>A body left a named region.</summary>
    RegionExit,
    /// <summary>A seat became human-occupied.</summary>
    SeatJoin,
    /// <summary>A seat stopped being human-occupied.</summary>
    SeatLeave,
    /// <summary>Two bodies began overlapping.</summary>
    CollisionBegin,
    /// <summary>Two bodies stopped overlapping.</summary>
    CollisionEnd,
    /// <summary>A route (possession/mirror/machine engagement) was established.</summary>
    RouteEngaged,
    /// <summary>A route was dissolved.</summary>
    RouteDisengaged,
}
/// <summary>One projected sense edge for the current tick — the minimal shape <see cref="MusicDirector"/> reads to
/// evaluate a transition's <c>when</c> condition. Carries no grant-gating fields: music state is never
/// addon-observation-filtered, unlike the world-scoped event feed this mirrors. The host projects its own edge list
/// into this shape at its own call site; nothing here resolves or subscribes to anything.</summary>
/// <param name="Family">The event family.</param>
/// <param name="A">The first payload lane (a body/seat index).</param>
/// <param name="B">The second payload lane (a region ordinal or encoded route target); zero when unused.</param>
public readonly record struct MusicSenseEdge(MusicSenseFamily Family, long A, long B);
