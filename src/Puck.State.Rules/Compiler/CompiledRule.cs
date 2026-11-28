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
    /// <summary>Skip the rest of a conjunction or disjunction once its answer is known.</summary>
    ShortCircuit,
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
/// <param name="Skip">Tokens skipped by a short-circuit instruction.</param>
/// <param name="SkipWhen">The truth value that takes that skip.</param>
public readonly record struct GateToken(CompiledValueSource LeftSource, ExpressionOp Comparison, CompiledValueSource RightSource, CellKind ValueKind, string Describe, GateOp Op = GateOp.Compare, int Arity = 0, int Skip = 0, bool SkipWhen = false);
/// <summary>How the interpreter runs one compiled token: the arm of its one dispatch, read off the token's operation
/// and payload when the token is built (<see cref="CompiledExpressionToken.Step"/>).</summary>
public enum ExpressionStep : byte {
    /// <summary>A binary arithmetic or comparison operator over the two values below it.</summary>
    Binary,
    /// <summary>Pushes the token's constant.</summary>
    Constant,
    /// <summary>Pushes one of the enclosing call's arguments.</summary>
    Argument,
    /// <summary>Pushes the fold member bound for the evaluation.</summary>
    Member,
    /// <summary>Reads an operand that declares no address and spells no word.</summary>
    Read,
    /// <summary>Reads an operand through the addresses it declares, the computed ones fed from the stack.</summary>
    ReadAddressed,
    /// <summary>Replaces the value below with whether it is absent.</summary>
    IsAbsent,
    /// <summary>Negates the truth of the value below.</summary>
    Not,
    /// <summary>Evaluates the token's body, then negates its truth.</summary>
    NotBody,
    /// <summary>Evaluates the token's body only when the truth below holds.</summary>
    And,
    /// <summary>Evaluates the token's body only when the truth below fails.</summary>
    Or,
    /// <summary>Replaces an absent value with the fallback above it.</summary>
    Coalesce,
    /// <summary>Reduces a row's cells through the token's fold body.</summary>
    Fold,
    /// <summary>Evaluates the token's subprogram over the values below it.</summary>
    Call,
    /// <summary>Clamps a value between two bounds.</summary>
    Clamp,
    /// <summary>Selects one of two values by a condition.</summary>
    Select,
    /// <summary>Extracts a bit field.</summary>
    BitField,
    /// <summary>Inserts a bit field.</summary>
    BitInsert,
    /// <summary>Shifts a board mask one step in the token's direction.</summary>
    BoardShift,
    /// <summary>Casts a board mask's rays in the token's direction.</summary>
    BoardRay,
    /// <summary>Maps a board mask through the token's symmetry image.</summary>
    BoardImage,
    /// <summary>Reads the cells a slider mask attacks over an occupancy mask in the token's direction.</summary>
    BoardSlide,
    /// <summary>A shared arithmetic function over the values below it.</summary>
    Function,
    /// <summary>A unary arithmetic operator over the value below it.</summary>
    Unary,
}
/// <summary>One token in an allocation-free postfix numeric expression. Its <see cref="Step"/> and
/// <see cref="Consumes"/> are derived from the operation and payload whenever a token is built or one of those is
/// changed by a <see langword="with"/> expression, so the interpreter dispatches without a table lookup.</summary>
public readonly record struct CompiledExpressionToken {
    private readonly CompiledSubprogram? m_call;
    private readonly int m_consumes;
    private readonly CompiledFold? m_fold;
    private readonly IRuleOperand? m_operand;
    private readonly ExpressionOp m_operation;
    private readonly ExpressionStep m_step;

    /// <summary>Initializes a new instance of the <see cref="CompiledExpressionToken"/> struct.</summary>
    /// <param name="Operation">The stack operation.</param>
    /// <param name="Constant">The raw destination-kind literal for a constant token, or the argument slot for an
    /// <see cref="ExpressionOp.Argument"/> token.</param>
    /// <param name="Operand">The live operand for an operand token.</param>
    /// <param name="Board">The compiled topology and direction of a board token.</param>
    /// <param name="Fold">The compiled row and per-cell body of a fold token.</param>
    /// <param name="Call">The compiled subprogram of a <see cref="ExpressionOp.Call"/> token.</param>
    /// <param name="CarrierKind">The numeric kind this instruction reads, or null for a caller-supplied carrier.</param>
    public CompiledExpressionToken(ExpressionOp Operation, long Constant = 0L, IRuleOperand? Operand = null, BoardQuery? Board = null, CompiledFold? Fold = null, CompiledSubprogram? Call = null, CellKind? CarrierKind = null) {
        m_call = Call;
        m_fold = Fold;
        m_operand = Operand;
        m_operation = Operation;
        this.Board = Board;
        this.CarrierKind = CarrierKind;
        this.Constant = Constant;
        (m_step, m_consumes) = Shape(call: Call, fold: Fold, operand: Operand, operation: Operation);
    }

    /// <summary>Gets the compiled topology and direction of a board token.</summary>
    public BoardQuery? Board { get; init; }
    /// <summary>Gets the compiled subprogram of a <see cref="ExpressionOp.Call"/> token, or the body of a
    /// short-circuit token.</summary>
    public CompiledSubprogram? Call {
        get => m_call;
        init {
            m_call = value;
            (m_step, m_consumes) = Shape(call: value, fold: m_fold, operand: m_operand, operation: m_operation);
        }
    }
    /// <summary>Gets the numeric kind this instruction reads, or null for a caller-supplied carrier.</summary>
    public CellKind? CarrierKind { get; init; }
    /// <summary>Gets the raw destination-kind literal for a constant token, or the argument slot for an
    /// <see cref="ExpressionOp.Argument"/> token.</summary>
    public long Constant { get; init; }
    /// <summary>Gets how many values the token consumes from the value stack: the operands of an operator, the
    /// arguments of a call, and for an operand token the values the programs computing its addresses left.</summary>
    public int Consumes => m_consumes;
    /// <summary>Gets the compiled row and per-cell body of a fold token.</summary>
    public CompiledFold? Fold {
        get => m_fold;
        init {
            m_fold = value;
            (m_step, m_consumes) = Shape(call: m_call, fold: value, operand: m_operand, operation: m_operation);
        }
    }
    /// <summary>Gets the live operand for an operand token.</summary>
    public IRuleOperand? Operand {
        get => m_operand;
        init {
            m_operand = value;
            (m_step, m_consumes) = Shape(call: m_call, fold: m_fold, operand: value, operation: m_operation);
        }
    }
    /// <summary>Gets the stack operation.</summary>
    public ExpressionOp Operation {
        get => m_operation;
        init {
            m_operation = value;
            (m_step, m_consumes) = Shape(call: m_call, fold: m_fold, operand: m_operand, operation: value);
        }
    }
    /// <summary>Gets the arm of the interpreter's dispatch the token runs.</summary>
    public ExpressionStep Step => m_step;

    // The one derivation of a token's arm and arity, in the precedence the interpreter has always applied: the stack
    // and truth operations by operation, then a fold or a call by payload, then the operator families.
    private static (ExpressionStep Step, int Consumes) Shape(ExpressionOp operation, IRuleOperand? operand, CompiledFold? fold, CompiledSubprogram? call) {
        var consumes = (call?.Arity ?? ((operand is not null)
            ? RuleExpressions.FeedCount(operand: operand)
            : ExpressionOperators.Arity(operation: operation)));
        var step = operation switch {
            ExpressionOp.Constant => ExpressionStep.Constant,
            ExpressionOp.Argument => ExpressionStep.Argument,
            ExpressionOp.Member => ExpressionStep.Member,
            ExpressionOp.Operand => (((operand is not null) && operand.Addresses.IsEmpty && (operand is not IWordFoldOperand))
                ? ExpressionStep.Read
                : ExpressionStep.ReadAddressed),
            ExpressionOp.IsAbsent => ExpressionStep.IsAbsent,
            ExpressionOp.Not => ((call is null) ? ExpressionStep.Not : ExpressionStep.NotBody),
            ExpressionOp.And => ExpressionStep.And,
            ExpressionOp.Or => ExpressionStep.Or,
            ExpressionOp.Coalesce => ExpressionStep.Coalesce,
            _ when (fold is not null) => ExpressionStep.Fold,
            _ when (call is not null) => ExpressionStep.Call,
            ExpressionOp.Clamp => ExpressionStep.Clamp,
            ExpressionOp.Select => ExpressionStep.Select,
            ExpressionOp.BitField => ExpressionStep.BitField,
            ExpressionOp.BitInsert => ExpressionStep.BitInsert,
            ExpressionOp.BoardShift => ExpressionStep.BoardShift,
            ExpressionOp.BoardRay => ExpressionStep.BoardRay,
            ExpressionOp.BoardImage => ExpressionStep.BoardImage,
            ExpressionOp.BoardSlide => ExpressionStep.BoardSlide,
            _ when (ExpressionArithmetic.FunctionArity(operation: operation) > 0) => ExpressionStep.Function,
            _ when ExpressionArithmetic.IsUnary(operation: operation) => ExpressionStep.Unary,
            _ => ExpressionStep.Binary,
        };

        return (step, consumes);
    }
}
/// <summary>A compiled fold: the one row whose cells are reduced, and the body evaluated once per cell the row holds.</summary>
/// <param name="Operation">All, Any, Count, Sum, ArgMinimum, or ArgMaximum.</param>
/// <param name="RowOrdinal">The folded row's catalog ordinal, the one row the fold reads.</param>
/// <param name="Describe">The row's authored name, for a refusal that quotes what the author wrote.</param>
/// <param name="MemberKind">The kind a cell reads as.</param>
/// <param name="Body">The per-cell body, reading the cell through <see cref="ExpressionOp.Member"/>.</param>
/// <param name="Cells">The most cells the row can hold (its capacity), which bounds one fold's visits and prices
/// it; the cells a row is authored with say nothing about the cells a rule writes into it later.</param>
public sealed record CompiledFold(ExpressionOp Operation, int RowOrdinal, string Describe, CellKind MemberKind, CompiledProgram Body, long Cells);
/// <summary>A compiled subprogram: the body a call site evaluates over the operands it consumed.</summary>
/// <param name="Name">The authored name, for a refusal that quotes what the author wrote.</param>
/// <param name="Arity">How many operands the call consumes.</param>
/// <param name="Body">The body, reading its operands through <see cref="ExpressionOp.Argument"/>.</param>
/// <param name="Steps">How many tokens one call evaluates, every call site and fold member inside it expanded
/// (<see cref="RuleWorkBudget.Steps"/>). A body is shared by its call sites, so a chain that calls the level below
/// twice at each level is short to write and doubles with its depth; this is what lets a walk know that without
/// taking it.</param>
public sealed record CompiledSubprogram(string Name, int Arity, CompiledProgram Body, long Steps);
/// <summary>One compiled <see cref="RuleLocal"/>: its ordinal is its slot in the evaluation's bound-value scratch,
/// and its expression may read only locals with a smaller ordinal.</summary>
/// <param name="Name">The authored name.</param>
/// <param name="Kind">The kind of value the expression leaves and the evaluator stores in this binding.</param>
/// <param name="Expression">The compiled postfix program.</param>
/// <param name="CarrierKind">The default carrier for literals and tokens without their own kind. It can be
/// <see cref="CellKind.Fixed"/> when <paramref name="Kind"/> is <see cref="CellKind.Int"/> because comparison and
/// sign expressions leave an integer after consuming exact fixed operands.</param>
public sealed record CompiledRuleLocal(string Name, CellKind Kind, CompiledProgram Expression, CellKind CarrierKind) {
    /// <summary>Gets how a refusal names the binding (<c>binding 'name'</c>), spelled once when the local is
    /// compiled so a refused evaluation reports it without building it again.</summary>
    public string Describe { get; } = $"binding '{Name}'";
}
/// <summary>One compiled rule: its name, its mode, the flattened gate, the compiled effects, and the
/// <see cref="RuleNeeds"/> the compiler read off its facts' declarations. A document project derives its own record
/// to carry what only it compiles beside these.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">Level or edge (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Gate">The flattened postfix Boolean program; empty means "always".</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
/// <param name="Needs">What the rule needs from the host that runs it.</param>
/// <param name="ForEachOrdinal">The catalog ordinal of the keyed row the rule iterates, or <c>-1</c> for one
/// evaluation per tick or for an iteration over the rule's own zone table or the live bodies.</param>
/// <param name="ForEachZones">Whether the rule iterates its own <paramref name="Zones"/> table.</param>
/// <param name="Locals">The compiled per-evaluation locals, in declared order.</param>
/// <param name="Zones">The compiled <see cref="Rule.Zones"/> table, or <see langword="null"/>.</param>
/// <param name="Describe">The rule's read-back spelling — the one place a compiled program carries authored
/// names.</param>
/// <param name="PoolForEach">The pool a whole-rule sweep snapshots, or <see langword="null"/>.</param>
/// <param name="PoolBindingSlot">The evaluator register carrying each whole-rule pool handle.</param>
/// <param name="ForEachBodies">Whether the rule iterates every live body the host's <see cref="ILiveBodies"/>
/// lists (<see cref="RuleFacts.ForEachBodies"/>).</param>
public record CompiledRule(string Name, ActionTriggerMode Mode, CompiledGate Gate, IRuleEffect[] Effects, RuleNeeds Needs, int ForEachOrdinal = -1, bool ForEachZones = false, CompiledRuleLocal[]? Locals = null, ZoneTable? Zones = null, string Describe = "", StatePoolDescriptor? PoolForEach = null, int PoolBindingSlot = -1, bool ForEachBodies = false) {
    /// <summary>A bounded document-owned source sharing the normal each sweep.</summary>
    public RuleIteration? Iteration { get; init; }
    /// <summary>Gets the row family the rule sweeps, or <see langword="null"/>: one binding per key of each member, in
    /// family order and then cell order, each binding the key to <c>$each</c> and the member's family index to
    /// <c>$member</c>, so a family selection under <c>$member</c> (<c>pile[$member]</c>) and a cell under it
    /// (<c>table[$member]</c>) are the binding's member's alone.</summary>
    public RowFamily? ForEachFamily { get; init; }
    /// <summary>Gets a value indicating whether the rule is evaluated once a pass rather than swept over a set.</summary>
    public bool EvaluatesOnce => ((ForEachOrdinal < 0) && !ForEachZones && !ForEachBodies && (PoolForEach is null) && (Iteration is null) && (ForEachFamily is null));
    /// <summary>Gets the most bindings of one sweep that may fire (<see cref="Rule.MaxFirings"/>), or 0 when every
    /// binding the sweep visits may.</summary>
    public int MaxFirings { get; init; }
    /// <summary>Gets, per local in declared order, whether the local is evaluated only once the gate has opened on an
    /// evaluation that fires, because neither the gate nor a zone selection's index (<see cref="ZoneTable.SelectionLocals"/>,
    /// resolved before the gate) reads it, directly or through another local; or
    /// <see langword="null"/> when every local is evaluated before the gate. The evaluator judges a binding by the
    /// locals this leaves undeferred and its gate, and the work sheet prices the deferred ones with the firing, so a
    /// binding whose gate stays closed costs its gate and nothing its effects alone need.</summary>
    public bool[]? Deferred { get; init; }

    /// <summary>Returns whether a local is evaluated only when the rule fires (<see cref="Deferred"/>).</summary>
    /// <param name="ordinal">The local's ordinal.</param>
    /// <returns><see langword="true"/> when the local waits for the firing.</returns>
    public bool IsDeferred(int ordinal) => ((Deferred is { } deferred) && (((uint)ordinal) < ((uint)deferred.Length)) && deferred[ordinal]);

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
        // Membership controls whether and how often a rule fires, even when its body never reads a cell value.
        if (ForEachOrdinal >= 0) {
            into.Add(new CellAccess(Key: default, Reach: CellReach.Members, RowOrdinal: ForEachOrdinal));
        }
        if (ForEachFamily is { } family) {
            foreach (var member in family.Ordinals()) {
                into.Add(new CellAccess(Key: default, Reach: CellReach.Members, RowOrdinal: member));
            }
        }
        if (PoolForEach is { } pool) {
            into.Add(item: new CellAccess(Key: default, RowOrdinal: pool.DomainRowOrdinal));
            into.Add(item: new CellAccess(Key: default, RowOrdinal: pool.GenerationRowOrdinal));
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
        var effects = RuleWorkBudget.EffectsCost(
            context: context,
            effects: Effects
        );
        var locals = (Locals ?? []);

        for (var ordinal = 0; (ordinal < locals.Length); ordinal++) {
            var cost = RuleWorkBudget.ExpressionCost(
                context: context,
                kind: locals[ordinal].Kind,
                tokens: locals[ordinal].Expression
            );

            if (IsDeferred(ordinal: ordinal)) {
                effects += cost;
            } else {
                check += cost;
            }
        }

        return new RuleCost(
            Check: check,
            Effects: effects,
            Setup: RuleWorkBudget.ForEachSetup(
                context: context,
                rule: this
            )
        ) {
            Doors = RuleWorkBudget.EffectsDoors(context: context, effects: Effects),
        };
    }
}
/// <summary>The orthogonal cost components of one compiled rule.</summary>
/// <param name="Check">What one evaluation costs before any effect fires.</param>
/// <param name="Effects">What one firing costs beyond its check: the locals deferred to it
/// (<see cref="CompiledRule.Deferred"/>) and its effects.</param>
/// <param name="Setup">What one sweep of the rule costs before its first evaluation, whatever the evaluations
/// number.</param>
public readonly record struct RuleCost(RuleWork Check, RuleWork Effects, RuleWork Setup) {
    /// <summary>Gets the fixed doors (<see cref="RuleWorkBudget.DoorUnits"/> each) one firing's
    /// <see cref="Effects"/> charges (<see cref="IRuleEffect.Doors"/>).</summary>
    public long Doors { get; init; }
    /// <summary>Gets the units of one firing that are not doors: <see cref="Effects"/> less every door's units.</summary>
    public RuleWork EffectElements => (Effects.IsKnown ? RuleWork.Known(units: Math.Max(val1: 0L, val2: (Effects.Units - BinaryIntegerFunctions.MultiplySaturating(left: Doors, right: RuleWorkBudget.DoorUnits)))) : Effects);
    /// <summary>Gets the sum of the three components.</summary>
    public RuleWork Total => ((Setup + Check) + Effects);

    /// <summary>Reports the unresolved conversion from heuristic work units to reference cycles.</summary>
    /// <returns>The unmodeled bound.</returns>
    public CostBound ToBound() => CostBound.Unmodeled(reason: "Heuristic rule work has no calibrated reference-cycle conversion.");
}
