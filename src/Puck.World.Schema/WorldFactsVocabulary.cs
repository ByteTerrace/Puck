using System.Globalization;
using Puck.Physics.Motion;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;
using EffectFamily = Puck.State.Rules.EffectFamily;
using GateToken = Puck.State.Rules.GateToken;
using IRuleEffect = Puck.State.Rules.IRuleEffect;
using KeyFamily = Puck.State.Rules.KeyFamily;
using PredicateFamily = Puck.State.Rules.PredicateFamily;
using RuleCompileContext = Puck.State.Rules.RuleCompileContext;
using RuleCompiler = Puck.State.Rules.RuleCompiler;
using RuleRefusal = Puck.State.Rules.RuleRefusal;
using RuleVocabulary = Puck.State.Rules.RuleVocabulary;
using TableSource = Puck.State.Rules.TableSource;

namespace Puck.World;

/// <summary>The world's compile context for the arena-addressed rule compiler: the library's context over the
/// document's state section, anchored topologies, lattice-fill draws, and the placement and body-reference
/// resolution only a world can answer.</summary>
public sealed class WorldFactsCompileContext : RuleCompileContext {
    private const string PlacementOrdinalsScope = "placements";

    /// <summary>The spelling a refusal quotes for one body reference.</summary>
    public static readonly string BodyRefVocabulary =
        (("a 'body:<n>', 'argmax:<row>'/'argmin:<row>', 'cell:<row>:<key>', 'placement:<id>'/'placement:$each', or a bound " +
        string.Join(
        separator: '/',
        values: RuleBindingTokens.Bindings.Select(selector: static entry => $"'{RuleBindingTokens.ReferenceTokenOf(keyToken: entry.KeyToken)}'")
    )) +
        " reference");

    /// <summary>Initializes the context over a document.</summary>
    /// <param name="definition">The world.</param>
    public WorldFactsCompileContext(WorldDefinition definition) : base(
        catalog: definition!.StateCatalog,
        generators: definition.Generators,
        patterns: definition.Patterns,
        section: definition.StateRaw,
        simulationRateHz: definition.SimulationRateHz,
        tables: TableSources(definition: definition),
        vocabulary: WorldFactsVocabulary.Instance
    ) {
        Definition = definition;
        Sets = definition.Sets;
    }

    /// <summary>Gets the document every name resolves against.</summary>
    public WorldDefinition Definition { get; }

    private static TableSource[] TableSources(WorldDefinition definition) {
        var rows = (definition.Tables ?? []);
        var sources = new TableSource[rows.Count];

        for (var index = 0; (index < rows.Count); index++) {
            sources[index] = (WorldAssetRowLoader.TryLoadTable(
                document: out var document,
                error: out var error,
                row: rows[index]
            )
                ? new TableSource(
                    Document: document,
                    LoadError: null,
                    Name: rows[index].Name
                )
                : new TableSource(
                    Document: null,
                    LoadError: error,
                    Name: rows[index].Name
                )
            );
        }

        return sources;
    }

    /// <summary>Returns how many colon-separated tokens the body reference starting at <paramref name="start"/>
    /// spans.</summary>
    /// <param name="tokens">The colon-separated operand tokens.</param>
    /// <param name="start">Where the body reference starts.</param>
    /// <returns>The token count.</returns>
    public static int BodyRefTokenWidth(string[] tokens, int start) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        if (start >= tokens.Length) {
            return 2;
        }
        if (string.Equals(
            a: tokens[start],
            b: "cell",
            comparisonType: StringComparison.Ordinal
        )) {
            return 3;
        }

        return ((RuleBindingTokens.OfReferenceToken(token: tokens[start]) != BoundKey.None)
            ? 1
            : 2
        );
    }
    /// <summary>Returns a declared channel's ordinal, or <c>-1</c>.</summary>
    /// <param name="name">The channel name.</param>
    /// <returns>The ordinal, or <c>-1</c>.</returns>
    public int ChannelOrdinal(string name) {
        var channels = Definition.Channels;

        for (var ordinal = 0; (ordinal < channels.Count); ordinal++) {
            if (
                (channels[ordinal] is { } channel) &&
                string.Equals(
                a: channel.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return ordinal;
            }
        }

        return -1;
    }
    /// <inheritdoc/>
    public override Draw? FindDraw(StateRow row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        return (row.Draw ?? (((row is WorldStateRow world) && (WorldLatticeFill.FindDraw(trait: world.Field) is { } fill))
            ? new Draw(
                Generator: fill.Generator,
                Source: fill.Source,
                Timing: DrawTiming.Event
            )
            : null));
    }
    /// <inheritdoc/>
    public override CompiledTopology? FindTopology(string name) => WorldTopologyCompilation.Find(
        definition: Definition,
        name: name
    );
    /// <summary>Returns the placement ordinal per cell of the enclosing rule's <c>forEach</c> row, in cell order,
    /// computed once per compile.</summary>
    /// <param name="channel">The authored channel, for refusal text.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <returns>The ordinals, in cell order.</returns>
    public IReadOnlyList<int> ForEachPlacementOrdinals(string channel, string ruleName) {
        if (Scope.TryGetValue(
            key: PlacementOrdinalsScope,
            value: out var cached
        )) {
            return ((IReadOnlyList<int>)cached);
        }
        if (ForEachRow is not { } forEachRow) {
            throw new RuleException(
                detail: $"'{channel}' names 'placement:$each', which requires the enclosing rule to declare 'forEach'",
                refusal: WorldRuleRefusal.SpatialChannelMalformed,
                ruleName: ruleName
            );
        }

        var row = (FindRow(name: forEachRow)
            ?? throw new RuleException(
            detail: $"'{channel}' names 'placement:$each', but forEach row '{forEachRow}' names no declared state row",
            refusal: WorldRuleRefusal.SpatialChannelMalformed,
            ruleName: ruleName
        ));
        var cells = (row.Cells ?? []);
        var ordinals = new int[cells.Count];

        for (var index = 0; (index < cells.Count); index++) {
            ordinals[index] = PlacementOrdinal(
                channel: channel,
                placementId: cells[index].Key.Value,
                ruleName: ruleName
            );
        }

        Scope[PlacementOrdinalsScope] = ordinals;

        return ordinals;
    }
    /// <summary>Gets a value indicating whether a placement carries a region facet.</summary>
    /// <param name="placementId">The placement's id.</param>
    /// <returns><see langword="true"/> when the placement declares a region.</returns>
    public bool HasRegion(string placementId) {
        foreach (var placement in Definition.Placements) {
            if (
                (placement.Region is not null) &&
                string.Equals(
                a: placement.Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Gets a value indicating whether the document declares a screen at an index.</summary>
    /// <param name="index">The screen index.</param>
    /// <returns><see langword="true"/> when the document declares the screen.</returns>
    public bool HasScreen(int index) {
        foreach (var screen in Definition.Screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the ordinal of a declared placement carrying a single-body inhabit facet.</summary>
    /// <param name="channel">The authored channel, for refusal text.</param>
    /// <param name="placementId">The placement's id.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <returns>The placement ordinal.</returns>
    public int PlacementOrdinal(string channel, string placementId, string ruleName) {
        var placements = Definition.Placements;

        for (var ordinal = 0; (ordinal < placements.Count); ordinal++) {
            if (!string.Equals(
                a: placements[ordinal].Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }
            if (
                (placements[ordinal].Inhabit is not { } inhabit) ||
                (inhabit.ResolvedCount is not { Row: null, Literal: 1 })
            ) {
                throw new RuleException(
                    detail: $"'{channel}' names 'placement:{placementId}', which does not carry an inhabit facet with count 1",
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName
                );
            }

            return ordinal;
        }

        throw new RuleException(
            detail: $"'{channel}' names 'placement:{placementId}', which is not a declared placement",
            refusal: WorldRuleRefusal.SpatialChannelMalformed,
            ruleName: ruleName
        );
    }
    /// <summary>Resolves an effect's body key: a dynamic key indirection, or a literal index inside capacity.</summary>
    /// <param name="key">The authored key.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <returns>The literal index (or <c>-1</c>), the interned key, and the live indirection.</returns>
    public (int Index, CellKey Key, CompiledCellRef? KeyFrom) ResolveBodyAddress(string key, string verb, string ruleName) {
        if (RuleCompiler.TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: this,
            key: key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: verb
        )) {
            return (-1, default, dynamicKey);
        }
        if (
            !int.TryParse(
            s: key,
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var index
        ) ||
            (index < 0) ||
            (index >= Definition.Population.Capacity)
        ) {
            throw new RuleException(
                detail: $"'{verb}' key '{key}' does not name a body inside capacity {Definition.Population.Capacity}",
                refusal: WorldRuleRefusal.BodyIndexUnknown,
                ruleName: ruleName
            );
        }

        return (index, RuleCompiler.InternKey(
            context: this,
            name: key
        ), null);
    }
    /// <summary>Compiles the body reference starting at a token index, in the world reader's own form.</summary>
    /// <param name="tokens">The channel's tokens.</param>
    /// <param name="start">Where the reference starts.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="channel">The authored channel, for refusal text.</param>
    /// <returns>The compiled reference.</returns>
    public CompiledBodyRef ResolveBodyRef(string[] tokens, int start, string ruleName, string channel) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var kind = tokens[start];

        if (
            (RuleBindingTokens.OfReferenceToken(token: kind) is var bound) &&
            (bound != BoundKey.None)
        ) {
            if (bound is BoundKey.Token or BoundKey.Previous) {
                throw new RuleException(
                    detail: $"'{channel}' names '{kind}', which binds a cell key inside a pattern value expression and never a body",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            RuleCompiler.RequireBindingInScope(
                binding: bound,
                context: this,
                ruleName: ruleName,
                spelled: kind,
                where: $"'{channel}'"
            );

            return new CompiledBodyRef(
                Index: ((int)bound),
                Kind: CompiledBodyRefKind.Binding,
                Row: null
            );
        }
        if ((start + 1) >= tokens.Length) {
            throw new RuleException(
                detail: $"'{channel}' names '{kind}' with no value — a body reference is {WorldFactsCompileContext.BodyRefVocabulary}",
                refusal: WorldRuleRefusal.SpatialChannelMalformed,
                ruleName: ruleName
            );
        }

        var value = tokens[(start + 1)];

        switch (kind) {
            case "cell": {
                    if ((start + 2) >= tokens.Length) {
                        throw new RuleException(
                            detail: $"'{channel}' names 'cell:{value}' with no key — spell 'cell:<row>:<key>'",
                            refusal: WorldRuleRefusal.SpatialChannelMalformed,
                            ruleName: ruleName
                        );
                    }

                    var cell = RuleCompiler.ResolveCellRef(
                        channel: channel,
                        context: this,
                        key: tokens[(start + 2)],
                        row: value,
                        ruleName: ruleName
                    );

                    return new CompiledBodyRef(
                        Handle: Catalog.Descriptors[cell.RowOrdinal].Handle,
                        Index: -1,
                        Key: KeyName(key: cell.Key),
                        Kind: CompiledBodyRefKind.Cell,
                        Row: Catalog.Descriptors[cell.RowOrdinal].Name
                    );
                }
            case "body": {
                    if (
                        !int.TryParse(
                        s: value,
                        style: NumberStyles.Integer,
                        provider: CultureInfo.InvariantCulture,
                        result: out var index
                    ) ||
                        (index < 0)
                    ) {
                        throw new RuleException(
                            detail: $"'{channel}' names 'body:{value}', which is not a non-negative integer",
                            refusal: WorldRuleRefusal.SpatialChannelMalformed,
                            ruleName: ruleName
                        );
                    }
                    if (index >= Definition.Population.Capacity) {
                        throw new RuleException(
                            detail: $"'{channel}' names 'body:{index}', which is outside the document's declared entity-table capacity ({Definition.Population.Capacity})",
                            refusal: WorldRuleRefusal.BodyIndexUnknown,
                            ruleName: ruleName
                        );
                    }

                    return new CompiledBodyRef(
                        Index: index,
                        Kind: CompiledBodyRefKind.Literal,
                        Row: null
                    );
                }
            case "argmax":
            case "argmin": {
                    if (string.IsNullOrEmpty(value: value)) {
                        throw new RuleException(
                            detail: $"'{channel}' names '{kind}:' with no row",
                            refusal: WorldRuleRefusal.SpatialChannelMalformed,
                            ruleName: ruleName
                        );
                    }

                    _ = RuleCompiler.ResolveNumericRow(
                        channel: channel,
                        context: this,
                        malformed: WorldRuleRefusal.SpatialChannelMalformed,
                        name: value,
                        requireKeyed: true,
                        ruleName: ruleName
                    );

                    return new CompiledBodyRef(
                        Handle: Catalog.Descriptors[RuleCompiler.ResolveRowOrdinal(
                            context: this,
                            name: value
                        )].Handle,
                        Index: -1,
                        Kind: ((kind == "argmax")
                        ? CompiledBodyRefKind.ArgMax
                        : CompiledBodyRefKind.ArgMin),
                        Row: value
                    );
                }
            case "placement":
                if (string.Equals(
                    a: value,
                    b: "$each",
                    comparisonType: StringComparison.Ordinal
                )) {
                    return new CompiledBodyRef(
                        Index: -1,
                        Kind: CompiledBodyRefKind.Placement,
                        PlacementOrdinals: ForEachPlacementOrdinals(
                            channel: channel,
                            ruleName: ruleName
                        ),
                        Row: null
                    );
                }

                return new CompiledBodyRef(
                    Index: PlacementOrdinal(
                        channel: channel,
                        placementId: value,
                        ruleName: ruleName
                    ),
                    Kind: CompiledBodyRefKind.Placement,
                    Row: null
                );
            default:
                throw new RuleException(
                    detail: $"'{channel}' names body-reference token '{kind}:{value}' — expected 'body:<n>', 'argmax:<row>'/'argmin:<row>', or 'placement:<id>'/'placement:$each'",
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName
                );
        }
    }
    /// <summary>Returns the ordinal-addressed form of a compiled body reference.</summary>
    /// <param name="body">The reference.</param>
    /// <returns>The ordinal-addressed reference.</returns>
    public WorldBodyRef Ordinals(in CompiledBodyRef body) => new(
        Index: body.Index,
        Key: (((body.Key is { } key) && Catalog.Keys.TryResolve(
            key: out var resolved,
            name: CellName.Parse(candidate: key)
        ))
            ? resolved
            : default),
        Kind: body.Kind,
        PlacementOrdinals: body.PlacementOrdinals,
        RowOrdinal: (body.Handle.IsValid
            ? body.Handle.Ordinal
            : -1)
    );

    private string KeyName(CellKey key) => (Catalog.Keys.TryGetName(
        key: key,
        name: out var name
    )
        ? name.Value
        : string.Empty
    );
}
/// <summary>The families the world registers with the arena-addressed rule compiler: its reserved operand channels,
/// its effect and predicate arms, and the <c>$pair:</c> key.</summary>
public static partial class WorldFactsVocabulary {
    /// <summary>Gets the one registry every world compile against the arena-addressed compiler shares.</summary>
    public static RuleVocabulary Instance { get; } = new(
        effects: [
            BodyProgramArm<WorldEffect.SetVerticalVelocity>(discriminator: "setVerticalVelocity"),
            BodyProgramArm<WorldEffect.ScaleVerticalVelocity>(discriminator: "scaleVerticalVelocity"),
            BodyProgramArm<WorldEffect.PlanarImpulse>(discriminator: "planarImpulse"),
            BodyProgramArm<WorldEffect.StartTimer>(discriminator: "startTimer"),
            BodyProgramArm<WorldEffect.Designate>(discriminator: "designate"),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveCue(
                    context: context,
                    effect: ((WorldEffect.EmitCue)effect),
                    ruleName: ruleName
                ),
                discriminator: "emitCue",
                effectType: typeof(WorldEffect.EmitCue)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveBodyVerticalVelocity(
                    context: context,
                    key: ((WorldEffect.SetBodyVerticalVelocity)effect).Key,
                    operation: BodyMotionOp.SetVerticalVelocity,
                    ruleName: ruleName,
                    value: ((WorldEffect.SetBodyVerticalVelocity)effect).Velocity,
                    verb: "setBodyVerticalVelocity"
                ),
                discriminator: "setBodyVerticalVelocity",
                effectType: typeof(WorldEffect.SetBodyVerticalVelocity)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveBodyVerticalVelocity(
                    context: context,
                    key: ((WorldEffect.ScaleBodyVerticalVelocity)effect).Key,
                    operation: BodyMotionOp.ScaleVerticalVelocity,
                    ruleName: ruleName,
                    value: ((WorldEffect.ScaleBodyVerticalVelocity)effect).Factor,
                    verb: "scaleBodyVerticalVelocity"
                ),
                discriminator: "scaleBodyVerticalVelocity",
                effectType: typeof(WorldEffect.ScaleBodyVerticalVelocity)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveBodyImpulse(
                    context: context,
                    effect: ((WorldEffect.ApplyBodyImpulse)effect),
                    ruleName: ruleName
                ),
                discriminator: "applyBodyImpulse",
                effectType: typeof(WorldEffect.ApplyBodyImpulse)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveRigidImpulse(
                    context: context,
                    effect: ((WorldEffect.ApplyRigidImpulse)effect),
                    ruleName: ruleName
                ),
                discriminator: "applyRigidImpulse",
                effectType: typeof(WorldEffect.ApplyRigidImpulse)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveBodyDesignation(
                    context: context,
                    effect: ((WorldEffect.DesignateBody)effect),
                    ruleName: ruleName
                ),
                discriminator: "designateBody",
                effectType: typeof(WorldEffect.DesignateBody)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveFieldPaint(
                    context: context,
                    effect: ((WorldEffect.PaintField)effect),
                    ruleName: ruleName
                ),
                discriminator: "paintField",
                effectType: typeof(WorldEffect.PaintField)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveUpsertHudPanel(
                    effect: ((WorldEffect.UpsertHudPanel)effect),
                    ruleName: ruleName
                ),
                discriminator: "upsertHudPanel",
                effectType: typeof(WorldEffect.UpsertHudPanel)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveRemoveHudPanel(
                    effect: ((WorldEffect.RemoveHudPanel)effect),
                    ruleName: ruleName
                ),
                discriminator: "removeHudPanel",
                effectType: typeof(WorldEffect.RemoveHudPanel)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveUpsertPlacement(
                    context: context,
                    effect: ((WorldEffect.UpsertPlacement)effect),
                    ruleName: ruleName
                ),
                discriminator: "upsertPlacement",
                effectType: typeof(WorldEffect.UpsertPlacement)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveRemovePlacement(
                    context: context,
                    effect: ((WorldEffect.RemovePlacement)effect),
                    ruleName: ruleName
                ),
                discriminator: "removePlacement",
                effectType: typeof(WorldEffect.RemovePlacement)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => new WorldDocumentEffect(
                    cost: 1L,
                    describe: "save",
                    id: string.Empty,
                    panel: null,
                    placement: null,
                    write: WorldDocumentWrite.Save
                ),
                discriminator: "save",
                effectType: typeof(WorldEffect.Save)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolvePose(
                    context: context,
                    effect: ((WorldEffect.Pose)effect),
                    ruleName: ruleName
                ),
                discriminator: "pose",
                effectType: typeof(WorldEffect.Pose)
            ),
            new WorldFactsEffectArm(
                compile: static (effect, ruleName, context) => WorldFactsCompiler.ResolveIdentityFact(
                    context: context,
                    effect: ((WorldEffect.SetIdentityFact)effect),
                    ruleName: ruleName
                ),
                discriminator: "setIdentityFact",
                effectType: typeof(WorldEffect.SetIdentityFact)
            ),
        ],
        keys: [new WorldFactsPairKeyFamily()],
        operands: [new WorldFactsOperandFamily()],
        predicates: [
            new WorldFactsPredicateArm(
                discriminator: "now",
                predicateType: typeof(WorldPredicate.Now)
            ),
            new WorldFactsPredicateArm(
                discriminator: "recently",
                predicateType: typeof(WorldPredicate.Recently)
            ),
            new WorldFactsPredicateArm(
                discriminator: "timerElapsed",
                predicateType: typeof(WorldPredicate.TimerElapsed)
            ),
            new WorldFactsPredicateArm(
                discriminator: "held",
                predicateType: typeof(WorldPredicate.Held)
            ),
        ]
    );

    private static WorldFactsEffectArm BodyProgramArm<TEffect>(string discriminator) where TEffect : ActionEffect => new(
        compile: (effect, ruleName, context) => throw new RuleException(
            detail: $"'{discriminator}' has no world-scope meaning — it belongs to a kit's action programs",
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: ruleName
        ),
        discriminator: discriminator,
        effectType: typeof(TEffect)
    );

    private sealed class WorldFactsEffectArm : EffectFamily {
        private readonly Func<ActionEffect, string, WorldFactsCompileContext, IRuleEffect> m_compile;

        public WorldFactsEffectArm(Type effectType, string discriminator, Func<ActionEffect, string, WorldFactsCompileContext, IRuleEffect> compile) {
            Discriminator = discriminator;
            EffectType = effectType;
            m_compile = compile;
        }

        public override string Discriminator { get; }
        public override Type EffectType { get; }

        public override IRuleEffect Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => m_compile(
            arg1: effect,
            arg2: ruleName,
            arg3: ((WorldFactsCompileContext)context)
        );
    }
    private sealed class WorldFactsPredicateArm : PredicateFamily {
        public WorldFactsPredicateArm(Type predicateType, string discriminator) {
            Discriminator = discriminator;
            PredicateType = predicateType;
        }

        public override string Discriminator { get; }
        public override Type PredicateType { get; }

        public override GateToken Compile(ActionPredicate predicate, string ruleName, RuleCompileContext context) => throw new RuleException(
            detail: $"'{Discriminator}' has no world-scope meaning — world gates admit 'compareState', 'compareValue', 'all', 'any', and 'not'",
            refusal: RuleRefusal.PredicateKindInadmissible,
            ruleName: ruleName
        );
    }
    private sealed class WorldFactsPairKeyFamily : KeyFamily {
        public override bool TryCompile(string key, string ruleName, string verb, string keyFieldLabel, RuleCompileContext context, out CompiledCellRef cell) {
            ArgumentNullException.ThrowIfNull(argument: key);

            if (!key.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.PairKeyPrefix
            )) {
                cell = default;

                return false;
            }

            var world = ((WorldFactsCompileContext)context);
            var tokens = key[WorldRuleFacts.PairKeyPrefix.Length..].Split(separator: ':');
            var widthA = WorldFactsCompileContext.BodyRefTokenWidth(
                start: 0,
                tokens: tokens
            );

            if (tokens.Length != (widthA + WorldFactsCompileContext.BodyRefTokenWidth(
                start: widthA,
                tokens: tokens
            ))) {
                throw new RuleException(
                    detail: $"'{verb}' {keyFieldLabel} '{key}' does not spell '{WorldRuleFacts.PairKeyPrefix}<bodyRefA>:<bodyRefB>' (each {WorldFactsCompileContext.BodyRefVocabulary})",
                    refusal: WorldRuleRefusal.PairKeyMalformed,
                    ruleName: ruleName
                );
            }

            var channel = $"{verb} {keyFieldLabel} '{key}'";

            cell = new CompiledCellRef(
                Custom: new WorldPairKeyFact(key: new PairKeyFact(
                    bodyA: world.ResolveBodyRef(
                        channel: channel,
                        ruleName: ruleName,
                        start: 0,
                        tokens: tokens
                    ),
                    bodyB: world.ResolveBodyRef(
                        channel: channel,
                        ruleName: ruleName,
                        start: widthA,
                        tokens: tokens
                    )
                )),
                Key: default,
                RowOrdinal: -1
            );

            return true;
        }
    }
}
