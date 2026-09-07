using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>A world rule: a state-library <see cref="Rule"/> plus the optional choice policy only a world evaluates.
/// Compiled by <see cref="WorldRuleCompiler"/>, which composes the library's own compile pieces with the world's
/// registered vocabulary (<see cref="WorldRuleVocabulary"/>).</summary>
/// <param name="Name">The rule's unique, unreserved name.</param>
/// <param name="Effects">The effects applied in order when the rule fires; with a decision, the common entry effects,
/// which may be empty.</param>
/// <param name="Gate">The predicate that must hold, or <see langword="null"/> for always.</param>
/// <param name="Mode">Level or edge; a decision requires level.</param>
/// <param name="ForEach">A keyed state row to iterate with <c>$each</c> bound to each key, or <see langword="null"/>.</param>
/// <param name="Decision">The optional choice policy; common effects run only when entering a selected option.</param>
/// <param name="Bindings">The values bound once per evaluation, in declared order, read as <c>$bind:&lt;name&gt;</c>.</param>
/// <param name="Zones">The rule's zone table (<see cref="Rule.Zones"/>), which every <c>$zones[&lt;index&gt;]</c> in
/// the rule selects from, or <see langword="null"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRule(
    CellName Name,
    IReadOnlyList<ActionEffect> Effects,
    ActionPredicate? Gate = null,
    ActionTriggerMode Mode = ActionTriggerMode.Level,
    string? ForEach = null,
    [property: JsonPropertyOrder(5)][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDecision? Decision = null,
    IReadOnlyList<RuleBinding>? Bindings = null,
    IReadOnlyList<string>? Zones = null
) : Rule(Name: Name, Effects: Effects, Gate: Gate, Mode: Mode, ForEach: ForEach, Bindings: Bindings, Zones: Zones);

/// <summary>How a body reference token resolved at compile time.</summary>
public enum CompiledBodyRefKind : byte {
    /// <summary>A literal <c>body:&lt;n&gt;</c> index.</summary>
    Literal,
    /// <summary>The body whose keyed-row cell is the maximum.</summary>
    ArgMax,
    /// <summary>The body whose keyed-row cell is the minimum.</summary>
    ArgMin,
    /// <summary>A body index read live from a state cell.</summary>
    Cell,
    /// <summary>A bound <c>each</c>/<c>left</c>/<c>right</c> participant.</summary>
    Binding,
    /// <summary>The body inhabiting a declared placement, or the placement the enclosing <c>forEach</c> key names.</summary>
    Placement,
}

/// <summary>One compiled body reference.</summary>
/// <param name="Kind">How the reference resolves.</param>
/// <param name="Index">The literal index, the <see cref="BoundKey"/> for a binding, or the placement ordinal.</param>
/// <param name="Row">The keyed row for an argmax/argmin or cell read.</param>
/// <param name="Key">The cell key for a cell read.</param>
/// <param name="Handle">The compiled handle of <paramref name="Row"/>.</param>
/// <param name="PlacementOrdinals">For <c>placement:$each</c>, the placement ordinal per <c>forEach</c> cell, in cell order.</param>
public readonly record struct CompiledBodyRef(CompiledBodyRefKind Kind, int Index, string? Row, string? Key = null, StateHandle Handle = default, IReadOnlyList<int>? PlacementOrdinals = null);

/// <summary>The co-occurrence an interaction evaluates.</summary>
/// <param name="Left">The left property.</param>
/// <param name="Right">The right property, or the region placement.</param>
/// <param name="CoOccurrence">Distance or region.</param>
/// <param name="Range">The distance range.</param>
/// <param name="Neighbours">At most this many right carriers per left carrier for a distance interaction, the nearest first; 0 for every carrier in range.</param>
public readonly record struct CompiledInteraction(string Left, string Right, WorldInteractionCoOccurrence CoOccurrence, FixedQ4816 Range, int Neighbours = 0);

/// <summary>A compiled body motion effect.</summary>
public readonly record struct CompiledWorldBodyEffect(
    BodyMotionOp Operation,
    FixedQ4816 Value,
    FixedVector3 Direction,
    ulong DurationTicks,
    string? Register = null,
    string? TargetKey = null,
    CompiledCellRef? TargetKeyFrom = null,
    WorldBodyDesignationKind Designation = WorldBodyDesignationKind.Body
);
/// <summary>A compiled lattice field paint.</summary>
public readonly record struct CompiledWorldFieldPaint(string Field, int X, int Y, int Z, FixedQ4816 Value, WorldFieldWriteOp Operation, int Radius);
/// <summary>A compiled literal pose.</summary>
public readonly record struct CompiledWorldPose(FixedVector3 Position, FixedQ4816 YawRadians, FixedQ4816 PitchRadians, FixedQ4816 RollRadians);

/// <summary>A compiled world rule or interaction: the library's <see cref="CompiledRule"/> plus the co-occurrence an
/// interaction evaluates and the choice policy a decision rule carries.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">Level or edge.</param>
/// <param name="Gate">The flattened postfix Boolean program; empty means always.</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
/// <param name="ForEach">The keyed row a rule iterates, or <see langword="null"/>.</param>
/// <param name="Interaction">The co-occurrence an interaction evaluates, or <see langword="null"/> for a rule.</param>
/// <param name="Decision">The compiled choice policy, or <see langword="null"/>.</param>
/// <param name="Bindings">The compiled per-evaluation bindings, in declared order.</param>
/// <param name="Zones">The compiled zone table, or <see langword="null"/>.</param>
public sealed record CompiledWorldRule(
    string Name,
    ActionTriggerMode Mode,
    GateToken[] Gate,
    EffectFact[] Effects,
    string? ForEach = null,
    CompiledInteraction? Interaction = null,
    CompiledWorldDecision? Decision = null,
    CompiledRuleBinding[]? Bindings = null,
    ZoneTable? Zones = null
) : CompiledRule(Name: Name, Mode: Mode, Gate: Gate, Effects: Effects, ForEach: ForEach, Bindings: Bindings, Zones: Zones) {
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        base.CollectReads(into: into);
        if (Decision is { } decision) {
            RuleDataflow.CollectGate(gate: (decision.Interrupt ?? []), into: into);
            RuleDataflow.CollectEffectReads(effects: decision.OnNoChoice, into: into);
            foreach (var option in decision.Options) {
                RuleDataflow.CollectGate(gate: option.Gate, into: into);
                RuleDataflow.CollectExpression(tokens: option.Score, into: into);
                RuleDataflow.CollectEffectReads(effects: option.Effects, into: into);
            }
        }
    }

    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) {
        base.CollectWrites(into: into);
        if (Decision is { } decision) {
            RuleDataflow.CollectEffectWrites(effects: decision.OnNoChoice, into: into);
            foreach (var option in decision.Options) {
                RuleDataflow.CollectEffectWrites(effects: option.Effects, into: into);
            }
        }
    }

    /// <summary>Adds the decision's cost: every option's gate, the costliest branch, the score of every retained
    /// candidate, and the perception sampling a neighbours option inspects.</summary>
    /// <inheritdoc/>
    public override RuleCost CostBreakdown(RuleCompileContext context) {
        var baseBreakdown = base.CostBreakdown(context: context);

        if (Decision is not { } decision) {
            return baseBreakdown;
        }

        var check = baseBreakdown.Check;
        var currentGate = 0L;
        var branch = RuleWorkBudget.EffectsCost(effects: decision.OnNoChoice, context: context);

        foreach (var option in decision.Options) {
            var gate = RuleWorkBudget.GateCost(tokens: option.Gate, context: context);
            var score = RuleWorkBudget.ExpressionCost(tokens: option.Score, context: context);

            currentGate = Math.Max(val1: currentGate, val2: gate);
            if (option.Neighbors is { } neighbors) {
                // Physical sampling and eligibility inspect at most the candidate budget; only retained candidates score.
                check = RuleWorkBudget.SaturatingAdd(left: check, right: RuleWorkBudget.SaturatingMultiply(left: neighbors.Source.CandidateBudget, right: RuleWorkBudget.SaturatingAdd(left: 1L, right: gate)));
                check = RuleWorkBudget.SaturatingAdd(left: check, right: RuleWorkBudget.SaturatingMultiply(left: neighbors.Source.MaxCandidates, right: RuleWorkBudget.SaturatingAdd(left: 1L, right: score)));
                check = RuleWorkBudget.SaturatingAdd(left: check, right: 27L); // Grid cell lookups, independent of crowd density.
                if (neighbors.Source.RequiresLineOfSight) {
                    check = RuleWorkBudget.SaturatingAdd(left: check, right: neighbors.Source.CandidateBudget);
                }
            } else {
                check = RuleWorkBudget.SaturatingAdd(left: check, right: RuleWorkBudget.SaturatingAdd(left: RuleWorkBudget.SaturatingAdd(left: 1L, right: gate), right: score));
            }
            branch = Math.Max(val1: branch, val2: RuleWorkBudget.EffectsCost(effects: option.Effects, context: context));
        }

        check = RuleWorkBudget.SaturatingAdd(left: check, right: currentGate);
        check = RuleWorkBudget.SaturatingAdd(left: check, right: RuleWorkBudget.GateCost(tokens: (decision.Interrupt ?? []), context: context));

        var effects = RuleWorkBudget.SaturatingAdd(left: baseBreakdown.Effects, right: branch);

        return new RuleCost(Setup: baseBreakdown.Setup, Check: check, Effects: effects);
    }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => CostBreakdown(context: context).Total;
}

/// <summary>Hard bounds for the world's own rule arms; representation and per-tick work limits, never gameplay tuning.
/// The library's bounds are <see cref="RuleCapacity"/>.</summary>
public static class WorldRuleCapacity {
    /// <summary>The maximum options considered by one decision.</summary>
    public const int MaxDecisionOptions = 32;
    /// <summary>The maximum individuals retained by one parameterized decision option.</summary>
    public const int MaxDecisionCandidates = 32;
    /// <summary>The largest cube radius a field paint covers.</summary>
    public const int MaxFieldPaintRadius = 8;
    /// <summary>The longest cue payload, in UTF-16 code units.</summary>
    public const int MaxCuePayloadLength = 256;
    /// <summary>The longest cue name.</summary>
    public const int MaxCueNameLength = 64;
}

/// <summary>The compile-time refusals only a world's own arms raise; the library's are <see cref="RuleRefusal"/>.
/// Both travel in a <see cref="RuleException"/>.</summary>
public enum WorldRuleRefusal : byte {
    [Refusal(door: "world.rule.compile", condition: "a 'body:<n>' reference names an index outside the document's declared entity-table capacity", kind: RefusalKind.Verdict)]
    BodyIndexUnknown,
    [Refusal(door: "world.rule.compile", condition: "a 'setIdentityFact' effect or a '$identity:' channel names a fact that is not a cell name, or the channel does not spell '$identity:<bodyRef>:<fact>'", kind: RefusalKind.Verdict)]
    IdentityFactMalformed,
    [Refusal(door: "world.rule.compile", condition: "a 'setIdentityFact' effect or a '$identity:' channel is authored in a document declaring no 'identity' lane row in state.world", kind: RefusalKind.Verdict)]
    IdentityLaneUndeclared,
    [Refusal(door: "world.rule.compile", condition: "a '$channel:' channel does not spell '$channel:<seat>:<channelName>' with seat in 1..population.localSeats and channelName naming a declared 'channels[]' row", kind: RefusalKind.Verdict)]
    ChannelMalformed,
    [Refusal(door: "world.rule.compile", condition: "an 'upsertHudPanel'/'removeHudPanel' effect carries no panel id", kind: RefusalKind.Verdict)]
    HudPanelInvalid,
    [Refusal(door: "world.rule.compile", condition: "a '$link:' channel does not name exactly one declared 'adjacencies' row", kind: RefusalKind.Verdict)]
    LinkChannelMalformed,
    [Refusal(door: "world.rule.compile", condition: "a '$machine:' channel does not spell '$machine:<screen>:<address>' with non-negative integers", kind: RefusalKind.Verdict)]
    MachineChannelMalformed,
    [Refusal(door: "world.rule.compile", condition: "a '$pair:' key does not spell exactly two body-reference tokens ('body:<n>', 'argmax:<row>'/'argmin:<row>', 'cell:<row>:<key>', or a bound each/left/right) each", kind: RefusalKind.Verdict)]
    PairKeyMalformed,
    [Refusal(door: "world.rule.compile", condition: "a '$parked:' channel does not spell exactly one body-reference token ('body:<n>' or 'argmax:<row>'/'argmin:<row>')", kind: RefusalKind.Verdict)]
    ParkedChannelMalformed,
    [Refusal(door: "world.rule.compile", condition: "an 'upsertPlacement'/'removePlacement' effect carries no placement id", kind: RefusalKind.Verdict)]
    PlacementInvalid,
    [Refusal(door: "world.rule.compile", condition: "a 'pose' effect authors both or neither of 'spawnPoint' and 'position'", kind: RefusalKind.Verdict)]
    PoseAmbiguous,
    [Refusal(door: "world.interaction.compile", condition: "an interaction's 'left'/'right' property reference names a value the declared 'properties' registry does not carry", kind: RefusalKind.Verdict)]
    PropertyUnknown,
    [Refusal(door: "world.rule.compile", condition: "a '$region:<placementId>' channel names no placement carrying a region facet", kind: RefusalKind.Verdict)]
    RegionUnknown,
    [Refusal(door: "world.rule.compile", condition: "a '$machine:' channel names a screen index the document does not declare", kind: RefusalKind.Verdict)]
    ScreenUnknown,
    [Refusal(door: "world.rule.compile", condition: "a '$distance:'/'$los:' channel does not spell exactly two body-reference tokens ('body:<n>' or 'argmax:<row>'/'argmin:<row>') each", kind: RefusalKind.Verdict)]
    SpatialChannelMalformed,
    [Refusal(door: "world.rule.compile", condition: "a 'pose' effect names a 'spawnPoint' the document's 'spawnPoints' section does not declare", kind: RefusalKind.Verdict)]
    SpawnPointUnknown,
}

/// <summary>The runtime refusals only a world-owned effect arm can draw; the state-neutral ones are
/// <see cref="RuleEffectRefusal"/>'s, and both share the evaluator's ledger.</summary>
public enum WorldRuleEffectRefusal : byte {
    [Refusal(door: "world.rule.effect", condition: "a rule body/pose/cue effect resolves no active body", kind: RefusalKind.Verdict)]
    BodyInactive,

    [Refusal(door: "world.rule.effect", condition: "a rule designation resolves no valid active target body or target register", kind: RefusalKind.Verdict)]
    BodyTargetInvalid,

    [Refusal(door: "world.rule.effect", condition: "a rule field paint has no matching live lattice field", kind: RefusalKind.Verdict)]
    FieldUnavailable,

    [Refusal(door: "world.rule.effect", condition: "a rule save effect has no host persistence tap", kind: RefusalKind.Verdict)]
    SaveUnavailable,

    [Refusal(door: "world.rule.effect", condition: "a rule's 'removePlacement' effect targets a placement whose inhabited body a concrete drive grant currently possesses", kind: RefusalKind.Verdict)]
    CarrierPossessed,

    [Refusal(door: "world.rule.effect", condition: "a 'setIdentityFact' effect addresses a body driving under no owned identity, so there is no document to carry the fact", kind: RefusalKind.Verdict)]
    IdentityUnbound,

    [Refusal(door: "world.rule.effect", condition: "a 'setIdentityFact' effect finds no 'identity' lane row in the installed document, or the lane or the identity's own facts row refuses the write", kind: RefusalKind.Verdict)]
    IdentityFactUnwritable,

    [Refusal(door: "world.rule.effect", condition: "an 'applyRigidImpulse' effect's struck body carries no 'rigid' kit facet", kind: RefusalKind.Verdict)]
    RigidBodyRequired,

    [Refusal(door: "world.rule.effect", condition: "an 'applyRigidImpulse' effect's magnitude cell is absent, or the resulting impulse is not representable or exceeds the world's declared rigid speed ceiling", kind: RefusalKind.Verdict)]
    RigidImpulseOutOfRange,
}
