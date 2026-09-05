using System.Globalization;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World;

public static partial class WorldRuleCompiler {
    // $history:<row>:<age> — the value pushed age pushes ago; the age is bounded by the ring at compile time.
    private static ResolvedOperand ResolveHistoryOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string detail) => new(WorldRuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length != 3 || key is not null) {
            throw Invalid("history read requires $history:<row>:<age> and no key");
        }
        var row = WorldDefinitionRows.FindStateRow(definition.State, tokens[1]) ?? throw Invalid($"'{tokens[1]}' names no state row");
        if (row.EffectiveDomain is not WorldStateDomain.Ring history) {
            throw Invalid($"'{tokens[1]}' is not a history row");
        }
        if (!int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var age) || age >= history.Capacity) {
            throw Invalid($"age must be 0..{history.Capacity - 1} on '{tokens[1]}'");
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.History, tokens[1], null, ValueKind: row.Kind, SymmetryArgument: age,
            StateHandle: ResolveWorldStateHandle(definition: definition, name: tokens[1])), row.Kind, name);
    }

    // pushState is a write whose destination is the ring's next slot rather than a named cell: it borrows the
    // write resolver for its one source spelling and its kind proof, then carries the effect as its own kind.
    private static CompiledWorldEffect ResolvePush(ActionEffect.PushState push, string ruleName, WorldDefinition definition) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, push.State)
            ?? throw new WorldRuleException(WorldRuleRefusal.StateRowUnknown, ruleName, $"'pushState' names no state row '{push.State}'");
        if (row.EffectiveDomain is not WorldStateDomain.Ring) {
            throw new WorldRuleException(WorldRuleRefusal.StateCellUnaddressable, ruleName, $"'pushState' requires a history row; '{push.State}' has no history trait");
        }
        var write = ResolveWrite(
            rowName: push.State,
            key: "0",
            target: ActionTarget.Self,
            write: WorldDocumentWriteKind.Set,
            value: push.Value,
            fromState: push.FromState,
            fromKey: push.FromKey,
            valueSeconds: null,
            text: null,
            expression: push.Expression,
            ruleName: ruleName,
            definition: definition,
            verb: "pushState"
        );
        return write with { Kind = WorldRuleEffectKind.PushState, Key = string.Empty, Describe = $"pushState {push.State}" };
    }
}
