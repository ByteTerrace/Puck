using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The words a program differs in from the program built before it, found where the program is built, off the frame
/// thread, so tables that hold the earlier program upload the later one by copying only those words
/// (<see cref="SdfWorldTables.UploadProgram"/>). Runs are in words from the program's first; past
/// <see cref="GpuRegion.MaxCopyRuns"/> runs, neighbours pair up and the words between them are owed again.
/// </summary>
public sealed class SdfProgramDelta {
    private SdfProgramDelta(SdfProgram from, SdfProgram to, GpuUploadRuns runs) {
        From = from;
        Runs = runs;
        To = to;
    }

    /// <summary>Gets the program the delta is measured from.</summary>
    public SdfProgram From { get; }
    /// <summary>Gets the runs of words in which <see cref="To"/> differs from <see cref="From"/>, every word past
    /// <see cref="From"/>'s end included.</summary>
    public GpuUploadRuns Runs { get; }
    /// <summary>Gets the program the delta leads to.</summary>
    public SdfProgram To { get; }

    /// <summary>Returns the words <paramref name="to"/> differs in from <paramref name="from"/>.</summary>
    /// <param name="from">The earlier program.</param>
    /// <param name="to">The later program.</param>
    /// <returns>The delta.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="from"/> or <paramref name="to"/> is
    /// <see langword="null"/>.</exception>
    public static SdfProgramDelta Between(SdfProgram from, SdfProgram to) {
        ArgumentNullException.ThrowIfNull(argument: from);
        ArgumentNullException.ThrowIfNull(argument: to);

        var runs = new GpuUploadRuns(
            capacity: GpuRegion.MaxCopyRuns,
            mergeGap: GpuRegion.StagedMergeGapWords
        );

        runs.AddDifferences(
            current: to.Words,
            previous: from.Words
        );

        return new SdfProgramDelta(
            from: from,
            runs: runs,
            to: to
        );
    }
}
