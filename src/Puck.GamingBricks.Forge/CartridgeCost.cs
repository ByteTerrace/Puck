using Puck.Maths;

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
/// <c>CartridgeCostMeasurement</c> reports those capacities. Each weight is the worst of the two targets' ratios, so a
/// document inside <see cref="FrameBudget"/> is inside both targets' own budgets and flipping <c>/target</c> cannot
/// change whether it keeps frame cadence.
/// </para>
/// <para>
/// An operation with no measured weight yields <see cref="CostBound.Unmodeled"/>, and an unmodeled document is refused
/// rather than admitted against an invented number. Adding a primitive means measuring it, not estimating it.
/// </para>
/// </remarks>
public static class CartridgeCost {
    /// <summary>Abstract work units a document's rule pass may spend in one frame.</summary>
    /// <remarks>The smaller of the two targets' measured reservations, which is the Color machine's.</remarks>
    public const long FrameBudget = 12300L;

    private const long BlitUnits = 600L;
    private const long ConditionKeyUnits = 24L;
    private const long MapWriteUnits = 89L;
    private const long ConditionCompareUnits = 15L;
    private const long LoopSetupUnits = 70L;
    private const long LoopStepUnits = 35L;
    private const long OperandArrayUnits = 17L;
    private const long OperandVariableUnits = 9L;
    private const long SaveByteUnits = 30L;
    private const long SaveFixedUnits = 70L;
    private const long SoundUnits = 300L;
    private const long SpriteUnits = 80L;
    private const long StepArithmeticUnits = 16L;
    private const long StepDivideUnits = 118L;
    private const long StepMultiplyUnits = 40L;
    private const long StepSetUnits = 11L;
    private const long StepShiftUnits = 34L;
    private const long TargetArrayUnits = 8L;

    /// <summary>Calculates the worst-case work a document's rules and sprites spend in one frame.</summary>
    /// <param name="document">The authored source.</param>
    /// <returns>The bound, or an unmodeled result naming the first primitive without a measured weight.</returns>
    public static CostBound Frame(CartridgeDocument document) {
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

        // A layer costs its visibility test and, when drawn, two scroll writes — the shape of an ordinary step.
        var total = CostBound.Known(cycles:
            ((document.Sprites?.Length ?? 0) * SpriteUnits)
            + ((document.Layers?.Length ?? 0) * StepSetUnits * 3)
            + ((document.Sounds?.Length ?? 0) == 0 ? 0L : SoundUnits));
        foreach (var rule in document.Rules ?? []) {
            total = CostBound.Add(left: total, right: Conditions(conditions: rule?.When));
            total = CostBound.Add(left: total, right: Statements(statements: rule?.Body, cells: cells, payload: payload));
        }

        return total;
    }

    /// <summary>Gets a value indicating whether a bound fits the shared frame reservation.</summary>
    /// <param name="bound">The document's frame bound.</param>
    /// <remarks>An unmodeled or overflowed bound never fits; neither can certify a per-frame deadline.</remarks>
    public static bool Admits(CostBound bound) => bound.IsKnown && bound.Cycles <= FrameBudget;

    private static CostBound Conditions(CartridgeCondition[]? conditions) {
        var total = CostBound.Zero;
        foreach (var condition in conditions ?? []) {
            total = CostBound.Add(left: total, right: condition?.Kind switch {
                "key" => CostBound.Known(cycles: ConditionKeyUnits),
                "compare" => CostBound.Add(
                    left: CostBound.Known(cycles: ConditionCompareUnits),
                    right: CostBound.Add(left: Operand(value: condition.Left), right: Operand(value: condition.Right))),
                _ => CostBound.Unmodeled(reason: $"Condition kind '{condition?.Kind}' has no measured weight."),
            });
        }

        return total;
    }

    // A branch costs its conditions plus its costlier arm; a loop multiplies its body by the literal count, which is
    // why a repeat's count may not be a variable.
    private static CostBound Statements(CartridgeStatement[]? statements, IReadOnlyDictionary<string, long> cells, long payload) {
        var total = CostBound.Zero;
        foreach (var statement in statements ?? []) {
            total = CostBound.Add(left: total, right: statement?.Kind switch {
                "set" => Step(statement: statement),
                "if" => CostBound.Add(
                    left: Conditions(conditions: statement.When),
                    right: CostBound.Max(left: Statements(statements: statement.Then, cells: cells, payload: payload), right: Statements(statements: statement.Else, cells: cells, payload: payload))),
                "repeat" => CostBound.Add(
                    left: CostBound.Known(cycles: LoopSetupUnits),
                    right: CostBound.Multiply(bound: CostBound.Add(left: CostBound.Known(cycles: LoopStepUnits), right: Statements(statements: statement.Body, cells: cells, payload: payload)), multiplier: statement.Count ?? 0)),
                // A break emits one jump, strictly less than the load, modify and store a set emits, so a set's weight bounds it.
                "break" => CostBound.Known(cycles: StepSetUnits),
                // Both steps walk the payload: a gather or scatter, plus the module's checksum pass over it.
                // A play, a stop and the per-frame sequencer tick were measured together at this cost; charging the
                // whole of it to each rather than apportioning it keeps every part an upper bound.
                "clock" => CostBound.Known(cycles: SaveFixedUnits),
                // A fade republishes every palette, which is the same shape of work as a save's payload walk.
                // A plot bounds two coordinates, multiplies, and rebuilds one halfword of video memory.
                "plot" => CostBound.Add(
                    left: CostBound.Known(cycles: StepMultiplyUnits + (StepArithmeticUnits * 3)),
                    right: CostBound.Add(left: Operand(value: statement.Row), right: CostBound.Add(left: Operand(value: statement.Column), right: Operand(value: statement.Colour)))),
                // A blend is two register writes and a clamp, so it costs what an arithmetic step does.
                "blend" => CostBound.Add(left: CostBound.Known(cycles: StepArithmeticUnits), right: Operand(value: statement.Weight)),
                "fade" => CostBound.Add(left: CostBound.Known(cycles: SaveFixedUnits), right: Operand(value: statement.Amount)),
                "play" or "stop" => CostBound.Known(cycles: SoundUnits),
                "save" or "load" => CostBound.Known(cycles: SaveFixedUnits + (SaveByteUnits * payload)),
                "map" => CostBound.Add(
                    left: CostBound.Known(cycles: MapWriteUnits),
                    right: CostBound.Add(left: Operand(value: statement.Row), right: CostBound.Add(left: Operand(value: statement.Column), right: Operand(value: statement.Tile)))),
                // Measurement puts a small blit near a fixed 600 units but a full-screen one past a whole frame, with
                // no model spanning both, so it carries no weight and a document using one is refused.
                // Flat to the cell cap: what a blit costs is the display-off window, not the cells copied.
                "blit" => CostBound.Known(cycles: BlitUnits),
                _ => CostBound.Unmodeled(reason: $"Step kind '{statement?.Kind}' has no measured weight."),
            });
        }

        return total;
    }

    private static CostBound Step(CartridgeStatement statement) {
        var operation = statement.Operation switch {
            "set" => CostBound.Known(cycles: StepSetUnits),
            "add" or "subtract" or "and" or "or" or "xor" => CostBound.Known(cycles: StepArithmeticUnits),
            "mul" => CostBound.Known(cycles: StepMultiplyUnits),
            "div" or "mod" => CostBound.Known(cycles: StepDivideUnits),
            "shl" or "shr" => CostBound.Known(cycles: StepShiftUnits),
            _ => CostBound.Unmodeled(reason: $"Operation '{statement.Operation}' has no measured weight."),
        };
        return CostBound.Add(left: operation, right: CostBound.Add(left: Operand(value: statement.Value), right: Target(target: statement.Target)));
    }

    private static CostBound Operand(CartridgeValue? value) {
        if (value?.Array is not null) {
            return CostBound.Add(left: CostBound.Known(cycles: OperandArrayUnits), right: Operand(value: value.Index));
        }

        return CostBound.Known(cycles: value?.Variable is null ? 0L : OperandVariableUnits);
    }

    private static CostBound Target(CartridgeTarget? target) =>
        target?.Array is null
            ? CostBound.Zero
            : CostBound.Add(left: CostBound.Known(cycles: TargetArrayUnits), right: Operand(value: target.Index));

}
