namespace Puck.World;

/// <summary>
/// Whether the latest edit loop the World times has settled: the edit reached a presented frame, every mesh it touched
/// draws its own geometry at the detail its demand asks and stands in the mesh region, and the lighting has converged.
/// It is the fact <c>world.wait iteration</c> waits on, so a script that edits and then reads the loop's times waits on
/// the loop rather than on a tick count. A host that composes no renderer registers none. Members are read on the host
/// pump.
/// </summary>
public interface IWorldIterationReadiness {
    /// <summary>Gets the count of loops begun over the World's life, the boot's included, which is also the latest loop's
    /// number: the mark a wait arms from.</summary>
    long Loops { get; }

    /// <summary>Returns whether the latest loop is the one numbered <paramref name="mark"/> or a later one, and has
    /// settled. Allocates nothing, so a hold polls it every drain.</summary>
    /// <param name="mark">The value of <see cref="Loops"/> when the wait armed: the loop the edit before the wait
    /// began.</param>
    /// <returns><see langword="true"/> when that loop or a later one has settled.</returns>
    bool IsSettledSince(long mark);

    /// <summary>Returns the latest loop's report line: its name and the seconds each of its stages took, each stage still
    /// open named with what it waits on, or <see langword="null"/> when no loop has begun. It builds a string, so a caller
    /// reads it only to report.</summary>
    /// <returns>The line, or <see langword="null"/>.</returns>
    string? Describe();
}
