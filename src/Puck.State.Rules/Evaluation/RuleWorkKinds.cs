using Puck.Abstractions.Counting;

namespace Puck.State.Rules;

/// <summary>
/// The kinds of work a <see cref="RuleEvaluator"/> counts and reports through its <see cref="IWorkCounterSource"/>.
/// Each is a monotonic total over the evaluator's life, read as the difference across the work being measured, and
/// none is part of simulation state: nothing hashes, exports, or checkpoints them.
/// </summary>
public static class RuleWorkKinds {
    /// <summary>The name a counters report heads a rule evaluator's section with.</summary>
    public const string SourceName = "state.rules";

    /// <summary>Gets the kind counting rule evaluations: one per rule and binding the evaluator is asked to judge,
    /// whether it runs the gate, keeps a closed verdict, or fires.</summary>
    public static WorkKind Evaluations { get; } = new(name: "state.rules.evaluations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the evaluations the schedule answered without running bindings or gate, because
    /// nothing the rule reads had changed since its gate last closed.</summary>
    public static WorkKind Skips { get; } = new(name: "state.rules.skips", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the firings that committed and moved the arena.</summary>
    public static WorkKind Firings { get; } = new(name: "state.rules.firings", unit: "count", workClass: WorkClass.Deterministic);

    /// <summary>Gets the evaluator's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds =>
        Order.Kinds;

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Evaluations,
            Skips,
            Firings,
        ];
    }
}
