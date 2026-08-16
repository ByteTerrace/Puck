using System.Numerics;
using Xunit;

using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldWindowProjectionMath.MapPoint"/> — the border-window's eye-mapping isometry
/// must agree with <see cref="WorldFrameIsometry.MapPoint"/>'s own position mapping for the SAME (source frame,
/// destination frame, point) triple. That correspondence IS the definition of "the window shows where the
/// door goes": a window is presentation math (float, GPU-free) computing "what an arrived traveler would see,"
/// while <see cref="WorldFrameIsometry"/> is simulation math (fixed-point) computing where a traveler who
/// actually crosses ends up — two independent implementations of the identical isometry, on two independent number
/// systems, that must land on the same point (<c>WorldFrameIsometryLawTests</c> carries the simulation side's own
/// contract).
/// </summary>
/// <remarks>
/// A reflection bug (flip only the Normal component, not Right) passed the visual "does a window show *something*
/// plausible" check this suite exists to make impossible to miss again: a mirrored destination still parallaxes as
/// eye moves, still fits the frustum's depth guard, still renders a picture — it is simply the WRONG picture,
/// laterally flipped. Only a numeric cross-check against an independent isometry (here, the fixed-point sibling) can
/// catch that a rendered image alone cannot.
/// </remarks>
public sealed class WorldWindowProjectionMathLawTests {
    private static FixedQ4816 DegreesToRadians(double degrees) => FixedQ4816.FromDouble(value: (degrees * (Math.PI / 180.0)));
    // Builds the SAME yaw-only, world-up-rotated (Right, Up, Normal) triad WorldFaceCatalog.DeriveFrame derives for
    // an un-rotated (identity shape) face: Right = R(yaw)*+X, Up = +Y always, Normal = R(yaw)*+Z. Float, mirroring
    // WorldFaceGeometry's own presentation-float contract — this suite proves the ISOMETRY, not the fixed-to-float
    // boundary (WorldFaceGeometry.FromFrame, exercised by running Puck.World).
    private static WorldFaceGeometry BuildFaceGeometry(Vector3 origin, float yawDegrees, float halfWidth = 2f, float halfHeight = 2f) {
        var yawRadians = (yawDegrees * (MathF.PI / 180f));
        var cos = MathF.Cos(x: yawRadians);
        var sin = MathF.Sin(x: yawRadians);

        return new WorldFaceGeometry(
            Origin: origin,
            Right: new Vector3(x: cos, y: 0f, z: -sin),
            Up: Vector3.UnitY,
            Normal: new Vector3(x: sin, y: 0f, z: cos),
            HalfWidth: halfWidth,
            HalfHeight: halfHeight
        );
    }
    private static FixedVector3 ToFixed(Vector3 value) => FixedVector3.FromVector3(value: value);
    private static WorldFaceFrame ToFrame(WorldFaceGeometry geometry) => new(
        Origin: ToFixed(value: geometry.Origin),
        Right: ToFixed(value: geometry.Right),
        Up: ToFixed(value: geometry.Up),
        Normal: ToFixed(value: geometry.Normal),
        HalfWidth: FixedQ4816.FromDouble(value: geometry.HalfWidth),
        HalfHeight: FixedQ4816.FromDouble(value: geometry.HalfHeight),
        HalfDepth: FixedQ4816.Zero
    );

    [Fact]
    public void MapPoint_AsymmetricPair_AgreesWithWorldPortalArrivalMathsPositionMapping() {
        // THE DISCRIMINATING CASE (matches the adversarial-review finding's own proof shape): source yaw -90 degrees,
        // destination yaw +90 degrees, an eye NOT on the door's own centerline (u != 0) and NOT level with it (v != 0)
        // in three dimensions, so every one of a reflection bug's fingerprints (a mirrored lateral component, a
        // dropped vertical offset, a wrong-sign depth) shows up as a visible failure rather than a coincidental pass.
        var sourceOrigin = new Vector3(x: -4f, y: 1.5f, z: 2f);
        var destinationOrigin = new Vector3(x: 6f, y: 1.5f, z: -3f);
        var source = BuildFaceGeometry(origin: sourceOrigin, yawDegrees: -90f);
        var destination = BuildFaceGeometry(origin: destinationOrigin, yawDegrees: 90f);

        // An eye offset from the source face that is NOT the identity-border special case (source/destination
        // sharing one origin) — chosen so u, v, and n are all distinct nonzero values.
        var eye = new Vector3(x: -4f, y: 2.6f, z: 7f);

        var mappedFloat = WorldWindowProjectionMath.MapPoint(destination: destination, point: eye, source: source);

        // The SAME isometry, independently computed in fixed point through WorldFrameIsometry, over the identical
        // (sourceOrigin, sourceYaw) -> (destinationOrigin, destinationYaw) pair.
        var sourceFrame = ToFrame(geometry: source);
        var destinationFrame = ToFrame(geometry: destination);
        var mappedFromArrival = WorldFrameIsometry.MapPoint(point: ToFixed(value: eye), source: in sourceFrame, destination: in destinationFrame).ToVector3();

        // A generous but real tolerance: WorldWindowProjectionMath computes entirely in float32 (MathF.Cos/Sin),
        // ComputeArrival entirely in FixedQ4816 (its own SinCos, up to ~0.51 ULP per its documented remarks) — two
        // independent number systems computing the SAME irrational trig, not the same bits. 1e-3 world units is
        // three orders of magnitude tighter than the ~5-unit-scale reflection error this law exists to catch (a
        // mirrored X/Z would miss by ~2x the eye's own offset from the door, here several world units), so it proves
        // agreement on the ISOMETRY, not floating-point identity.
        const float tolerance = 1e-3f;

        Assert.True(condition: (Vector3.Distance(value1: mappedFloat, value2: mappedFromArrival) < tolerance), userMessage: $"MapPoint={mappedFloat} vs ComputeArrival.Position={mappedFromArrival} (source yaw -90, destination yaw 90) — a reflection bug mirrors this by several world units, far past the {tolerance} tolerance.");

        // The reflection-bug fingerprint, named directly: a Normal-only flip (u,v,n) -> (u,v,-n) instead of the full
        // (-u,v,-n) — reconstructed here from the SAME source/destination bases this test already built, so a
        // regression back to that formula fails LOUD rather than by a silent tolerance miss.
        var offset = (eye - source.Origin);
        var u = Vector3.Dot(vector1: offset, vector2: source.Right);
        var v = Vector3.Dot(vector1: offset, vector2: source.Up);
        var n = Vector3.Dot(vector1: offset, vector2: source.Normal);
        var reflectedWrong = (((destination.Origin + (u * destination.Right)) + (v * destination.Up)) - (n * destination.Normal));

        Assert.True(condition: (Vector3.Distance(value1: mappedFloat, value2: reflectedWrong) > 1f), userMessage: $"MapPoint={mappedFloat} matches the KNOWN-WRONG Normal-only-flip reflection {reflectedWrong} — the regression this law exists to catch.");
    }
    [Fact]
    public void MapPoint_IdentityBorder_ReturnsTheSameWorldPoint() {
        // The identity-border control (the adversarial-review finding's own hand-computed proof, reproduced as a
        // law): source yaw -90, destination yaw +90, SAME origin for both frames. destYaw - srcYaw + 180 = 360 = 0
        // (mod 360), so WorldPortalArrivalMath's own deltaYaw collapses to an exact rotation identity and a
        // traveler's arrival position equals their departure position exactly — the one case simple enough to
        // verify by hand rather than only by cross-checking against ComputeArrival, so this law does not depend
        // entirely on trusting the sibling suite it also compares against above.
        var origin = new Vector3(x: 2f, y: 1f, z: -5f);
        var source = BuildFaceGeometry(origin: origin, yawDegrees: -90f);
        var destination = BuildFaceGeometry(origin: origin, yawDegrees: 90f);
        var eye = new Vector3(x: -1f, y: 2f, z: 0f); // offset (-3, 1, 5) from origin: u=5, v=1, n=3 (all nonzero).

        var mapped = WorldWindowProjectionMath.MapPoint(destination: destination, point: eye, source: source);

        Assert.True(condition: (Vector3.Distance(value1: mapped, value2: eye) < 1e-4f), userMessage: $"identity-border MapPoint={mapped} should reproduce the original eye {eye} exactly (deltaYaw collapses to 0 for this yaw pair) — a Normal-only flip instead mirrors it laterally.");
    }
}
