using System.Globalization;

namespace Puck.World.Server;

/// <summary>What the evaluator ran since the latches began, summed over every rule and interaction, with the units it
/// spent at the sheet's own prices: what <c>puck bench rules</c> divides a measured rule pass by.</summary>
/// <param name="Ticks">The ticks counted.</param>
/// <param name="Sweeps">The sweeps that snapshotted their keys.</param>
/// <param name="SetupUnits">The setup units those sweeps spent.</param>
/// <param name="Judged">The evaluations that ran (evaluations less skips).</param>
/// <param name="CheckUnits">The check units those evaluations spent.</param>
/// <param name="Firings">The firings.</param>
/// <param name="EffectUnits">The effect units those firings spent, their doors' units included.</param>
/// <param name="Doors">The doors those firings charged (<see cref="RuleCost.Doors"/>), each
/// <see cref="RuleWorkBudget.DoorUnits"/> of <paramref name="EffectUnits"/>.</param>
public readonly record struct WorldRuleSpent(long Ticks, long Sweeps, long SetupUnits, long Judged, long CheckUnits, long Firings, long EffectUnits, long Doors);
public sealed partial class WorldRuleHost {
    private readonly record struct SpentLine(string Name, bool IsInteraction, RuleLatch.RuleSpent Spent, long Work, string Schedule);

    // Each line's sheet price by name: what one sweep's setup, one evaluation and one firing spend.
    private Dictionary<(string Name, bool IsInteraction), RuleCost> SpentPrices() {
        var prices = new Dictionary<(string Name, bool IsInteraction), RuleCost>();

        foreach (var contributor in (m_growthCompilation?.WorkContributors ?? [])) {
            prices[(contributor.Name, contributor.IsInteraction)] = contributor.Cost;
        }
        return prices;
    }
    // A component the sheet could not bound prices nothing here; the worst-case sheet already names it.
    private static long SpentPrice(RuleWork work) => (work.IsKnown ? work.Units : 0L);

    /// <summary>Sums what the evaluator ran since the latches began over every rule and interaction, at the sheet's own
    /// prices. Every count is deterministic.</summary>
    /// <returns>The totals.</returns>
    public WorldRuleSpent SpentTotals() {
        var prices = SpentPrices();
        var totals = new WorldRuleSpent(Ticks: m_ruleGateHeld.SpentTicks, Sweeps: 0L, SetupUnits: 0L, Judged: 0L, CheckUnits: 0L, Firings: 0L, EffectUnits: 0L, Doors: 0L);

        m_evaluator.SettleSpent();
        Sum(compiled: m_rules, isInteraction: false, latch: m_ruleGateHeld);
        Sum(compiled: m_interactions, isInteraction: true, latch: m_interactionGateHeld);
        return totals;

        void Sum(CompiledRule[] compiled, bool isInteraction, RuleLatch latch) {
            foreach (var rule in compiled) {
                if (!latch.TryGetRule(name: rule.Name, state: out var state)) {
                    continue;
                }

                var spent = state.Spent;
                var cost = (prices.TryGetValue(key: (rule.Name, isInteraction), value: out var priced) ? priced : default);
                var ran = (spent.Evaluations - spent.Skips);

                totals = totals with {
                    Sweeps = (totals.Sweeps + spent.Sweeps),
                    SetupUnits = (totals.SetupUnits + (spent.Sweeps * SpentPrice(work: cost.Setup))),
                    Judged = (totals.Judged + ran),
                    CheckUnits = (totals.CheckUnits + (ran * SpentPrice(work: cost.Check))),
                    Firings = (totals.Firings + spent.Firings),
                    EffectUnits = (totals.EffectUnits + (spent.Firings * SpentPrice(work: cost.Effects))),
                    Doors = (totals.Doors + (spent.Firings * cost.Doors)),
                };
            }
        }
    }

    // A rule's read schedule, and beside it the schedule its Level firing stands on once it has changed nothing.
    private string DescribeSchedule(CompiledRule rule, RuleLatch latch) {
        var judged = m_evaluator.ScheduleOf(rule: rule, latch: latch).Describe(reader: this);

        return ((m_evaluator.FiringScheduleOf(rule: rule, latch: latch) is { } firing)
            ? $"{judged}; a firing stands on {firing.Describe(reader: this)}"
            : judged);
    }

    /// <summary>Gets or sets whether every rule judges every binding in place every tick, with no schedule, kept
    /// verdict or due list (<see cref="RuleEvaluator.SchedulingEnabled"/> off): the evaluation those stand for, which
    /// a replay runs against a recording that used them. Either change forgets every cached verdict, since none was
    /// kept while the schedules were off.</summary>
    public bool JudgesEveryBinding {
        get => !m_evaluator.SchedulingEnabled;
        set {
            if (value == JudgesEveryBinding) {
                return;
            }
            m_evaluator.SchedulingEnabled = !value;
            m_ruleGateHeld.InvalidateScheduler();
            m_interactionGateHeld.InvalidateScheduler();
        }
    }

    /// <summary>Spells the measured rule sheet: per rule and interaction, what the evaluator ran since the latches
    /// began (visits, evaluations, skips, the skips that stood for a settled Level firing, firings) and the work units that spent at the sheet's own
    /// prices — a sweep's setup per sweep that snapshotted its keys, the check per evaluation that ran, the effects per firing — costliest
    /// first, each with the schedule that decides whether it may be skipped. Every count is deterministic, so two
    /// runs of one document and input print the same sheet.</summary>
    /// <param name="top">The most lines to print.</param>
    /// <returns>The sheet's lines, the summary first.</returns>
    public IReadOnlyList<string> DescribeSpent(int top) {
        var prices = SpentPrices();
        var lines = new List<SpentLine>(capacity: (m_rules.Length + m_interactions.Length));

        m_evaluator.SettleSpent();

        Collect(compiled: m_rules, isInteraction: false, latch: m_ruleGateHeld);
        Collect(compiled: m_interactions, isInteraction: true, latch: m_interactionGateHeld);
        lines.Sort(comparison: static (left, right) => {
            var order = right.Work.CompareTo(value: left.Work);

            if (order != 0) {
                return order;
            }
            order = string.CompareOrdinal(strA: left.Name, strB: right.Name);
            return ((order != 0) ? order : left.IsInteraction.CompareTo(value: right.IsInteraction));
        });

        var ticks = m_ruleGateHeld.SpentTicks;
        var shown = Math.Min(val1: top, val2: lines.Count);
        long visits = 0L, elided = 0L, evaluations = 0L, skips = 0L, settled = 0L, firings = 0L, work = 0L;

        foreach (var line in lines) {
            visits += line.Spent.Visits;
            evaluations += line.Spent.Evaluations;
            skips += line.Spent.Skips;
            settled += line.Spent.Settled;
            firings += line.Spent.Firings;
            elided += line.Spent.Elided;
            work += line.Work;
        }

        var output = new List<string>(capacity: (shown + 1)) {
            $"[world.budget.rules --spent: {ticks} tick(s), {lines.Count} line(s), showing {shown}; visits={visits} elided={elided} evaluations={evaluations} skips={skips} settled={settled} firings={firings} work={work} work/tick={PerTick(total: work, ticks: ticks)}]",
        };

        for (var index = 0; (index < shown); index++) {
            var line = lines[index];

            output.Add(item: $"[world.budget.rules --spent {line.Name}{(line.IsInteraction ? " interaction" : string.Empty)} visits={line.Spent.Visits} elided={line.Spent.Elided} evaluations={line.Spent.Evaluations} skips={line.Spent.Skips} settled={line.Spent.Settled} firings={line.Spent.Firings} armed={line.Spent.Armed} work={line.Work} work/tick={PerTick(total: line.Work, ticks: ticks)}; {line.Schedule}]");
        }

        return output;

        void Collect(CompiledRule[] compiled, bool isInteraction, RuleLatch latch) {
            foreach (var rule in compiled) {
                if (!latch.TryGetRule(name: rule.Name, state: out var state)) {
                    continue;
                }

                var spent = state.Spent;
                var cost = (prices.TryGetValue(key: (rule.Name, isInteraction), value: out var priced) ? priced : default);
                var ran = (spent.Evaluations - spent.Skips);

                lines.Add(item: new SpentLine(
                    IsInteraction: isInteraction,
                    Name: rule.Name,
                    Schedule: DescribeSchedule(latch: latch, rule: rule),
                    Spent: spent,
                    Work: (((spent.Sweeps * SpentPrice(work: cost.Setup)) + (ran * SpentPrice(work: cost.Check))) + (spent.Firings * SpentPrice(work: cost.Effects)))
                ));
            }
        }
    }
    /// <summary>Spells the read schedule of every rule or interaction compiled under a name: the rows it depends on
    /// and why it is volatile when it is.</summary>
    /// <param name="name">The rule's or interaction's name.</param>
    /// <returns>One line per compiled rule or interaction of that name; empty when none is.</returns>
    public IReadOnlyList<string> DescribeSchedules(string name) {
        var output = new List<string>(capacity: 2);

        Describe(compiled: m_rules, isInteraction: false, latch: m_ruleGateHeld);
        Describe(compiled: m_interactions, isInteraction: true, latch: m_interactionGateHeld);

        return output;

        void Describe(CompiledRule[] compiled, bool isInteraction, RuleLatch latch) {
            foreach (var rule in compiled) {
                if (string.Equals(a: rule.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                    output.Add(item: $"[world.budget.rules --why {rule.Name}{(isInteraction ? " interaction" : string.Empty)} schedule: {DescribeSchedule(latch: latch, rule: rule)}]");
                }
            }
        }
    }

    private static string PerTick(long total, long ticks) => ((ticks > 0L)
        ? (((decimal)total) / ticks).ToString(format: "0.0", provider: CultureInfo.InvariantCulture)
        : "0.0");
}
