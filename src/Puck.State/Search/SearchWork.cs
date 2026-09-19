namespace Puck.State;

/// <summary>What one search job may spend a step and what each indivisible unit of its walk costs, in the work
/// sheet's heuristic units. The walk reserves <see cref="Unit"/> before it runs any unit and yields when that does
/// not fit, so a step's spending never passes <see cref="Allowance"/>.</summary>
/// <param name="Allowance">The work units one step may spend on the job. Unspent allowance does not carry over.</param>
/// <param name="Judge">One judge run.</param>
/// <param name="Inspect">One candidate resolved from the position and refused before a scope opens.</param>
/// <param name="Candidate">One candidate resolved, applied, judged, scored, keyed, and folded.</param>
/// <param name="Outcome">One chance outcome applied and, at the frontier, scored.</param>
/// <param name="TreeStep">One step of a tree search in its costliest phase.</param>
/// <param name="Restart">Discarding a job's progress and reading its tokens again.</param>
/// <param name="Scopes">The most scopes the job holds open, each replayed as a candidate when a step resumes.</param>
public readonly record struct SearchWork(long Allowance, long Judge, long Inspect, long Candidate, long Outcome, long TreeStep, long Restart, int Scopes) {
    /// <summary>The cost of a cursor move that inspects no candidate.</summary>
    public const long Cursor = 1L;

    /// <summary>Gets the least allowance under which every step makes progress: a full replay that fails into a
    /// restart, then one unit.</summary>
    public long Minimum => SaturatingAdd(
        left: SaturatingAdd(
            left: Restart,
            right: Replay(scopes: Scopes)
        ),
        right: Unit
    );
    /// <summary>Gets the costliest indivisible unit, which is what the walk reserves before running any.</summary>
    public long Unit => Math.Max(
        val1: Math.Max(
            val1: Candidate,
            val2: Outcome
        ),
        val2: TreeStep
    );

    private static long SaturatingAdd(long left, long right) => ((left > (long.MaxValue - right))
        ? long.MaxValue
        : (left + right)
    );
    private static long SaturatingMultiply(long left, long right) => (((left == 0L) || (right == 0L))
        ? 0L
        : ((left > (long.MaxValue / right))
            ? long.MaxValue
            : (left * right)
    ));

    /// <summary>Returns a job bounded by its judged-candidate quota alone: every unit is priced at its judge run
    /// and the allowance never binds.</summary>
    /// <param name="judge">The work units one judge run costs.</param>
    /// <returns>The prices.</returns>
    public static SearchWork NodeBounded(long judge) => new(
        Allowance: long.MaxValue,
        Candidate: SaturatingAdd(
            left: judge,
            right: Cursor
        ),
        Inspect: Cursor,
        Judge: judge,
        Outcome: Cursor,
        Restart: 0L,
        Scopes: 0,
        TreeStep: SaturatingAdd(
            left: judge,
            right: Cursor
        )
    );
    /// <summary>Prices a job's units from the shape of its plan. A price that no <see cref="long"/> holds clamps at
    /// <see cref="long.MaxValue"/>, which no allowance admits.</summary>
    /// <param name="allowance">The work units one step may spend on the job.</param>
    /// <param name="judge">One judge run.</param>
    /// <param name="score">One score read.</param>
    /// <param name="position">Folding one position key: a unit per cell of every row the key covers.</param>
    /// <param name="shapes">The candidate shapes.</param>
    /// <param name="cellCount">The cells the job's board or zone set holds.</param>
    /// <param name="tokens">The most tokens the job walks.</param>
    /// <param name="seats">The seats a max-n job reads, or zero.</param>
    /// <param name="depth">The plies the job searches ahead.</param>
    /// <param name="chanceCells">The cells a chance outcome writes, or zero for a job with no chance node.</param>
    /// <param name="method">How the job compares plies.</param>
    /// <param name="keyed">Whether the job keeps a transposition table, whose probe and store fold a position key.</param>
    /// <returns>The prices.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> is <see langword="null"/>.</exception>
    public static SearchWork Price(long allowance, long judge, long score, long position, SearchShapePlan[] shapes, int cellCount, int tokens, int seats, int depth, int chanceCells, SearchMethod method, bool keyed) {
        ArgumentNullException.ThrowIfNull(argument: shapes);

        var candidates = 0L;
        var hops = 1L;

        foreach (var shape in shapes) {
            candidates = SaturatingAdd(
                left: candidates,
                right: SaturatingMultiply(
                    left: tokens,
                    right: shape.CandidateCount(cellCount: cellCount)
                )
            );
            if (shape.Kind == SearchShapeKind.Jump) {
                hops = Math.Max(
                    val1: hops,
                    val2: shape.MaxHops
                );
            }
        }

        // A resolution scans the tokens for an occupant at most twice a hop, and a chained jump also scans the
        // cells it has already visited.
        var inspect = SaturatingAdd(
            left: 2L,
            right: SaturatingMultiply(
                left: hops,
                right: ((2L * tokens) + hops)
            )
        );
        // Applying writes a handful of cells and scans the tokens once for a capture.
        var apply = (8L + tokens);
        var candidate = SaturatingAdd(
            left: SaturatingAdd(
                left: inspect,
                right: apply
            ),
            right: SaturatingAdd(
                left: SaturatingAdd(
                    left: judge,
                    right: score
                ),
                right: SaturatingAdd(
                    left: (keyed
                        ? SaturatingMultiply(
                            left: 2L,
                            right: position
                        )
                        : 0L),
                    right: (4L + seats)
                )
            )
        );
        var tree = ((method == SearchMethod.Tree)
            // A tree step selects among a node's children or decodes one flat candidate, opens it, scores the
            // leaf, and folds the value back along the path.
            ? SaturatingAdd(
                left: SaturatingAdd(
                    left: candidate,
                    right: candidates
                ),
                right: SaturatingAdd(
                    left: score,
                    right: ((2L * depth) + 4L)
                )
            )
            : 0L
        );

        return new SearchWork(
            Allowance: allowance,
            Candidate: candidate,
            Inspect: inspect,
            Judge: judge,
            Outcome: ((chanceCells > 0)
                ? SaturatingAdd(
                    left: (2L + chanceCells),
                    right: score
                )
                : 0L),
            // A restart reads every token's key and clears the root's outputs and the transposition table.
            Restart: SaturatingAdd(
                left: (4L * tokens),
                right: (keyed
                    ? ((3L * SearchCapacity.TranspositionEntries) / 8L)
                    : 0L)
            ),
            // A tree job holds its path and its playout open together; a walk holds one scope a ply and one for a
            // chance draw.
            Scopes: ((method == SearchMethod.Tree)
                ? ((2 * depth) + 2)
                : (depth + 1)),
            TreeStep: tree
        );
    }

    /// <summary>Returns what reopening a suspended job's scopes costs.</summary>
    /// <param name="scopes">The scopes held open.</param>
    /// <returns>The work units.</returns>
    public long Replay(int scopes) => SaturatingMultiply(
        left: scopes,
        right: Candidate
    );
}
