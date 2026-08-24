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
public sealed record WorldGravityAttractor(string PlacementId, float Mass);
/// <summary>A point-gravity preset riding a placement, authored in the quantities a world designer observes.</summary>
/// <param name="PlacementId">The <c>placements</c> row at the point source's centre.</param>
/// <param name="SurfaceGravity">The positive acceleration magnitude, in world units per second squared, promised at
/// <paramref name="ReferenceRadius"/> after the world's authored softening is applied.</param>
/// <param name="ReferenceRadius">The positive distance from the source centre at which <paramref name="SurfaceGravity"/>
/// is promised. For a planet this is its surface radius; a source need not carry geometry.</param>
public sealed record WorldGravityPoint(string PlacementId, float SurfaceGravity, float ReferenceRadius);
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
/// <para>Absent or inert, a body integrating <c>ApplyVerticalGravity</c> falls at its kit's authored rate along world
/// <c>-Y</c>. Active, that same op takes its magnitude and direction from here instead, and the kit's rise/fall
/// asymmetry and terminal speed still shape the result.</para>
/// <para>Bodies are sources as well as targets, so they attract one another. Attractors are sources only — they ride a
/// placement's authored transform and never move, so the acceleration a solve computes for them is discarded.</para>
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
public sealed record WorldGravity(
    WorldGravitySolver Solver,
    float GravitationalConstant,
    float SofteningLength,
    IReadOnlyList<WorldGravityAttractor> Attractors,
    DocumentVector3? Uniform = null,
    IReadOnlyList<WorldGravityPoint>? Points = null
) {
    /// <summary>Gets the inert field — the exact solver, no sources, and constants that produce no acceleration.</summary>
    public static WorldGravity Default { get; } = new(
        Attractors: [],
        GravitationalConstant: 0f,
        SofteningLength: 1f,
        Solver: WorldGravitySolver.Pairwise,
        Uniform: null,
        Points: null
    );
    /// <summary>Gets a value indicating whether this field can produce a nonzero acceleration.</summary>
    public bool IsActive => (
        (UniformAcceleration != Vector3.Zero) ||
        ((GravitationalConstant > 0f) && (
            (Attractors is { Count: > 0 }) ||
            (Points is { Count: > 0 })
        ))
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
public readonly record struct FixedWorldGravity(
    GravitySolverKind Kind,
    GravityParameters Parameters,
    GravityBody[] Attractors,
    FixedVector3 Uniform
) {
    /// <summary>Gets a value indicating whether any attractor contributes, so a solver run is worth its cost.</summary>
    public bool HasAttractors => ((Parameters.GravitationalConstant > FixedQ4816.Zero) && (Attractors.Length > 0));
    /// <summary>Gets the inert compilation — no sources and a zero constant.</summary>
    public static FixedWorldGravity Inert { get; } = new(
        Attractors: [],
        Kind: GravitySolverKind.Pairwise,
        Parameters: new GravityParameters(
            GravitationalConstant: FixedQ4816.Zero,
            SofteningLength: FixedQ4816.One
        ),
        Uniform: FixedVector3.Zero
    );
    /// <summary>Gets a value indicating whether a solve can produce a nonzero acceleration.</summary>
    public bool IsActive => (
        (Uniform != FixedVector3.Zero) ||
        ((Parameters.GravitationalConstant > FixedQ4816.Zero) && (Attractors.Length > 0))
    );

    /// <summary>Compiles the authored section against the placements its attractors name.</summary>
    /// <param name="gravity">The authored section.</param>
    /// <param name="placements">The placement rows an attractor resolves its position from.</param>
    /// <returns>The compiled field; <see cref="Inert"/> when nothing can produce an acceleration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gravity"/> is <see langword="null"/>.</exception>
    public static FixedWorldGravity Compile(WorldGravity gravity, IReadOnlyList<WorldPlacement> placements) {
        ArgumentNullException.ThrowIfNull(gravity);

        if (!gravity.IsActive) {
            return Inert;
        }

        var uniform = FixedVector3.FromVector3(value: gravity.UniformAcceleration);

        var sourceCount = ((gravity.Attractors?.Count ?? 0) + (gravity.Points?.Count ?? 0));

        if (!((gravity.GravitationalConstant > 0f) && (sourceCount > 0))) {
            return Inert with { Uniform = uniform };
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
                Position: FixedVector3.FromVector3(value: placement.Position)
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
                Position: FixedVector3.FromVector3(value: placement.Position)
            ));
        }

        if (attractors.Count == 0) {
            return Inert with { Uniform = uniform };
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
            Uniform: uniform
        );
    }

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
}
