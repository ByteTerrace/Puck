using Puck.Maths;

using Puck.State;

namespace Puck.GamingBricks.Forge;

/// <summary>
/// The abstract work a document's rule pass spends in one frame, and the reservation it must fit. Units are a
/// normalization, not a processor, clock or instruction count: one unit is a eleventh of a <c>set</c> step writing a
/// literal to a variable.
/// </summary>
/// <remarks>
/// <para>
/// Weights come from measured sustained capacity on both real machines, never from counting an emitter's instructions:
/// cost per iteration is inversely proportional to the iterations a machine sustains at full frame rate, and
/// <c>CartridgeCostMeasurement</c> reports those capacities. Each weight is the worst of the two targets' ratios, so
/// one unit means the same amount of work on either machine; how many units a machine grants per frame is its
/// <see cref="CartridgeCostProfile"/>.
/// </para>
/// <para>
/// An operation with no measured weight yields <see cref="CostBound.Unmodeled"/>, so a document using one is reported
/// as unmodeled rather than against an invented number. Adding a primitive means measuring it, not estimating it.
/// </para>
/// <para>
/// Rules are not simply summed. A set of rules each guarded by one equality of the same variable against a different
/// constant cannot all run in a frame, so only the dearest of them is charged — the shape a phase machine takes, where
/// summing would report several times what any frame really costs. The saving is only claimed when the guard variable
/// cannot change while those rules are being evaluated; a write to it from the first of them through the last gives
/// the whole group up and sums instead.
/// </para>
/// </remarks>
public static class CartridgeCost {
    /// <summary>Calculates the worst-case work a document's rules and sprites spend in one frame.</summary>
    /// <param name="document">The authored source.</param>
    /// <param name="profile">The machine's weights.</param>
    /// <returns>The bound, or an unmodeled result naming the first primitive without a measured weight.</returns>
    public static CostBound Frame(CartridgeDocument document, CartridgeCostProfile profile) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var cells = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        foreach (var screen in document.Screens ?? []) {
            if (screen is not null) {
                cells[key: screen.Name] = screen.Tiles?.Length ?? 0;
            }
        }

        var payload = 0L;
        if (document.Save is { } save) {
            payload += save.Variables?.Length ?? 0;
            foreach (var name in save.Arrays ?? []) {
                payload += document.Arrays?.FirstOrDefault(predicate: array => array?.Name == name)?.Initial?.Length ?? 0;
            }
        }

        // A layer costs its visibility test and, when drawn, two scroll writes — the shape of an ordinary step. A
        // raster row costs far more, because the advanced machine republishes a whole scanline table for each band.
        var total = CostBound.Known(cycles:
            ((document.Sprites?.Length ?? 0) * profile.Sprite)
            + ((document.Layers?.Length ?? 0) * profile.StepSet * 3)
            + ((document.Raster?.Length ?? 0) == 0 ? 0L : profile.RasterSetup + ((document.Raster?.Length ?? 0) * profile.RasterRow))
            + ((document.Sounds?.Length ?? 0) == 0 ? 0L : profile.Sound));
        // A guard is tested whether or not it holds, so every rule's conditions are charged.
        var rules = document.Rules ?? [];
        foreach (var rule in rules) {
            total = CostBound.Add(left: total, right: Conditions(gate: rule?.When, profile: profile));
        }

        var bodies = new CostBound[rules.Length];
        for (var index = 0; index < rules.Length; ++index) {
            bodies[index] = Statements(statements: rules[index]?.Body, cells: cells, payload: payload, profile: profile);
        }

        var guards = new (string? Name, int Value)[rules.Length];
        for (var index = 0; index < rules.Length; ++index) {
            guards[index] = Guard(rule: rules[index]);
        }

        var exclusive = ExclusiveGuards(rules: rules, guards: guards, scene: document.Scene);
        for (var index = 0; index < rules.Length; ++index) {
            if (guards[index].Name is not { } name || !exclusive.Contains(item: name)) {
                total = CostBound.Add(left: total, right: bodies[index]);
            }
        }

        // Each settled guard contributes only its dearest arm: one value of the variable holds for the whole frame,
        // so the rules keyed to the other values cannot run.
        foreach (var name in exclusive) {
            var arms = new Dictionary<int, CostBound>();
            for (var index = 0; index < rules.Length; ++index) {
                if (guards[index].Name != name) {
                    continue;
                }

                var value = guards[index].Value;
                arms[key: value] = arms.TryGetValue(key: value, value: out var running)
                    ? CostBound.Add(left: running, right: bodies[index])
                    : bodies[index];
            }

            var dearest = CostBound.Zero;
            foreach (var arm in arms.Values) {
                dearest = CostBound.Max(left: dearest, right: arm);
            }

            total = CostBound.Add(left: total, right: dearest);
        }

        return total;
    }

    /// <summary>Returns the guard a rule is keyed to: one equality against a constant, or none.</summary>
    /// <param name="rule">The rule to read.</param>
    /// <returns>The guard variable's name and the value it must hold, or a null name.</returns>
    /// <remarks>The shape a phase machine's rules take.</remarks>
    public static (string? Name, int Value) Guard(CartridgeRule? rule) =>
        ((rule?.When is ActionPredicate.CompareValue { Comparison: ActionStateComparison.Equal } compare)
            && (Slot(expression: compare.Left) is { } name)
            && (Whole(expression: compare.Right) is { } value))
                ? (name, value)
                : (null, 0);

    // The one-token forms a guard is recognised by: a bare slot read on the left, a bare literal on the right.
    private static string? Slot(ValueExpression expression) =>
        ((expression.Tokens is [ValueToken.State { Key: null } state]) ? state.Name : null);

    private static int? Whole(ValueExpression expression) =>
        ((expression.Tokens is [ValueToken.Constant constant]) && (decimal.Truncate(d: constant.Value) == constant.Value)
            ? (int)constant.Value
            : null);

    /// <summary>Returns the guard variables whose values genuinely partition a frame.</summary>
    /// <param name="rules">The document's rules, in evaluation order.</param>
    /// <param name="guards">Each rule's guard, from <see cref="Guard"/>.</param>
    /// <param name="scene">The document's declared scene variable, or null.</param>
    /// <returns>The names that partition.</returns>
    /// <remarks>
    /// An UNDECLARED guard only partitions if nothing can change it while the rules keyed to it are still being
    /// evaluated, so a write to it anywhere from the first such rule through the last disqualifies it — including a
    /// write by a rule that is not itself guarded on it. A write after the last one is what a phase machine's own
    /// advance step is, and it is harmless. The document's declared <c>scene</c> is exempt: the frame compares every
    /// guard on it against a snapshot taken before any rule runs.
    /// </remarks>
    public static HashSet<string> ExclusiveGuards(CartridgeRule[] rules, (string? Name, int Value)[] guards, string? scene = null) {
        var candidates = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var guard in guards) {
            if (guard.Name is { } name) {
                candidates.Add(item: name);
            }
        }

        foreach (var name in candidates.ToArray()) {
            // A DECLARED scene is snapshotted before any rule evaluates and every guard on it compares against that
            // snapshot, so a mid-frame write cannot let a second scene run: the partition holds wherever the write sits.
            if (name == scene) {
                continue;
            }

            var first = Array.FindIndex(array: guards, match: guard => guard.Name == name);
            var last = Array.FindLastIndex(array: guards, match: guard => guard.Name == name);
            for (var index = first; index <= last; ++index) {
                if (Writes(statements: rules[index]?.Body, name: name)) {
                    candidates.Remove(item: name);
                    break;
                }
            }
        }

        return candidates;
    }

    private static bool Writes(CartridgeStatement[]? statements, string name) {
        foreach (var statement in statements ?? []) {
            if (statement is null) {
                continue;
            }

            if ((statement.Target is { Key: null } target) && (target.State == name)) {
                return true;
            }

            // A counted loop writes its own index, which can be a guard variable elsewhere in the document.
            if (statement.Index == name) {
                return true;
            }

            if (Writes(statements: statement.Then, name: name)
                || Writes(statements: statement.Else, name: name)
                || Writes(statements: statement.Body, name: name)) {
                return true;
            }
        }

        return false;
    }


    // A gate prices as the comparisons it reaches. Composition costs nothing of its own: all and any are the branch
    // structure the arms were already charged for, and not swaps a branch condition rather than adding work.
    private static CostBound Conditions(ActionPredicate? gate, CartridgeCostProfile profile) => (gate switch {
        null => CostBound.Zero,
        ActionPredicate.All all => Composite(predicates: all.Predicates, profile: profile),
        ActionPredicate.Any any => Composite(predicates: any.Predicates, profile: profile),
        ActionPredicate.Not not => Conditions(gate: not.Predicate, profile: profile),
        ActionPredicate.CompareValue compare => CostBound.Add(
            left: CostBound.Known(cycles: profile.ConditionCompare),
            right: CostBound.Add(left: Operand(expression: compare.Left, profile: profile), right: Operand(expression: compare.Right, profile: profile))),
        _ => CostBound.Unmodeled(reason: $"Gate '{gate.GetType().Name}' has no measured weight."),
    });

    private static CostBound Composite(IReadOnlyList<ActionPredicate> predicates, CartridgeCostProfile profile) {
        var total = CostBound.Zero;

        foreach (var predicate in predicates) {
            total = CostBound.Add(left: total, right: Conditions(gate: predicate, profile: profile));
        }

        return total;
    }

    // A branch costs its conditions plus its costlier arm; a loop multiplies its body by the literal count, which is
    // why a repeat's count may not be a variable.
    private static CostBound Statements(CartridgeStatement[]? statements, IReadOnlyDictionary<string, long> cells, long payload, CartridgeCostProfile profile) {
        var total = CostBound.Zero;
        foreach (var statement in statements ?? []) {
            total = CostBound.Add(left: total, right: statement?.Kind switch {
                "set" => Step(statement: statement, profile: profile),
                "if" => CostBound.Add(
                    left: Conditions(gate: statement.When, profile: profile),
                    right: CostBound.Max(left: Statements(statements: statement.Then, cells: cells, payload: payload, profile: profile), right: Statements(statements: statement.Else, cells: cells, payload: payload, profile: profile))),
                "repeat" => CostBound.Add(
                    left: CostBound.Known(cycles: profile.LoopSetup),
                    right: CostBound.Multiply(bound: CostBound.Add(left: CostBound.Known(cycles: profile.LoopStep), right: Statements(statements: statement.Body, cells: cells, payload: payload, profile: profile)), multiplier: statement.Count ?? 0)),
                // A break emits one jump, strictly less than the load, modify and store a set emits, so a set's weight bounds it.
                "break" => CostBound.Known(cycles: profile.StepSet),
                // Both steps walk the payload: a gather or scatter, plus the module's checksum pass over it.
                // A play, a stop and the per-frame sequencer tick were measured together at this cost; charging the
                // whole of it to each rather than apportioning it keeps every part an upper bound.
                "clock" => CostBound.Known(cycles: profile.SaveFixed),
                // A fade republishes every palette, which is the same shape of work as a save's payload walk.
                // A plot bounds two coordinates, multiplies, and rebuilds one halfword of video memory.
                "plot" => CostBound.Add(
                    left: CostBound.Known(cycles: profile.StepMultiply + (profile.StepArithmetic * 3)),
                    right: CostBound.Add(left: Operand(expression: statement.Row, profile: profile), right: CostBound.Add(left: Operand(expression: statement.Column, profile: profile), right: Operand(expression: statement.Colour, profile: profile)))),
                // A blend is two register writes and a clamp, so it costs what an arithmetic step does.
                "blend" => CostBound.Add(left: CostBound.Known(cycles: profile.StepArithmetic), right: Operand(expression: statement.Weight, profile: profile)),
                "fade" => CostBound.Add(left: CostBound.Known(cycles: profile.SaveFixed), right: Operand(expression: statement.Amount, profile: profile)),
                "play" or "stop" => CostBound.Known(cycles: profile.Sound),
                "save" or "load" => CostBound.Known(cycles: profile.SaveFixed + (profile.SaveByte * payload)),
                "map" => CostBound.Add(
                    left: CostBound.Known(cycles: profile.MapWrite),
                    right: CostBound.Add(left: Operand(expression: statement.Row, profile: profile), right: CostBound.Add(left: Operand(expression: statement.Column, profile: profile), right: Operand(expression: statement.Tile, profile: profile)))),
                // Measurement puts a small blit near a fixed 600 units but a full-screen one past a whole frame, with
                // no model spanning both, so it carries no weight and a document using one is refused.
                // Flat to the cell cap: what a blit costs is the display-off window, not the cells copied.
                "blit" => CostBound.Known(cycles: profile.Blit),
                _ => CostBound.Unmodeled(reason: $"Step kind '{statement?.Kind}' has no measured weight."),
            });
        }

        return total;
    }

    private static CostBound Step(CartridgeStatement statement, CartridgeCostProfile profile) =>
        CostBound.Add(
            left: Combine(operation: statement.Operation, profile: profile),
            right: CostBound.Add(left: Operand(expression: statement.Value, profile: profile), right: Target(target: statement.Target, profile: profile)));

    // An operation's weight is the same whether it combines into a destination or sits inside an expression: it is the
    // same emitted helper either way, measured once. Pricing a nested operation at its combining weight charges one
    // store the expression does not perform, so an expression is priced as the sequence of single steps it replaces —
    // never under it, which is the safe direction for a number that only ever advises.
    // A write admits the ten combining operations; an expression admits a wider set. Both price from one table.
    private static CostBound Combine(ExpressionOp? operation, CartridgeCostProfile profile) =>
        (CartridgeOperations.AdmitsCombine(operation: operation)
            ? Weight(operation: operation, profile: profile)
            : CostBound.Unmodeled(reason: $"Operation '{ExpressionVocabulary.Spelling(operation: operation!.Value)}' has no sixteen-bit combining form."));

    private static CostBound Evaluate(ExpressionOp operation, CartridgeCostProfile profile) =>
        (CartridgeExpressions.Admits(operation: operation)
            ? Weight(operation: operation, profile: profile)
            : CostBound.Unmodeled(reason: $"The rule language evaluates '{ExpressionVocabulary.Spelling(operation: operation)}'; a cartridge does not."));

    private static CostBound Weight(ExpressionOp? operation, CartridgeCostProfile profile) => (operation switch {
        null => CostBound.Known(cycles: profile.StepSet),
        ExpressionOp.Add or ExpressionOp.Subtract or ExpressionOp.BitAnd or ExpressionOp.BitOr or ExpressionOp.BitXor
            or ExpressionOp.BitNot or ExpressionOp.Negate => CostBound.Known(cycles: profile.StepArithmetic),
        ExpressionOp.Multiply => CostBound.Known(cycles: profile.StepMultiply),
        ExpressionOp.Divide or ExpressionOp.Modulo => CostBound.Known(cycles: profile.StepDivide),
        ExpressionOp.ShiftLeft or ExpressionOp.ShiftRight => CostBound.Known(cycles: profile.StepShift),
        // A comparison, a bound and a choice are each a compare and a branch, which is what a gate's comparison was
        // measured at.
        ExpressionOp.Equal or ExpressionOp.NotEqual or ExpressionOp.Less or ExpressionOp.LessOrEqual
            or ExpressionOp.Greater or ExpressionOp.GreaterOrEqual or ExpressionOp.Minimum or ExpressionOp.Maximum
            or ExpressionOp.Select or ExpressionOp.Sign => CostBound.Known(cycles: profile.ConditionCompare),
        ExpressionOp.Clamp => CostBound.Known(cycles: (profile.ConditionCompare * 2L)),
        _ => CostBound.Unmodeled(reason: $"Operation '{ExpressionVocabulary.Spelling(operation: operation.Value)}' has no measured weight."),
    });

    private static CostBound Operand(ValueExpression? expression, CartridgeCostProfile profile) {
        if (expression is null) {
            return CostBound.Zero;
        }

        var total = CostBound.Zero;

        foreach (var token in expression.Tokens) {
            total = CostBound.Add(left: total, right: token switch {
                ValueToken.Constant => CostBound.Zero,
                ValueToken.State state => Read(state: state, profile: profile),
                _ => ((ExpressionVocabulary.Operation(token: token) is { } operation)
                    ? Evaluate(operation: operation, profile: profile)
                    : CostBound.Unmodeled(reason: $"'{CartridgeExpressions.Spell(token: token)}' is not an expression a cartridge evaluates.")),
            });
        }

        return total;
    }

    // A key on a read is an element index, which is the one read that carries its own inner expression.
    private static CostBound Read(ValueToken.State state, CartridgeCostProfile profile) =>
        ((CartridgeExpressions.Index(key: state.Key) is { } index)
            ? CostBound.Add(left: CostBound.Known(cycles: profile.OperandArray), right: Operand(expression: index, profile: profile))
            : CostBound.Known(cycles: (CartridgeExpressions.TryKey(name: state.Name, button: out _, mode: out _) ? profile.ConditionKey : profile.OperandVariable)));

    private static CostBound Target(CartridgeTarget? target, CartridgeCostProfile profile) =>
        ((CartridgeExpressions.Index(key: target?.Key) is { } index)
            ? CostBound.Add(left: CostBound.Known(cycles: profile.TargetArray), right: Operand(expression: index, profile: profile))
            : CostBound.Zero);

}
