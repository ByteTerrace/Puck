namespace Puck.State.Rules;

/// <summary>A tick's deterministic allowance for rule firings: each firing that changes something is charged its
/// price on the reference device, and once a tick's charges pass the allowance the evaluator carries the rest of the
/// tick's due work to the following ticks (<see cref="RuleEvaluator.Allowance"/>). The allowance and every price are
/// document constants, never host timing, so every machine carries the same work on the same tick.</summary>
/// <remarks>
/// <para>Only firings are charged, and only one that moved something (a row, an arm, the host) or that refused or
/// faulted: those are the firings an evaluation that judges every binding in place runs too, in the same order, since a
/// binding a due list leaves alone answers idle and a settled Level binding moves nothing. So the tick reaches the
/// allowance at the same firing whether or not its schedules are on, and <c>puck test --reproduce</c> replays it.</para>
/// <para>The firing that passes the allowance completes, since a firing is atomic; from then on the tick visits no
/// further rule and judges no further binding, except a rule whose work a later tick could not do
/// (<see cref="Transient"/>: a sweep of this tick's events, or a gate that holds on one tick alone) and the rest of a
/// group pass already under way (a pass is one unit of its group's progress). A tick past its allowance starts no group.
/// Every binding left stays due, so it is judged on a later tick against the state it finds then, as any due binding
/// is.</para>
/// </remarks>
public sealed class RuleTickAllowance {
    private readonly Dictionary<CompiledRule, long> m_prices;

    /// <summary>Initializes a new instance of the <see cref="RuleTickAllowance"/> class.</summary>
    /// <param name="allowance">The picoseconds of reference-device firing work a tick may charge.</param>
    /// <param name="prices">Each rule's price for one firing, its judgement included, in reference picoseconds; a rule
    /// with no price is charged the whole allowance.</param>
    /// <param name="sustainedTicks">The consecutive carrying ticks that make an overrun sustained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prices"/> is <see langword="null"/>.</exception>
    public RuleTickAllowance(long allowance, IReadOnlyDictionary<CompiledRule, long> prices, int sustainedTicks) {
        ArgumentNullException.ThrowIfNull(argument: prices);

        Allowance = Math.Max(val1: 0L, val2: allowance);
        SustainedTicks = Math.Max(val1: 1, val2: sustainedTicks);
        m_prices = new Dictionary<CompiledRule, long>(collection: prices, comparer: ReferenceEqualityComparer.Instance);
        m_transient = new HashSet<CompiledRule>(comparer: ReferenceEqualityComparer.Instance);
        foreach (var rule in m_prices.Keys) {
            if (IsTransient(rule: rule)) {
                _ = m_transient.Add(item: rule);
            }
        }
    }

    private readonly HashSet<CompiledRule> m_transient;

    /// <summary>Returns whether a rule's work is never carried: a sweep of this tick's events, whose events are gone
    /// next tick, or a rule whose gate holds on one tick alone (a deadline of <c>$tick ==</c>), which a later tick
    /// would never fire.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns><see langword="true"/> when the tick runs the rule whatever it has charged.</returns>
    public bool Transient(CompiledRule rule) => m_transient.Contains(item: rule);

    private static bool IsTransient(CompiledRule rule) => (
        (rule.Iteration is { BindsEvents: true }) ||
        (RuleDeadline.Find(gate: rule.Gate) is { Exact: true })
    );

    /// <summary>Gets the picoseconds of reference-device firing work one tick may charge.</summary>
    public long Allowance { get; }
    /// <summary>Gets the consecutive carrying ticks after which an overrun is reported as sustained.</summary>
    public int SustainedTicks { get; }
    /// <summary>Gets what the tick in flight has charged, in reference picoseconds.</summary>
    public long Spent { get; private set; }
    /// <summary>Gets a value indicating whether the tick in flight has passed its allowance and carries its remaining
    /// due work.</summary>
    public bool Exhausted { get; private set; }
    /// <summary>Gets how many ticks in a row, up to the last one closed, carried due work.</summary>
    public int CarryingTicks { get; private set; }
    /// <summary>Gets how many ticks carried due work since the allowance was installed.</summary>
    public long CarriedTicks { get; private set; }
    /// <summary>Gets the most any closed tick charged, in reference picoseconds.</summary>
    public long PeakSpent { get; private set; }
    /// <summary>Gets a value indicating whether the overrun is sustained: at least <see cref="SustainedTicks"/> ticks in a
    /// row carried work, so the world's spend is not keeping up with its allowance.</summary>
    public bool Sustained => (CarryingTicks >= SustainedTicks);

    /// <summary>Closes the previous tick's account and opens the next one's.</summary>
    /// <returns><see langword="true"/> when the closed tick carried due work.</returns>
    public bool BeginTick() {
        var carried = Exhausted;

        PeakSpent = Math.Max(val1: PeakSpent, val2: Spent);
        if (carried) {
            CarryingTicks++;
            CarriedTicks++;
        } else {
            CarryingTicks = 0;
        }
        Spent = 0L;
        Exhausted = false;

        return carried;
    }
    /// <summary>Returns one firing's price, in reference picoseconds.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The price, or the whole allowance for a rule with none.</returns>
    public long PriceOf(CompiledRule rule) => (m_prices.TryGetValue(key: rule, value: out var price) ? price : Allowance);

    internal void Charge(CompiledRule rule) {
        Spent = Puck.Maths.BinaryIntegerFunctions.AddSaturating(left: Spent, right: PriceOf(rule: rule));
        if (Spent > Allowance) {
            Exhausted = true;
        }
    }
}
