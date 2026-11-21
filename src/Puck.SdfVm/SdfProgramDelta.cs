using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The words a program differs in from the program built before it, found where the program is built, off the frame
/// thread, so tables that hold the earlier program upload the later one by copying only those words
/// (<see cref="SdfWorldTables.UploadProgram"/>). Runs are in words of the whole stream, the field's words then the appearance section; past
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

        // A program that shares the earlier one's field shares its field words, so only the appearance section can
        // differ. Where the field's length moved, the appearance section starts at another word and is owed whole.
        var field = to.FieldWords.Length;

        if (!to.SharesField(other: from)) { _ = runs.AddDifferences(current: to.FieldWords, previous: from.FieldWords); }
        if (from.FieldWords.Length == field) {
            _ = runs.AddDifferences(current: to.ShadingWords, previous: from.ShadingWords, startWord: field);
        } else {
            runs.Add(length: to.ShadingWords.Length, start: field);
        }

        return new SdfProgramDelta(
            from: from,
            runs: runs,
            to: to
        );
    }
}
