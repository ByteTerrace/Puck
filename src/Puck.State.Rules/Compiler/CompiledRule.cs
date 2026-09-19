using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>One operation in a compiled postfix Boolean gate.</summary>
public enum GateOp : byte {
    /// <summary>Evaluate one comparison.</summary>
    Compare,
    /// <summary>Conjoin <see cref="GateToken.Arity"/> preceding results.</summary>
    All,
    /// <summary>Disjoin <see cref="GateToken.Arity"/> preceding results.</summary>
    Any,
    /// <summary>Invert the preceding result.</summary>
    Not,
}
/// <summary>One token in a compiled postfix rule gate. The representation preserves arbitrary nested
/// <see cref="ActionPredicate.All"/>/<see cref="ActionPredicate.Any"/>/<see cref="ActionPredicate.Not"/> trees while
/// evaluation remains a single bounded, allocation-free pass.</summary>
/// <param name="LeftSource">The left-hand value source.</param>
/// <param name="Comparison">The comparison to apply.</param>
/// <param name="RightSource">The right-hand value source.</param>
/// <param name="ValueKind">The encoding carried by the comparison.</param>
/// <param name="Describe">The authored spelling of this conjunct.</param>
/// <param name="Op">The postfix Boolean operation.</param>
/// <param name="Arity">The number of preceding results consumed by an <c>all</c> or <c>any</c> token.</param>
public readonly record struct GateToken(CompiledValueSource LeftSource, ActionStateComparison Comparison, CompiledValueSource RightSource, CellKind ValueKind, string Describe, GateOp Op = GateOp.Compare, int Arity = 0);
/// <summary>One token in an allocation-free postfix numeric expression.</summary>
/// <param name="Operation">The stack operation.</param>
/// <param name="Constant">The raw destination-kind literal for a constant token, or the argument slot for an
/// <see cref="ExpressionOp.Argument"/> token.</param>
/// <param name="Operand">The live operand for an operand token.</param>
/// <param name="Board">The compiled topology and direction of a board token.</param>
/// <param name="Fold">The compiled family and per-member body of a fold token.</param>
/// <param name="Call">The compiled subprogram of a <see cref="ExpressionOp.Call"/> token.</param>
public readonly record struct CompiledExpressionToken(ExpressionOp Operation, long Constant = 0L, IRuleOperand? Operand = null, BoardQuery? Board = null, CompiledFold? Fold = null, CompiledSubprogram? Call = null);
/// <summary>A compiled fold: the family whose members are reduced, and the body evaluated once per member.</summary>
/// <param name="Operation">All, Any, Count, or Sum.</param>
/// <param name="RowOrdinal">The family row's catalog ordinal.</param>
/// <param name="Describe">The family's authored name, for a refusal that quotes what the author wrote.</param>
/// <param name="MemberKind">The kind a member reads as.</param>
/// <param name="Body">The per-member body, reading the member through <see cref="ExpressionOp.Member"/>.</param>
/// <param name="Members">The family's declared member count, which prices the fold.</param>
public sealed record CompiledFold(ExpressionOp Operation, int RowOrdinal, string Describe, CellKind MemberKind, CompiledExpressionToken[] Body, int Members);
/// <summary>A compiled subprogram: the body a call site evaluates over the operands it consumed.</summary>
/// <param name="Name">The authored name, for a refusal that quotes what the author wrote.</param>
/// <param name="Arity">How many operands the call consumes.</param>
/// <param name="Body">The body, reading its operands through <see cref="ExpressionOp.Argument"/>.</param>
public sealed record CompiledSubprogram(string Name, int Arity, CompiledExpressionToken[] Body);
/// <summary>One compiled <see cref="RuleLocal"/>: its ordinal is its slot in the evaluation's bound-value scratch,
/// and its expression may read only locals with a smaller ordinal.</summary>
/// <param name="Name">The authored name.</param>
/// <param name="Kind">The value kind the expression was compiled in.</param>
/// <param name="Expression">The compiled postfix program.</param>
public sealed record CompiledRuleLocal(string Name, CellKind Kind, CompiledExpressionToken[] Expression);
/// <summary>One compiled rule: its name, its mode, the flattened gate, the compiled effects, and the
/// <see cref="RuleNeeds"/> the compiler read off its facts' declarations. A document project derives its own record
/// to carry what only it compiles beside these.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">Level or edge (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Gate">The flattened postfix Boolean program; empty means "always".</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
/// <param name="Needs">What the rule needs from the host that runs it.</param>
/// <param name="ForEachOrdinal">The catalog ordinal of the keyed row the rule iterates, or <c>-1</c> for one
/// evaluation per tick or for an iteration over the rule's own zone table.</param>
/// <param name="ForEachZones">Whether the rule iterates its own <paramref name="Zones"/> table.</param>
/// <param name="Locals">The compiled per-evaluation locals, in declared order.</param>
/// <param name="Zones">The compiled <see cref="Rule.Zones"/> table, or <see langword="null"/>.</param>
/// <param name="Describe">The rule's read-back spelling — the one place a compiled program carries authored
/// names.</param>
public record CompiledRule(string Name, ActionTriggerMode Mode, GateToken[] Gate, IRuleEffect[] Effects, RuleNeeds Needs, int ForEachOrdinal = -1, bool ForEachZones = false, CompiledRuleLocal[]? Locals = null, ZoneTable? Zones = null, string Describe = "") {
    /// <summary>Appends every state cell one evaluation reads: the gate, the locals, and the effects. A document
    /// project's rule appends the branches it alone carries.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        RuleDataflow.CollectGate(
            gate: Gate,
            into: into
        );
        foreach (var local in (Locals ?? [])) {
            RuleDataflow.CollectExpression(
                into: into,
                tokens: local.Expression
            );
        }
        foreach (var effect in Effects) {
            effect.CollectReads(into: into);
        }
    }
    /// <summary>Appends every state cell one evaluation writes.</summary>
    /// <param name="into">The write set being collected.</param>
    public virtual void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var effect in Effects) {
            effect.CollectWrites(into: into);
        }
    }
    /// <summary>Returns the conservative work units one evaluation costs: one for the visit, plus the gate, the
    /// locals, and the effects.</summary>
    /// <param name="context">The context the rule was priced against.</param>
    /// <returns>The work units.</returns>
    public virtual RuleWork Cost(IRuleCostContext context) => CostBreakdown(context: context).Total;
    /// <summary>Returns the orthogonal cost components: per-rule setup, per-evaluation check, and per-firing effects.</summary>
    /// <param name="context">The context the rule was priced against.</param>
    /// <returns>The breakdown.</returns>
    public virtual RuleCost CostBreakdown(IRuleCostContext context) {
        var check = (1L + RuleWorkBudget.GateCost(
            context: context,
            tokens: Gate
        ));

        foreach (var local in (Locals ?? [])) {
            check += RuleWorkBudget.ExpressionCost(
                context: context,
                kind: local.Kind,
                tokens: local.Expression
            );
        }

        return new RuleCost(
            Check: check,
            Effects: RuleWorkBudget.EffectsCost(
                context: context,
                effects: Effects
            ),
            Setup: RuleWorkBudget.ForEachSetup(
                context: context,
                rule: this
            )
        );
    }
}
/// <summary>The orthogonal cost components of one compiled rule.</summary>
/// <param name="Check">What one evaluation costs before any effect fires.</param>
/// <param name="Effects">What one firing's effects cost.</param>
/// <param name="Setup">What one sweep of the rule costs before its first evaluation, whatever the evaluations
/// number.</param>
public readonly record struct RuleCost(RuleWork Check, RuleWork Effects, RuleWork Setup) {
    /// <summary>Gets the sum of the three components.</summary>
    public RuleWork Total => ((Setup + Check) + Effects);

    /// <summary>Reports the unresolved conversion from heuristic work units to reference cycles.</summary>
    /// <returns>The unmodeled bound.</returns>
    public CostBound ToBound() => CostBound.Unmodeled(reason: "Heuristic rule work has no calibrated reference-cycle conversion.");
}
