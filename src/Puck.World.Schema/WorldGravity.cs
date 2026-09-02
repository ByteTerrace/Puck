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
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Directional), typeDiscriminator: "directional")]
[JsonDerivedType(typeof(WorldGravityAreaAcceleration.Radial), typeDiscriminator: "radial")]
public abstract record WorldGravityAreaAcceleration {
    private WorldGravityAreaAcceleration() { }

    /// <summary>A placement-local acceleration vector, rotated by the placement's resolved yaw.</summary>
    /// <param name="Value">The acceleration vector in world units per second squared. Zero is admitted so
    /// <see cref="WorldGravityAreaMode.Replace"/> can author a zero-gravity pocket.</param>
    public sealed record Directional(DocumentVector3 Value) : WorldGravityAreaAcceleration;
    /// <summary>A constant-magnitude acceleration directed toward the placement origin.</summary>
    /// <param name="Magnitude">The positive acceleration magnitude in world units per second squared.</param>
    public sealed record Radial(float Magnitude) : WorldGravityAreaAcceleration;
}
/// <summary>A bounded local gravity influence riding a placement's resolved transform.</summary>
/// <param name="PlacementId">The placement whose position, yaw, scale, and optional body attachment the area follows.</param>
/// <param name="Priority">The deterministic composition priority. Lower values apply first; authored order breaks
/// ties, so a later equal-priority row applies later.</param>
/// <param name="Mode">Whether the matching area's acceleration combines with or replaces the accumulated answer.</param>
/// <param name="Bounds">The placement-relative analytic bound. Boundary points are included.</param>
/// <param name="Acceleration">The placement-relative acceleration form.</param>
public sealed record WorldGravityArea(
    string PlacementId,
    int Priority,
    WorldGravityAreaMode Mode,
    WorldGravityAreaBounds Bounds,
    WorldGravityAreaAcceleration Acceleration
);
/// <summary>Declared capacity limits for local gravity authoring.</summary>
public static class WorldGravityCapacity {
    /// <summary>The most bounded local areas one world may evaluate for each active body.</summary>
    public const int MaxAreas = 64;
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
/// <para>Absent or inert, a body integrating <c>ApplyVerticalGravity</c> falls at its kit's authored rate along world
/// <c>-Y</c>. Active, that same op takes its magnitude and direction from here instead, and the kit's rise/fall
/// asymmetry and terminal speed still shape the result.</para>
/// <para>Bodies are sources as well as targets, so they attract one another. Attractors are sources only — they ride a
/// placement's authored transform and never move, so the acceleration a solve computes for them is discarded.</para>
/// <para>Bounded areas are evaluated after that global answer, in deterministic priority/authored order. They ride a
/// placement's scale and resolved pose but never infer acceleration from its solid or SDF geometry.</para>
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
public sealed record WorldGravity(
    WorldGravitySolver Solver,
    float GravitationalConstant,
    float SofteningLength,
    IReadOnlyList<WorldGravityAttractor> Attractors,
    DocumentVector3? Uniform = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGravityPoint>? Points = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGravityArea>? Areas = null
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
    /// <summary>Gets a value indicating whether the section declares an active global or bounded local field.</summary>
    public bool IsActive => (
        (UniformAcceleration != Vector3.Zero) ||
        (Areas is { Count: > 0 }) ||
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
public readonly record struct FixedWorldGravity(
    GravitySolverKind Kind,
    GravityParameters Parameters,
    GravityBody[] Attractors,
    FixedVector3 Uniform,
    FixedWorldGravityArea[] Areas
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
        Areas: []
    );
    /// <summary>Gets a value indicating whether the compilation declares an active global or bounded local field.</summary>
    public bool IsActive => (
        (Uniform != FixedVector3.Zero) ||
        (Areas.Length > 0) ||
        HasGlobalSolve
    );

    /// <summary>Compiles the authored section against the placements its attractors name.</summary>
    /// <param name="gravity">The authored section.</param>
    /// <param name="placements">The placement rows an attractor resolves its position from.</param>
    /// <returns>The compiled field; <see cref="Inert"/> when no global or bounded local field is declared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gravity"/> is <see langword="null"/>.</exception>
    public static FixedWorldGravity Compile(WorldGravity gravity, IReadOnlyList<WorldPlacement> placements) {
        ArgumentNullException.ThrowIfNull(gravity);

        if (!gravity.IsActive) {
            return Inert;
        }

        var uniform = FixedVector3.FromVector3(value: gravity.UniformAcceleration);
        var areas = CompileAreas(
            areas: gravity.Areas,
            placements: placements
        );

        var sourceCount = ((gravity.Attractors?.Count ?? 0) + (gravity.Points?.Count ?? 0));

        if (!(gravity.GravitationalConstant > 0f)) {
            return Inert with {
                Areas = areas,
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
            Areas: areas
        );
    }

    private static FixedWorldGravityArea[] CompileAreas(IReadOnlyList<WorldGravityArea>? areas, IReadOnlyList<WorldPlacement> placements) {
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
                    placement: placement
                )
            ) {
                continue;
            }

            compiled.Add(item: lowered);
        }

        compiled.Sort(comparison: static (left, right) => {
            var priority = left.Priority.CompareTo(value: right.Priority);

            return ((priority != 0)
                ? priority
                : left.AuthoredIndex.CompareTo(value: right.AuthoredIndex)
            );
        });

        return [.. compiled];
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
}
/// <summary>One fixed-point bounded gravity influence in deterministic evaluation order.</summary>
/// <param name="AuthoredIndex">The row's original index, used as the stable equal-priority tie-break.</param>
/// <param name="PlacementId">The placement the area rides.</param>
/// <param name="Priority">The authored priority; lower values apply first.</param>
/// <param name="Mode">The area's explicit composition mode.</param>
/// <param name="BoundsKind">The compiled analytic-bound kind.</param>
/// <param name="RadiusSquared">The scaled squared sphere radius, or zero for a box.</param>
/// <param name="HalfExtents">The scaled box half extents, or zero for a sphere.</param>
/// <param name="AccelerationKind">The compiled acceleration kind.</param>
/// <param name="LocalAcceleration">The placement-local directional vector, or zero for radial acceleration.</param>
/// <param name="RadialMagnitude">The radial magnitude, or zero for directional acceleration.</param>
/// <param name="AuthoredPosition">The static placement position used when <paramref name="Attach"/> is absent.</param>
/// <param name="AuthoredYawRadians">The static placement yaw used when <paramref name="Attach"/> is absent.</param>
/// <param name="Attach">The existing placement attachment facet, or null for a static area.</param>
public readonly record struct FixedWorldGravityArea(
    int AuthoredIndex,
    string PlacementId,
    int Priority,
    WorldGravityAreaMode Mode,
    FixedWorldGravityAreaBoundsKind BoundsKind,
    FixedQ4816 RadiusSquared,
    FixedVector3 HalfExtents,
    FixedWorldGravityAreaAccelerationKind AccelerationKind,
    FixedVector3 LocalAcceleration,
    FixedQ4816 RadialMagnitude,
    FixedVector3 AuthoredPosition,
    FixedQ4816 AuthoredYawRadians,
    WorldPlacementAttach? Attach
) {
    private static readonly FixedVector3 s_up = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

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

    /// <summary>Gets the area's authored static yaw rotation.</summary>
    public FixedQuaternion AuthoredRotation => FixedQuaternion.FromAxisAngle(
        angle: AuthoredYawRadians,
        axis: s_up
    );

    /// <summary>Returns whether a world-space point lies inside the analytic bound, including its boundary.</summary>
    public bool Contains(FixedVector3 point, FixedVector3 center, FixedQuaternion rotation) {
        var delta = (point - center);

        if (BoundsKind == FixedWorldGravityAreaBoundsKind.Sphere) {
            return (delta.TryLengthSquared(squaredLength: out var squaredDistance) && (squaredDistance <= RadiusSquared));
        }

        var local = rotation.Conjugate().Rotate(vector: delta);

        return (
            (local.X >= -HalfExtents.X) && (local.X <= HalfExtents.X) &&
            (local.Y >= -HalfExtents.Y) && (local.Y <= HalfExtents.Y) &&
            (local.Z >= -HalfExtents.Z) && (local.Z <= HalfExtents.Z)
        );
    }
    /// <summary>Evaluates the area's world-space acceleration at a point already known to match its bound.</summary>
    public FixedVector3 AccelerationAt(FixedVector3 point, FixedVector3 center, FixedQuaternion rotation) =>
        ((AccelerationKind == FixedWorldGravityAreaAccelerationKind.Directional)
            ? rotation.Rotate(vector: LocalAcceleration)
            : ((center - point).Normalize() * RadialMagnitude)
        );

    /// <summary>Attempts to lower an authored area through the fixed-point analytic evaluator.</summary>
    internal static bool TryCompile(WorldGravityArea area, WorldPlacement placement, int authoredIndex, out FixedWorldGravityArea compiled) {
        compiled = default;

        if (
            !Enum.IsDefined(value: area.Mode) ||
            !IsFixedRepresentable(value: placement.Position) ||
            !IsFixedRepresentable(value: placement.Scale) ||
            !(placement.Scale > 0f) ||
            !IsFixedRepresentable(value: placement.YawDegrees)
        ) {
            return false;
        }

        try {
            var scale = FixedQ4816.FromDouble(value: placement.Scale);
            var position = FixedVector3.FromVector3(value: placement.Position);
            var yaw = FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180.0)));
            var boundsKind = default(FixedWorldGravityAreaBoundsKind);
            var radiusSquared = FixedQ4816.Zero;
            var halfExtents = FixedVector3.Zero;

            switch (area.Bounds) {
                case WorldGravityAreaBounds.SphereBounds sphere: {
                        if (!IsFixedRepresentable(value: sphere.Radius) || !(sphere.Radius > 0f)) {
                            return false;
                        }

                        var radius = FixedQ4816.FromDouble(value: sphere.Radius);
                        var scaledRadius = checked((radius * scale));

                        if (scaledRadius <= FixedQ4816.Zero) {
                            return false;
                        }

                        radiusSquared = checked((scaledRadius * scaledRadius));
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

                        halfExtents = new FixedVector3(
                            X: checked((local.X * scale)),
                            Y: checked((local.Y * scale)),
                            Z: checked((local.Z * scale))
                        );
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
            var localAcceleration = FixedVector3.Zero;
            var radialMagnitude = FixedQ4816.Zero;

            switch (area.Acceleration) {
                case WorldGravityAreaAcceleration.Directional directional:
                    if (!IsFixedRepresentable(value: directional.Value)) {
                        return false;
                    }

                    localAcceleration = FixedVector3.FromVector3(value: directional.Value);
                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Directional;
                    break;
                case WorldGravityAreaAcceleration.Radial radial:
                    if (!IsFixedRepresentable(value: radial.Magnitude) || !(radial.Magnitude > 0f)) {
                        return false;
                    }

                    radialMagnitude = FixedQ4816.FromDouble(value: radial.Magnitude);
                    if (radialMagnitude <= FixedQ4816.Zero) {
                        return false;
                    }
                    accelerationKind = FixedWorldGravityAreaAccelerationKind.Radial;
                    break;
                default:
                    return false;
            }

            // Exercise the static rotation at compile time so a malformed fixed conversion is a validator refusal,
            // never a first-tick exception. Attached yaws use the same unit-quaternion operation at runtime.
            var rotation = FixedQuaternion.FromAxisAngle(
                angle: yaw,
                axis: s_up
            );

            _ = ((accelerationKind == FixedWorldGravityAreaAccelerationKind.Directional)
                ? rotation.Rotate(vector: localAcceleration)
                : FixedVector3.Zero
            );

            compiled = new FixedWorldGravityArea(
                AccelerationKind: accelerationKind,
                Attach: placement.Attach,
                AuthoredIndex: authoredIndex,
                AuthoredPosition: position,
                AuthoredYawRadians: yaw,
                BoundsKind: boundsKind,
                HalfExtents: halfExtents,
                LocalAcceleration: localAcceleration,
                Mode: area.Mode,
                PlacementId: area.PlacementId,
                Priority: area.Priority,
                RadialMagnitude: radialMagnitude,
                RadiusSquared: radiusSquared
            );

            return true;
        } catch (OverflowException) {
            compiled = default;

            return false;
        }
    }
}
