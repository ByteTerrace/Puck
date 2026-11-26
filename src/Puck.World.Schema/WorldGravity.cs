using System.Numerics;
using System.Text.Json.Serialization;

using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>One gravitational source the world declares, riding a placement's authored transform.</summary>
/// <param name="PlacementId">The <c>placements</c> row whose position the source sits at. The row need not be solid or
/// visible; only its transform is read.</param>
/// <param name="Mass">The source's non-negative gravitational mass.</param>
public sealed record WorldGravityAttractor([property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)] string PlacementId, float Mass);
/// <summary>A point-gravity preset riding a placement, authored in the quantities a world designer observes.</summary>
/// <param name="PlacementId">The <c>placements</c> row at the point source's centre.</param>
/// <param name="SurfaceGravity">The positive acceleration magnitude, in world units per second squared, promised at
/// <paramref name="ReferenceRadius"/> after the world's authored softening is applied.</param>
/// <param name="ReferenceRadius">The positive distance from the source centre at which <paramref name="SurfaceGravity"/>
/// is promised. For a planet this is its surface radius; a source need not carry geometry.</param>
public sealed record WorldGravityPoint(
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)] string PlacementId,
    float SurfaceGravity,
    float ReferenceRadius
);
/// <summary>How a matching local gravity area composes its acceleration with the answer accumulated so far.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGravityAreaMode>))]
public enum WorldGravityAreaMode : byte {
    /// <summary>Add the area's acceleration to the accumulated global and lower-priority answer.</summary>
    Combine,

    /// <summary>Replace the accumulated answer with the area's acceleration. A zero directional vector creates a
    /// deliberate zero-gravity pocket.</summary>
    Replace,
}
/// <summary>An analytic, placement-local bound for a gravity area.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldGravityAreaBounds.SphereBounds), typeDiscriminator: "sphere")]
[JsonDerivedType(typeof(WorldGravityAreaBounds.BoxBounds), typeDiscriminator: "box")]
public abstract record WorldGravityAreaBounds {
    private WorldGravityAreaBounds() { }

    /// <summary>A sphere centered on the riding placement.</summary>
    /// <param name="Radius">The positive placement-local radius, multiplied by the placement's scale.</param>
    public sealed record SphereBounds(float Radius) : WorldGravityAreaBounds;
    /// <summary>A yaw-oriented box centered on the riding placement.</summary>
    /// <param name="HalfExtents">The positive placement-local half extents, multiplied by the placement's scale.</param>
    public sealed record BoxBounds(DocumentVector3 HalfExtents) : WorldGravityAreaBounds;
}
/// <summary>The acceleration a matching local gravity area contributes.</summary>
/// <remarks>Every form but <see cref="Directional"/> takes a signed magnitude: positive pulls toward the form's
/// centre, axis, plane or surface, and negative pushes away from it, which is how the inside of a hollow shell or a
/// tube holds a body against its wall.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Directional), typeDiscriminator: "directional")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Radial), typeDiscriminator: "radial")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Axial), typeDiscriminator: "axial")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Planar), typeDiscriminator: "planar")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.PointMass), typeDiscriminator: "point")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Surface), typeDiscriminator: "surface")]
public abstract record WorldGravityAreaAcceleration {
    private WorldGravityAreaAcceleration() { }

    /// <summary>A placement-local acceleration vector, rotated by the placement's resolved yaw.</summary>
    /// <param name="Value">The acceleration vector in world units per second squared. Zero is admitted so
    /// <see cref="WorldGravityAreaMode.Replace"/> can author a zero-gravity pocket. An area's
    /// <see cref="WorldGravityArea.Vector"/> rows, where authored, supply the vector in its place.</param>
    public sealed record Directional(DocumentVector3 Value) : WorldGravityAreaAcceleration;
    /// <summary>A constant-magnitude acceleration directed toward the placement origin.</summary>
    /// <param name="Magnitude">The nonzero signed acceleration magnitude in world units per second squared.</param>
    public sealed record Radial(float Magnitude) : WorldGravityAreaAcceleration;
    /// <summary>A constant-magnitude acceleration directed toward the line through the placement origin along
    /// <paramref name="Axis"/>: the pull of a cylinder, or with a negative magnitude the hold of a tube's inside.</summary>
    /// <param name="Magnitude">The nonzero signed acceleration magnitude in world units per second squared.</param>
    /// <param name="Axis">The line's placement-local direction, rotated by the placement's resolved yaw. Absent is
    /// the placement's own Y axis.</param>
    public sealed record Axial(float Magnitude, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Axis = null) : WorldGravityAreaAcceleration;
    /// <summary>A constant-magnitude acceleration directed toward the plane through the placement origin, from
    /// either side of it: the pull of a slab a body walks on both faces of.</summary>
    /// <param name="Magnitude">The nonzero signed acceleration magnitude in world units per second squared.</param>
    /// <param name="Normal">The plane's placement-local normal, rotated by the placement's resolved yaw. Absent is
    /// the placement's own Y axis.</param>
    public sealed record Planar(float Magnitude, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Normal = null) : WorldGravityAreaAcceleration;
    /// <summary>An acceleration toward the placement origin that falls with distance as a uniform ball's does:
    /// <paramref name="SurfaceGravity"/> at <paramref name="ReferenceRadius"/>, the inverse square of the distance
    /// beyond it, and in proportion to the distance inside it.</summary>
    /// <param name="SurfaceGravity">The nonzero signed acceleration at the reference radius.</param>
    /// <param name="ReferenceRadius">The positive distance from the origin at which the surface value holds,
    /// multiplied by the placement's scale.</param>
    public sealed record PointMass(float SurfaceGravity, float ReferenceRadius) : WorldGravityAreaAcceleration;
    /// <summary>A constant-magnitude acceleration toward the nearest solid surface: against the world contact
    /// field's gradient at the body, so a body walks on whatever shape the world's solids give the field. The world
    /// must select gradient-derived up (<see cref="WorldContactRequirement.GradientDerivedUp"/>), which is what makes
    /// its contact provider answer the gradient.</summary>
    /// <param name="Magnitude">The nonzero signed acceleration magnitude in world units per second squared.</param>
    public sealed record Surface(float Magnitude) : WorldGravityAreaAcceleration;
}
/// <summary>A bounded local gravity influence riding a placement's resolved transform.</summary>
/// <param name="PlacementId">The placement whose position, yaw, scale, and optional body attachment the area follows.</param>
/// <param name="Priority">The deterministic composition priority. Lower values apply first; authored order breaks
/// ties, so a later equal-priority row applies later.</param>
/// <param name="Mode">Whether the matching area's acceleration combines with or replaces the accumulated answer.</param>
/// <param name="Bounds">The placement-relative analytic bound. Boundary points are included.</param>
/// <param name="Acceleration">The placement-relative acceleration form.</param>
/// <param name="Blend">The depth inside the bound, in world units, over which the area's weight rises smoothly from
/// nothing at the bound to one. Zero, the default, gives the area its whole weight everywhere inside. At a weight
/// between the two a <see cref="WorldGravityAreaMode.Replace"/> area mixes its answer with the accumulated one in
/// that proportion and a <see cref="WorldGravityAreaMode.Combine"/> area adds that share of its own, so a body
/// passing between two areas turns across the depth and never in one tick.</param>
/// <param name="Weight">A <c>Fixed</c> state row whose value, clamped to zero through one, multiplies the area's
/// weight: zero takes the area out of the fold altogether. Absent is one.</param>
/// <param name="Scale">A <c>Fixed</c> state row whose signed value multiplies the area's acceleration: minus one
/// turns the pull about. Absent is one.</param>
/// <param name="Vector">Three <c>Fixed</c> state rows that supply a
/// <see cref="WorldGravityAreaAcceleration.Directional"/> area's placement-local vector in place of the authored
/// one. Each is a row of its own so each may be a followed row: another world's value, read here as gravity.</param>
public sealed record WorldGravityArea(
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)] string PlacementId,
    int Priority,
    WorldGravityAreaMode Mode,
    WorldGravityAreaBounds Bounds,
    WorldGravityAreaAcceleration Acceleration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Blend = 0f,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Weight = null,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGravityAreaVector? Vector = null
);
/// <summary>The three state rows a directional gravity area reads its vector from, one an axis.</summary>
/// <param name="X">The <c>Fixed</c> slot row holding the vector's placement-local x.</param>
/// <param name="Y">The <c>Fixed</c> slot row holding the vector's placement-local y.</param>
/// <param name="Z">The <c>Fixed</c> slot row holding the vector's placement-local z.</param>
public sealed record WorldGravityAreaVector(
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string X,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string Y,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string Z
);
/// <summary>A closed-form orbit about a placement: a conic whose phase is an exact fraction of the engine tick, so its
/// position at any tick is computed directly and never by stepping the ticks before it.</summary>
/// <remarks>A body named by <paramref name="BodyIndex"/> rides the orbit: each tick it is moved to the orbit's
/// position as continuous motion, so a solid, a gravity area or a light attached to that body rides with it. While
/// <paramref name="Riding"/> reads zero the body is left to its own kit, and on the tick it falls to zero the body
/// takes the orbit's velocity, so it leaves the rails moving as it was. A tide source
/// (<see cref="WorldGravityTide.Orbit"/>) reads the same position with no body at all.</remarks>
/// <param name="Name">The orbit's name, unique in the section.</param>
/// <param name="Around">The placement at the orbit's focus. One attached to a body follows that body, so an orbit
/// nests about a body riding another.</param>
/// <param name="SemiMajorAxis">The positive semi-major axis, in world units.</param>
/// <param name="PeriodSeconds">The positive period, an exact whole number of engine ticks.</param>
/// <param name="Eccentricity">The eccentricity, zero for a circle and below
/// <see cref="FixedConicOrbit.MaxEccentricity"/>.</param>
/// <param name="InclinationDegrees">The tilt of the orbit's plane from the world's XZ plane, about its line of nodes.</param>
/// <param name="AscendingNodeDegrees">The heading of the line of nodes about +Y from +X.</param>
/// <param name="PeriapsisDegrees">The angle in the plane from the line of nodes to the periapsis.</param>
/// <param name="Phase">The fraction of a turn past the periapsis at engine tick zero.</param>
/// <param name="BodyIndex">The population body that rides the orbit, or absent for an orbit nothing rides.</param>
/// <param name="Riding">A state row whose nonzero value keeps the body on the orbit. Absent rides always.</param>
/// <param name="JoinSpeed">The speed, in world units per second, at which a body away from the orbit closes on it
/// once it rides again, on top of the orbit's own motion: it catches the rails rather than jumping onto them. Zero,
/// the default, puts the body on the orbit at once.</param>
/// <param name="PhaseBy">A <c>Fixed</c> state row whose value's fractional part is the orbit's phase, in place of
/// the engine tick: one whole unit of the row is one turn, and <paramref name="Phase"/> is added to it. The row is
/// the orbit's clock, so a row another world owns and this one follows places the orbit where that world's clock
/// says, and writing or holding the row moves or holds the body. <paramref name="PeriodSeconds"/> then states the
/// period the row's own advance gives and places nothing. Absent reads the engine tick.</param>
public sealed record WorldGravityOrbit(
    [property: DocumentText("an orbit name")] string Name,
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)] string Around,
    float SemiMajorAxis,
    decimal PeriodSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Eccentricity = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float InclinationDegrees = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float AscendingNodeDegrees = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float PeriapsisDegrees = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Phase = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BodyIndex = null,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Riding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float JoinSpeed = 0f,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhaseBy = null
);
/// <summary>A far mass whose pull reaches this world as a tide: the difference of its pull across the world, since
/// the pull itself moves the world's frame with everything in it and is dropped.</summary>
/// <remarks>The tide is one tensor a tick for the whole world, the sum over the section's sources, formed at
/// <see cref="WorldGravity.TideOrigin"/>; each body adds the tensor's product with its own offset from that origin.
/// It is exact to first order in that offset over the source's distance.</remarks>
/// <param name="SurfaceGravity">The positive acceleration the source would give at <paramref name="ReferenceRadius"/>
/// from its centre, in world units per second squared. With the radius it states the source's mass in the
/// quantities a designer observes.</param>
/// <param name="ReferenceRadius">The positive distance from the source's centre at which
/// <paramref name="SurfaceGravity"/> holds.</param>
/// <param name="PlacementId">The placement at the source's centre, in this world's frame. Exactly one of this and
/// <paramref name="Orbit"/> is authored.</param>
/// <param name="Orbit">The orbit whose position is the source's centre.</param>
/// <param name="Scale">A <c>Fixed</c> state row whose value multiplies the source's mass: a followed row carries
/// another world's mass here, and zero is a source that has gone. Absent is one.</param>
public sealed record WorldGravityTide(
    float SurfaceGravity,
    float ReferenceRadius,
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PlacementId = null,
    [property: DocumentText("an orbit name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Orbit = null,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scale = null
);
/// <summary>Declared capacity limits for local gravity authoring.</summary>
public static class WorldGravityCapacity {
    /// <summary>The most bounded local areas one world may evaluate for each active body.</summary>
    public const int MaxAreas = 64;
    /// <summary>The most orbits one world may evaluate each tick.</summary>
    public const int MaxOrbits = 256;
    /// <summary>The most far sources one world may sum into its tide each tick.</summary>
    public const int MaxTides = 64;
}
/// <summary>The gravitational strategies a world may select.</summary>
/// <remarks>The three disagree by design: <see cref="Pairwise"/> is the exact oracle, and the two hierarchical solvers
/// approximate it under their own opening rules. Selecting one is therefore a simulation-state decision, not a
/// presentation toggle.</remarks>
[JsonConverter(typeof(StrictEnumConverter<WorldGravitySolver>))]
public enum WorldGravitySolver : byte {
    /// <summary>Every source-target interaction, the exact deterministic oracle.</summary>
    Pairwise,

    /// <summary>An adaptive octree whose accepted distant cells contribute their total mass at their centre of mass.</summary>
    FastMonopole,

    /// <summary>An adaptive dual-tree fast multipole solve with first-order Cartesian local expansions.</summary>
    AdaptiveFmm,
}
/// <summary>
/// The world's gravitational field as data: the solver that evaluates it, the constants every interaction shares, and
/// the static sources it is summed from.
/// </summary>
/// <remarks>
/// <para>Absent or inert, a body under a Gravity or Lift hold falls at that row's own authored rate along world
/// <c>-Y</c>. Active, <c>ApplyHold</c> takes its magnitude and direction from here instead, and the row's rise/fall
/// asymmetry and terminal speed still shape the result.</para>
/// <para>Bodies are sources as well as targets, so they attract one another. Attractors are sources only — they ride a
/// placement's authored transform and never move, so the acceleration a solve computes for them is discarded.</para>
/// <para>Bounded areas are evaluated after that global answer, in deterministic priority/authored order. They ride a
/// placement's scale and resolved pose; only a <see cref="WorldGravityAreaAcceleration.Surface"/> area reads the
/// world's solid geometry, through the contact field's gradient.</para>
/// <para>Orbits and tides are what the field of another body becomes at a distance: an orbit is the closed form of
/// falling round a focus, and a tide is the part of a far pull a world feels inside itself. Tides add to an authored
/// field, so a world that declares them declares its own field too.</para>
/// </remarks>
/// <param name="Solver">The evaluation strategy.</param>
/// <param name="Uniform">A constant acceleration added to every solved answer, in world units per second squared.
/// Point masses cannot express a uniform field, so this is how a world keeps a flat floor underfoot while its
/// attractors own the space around them: authored as an acceleration, it is already what a body integrates.</param>
/// <param name="GravitationalConstant">The non-negative proportionality constant applied to every source mass.</param>
/// <param name="SofteningLength">The positive Plummer softening length that bounds the force at short range.</param>
/// <param name="Attractors">The static sources, or empty for a field summed from bodies alone.</param>
/// <param name="Points">Ergonomic point/planet presets lowered to static masses through the same Plummer kernel as
/// <paramref name="Attractors"/>. Absent means none.</param>
/// <param name="Areas">Bounded placement-relative influences evaluated after the global solve. Absent means none.</param>
/// <param name="Orbits">Closed-form orbits, each about a placement. Absent means none.</param>
/// <param name="Tides">Far sources whose pull reaches the world as a tide. Absent means none.</param>
/// <param name="TideOrigin">The placement the tide is formed at: the point of the world that falls freely with the
/// far sources' pull. Absent is the world's origin.</param>
public sealed record WorldGravity(
    WorldGravitySolver Solver,
    float GravitationalConstant,
    float SofteningLength,
    IReadOnlyList<WorldGravityAttractor> Attractors,
    DocumentVector3? Uniform = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGravityPoint>? Points = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGravityArea>? Areas = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGravityOrbit>? Orbits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGravityTide>? Tides = null,
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TideOrigin = null
) {
    /// <summary>Gets the inert field — the exact solver, no sources, and constants that produce no acceleration.</summary>
    public static WorldGravity Default { get; } = new(
        Areas: null,
        Attractors: [],
        GravitationalConstant: 0f,
        Points: null,
        SofteningLength: 1f,
        Solver: WorldGravitySolver.Pairwise,
        Uniform: null
    );
    /// <summary>Gets a value indicating whether the section declares an active global or bounded local field, an
    /// orbit or a tide.</summary>
    public bool IsActive => (
        (UniformAcceleration != Vector3.Zero) ||
        (Areas is { Count: > 0 }) ||
        (Orbits is { Count: > 0 }) ||
        (Tides is { Count: > 0 }) ||
        (GravitationalConstant > 0f)
    );
    /// <summary>Gets the authored uniform acceleration — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public Vector3 UniformAcceleration => (Uniform?.Value ?? Vector3.Zero);
}
/// <summary>The one-time fixed-point compilation of the world's gravity section, read by the per-tick solve.</summary>
/// <param name="Kind">The selected solver.</param>
/// <param name="Parameters">The compiled shared constants.</param>
/// <param name="Attractors">The compiled static sources, in authored order.</param>
/// <param name="Uniform">The compiled constant acceleration added to every answer.</param>
/// <param name="Areas">The compiled local areas in ascending priority/authored-order evaluation order.</param>
/// <param name="Orbits">The compiled orbits, in authored order.</param>
/// <param name="Tides">The compiled far sources, in authored order.</param>
/// <param name="TideOrigin">The point the tide is formed at.</param>
public readonly record struct FixedWorldGravity(
    GravitySolverKind Kind,
    GravityParameters Parameters,
    GravityBody[] Attractors,
    FixedVector3 Uniform,
    FixedWorldGravityArea[] Areas,
    FixedWorldGravityOrbit[] Orbits,
    FixedWorldGravityTide[] Tides,
    FixedVector3 TideOrigin
) {
    /// <summary>Gets a value indicating whether dynamic bodies participate in the global solver. Static attractors are
    /// optional because massive bodies are sources too.</summary>
    public bool HasGlobalSolve => (Parameters.GravitationalConstant > FixedQ4816.Zero);
    /// <summary>Gets the inert compilation — no sources and a zero constant.</summary>
    public static FixedWorldGravity Inert { get; } = new(
        Attractors: [],
        Kind: GravitySolverKind.Pairwise,
        Parameters: new GravityParameters(
            GravitationalConstant: FixedQ4816.Zero,
            SofteningLength: FixedQ4816.One
        ),
        Uniform: FixedVector3.Zero,
        Areas: [],
        Orbits: [],
        Tides: [],
        TideOrigin: FixedVector3.Zero
    );
    /// <summary>Gets a value indicating whether the compilation declares an active global or bounded local field, an
    /// orbit or a tide.</summary>
    public bool IsActive => (
        (Uniform != FixedVector3.Zero) ||
        (Areas.Length > 0) ||
        (Orbits.Length > 0) ||
        (Tides.Length > 0) ||
        HasGlobalSolve
    );

    // The point preset promises the ACTUAL softened-kernel acceleration at its reference radius. Keeping this
    // derivation in fixed point means validation, compilation, and the per-tick solver agree about every rounding
    // boundary; it also makes every overflow a named authoring refusal instead of a first-tick failure.
    internal static bool TryCompilePointMass(
        WorldGravityPoint point,
        float gravitationalConstant,
        float softeningLength,
        out FixedQ4816 mass
    ) {
        mass = FixedQ4816.Zero;

        if (
            !float.IsFinite(f: point.SurfaceGravity) ||
            !(point.SurfaceGravity > 0f) ||
            !float.IsFinite(f: point.ReferenceRadius) ||
            !(point.ReferenceRadius > 0f) ||
            !float.IsFinite(f: gravitationalConstant) ||
            !(gravitationalConstant > 0f) ||
            !float.IsFinite(f: softeningLength) ||
            !(softeningLength > 0f)
        ) {
            return false;
        }

        try {
            // FromDouble saturates finite out-of-range inputs, and the checked kernel below then rejects them. Keep
            // conversion inside this guarded block so this method stays a total validation predicate if that primitive's
            // conversion contract ever tightens.
            var surfaceGravity = FixedQ4816.FromDouble(value: point.SurfaceGravity);
            var referenceRadius = FixedQ4816.FromDouble(value: point.ReferenceRadius);
            var gravitationalScale = FixedQ4816.FromDouble(value: gravitationalConstant);
            var softening = FixedQ4816.FromDouble(value: softeningLength);

            if (
                (surfaceGravity <= FixedQ4816.Zero) ||
                (referenceRadius <= FixedQ4816.Zero) ||
                (gravitationalScale <= FixedQ4816.Zero) ||
                (softening <= FixedQ4816.Zero)
            ) {
                return false;
            }

            var radiusSquared = checked((referenceRadius * referenceRadius));
            var softeningSquared = checked((softening * softening));
            var softenedRadiusSquared = checked((radiusSquared + softeningSquared));
            var softenedRadius = FixedQ4816.Sqrt(value: softenedRadiusSquared);
            var softenedCube = checked((softenedRadiusSquared * softenedRadius));
            var numerator = checked((surfaceGravity * softenedCube));
            var denominator = checked((gravitationalScale * referenceRadius));

            mass = checked((numerator / denominator));

            if (mass <= FixedQ4816.Zero) {
                mass = FixedQ4816.Zero;

                return false;
            }

            // Mirror the exact point-kernel arithmetic once so a representable mass whose eventual G*m intermediate
            // overflows is refused here, not during WorldGravityField.Solve.
            var inverseSquareStrength = checked((checked((gravitationalScale * mass)) / softenedRadiusSquared));
            var scale = checked((inverseSquareStrength / softenedRadius));
            var compiledSurfaceGravity = checked((referenceRadius * scale));

            if (compiledSurfaceGravity <= FixedQ4816.Zero) {
                mass = FixedQ4816.Zero;

                return false;
            }

            return true;
        } catch (OverflowException) {
            mass = FixedQ4816.Zero;

            return false;
        } catch (DivideByZeroException) {
            mass = FixedQ4816.Zero;

            return false;
        }
    }

    private static FixedWorldGravityArea[] CompileAreas(IReadOnlyList<WorldGravityArea>? areas, IReadOnlyDictionary<string, CompiledPlacementFrame> frames, IReadOnlyList<WorldPlacement> placements) {
        if (areas is not { Count: > 0 }) {
            return [];
        }

        var compiled = new List<FixedWorldGravityArea>(capacity: areas.Count);

        for (var authoredIndex = 0; (authoredIndex < areas.Count); authoredIndex++) {
            var area = areas[authoredIndex];

            if (
                (area is null) ||
                (WorldDefinitionRows.FindPlacement(
                id: area.PlacementId,
                placements: placements
            ) is not { } placement) ||
                !FixedWorldGravityArea.TryCompile(
                area: area,
                authoredIndex: authoredIndex,
                compiled: out var lowered,
                frame: frames[placement.Id],
                placement: placement
            )
            ) {
                continue;
            }

            compiled.Add(item: lowered);
        }

        compiled.Sort(comparison: static (left, right) => {
            return LexicographicOrder.Compare(
                leftPrimary: left.Priority,
                leftSecondary: left.AuthoredIndex,
                rightPrimary: right.Priority,
                rightSecondary: right.AuthoredIndex
            );
        });

        return [.. compiled];
    }

    /// <summary>Compiles the authored section against the placements its attractors name.</summary>
    /// <param name="definition">The authored world and its canonical placement frames. A distributed row names its
    /// group frame, never one implicit attractor per occurrence.</param>
    /// <returns>The compiled field; <see cref="Inert"/> when no global or bounded local field is declared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static FixedWorldGravity Compile(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        var gravity = definition.Gravity;
        var placements = definition.Placements;

        if (!gravity.IsActive) {
            return Inert;
        }

        var frames = definition.PlacementFrames;
        var uniform = FixedVector3.FromVector3(value: gravity.UniformAcceleration);
        var areas = CompileAreas(
            areas: gravity.Areas,
            frames: frames,
            placements: placements
        );

        var orbits = CompileOrbits(
            frames: frames,
            orbits: gravity.Orbits,
            placements: placements
        );
        var tides = CompileTides(
            frames: frames,
            orbits: orbits,
            placements: placements,
            tides: gravity.Tides
        );
        var tideOrigin = (((gravity.TideOrigin is { } originId) && (WorldDefinitionRows.FindPlacement(id: originId, placements: placements) is { } origin))
            ? FixedVector3.FromVector3(value: frames[origin.Id].Position)
            : FixedVector3.Zero
        );
        var sourceCount = ((gravity.Attractors?.Count ?? 0) + (gravity.Points?.Count ?? 0));

        if (!(gravity.GravitationalConstant > 0f)) {
            return Inert with {
                Areas = areas,
                Orbits = orbits,
                Parameters = Inert.Parameters with { SofteningLength = FixedQ4816.FromDouble(value: gravity.SofteningLength) },
                TideOrigin = tideOrigin,
                Tides = tides,
                Uniform = uniform,
            };
        }

        var attractors = new List<GravityBody>(capacity: sourceCount);

        foreach (var attractor in (gravity.Attractors ?? [])) {
            // An attractor naming no live placement contributes nothing rather than throwing: the validator already
            // refuses the unresolved id, so reaching here means the row was removed after validation.
            if (WorldDefinitionRows.FindPlacement(
                id: attractor.PlacementId,
                placements: placements
            ) is not { } placement) {
                continue;
            }

            attractors.Add(item: new GravityBody(
                Mass: FixedQ4816.FromDouble(value: attractor.Mass),
                Position: FixedVector3.FromVector3(value: frames[placement.Id].Position)
            ));
        }

        foreach (var point in (gravity.Points ?? [])) {
            if (
                (point is null) ||
                (WorldDefinitionRows.FindPlacement(
                id: point.PlacementId,
                placements: placements
            ) is not { } placement) ||
                !TryCompilePointMass(
                gravitationalConstant: gravity.GravitationalConstant,
                mass: out var mass,
                point: point,
                softeningLength: gravity.SofteningLength
            )
            ) {
                continue;
            }

            attractors.Add(item: new GravityBody(
                Mass: mass,
                Position: FixedVector3.FromVector3(value: frames[placement.Id].Position)
            ));
        }

        return new FixedWorldGravity(
            Attractors: [.. attractors],
            Kind: gravity.Solver switch {
                WorldGravitySolver.FastMonopole => GravitySolverKind.FastMonopole,
                WorldGravitySolver.AdaptiveFmm => GravitySolverKind.AdaptiveFmm,
                _ => GravitySolverKind.Pairwise,
            },
            Parameters: new GravityParameters(
                GravitationalConstant: FixedQ4816.FromDouble(value: gravity.GravitationalConstant),
                SofteningLength: FixedQ4816.FromDouble(value: gravity.SofteningLength)
            ),
            Uniform: uniform,
            Areas: areas,
            Orbits: orbits,
            Tides: tides,
            TideOrigin: tideOrigin
        );
    }

    private static FixedWorldGravityOrbit[] CompileOrbits(IReadOnlyList<WorldGravityOrbit>? orbits, IReadOnlyDictionary<string, CompiledPlacementFrame> frames, IReadOnlyList<WorldPlacement> placements) {
        if (orbits is not { Count: > 0 }) {
            return [];
        }

        var compiled = new List<FixedWorldGravityOrbit>(capacity: orbits.Count);

        foreach (var orbit in orbits) {
            // A row the validator refuses compiles to nothing rather than throwing, as an attractor naming no live
            // placement does.
            if (
                (orbit is not null) &&
                (WorldDefinitionRows.FindPlacement(id: orbit.Around, placements: placements) is { } around) &&
                FixedWorldGravityOrbit.TryCompile(compiled: out var lowered, frame: frames[around.Id], orbit: orbit, placement: around)
            ) {
                compiled.Add(item: lowered);
            }
        }

        return [.. compiled];
    }
    private static FixedWorldGravityTide[] CompileTides(IReadOnlyList<WorldGravityTide>? tides, FixedWorldGravityOrbit[] orbits, IReadOnlyDictionary<string, CompiledPlacementFrame> frames, IReadOnlyList<WorldPlacement> placements) {
        if (tides is not { Count: > 0 }) {
            return [];
        }

        var compiled = new List<FixedWorldGravityTide>(capacity: tides.Count);

        foreach (var tide in tides) {
            if ((tide is null) || !FixedWorldGravityTide.TryStrength(strength: out var strength, tide: tide)) {
                continue;
            }

            if (tide.Orbit is { } orbitName) {
                var orbitIndex = Array.FindIndex(array: orbits, match: orbit => string.Equals(a: orbit.Name, b: orbitName, comparisonType: StringComparison.Ordinal));

                if (orbitIndex >= 0) {
                    compiled.Add(item: new FixedWorldGravityTide(Attach: null, AuthoredPosition: FixedVector3.Zero, OrbitIndex: orbitIndex, ScaleRow: tide.Scale, Strength: strength));
                }
            } else if ((tide.PlacementId is { } placementId) && (WorldDefinitionRows.FindPlacement(id: placementId, placements: placements) is { } placement)) {
                compiled.Add(item: new FixedWorldGravityTide(
                    Attach: placement.Attach,
                    AuthoredPosition: FixedVector3.FromVector3(value: frames[placement.Id].Position),
                    OrbitIndex: -1,
                    ScaleRow: tide.Scale,
                    Strength: strength
                ));
            }
        }

        return [.. compiled];
    }
}
/// <summary>One compiled orbit: its focus, its conic and its exact period.</summary>
/// <param name="Name">The orbit's authored name.</param>
/// <param name="AuthoredCenter">The focus placement's static position, used when <paramref name="Attach"/> is absent.</param>
/// <param name="Attach">The focus placement's attachment facet, or <see langword="null"/> for a static focus.</param>
/// <param name="Orbit">The conic, in world units.</param>
/// <param name="PeriodTicks">The period in engine ticks.</param>
/// <param name="PhaseOffset">The phase at engine tick zero, as a fraction of one turn scaled by 2^64.</param>
/// <param name="BodyIndex">The body that rides the orbit, or -1 for none.</param>
/// <param name="RidingRow">The state row whose nonzero value keeps the body on the orbit, or <see langword="null"/>
/// for a body that always rides.</param>
/// <param name="JoinSpeed">The speed at which a body away from the orbit closes on it, or zero to join at once.</param>
/// <param name="PhaseRow">The <c>Fixed</c> state row whose fractional part is the phase, or <see langword="null"/> for
/// an orbit that reads the engine tick.</param>
public readonly record struct FixedWorldGravityOrbit(
    string Name,
    FixedVector3 AuthoredCenter,
    WorldPlacementAttach? Attach,
    FixedConicOrbit Orbit,
    ulong PeriodTicks,
    ulong PhaseOffset,
    int BodyIndex,
    string? RidingRow,
    FixedQ4816 JoinSpeed,
    string? PhaseRow = null
) {
    /// <summary>Returns the orbit's position relative to its focus at an engine tick: one integer division for the
    /// phase and one closed-form evaluation, whatever ticks were evaluated before.</summary>
    /// <param name="engineTick">The engine tick.</param>
    /// <returns>The offset from the focus, in world units.</returns>
    public FixedVector3 OffsetAt(ulong engineTick) => OffsetAtTurn(turn: FixedConicOrbit.Phase(elapsedTicks: engineTick, periodTicks: PeriodTicks));
    /// <summary>Returns the orbit's position relative to its focus where a clock row's turn stands
    /// (<see cref="WorldGravityOrbit.PhaseBy"/>, read exactly by <see cref="WorldStateReader.TryReadTurn"/>), which the
    /// authored phase offsets.</summary>
    /// <param name="turn">The clock row's fractional turn, scaled by 2^64.</param>
    /// <returns>The offset from the focus, in world units.</returns>
    public FixedVector3 OffsetAtTurn(ulong turn) => Orbit.PositionAt(phase: unchecked((turn + PhaseOffset)));

    /// <summary>Returns a turn's fractional part as a fraction of one turn scaled by 2^64: an authored phase, or a
    /// presentation clock's.</summary>
    /// <param name="turn">The finite turn count; only its fractional part places a body.</param>
    /// <returns>The turn.</returns>
    public static ulong TurnOf(double turn) => unchecked((ulong)((turn - Math.Floor(d: turn)) * 18446744073709551616d));
    /// <summary>Lowers an orbit's authored shape and plane to its conic: the one lowering an authority's orbit row and
    /// a presentation's light source share, so both place a body on the same curve.</summary>
    /// <param name="semiMajorAxis">The semi-major axis, in world units.</param>
    /// <param name="eccentricity">The eccentricity.</param>
    /// <param name="inclinationDegrees">The tilt of the plane about its line of nodes, in degrees.</param>
    /// <param name="ascendingNodeDegrees">The heading of the line of nodes about +Y from +X, in degrees.</param>
    /// <param name="periapsisDegrees">The angle in the plane from the line of nodes to the periapsis, in degrees.</param>
    /// <param name="conic">The conic on return.</param>
    /// <returns><see langword="true"/> when every element is finite, the axis is positive and the eccentricity lies in
    /// zero up to <see cref="FixedConicOrbit.MaxEccentricity"/>.</returns>
    public static bool TryConic(float semiMajorAxis, float eccentricity, float inclinationDegrees, float ascendingNodeDegrees, float periapsisDegrees, out FixedConicOrbit conic) {
        conic = default;

        if (
            !float.IsFinite(f: semiMajorAxis) || !(semiMajorAxis > 0f) ||
            !float.IsFinite(f: eccentricity) || (eccentricity < 0f) ||
            !float.IsFinite(f: inclinationDegrees) || !float.IsFinite(f: ascendingNodeDegrees) || !float.IsFinite(f: periapsisDegrees)
        ) {
            return false;
        }

        try {
            var shape = FixedQ4816.FromDouble(value: eccentricity);
            var axis = FixedQ4816.FromDouble(value: semiMajorAxis);

            if ((shape >= FixedConicOrbit.MaxEccentricity) || (axis <= FixedQ4816.Zero)) {
                return false;
            }

            conic = FixedConicOrbit.FromAngles(
                argumentOfPeriapsis: FixedQ4816.FromDegrees(degrees: periapsisDegrees),
                ascendingNode: FixedQ4816.FromDegrees(degrees: ascendingNodeDegrees),
                eccentricity: shape,
                inclination: FixedQ4816.FromDegrees(degrees: inclinationDegrees),
                semiMajorAxis: axis
            );

            return true;
        } catch (OverflowException) {
            conic = default;

            return false;
        }
    }

    /// <summary>Attempts to lower an authored orbit to fixed point.</summary>
    internal static bool TryCompile(WorldGravityOrbit orbit, WorldPlacement placement, CompiledPlacementFrame frame, out FixedWorldGravityOrbit compiled) {
        compiled = default;

        if (
            string.IsNullOrWhiteSpace(value: orbit.Name) ||
            !float.IsFinite(f: orbit.Phase) ||
            !float.IsFinite(f: orbit.JoinSpeed) ||
            (orbit.JoinSpeed < 0f) ||
            (orbit.BodyIndex is < 0) ||
            !FixedTickConversion.TryDurationEngineTicksExact(seconds: orbit.PeriodSeconds, ticks: out var periodTicks) ||
            (periodTicks == 0UL) ||
            !TryConic(
                ascendingNodeDegrees: orbit.AscendingNodeDegrees,
                conic: out var conic,
                eccentricity: orbit.Eccentricity,
                inclinationDegrees: orbit.InclinationDegrees,
                periapsisDegrees: orbit.PeriapsisDegrees,
                semiMajorAxis: orbit.SemiMajorAxis
            )
        ) {
            return false;
        }

        try {
            compiled = new FixedWorldGravityOrbit(
                Attach: placement.Attach,
                AuthoredCenter: FixedVector3.FromVector3(value: frame.Position),
                BodyIndex: (orbit.BodyIndex ?? -1),
                JoinSpeed: FixedQ4816.FromDouble(value: orbit.JoinSpeed),
                Name: orbit.Name,
                Orbit: conic,
                PeriodTicks: periodTicks,
                PhaseOffset: TurnOf(turn: orbit.Phase),
                PhaseRow: orbit.PhaseBy,
                RidingRow: orbit.Riding
            );

            return true;
        } catch (OverflowException) {
            compiled = default;

            return false;
        }
    }
}
/// <summary>One compiled far source of the world's tide.</summary>
/// <param name="Strength">The source's gravitational parameter: its surface gravity times its reference radius
/// squared.</param>
/// <param name="AuthoredPosition">The source placement's static position, used when it neither rides an orbit nor
/// is attached.</param>
/// <param name="Attach">The source placement's attachment facet, or <see langword="null"/>.</param>
/// <param name="OrbitIndex">The index of the orbit the source rides, or -1 for a placement.</param>
/// <param name="ScaleRow">The state row that multiplies the source's mass, or <see langword="null"/>.</param>
public readonly record struct FixedWorldGravityTide(FixedQ4816 Strength, FixedVector3 AuthoredPosition, WorldPlacementAttach? Attach, int OrbitIndex, string? ScaleRow) {
    /// <summary>Attempts to lower a source's surface gravity and reference radius to its gravitational parameter.</summary>
    internal static bool TryStrength(WorldGravityTide tide, out FixedQ4816 strength) {
        strength = FixedQ4816.Zero;

        if (
            !float.IsFinite(f: tide.SurfaceGravity) ||
            !(tide.SurfaceGravity > 0f) ||
            !float.IsFinite(f: tide.ReferenceRadius) ||
            !(tide.ReferenceRadius > 0f)
        ) {
            return false;
        }

        try {
            var radius = FixedQ4816.FromDouble(value: tide.ReferenceRadius);

            strength = checked(((FixedQ4816.FromDouble(value: tide.SurfaceGravity) * radius) * radius));

            return (strength > FixedQ4816.Zero);
        } catch (OverflowException) {
            strength = FixedQ4816.Zero;

            return false;
        }
    }
}
/// <summary>The compiled analytic bound kind for a local gravity area.</summary>
public enum FixedWorldGravityAreaBoundsKind : byte {
    /// <summary>A sphere whose boundary is included.</summary>
    Sphere,

    /// <summary>A placement-yaw-local box whose faces are included.</summary>
    Box,
}
/// <summary>The compiled acceleration kind for a local gravity area.</summary>
public enum FixedWorldGravityAreaAccelerationKind : byte {
    /// <summary>A placement-local vector rotated into world space.</summary>
    Directional,

    /// <summary>A constant magnitude directed toward the placement origin.</summary>
    Radial,

    /// <summary>A constant magnitude directed toward the line through the placement origin.</summary>
    Axial,

    /// <summary>A constant magnitude directed toward the plane through the placement origin.</summary>
    Planar,

    /// <summary>A uniform ball's pull: the inverse square of the distance outside the reference radius and in
    /// proportion to the distance inside it.</summary>
    Point,

    /// <summary>A constant magnitude directed against the contact field's gradient at the body.</summary>
    Surface,
}
/// <summary>One fixed-point bounded gravity influence in deterministic evaluation order.</summary>
/// <param name="AuthoredIndex">The row's original index, used as the stable equal-priority tie-break.</param>
/// <param name="PlacementId">The placement the area rides.</param>
/// <param name="Priority">The authored priority; lower values apply first.</param>
/// <param name="Mode">The area's explicit composition mode.</param>
/// <param name="BoundsKind">The compiled analytic-bound kind.</param>
/// <param name="Radius">The scaled sphere radius, or zero for a box.</param>
/// <param name="RadiusSquared">The scaled squared sphere radius, or zero for a box.</param>
/// <param name="HalfExtents">The scaled box half extents, or zero for a sphere.</param>
/// <param name="AccelerationKind">The compiled acceleration kind.</param>
/// <param name="LocalVector">The placement-local directional vector, or the unit axis of an axial area or unit normal
/// of a planar one; zero for the other forms.</param>
/// <param name="Magnitude">The signed magnitude of every form but directional, for which it is zero; a point area's
/// value at its reference radius.</param>
/// <param name="ReferenceRadius">A point area's scaled reference radius, or zero.</param>
/// <param name="Blend">The depth inside the bound over which the area's weight rises to one, or zero for none.</param>
/// <param name="WeightRow">The state row that multiplies the area's weight, or <see langword="null"/>.</param>
/// <param name="ScaleRow">The state row that multiplies the area's acceleration, or <see langword="null"/>.</param>
/// <param name="VectorRows">The three state rows that supply a directional area's vector, or <see langword="null"/>.</param>
/// <param name="AuthoredPosition">The static placement position used when <paramref name="Attach"/> is absent.</param>
/// <param name="AuthoredYawRadians">The static placement yaw used when <paramref name="Attach"/> is absent.</param>
/// <param name="Attach">The existing placement attachment facet, or null for a static area.</param>
public readonly record struct FixedWorldGravityArea(
    int AuthoredIndex,
    string PlacementId,
    int Priority,
    WorldGravityAreaMode Mode,
    FixedWorldGravityAreaBoundsKind BoundsKind,
    FixedQ4816 Radius,
    FixedQ4816 RadiusSquared,
    FixedVector3 HalfExtents,
    FixedWorldGravityAreaAccelerationKind AccelerationKind,
    FixedVector3 LocalVector,
    FixedQ4816 Magnitude,
    FixedQ4816 ReferenceRadius,
    FixedQ4816 Blend,
    string? WeightRow,
    string? ScaleRow,
    WorldGravityAreaVector? VectorRows,
    FixedVector3 AuthoredPosition,
    FixedQ4816 AuthoredYawRadians,
    WorldPlacementAttach? Attach
) {

    // A signed magnitude that survives its Q48.16 lowering as nonzero.
    private static bool TryMagnitude(float value, out FixedQ4816 magnitude) {
        magnitude = FixedQ4816.Zero;

        if (
            !IsFixedRepresentable(value: value) ||
            (value == 0f)
        ) {
            return false;
        }

        magnitude = FixedQ4816.FromDouble(value: value);

        return (magnitude != FixedQ4816.Zero);
    }
    // A direction lowered to a unit vector; absent is the placement's own Y axis.
    private static bool TryUnit(DocumentVector3? value, out FixedVector3 unit) {
        unit = FixedVector3.UnitY;

        if (value is not { } authored) {
            return true;
        }

        var vector = ((Vector3)authored);

        if (!IsFixedRepresentable(value: vector)) {
            return false;
        }

        unit = FixedVector3.FromVector3(value: vector).Normalize();

        return (unit != FixedVector3.Zero);
    }

    /// <summary>Attempts to lower an authored area through the fixed-point analytic evaluator.</summary>
    internal static bool TryCompile(WorldGravityArea area, WorldPlacement placement, CompiledPlacementFrame frame, int authoredIndex, out FixedWorldGravityArea compiled) {
        compiled = default;

        if (
            !Enum.IsDefined(value: area.Mode) ||
            !IsFixedRepresentable(value: frame.Position) ||
            !IsFixedRepresentable(value: placement.Scale) ||
            !(placement.Scale > 0f) ||
            !IsFixedRepresentable(value: frame.YawDegrees) ||
            !IsFixedRepresentable(value: area.Blend) ||
            (area.Blend < 0f)
        ) {
            return false;
        }

        try {
            var scale = FixedQ4816.FromDouble(value: placement.Scale);
            var position = FixedVector3.FromVector3(value: frame.Position);
            var yaw = FixedQ4816.FromDegrees(degrees: frame.YawDegrees);
            var boundsKind = default(FixedWorldGravityAreaBoundsKind);
            var radius = FixedQ4816.Zero;
            var radiusSquared = FixedQ4816.Zero;
            var halfExtents = FixedVector3.Zero;

            switch (area.Bounds) {
                case WorldGravityAreaBounds.SphereBounds sphere: {
                        if (
                            !IsFixedRepresentable(value: sphere.Radius) ||
                            !(sphere.Radius > 0f)
                        ) {
                            return false;
                        }

                        radius = checked((FixedQ4816.FromDouble(value: sphere.Radius) * scale));

                        if (radius <= FixedQ4816.Zero) {
                            return false;
                        }

                        radiusSquared = checked((radius * radius));
                        boundsKind = FixedWorldGravityAreaBoundsKind.Sphere;
                        break;
                    }
                case WorldGravityAreaBounds.BoxBounds box: {
                        if (
                            !IsFixedRepresentable(value: box.HalfExtents) ||
                            !(box.HalfExtents.X > 0f) ||
                            !(box.HalfExtents.Y > 0f) ||
                            !(box.HalfExtents.Z > 0f)
                        ) {
                            return false;
                        }

                        var local = FixedVector3.FromVector3(value: box.HalfExtents);

                        halfExtents = checked((local * scale));
                        if (
                            (halfExtents.X <= FixedQ4816.Zero) ||
                            (halfExtents.Y <= FixedQ4816.Zero) ||
                            (halfExtents.Z <= FixedQ4816.Zero)
                        ) {
                            return false;
                        }
                        boundsKind = FixedWorldGravityAreaBoundsKind.Box;
                        break;
                    }
                default:
                    return false;
            }

            var accelerationKind = default(FixedWorldGravityAreaAccelerationKind);
            var localVector = FixedVector3.Zero;
            var magnitude = FixedQ4816.Zero;
            var referenceRadius = FixedQ4816.Zero;

            switch (area.Acceleration) {
                case WorldGravityAreaAcceleration.Directional directional:
                    if (!IsFixedRepresentable(value: directional.Value)) {
                        return false;
                    }

                    localVector = FixedVector3.FromVector3(value: directional.Value);
                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Directional;
                    break;
                case WorldGravityAreaAcceleration.Radial radial:
                    if (!TryMagnitude(value: radial.Magnitude, magnitude: out magnitude)) {
                        return false;
                    }

                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Radial;
                    break;
                case WorldGravityAreaAcceleration.Axial axial:
                    if (
                        !TryMagnitude(value: axial.Magnitude, magnitude: out magnitude) ||
                        !TryUnit(value: axial.Axis, unit: out localVector)
                    ) {
                        return false;
                    }

                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Axial;
                    break;
                case WorldGravityAreaAcceleration.Planar planar:
                    if (
                        !TryMagnitude(value: planar.Magnitude, magnitude: out magnitude) ||
                        !TryUnit(value: planar.Normal, unit: out localVector)
                    ) {
                        return false;
                    }

                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Planar;
                    break;
                case WorldGravityAreaAcceleration.PointMass point:
                    if (
                        !TryMagnitude(value: point.SurfaceGravity, magnitude: out magnitude) ||
                        !IsFixedRepresentable(value: point.ReferenceRadius) ||
                        !(point.ReferenceRadius > 0f)
                    ) {
                        return false;
                    }

                    referenceRadius = checked((FixedQ4816.FromDouble(value: point.ReferenceRadius) * scale));
                    if (referenceRadius <= FixedQ4816.Zero) {
                        return false;
                    }
                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Point;
                    break;
                case WorldGravityAreaAcceleration.Surface surface:
                    if (!TryMagnitude(value: surface.Magnitude, magnitude: out magnitude)) {
                        return false;
                    }

                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Surface;
                    break;
                default:
                    return false;
            }

            // A state vector belongs to the one form that has a vector to supply.
            if (
                (area.Vector is not null) &&
                (accelerationKind != FixedWorldGravityAreaAccelerationKind.Directional)
            ) {
                return false;
            }

            // Exercise the static rotation at compile time so a malformed fixed conversion is a validator refusal,
            // never a first-tick exception. Attached yaws use the same unit-quaternion operation at runtime.
            _ = FixedQuaternion.FromAxisAngle(
                angle: yaw,
                axis: FixedVector3.UnitY
            ).Rotate(vector: localVector);

            compiled = new FixedWorldGravityArea(
                AccelerationKind: accelerationKind,
                Attach: placement.Attach,
                AuthoredIndex: authoredIndex,
                AuthoredPosition: position,
                AuthoredYawRadians: yaw,
                Blend: FixedQ4816.FromDouble(value: area.Blend),
                BoundsKind: boundsKind,
                HalfExtents: halfExtents,
                LocalVector: localVector,
                Magnitude: magnitude,
                Mode: area.Mode,
                PlacementId: area.PlacementId,
                Priority: area.Priority,
                Radius: radius,
                RadiusSquared: radiusSquared,
                ReferenceRadius: referenceRadius,
                ScaleRow: area.Scale,
                VectorRows: area.Vector,
                WeightRow: area.Weight
            );

            return true;
        } catch (OverflowException) {
            compiled = default;

            return false;
        }
    }

    private static bool IsFixedRepresentable(float value) => (
        float.IsFinite(f: value) &&
        (((double)value) >= ((double)FixedQ4816.MinValue)) &&
        (((double)value) <= ((double)FixedQ4816.MaxValue))
    );
    private static bool IsFixedRepresentable(Vector3 value) => (
        IsFixedRepresentable(value: value.X) &&
        IsFixedRepresentable(value: value.Y) &&
        IsFixedRepresentable(value: value.Z)
    );

    /// <summary>Evaluates the area's world-space acceleration at a point already known to match its bound, before
    /// its scale row. A <see cref="FixedWorldGravityAreaAccelerationKind.Surface"/> area answers through
    /// <see cref="SurfaceAcceleration"/> instead, since its direction is the contact field's.</summary>
    /// <param name="point">The world-space point.</param>
    /// <param name="center">The area's resolved world position.</param>
    /// <param name="rotation">The area's resolved yaw rotation.</param>
    /// <param name="localVector">The directional vector in force: <see cref="LocalVector"/>, or the area's state
    /// vector where it authors one.</param>
    /// <returns>The acceleration; zero at a radial, axial or point area's own centre line and on a planar area's
    /// plane, where the form has no direction.</returns>
    public FixedVector3 AccelerationAt(FixedVector3 point, FixedVector3 center, FixedQuaternion rotation, FixedVector3 localVector) {
        var inward = (center - point);

        switch (AccelerationKind) {
            case FixedWorldGravityAreaAccelerationKind.Directional:
                return rotation.Rotate(vector: localVector);
            case FixedWorldGravityAreaAccelerationKind.Radial:
                return (inward.Normalize() * Magnitude);
            case FixedWorldGravityAreaAccelerationKind.Axial:
                return (inward.RejectFrom(unitAxis: rotation.Rotate(vector: LocalVector)).Normalize() * Magnitude);
            case FixedWorldGravityAreaAccelerationKind.Planar: {
                    var normal = rotation.Rotate(vector: LocalVector);
                    var height = FixedVector3.Dot(left: inward, right: normal);

                    return ((height == FixedQ4816.Zero)
                        ? FixedVector3.Zero
                        : (normal * ((height > FixedQ4816.Zero) ? Magnitude : -Magnitude))
                    );
                }
            case FixedWorldGravityAreaAccelerationKind.Point: {
                    var distance = inward.Length;

                    if (distance == FixedQ4816.Zero) {
                        return FixedVector3.Zero;
                    }

                    // Inside the reference radius the pull is the surface value in proportion to the distance, so
                    // the ratio is at most one on both branches and its square never leaves the grid.
                    var ratio = ((distance >= ReferenceRadius)
                        ? (ReferenceRadius / distance)
                        : (distance / ReferenceRadius)
                    );
                    var strength = ((distance >= ReferenceRadius)
                        ? ((Magnitude * ratio) * ratio)
                        : (Magnitude * ratio)
                    );

                    return (inward.Normalize() * strength);
                }
            default:
                return FixedVector3.Zero;
        }
    }
    /// <summary>Evaluates a <see cref="FixedWorldGravityAreaAccelerationKind.Surface"/> area's acceleration from the
    /// contact field's up at the point.</summary>
    /// <param name="surfaceUp">The unit direction away from the nearest solid surface.</param>
    /// <returns>The acceleration toward that surface, before the area's scale row.</returns>
    public FixedVector3 SurfaceAcceleration(FixedVector3 surfaceUp) => (surfaceUp * -Magnitude);
    /// <summary>Returns whether a world-space point lies inside the analytic bound, including its boundary.</summary>
    public bool Contains(FixedVector3 point, FixedVector3 center, FixedQuaternion rotation) {
        var delta = (point - center);

        if (BoundsKind == FixedWorldGravityAreaBoundsKind.Sphere) {
            return (
                delta.TryLengthSquared(squaredLength: out var squaredDistance) &&
                (squaredDistance <= RadiusSquared)
            );
        }

        var local = rotation.Conjugate().Rotate(vector: delta);

        return (
            (local.X >= -HalfExtents.X) &&
            (local.X <= HalfExtents.X) &&
            (local.Y >= -HalfExtents.Y) &&
            (local.Y <= HalfExtents.Y) &&
            (local.Z >= -HalfExtents.Z) &&
            (local.Z <= HalfExtents.Z)
        );
    }
    /// <summary>Returns the area's geometric weight at a point already known to match its bound: one where it
    /// authors no blend, else the smooth rise across <see cref="Blend"/> of the point's depth inside the bound.</summary>
    /// <param name="point">The world-space point.</param>
    /// <param name="center">The area's resolved world position.</param>
    /// <param name="rotation">The area's resolved yaw rotation.</param>
    /// <returns>The weight, zero on the bound through one at the blend depth and deeper.</returns>
    public FixedQ4816 WeightAt(FixedVector3 point, FixedVector3 center, FixedQuaternion rotation) {
        if (Blend <= FixedQ4816.Zero) {
            return FixedQ4816.One;
        }

        var delta = (point - center);
        FixedQ4816 depth;

        if (BoundsKind == FixedWorldGravityAreaBoundsKind.Sphere) {
            depth = (Radius - delta.Length);
        } else {
            var local = rotation.Conjugate().Rotate(vector: delta);

            depth = FixedQ4816.Min(
                x: FixedQ4816.Min(
                    x: (HalfExtents.X - FixedQ4816.Abs(value: local.X)),
                    y: (HalfExtents.Y - FixedQ4816.Abs(value: local.Y))
                ),
                y: (HalfExtents.Z - FixedQ4816.Abs(value: local.Z))
            );
        }

        return FixedQ4816.Smoothstep(
            edge0: FixedQ4816.Zero,
            edge1: Blend,
            value: depth
        );
    }

    /// <summary>Gets the area's authored static yaw rotation.</summary>
    public FixedQuaternion AuthoredRotation => FixedQuaternion.FromAxisAngle(
        angle: AuthoredYawRadians,
        axis: FixedVector3.UnitY
    );
}
