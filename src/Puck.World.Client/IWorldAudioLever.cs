namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's audio director a session-lever sink and the save fold need.
/// Declared here so a Client-side type can hold the lever without naming the root's concrete audio-director type — the
/// same shape <see cref="IWorldSimulationClock"/> and <see cref="IWorldScreenPresenter"/> already carry for the frame
/// source's other root-held dependencies.</summary>
public interface IWorldAudioLever {
    /// <summary>Gets the master volume the session lever set, or <see langword="null"/> while the lever is unengaged
    /// and the document's master gain owns the mix. <see cref="WorldSessionLevers.Fold"/> reads this.</summary>
    float? SessionMasterVolume { get; }

    /// <summary>Sets the live master volume the session lever owns for the rest of the session.</summary>
    /// <param name="value">The new master volume.</param>
    void SetMasterVolume(float value);
}
