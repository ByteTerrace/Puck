namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's audio director a session-lever sink needs. Declared here so a
/// Client-side type can hold the write without naming the root's concrete audio-director type — the same shape
/// <see cref="IWorldSimulationClock"/> and <see cref="IWorldScreenPresenter"/> already carry for the frame source's
/// other root-held dependencies.</summary>
public interface IWorldAudioLever {
    /// <summary>Sets the live master volume the session lever owns for the rest of the session.</summary>
    /// <param name="value">The new master volume.</param>
    void SetMasterVolume(float value);
}
