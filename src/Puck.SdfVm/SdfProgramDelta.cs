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
    // The program the delta is measured from, held weakly: the delta stands beside every frame of its later program, and
    // only tables that still hold the earlier one apply it (LeadsFrom), which keeps it alive themselves, so a strong
    // reference would keep a whole earlier program's words alive for as long as the later one stands.
    private readonly WeakReference<SdfProgram> m_from;

    private SdfProgramDelta(SdfProgram from, SdfProgram to, GpuUploadRuns runs) {
        m_from = new WeakReference<SdfProgram>(target: from);
        Runs = runs;
        To = to;
    }

    /// <summary>Returns whether the delta is measured from a program: whether tables that hold
    /// <paramref name="program"/> may upload <see cref="To"/> by copying <see cref="Runs"/> alone.</summary>
    /// <param name="program">The program the tables hold.</param>
    /// <returns><see langword="true"/> when <paramref name="program"/> is the program the delta was measured from.</returns>
    public bool LeadsFrom(SdfProgram? program) => ((program is not null) && m_from.TryGetTarget(target: out var from) && ReferenceEquals(objA: from, objB: program));
    /// <summary>Gets the runs of words in which <see cref="To"/> differs from the program the delta was measured from,
    /// every word past that program's end included.</summary>
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
