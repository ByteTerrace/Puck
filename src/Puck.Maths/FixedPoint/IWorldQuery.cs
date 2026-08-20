
namespace Puck.Maths;

/// <summary>
/// How much a query answer should be trusted: whether it came from an exact evaluator or a coarsened baked
/// approximation. Both are deterministic (bit-identical for the same inputs) — this is NOT a determinism signal,
/// it is a FIDELITY signal, so a caller can decide whether "probably right" is good enough for the decision it's
/// making (an RTS ground-snap can live with <see cref="Bounded"/>; a competitive hitscan might not).
/// </summary>
public enum WorldQueryConfidence {
    /// <summary>The answer is conservative rather than measured: a baked, resolution-quantized artifact (see the
    /// <c>Puck.SignedDistance.Queries</c> namespace remarks) — sign-correct and conservatively dilated, but not
    /// sub-cell-exact — or an exact evaluator's march that ran out of iterations and resolved to the answer its verb
    /// can survive being wrong about, at the last point it reached.</summary>
    Bounded = 0,
    /// <summary>The answer came from a fixed-point evaluator against the live SDF program, and the march that produced
    /// it converged.</summary>
    Exact = 1,
}
/// <summary>
/// One raycast/spherecast hit — fully fixed-point, so a hit result can feed straight back into deterministic sim
/// state without a float round-trip.
/// </summary>
/// <param name="Point">The world-space contact point on the touched geometry. For a sphere cast this is the surface
/// the swept sphere first touches, which sits up to one radius off the sweeping center's own path.</param>
/// <param name="Normal">The surface normal at the hit point — per-provider, not a shared contract. A
/// <see cref="WorldQueryConfidence.Bounded"/> answer (<c>BakedWorldQuery</c>) hardcodes constant world
/// <c>+Y</c> — a heightfield's only honest answer, since the baked artifact carries no per-hit surface orientation.
/// A <see cref="WorldQueryConfidence.Exact"/> answer (<c>SdfFieldEvaluator</c>'s march) is always
/// <see cref="FixedVector3.Zero"/>, deliberately NOT computed: no consumer reads it today (<c>WorldSolidField.ResolveCore</c>
/// consumes only <c>RayHit.Distance</c>), so spending a gradient tap on every march hit was pure waste. A future
/// consumer that needs it should call <see cref="IFieldEvaluator.TryFieldGradient(FixedPosition, out FixedVector3)"/>
/// at <see cref="Point"/> itself.</param>
/// <param name="Distance">How far the query travelled along the cast direction before contact. For a sphere cast
/// that is how far the sphere's CENTER moved, not the distance to <see cref="Point"/>; the two coincide only for a
/// ray. Adding <c>Distance</c> to the origin along the direction gives the sphere's resting center.</param>
/// <param name="Material">The hit surface's material id, or -1 when the provider doesn't track materials (the baked provider today).</param>
/// <param name="Confidence">How exact the hit is — see <see cref="WorldQueryConfidence"/>.</param>
public readonly record struct RayHit(FixedPosition Point, FixedVector3 Normal, FixedQ4816 Distance, int Material, WorldQueryConfidence Confidence);
/// <summary>
/// Describes the query layers available from an <see cref="IWorldQuery"/> provider. A provider reports a layer
/// present only when that layer can answer, so an allocated but empty layer reports absent. Ray and line-of-sight
/// queries consult every present layer: a baked artifact that omits the occupancy grid still answers them from the
/// 2.5D heightfield, and one that omits the heightfield still answers them from the blocked grid. Check these
/// capabilities once when binding the provider rather than before every query.
/// <para>
/// Where the layers are 2D grids (<c>BakedWorldQuery</c>), Y resolves differently per layer. A blocked cell carries
/// no height and blocks at every Y — an infinite vertical column. The heightfield blocks where the query's lowest
/// point reaches at or below the cell's authored ground, so a line of sight is blocked when it grazes terrain, not
/// only when it dips beneath it; a caller wanting eye-to-eye visibility passes eye-height endpoints.
/// </para>
/// </summary>
/// <param name="HasHeightfield">Whether <see cref="IWorldQuery.TryGroundHeight"/> can answer.</param>
/// <param name="HasBlocked">Whether the 2D blocked-cell layer (walk-grid-shaped) is present.</param>
/// <param name="HasOccupancy">Whether a coarse 3D occupancy grid is present.</param>
public readonly record struct QueryCapabilities(bool HasHeightfield, bool HasBlocked, bool HasOccupancy);
/// <summary>
/// Provides synchronous, deterministic gameplay queries against an SDF world. Inputs and results use
/// <see cref="FixedQ4816"/>, <see cref="FixedVector3"/>, and <see cref="FixedPosition"/> so they can participate in
/// simulation without a floating-point round trip. Direction arguments need not be normalized.
/// <para>
/// <c>BakedWorldQuery</c> serves quantized <c>WorldQueryArtifact</c> data and returns
/// <see cref="WorldQueryConfidence.Bounded"/> answers. <c>SdfFieldEvaluator</c> interprets a supported subset
/// of the live <c>SdfProgram</c> in fixed point and returns <see cref="WorldQueryConfidence.Exact"/> answers.
/// These providers are deterministic but are not expected to match the floating-point GPU renderer bit for bit.
/// </para>
/// <para>
/// The cast and visibility verbs are CONSERVATIVE where they cannot decide: a provider that runs out of its own
/// iteration budget mid-cast must resolve toward the answer a contact, sweep, or visibility consumer can survive being
/// wrong about — an obstruction, marked <see cref="WorldQueryConfidence.Bounded"/> — never toward "clear". Every verb
/// here is read by authoritative simulation, where a false "nothing there" changes state.
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
    bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit);
    /// <summary>Sweeps a sphere of <paramref name="radius"/> from <paramref name="origin"/> along <paramref name="dir"/>
    /// up to <paramref name="maxDist"/>, returning the nearest hit (the swept sphere's first point of contact).</summary>
    /// <param name="origin">The sphere's starting center.</param>
    /// <param name="dir">The sweep direction (normalized internally).</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <param name="maxDist">The maximum sweep distance.</param>
    /// <param name="hit">The nearest hit, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the swept sphere hit something within <paramref name="maxDist"/>.</returns>
    bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit);
    /// <summary>Returns a value indicating whether a sphere of <paramref name="radius"/> centered at <paramref name="center"/> overlaps blocked
    /// geometry — a placement/spawn/selection check, not a cast.</summary>
    /// <param name="center">The sphere's center.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns><see langword="true"/> when the sphere overlaps something blocked.</returns>
    bool Overlap(FixedPosition center, FixedQ4816 radius);
    /// <summary>Finds the ground height directly beneath (or above) <paramref name="position"/>, searching from
    /// <paramref name="probeUp"/> above to <paramref name="probeDown"/> below its Y.</summary>
    /// <param name="position">The XZ to probe (its own Y is the probe's center).</param>
    /// <param name="probeUp">How far above <paramref name="position"/>.Y to search.</param>
    /// <param name="probeDown">How far below <paramref name="position"/>.Y to search.</param>
    /// <param name="groundY">The ground height, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when ground was found within the probe range.</returns>
    bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY);
    /// <summary>Returns a value indicating whether a straight line from <paramref name="from"/> to <paramref name="to"/> is unobstructed.</summary>
    /// <param name="from">The line's start point.</param>
    /// <param name="to">The line's end point.</param>
    /// <returns><see langword="true"/> when nothing blocked lies between the two points.</returns>
    bool LineOfSight(FixedPosition from, FixedPosition to);
}
