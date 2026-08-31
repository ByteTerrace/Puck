using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>One kit's named arguments for an authored producer program.</summary>
/// <param name="Scalars">Fixed-point scalar arguments keyed by instruction-defined name.</param>
/// <param name="Channels">Authored channel arguments keyed by instruction-defined name.</param>
public sealed record BodyProgramParameters(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, string> Channels
);
/// <summary>One authored per-body target register and the envelope a designation into it must satisfy.</summary>
/// <param name="Name">The game-authored register name.</param>
/// <param name="MaximumRange">The greatest designation distance.</param>
/// <param name="MaximumHalfAngleDegrees">The widest accepted body-forward cone.</param>
/// <param name="RequiresLineOfSight">Whether solid world geometry must leave the segment unobstructed.</param>
/// <param name="RangeState">An optional durable counter slot supplying the player's requested range.</param>
/// <param name="HalfAngleState">An optional durable counter slot supplying the player's requested cone half-angle.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldTargetRegister(
    string Name,
    float MaximumRange,
    float MaximumHalfAngleDegrees,
    bool RequiresLineOfSight,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RangeState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HalfAngleState = null
);
/// <summary>The compiled target-register name and Drive-reach ordinal tables.</summary>
public sealed class WorldTargetRegisterTable {
    private readonly OrdinalTable m_table;

    private WorldTargetRegisterTable(OrdinalTable table, int reachBase) {
        m_table = table;
        ReachBase = reachBase;
    }

    /// <summary>Gets the number of authored registers.</summary>
    public int Count => m_table.Count;
    /// <summary>Gets an empty target-register table.</summary>
    public static WorldTargetRegisterTable Empty { get; } = new(
        table: OrdinalTable.Empty,
        reachBase: 0
    );
    /// <summary>Gets the first target-register bit in a Drive row's shared reach mask.</summary>
    public int ReachBase { get; }

    /// <summary>Compiles target registers after the world's channel ordinal range.</summary>
    public static WorldTargetRegisterTable Compile(IReadOnlyList<WorldTargetRegister> registers, int channelCount) => new(
        table: OrdinalTable.Build(
            names: registers.Select(selector: static register => register.Name).ToArray(),
            comparer: StringComparer.Ordinal
        ),
        reachBase: channelCount
    );
    /// <summary>Gets a register's authored name.</summary>
    public string Name(int index) => m_table.Name(ordinal: index);
    /// <summary>Gets the Drive-reach ordinal for a compact register index.</summary>
    public int ReachOrdinal(int index) => (ReachBase + index);
    /// <summary>Resolves a register name to its compact storage index.</summary>
    public bool TryGetIndex(string name, out int index) => m_table.TryGetOrdinal(
        name: name,
        ordinal: out index
    );
}
/// <summary>The fixed-point target source a producer executes.</summary>
/// <param name="Source">The authored source declaration.</param>
/// <param name="Range">The sensed cone range, or zero for a designated/curve source.</param>
/// <param name="MinimumDot">The cosine of the sensed cone half-angle, or zero for a designated/curve source.</param>
/// <param name="RegisterIndex">The designated register index, or <c>-1</c> for a sensed/curve source.</param>
/// <param name="CurveIndex">The curve row's compact index, or <c>-1</c> for a sensed/designated source.</param>
/// <param name="ArcStepRaw">The per-tick arc-length increment, Q32 raw — the exact rational
/// <see cref="BodyTargetSource.CurveFollow.Rate"/><c> / simulationRateHz</c> rounded once at compile time so the
/// runtime step is a single addition, never a division. Zero for a sensed/designated source.</param>
public readonly record struct FixedBodyTargetSource(BodyTargetSource Source, FixedQ4816 Range, FixedQ4816 MinimumDot, int RegisterIndex, int CurveIndex = -1, long ArcStepRaw = 0L) {
    /// <summary>Compiles one validated target declaration.</summary>
    /// <param name="source">The authored source declaration.</param>
    /// <param name="registers">The world's compiled target-register table.</param>
    /// <param name="curves">The world's compiled curves-row table.</param>
    /// <param name="simulationRateHz">The world's own simulation rate — the per-tick arc step's divisor. A
    /// <see cref="BodyTargetSource.CurveFollow"/> source at rate 0 (never validated through) compiles a zero step
    /// rather than dividing.</param>
    public static FixedBodyTargetSource Compile(BodyTargetSource source, WorldTargetRegisterTable registers, WorldCurveTable curves, int simulationRateHz) => source switch {
        BodyTargetSource.Sensed sensed => new FixedBodyTargetSource(
        Source: source,
        Range: FixedQ4816.FromDouble(value: sensed.Range),
        MinimumDot: FixedQ4816.FromDouble(value: Math.Cos(d: (sensed.HalfAngleDegrees * (Math.PI / 180.0)))),
        RegisterIndex: -1
    ),
        BodyTargetSource.Designated designated => new FixedBodyTargetSource(
        Source: source,
        Range: FixedQ4816.Zero,
        MinimumDot: FixedQ4816.Zero,
        RegisterIndex: (registers.TryGetIndex(
            name: designated.Register,
            index: out var index
        )
        ? index
        : -1)
    ),
        BodyTargetSource.CurveFollow curve => new FixedBodyTargetSource(
        Source: source,
        Range: FixedQ4816.Zero,
        MinimumDot: FixedQ4816.Zero,
        RegisterIndex: -1,
        CurveIndex: (curves.TryGetIndex(
            name: curve.Curve,
            index: out var curveIndex
        )
        ? curveIndex
        : -1),
        ArcStepRaw: CompileArcStepRaw(
            rate: curve.Rate,
            simulationRateHz: simulationRateHz
        )
    ),
        _ => throw new InvalidOperationException(message: $"Unknown body target source '{source.GetType().Name}'."),
    };
    // rate/simulationRateHz rounded once to Q32: rate parses to Q16 at the authoring boundary (the same one rounding
    // every authored float takes), then the division to Q32 is the ONE further rounding — never repeated per tick.
    // simulationRateHz <= 0 (an unvalidated caller) rounds to zero rather than dividing by zero.
    private static long CompileArcStepRaw(float rate, int simulationRateHz) {
        var rateRaw = FixedQ4816.FromDouble(value: rate).Value;

        return (FixedPointRounding.TryRoundRational(
            numerator: rateRaw,
            denominator: simulationRateHz,
            fractionBitCount: 16,
            result: out var arcStepRaw
        )
            ? arcStepRaw
            : 0L
        );
    }
}
/// <summary>The shared fixed-point body-forward cone predicate used by client proposals and authoritative senses.</summary>
public static class BodyTargetConeSense {
    /// <summary>Reports whether a candidate lies inside the supplied cone.</summary>
    public static bool Contains(in FixedVector3 origin, in FixedVector3 forward, in FixedVector3 candidate, FixedQ4816 range, FixedQ4816 minimumDot, out FixedQ4816 distanceSquared) {
        var delta = (candidate - origin);

        distanceSquared = delta.LengthSquared;

        if (
            (distanceSquared <= FixedQ4816.Zero) ||
            (distanceSquared > (range * range))
        ) {
            return false;
        }

        return (FixedVector3.Dot(
            left: forward.Normalize(),
            right: delta.Normalize()
        ) >= minimumDot);
    }
}
/// <summary>Selects whether authored target decisions require the deterministic solid-field query provider.</summary>
public static class WorldTargetSelection {
    private static bool EffectReferencesLineOfSight(ActionEffect effect) => effect switch {
        ActionEffect.SetState set => NamesLineOfSight(name: set.FromState),
        ActionEffect.AddState add => NamesLineOfSight(name: add.FromState),
        _ => false,
    };
    private static bool NamesLineOfSight(string? name) => ((name is not null) && name.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: WorldRuleFacts.LineOfSightPrefix
    ));
    private static bool PredicateReferencesLineOfSight(ActionPredicate? predicate) => predicate switch {
        ActionPredicate.CompareState compare => (NamesLineOfSight(name: compare.State) || NamesLineOfSight(name: compare.ComparandState)),
        ActionPredicate.All all => all.Predicates.Any(predicate: PredicateReferencesLineOfSight),
        _ => false,
    };
    // Scanned over the AUTHORED rule rows (mirroring the two checks above), never the compiled form — this decides
    // whether to build the field the compiler's own ReadWorldFact will later read from, so it must run before (and
    // independently of) rule compilation.
    private static bool RulesReferenceLineOfSight(IReadOnlyList<WorldRule>? rules) {
        if (rules is null) {
            return false;
        }

        foreach (var rule in rules) {
            if (
                (rule is not null) &&
                (PredicateReferencesLineOfSight(predicate: rule.Gate) || rule.Effects.Any(predicate: EffectReferencesLineOfSight))
            ) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether any designation envelope, sensed source, or world-rule <c>$los:</c> operand requires
    /// line of sight — the one gate <c>Server.WorldPopulation.CompileFixedTables</c> reads to decide whether to build
    /// the solid field at all. A world rule's <c>$los:</c> channel rides the same
    /// <c>Server.WorldPopulation.HasLineOfSight</c> primitive a sensed target's own check does, and that primitive
    /// reads a field the population would otherwise never build if nothing else in the document asked for one —
    /// admitting it here is what keeps a rules-only <c>$los:</c> authoring from silently reading "always false"
    /// forever.</summary>
    public static bool RequiresLineOfSight(WorldDefinition definition) =>
        (definition.TargetRegisters.Any(predicate: register => register.RequiresLineOfSight)
        || definition.BodyMotionPrograms.Any(predicate: program => (program.Target is BodyTargetSource.Sensed { RequiresLineOfSight: true }))
        || RulesReferenceLineOfSight(rules: definition.Rules));
}
