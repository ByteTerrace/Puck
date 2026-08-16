using Xunit;

using Puck.Maths;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the swept face-region seam (<see cref="WorldFaceRegion"/>), the crossing authority that
/// picks one winner per body (<see cref="WorldFaceCrossingClaim"/>), and the edge latch that turns a region answer
/// into a fired crossing (<see cref="WorldPortalOccupancy"/>). <c>Puck.World.WorldInstanceHost</c> — the composition
/// root that drives all three — is out of reach for this project (see README.md), so these laws prove the primitives
/// it orchestrates; the orchestration is verified by RUNNING <c>Puck.World</c> (CLAUDE.md rule 3).
/// </summary>
public sealed class WorldFaceRegionLawTests {
    private const double Depth = 0.5;
    private const double HalfHeight = 1.0;
    private const double HalfWidth = 2.0;

    private static FixedVector3 At(double x, double y, double z) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y),
        Z: FixedQ4816.FromDouble(value: z)
    );

    // A frame at the origin looking down world +Z, the shape every case below varies a body's segment against.
    private static WorldFaceFrame Frame { get; } = new(
        Origin: At(x: 0, y: 0, z: 0),
        Right: At(x: 1, y: 0, z: 0),
        Up: At(x: 0, y: 1, z: 0),
        Normal: At(x: 0, y: 0, z: 1),
        HalfWidth: FixedQ4816.FromDouble(value: HalfWidth),
        HalfHeight: FixedQ4816.FromDouble(value: HalfHeight),
        HalfDepth: FixedQ4816.FromDouble(value: 0.1)
    );
    private static WorldFaceAperture Aperture { get; } = new WorldFaceAperture.Box(Frame: Frame, Depth: FixedQ4816.FromDouble(value: Depth));

    private static WorldFaceCrossing Sweep(FixedVector3 from, FixedVector3 to) =>
        WorldFaceRegion.Sweep(aperture: Aperture, from: from, to: to);

    [Fact]
    public void DegenerateSegmentInsideTheBand_IsInsideAndCrossed() {
        var crossing = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));

        Assert.True(condition: crossing.Inside);
        Assert.True(condition: crossing.Crossed);
        Assert.Equal(expected: FixedQ4816.Zero, actual: crossing.Parameter);
    }
    [Fact]
    public void TheBandIsOneSided_TheBackOfADoorIsNotACrossing() {
        // The SAME distance from the face, on the other side of it. A two-sided band would make these agree, so
        // this pair is what discriminates the one-sided contract from a symmetric slab.
        var front = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));
        var back = Sweep(from: At(x: 0, y: 0, z: -0.25), to: At(x: 0, y: 0, z: -0.25));

        Assert.True(condition: front.Crossed);
        Assert.False(condition: back.Crossed);
        Assert.False(condition: back.Inside);
    }
    [Fact]
    public void OutsideTheFacesWidth_NeverCrosses() {
        var crossing = Sweep(from: At(x: (HalfWidth + 1.0), y: 0, z: 2), to: At(x: (HalfWidth + 1.0), y: 0, z: -2));

        Assert.False(condition: crossing.Crossed);
        Assert.False(condition: crossing.Inside);
    }
    [Fact]
    public void ASegmentPassingCleanThrough_CrossesWithoutEitherEndpointBeingInside() {
        // The tunnelling case the swept clip exists for: both endpoints sample outside the band, so a point test
        // alone reports nothing at all. Front (z=2) to back (z=-2): this is the FORWARD control for
        // BackToFrontTunnelling_MustNotFire below — the same shape of segment, the opposite direction.
        var crossing = Sweep(from: At(x: 0, y: 0, z: 2), to: At(x: 0, y: 0, z: -2));

        Assert.False(condition: crossing.Inside);
        Assert.True(condition: crossing.Crossed);
    }
    [Fact]
    public void BackToFrontTunnelling_MustNotFire() {
        // The exact reverse of ASegmentPassingCleanThrough_CrossesWithoutEitherEndpointBeingInside above: a body
        // starting BEHIND the face (negative alongNormal) sweeps forward through the band and out the front, both
        // endpoints outside the band exactly as the forward case is. The aperture is documented one-sided — "a door
        // fires from the side it faces" (WorldFaceAperture.Box.Depth) — so this direction must NOT fire even though
        // the segment visits [0, Depth] along the way; only an approach FROM the front is a legitimate entry.
        var behindToFront = Sweep(from: At(x: 0, y: 0, z: -2), to: At(x: 0, y: 0, z: 2));

        Assert.False(condition: behindToFront.Inside);
        Assert.False(condition: behindToFront.Crossed, userMessage: "a sweep from behind the face, through the band, and out the front is not a front-entry crossing");
        Assert.Equal(expected: FixedQ4816.Zero, actual: behindToFront.Parameter);

        // The forward direction over the identical band is the control that proves the gate is direction-aware, not
        // just broken.
        var frontToBehind = Sweep(from: At(x: 0, y: 0, z: 2), to: At(x: 0, y: 0, z: -2));

        Assert.True(condition: frontToBehind.Crossed, userMessage: "the forward direction over the same band must still fire");
    }
    [Fact]
    public void ABackSideSweepEndingInsideTheBand_LatchesOccupancyButDoesNotFire() {
        // Inside is the direction-free occupancy fact the latch needs, while Crossed is the one-sided firing edge.
        // A normal walking step from behind can end inside the band just as easily as a tunnelling step can pass
        // through it; neither may open a door whose face points the other way.
        var crossing = Sweep(from: At(x: 0, y: 0, z: -2), to: At(x: 0, y: 0, z: 0.25));

        Assert.True(condition: crossing.Inside);
        Assert.False(condition: crossing.Crossed);

        var frontControl = Sweep(from: At(x: 0, y: 0, z: 2), to: At(x: 0, y: 0, z: 0.25));

        Assert.True(condition: frontControl.Inside);
        Assert.True(condition: frontControl.Crossed);
    }
    [Fact]
    public void TheCrossingParameterIsTheEarliestMeetingPoint() {
        // Entering at z = Depth along a segment from z = 2 to z = -2 puts the meeting point at (2 - Depth) / 4.
        var crossing = Sweep(from: At(x: 0, y: 0, z: 2), to: At(x: 0, y: 0, z: -2));
        var expected = FixedQ4816.FromDouble(value: ((2.0 - Depth) / 4.0));

        Assert.True(condition: (FixedQ4816.Abs(value: (crossing.Parameter - expected)) <= FixedQ4816.FromDouble(value: 0.001)), userMessage: $"parameter {((double)crossing.Parameter)} is not the band entry {((double)expected)}");
    }
    [Fact]
    public void TheSeamIsReadAtTheCrossingParameter_NotAtEitherEndpoint() {
        // A DIAGONAL segment: its in-plane coordinates differ at t=0, at t=1, and at the crossing, so a seam read
        // from the wrong sample is visibly wrong rather than coincidentally right.
        var crossing = Sweep(from: At(x: -1, y: -0.5, z: 2), to: At(x: 1, y: 0.5, z: -2));
        var tolerance = FixedQ4816.FromDouble(value: 0.001);
        var parameter = ((double)crossing.Parameter);
        var expectedU = FixedQ4816.FromDouble(value: (-1.0 + (2.0 * parameter)));
        var expectedV = FixedQ4816.FromDouble(value: (-0.5 + (1.0 * parameter)));

        Assert.True(condition: crossing.Crossed);
        Assert.True(condition: (FixedQ4816.Abs(value: (crossing.SeamU - expectedU)) <= tolerance), userMessage: $"seam u {((double)crossing.SeamU)} != {((double)expectedU)}");
        Assert.True(condition: (FixedQ4816.Abs(value: (crossing.SeamV - expectedV)) <= tolerance), userMessage: $"seam v {((double)crossing.SeamV)} != {((double)expectedV)}");
        Assert.NotEqual(expected: crossing.SeamU, actual: FixedQ4816.FromDouble(value: -1.0));
        Assert.NotEqual(expected: crossing.SeamU, actual: FixedQ4816.One);
    }
    [Fact]
    public void TheAnswerCarriesTheFrameItWasMeasuredAgainst() {
        // The seam API must not assume a caller can reconstruct the frame: an arm whose frame varies along the
        // surface has no single frame to look up afterwards.
        Assert.Equal(expected: Frame, actual: Sweep(from: At(x: 0, y: 0, z: 2), to: At(x: 0, y: 0, z: -2)).Frame);
    }

    // ---- The edge latch ----

    private const string Face = "screen";
    private const string Placement = "door";

    private static bool Observe(WorldPortalOccupancy occupancy, WorldFaceCrossing crossing) =>
        occupancy.Observe(placementId: Placement, faceName: Face, seat: 0, inside: crossing.Inside, crossed: crossing.Crossed);

    [Fact]
    public void ACrossingFiresOnTheEdgeIn_AndOnceOnly() {
        var occupancy = new WorldPortalOccupancy();
        var inside = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));

        Assert.True(condition: Observe(crossing: inside, occupancy: occupancy));
        Assert.False(condition: Observe(crossing: inside, occupancy: occupancy));
    }
    [Fact]
    public void LeavingTheBandReArmsTheDoor() {
        var occupancy = new WorldPortalOccupancy();
        var inside = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));
        var outside = Sweep(from: At(x: 0, y: 0, z: 3), to: At(x: 0, y: 0, z: 3));

        Assert.True(condition: Observe(crossing: inside, occupancy: occupancy));
        Assert.False(condition: Observe(crossing: outside, occupancy: occupancy));
        Assert.True(condition: Observe(crossing: inside, occupancy: occupancy));
    }
    [Fact]
    public void ATunnellingCrossingFiresWithoutLatching() {
        // A body that passes fully through ends OUTSIDE, so it fires and leaves the door armed — a repeat tunnelling
        // crossing next step must fire again, exactly as a repeat walk-through would.
        var occupancy = new WorldPortalOccupancy();
        var through = Sweep(from: At(x: 0, y: 0, z: 2), to: At(x: 0, y: 0, z: -2));

        Assert.True(condition: Observe(crossing: through, occupancy: occupancy));
        Assert.False(condition: occupancy.IsInside(faceName: Face, placementId: Placement, seat: 0));
        Assert.True(condition: Observe(crossing: through, occupancy: occupancy));
    }
    [Fact]
    public void ArrivalSeedsOccupancy_SoAMappedPairCannotBounceTheTravelerBack() {
        // A traveler SET DOWN inside a door's band did not walk in. Without the seed its first scan there reads as a
        // fresh entry edge and fires the door it just came through; the control is the identical body with no seed.
        var arrived = new WorldPortalOccupancy();
        var walkedIn = new WorldPortalOccupancy();
        var standing = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));

        arrived.SeedInside(faceName: Face, placementId: Placement, seat: 0);

        Assert.False(condition: Observe(crossing: standing, occupancy: arrived), userMessage: "a seeded arrival must not fire the door it arrived at");
        Assert.True(condition: Observe(crossing: standing, occupancy: walkedIn), userMessage: "the control body, which walked in, must still fire");
    }
    [Fact]
    public void SeedingOneSeatDoesNotSilenceAnother() {
        var occupancy = new WorldPortalOccupancy();
        var standing = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));

        occupancy.SeedInside(faceName: Face, placementId: Placement, seat: 0);

        Assert.False(condition: occupancy.Observe(placementId: Placement, faceName: Face, seat: 0, inside: standing.Inside, crossed: standing.Crossed));
        Assert.True(condition: occupancy.Observe(placementId: Placement, faceName: Face, seat: 1, inside: standing.Inside, crossed: standing.Crossed));
    }
    [Fact]
    public void ForgettingASeatReArmsIt() {
        var occupancy = new WorldPortalOccupancy();
        var standing = Sweep(from: At(x: 0, y: 0, z: 0.25), to: At(x: 0, y: 0, z: 0.25));

        Assert.True(condition: Observe(crossing: standing, occupancy: occupancy));

        occupancy.Forget(faceName: Face, placementId: Placement, seat: 0);

        Assert.True(condition: Observe(crossing: standing, occupancy: occupancy));
    }

    // ---- The one-winner-per-body authority ----

    private static WorldFaceCrossingClaim Claim(string placement, string face, double parameter) =>
        new(PlacementId: placement, FaceName: face, Parameter: FixedQ4816.FromDouble(value: parameter));

    [Fact]
    public void TheEarliestCrossingWins_RegardlessOfWhichFaceWasScannedFirst() {
        var early = Claim(face: "screen", parameter: 0.25, placement: "zeta");
        var late = Claim(face: "screen", parameter: 0.75, placement: "alpha");

        // The later claim names an alphabetically EARLIER placement, so a comparison that fell through to identity
        // first would pick the wrong one.
        Assert.True(condition: early.Outranks(other: late));
        Assert.False(condition: late.Outranks(other: early));
    }
    [Fact]
    public void EqualParameters_TieBreakOnStablePlacementThenFaceIdentity() {
        var alphaFirst = Claim(face: "b", parameter: 0.5, placement: "alpha");
        var alphaSecond = Claim(face: "c", parameter: 0.5, placement: "alpha");
        var zeta = Claim(face: "a", parameter: 0.5, placement: "zeta");

        Assert.True(condition: alphaFirst.Outranks(other: zeta));
        Assert.True(condition: alphaFirst.Outranks(other: alphaSecond));
        Assert.False(condition: alphaSecond.Outranks(other: alphaFirst));
    }
    [Fact]
    public void TheOrderingIsTotalAndAntisymmetric() {
        var claims = new[] {
            Claim(face: "y", parameter: 0.5, placement: "b"),
            Claim(face: "z", parameter: 0.5, placement: "a"),
            Claim(face: "y", parameter: 0.5, placement: "a"),
            Claim(face: "x", parameter: 0.1, placement: "c"),
        };

        foreach (var left in claims) {
            Assert.False(condition: left.Outranks(other: left));

            foreach (var right in claims) {
                if (left == right) {
                    continue;
                }

                Assert.True(condition: (left.Outranks(other: right) != right.Outranks(other: left)), userMessage: $"{left} vs {right} is not antisymmetric");
            }
        }
    }
}
