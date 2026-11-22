using System.Globalization;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private readonly record struct SpentLine(string Name, bool IsInteraction, RuleLatch.RuleSpent Spent, long Work, string Schedule);

    // A rule's read schedule, and beside it the schedule its Level firing stands on once it has changed nothing.
    private string DescribeSchedule(CompiledRule rule, RuleLatch latch) {
        var judged = m_evaluator.ScheduleOf(rule: rule, latch: latch).Describe(reader: this);

        return ((m_evaluator.FiringScheduleOf(rule: rule, latch: latch) is { } firing)
            ? $"{judged}; a firing stands on {firing.Describe(reader: this)}"
            : judged);
    }

    /// <summary>Gets or sets whether every Level firing a schedule would answer for is fired anyway and held to
    /// changing nothing (<see cref="RuleEvaluator.QuiescenceChecked"/>).</summary>
    internal bool QuiescenceChecked {
        get => m_evaluator.QuiescenceChecked;
        set {
            m_staleFactDerivation = null;
            m_evaluator.QuiescenceChecked = value;
        }
    }
    /// <summary>Gets the first Level firing or derived-fact skip the check found that did not settle again
    /// (<see cref="RuleEvaluator.StaleQuiescence"/>), or <see langword="null"/>.</summary>
    internal string? StaleQuiescence => (m_evaluator.StaleQuiescence ?? m_staleFactDerivation);

    /// <summary>Spells the measured rule sheet: per rule and interaction, what the evaluator ran since the latches
    /// began (visits, evaluations, skips, the skips that stood for a settled Level firing, firings) and the work units that spent at the worst-case sheet's own
    /// prices — a sweep's setup per visit, the check per evaluation that ran, the effects per firing — costliest
    /// first, each with the schedule that decides whether it may be skipped. Every count is deterministic, so two
    /// runs of one document and input print the same sheet.</summary>
    /// <param name="top">The most lines to print.</param>
    /// <returns>The sheet's lines, the summary first.</returns>
    public IReadOnlyList<string> DescribeSpent(int top) {
        var prices = new Dictionary<(string Name, bool IsInteraction), RuleCost>();

        foreach (var contributor in (m_growthCompilation?.WorkContributors ?? [])) {
            prices[(contributor.Name, contributor.IsInteraction)] = contributor.Cost;
        }

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
                    Work: (((spent.Visits * Price(work: cost.Setup)) + (ran * Price(work: cost.Check))) + (spent.Firings * Price(work: cost.Effects)))
                ));
            }
        }

        // A component the sheet could not bound prices nothing here; the worst-case sheet already names it.
        static long Price(RuleWork work) => (work.IsKnown ? work.Units : 0L);
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
