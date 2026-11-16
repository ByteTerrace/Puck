using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The live facts an action gate's host supplies. Slots are resolved at compile time; clocks and input
/// belong to the action lane currently being evaluated.</summary>
public interface IActionGateFacts : IFacet {
    /// <summary>Reads a current action fact.</summary>
    bool Now(int fact);
    /// <summary>Reads whether a recency clock is still running.</summary>
    bool Recently(int slot);
    /// <summary>Reads a fixed-point action counter.</summary>
    FixedQ4816 Counter(int slot);
    /// <summary>Reads whether an action timer has elapsed.</summary>
    bool TimerElapsed(int slot);
    /// <summary>Reads whether input meets its channel's authored threshold.</summary>
    bool Held(int channel);
}
/// <summary>A resolved action fact, read through the same operand protocol as every rule gate.</summary>
public abstract class ActionGateOperand : FacetRuleOperand<IActionGateFacts> {
    private ActionGateOperand(CellKind kind) : base(valueKind: kind) { }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1;

    /// <summary>A current action fact.</summary>
    public sealed class Current(int fact) : ActionGateOperand(CellKind.Bool) {
        /// <inheritdoc/>
        public override Fact Read(IStateReader reader, IActionGateFacts facet) => Fact.Finite(kind: CellKind.Bool, value: (facet.Now(fact: fact) ? 1 : 0));
    }
    /// <summary>A lane's recency clock.</summary>
    public sealed class Recent(int slot) : ActionGateOperand(CellKind.Bool) {
        /// <inheritdoc/>
        public override Fact Read(IStateReader reader, IActionGateFacts facet) => Fact.Finite(kind: CellKind.Bool, value: (facet.Recently(slot: slot) ? 1 : 0));
    }
    /// <summary>A named action counter.</summary>
    public sealed class Counter(int slot) : ActionGateOperand(CellKind.Fixed) {
        /// <inheritdoc/>
        public override Fact Read(IStateReader reader, IActionGateFacts facet) => Fact.Finite(value: facet.Counter(slot: slot));
    }
    /// <summary>A named action timer's zero test.</summary>
    public sealed class Elapsed(int slot) : ActionGateOperand(CellKind.Bool) {
        /// <inheritdoc/>
        public override Fact Read(IStateReader reader, IActionGateFacts facet) => Fact.Finite(kind: CellKind.Bool, value: (facet.TimerElapsed(slot: slot) ? 1 : 0));
    }
    /// <summary>A composition channel's held test.</summary>
    public sealed class Held(int channel) : ActionGateOperand(CellKind.Bool) {
        /// <inheritdoc/>
        public override Fact Read(IStateReader reader, IActionGateFacts facet) => Fact.Finite(kind: CellKind.Bool, value: (facet.Held(channel: channel) ? 1 : 0));
    }
}
