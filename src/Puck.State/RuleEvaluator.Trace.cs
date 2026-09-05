using System.Runtime.CompilerServices;

namespace Puck.State;

/// <summary>One captured evaluation of a traced rule: what each binding computed, how every gate conjunct decided,
/// and what each effect did. An observer only: capturing never touches simulation state, so a traced run hashes
/// identically to an untraced one.</summary>
public sealed class RuleTraceEvaluation {
    /// <summary>Gets the simulation tick the evaluation ran on.</summary>
    public ulong Tick { get; init; }
    /// <summary>Gets the forEach key bound for this evaluation, or <see langword="null"/> for an unbound rule.</summary>
    public string? EachKey { get; init; }
    /// <summary>Gets each binding as <c>name=value</c>, or <c>name=refused</c> for one that could not evaluate.</summary>
    public List<string> Bindings { get; } = [];
    /// <summary>Gets each gate conjunct's spelling, the two values it compared, and its verdict, in evaluation
    /// order.</summary>
    public List<string> Conjuncts { get; } = [];
    /// <summary>Gets or sets a value indicating whether the gate held.</summary>
    public bool GateOpen { get; set; }
    /// <summary>Gets or sets a value indicating whether an edge rule's gate was already held, so it did not fire.</summary>
    public bool EdgeHeld { get; set; }
    /// <summary>Gets each effect's spelling and outcome — applied, refused with its reason, emitted, or skipped
    /// because the write could not move its destination.</summary>
    public List<string> Effects { get; } = [];

    /// <summary>Formats the evaluation as one read-back line.</summary>
    /// <param name="verb">The read-back verb the line is bracketed under.</param>
    /// <param name="rule">The traced rule's name.</param>
    public string Describe(string verb, string rule) {
        var each = ((EachKey is { } key) ? $" each={key}" : string.Empty);
        var bindings = ((Bindings.Count > 0) ? $" bind [{string.Join(separator: ", ", values: Bindings)}]" : string.Empty);
        var gate = ((Conjuncts.Count == 0) ? "always" : string.Join(separator: "; ", values: Conjuncts));
        var verdict = (GateOpen ? (EdgeHeld ? "open (edge already held, not fired)" : "open") : "closed");
        var effects = ((Effects.Count > 0) ? $" -> {string.Join(separator: " | ", values: Effects)}" : string.Empty);

        return $"[{verb} {rule} tick={Tick}{each}{bindings} gate={verdict}: {gate}{effects}]";
    }
}

/// <summary>One bounded runtime refusal counter and its most recent occurrence.</summary>
/// <param name="Refusal">The refusal category — a member of <see cref="RuleEffectRefusal"/> or of a host's own
/// tagged enum.</param>
/// <param name="Count">How many occurrences the evaluator has observed.</param>
/// <param name="LastTick">The latest simulation tick that observed it.</param>
/// <param name="Rule">The latest rule name.</param>
/// <param name="Effect">The latest effect description.</param>
/// <param name="Detail">The latest concrete runtime reason.</param>
public readonly record struct RuleRuntimeDiagnostic(Enum Refusal, ulong Count, ulong LastTick, string Rule, string Effect, string Detail);

public sealed partial class RuleEvaluator {
    /// <summary>The most evaluations one trace arming captures.</summary>
    public const int MaxTraceEvaluations = 32;

    private readonly Dictionary<(Type, int), int> m_refusalSlots = [];
    private readonly List<RuleRuntimeDiagnostic> m_refusals = [];
    private readonly List<RuleTraceEvaluation> m_traceCaptured = [];
    private string? m_traceRule;
    private int m_traceWanted;
    private RuleTraceEvaluation? m_traceEntry;
    private string? m_traceEffectValue;
    private ulong m_refusalSerial;
    private string? m_lastRefusal;

    /// <summary>Gets the name of the rule a trace is armed for, or <see langword="null"/>.</summary>
    public string? TracedRule => m_traceRule;
    /// <summary>Gets how many evaluations the armed trace wants.</summary>
    public int TraceWanted => m_traceWanted;
    /// <summary>Gets the evaluations captured so far.</summary>
    public IReadOnlyList<RuleTraceEvaluation> TraceCaptured => m_traceCaptured;

    /// <summary>Arms a capture of the next evaluations of one rule, replacing any earlier capture. The caller resolves
    /// the name against its own compiled families first.</summary>
    /// <param name="rule">The rule's name.</param>
    /// <param name="evaluations">How many evaluations to capture, 1..<see cref="MaxTraceEvaluations"/>.</param>
    /// <returns><see langword="false"/> when the count is out of range.</returns>
    public bool ArmTrace(string rule, int evaluations) {
        if ((evaluations < 1) || (evaluations > MaxTraceEvaluations)) {
            return false;
        }

        m_traceRule = rule;
        m_traceWanted = evaluations;
        m_traceCaptured.Clear();

        return true;
    }
    /// <summary>Disarms the capture and discards what it captured.</summary>
    /// <returns><see langword="true"/> when a capture was armed.</returns>
    public bool DisarmTrace() {
        var armed = (m_traceRule is not null);

        m_traceRule = null;
        m_traceWanted = 0;
        m_traceCaptured.Clear();

        return armed;
    }
    /// <summary>Formats the armed capture: a header line, then one line per evaluation.</summary>
    /// <param name="verb">The read-back verb the lines are bracketed under.</param>
    /// <returns>The lines, or <see langword="null"/> when nothing is armed.</returns>
    public string? DescribeTrace(string verb) {
        if (m_traceRule is not { } rule) {
            return null;
        }

        var state = ((m_traceCaptured.Count < m_traceWanted) ? "armed" : "complete");
        var lines = new List<string>(capacity: (m_traceCaptured.Count + 1)) {
            $"[{verb} {rule}: {m_traceCaptured.Count}/{m_traceWanted} evaluation(s) captured, {state}]",
        };

        foreach (var evaluation in m_traceCaptured) {
            lines.Add(item: evaluation.Describe(verb: verb, rule: rule));
        }

        return string.Join(separator: Environment.NewLine, values: lines);
    }

    /// <summary>Returns the refusal ledger: one entry per category that has occurred, in first-occurrence order.</summary>
    public IReadOnlyList<RuleRuntimeDiagnostic> Diagnostics() => m_refusals;

    /// <summary>Records a runtime refusal against a compiled effect.</summary>
    /// <typeparam name="TRefusal">The refusal enum.</typeparam>
    /// <param name="refusal">The category.</param>
    /// <param name="ruleName">The rule.</param>
    /// <param name="effect">The effect.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="detail">The concrete reason.</param>
    public void ReportRefusal<TRefusal>(TRefusal refusal, string ruleName, EffectFact effect, ulong tick, string detail) where TRefusal : unmanaged, Enum =>
        ReportRefusal(refusal: refusal, ruleName: ruleName, effect: effect.Describe, tick: tick, detail: detail);
    /// <summary>Records a runtime refusal. The counter is exact and saturating; the host narrates only the first
    /// occurrence of a category, through <see cref="IRuleHost.RefusalRecorded"/>.</summary>
    /// <typeparam name="TRefusal">The refusal enum.</typeparam>
    /// <param name="refusal">The category.</param>
    /// <param name="ruleName">The rule.</param>
    /// <param name="effect">The effect's description, or the channel that refused.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="detail">The concrete reason.</param>
    public void ReportRefusal<TRefusal>(TRefusal refusal, string ruleName, string effect, ulong tick, string detail) where TRefusal : unmanaged, Enum {
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_refusalSlots, key: (typeof(TRefusal), Ordinal(refusal: refusal)), exists: out var exists);
        var count = 1UL;

        if (exists) {
            var previous = m_refusals[slot].Count;

            count = ((previous == ulong.MaxValue) ? previous : (previous + 1UL));
        } else {
            slot = m_refusals.Count;
            m_refusals.Add(item: default);
        }

        m_refusalSerial++;
        m_lastRefusal = $"{refusal}: {detail}";
        m_refusals[slot] = new RuleRuntimeDiagnostic(Refusal: refusal, Count: count, LastTick: tick, Rule: ruleName, Effect: effect, Detail: detail);

        if (!exists) {
            m_host.RefusalRecorded(diagnostic: in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_refusals)[slot]);
        }
    }
    /// <summary>Records a dynamic table key the table does not carry against the rule in flight and flags the
    /// enclosing gate or expression to fail.</summary>
    /// <param name="table">The table's authored name.</param>
    /// <param name="key">The missing key.</param>
    public void ReportTableKeyMissing(string table, long key) {
        m_host.TableKeyMissing = true;
        ReportRefusal(refusal: RuleEffectRefusal.TableKeyMissing, ruleName: RuleName, effect: $"$table:{table}", tick: Tick, detail: $"key {key} is not an entry of table '{table}'");
    }

    private static int Ordinal<TRefusal>(TRefusal refusal) where TRefusal : unmanaged, Enum => Unsafe.SizeOf<TRefusal>() switch {
        1 => Unsafe.As<TRefusal, byte>(source: ref refusal),
        2 => Unsafe.As<TRefusal, ushort>(source: ref refusal),
        4 => Unsafe.As<TRefusal, int>(source: ref refusal),
        _ => ((int)Unsafe.As<TRefusal, long>(source: ref refusal)),
    };

    // Null unless this rule is the armed one and the capture still has room; the entry stays current through the
    // evaluation's bindings, gate, and effects and is released by EndTrace.
    private RuleTraceEvaluation? BeginTrace(CompiledRule rule, ulong tick) {
        if ((m_traceRule is null) || (m_traceCaptured.Count >= m_traceWanted) || !string.Equals(a: m_traceRule, b: rule.Name, comparisonType: StringComparison.Ordinal)) {
            return null;
        }

        var entry = new RuleTraceEvaluation { Tick = tick, EachKey = BoundEachKey };

        m_traceCaptured.Add(item: entry);
        m_traceEntry = entry;

        return entry;
    }
    private void EndTrace(RuleTraceEvaluation? entry) {
        if (entry is not null) {
            m_traceEntry = null;
        }
    }
    private string DescribeTracedEffect(EffectFact effect, bool applied, bool refused) {
        var value = ((m_traceEffectValue is { } computed) ? $" = {computed}" : string.Empty);

        m_traceEffectValue = null;

        var outcome = (refused
            ? $"refused ({m_lastRefusal})"
            : (applied
                ? "applied"
                : (!effect.SubmitsMutation
                    ? "emitted"
                    : "skipped (could not move the destination)")));

        return $"{effect.Describe}{value}: {outcome}";
    }
}
