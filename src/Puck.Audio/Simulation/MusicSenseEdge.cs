namespace Puck.Audio.Simulation;

/// <summary>The conditions a <see cref="MusicDirector"/> can key a transition, layer or embellishment on — the
/// ambient world events a recipient is told about plus the rule cues delivered to it. An audio-owned enum, so this
/// project never references the world-server rank that computes the events.</summary>
public enum MusicSenseFamily : byte {
    /// <summary>A body entered a named region.</summary>
    RegionEnter,
    /// <summary>A body left a named region.</summary>
    RegionExit,
    /// <summary>A seat became human-occupied.</summary>
    SeatJoin,
    /// <summary>An authored rule cue delivered to this listener.</summary>
    RuleCue,
}
/// <summary>One sense edge for the current tick — the minimal shape <see cref="MusicDirector"/> reads to evaluate a
/// <c>when</c> condition. The host builds the list from what its recipient was delivered; nothing here resolves or
/// subscribes to anything.</summary>
/// <param name="Family">The condition's family.</param>
/// <param name="Cue">The delivered cue's name, when the family is <see cref="MusicSenseFamily.RuleCue"/>.</param>
public readonly record struct MusicSenseEdge(MusicSenseFamily Family, string? Cue = null);
