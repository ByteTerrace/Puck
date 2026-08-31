using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>The document-shape caps for the <c>curves</c> section — capacity ceilings
/// <see cref="WorldDefinitionValidator"/> enforces beyond what <see cref="CurvatureSpline.Compile"/> itself refuses.
/// The curvature/coordinate/chord/speed bounds live on <see cref="CurvatureSpline"/> itself (the ONE source both the
/// document validator and the compiled primitive read); this class holds only the row/section sizing this document
/// layer owns.</summary>
public static class WorldCurves {
    /// <summary>The greatest number of knots a single curve row may declare.</summary>
    public const int MaxKnots = 64;
    /// <summary>The greatest number of curve rows the section may declare.</summary>
    public const int MaxRows = 64;
    /// <summary>The greatest |rate| a <c>Puck.Physics.Motion.BodyTargetSource.Curve</c> target may declare, in the
    /// curve's own arc units per second.</summary>
    public const float MaxFollowRate = 128f;
}
/// <summary>The compiled <c>curves</c> row name→ordinal table a <c>Puck.Physics.Motion.BodyTargetSource.Curve</c>
/// target resolves against — the <see cref="WorldTargetRegisterTable"/> shape, sharing no ordinal space with it (a
/// curve reference is a document-row lookup, never a Drive-reach channel).</summary>
public sealed class WorldCurveTable {
    private readonly OrdinalTable m_table;

    private WorldCurveTable(OrdinalTable table) {
        m_table = table;
    }

    /// <summary>Gets the empty table.</summary>
    public static WorldCurveTable Empty { get; } = new(table: OrdinalTable.Empty);

    /// <summary>Compiles the section's row names in declaration order.</summary>
    public static WorldCurveTable Compile(IReadOnlyList<WorldCurveRow> curves) => new(
        table: OrdinalTable.Build(
            names: curves.Select(selector: static row => row.Name).ToArray(),
            comparer: StringComparer.Ordinal
        )
    );
    /// <summary>Resolves a declared curve row name to its compact index.</summary>
    public bool TryGetIndex(string name, out int index) => m_table.TryGetOrdinal(
        name: name,
        ordinal: out index
    );
}

/// <summary>
/// One authored knot of a named <c>curves</c> row: a planar position with elevation, a tangent direction, and the
/// signed curvature the compiled spline reaches there — see <see cref="Puck.Maths.CurvatureSplineKnot"/> for the
/// compiled fixed-point form and the exact <c>cross2</c> convention.
/// </summary>
/// <param name="Position">The knot's world position — X/Z are the planar curvature-solve inputs, Y is the elevation
/// lift (outside the planar curvature and arc-length solve; carried through as a linear grade over each segment's
/// arc length — see <see cref="Puck.Maths.CurvatureSpline"/>'s remarks). A <see cref="DocumentVector3"/>, the SAME
/// spelling every other authored position in this document uses — no bespoke 2-vector shape.</param>
/// <param name="TangentYaw">The tangent direction, in radians, reduced to the canonical interval
/// <c>[-π, π]</c> (a small validator slack absorbs float round-trip of a double π literal); the unit tangent is
/// <c>(cos, sin)</c> in the XZ plane — the SAME facing convention <see cref="FixedQ4816.SinCos"/> and the engine's
/// own facing path use, pinned once here: every consumer (the camera <c>path</c> op, the sim curve-follow target)
/// reads this convention rather than re-deriving one. An angle outside the canonical interval is refused rather
/// than normalized: <see cref="FixedQ4816.FromDouble(double)"/> saturates a finite value outside its representable
/// range instead of wrapping it, so silently accepting one would compile a direction unrelated to the authored
/// angle.</param>
/// <param name="Curvature">The signed planar curvature at this knot, under the <c>cross2(a, b) = a.X·b.Z − a.Z·b.X</c>
/// convention: positive curvature turns from the tangent toward +Z faster than toward −Z. Must fall within
/// ±<see cref="CurvatureSpline.MaxCurvature"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldCurveKnot(
    DocumentVector3 Position,
    float TangentYaw,
    float Curvature
);

/// <summary>
/// One named <c>curves</c> row: an ordered list of curvature-declaring knots that <see cref="Compiled"/> derives
/// into a curvature-continuous cubic-Bézier spline (Steven Wittens' construction — see
/// <see cref="Puck.Maths.CurvatureSpline"/>). The author declares intent (positions, tangent directions, endpoint
/// curvatures); the engine derives the machinery (tangent lengths) at compile time, exactly — no control-point
/// document shape ever ships. The section is OPTIONAL and every reference to a row is nullable, so an unauthored
/// world is unchanged.
/// </summary>
/// <param name="Name">The row's stable name, unique within the section — the spelling every consumer's own
/// <c>curve</c> reference resolves against.</param>
/// <param name="Knots">The authored knots, in curve order. An open curve needs at least two; a closed curve at
/// least three; at most <see cref="WorldCurves.MaxKnots"/>.</param>
/// <param name="Closed">Whether the last knot connects back to the first. Defaults to <see langword="false"/>
/// (open).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldCurveRow(
    string Name,
    IReadOnlyList<WorldCurveKnot> Knots,
    bool Closed = false
) {
    // Keyed on the row instance itself: an authored row is an immutable record and a live retune always installs a
    // fresh instance (never a mutation in place), so the cache can never serve a stale compile and needs no
    // invalidation of its own — the WorldDynamicsRow.Compiled precedent, exactly. Kept off the record's own
    // equality-compared surface for the same reason that precedent gives: a lazily-populated field would make two
    // otherwise-identical rows compare unequal purely because one had been read from and the other had not.
    private static readonly ConditionalWeakTable<WorldCurveRow, StrongBox<CompiledCurvatureSpline>> CompiledCache = new();

    /// <summary>Gets this row's compiled, curvature-continuous spline — the SAME derivation
    /// <see cref="WorldDefinitionValidator"/> runs at the door, so a validated row always compiles here too. Derived
    /// once per row instance and cached.</summary>
    /// <exception cref="CurvatureSplineException">The row does not compile. Reachable only for an UNVALIDATED row (a
    /// hand-built candidate that skipped <see cref="WorldDefinitionValidator"/>) — the validator itself runs this
    /// same derivation and refuses by name before a validated row can be read here.</exception>
    [JsonIgnore]
    public CompiledCurvatureSpline Compiled => CompiledCache.GetValue(
        key: this,
        createValueCallback: static row => new StrongBox<CompiledCurvatureSpline>(value: CurvatureSpline.Compile(
            knots: row.ToSplineKnots(),
            closed: row.Closed
        ))
    ).Value!; // the callback always constructs a non-null StrongBox.Value; StrongBox<T>.Value is merely MaybeNull-annotated.

    private CurvatureSplineKnot[] ToSplineKnots() {
        var knots = Knots;
        var result = new CurvatureSplineKnot[knots.Count];

        for (var index = 0; (index < knots.Count); index++) {
            var knot = knots[index];
            var position = knot.Position.Value;

            result[index] = new CurvatureSplineKnot(
                Curvature: FixedQ4816.FromDouble(value: knot.Curvature),
                Elevation: FixedQ4816.FromDouble(value: position.Y),
                TangentYaw: FixedQ4816.FromDouble(value: knot.TangentYaw),
                X: FixedQ4816.FromDouble(value: position.X),
                Z: FixedQ4816.FromDouble(value: position.Z)
            );
        }

        return result;
    }
}
