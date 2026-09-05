using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>One captured evaluation of a traced rule: what each binding computed, how every gate conjunct decided,
/// and what each effect did — the <c>world.rule.trace</c> read-back. An observer only: capturing never touches
/// simulation state, so a traced run hashes identically to an untraced one.</summary>
public sealed class WorldRuleTraceEvaluation {
    /// <summary>Gets the simulation tick the evaluation ran on.</summary>
    public ulong Tick { get; init; }
    /// <summary>Gets the <c>forEach</c> key bound for this evaluation, or <see langword="null"/> for an unbound rule.</summary>
    public string? EachKey { get; init; }
    /// <summary>Gets each binding as <c>name=value</c>, or <c>name=refused</c> for one that could not evaluate.</summary>
    public List<string> Bindings { get; } = [];
    /// <summary>Gets each gate conjunct's spelling, the two values it compared, and its verdict, in evaluation
    /// order.</summary>
    public List<string> Conjuncts { get; } = [];
    /// <summary>Gets a value indicating whether the gate held.</summary>
    public bool GateOpen { get; set; }
    /// <summary>Gets a value indicating whether an edge rule's gate was already held, so it did not fire.</summary>
    public bool EdgeHeld { get; set; }
    /// <summary>Gets each effect's spelling and outcome — applied, refused with its reason, emitted (a cue, body,
    /// field, pose, or save effect), or skipped because the write could not move its destination.</summary>
    public List<string> Effects { get; } = [];

    /// <summary>Formats the evaluation as one read-back line.</summary>
    /// <param name="rule">The traced rule's name.</param>
    /// <returns>The line.</returns>
    public string Describe(string rule) {
        var each = ((EachKey is { } key) ? $" each={key}" : string.Empty);
        var bindings = ((Bindings.Count > 0) ? $" bind [{string.Join(separator: ", ", values: Bindings)}]" : string.Empty);
        var gate = ((Conjuncts.Count == 0) ? "always" : string.Join(separator: "; ", values: Conjuncts));
        var verdict = (GateOpen ? (EdgeHeld ? "open (edge already held, not fired)" : "open") : "closed");
        var effects = ((Effects.Count > 0) ? $" -> {string.Join(separator: " | ", values: Effects)}" : string.Empty);
        return $"[world.rule.trace {rule} tick={Tick}{each}{bindings} gate={verdict}: {gate}{effects}]";
    }
}

public sealed partial class WorldServer {
    /// <summary>The most evaluations one arming captures.</summary>
    public const int MaxRuleTraceEvaluations = 32;

    private string? m_ruleTraceRule;
    private int m_ruleTraceWanted;
    private readonly List<WorldRuleTraceEvaluation> m_ruleTraceCaptured = [];
    private WorldRuleTraceEvaluation? m_traceEntry;
    private List<string>? m_gateTrace;
    private string? m_traceEffectValue;
    private ulong m_ruleRefusalSerial;
    private string? m_lastRuleRefusal;

    /// <summary>Arms a capture of the next evaluations of one rule or interaction, replacing any earlier capture.</summary>
    /// <param name="rule">The rule or interaction name.</param>
    /// <param name="evaluations">How many evaluations to capture, 1..<see cref="MaxRuleTraceEvaluations"/>.</param>
    /// <param name="refusal">Why arming was refused, or empty.</param>
    /// <returns><see langword="true"/> when armed.</returns>
    public bool TryArmRuleTrace(string rule, int evaluations, out string refusal) {
        if ((evaluations < 1) || (evaluations > MaxRuleTraceEvaluations)) {
            refusal = $"[world.rule.trace: evaluations must be 1..{MaxRuleTraceEvaluations}]";
            return false;
        }
        var compiled = (FindCompiledRule(rules: m_rules, name: rule) ?? FindCompiledRule(rules: m_interactions, name: rule));
        if (compiled is null) {
            refusal = $"[world.rule.trace: no rule or interaction named '{rule}' — world.rules and world.interactions list them]";
            return false;
        }
        if (compiled.Decision is not null) {
            refusal = $"[world.rule.trace: '{rule}' is a decision rule; world.decisions echoes its choices and timers]";
            return false;
        }
        m_ruleTraceRule = rule;
        m_ruleTraceWanted = evaluations;
        m_ruleTraceCaptured.Clear();
        refusal = string.Empty;
        return true;
    }

    /// <summary>Disarms the capture and discards what it captured.</summary>
    /// <returns><see langword="true"/> when a capture was armed.</returns>
    public bool DisarmRuleTrace() {
        var armed = (m_ruleTraceRule is not null);
        m_ruleTraceRule = null;
        m_ruleTraceWanted = 0;
        m_ruleTraceCaptured.Clear();
        return armed;
    }

    /// <summary>Formats the capture for <c>world.rule.trace</c>: a header line, then one line per evaluation.</summary>
    public string DescribeRuleTrace() {
        if (m_ruleTraceRule is not { } rule) {
            return "[world.rule.trace: none armed — world.rule.trace <rule> [evaluations] arms one]";
        }
        var state = ((m_ruleTraceCaptured.Count < m_ruleTraceWanted) ? "armed" : "complete");
        var lines = new List<string>(capacity: m_ruleTraceCaptured.Count + 1) {
            $"[world.rule.trace {rule}: {m_ruleTraceCaptured.Count}/{m_ruleTraceWanted} evaluation(s) captured, {state}]",
        };
        foreach (var evaluation in m_ruleTraceCaptured) {
            lines.Add(item: evaluation.Describe(rule: rule));
        }
        return string.Join(separator: Environment.NewLine, values: lines);
    }

    private static CompiledWorldRule? FindCompiledRule(CompiledWorldRule[] rules, string name) {
        foreach (var rule in rules) {
            if (string.Equals(a: rule.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return rule;
            }
        }
        return null;
    }

    // Null unless this rule is the armed one and the capture still has room; the entry stays current through the
    // evaluation's bindings, gate, and effects and is released by EndRuleTrace.
    private WorldRuleTraceEvaluation? BeginRuleTrace(CompiledWorldRule rule, ulong tick) {
        if ((m_ruleTraceRule is null) || (m_ruleTraceCaptured.Count >= m_ruleTraceWanted) || !string.Equals(a: m_ruleTraceRule, b: rule.Name, comparisonType: StringComparison.Ordinal)) {
            return null;
        }
        var entry = new WorldRuleTraceEvaluation { Tick = tick, EachKey = m_boundEachKey };
        m_ruleTraceCaptured.Add(item: entry);
        m_traceEntry = entry;
        return entry;
    }
    private void EndRuleTrace(WorldRuleTraceEvaluation? entry) {
        if (entry is not null) {
            m_traceEntry = null;
        }
    }
    private string DescribeTracedEffect(CompiledWorldEffect effect, bool applied, bool refused) {
        var value = ((m_traceEffectValue is { } computed) ? $" = {computed}" : string.Empty);
        m_traceEffectValue = null;
        var outcome = (refused
            ? $"refused ({m_lastRuleRefusal})"
            : (applied
                ? "applied"
                : (effect.Kind is WorldRuleEffectKind.EmitCue or WorldRuleEffectKind.Body or WorldRuleEffectKind.PaintField or WorldRuleEffectKind.Pose or WorldRuleEffectKind.Save
                    ? "emitted"
                    : "skipped (could not move the destination)")));
        return $"{effect.Describe}{value}: {outcome}";
    }
    private static string DescribeTracedFact(long value, CellKind kind, bool isForever) =>
        (isForever
            ? "forever"
            : ((kind == CellKind.Fixed)
                ? FixedQ4816.FromRawBits(value: value).ToString()
                : value.ToString(provider: CultureInfo.InvariantCulture)));
    private static string DescribeTracedComparison(ActionStateComparison comparison) => comparison switch {
        ActionStateComparison.Equal => "==",
        ActionStateComparison.NotEqual => "!=",
        ActionStateComparison.Less => "<",
        ActionStateComparison.LessOrEqual => "<=",
        ActionStateComparison.Greater => ">",
        _ => ">=",
    };
}
