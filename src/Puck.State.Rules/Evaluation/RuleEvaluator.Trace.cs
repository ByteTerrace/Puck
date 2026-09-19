using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.State.Rules;

/// <summary>One captured evaluation of a traced rule: what each local computed, how every gate conjunct decided,
/// and what each effect did. An observer only: capturing never touches simulation state, so a traced run hashes
/// identically to an untraced one.</summary>
public sealed class RuleTraceEvaluation {
    /// <summary>Gets each local as <c>name=value</c>, or <c>name=refused</c> for one that could not evaluate.</summary>
    public List<string> Locals { get; } = [];
    /// <summary>Gets each gate conjunct's spelling, the two values it compared, and its verdict, in evaluation
    /// order.</summary>
    public List<string> Conjuncts { get; } = [];

    /// <summary>Gets the iteration key bound for this evaluation, or <see langword="null"/> for an unbound rule.</summary>
    public string? EachKey { get; init; }
    /// <summary>Gets or sets a value indicating whether an edge rule's gate was already held, so it did not fire.</summary>
    public bool EdgeHeld { get; set; }

    /// <summary>Gets each effect's spelling and outcome — applied, refused with its reason, emitted, or skipped
    /// because the write could not move its destination.</summary>
    public List<string> Effects { get; } = [];

    /// <summary>Gets or sets a value indicating whether the gate held.</summary>
    public bool GateOpen { get; set; }

    /// <summary>Gets the name of the rule this evaluation belongs to.</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>Gets the simulation tick the evaluation ran on.</summary>
    public ulong Tick { get; init; }

    /// <summary>Gets each live row spelling and the row it selected, or <c>none</c> — an evaluation with any
    /// <c>none</c> reads its gate closed without consulting it.</summary>
    public List<string> Zones { get; } = [];

    /// <summary>Formats the evaluation as one read-back line.</summary>
    /// <param name="verb">The read-back verb the line is bracketed under.</param>
    /// <param name="rule">The traced rule's name.</param>
    /// <returns>The line.</returns>
    public string Describe(string verb, string rule) {
        var each = ((EachKey is { } key)
            ? $" each={key}"
            : string.Empty
        );
        var locals = ((Locals.Count > 0)
            ? $" local [{string.Join(
                separator: ", ",
                values: Locals
            )}]"
            : string.Empty
        );
        var zones = ((Zones.Count > 0)
            ? $" zones [{string.Join(
                separator: ", ",
                values: Zones
            )}]"
            : string.Empty
        );
        var gate = ((Conjuncts.Count == 0)
            ? ((GateOpen || (Zones.Count == 0))
                ? "always"
                : "not for these zones")
            : string.Join(
                separator: "; ",
                values: Conjuncts
            )
        );
        var verdict = (GateOpen
            ? (EdgeHeld
                ? "open (edge already held, not fired)"
                : "open")
            : "closed"
        );
        var effects = ((Effects.Count > 0)
            ? $" -> {string.Join(
                separator: " | ",
                values: Effects
            )}"
            : string.Empty
        );

        return $"[{verb} {rule} tick={Tick.ToString(provider: CultureInfo.InvariantCulture)}{each}{locals}{zones} gate={verdict}: {gate}{effects}]";
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
    public const int MaxTraceEvaluations = 256;

    private readonly Dictionary<(Type, int), int> m_refusalSlots = [];
    private readonly List<RuleRuntimeDiagnostic> m_refusals = [];
    private readonly List<RuleTraceEvaluation> m_traceCaptured = [];

    private string? m_traceRule;
    private bool m_traceAll;
    private int m_traceWanted;
    private RuleTraceEvaluation? m_traceEntry;
    private string? m_traceEffectValue;
    private ulong m_refusalSerial;
    // Only a traced evaluation reads this back, so an untraced refusal never builds the string.
    private string? m_lastRefusal;

    /// <summary>Gets the evaluations captured so far.</summary>
    public IReadOnlyList<RuleTraceEvaluation> TraceCaptured => m_traceCaptured;
    /// <summary>Gets the name of the rule a trace is armed for, or <see langword="null"/>.</summary>
    public string? TracedRule => m_traceRule;
    /// <summary>Gets how many evaluations the armed trace wants.</summary>
    public int TraceWanted => m_traceWanted;

    private static int Ordinal<TRefusal>(TRefusal refusal) where TRefusal : unmanaged, Enum => Unsafe.SizeOf<TRefusal>() switch {
        1 => Unsafe.As<TRefusal, byte>(source: ref refusal),
        2 => Unsafe.As<TRefusal, ushort>(source: ref refusal),
        4 => Unsafe.As<TRefusal, int>(source: ref refusal),
        _ => ((int)Unsafe.As<TRefusal, long>(source: ref refusal)),
    };
    // Null unless this rule is the armed one and the capture still has room; the entry stays current through the
    // evaluation's locals, gate and effects and is released by EndTrace.
    private RuleTraceEvaluation? BeginTrace(CompiledRule rule, ulong tick) {
        var armed = (m_traceAll || ((m_traceRule is not null) && string.Equals(
            a: m_traceRule,
            b: rule.Name,
            comparisonType: StringComparison.Ordinal
        )));

        if (
            !armed ||
            (m_traceCaptured.Count >= m_traceWanted)
        ) {
            return null;
        }

        var entry = new RuleTraceEvaluation {
            EachKey = KeyName(key: BoundEachKey),
            Rule = rule.Name,
            Tick = tick,
        };

        m_traceCaptured.Add(item: entry);
        m_traceEntry = entry;

        return entry;
    }
    private string DescribeTracedEffect(IRuleEffect effect, bool applied, bool refused) {
        var value = ((m_traceEffectValue is { } computed)
            ? $" = {computed}"
            : string.Empty
        );

        m_traceEffectValue = null;

        var outcome = (refused
            ? $"refused ({m_lastRefusal})"
            : (applied
                ? "applied"
                : (!effect.SubmitsMutation
                    ? "emitted"
                    : "skipped (could not move the destination)"
        )));

        return $"{effect.Describe}{value}: {outcome}";
    }
    private void EndTrace(RuleTraceEvaluation? entry) {
        if (entry is not null) {
            m_traceEntry = null;
        }
    }
    private string? KeyName(CellKey key) => ((key.IsValid && m_host.Catalog.Keys.TryGetName(
        key: key,
        name: out var name
    ))
        ? name.Value
        : null
    );

    /// <summary>Arms a capture of the next evaluations of one rule, replacing any earlier capture. The caller
    /// resolves the name against its own compiled families first.</summary>
    /// <param name="rule">The rule's name.</param>
    /// <param name="evaluations">How many evaluations to capture, 1..<see cref="MaxTraceEvaluations"/>.</param>
    /// <returns><see langword="false"/> when the count is out of range.</returns>
    public bool ArmTrace(string rule, int evaluations) {
        if (
            (evaluations < 1) ||
            (evaluations > MaxTraceEvaluations)
        ) {
            return false;
        }

        m_traceAll = false;
        m_traceCaptured.Clear();
        m_traceRule = rule;
        m_traceWanted = evaluations;

        return true;
    }
    /// <summary>Arms a capture of every rule's evaluations over the next judged tick, replacing any earlier capture.
    /// Each captured entry's own <see cref="RuleTraceEvaluation.Rule"/> says which rule it belongs to.</summary>
    /// <param name="maxEvaluations">The most evaluations to capture across every rule combined.</param>
    /// <returns><see langword="false"/> when the count is out of range.</returns>
    public bool ArmTraceAll(int maxEvaluations) {
        if (maxEvaluations < 1) {
            return false;
        }

        m_traceAll = true;
        m_traceCaptured.Clear();
        m_traceRule = null;
        m_traceWanted = maxEvaluations;

        return true;
    }
    /// <summary>Formats the armed capture: a header line, then one line per evaluation.</summary>
    /// <param name="verb">The read-back verb the lines are bracketed under.</param>
    /// <returns>The lines, or <see langword="null"/> when nothing is armed.</returns>
    public string? DescribeTrace(string verb) {
        if (m_traceRule is not { } rule) {
            return null;
        }

        var state = ((m_traceCaptured.Count < m_traceWanted)
            ? "armed"
            : "complete"
        );
        var lines = new List<string>(capacity: (m_traceCaptured.Count + 1)) {
            $"[{verb} {rule}: {m_traceCaptured.Count.ToString(provider: CultureInfo.InvariantCulture)}/{m_traceWanted.ToString(provider: CultureInfo.InvariantCulture)} evaluation(s) captured, {state}]",
        };

        foreach (var evaluation in m_traceCaptured) {
            lines.Add(item: evaluation.Describe(
                rule: rule,
                verb: verb
            ));
        }

        return string.Join(
            separator: Environment.NewLine,
            values: lines
        );
    }
    /// <summary>Returns the refusal ledger: one entry per category that has occurred, in first-occurrence order.</summary>
    /// <returns>The ledger.</returns>
    public IReadOnlyList<RuleRuntimeDiagnostic> Diagnostics() => m_refusals;
    /// <summary>Disarms the capture and discards what it captured.</summary>
    /// <returns><see langword="true"/> when a capture was armed.</returns>
    public bool DisarmTrace() {
        var armed = ((m_traceRule is not null) || m_traceAll);

        m_traceAll = false;
        m_traceCaptured.Clear();
        m_traceRule = null;
        m_traceWanted = 0;

        return armed;
    }
    /// <summary>Records a runtime refusal against a compiled effect.</summary>
    /// <typeparam name="TRefusal">The refusal enum.</typeparam>
    /// <param name="refusal">The category.</param>
    /// <param name="ruleName">The rule.</param>
    /// <param name="effect">The effect.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="detail">The concrete reason.</param>
    public void ReportRefusal<TRefusal>(TRefusal refusal, string ruleName, IRuleEffect effect, ulong tick, string detail) where TRefusal : unmanaged, Enum {
        ArgumentNullException.ThrowIfNull(argument: effect);

        ReportRefusal(
            detail: detail,
            effect: effect.Describe,
            refusal: refusal,
            ruleName: ruleName,
            tick: tick
        );
    }
    /// <summary>Records a runtime refusal. The counter is exact and saturating; a host observes only the first
    /// occurrence of a category, through <see cref="IRuleRefusalSink.RefusalRecorded"/>.</summary>
    /// <typeparam name="TRefusal">The refusal enum.</typeparam>
    /// <param name="refusal">The category.</param>
    /// <param name="ruleName">The rule.</param>
    /// <param name="effect">The effect's description, or the channel that refused.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="detail">The concrete reason.</param>
    public void ReportRefusal<TRefusal>(TRefusal refusal, string ruleName, string effect, ulong tick, string detail) where TRefusal : unmanaged, Enum {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_refusalSlots,
            exists: out var exists,
            key: (typeof(TRefusal), Ordinal(refusal: refusal))
        );
        var count = 1UL;

        if (exists) {
            var previous = m_refusals[slot].Count;

            count = ((previous == ulong.MaxValue)
                ? previous
                : (previous + 1UL)
            );
        } else {
            slot = m_refusals.Count;
            m_refusals.Add(item: default);
        }

        m_refusalSerial++;

        if (m_traceEntry is not null) {
            m_lastRefusal = $"{refusal}: {detail}";
        }

        m_refusals[slot] = new RuleRuntimeDiagnostic(
            Count: count,
            Detail: detail,
            Effect: effect,
            LastTick: tick,
            Refusal: refusal,
            Rule: ruleName
        );

        if (
            !exists &&
            (m_host is IRuleRefusalSink sink)
        ) {
            sink.RefusalRecorded(diagnostic: in CollectionsMarshal.AsSpan(list: m_refusals)[slot]);
        }
    }
    /// <summary>Records a refusal a host's own door reported.</summary>
    /// <param name="refusal">The carrier the door answered with.</param>
    /// <param name="ruleName">The rule.</param>
    /// <param name="effect">The effect's description.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="fallback">The category to record when the carrier names none.</param>
    public void ReportRefusal(in EffectRefusal refusal, string ruleName, string effect, ulong tick, RuleEffectRefusal fallback) {
        if (refusal.Code is not { } code) {
            ReportRefusal(
                detail: refusal.Reason,
                effect: effect,
                refusal: fallback,
                ruleName: ruleName,
                tick: tick
            );

            return;
        }

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_refusalSlots,
            exists: out var exists,
            key: (code.GetType(), Convert.ToInt32(
            provider: CultureInfo.InvariantCulture,
            value: code
        ))
        );
        var count = 1UL;

        if (exists) {
            var previous = m_refusals[slot].Count;

            count = ((previous == ulong.MaxValue)
                ? previous
                : (previous + 1UL)
            );
        } else {
            slot = m_refusals.Count;
            m_refusals.Add(item: default);
        }

        m_refusalSerial++;

        if (m_traceEntry is not null) {
            m_lastRefusal = $"{code}: {refusal.Reason}";
        }

        m_refusals[slot] = new RuleRuntimeDiagnostic(
            Count: count,
            Detail: refusal.Reason,
            Effect: effect,
            LastTick: tick,
            Refusal: code,
            Rule: ruleName
        );

        if (
            !exists &&
            (m_host is IRuleRefusalSink sink)
        ) {
            sink.RefusalRecorded(diagnostic: in CollectionsMarshal.AsSpan(list: m_refusals)[slot]);
        }
    }
}
/// <summary>What a host implements to observe the first occurrence of a refusal category in the evaluator's
/// ledger — the one moment a host narrates it, so a level rule refusing every tick never becomes an unbounded
/// stream. The ledger itself keeps the exact running count.</summary>
public interface IRuleRefusalSink {
    /// <summary>Observes a refusal category's first entry.</summary>
    /// <param name="diagnostic">The category's first entry.</param>
    void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic);
}
