using Puck.Maths;

namespace Puck.SdfVm.Queries;

/// <summary>
/// How much a query answer should be trusted: whether it came from an exact evaluator or a coarsened baked
/// approximation. Both are deterministic (bit-identical for the same inputs) — this is NOT a determinism signal,
/// it is a FIDELITY signal, so a caller can decide whether "probably right" is good enough for the decision it's
/// making (an RTS ground-snap can live with <see cref="Bounded"/>; a competitive hitscan might not).
/// </summary>
public enum WorldQueryConfidence {
    /// <summary>The answer came from a baked, resolution-quantized artifact (see the <c>Puck.SdfVm.Queries</c>
    /// namespace remarks) — sign-correct and conservatively dilated, but not sub-cell-exact.</summary>
    Bounded = 0,
    /// <summary>The answer came from a fixed-point evaluator against the live SDF program.</summary>
    Exact = 1,
}

/// <summary>
/// One raycast/spherecast hit — fully fixed-point, so a hit result can feed straight back into deterministic sim
/// state without a float round-trip.
/// </summary>
/// <param name="Point">The world-space hit point.</param>
/// <param name="Normal">The surface normal at the hit point (not necessarily unit length for a <see cref="WorldQueryConfidence.Bounded"/> answer — see the provider's own remarks).</param>
/// <param name="Distance">The distance from the query origin to <see cref="Point"/>, along the cast direction.</param>
/// <param name="Material">The hit surface's material id, or -1 when the provider doesn't track materials (the baked provider today).</param>
/// <param name="Confidence">How exact the hit is — see <see cref="WorldQueryConfidence"/>.</param>
public readonly record struct RayHit(WorldCoord3 Point, FixedVector3 Normal, FixedQ4816 Distance, int Material, WorldQueryConfidence Confidence);

/// <summary>
/// Describes the query layers available from an <see cref="IWorldQuery"/> provider. A baked artifact may omit a
/// layer, such as the occupancy grid, in which case ray and line-of-sight queries use the 2.5D heightfield. Check
/// these capabilities once when binding the provider rather than before every query.
/// </summary>
/// <param name="HasHeightfield">Whether <see cref="IWorldQuery.TryGroundHeight"/> can answer.</param>
/// <param name="HasBlocked">Whether the 2D blocked-cell layer (walk-grid-shaped) is present.</param>
/// <param name="HasOccupancy">Whether a coarse 3D occupancy grid is present.</param>
public readonly record struct QueryCapabilities(bool HasHeightfield, bool HasBlocked, bool HasOccupancy);

/// <summary>
/// Provides synchronous, deterministic gameplay queries against an SDF world. Inputs and results use
/// <see cref="FixedQ4816"/>, <see cref="FixedVector3"/>, and <see cref="WorldCoord3"/> so they can participate in
/// simulation without a floating-point round trip. Direction arguments need not be normalized.
/// <para>
/// <see cref="BakedWorldQuery"/> serves quantized <see cref="WorldQueryArtifact"/> data and returns
/// <see cref="WorldQueryConfidence.Bounded"/> answers. <see cref="SdfFieldEvaluator"/> interprets a supported subset
/// of the live <see cref="SdfProgram"/> in fixed point and returns <see cref="WorldQueryConfidence.Exact"/> answers.
/// These providers are deterministic but are not expected to match the floating-point GPU renderer bit for bit.
/// </para>
/// </summary>
public interface IWorldQuery {
    /// <summary>What this provider can answer — check once, not per query.</summary>
    QueryCapabilities Capabilities { get; }

    /// <summary>Casts a ray from <paramref name="origin"/> along <paramref name="dir"/> (need not be unit length) up
    /// to <paramref name="maxDist"/>, returning the nearest hit.</summary>
    /// <param name="origin">The ray's world-space origin.</param>
    /// <param name="dir">The ray's direction (normalized internally).</param>
    /// <param name="maxDist">The maximum distance to search.</param>
    /// <param name="hit">The nearest hit, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the ray hit something within <paramref name="maxDist"/>.</returns>
    bool Raycast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit);

    /// <summary>Sweeps a sphere of <paramref name="radius"/> from <paramref name="origin"/> along <paramref name="dir"/>
    /// up to <paramref name="maxDist"/>, returning the nearest hit (the swept sphere's first point of contact).</summary>
    /// <param name="origin">The sphere's starting center.</param>
    /// <param name="dir">The sweep direction (normalized internally).</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <param name="maxDist">The maximum sweep distance.</param>
    /// <param name="hit">The nearest hit, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the swept sphere hit something within <paramref name="maxDist"/>.</returns>
    bool SphereCast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit);

    /// <summary>Whether a sphere of <paramref name="radius"/> centered at <paramref name="center"/> overlaps blocked
    /// geometry — a placement/spawn/selection check, not a cast.</summary>
    /// <param name="center">The sphere's center.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns><see langword="true"/> when the sphere overlaps something blocked.</returns>
    bool Overlap(WorldCoord3 center, FixedQ4816 radius);

    /// <summary>Finds the ground height directly beneath (or above) <paramref name="position"/>, searching from
    /// <paramref name="probeUp"/> above to <paramref name="probeDown"/> below its Y.</summary>
    /// <param name="position">The XZ to probe (its own Y is the probe's center).</param>
    /// <param name="probeUp">How far above <paramref name="position"/>.Y to search.</param>
    /// <param name="probeDown">How far below <paramref name="position"/>.Y to search.</param>
    /// <param name="groundY">The ground height, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when ground was found within the probe range.</returns>
    bool TryGroundHeight(WorldCoord3 position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY);

    /// <summary>Whether a straight line from <paramref name="from"/> to <paramref name="to"/> is unobstructed.</summary>
    /// <param name="from">The line's start point.</param>
    /// <param name="to">The line's end point.</param>
    /// <returns><see langword="true"/> when nothing blocked lies between the two points.</returns>
    bool LineOfSight(WorldCoord3 from, WorldCoord3 to);
}
