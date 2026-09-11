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
/// <c>CartridgeCostMeasurement</c> reports those capacities. Each weight is the worst of the two targets' ratios, so
/// one unit means the same amount of work on either machine; how many units a machine grants per frame is its
/// <see cref="CartridgeCostProfile"/>.
/// </para>
/// <para>
/// An operation with no measured weight yields <see cref="CostBound.Unmodeled"/>, and an unmodeled document is refused
/// rather than admitted against an invented number. Adding a primitive means measuring it, not estimating it.
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
        foreach (var rule in document.Rules ?? []) {
            total = CostBound.Add(left: total, right: Conditions(conditions: rule?.When, profile: profile));
            total = CostBound.Add(left: total, right: Statements(statements: rule?.Body, cells: cells, payload: payload, profile: profile));
        }

        return total;
    }


    private static CostBound Conditions(CartridgeCondition[]? conditions, CartridgeCostProfile profile) {
        var total = CostBound.Zero;
        foreach (var condition in conditions ?? []) {
            total = CostBound.Add(left: total, right: condition?.Kind switch {
                "key" => CostBound.Known(cycles: profile.ConditionKey),
                "compare" => CostBound.Add(
                    left: CostBound.Known(cycles: profile.ConditionCompare),
                    right: CostBound.Add(left: Operand(value: condition.Left, profile: profile), right: Operand(value: condition.Right, profile: profile))),
                _ => CostBound.Unmodeled(reason: $"Condition kind '{condition?.Kind}' has no measured weight."),
            });
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
                    left: Conditions(conditions: statement.When, profile: profile),
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
                    right: CostBound.Add(left: Operand(value: statement.Row, profile: profile), right: CostBound.Add(left: Operand(value: statement.Column, profile: profile), right: Operand(value: statement.Colour, profile: profile)))),
                // A blend is two register writes and a clamp, so it costs what an arithmetic step does.
                "blend" => CostBound.Add(left: CostBound.Known(cycles: profile.StepArithmetic), right: Operand(value: statement.Weight, profile: profile)),
                "fade" => CostBound.Add(left: CostBound.Known(cycles: profile.SaveFixed), right: Operand(value: statement.Amount, profile: profile)),
                "play" or "stop" => CostBound.Known(cycles: profile.Sound),
                "save" or "load" => CostBound.Known(cycles: profile.SaveFixed + (profile.SaveByte * payload)),
                "map" => CostBound.Add(
                    left: CostBound.Known(cycles: profile.MapWrite),
                    right: CostBound.Add(left: Operand(value: statement.Row, profile: profile), right: CostBound.Add(left: Operand(value: statement.Column, profile: profile), right: Operand(value: statement.Tile, profile: profile)))),
                // Measurement puts a small blit near a fixed 600 units but a full-screen one past a whole frame, with
                // no model spanning both, so it carries no weight and a document using one is refused.
                // Flat to the cell cap: what a blit costs is the display-off window, not the cells copied.
                "blit" => CostBound.Known(cycles: profile.Blit),
                _ => CostBound.Unmodeled(reason: $"Step kind '{statement?.Kind}' has no measured weight."),
            });
        }

        return total;
    }

    private static CostBound Step(CartridgeStatement statement, CartridgeCostProfile profile) {
        var operation = statement.Operation switch {
            "set" => CostBound.Known(cycles: profile.StepSet),
            "add" or "subtract" or "and" or "or" or "xor" => CostBound.Known(cycles: profile.StepArithmetic),
            "mul" => CostBound.Known(cycles: profile.StepMultiply),
            "div" or "mod" => CostBound.Known(cycles: profile.StepDivide),
            "shl" or "shr" => CostBound.Known(cycles: profile.StepShift),
            _ => CostBound.Unmodeled(reason: $"Operation '{statement.Operation}' has no measured weight."),
        };
        return CostBound.Add(left: operation, right: CostBound.Add(left: Operand(value: statement.Value, profile: profile), right: Target(target: statement.Target, profile: profile)));
    }

    private static CostBound Operand(CartridgeValue? value, CartridgeCostProfile profile) {
        if (value?.Array is not null) {
            return CostBound.Add(left: CostBound.Known(cycles: profile.OperandArray), right: Operand(value: value.Index, profile: profile));
        }

        return CostBound.Known(cycles: value?.Variable is null ? 0L : profile.OperandVariable);
    }

    private static CostBound Target(CartridgeTarget? target, CartridgeCostProfile profile) =>
        target?.Array is null
            ? CostBound.Zero
            : CostBound.Add(left: CostBound.Known(cycles: profile.TargetArray), right: Operand(value: target.Index, profile: profile));

}
