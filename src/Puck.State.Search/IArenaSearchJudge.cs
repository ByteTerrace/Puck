namespace Puck.State;

/// <summary>What one search job asks of the judge behind it: whether the host it fires through is admitted to the
/// job's rules at all, which rows its verdict depends on, whether it can score a position, the verdict one
/// candidate's position draws, and that position's score.</summary>
/// <remarks>
/// <para>Every call arrives with the candidate's journal scope already open on <see cref="Arena"/>, so a judge
/// reads the position the candidate reached and writes into that same scope. The scope is rewound when the walk
/// leaves the candidate, which is what makes a judge's writes hypothetical; a judge must therefore never open or
/// close a scope of its own, and never act outside the arena.</para>
/// <para><see cref="TryAdmit"/> is asked once per plan when a job set is installed, never per candidate.</para>
/// </remarks>
public interface IArenaSearchJudge {
    /// <summary>Gets the arena every candidate scope opens on.</summary>
    StateArena Arena { get; }
    /// <summary>Gets the document row ordinals a verdict depends on, ascending and deduplicated.</summary>
    /// <remarks>A position key folds these beside the rows the plan itself names, so two positions differing only
    /// outside their union transpose.</remarks>
    IReadOnlyList<int> KeyRows { get; }
    /// <summary>Gets a value indicating whether a verdict or score reads the live tick pair.</summary>
    /// <remarks>When this is <see langword="true"/>, the search folds the tick pair into its candidate cache keys
    /// and restarts this job when either value changes. A judge that reads time through a row with value-over-time
    /// traits need not set it: the search derives that dependency from <see cref="KeyRows"/>.</remarks>
    bool ReadsTick => false;
    /// <summary>Gets a value indicating whether the judge can score a position.</summary>
    bool Scores { get; }
    /// <summary>Gets how many rules a verdict evaluates, which the caller's own read-back prints.</summary>
    int RuleCount => 0;

    /// <summary>Judges the position a candidate reached, writing its verdict and turn cells into the open
    /// scope.</summary>
    /// <param name="view">The scoped arena and the tick pair the judge reads as of.</param>
    /// <returns><see langword="true"/> when the judge wrote the arena.</returns>
    bool Judge(in ArenaSearchView view);
    /// <summary>Returns the score of the position the arena holds, from the perspective of the side that moved into
    /// it.</summary>
    /// <param name="view">The scoped arena and the tick pair the score reads as of.</param>
    /// <returns>The score.</returns>
    long Score(in ArenaSearchView view);
    /// <summary>Decides whether the host behind this judge may run one plan's rules.</summary>
    /// <param name="plan">The plan being installed.</param>
    /// <param name="refusal">Why the plan was refused, or empty on admission.</param>
    /// <returns><see langword="true"/> when the host serves everything the plan's rules need.</returns>
    bool TryAdmit(ArenaSearchPlan plan, out string refusal);
}
