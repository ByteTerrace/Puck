using Puck.Audio.Simulation;

namespace Puck.World.Server;

/// <summary>Names the ambient world events a recipient is told about as sense families — the vocabulary-aware conversion
/// <c>Puck.Audio</c> itself cannot perform (it cannot reference the project that declares <see cref="WorldEventFamily"/>).
/// A score's own compilation is <c>Puck.World.Client.MusicScoreCompiler</c>'s.</summary>
public static class MusicDirectorFactory {
    /// <summary>Gets the sense family a world event delivers to a score as, or <see langword="null"/> for an event the
    /// score vocabulary does not name.</summary>
    /// <param name="family">The world event's family.</param>
    /// <returns>The sense family, or <see langword="null"/>.</returns>
    public static MusicSenseFamily? SenseOf(WorldEventFamily family) => (family switch {
        WorldEventFamily.RegionEnter => MusicSenseFamily.RegionEnter,
        WorldEventFamily.RegionExit => MusicSenseFamily.RegionExit,
        WorldEventFamily.SeatJoin => MusicSenseFamily.SeatJoin,
        _ => null,
    });
}
