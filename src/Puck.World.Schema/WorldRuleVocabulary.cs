using System.Globalization;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The world's compile context: the library's context over the document's state section, anchored
/// topologies, lattice-fill draws, and the placement and body-reference resolution only a world can answer.</summary>
public sealed class WorldRuleCompileContext : RuleCompileContext {
    private const string PlacementOrdinalsScope = "placements";

    public WorldRuleCompileContext(WorldDefinition definition) : base(
        section: definition.StateRaw,
        catalog: definition.StateCatalog,
        tables: TableSources(definition: definition),
        patterns: definition.Patterns,
        generators: definition.Generators,
        simulationRateHz: definition.SimulationRateHz,
        vocabulary: WorldRuleVocabulary.Instance
    ) => Definition = definition;

    /// <summary>Gets the document every name resolves against.</summary>
    public WorldDefinition Definition { get; }

    /// <inheritdoc/>
    public override CompiledTopology? FindTopology(string name) => WorldTopologyCompilation.Find(definition: Definition, name: name);

    /// <inheritdoc/>
    public override Draw? FindDraw(StateRow row) => (row.Draw ?? (((row is WorldStateRow world) && (WorldLatticeFill.FindDraw(trait: world.Field) is { } fill))
        ? new Draw(Source: fill.Source, Generator: fill.Generator, Timing: DrawTiming.Event)
        : null));

    private static TableSource[] TableSources(WorldDefinition definition) {
        var rows = (definition.Tables ?? []);
        var sources = new TableSource[rows.Count];

        for (var index = 0; index < rows.Count; index++) {
            sources[index] = (WorldAssetRowLoader.TryLoadTable(row: rows[index], document: out var document, error: out var error)
                ? new TableSource(Name: rows[index].Name, Document: document, LoadError: null)
                : new TableSource(Name: rows[index].Name, Document: null, LoadError: error));
        }

        return sources;
    }

    /// <summary>Returns the ordinal of a declared placement carrying a single-body inhabit facet.</summary>
    public int PlacementOrdinal(string channel, string placementId, string ruleName) {
        var placements = Definition.Placements;

        for (var ordinal = 0; (ordinal < placements.Count); ordinal++) {
            if (!string.Equals(a: placements[ordinal].Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                continue;
            }
            if (placements[ordinal].Inhabit is not { Count: 1 }) {
                throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names 'placement:{placementId}', which does not carry an inhabit facet with count 1");
            }

            return ordinal;
        }

        throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names 'placement:{placementId}', which is not a declared placement");
    }

    /// <summary>Returns the placement ordinal per cell of the enclosing rule's <c>forEach</c> row, in cell order,
    /// computed once per compile.</summary>
    public IReadOnlyList<int> ForEachPlacementOrdinals(string channel, string ruleName) {
        if (Scope.TryGetValue(key: PlacementOrdinalsScope, value: out var cached)) {
            return (IReadOnlyList<int>)cached;
        }
        if (ForEachRow is not { } forEachRow) {
            throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names 'placement:$each', which requires the enclosing rule to declare 'forEach'");
        }

        var row = (FindRow(name: forEachRow)
            ?? throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names 'placement:$each', but forEach row '{forEachRow}' names no declared state row"));
        var cells = (row.Cells ?? []);
        var ordinals = new int[cells.Count];

        for (var index = 0; (index < cells.Count); index++) {
            ordinals[index] = PlacementOrdinal(channel: channel, placementId: cells[index].Key.Value, ruleName: ruleName);
        }

        Scope[PlacementOrdinalsScope] = ordinals;

        return ordinals;
    }

    /// <summary>Returns how many colon-separated tokens the body reference starting at <paramref name="start"/> spans.</summary>
    public static int BodyRefTokenWidth(string[] tokens, int start) {
        if (start >= tokens.Length) {
            return 2;
        }
        if (string.Equals(a: tokens[start], b: "cell", comparisonType: StringComparison.Ordinal)) {
            return 3;
        }

        return ((RuleBindingTokens.OfReferenceToken(token: tokens[start]) != BoundKey.None) ? 1 : 2);
    }

    /// <summary>The spelling a refusal quotes for one body reference.</summary>
    public static readonly string BodyRefVocabulary =
        (("a 'body:<n>', 'argmax:<row>'/'argmin:<row>', 'cell:<row>:<key>', 'placement:<id>'/'placement:$each', or a bound " +
        string.Join(separator: '/', values: RuleBindingTokens.Bindings.Select(selector: static entry => $"'{RuleBindingTokens.ReferenceTokenOf(keyToken: entry.KeyToken)}'"))) +
        " reference");

    /// <summary>Compiles the body reference starting at <paramref name="start"/>.</summary>
    public CompiledBodyRef ResolveBodyRef(string[] tokens, int start, string ruleName, string channel) {
        var kind = tokens[start];

        if ((RuleBindingTokens.OfReferenceToken(token: kind) is var bound) && (bound != BoundKey.None)) {
            if (bound is BoundKey.Token or BoundKey.Previous) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{channel}' names '{kind}', which binds a cell key inside a pattern value expression and never a body");
            }

            RuleCompiler.RequireBindingInScope(context: this, binding: bound, spelled: kind, ruleName: ruleName, where: $"'{channel}'");

            return new CompiledBodyRef(Kind: CompiledBodyRefKind.Binding, Index: ((int)bound), Row: null);
        }
        if ((start + 1) >= tokens.Length) {
            throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names '{kind}' with no value — a body reference is {BodyRefVocabulary}");
        }

        var value = tokens[(start + 1)];

        switch (kind) {
            case "cell": {
                if ((start + 2) >= tokens.Length) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names 'cell:{value}' with no key — spell 'cell:<row>:<key>'");
                }

                var cell = RuleCompiler.ResolveCellRef(row: value, key: tokens[(start + 2)], ruleName: ruleName, context: this, channel: channel);

                return new CompiledBodyRef(Kind: CompiledBodyRefKind.Cell, Index: -1, Row: cell.Row, Key: cell.Key, Handle: cell.Handle);
            }
            case "body": {
                if (!int.TryParse(s: value, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var index) || (index < 0)) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names 'body:{value}', which is not a non-negative integer");
                }
                if (index >= Definition.Population.Capacity) {
                    throw new RuleException(refusal: WorldRuleRefusal.BodyIndexUnknown, ruleName: ruleName, detail: $"'{channel}' names 'body:{index}', which is outside the document's declared entity-table capacity ({Definition.Population.Capacity})");
                }

                return new CompiledBodyRef(Kind: CompiledBodyRefKind.Literal, Index: index, Row: null);
            }
            case "argmax":
            case "argmin": {
                if (string.IsNullOrEmpty(value: value)) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names '{kind}:' with no row");
                }

                _ = RuleCompiler.ResolveNumericRow(name: value, ruleName: ruleName, context: this, requireKeyed: true, malformed: WorldRuleRefusal.SpatialChannelMalformed, channel: channel);

                return new CompiledBodyRef(
                    Kind: ((kind == "argmax") ? CompiledBodyRefKind.ArgMax : CompiledBodyRefKind.ArgMin),
                    Index: -1,
                    Row: value,
                    Handle: RuleCompiler.ResolveHandle(context: this, name: value)
                );
            }
            case "placement":
                if (string.Equals(a: value, b: "$each", comparisonType: StringComparison.Ordinal)) {
                    return new CompiledBodyRef(Kind: CompiledBodyRefKind.Placement, Index: -1, Row: null, PlacementOrdinals: ForEachPlacementOrdinals(channel: channel, ruleName: ruleName));
                }

                return new CompiledBodyRef(Kind: CompiledBodyRefKind.Placement, Index: PlacementOrdinal(channel: channel, placementId: value, ruleName: ruleName), Row: null);
            default:
                throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{channel}' names body-reference token '{kind}:{value}' — expected 'body:<n>', 'argmax:<row>'/'argmin:<row>', or 'placement:<id>'/'placement:$each'");
        }
    }

    /// <summary>Resolves an effect's body key: a dynamic key indirection, or a literal index inside capacity.</summary>
    public (string Key, CompiledCellRef? KeyFrom) ResolveBodyAddress(string key, string verb, string ruleName) {
        if (RuleCompiler.TryResolveDynamicKey(key: key, ruleName: ruleName, context: this, verb: verb, keyFieldLabel: "key", cell: out var dynamicKey)) {
            return (key, dynamicKey);
        }
        if (
            !int.TryParse(s: key, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var index) ||
            (index < 0) ||
            (index >= Definition.Population.Capacity)
        ) {
            throw new RuleException(refusal: WorldRuleRefusal.BodyIndexUnknown, ruleName: ruleName, detail: $"'{verb}' key '{key}' does not name a body inside capacity {Definition.Population.Capacity}");
        }

        return (key, null);
    }

    /// <summary>Gets a value indicating whether a placement carries a region facet.</summary>
    public bool HasRegion(string placementId) {
        foreach (var placement in Definition.Placements) {
            if ((placement.Region is not null) && string.Equals(a: placement.Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets a value indicating whether the document declares a screen at <paramref name="index"/>.</summary>
    public bool HasScreen(int index) {
        foreach (var screen in Definition.Screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns a declared channel's ordinal, or -1.</summary>
    public int ChannelOrdinal(string name) {
        var channels = Definition.Channels;

        for (var ordinal = 0; (ordinal < channels.Count); ordinal++) {
            if ((channels[ordinal] is { } channel) && string.Equals(a: channel.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return ordinal;
            }
        }

        return -1;
    }
}

/// <summary>The families the world registers with the state library: its reserved operand channels, its effect and
/// predicate arms (and their JSON discriminators), and the <c>$pair:</c> key.</summary>
public static class WorldRuleVocabulary {
    /// <summary>The one registry every world compile and every world serializer shares.</summary>
    public static RuleVocabulary Instance { get; } = new(
        operands: [new WorldOperandFamily()],
        effects: [
            BodyProgramArm<WorldEffect.SetVerticalVelocity>(discriminator: "setVerticalVelocity"),
            BodyProgramArm<WorldEffect.ScaleVerticalVelocity>(discriminator: "scaleVerticalVelocity"),
            BodyProgramArm<WorldEffect.PlanarImpulse>(discriminator: "planarImpulse"),
            BodyProgramArm<WorldEffect.StartTimer>(discriminator: "startTimer"),
            BodyProgramArm<WorldEffect.Designate>(discriminator: "designate"),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.EmitCue), discriminator: "emitCue",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveCue(effect: (WorldEffect.EmitCue)effect, ruleName: ruleName, context: context)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.SetBodyVerticalVelocity), discriminator: "setBodyVerticalVelocity",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => { var body = (WorldEffect.SetBodyVerticalVelocity)effect; return WorldRuleCompiler.ResolveBodyVerticalVelocity(key: body.Key, value: body.Velocity, operation: BodyMotionOp.SetVerticalVelocity, verb: "setBodyVerticalVelocity", ruleName: ruleName, context: context); }
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.ScaleBodyVerticalVelocity), discriminator: "scaleBodyVerticalVelocity",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => { var body = (WorldEffect.ScaleBodyVerticalVelocity)effect; return WorldRuleCompiler.ResolveBodyVerticalVelocity(key: body.Key, value: body.Factor, operation: BodyMotionOp.ScaleVerticalVelocity, verb: "scaleBodyVerticalVelocity", ruleName: ruleName, context: context); }
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.ApplyBodyImpulse), discriminator: "applyBodyImpulse",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveBodyImpulse(effect: (WorldEffect.ApplyBodyImpulse)effect, ruleName: ruleName, context: context)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.DesignateBody), discriminator: "designateBody",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveBodyDesignation(effect: (WorldEffect.DesignateBody)effect, ruleName: ruleName, context: context)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.PaintField), discriminator: "paintField",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveFieldPaint(effect: (WorldEffect.PaintField)effect, ruleName: ruleName, context: context)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.UpsertHudPanel), discriminator: "upsertHudPanel",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveUpsertHudPanel(effect: (WorldEffect.UpsertHudPanel)effect, ruleName: ruleName)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.RemoveHudPanel), discriminator: "removeHudPanel",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveRemoveHudPanel(effect: (WorldEffect.RemoveHudPanel)effect, ruleName: ruleName)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.UpsertPlacement), discriminator: "upsertPlacement",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveUpsertPlacement(effect: (WorldEffect.UpsertPlacement)effect, ruleName: ruleName)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.RemovePlacement), discriminator: "removePlacement",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolveRemovePlacement(effect: (WorldEffect.RemovePlacement)effect, ruleName: ruleName)
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.Save), discriminator: "save",
                allowsTransaction: false,
                compile: static (effect, ruleName, context) => SaveEffect.Instance
            ),
            new WorldEffectArm(
                effectType: typeof(WorldEffect.Pose), discriminator: "pose",
                allowsTransaction: true,
                compile: static (effect, ruleName, context) => WorldRuleCompiler.ResolvePose(effect: (WorldEffect.Pose)effect, ruleName: ruleName, context: context)
            ),
        ],
        predicates: [
            new WorldPredicateArm(predicateType: typeof(WorldPredicate.Now), discriminator: "now"),
            new WorldPredicateArm(predicateType: typeof(WorldPredicate.Recently), discriminator: "recently"),
            new WorldPredicateArm(predicateType: typeof(WorldPredicate.TimerElapsed), discriminator: "timerElapsed"),
            new WorldPredicateArm(predicateType: typeof(WorldPredicate.Held), discriminator: "held"),
        ],
        keys: [new PairKeyFamily()]
    );

    private static WorldEffectArm BodyProgramArm<TEffect>(string discriminator) where TEffect : ActionEffect => new(
        effectType: typeof(TEffect),
        discriminator: discriminator,
        allowsTransaction: false,
        compile: (effect, ruleName, context) => throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'{discriminator}' has no world-scope meaning — it belongs to a kit's action programs")
    );

    private sealed class WorldEffectArm : EffectFamily {
        private readonly Func<ActionEffect, string, WorldRuleCompileContext, EffectFact> m_compile;

        public WorldEffectArm(Type effectType, string discriminator, bool allowsTransaction, Func<ActionEffect, string, WorldRuleCompileContext, EffectFact> compile) {
            EffectType = effectType;
            Discriminator = discriminator;
            AllowsTransaction = allowsTransaction;
            m_compile = compile;
        }

        public override Type EffectType { get; }
        public override string Discriminator { get; }
        public override bool AllowsTransaction { get; }

        public override EffectFact Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => m_compile(effect, ruleName, (WorldRuleCompileContext)context);
    }

    private sealed class WorldPredicateArm : PredicateFamily {
        public WorldPredicateArm(Type predicateType, string discriminator) {
            PredicateType = predicateType;
            Discriminator = discriminator;
        }

        public override Type PredicateType { get; }
        public override string Discriminator { get; }

        public override GateToken Compile(ActionPredicate predicate, string ruleName, RuleCompileContext context) => throw new RuleException(
            refusal: RuleRefusal.PredicateKindInadmissible,
            ruleName: ruleName,
            detail: $"'{Discriminator}' has no world-scope meaning — world gates admit 'compareState', 'compareValue', 'all', 'any', and 'not'"
        );
    }

    private sealed class PairKeyFamily : KeyFamily {
        public override bool TryCompile(string key, string ruleName, string verb, string keyFieldLabel, RuleCompileContext context, out CompiledCellRef cell) {
            if (!key.StartsWith(value: WorldRuleFacts.PairKeyPrefix, comparisonType: StringComparison.Ordinal)) {
                cell = default;

                return false;
            }

            var world = (WorldRuleCompileContext)context;
            var tokens = key[WorldRuleFacts.PairKeyPrefix.Length..].Split(separator: ':');
            var widthA = WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: 0);

            if (tokens.Length != (widthA + WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: widthA))) {
                throw new RuleException(refusal: WorldRuleRefusal.PairKeyMalformed, ruleName: ruleName, detail: $"'{verb}' {keyFieldLabel} '{key}' does not spell '{WorldRuleFacts.PairKeyPrefix}<bodyRefA>:<bodyRefB>' (each {WorldRuleCompileContext.BodyRefVocabulary})");
            }

            var channel = $"{verb} {keyFieldLabel} '{key}'";

            cell = new CompiledCellRef(
                Row: string.Empty,
                Key: string.Empty,
                Custom: new PairKeyFact(
                    bodyA: world.ResolveBodyRef(tokens: tokens, start: 0, ruleName: ruleName, channel: channel),
                    bodyB: world.ResolveBodyRef(tokens: tokens, start: widthA, ruleName: ruleName, channel: channel)
                )
            );

            return true;
        }
    }

    private sealed class WorldOperandFamily : OperandFamily {
        private const string BoardCellOfPrefix = "$board:cellOf:";

        public override IReadOnlyList<string> Spellings { get; } = [
            WorldRuleFacts.Population,
            WorldRuleFacts.PhysicsQuiescent,
            $"{WorldRuleFacts.RegionPrefix}<placementId>",
            $"{WorldRuleFacts.MachinePrefix}<screen>:<address>",
            $"{WorldRuleFacts.ArgMaxPrefix}<row>",
            $"{WorldRuleFacts.ArgMinPrefix}<row>",
            $"{WorldRuleFacts.DistancePrefix}<a>:<b>",
            $"{WorldRuleFacts.LineOfSightPrefix}<a>:<b>",
            $"{WorldRuleFacts.UprightPrefix}<bodyRef>",
            $"{WorldRuleFacts.NavigationPrefix}<bodyRef>:<facet>",
            $"{WorldRuleFacts.ParkedPrefix}<bodyRef>",
            $"{WorldRuleFacts.LinkPrefix}<adjacencyName>",
            $"{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>",
            $"{WorldRuleFacts.NearestPrefix}<bodyRef>:<row>",
            $"{WorldRuleFacts.ClockPrefix}<music>:phaseError",
            $"{BoardCellOfPrefix}<row>:<bodyRef>",
        ];

        public override bool TryCompile(string name, string? key, in OperandSite site, RuleCompileContext context, out OperandFact? fact) {
            var world = (WorldRuleCompileContext)context;
            var ruleName = site.RuleName;

            fact = null;
            if (string.Equals(a: name, b: WorldRuleFacts.Population, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                fact = PopulationOperand.Instance;
            } else if (string.Equals(a: name, b: WorldRuleFacts.PhysicsQuiescent, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                fact = PhysicsQuiescentOperand.Instance;
            } else if (name.StartsWith(value: WorldRuleFacts.RegionPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var placementId = name[WorldRuleFacts.RegionPrefix.Length..];
                if (string.IsNullOrEmpty(value: placementId) || !world.HasRegion(placementId: placementId)) {
                    throw new RuleException(refusal: WorldRuleRefusal.RegionUnknown, ruleName: ruleName, detail: $"'{name}' names no placement carrying a region facet");
                }
                fact = new RegionOccupancyOperand(placementId: placementId);
            } else if (name.StartsWith(value: WorldRuleFacts.MachinePrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var suffix = name[WorldRuleFacts.MachinePrefix.Length..];
                var separator = suffix.IndexOf(value: ':', comparisonType: StringComparison.Ordinal);
                if (
                    (separator < 0) ||
                    !int.TryParse(s: suffix[..separator], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var screen) ||
                    !int.TryParse(s: suffix[(separator + 1)..], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var address) ||
                    (screen < 0) ||
                    (address < 0)
                ) {
                    throw new RuleException(refusal: WorldRuleRefusal.MachineChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.MachinePrefix}<screen>:<address>' with non-negative integers");
                }
                if (!world.HasScreen(index: screen)) {
                    throw new RuleException(refusal: WorldRuleRefusal.ScreenUnknown, ruleName: ruleName, detail: $"'{name}' names screen {screen}, which the document does not declare");
                }
                fact = new MachineMemoryOperand(screen: screen, address: address);
            } else if (name.StartsWith(value: WorldRuleFacts.ArgMaxPrefix, comparisonType: StringComparison.Ordinal) || name.StartsWith(value: WorldRuleFacts.ArgMinPrefix, comparisonType: StringComparison.Ordinal)) {
                fact = ArgBody(name: name, key: key, site: in site, world: world);
            } else if (name.StartsWith(value: WorldRuleFacts.DistancePrefix, comparisonType: StringComparison.Ordinal) || name.StartsWith(value: WorldRuleFacts.LineOfSightPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var isDistance = name.StartsWith(value: WorldRuleFacts.DistancePrefix, comparisonType: StringComparison.Ordinal);
                var tokens = name[(isDistance ? WorldRuleFacts.DistancePrefix.Length : WorldRuleFacts.LineOfSightPrefix.Length)..].Split(separator: ':');
                var widthA = WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: 0);
                if (tokens.Length != (widthA + WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: widthA))) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{(isDistance ? WorldRuleFacts.DistancePrefix : WorldRuleFacts.LineOfSightPrefix)}<bodyRefA>:<bodyRefB>' (each {WorldRuleCompileContext.BodyRefVocabulary})");
                }
                var bodyA = world.ResolveBodyRef(tokens: tokens, start: 0, ruleName: ruleName, channel: name);
                var bodyB = world.ResolveBodyRef(tokens: tokens, start: widthA, ruleName: ruleName, channel: name);
                fact = (isDistance ? new BodyDistanceOperand(bodyA: bodyA, bodyB: bodyB) : new LineOfSightOperand(bodyA: bodyA, bodyB: bodyB));
            } else if (name.StartsWith(value: WorldRuleFacts.UprightPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var tokens = name[WorldRuleFacts.UprightPrefix.Length..].Split(separator: ':');
                if (tokens.Length != WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: 0)) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.UprightPrefix}<bodyRef>' ({WorldRuleCompileContext.BodyRefVocabulary})");
                }
                fact = new UprightOperand(bodyA: world.ResolveBodyRef(tokens: tokens, start: 0, ruleName: ruleName, channel: name));
            } else if (name.StartsWith(value: WorldRuleFacts.ParkedPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var tokens = name[WorldRuleFacts.ParkedPrefix.Length..].Split(separator: ':');
                if (tokens.Length != 2) {
                    throw new RuleException(refusal: WorldRuleRefusal.ParkedChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.ParkedPrefix}<bodyRef>' (a 'body:<n>' or 'argmax:<row>'/'argmin:<row>' pair)");
                }
                fact = new ParkedOperand(bodyA: world.ResolveBodyRef(tokens: tokens, start: 0, ruleName: ruleName, channel: name));
            } else if (name.StartsWith(value: WorldRuleFacts.ChannelPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var suffix = name[WorldRuleFacts.ChannelPrefix.Length..];
                var separator = suffix.IndexOf(value: ':', comparisonType: StringComparison.Ordinal);
                var seats = world.Definition.Population.LocalSeats;
                if (
                    (separator < 0) ||
                    !int.TryParse(s: suffix[..separator], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var seat) ||
                    (seat < 1) ||
                    (seat > seats) ||
                    string.IsNullOrEmpty(value: suffix[(separator + 1)..])
                ) {
                    throw new RuleException(refusal: WorldRuleRefusal.ChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>' with seat in 1..{seats}");
                }
                var channelName = suffix[(separator + 1)..];
                var channelOrdinal = world.ChannelOrdinal(name: channelName);
                if (channelOrdinal < 0) {
                    throw new RuleException(refusal: WorldRuleRefusal.ChannelMalformed, ruleName: ruleName, detail: $"'{name}' names channel '{channelName}', which the document does not declare in 'channels[]'");
                }
                fact = new ChannelOperand(seat: (seat - 1), channelOrdinal: channelOrdinal);
            } else if (name.StartsWith(value: WorldRuleFacts.LinkPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var adjacencyName = name[WorldRuleFacts.LinkPrefix.Length..];
                if (string.IsNullOrEmpty(value: adjacencyName) || adjacencyName.Contains(value: ':') || (WorldDefinitionRows.FindAdjacency(adjacencies: world.Definition.Adjacencies, name: adjacencyName) is null)) {
                    throw new RuleException(refusal: WorldRuleRefusal.LinkChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.LinkPrefix}<adjacencyName>' naming a declared 'adjacencies' row");
                }
                fact = new LinkStalenessOperand(adjacencyName: adjacencyName);
            } else if (name.StartsWith(value: WorldRuleFacts.NavigationPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var tokens = name[WorldRuleFacts.NavigationPrefix.Length..].Split(separator: ':');
                var width = WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: 0);
                if ((tokens.Length != (width + 1)) || tokens[width] is not ("hasPath" or "active" or "arrived" or "unreachable" or "remaining" or "pending" or "capacity")) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.NavigationPrefix}<bodyRef>:<hasPath|active|arrived|unreachable|remaining|pending|capacity>' ({WorldRuleCompileContext.BodyRefVocabulary})");
                }
                fact = new NavigationOperand(bodyA: world.ResolveBodyRef(tokens: tokens, start: 0, ruleName: ruleName, channel: name), facet: tokens[width]);
            } else if (name.StartsWith(value: WorldRuleFacts.NearestPrefix, comparisonType: StringComparison.Ordinal)) {
                RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);
                var tokens = name[WorldRuleFacts.NearestPrefix.Length..].Split(separator: ':');
                var width = WorldRuleCompileContext.BodyRefTokenWidth(tokens: tokens, start: 0);
                if (tokens.Length != (width + 1)) {
                    throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{WorldRuleFacts.NearestPrefix}<bodyRef>:<row>' ({WorldRuleCompileContext.BodyRefVocabulary}, then the keyed tag row)");
                }
                var from = world.ResolveBodyRef(tokens: tokens, start: 0, ruleName: ruleName, channel: name);
                _ = RuleCompiler.ResolveNumericRow(name: tokens[width], ruleName: ruleName, context: world, requireKeyed: true, malformed: WorldRuleRefusal.SpatialChannelMalformed, channel: name);
                fact = new NearestOperand(bodyA: from, row: tokens[width], stateHandle: RuleCompiler.ResolveHandle(context: world, name: tokens[width]));
            } else if (name.StartsWith(value: WorldRuleFacts.ClockPrefix, comparisonType: StringComparison.Ordinal)) {
                var tokens = name.Split(separator: ':');
                if ((tokens.Length != 3) || (tokens[2] != "phaseError")) {
                    throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: "clock read requires $clock:<music>:phaseError");
                }
                if (key is not null) {
                    throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: "phaseError does not accept key");
                }
                if ((world.Definition.Music is not { Count: > 0 } music) || (music[0]?.Name != tokens[1])) {
                    throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{tokens[1]}' does not name the document's declared music row");
                }
                fact = ClockOperand.Instance;
            } else if (name.StartsWith(value: BoardCellOfPrefix, comparisonType: StringComparison.Ordinal)) {
                if (key is not null) {
                    throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: "cellOf does not accept key");
                }
                var tokens = name[BoardCellOfPrefix.Length..].Split(separator: ':');
                var row = world.FindRow(name: tokens[0]);
                if ((row?.EffectiveDomain is not StateDomain.CellsOf board) || (world.FindTopology(name: board.Topology) is not { } topology)) {
                    throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{tokens[0]}' names no discrete board row");
                }
                var bodyTokens = tokens[1..];
                if (bodyTokens.Length != WorldRuleCompileContext.BodyRefTokenWidth(tokens: bodyTokens, start: 0)) {
                    throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' does not spell '{BoardCellOfPrefix}<row>:<bodyRef>' ({WorldRuleCompileContext.BodyRefVocabulary})");
                }
                fact = new BoardCellOfOperand(row: row.Name.Value, topology: topology, bodyA: world.ResolveBodyRef(tokens: bodyTokens, start: 0, ruleName: ruleName, channel: name));
            }

            return (fact is not null);
        }

        private static ArgBodyOperand ArgBody(string name, string? key, in OperandSite site, WorldRuleCompileContext world) {
            var ruleName = site.RuleName;

            RuleCompiler.RefuseKeyOnReservedChannel(key: key, ruleName: ruleName, name: name, keyFieldLabel: site.KeyFieldLabel);

            var isMax = name.StartsWith(value: WorldRuleFacts.ArgMaxPrefix, comparisonType: StringComparison.Ordinal);
            var rowAndFilter = name[(isMax ? WorldRuleFacts.ArgMaxPrefix.Length : WorldRuleFacts.ArgMinPrefix.Length)..];
            const string WhereMarker = ":where:";
            var where = rowAndFilter.IndexOf(value: WhereMarker, comparisonType: StringComparison.Ordinal);
            var rowName = ((where < 0) ? rowAndFilter : rowAndFilter[..where]);
            var filterRowName = ((where < 0) ? null : rowAndFilter[(where + WhereMarker.Length)..]);

            if (string.IsNullOrEmpty(value: rowName) || ((where >= 0) && string.IsNullOrEmpty(value: filterRowName))) {
                throw new RuleException(refusal: RuleRefusal.ArgChannelMalformed, ruleName: ruleName, detail: $"'{name}' does not spell '{(isMax ? WorldRuleFacts.ArgMaxPrefix : WorldRuleFacts.ArgMinPrefix)}<row>'");
            }

            _ = RuleCompiler.ResolveNumericRow(name: rowName, ruleName: ruleName, context: world, requireKeyed: true, malformed: RuleRefusal.ArgChannelMalformed, channel: name);

            StateHandle filterHandle = default;

            if (filterRowName is not null) {
                _ = RuleCompiler.ResolveNumericRow(name: filterRowName, ruleName: ruleName, context: world, requireKeyed: true, malformed: RuleRefusal.ArgChannelMalformed, channel: name);
                filterHandle = RuleCompiler.ResolveHandle(context: world, name: filterRowName);
            }

            return new ArgBodyOperand(
                row: rowName,
                stateHandle: RuleCompiler.ResolveHandle(context: world, name: rowName),
                reduce: (isMax ? StateReduceOp.Max : StateReduceOp.Min),
                filterRow: filterRowName,
                filterHandle: filterHandle
            );
        }
    }
}
