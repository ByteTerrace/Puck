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
    private readonly Dictionary<string, int> m_indexByName;
    private readonly string[] m_names;

    private WorldTargetRegisterTable(Dictionary<string, int> indexByName, string[] names, int reachBase) {
        m_indexByName = indexByName;
        m_names = names;
        ReachBase = reachBase;
    }

    /// <summary>Gets the number of authored registers.</summary>
    public int Count => m_names.Length;
    /// <summary>Gets an empty target-register table.</summary>
    public static WorldTargetRegisterTable Empty { get; } = new(
        indexByName: new Dictionary<string, int>(comparer: StringComparer.Ordinal),
        names: [],
        reachBase: 0
    );
    /// <summary>Gets the first target-register bit in a Drive row's shared reach mask.</summary>
    public int ReachBase { get; }

    /// <summary>Compiles target registers after the world's channel ordinal range.</summary>
    public static WorldTargetRegisterTable Compile(IReadOnlyList<WorldTargetRegister> registers, int channelCount) {
        var names = new string[registers.Count];
        var indexByName = new Dictionary<string, int>(
            capacity: registers.Count,
            comparer: StringComparer.Ordinal
        );

        for (var index = 0; (index < registers.Count); index++) {
            names[index] = registers[index].Name;
            indexByName.Add(
                key: registers[index].Name,
                value: index
            );
        }

        return new WorldTargetRegisterTable(
            indexByName: indexByName,
            names: names,
            reachBase: channelCount
        );
    }
    /// <summary>Gets a register's authored name.</summary>
    public string Name(int index) => m_names[index];
    /// <summary>Gets the Drive-reach ordinal for a compact register index.</summary>
    public int ReachOrdinal(int index) => (ReachBase + index);
    /// <summary>Resolves a register name to its compact storage index.</summary>
    public bool TryGetIndex(string name, out int index) => m_indexByName.TryGetValue(
        key: name,
        value: out index
    );
}
/// <summary>The fixed-point target source a producer executes.</summary>
/// <param name="Source">The authored source declaration.</param>
/// <param name="Range">The sensed cone range, or zero for a designated source.</param>
/// <param name="MinimumDot">The cosine of the sensed cone half-angle, or zero for a designated source.</param>
/// <param name="RegisterIndex">The designated register index, or <c>-1</c> for a sensed source.</param>
public readonly record struct FixedBodyTargetSource(BodyTargetSource Source, FixedQ4816 Range, FixedQ4816 MinimumDot, int RegisterIndex) {
    /// <summary>Compiles one validated target declaration.</summary>
    public static FixedBodyTargetSource Compile(BodyTargetSource source, WorldTargetRegisterTable registers) => source switch {
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
        _ => throw new InvalidOperationException(message: $"Unknown body target source '{source.GetType().Name}'."),
    };
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
