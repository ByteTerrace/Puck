using Puck.Physics.Motion;
using Puck.State.Rules;

namespace Puck.World;

public static partial class BodyActionSpecFactory {
    private static readonly StateCatalog GateCatalog = StateCatalog.Compile(section: null);
    private static readonly RuleVocabulary GateVocabulary = new(operands: [], effects: [], keys: [], predicates: [
        new BodyPredicateFamily(type: typeof(WorldPredicate.Now)),
        new BodyPredicateFamily(type: typeof(WorldPredicate.Recently)),
        new BodyPredicateFamily(type: typeof(WorldPredicate.Held)),
        new BodyPredicateFamily(type: typeof(WorldPredicate.TimerElapsed)),
        new BodyPredicateFamily(type: typeof(ActionPredicate.CompareState)),
    ], corePredicates: false);

    /// <summary>Compiles an action or shaping gate through the rule compiler, allocating one shared recency slot
    /// per authored recency test. The body's vocabulary resolves leaves; the rule compiler owns Boolean structure.</summary>
    /// <param name="predicate">The authored gate, or null for always.</param>
    /// <param name="recencyFacts">The lane's recency facts.</param>
    /// <param name="recencyWindows">The lane's recency windows.</param>
    /// <param name="stateSlots">The action-state slots, absent for a shaping gate.</param>
    /// <param name="channels">The shaping gate's channel table, absent for an action trigger.</param>
    /// <returns>The compiled rule gate.</returns>
    public static GateToken[] CompileGate(ActionPredicate? predicate, List<ActionFact> recencyFacts,
        List<ulong> recencyWindows, IReadOnlyDictionary<string, int>? stateSlots = null, WorldChannelTable? channels = null) {
        return RuleCompiler.CompileGate(predicate, "body gate",
            new BodyGateContext(channels: channels, facts: recencyFacts, slots: stateSlots, windows: recencyWindows));
    }

    private sealed class BodyGateContext(List<ActionFact> facts, List<ulong> windows,
        IReadOnlyDictionary<string, int>? slots, WorldChannelTable? channels)
        : RuleCompileContext(null, GateCatalog, null, null, null, 0, GateVocabulary) {
        public List<ActionFact> Facts { get; } = facts;
        public List<ulong> Windows { get; } = windows;
        public IReadOnlyDictionary<string, int>? Slots { get; } = slots;
        public WorldChannelTable? Channels { get; } = channels;
    }
    private sealed class BodyPredicateFamily(Type type) : PredicateFamily {
        public override string Discriminator => type.Name;
        public override Type PredicateType => type;

        public override GateToken Compile(ActionPredicate predicate, string ruleName, RuleCompileContext context) {
            var body = ((BodyGateContext)context);
            ActionGateOperand operand;
            var comparison = ExpressionOp.NotEqual;
            var value = 0L;

            switch (predicate) {
                case WorldPredicate.Now now:
                    operand = new ActionGateOperand.Current(fact: ((int)now.Fact));
                    break;
                case WorldPredicate.Recently recently:
                    operand = new ActionGateOperand.Recent(slot: body.Facts.Count);
                    body.Facts.Add(item: recently.Fact);
                    body.Windows.Add(item: DurationTicks(seconds: recently.WindowSeconds));
                    break;
                case WorldPredicate.Held held:
                    if ((body.Channels is not { } channels) || !channels.TryGetOrdinal(name: held.Channel, ordinal: out var channel)) {
                        throw new InvalidOperationException(message: $"Predicate 'held' names channel '{held.Channel}', which does not resolve against the world's channel table — 'held' is legitimate only inside a kit's shaping-row gate.");
                    }
                    operand = new ActionGateOperand.Held(channel: channel);
                    break;
                case WorldPredicate.TimerElapsed elapsed:
                    operand = new ActionGateOperand.Elapsed(slot: ResolveState(name: elapsed.State, stateSlots: body.Slots));
                    break;
                case ActionPredicate.CompareState compare:
                    if (compare.Key is not null) {
                        throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries a 'key' — a per-body action-state slot is not keyed; 'key' addresses a world state row's cell and is legitimate only in a world rule.");
                    }
                    if ((compare.ComparandState is not null) || (compare.ComparandKey is not null)) {
                        throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries a 'comparandState'/'comparandKey' — a per-body action-state slot has no world state row to reference; a comparand row is legitimate only in a world rule.");
                    }
                    if ((compare.Text is not null) || (compare.Value is not { } constant) || !compare.Comparison.IsComparison()) {
                        throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' requires a numeric 'value' and a comparison.");
                    }
                    operand = new ActionGateOperand.Counter(slot: ResolveState(name: compare.State.Spelling, stateSlots: body.Slots));
                    comparison = compare.Comparison;
                    value = NumericLiteral.ToFixed(value: constant).Value;
                    break;
                default:
                    throw new InvalidOperationException(message: $"'{predicate.GetType().Name}' has no body-gate meaning.");
            }
            context.Needs.AddFact(fact: operand);
            return new GateToken(CompiledValueSource.FromOperand(operand: operand), comparison,
                CompiledValueSource.Constant(rawValue: value), operand.ValueKind, predicate.GetType().Name);
        }
    }
}

