namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's audio director the instrument-clock session lever needs —
/// mirrors <see cref="IWorldAudioLever"/>'s one-method shape. Presentation-only, like every session lever
/// (<c>WorldSessionLever</c>'s own remarks): the simulation-side clock fold this lever's console verb narrates is
/// gated by holding the screen application itself (<c>Server.WorldServer.InstrumentClockBoundary</c>), never by
/// this write.</summary>
public interface IWorldInstrumentClockLever {
    /// <summary>Records whether <paramref name="seat"/> has asked its engaged instrument to be treated as the
    /// session's reference clock, for presentation to echo (e.g. a future HUD cue) — carries no simulation effect.</summary>
    /// <param name="seat">The 0-based local seat the lever names.</param>
    /// <param name="engaged">Whether the knob is on.</param>
    void SetInstrumentClockEngaged(int seat, bool engaged);
}
