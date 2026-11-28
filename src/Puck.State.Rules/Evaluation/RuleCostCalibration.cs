using System.Globalization;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State.Rules;

/// <summary>What one priced quantity of the work sheet costs on one device, in picoseconds, measured by
/// <c>puck bench rules</c> over compiled rules on the closure evaluator. The sheet counts work in units
/// (<see cref="RuleWorkContributor"/>); a calibration turns those counts into time, so a world's worst tick is compared
/// with a share of the tick period rather than with a unit ceiling resting on a guessed nanosecond a unit. Besides the
/// unit kinds it prices the two fixed costs the units leave out, judging one binding (the evaluator's visit, its frame
/// and its verdict) and firing once (the journal scope, the latch and the commit), and it prices a door
/// (<see cref="RuleWorkBudget.DoorUnits"/> units on the sheet) apart from the element work around it, so the sheet's
/// fixed weight for a cell write is replaced by what a write measures.</summary>
/// <param name="Setup">Picoseconds per setup unit: one key a sweep snapshots, or one distance test an interaction
/// charges before its evaluations.</param>
/// <param name="Binding">Picoseconds per binding judged, beyond the units its gate prices.</param>
/// <param name="Check">Picoseconds per check unit: an operand read, an operation or a comparison of a gate or local.</param>
/// <param name="Firing">Picoseconds per firing, beyond the units its effects price.</param>
/// <param name="Door">Picoseconds per door an effect charges (<see cref="IRuleEffect.Doors"/>): an arena cell write,
/// an effect arm's dispatch.</param>
/// <param name="Effect">Picoseconds per effect unit that is not a door: a source's operand reads and operations, a
/// transform's element moves, the host's commit work.</param>
/// <param name="Host">Picoseconds per unit of the passes a host runs beside the rules (flock affinities, decision
/// perception).</param>
/// <param name="Growth">Picoseconds per arena growth unit (<see cref="StateArena.ReservationWork"/>): copying and
/// reindexing a changed reservation, or taking it back.</param>
/// <param name="Build">What measured these figures (the device, the build's version and commit, the date), as the
/// bench wrote it; absent for figures an author wrote by hand.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleCostCalibration(
    long Setup,
    long Binding,
    long Check,
    long Firing,
    long Door,
    long Effect,
    long Host,
    long Growth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)][property: DocumentText("what measured a rule cost calibration")] string? Build = null
) {
    /// <summary>The picoseconds the unit ceiling this replaces assumed every unit costs, 1.6 ns.</summary>
    public const long UncalibratedPicosecondsPerUnit = 1_600L;

    /// <summary>Gets the figures a device no calibration names is priced at when no device of the world is
    /// calibrated: every unit at <see cref="UncalibratedPicosecondsPerUnit"/>, a door at its units' worth, and no fixed
    /// binding or firing cost, the assumption the unit ceiling made. They are not measured and so are not proved
    /// conservative.</summary>
    public static RuleCostCalibration Uncalibrated { get; } = new(
        Binding: 0L,
        Check: UncalibratedPicosecondsPerUnit,
        Door: (UncalibratedPicosecondsPerUnit * RuleWorkBudget.DoorUnits),
        Effect: UncalibratedPicosecondsPerUnit,
        Firing: 0L,
        Growth: UncalibratedPicosecondsPerUnit,
        Host: UncalibratedPicosecondsPerUnit,
        Setup: UncalibratedPicosecondsPerUnit
    );

    /// <summary>Returns why the figures cannot price anything (a negative figure), or <see langword="null"/>.</summary>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Refusal() => (((Setup | Binding | Check | Firing | Door | Effect | Host | Growth) < 0L)
        ? "a rule cost calibration's figures are picoseconds and cannot be negative"
        : null);
    /// <summary>Returns what one line costs in time: its check (setup, bindings judged and check units) and its firing
    /// (firings, doors and the other effect units), at its price and at its worst case.</summary>
    /// <param name="line">The line, priced in units.</param>
    /// <returns>The line with <see cref="RuleWorkContributor.CheckUnits"/>, <see cref="RuleWorkContributor.FiringUnits"/>,
    /// <see cref="RuleWorkContributor.WorkUnits"/> and <see cref="RuleWorkContributor.WorstUnits"/> in picoseconds.</returns>
    public RuleWorkContributor Time(RuleWorkContributor line) {
        var check = ((line.SetupUnits * Setup) + (line.Judged * RuleWork.Known(units: Binding))) + ((line.Judged * line.Cost.Check) * Check);
        var fire = OneFiring(cost: line.Cost);
        var firing = (line.Firings * fire);
        var worst = (((line.Cost.Setup * Setup) + (line.Multiplier * RuleWork.Known(units: Binding))) + ((line.Multiplier * line.Cost.Check) * Check))
            + (line.WorstFirings * fire);

        return line with { CheckUnits = check, FiringUnits = firing, WorkUnits = (check + firing), WorstUnits = worst };
    }
    // One firing: its fixed cost, its doors at the door figure, and the rest of its units at the element figure.
    private RuleWork OneFiring(RuleCost cost) => ((RuleWork.Known(units: Firing) + (cost.Doors * RuleWork.Known(units: Door))) + (cost.EffectElements * Effect));
    /// <summary>Returns a tick's worst-case time: every line converted (<see cref="Time"/>) and tallied through the same
    /// exclusion tree that bounds the units (<see cref="RuleWorkBudget.Tally"/>), plus the host's own passes.</summary>
    /// <param name="lines">The sheet's lines, priced in units.</param>
    /// <param name="writers">Per cell, the firings that can write it and their places (<see cref="RuleWorkBudget.CountWriters"/>).</param>
    /// <param name="hostUnits">The units of the passes the host runs beside the rules.</param>
    /// <returns>The tick's time and its worst case (every line's worst case at once).</returns>
    public (RuleTime Tick, RuleTime Worst) Price(IReadOnlyList<RuleWorkContributor> lines, IReadOnlyDictionary<(int RowOrdinal, CellKey Key), RuleCellWriters> writers, RuleWork hostUnits) {
        ArgumentNullException.ThrowIfNull(argument: lines);
        ArgumentNullException.ThrowIfNull(argument: writers);

        var timed = new RuleWorkContributor[lines.Count];
        var host = (hostUnits * Host);
        var worst = host;

        for (var index = 0; (index < timed.Length); index++) {
            timed[index] = Time(line: lines[index]);
            worst += timed[index].WorstUnits;
        }

        return (RuleTime.FromPicoseconds(picoseconds: (RuleWorkBudget.Tally(contributors: timed, writers: writers).Work + host)), RuleTime.FromPicoseconds(picoseconds: worst));
    }
    /// <summary>Returns what an arena growth of <paramref name="units"/> costs.</summary>
    /// <param name="units">The growth units (<see cref="StateArena.ReservationWork"/>).</param>
    /// <returns>The time.</returns>
    public RuleTime GrowthTime(long units) => RuleTime.FromPicoseconds(picoseconds: (RuleWork.Known(units: units) * Growth));
    /// <summary>Spells the figures as the <c>.puck</c> block a device profile carries.</summary>
    /// <returns>The block.</returns>
    public string Describe() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"rules {{ setup: {Setup}, binding: {Binding}, check: {Check}, firing: {Firing}, door: {Door}, effect: {Effect}, host: {Host}, growth: {Growth}{((Build is null) ? string.Empty : $", build: \"{Build}\"")} }}"
    );
}
/// <summary>A bound on rule work in calibrated time: picoseconds on one device, a reachable operation nothing prices,
/// or a time no <see cref="long"/> holds. Kept apart from <see cref="RuleWork"/>, whose units are not time.</summary>
public readonly record struct RuleTime {
    private readonly RuleWork m_picoseconds;

    private RuleTime(RuleWork picoseconds) {
        m_picoseconds = picoseconds;
    }

    /// <summary>Gets no time.</summary>
    public static RuleTime Zero { get; } = new(picoseconds: RuleWork.Zero);

    /// <summary>Gets a value indicating whether the bound is a known time.</summary>
    public bool IsKnown => m_picoseconds.IsKnown;
    /// <summary>Gets the picoseconds of a known bound.</summary>
    /// <exception cref="InvalidOperationException">The bound is unmodeled or overflowed.</exception>
    public long Picoseconds => m_picoseconds.Units;

    /// <summary>Creates a known time.</summary>
    /// <param name="picoseconds">The picoseconds; a negative count is an overflow.</param>
    /// <returns>The bound.</returns>
    public static RuleTime Known(long picoseconds) => new(picoseconds: RuleWork.Known(units: picoseconds));
    // The tally's arithmetic is RuleWork's; only this type's own factory reads its result as time.
    internal static RuleTime FromPicoseconds(RuleWork picoseconds) => new(picoseconds: picoseconds);
    /// <summary>Orders two bounds by how long they could take: a known time, beneath an unmodeled bound, beneath an
    /// overflow.</summary>
    /// <param name="left">The left bound.</param>
    /// <param name="right">The right bound.</param>
    /// <returns>Negative, zero, or positive as <paramref name="left"/> orders before, with, or after it.</returns>
    public static int Compare(RuleTime left, RuleTime right) => RuleWork.Compare(left: left.m_picoseconds, right: right.m_picoseconds);
    /// <summary>Returns whether the bound is known and within a budget.</summary>
    /// <param name="budgetPicoseconds">The budget.</param>
    /// <returns><see langword="true"/> when the time is known to fit.</returns>
    public bool Fits(long budgetPicoseconds) => m_picoseconds.Fits(ceiling: budgetPicoseconds);
    /// <summary>Spells a picosecond count in milliseconds, three places.</summary>
    /// <param name="picoseconds">The picoseconds.</param>
    /// <returns>The spelling, such as <c>4.213 ms</c>.</returns>
    public static string Milliseconds(long picoseconds) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(picoseconds / 1e9):0.000} ms");
    /// <inheritdoc/>
    public override string ToString() => (m_picoseconds.IsKnown ? Milliseconds(picoseconds: m_picoseconds.Units) : m_picoseconds.ToString());

    public static RuleTime operator +(RuleTime left, RuleTime right) => new(picoseconds: (left.m_picoseconds + right.m_picoseconds));
}
