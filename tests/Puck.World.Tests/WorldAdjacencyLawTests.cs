using System.Numerics;
using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for invisible reciprocal ownership boundaries: exact cardinal frames, outward swept handoff,
/// compiler-owned symmetric overlap, and cross-document reciprocal proof.</summary>
public sealed class WorldAdjacencyLawTests {
    [Fact]
    public void CardinalFramesKeepExactAxes() {
        var east = Boundary(yaw: 90f).CompileFrame();
        var west = Boundary(yaw: -90f).CompileFrame();

        Assert.Equal(new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), east.Normal);
        Assert.Equal(new FixedVector3(X: -FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), west.Normal);
        Assert.Equal(-east.Right, west.Right);
    }
    [Fact]
    public void CardinalPitchKeepsExactAxesAndVerticalPairIsIdentity() {
        var up = Boundary(pitch: 90f, yaw: 0f).CompileFrame();
        var down = Boundary(pitch: -90f, yaw: 180f).CompileFrame();
        var worldUp = Fixed(x: 0, y: 1, z: 0);
        var probe = Fixed(x: 3, y: 7, z: -2);

        Assert.Equal(worldUp, up.Normal);
        Assert.Equal(-worldUp, down.Normal);
        Assert.Equal(probe, WorldFrameIsometry.MapVector(value: probe, source: up, destination: down));
    }
    [Fact]
    public void HorizontalBoundarySweepsUpward() {
        var frame = Boundary(pitch: 90f, yaw: 0f).CompileFrame();
        var crossing = WorldAdjacencyRegion.Sweep(frame, Fixed(x: 0, y: -1, z: 0), Fixed(x: 0, y: 1, z: 0));

        Assert.True(crossing.Crossed);
        Assert.Equal(FixedQ4816.FromDouble(value: 0.5), crossing.Parameter);
    }
    [Fact]
    public void SweepOnlyHandsOffOutwardThroughRectangle() {
        var frame = Boundary(yaw: 90f).CompileFrame();
        var outward = WorldAdjacencyRegion.Sweep(frame, Fixed(x: -1, y: 0, z: 0), Fixed(x: 1, y: 0, z: 0));
        var inward = WorldAdjacencyRegion.Sweep(frame, Fixed(x: 1, y: 0, z: 0), Fixed(x: -1, y: 0, z: 0));
        var above = WorldAdjacencyRegion.Sweep(frame, Fixed(x: -1, y: 20, z: 0), Fixed(x: 1, y: 20, z: 0));

        Assert.True(outward.Crossed);
        Assert.Equal(FixedQ4816.FromDouble(value: 0.5), outward.Parameter);
        Assert.False(inward.Crossed);
        Assert.False(above.Crossed);
    }
    [Fact]
    public void OverlapIsSymmetricAndPositive() {
        var first = Fixtures.BuildDocument();
        var second = Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 30) };

        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(first, second, out var forward, out var forwardReason), forwardReason);
        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(second, first, out var reverse, out var reverseReason), reverseReason);
        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(first, out var firstHysteresis, out var firstReason), firstReason);
        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(second, out var secondHysteresis, out var secondReason), secondReason);
        Assert.Equal(forward, reverse);
        Assert.True(condition: (forward > FixedQ4816.Zero));
        Assert.True(condition: (forward >= firstHysteresis));
        Assert.True(condition: (forward >= secondHysteresis));
    }
    [Fact]
    public void ReciprocalHysteresisFormsAClosedOwnershipDeadbandForRapidReversal() {
        var source = Boundary(yaw: 90f).CompileFrame();
        var destination = Boundary(yaw: -90f).CompileFrame();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);
        var settle = FixedQ4816.FromDouble(value: 0.02);
        var sourceThreshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in source, reciprocalHysteresis: hysteresis, verticalSettleDeadband: settle);
        var destinationThreshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in destination, reciprocalHysteresis: hysteresis, verticalSettleDeadband: settle);

        var seamZig = WorldAdjacencyRegion.Sweep(
            frame: source,
            from: Fixed(x: -0.1, y: 0, z: 0),
            to: Fixed(x: 0.1, y: 0, z: 0),
            outwardThreshold: sourceThreshold);
        var sourceExit = WorldAdjacencyRegion.Sweep(
            frame: source,
            from: Fixed(x: 0.7, y: 0, z: 0),
            to: Fixed(x: 0.8, y: 0, z: 0),
            outwardThreshold: sourceThreshold);
        var destinationNearReturn = WorldAdjacencyRegion.Sweep(
            frame: destination,
            from: Fixed(x: 0.8, y: 0, z: 0),
            to: Fixed(x: -0.1, y: 0, z: 0),
            outwardThreshold: destinationThreshold);
        var destinationExit = WorldAdjacencyRegion.Sweep(
            frame: destination,
            from: Fixed(x: 0.8, y: 0, z: 0),
            to: Fixed(x: -0.8, y: 0, z: 0),
            outwardThreshold: destinationThreshold);

        Assert.Equal(hysteresis, sourceThreshold);
        Assert.Equal(hysteresis, destinationThreshold);
        Assert.False(seamZig.Crossed);
        Assert.True(sourceExit.Crossed);
        Assert.False(destinationNearReturn.Crossed);
        Assert.True(destinationExit.Crossed);
    }
    [Fact]
    public void YawOnlyHysteresisClosesTheDiagonalCornerBetweenPerpendicularBoundaries() {
        var east = new WorldAdjacencyBoundary(
            Center: new Vector3(x: 0f, y: 0f, z: -12f),
            OutwardYawDegrees: 90f,
            OutwardPitchDegrees: 0f,
            Width: 24f,
            Height: 16f).CompileFrame();
        var south = new WorldAdjacencyBoundary(
            Center: new Vector3(x: -12f, y: 0f, z: 0f),
            OutwardYawDegrees: 0f,
            OutwardPitchDegrees: 0f,
            Width: 24f,
            Height: 16f).CompileFrame();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);
        var from = Fixed(x: 0.7, y: 0.05, z: 0.7);
        var to = Fixed(x: 0.8, y: 0.05, z: 0.8);

        var eastCrossing = WorldAdjacencyRegion.Sweep(frame: east, from: from, to: to, outwardThreshold: hysteresis);
        var southCrossing = WorldAdjacencyRegion.Sweep(frame: south, from: from, to: to, outwardThreshold: hysteresis);

        Assert.True(eastCrossing.Crossed, userMessage: "the east ownership face left the expanded southeast corner unclaimed");
        Assert.True(southCrossing.Crossed, userMessage: "the south ownership face left the expanded southeast corner unclaimed");
        Assert.Equal(eastCrossing.Parameter, southCrossing.Parameter);
    }
    [Fact]
    public void ContactApertureIsNeverNarrowerThanTheOwnershipApertureItClaims() {
        var east = CornerBoundary(center: new Vector3(x: 0f, y: 0f, z: -12f), yaw: 90f).CompileFrame();
        var south = CornerBoundary(center: new Vector3(x: -12f, y: 0f, z: 0f), yaw: 0f).CompileFrame();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);
        var depth = FixedQ4816.FromDouble(value: 3.0172);
        var claimed = Fixed(x: 0.72, y: 0.05, z: 0.72);
        var beyondAperture = Fixed(x: 0.72, y: 0.05, z: 4);

        var eastBand = new WorldAdjacencyBand(Name: "east", Frame: east);
        var southBand = new WorldAdjacencyBand(Name: "south", Frame: south);
        var handoff = WorldAdjacencyRegion.Sweep(frame: east, from: Fixed(x: 0.7, y: 0.05, z: 0.7), to: Fixed(x: 0.75, y: 0.05, z: 0.75), outwardThreshold: hysteresis);

        // The corner of the expanded ownership rectangle: the farthest in-plane point this face still hands over.
        Assert.True(handoff.Crossed);
        Assert.Equal(expected: -(east.HalfWidth + hysteresis), actual: handoff.SeamU);
        Assert.True(eastBand.Contains(depth: depth, ownershipThreshold: hysteresis, position: claimed),
            userMessage: "the east contact aperture refused a point its own ownership face hands over");
        Assert.True(southBand.Contains(depth: depth, ownershipThreshold: hysteresis, position: claimed),
            userMessage: "the south contact aperture refused a point its own ownership face hands over");

        // The aperture is widened by the ownership threshold, not abolished: a face still refuses to answer for a
        // point four metres past the end of its own authored rectangle, where the diagonal peer's own path applies.
        Assert.False(condition: eastBand.Contains(depth: depth, ownershipThreshold: hysteresis, position: beyondAperture));
        Assert.True(eastBand.Transits(depth: depth, position: beyondAperture),
            userMessage: "a corner path could not transport past the intermediate face's aperture");

        // Outward is unbounded: a step overshoots the threshold before the ownership scan runs, and a handoff takes
        // ticks to drain. A body in that window is standing on the neighbour's floor, so the neighbour must be asked.
        Assert.True(eastBand.Contains(position: Fixed(x: 40, y: 0.05, z: -12), depth: depth, ownershipThreshold: hysteresis),
            userMessage: "contact stopped at a fixed distance outward, where nothing else answers");
        Assert.False(eastBand.Contains(position: Fixed(x: -40, y: 0.05, z: -12), depth: depth, ownershipThreshold: hysteresis),
            userMessage: "the owned side is bounded by the derived depth");
    }

    private static WorldAdjacencyBoundary CornerBoundary(Vector3 center, float yaw) => new(
        Center: center,
        OutwardYawDegrees: yaw,
        OutwardPitchDegrees: 0f,
        Width: 24f,
        Height: 16f
    );

    [Fact]
    public void FloorAdjacencyCarriesTheSettleDeadbandNotTheContactHysteresis() {
        var up = Boundary(pitch: 90f, yaw: 0f).CompileFrame();
        var definition = Fixtures.BuildDocument();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);

        Assert.True(WorldAdjacencyPolicy.TryVerticalSettleDeadband(definition, out var settle, out var reason), reason);

        var threshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in up, reciprocalHysteresis: hysteresis, verticalSettleDeadband: settle);

        Assert.Equal(settle, threshold);
        // Ascent headroom: the deadband a vertical crossing delays handoff by must stay far below the boundary's own
        // aperture, which the two-body contact hysteresis a wall carries would not.
        Assert.True(condition: (threshold < hysteresis));
        Assert.True(condition: (threshold < (up.HalfHeight / FixedQ4816.FromInteger(value: 100))));
    }
    [Fact]
    public void VerticalSettleDeadbandExceedsOneStepOfFreeFallPlusContactSkin() {
        var definition = Fixtures.BuildDocument();

        Assert.True(WorldAdjacencyPolicy.TryVerticalSettleDeadband(definition, out var settle, out var reason), reason);

        // The document's own envelope, recomputed independently in double precision: the grounded arm's fall gravity
        // over one authority step is the speed a body at rest reaches, and that speed over one more step is the sag.
        var rate = ((double)definition.SimulationRateHz);
        var sag = (Math.Min(val1: 20.0, val2: (23.0 / rate)) / rate);
        var skin = ((double)definition.Collision.ContactSkin);

        Assert.True((((double)settle) > (sag + skin)), userMessage: $"deadband {((double)settle)} does not exceed one step of sag {sag} plus skin {skin}");
        // Bounded above: outward rounding and one raw unit, never a term this derivation does not name.
        Assert.True(condition: (((double)settle) < ((sag + skin) + 0.001)));
    }
    [Fact]
    public void FloorOwnershipDeadbandHoldsASettlingArrivalAndPassesADeliberateDescent() {
        // The destination side of a vertical pair: outward is DOWN, so a body that arrives inside and sags under
        // gravity moves toward this edge. Arrival lands one deadband inside, which is where the reciprocal source
        // threshold hands it over.
        var down = Boundary(pitch: -90f, yaw: 180f).CompileFrame();
        var definition = Fixtures.BuildDocument();

        Assert.True(WorldAdjacencyPolicy.TryVerticalSettleDeadband(definition, out var settle, out var reason), reason);

        var threshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in down, reciprocalHysteresis: FixedQ4816.FromDouble(value: 0.72), verticalSettleDeadband: settle);
        var rate = ((double)definition.SimulationRateHz);
        // The reciprocal source threshold hands over one threshold past its own plane, which maps to one threshold
        // inside this one. Reading the arrival off the threshold rather than off the deadband is what makes a zero
        // threshold fail this law instead of satisfying it vacuously.
        var arrival = ((double)threshold);
        var sag = (Math.Min(val1: 20.0, val2: (23.0 / rate)) / rate);
        var commandedDescent = (20.0 / rate);

        var settling = WorldAdjacencyRegion.Sweep(
            frame: down,
            from: Fixed(x: 0, y: arrival, z: 0),
            to: Fixed(x: 0, y: (arrival - sag), z: 0),
            outwardThreshold: threshold);
        var deliberate = WorldAdjacencyRegion.Sweep(
            frame: down,
            from: Fixed(x: 0, y: arrival, z: 0),
            to: Fixed(x: 0, y: (arrival - commandedDescent), z: 0),
            outwardThreshold: threshold);

        Assert.False(condition: settling.Crossed, userMessage: "a settling arrival re-crossed its own reciprocal edge under gravity alone");
        Assert.True(condition: deliberate.Crossed, userMessage: "a body driven back down at terminal speed was refused its return crossing");
    }
    [Fact]
    public void ReciprocalHysteresisCoversTwoBodyContactAndSkin() {
        var definition = Fixtures.BuildGradientUpDocument(gradientUp: true);

        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(definition, out var depth, out var reason), reason);
        Assert.True(condition: (depth >= FixedQ4816.FromDouble(value: 0.72)));
        Assert.True(condition: (depth < FixedQ4816.FromDouble(value: 0.721)));
    }
    [Fact]
    public void ValidatorProvesReciprocalBoundaryAndRefusesDrift() {
        var (west, east) = Pair();
        var resolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> { ["east.world.json"] = east, ["west.world.json"] = west });

        Assert.True(WorldDefinitionValidator.TryValidate(west, out var accepted, resolver), accepted);

        var drifted = east with {
            Adjacencies = [east.Adjacencies![0] with { Boundary = Boundary(yaw: -90f) with { Width = 7f } }],
        };
        var driftResolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> { ["east.world.json"] = drifted, ["west.world.json"] = west });

        Assert.False(WorldDefinitionValidator.TryValidate(west, out var refused, driftResolver));
        Assert.Contains("but neighbour", refused, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void ValidatorRefusesAFramePairThatCannotPreserveBodyUp() {
        var (west, east) = Pair();
        var pitched = east with {
            Adjacencies = [east.Adjacencies![0] with { Boundary = Boundary(pitch: 90f, yaw: -90f) }],
        };
        var resolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> { ["east.world.json"] = pitched, ["west.world.json"] = west });

        Assert.False(WorldDefinitionValidator.TryValidate(west, out var refused, resolver));
        Assert.Contains("do not preserve world up", refused, StringComparison.Ordinal);
    }
    [Fact]
    public void ValidatorRequiresTheDirectRouteForADerivedCornerPeer() {
        var (source, left, right, corner) = Corner();
        var resolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> {
            ["left.world.json"] = left,
            ["right.world.json"] = right,
            ["corner.world.json"] = corner,
        });

        Assert.True(WorldDefinitionValidator.TryValidate(source, out var accepted, resolver), accepted);

        var missingRoute = source with {
            Destinations = source.Destinations!.Where(row => !string.Equals(row?.Name.Value, "corner", StringComparison.Ordinal)).ToArray(),
        };

        Assert.False(WorldDefinitionValidator.TryValidate(missingRoute, out var refused, resolver));
        Assert.Contains("derives corner neighbour 'corner.world.json'", refused, StringComparison.Ordinal);
        Assert.Contains("no global persisted destination/reference", refused, StringComparison.Ordinal);
    }
    [Fact]
    public void ValidatorRefusesACornerWhoseTwoTransformPathsDisagree() {
        var (source, left, right, corner) = Corner();
        var shiftedCorner = corner with {
            Adjacencies = [
                corner.Adjacencies![0],
                corner.Adjacencies[1] with {
                    Boundary = corner.Adjacencies[1]!.Boundary with { Center = new Vector3(x: 0.25f, y: 0f, z: 0f) },
                },
            ],
        };
        var resolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> {
            ["left.world.json"] = left,
            ["right.world.json"] = right,
            ["corner.world.json"] = shiftedCorner,
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.Contains("does not close its transform diamond", refused, StringComparison.Ordinal);
    }
    // A verified-attested corner reads through the same WorldAdjacencyDocumentView/WorldAdjacencyEdgeView the
    // resolved-document arm does — every corner law below composes a WorldCounterpartAttestation and wraps it in
    // WorldNeighbourResolution.VerifiedAttested rather than signing one, because signing happens upstream of
    // IWorldNeighbourResolver.Resolve (CounterpartAttestationLawTests covers the signed wire path) and the validator
    // never calls TryVerify itself.
    [Fact]
    public void VerifiedAttestedCornerProvesLikeAResolvedOne() {
        var (source, left, right, corner) = Corner();
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = VerifiedAttested(definition: left, document: "left.world.json"),
            ["right.world.json"] = VerifiedAttested(definition: right, document: "right.world.json"),
            ["corner.world.json"] = VerifiedAttested(definition: corner, document: "corner.world.json"),
        });

        Assert.True(WorldDefinitionValidator.TryValidate(source, out var accepted, resolver), accepted);
        // The resolver call log is the positive control against a walk that silently skipped the corner instead of
        // proving it: if the corner document was never actually asked for, this list would not name it, and this
        // test would go green for the wrong reason (nothing checked, not something proved).
        Assert.Contains("corner.world.json", resolver.Requests);
    }
    [Fact]
    public void VerifiedAttestedCornerRefusesATamperedCounterpartBoundary() {
        var (source, left, right, corner) = Corner();
        var shiftedCorner = corner with {
            Adjacencies = [
                corner.Adjacencies![0],
                corner.Adjacencies[1] with {
                    Boundary = corner.Adjacencies[1]!.Boundary with { Center = new Vector3(x: 0.25f, y: 0f, z: 0f) },
                },
            ],
        };
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = VerifiedAttested(definition: left, document: "left.world.json"),
            ["right.world.json"] = VerifiedAttested(definition: right, document: "right.world.json"),
            ["corner.world.json"] = VerifiedAttested(definition: shiftedCorner, document: "corner.world.json"),
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.Contains("does not close its transform diamond", refused, StringComparison.Ordinal);
    }
    [Fact]
    public void VerifiedAttestedCornerRefusesAMismatchedCounterpartExtent() {
        var (source, left, right, corner) = Corner();
        var widenedCorner = corner with {
            Adjacencies = [
                corner.Adjacencies![0] with { Boundary = corner.Adjacencies[0]!.Boundary with { Width = 9f } },
                corner.Adjacencies[1]!,
            ],
        };
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = VerifiedAttested(definition: left, document: "left.world.json"),
            ["right.world.json"] = VerifiedAttested(definition: right, document: "right.world.json"),
            ["corner.world.json"] = VerifiedAttested(definition: widenedCorner, document: "corner.world.json"),
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.Contains("boundary dimensions do not match corner", refused, StringComparison.Ordinal);
    }
    [Fact]
    public void MixedResolvedAndVerifiedAttestedNeighboursShareOneCorner() {
        var (source, left, right, corner) = Corner();
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = WorldNeighbourResolution.Resolved(left),
            ["right.world.json"] = VerifiedAttested(definition: right, document: "right.world.json"),
            ["corner.world.json"] = VerifiedAttested(definition: corner, document: "corner.world.json"),
        });

        Assert.True(WorldDefinitionValidator.TryValidate(source, out var accepted, resolver), accepted);
    }
    [Fact]
    public void DerivedCornerRefusalNamesTheUnavailableReason() {
        var (source, left, right, corner) = Corner();
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = WorldNeighbourResolution.Resolved(left),
            ["right.world.json"] = WorldNeighbourResolution.Resolved(right),
            ["corner.world.json"] = WorldNeighbourResolution.Unavailable("a distinctive named reason"),
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.Contains("a distinctive named reason", refused, StringComparison.Ordinal);
    }
    // The edge topology two verified-attested neighbours independently name is compiler-derived; the route this
    // document itself has TO that named third authority is separate, authored data. A corner two neighbours agree on
    // must still refuse when this document declares no global persisted destination/reference for it.
    [Fact]
    public void AttestedEdgeNamingAnUnroutedCornerRefusesByName() {
        var (source, left, right, _) = Corner();
        var unrouted = source with {
            Destinations = [source.Destinations![0], source.Destinations[1]],
        };
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = VerifiedAttested(definition: left, document: "left.world.json"),
            ["right.world.json"] = VerifiedAttested(definition: right, document: "right.world.json"),
        });

        Assert.False(WorldDefinitionValidator.TryValidate(unrouted, out var refused, resolver));
        Assert.Contains("derives corner neighbour 'corner.world.json'", refused, StringComparison.Ordinal);
        Assert.Contains("declares no global persisted destination/reference", refused, StringComparison.Ordinal);
    }
    // A resolver that answers a Resolved-kind outcome carrying no Definition (the struct's zero value, or any
    // resolver bug shaped like it) must name that fact rather than end the refusal in a blank interpolation.
    [Fact]
    public void DerivedCornerRefusalNamesAPayloadlessResolvedOutcomeRatherThanGoingBlank() {
        var (source, left, right, _) = Corner();
        var resolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = WorldNeighbourResolution.Resolved(left),
            ["right.world.json"] = WorldNeighbourResolution.Resolved(right),
            ["corner.world.json"] = default,
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.DoesNotContain("— .", refused, StringComparison.Ordinal);
        Assert.Contains("outcome carrying no payload", refused, StringComparison.Ordinal);
    }
    // The security posture this brief adds: a corner names a third authority, so a plain, locally composed
    // WorldNeighbourResolutionKind.Attested outcome — exactly what WorldStorageNeighbourResolver produces today,
    // with no signature and no verified subject — must never enter the corner proof, even when its shape (the
    // WorldCounterpartAttestation payload) is byte-identical to what a verified resolution would carry. Paired with
    // the same fixture's positive control so a reader can see the exact line the resolution's Kind draws.
    [Fact]
    public void PlainAttestedCornerNeverProvesTheDerivedNeighbourButAVerifiedOneDoes() {
        var (source, left, right, corner) = Corner();

        Assert.True(WorldCounterpartAttestation.TryCompose(definition: corner, document: "corner.world.json", attestation: out var attestation, reason: out var composeReason), composeReason);

        var unsignedResolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = WorldNeighbourResolution.Resolved(left),
            ["right.world.json"] = WorldNeighbourResolution.Resolved(right),
            ["corner.world.json"] = WorldNeighbourResolution.Attested(attestation!),
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, unsignedResolver));
        Assert.Contains("cannot reach derived corner neighbour 'corner.world.json'", refused, StringComparison.Ordinal);
        Assert.Contains("without a verified claim binding an authenticated subject", refused, StringComparison.Ordinal);

        var verifiedResolver = new Resolver(resolutions: new Dictionary<string, WorldNeighbourResolution> {
            ["left.world.json"] = WorldNeighbourResolution.Resolved(left),
            ["right.world.json"] = WorldNeighbourResolution.Resolved(right),
            ["corner.world.json"] = WorldNeighbourResolution.VerifiedAttested(attestation!, "subject"),
        });

        Assert.True(WorldDefinitionValidator.TryValidate(source, out var accepted, verifiedResolver), accepted);
    }
    // WorldAdjacencyDocumentView.FindEdge must match by the row's own Name, exactly like
    // WorldDefinitionRows.FindAdjacency and WorldCounterpartAttestation.FindEdge do — never by the other side's
    // Counterpart spelling, which the two names being swapped here would make a transposed implementation return
    // the wrong edge for.
    [Fact]
    public void EdgeViewFindsByNameNotByCounterpart() {
        var definition = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("a-ref"), "a.world.json"),
                new WorldReference(WorldSafeName.Parse("b-ref"), "b.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("a"), "a-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("b"), "b-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("a"), "a", "b", Boundary(yaw: 90f)),
                new WorldAdjacency(WorldSafeName.Parse("b"), "b", "a", Boundary(yaw: -90f)),
            ],
        };

        var definitionView = WorldAdjacencyDocumentView.FromDefinition(definition);
        var definitionFound = definitionView.FindEdge(name: "a");

        Assert.NotNull(value: definitionFound);
        Assert.Equal("a", definitionFound!.Value.Name.Value);
        Assert.Equal("b", definitionFound.Value.Counterpart);

        Assert.True(WorldCounterpartAttestation.TryCompose(definition: definition, document: "self.world.json", attestation: out var attestation, reason: out var reason), reason);

        var attestedView = WorldAdjacencyDocumentView.FromAttestation(attestation!);
        var attestedFound = attestedView.FindEdge(name: "a");

        Assert.NotNull(value: attestedFound);
        Assert.Equal("a", attestedFound!.Value.Name.Value);
        Assert.Equal("b", attestedFound.Value.Counterpart);
    }
    // A neighbour whose back edge names a Destination that does not resolve in its own document must still let the
    // diamond proof run over that edge's Boundary — FindEdge never drops a row for an unresolvable destination, only
    // Edges (corner discovery) does. Tampering the same unresolvable edge's Boundary alongside the broken
    // Destination and still catching the mismatch is the falsifier: an implementation that filtered FindEdge by
    // resolvability would hit ValidateCornerDiamond's early return and this corner would validate clean instead.
    [Fact]
    public void CornerProofSeesAnEdgeWhoseBackDestinationDoesNotResolve() {
        var (source, left, right, corner) = Corner();
        var brokenLeft = left with {
            Adjacencies = [
                left.Adjacencies![0] with {
                    Destination = "missing",
                    Boundary = left.Adjacencies[0]!.Boundary with { Center = new Vector3(x: 0.25f, y: 0f, z: 0f) },
                },
                left.Adjacencies[1]!,
            ],
        };
        var resolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> {
            ["left.world.json"] = brokenLeft,
            ["right.world.json"] = right,
            ["corner.world.json"] = corner,
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.Contains("does not close its transform diamond", refused, StringComparison.Ordinal);
    }
    // TrySharedCorner's first-match-wins order must agree whether a neighbour's edges arrive as a whole document or
    // as an attestation — the quilt's own nw/ne/sw/se-vs-island collision (docs/campaign.md) is the real-world
    // instance of this; FiveDocumentCollision reproduces the same two-candidate ambiguity in code so this suite
    // never reads a shipped world file.
    [Fact]
    public void TrySharedCornerAgreesAcrossRepresentationsWhenTwoCandidatesCollide() {
        var (left, right) = FiveDocumentCollision();

        Assert.True(WorldAdjacencyPolicy.TrySharedCorner(
            left: WorldAdjacencyDocumentView.FromDefinition(left),
            leftBack: "left-back",
            right: WorldAdjacencyDocumentView.FromDefinition(right),
            rightBack: "right-back",
            document: out var resolvedDocument,
            leftEdge: out var resolvedLeftEdge,
            rightEdge: out var resolvedRightEdge
        ));

        Assert.True(WorldCounterpartAttestation.TryCompose(left, "left.world.json", out var leftAttestation, out var leftReason), leftReason);
        Assert.True(WorldCounterpartAttestation.TryCompose(right, "right.world.json", out var rightAttestation, out var rightReason), rightReason);

        Assert.True(WorldAdjacencyPolicy.TrySharedCorner(
            left: WorldAdjacencyDocumentView.FromAttestation(leftAttestation!),
            leftBack: "left-back",
            right: WorldAdjacencyDocumentView.FromAttestation(rightAttestation!),
            rightBack: "right-back",
            document: out var attestedDocument,
            leftEdge: out var attestedLeftEdge,
            rightEdge: out var attestedRightEdge
        ));

        Assert.Equal(resolvedDocument, attestedDocument);
        Assert.Equal(resolvedLeftEdge.Name.Value, attestedLeftEdge.Name.Value);
        Assert.Equal(resolvedRightEdge.Name.Value, attestedRightEdge.Name.Value);

        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(left, right, out var resolvedDepth, out var resolvedOverlapReason), resolvedOverlapReason);
        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(leftAttestation!.Overlap, rightAttestation!.Overlap, out var attestedDepth, out var attestedOverlapReason), attestedOverlapReason);
        Assert.Equal(resolvedDepth.Value, attestedDepth.Value);
    }
    [Fact]
    public void OverlapDepthAgreesAcrossEveryOverload() {
        var pairs = new (WorldDefinition Local, WorldDefinition Neighbour)[] {
            (Fixtures.BuildDocument(), Fixtures.BuildDocument()),
            (Fixtures.BuildDocument(), Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 30) }),
            (Fixtures.BuildGradientUpDocument(gradientUp: true), Fixtures.BuildDocument()),
            (Fixtures.BuildGradientUpDocument(gradientUp: true), (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
                Collision = Fixtures.BuildGradientUpDocument(gradientUp: false).Collision with { ContactSkin = 0.5f },
            })),
        };

        foreach (var (local, neighbour) in pairs) {
            Assert.True(WorldOverlapTerms.TryDerive(local, out var localTerms, out var localReason), localReason);
            Assert.True(WorldOverlapTerms.TryDerive(neighbour, out var neighbourTerms, out var neighbourReason), neighbourReason);

            var documentDocumentOk = WorldAdjacencyPolicy.TryDeriveOverlap(local, neighbour, out var documentDocumentDepth, out var documentDocumentReason);
            var documentTermsOk = WorldAdjacencyPolicy.TryDeriveOverlap(local, neighbourTerms!, out var documentTermsDepth, out var documentTermsReason);
            var termsTermsOk = WorldAdjacencyPolicy.TryDeriveOverlap(localTerms!, neighbourTerms!, out var termsTermsDepth, out var termsTermsReason);

            Assert.True(documentDocumentOk, documentDocumentReason);
            Assert.Equal(documentDocumentOk, documentTermsOk);
            Assert.Equal(documentDocumentOk, termsTermsOk);
            Assert.Equal(documentDocumentDepth.Value, documentTermsDepth.Value);
            Assert.Equal(documentDocumentDepth.Value, termsTermsDepth.Value);
            Assert.Equal(documentDocumentReason, documentTermsReason);
            Assert.Equal(documentDocumentReason, termsTermsReason);

            // Independent oracle: recompute the raw depth straight from the two sides' overlap terms, outside
            // WorldAdjacencyPolicy.TryDeriveOverlap entirely, so a change to the shared arithmetic every overload
            // delegates to cannot hide behind "the three overloads still agree with each other".
            Assert.Equal(ExpectedOverlapDepthRaw(localTerms!, neighbourTerms!), termsTermsDepth.Value);
        }
    }

    private static long ExpectedOverlapDepthRaw(WorldOverlapTerms local, WorldOverlapTerms neighbour) {
        var bodyReach = Math.Max(val1: local.BodyReachRaw, val2: neighbour.BodyReachRaw);
        var interactionReach = Math.Max(val1: local.InteractionReachRaw, val2: neighbour.InteractionReachRaw);
        var closingSpeed = (local.SpeedCeilingRaw + neighbour.SpeedCeilingRaw);
        var slowestRate = Math.Min(
            val1: Math.Max(val1: local.SimulationRateHz, val2: 1),
            val2: Math.Max(val1: neighbour.SimulationRateHz, val2: 1)
        );

        Assert.True(condition: FixedDirectedRounding.TryCeilingQuotient(
            numerator: (FixedQ4816.One.Value * WorldAdjacencyPolicy.DeliveryPeriods),
            fractionBitsNumerator: FixedQ4816.FractionBitCount,
            denominator: slowestRate,
            fractionBitsDenominator: 0,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var latencyRaw
        ));
        Assert.True(condition: FixedDirectedRounding.TryCeilingProductSum(
            a: closingSpeed,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: latencyRaw,
            fractionBitsB: FixedQ4816.FractionBitCount,
            addend: (bodyReach + interactionReach),
            fractionBitsAddend: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var depthRaw
        ));

        return Math.Max(
            val1: depthRaw,
            val2: Math.Max(
                val1: Math.Max(val1: local.HysteresisRaw, val2: neighbour.HysteresisRaw),
                val2: Math.Max(val1: local.SettleDeadbandRaw, val2: neighbour.SettleDeadbandRaw)
            )
        );
    }

    [Fact]
    public void OverlapDepthAgreesAcrossEveryOverloadWhenTheEnvelopeOverflows() {
        var local = Fixtures.BuildDocument();

        Assert.True(WorldOverlapTerms.TryDerive(local, out var localTerms, out var localReason), localReason);

        var extremeNeighbour = (localTerms! with { SpeedCeilingRaw = FixedQ4816.MaxValue.Value });
        var extremeLocalTerms = (localTerms with { SpeedCeilingRaw = FixedQ4816.MaxValue.Value });

        var documentTermsOk = WorldAdjacencyPolicy.TryDeriveOverlap(local, extremeNeighbour, out var documentTermsDepth, out var documentTermsReason);
        var termsTermsOk = WorldAdjacencyPolicy.TryDeriveOverlap(extremeLocalTerms, extremeNeighbour, out var termsTermsDepth, out var termsTermsReason);

        Assert.False(documentTermsOk);
        Assert.Equal(documentTermsOk, termsTermsOk);
        Assert.Equal(documentTermsDepth.Value, termsTermsDepth.Value);
        Assert.Equal(documentTermsReason, termsTermsReason);
    }
    [Fact]
    public void UnavailableBindingMustNameADeclaredChannel() {
        var (west, east) = Pair();
        west = west with {
            Adjacencies = [west.Adjacencies![0] with { OnUnavailable = "missing-channel" }],
        };
        var resolver = new Resolver(definitions: new Dictionary<string, WorldDefinition> { ["east.world.json"] = east, ["west.world.json"] = west });

        Assert.False(WorldDefinitionValidator.TryValidate(west, out var refused, resolver));
        Assert.Contains("onUnavailable 'missing-channel' names no declared channel", refused, StringComparison.Ordinal);
    }
    [Fact]
    public void FederatedEntityAddressesUseTheDeclaredAuthorityNamespace() {
        const string endpoint = "127.0.0.1:38601";
        var definition = Fixtures.BuildDocument() with { Host = Fixtures.BuildDocument().Host with { Authority = endpoint } };
        using var fixture = Fixtures.FreshServer(definition);

        Assert.Equal(endpoint, fixture.Server.AuthorityIdentity);
    }

    private static (WorldDefinition West, WorldDefinition East) Pair() {
        var west = Fixtures.BuildDocument() with {
            References = [new WorldReference(WorldSafeName.Parse("east-ref"), "east.world.json")],
            Destinations = [new WorldDestination(WorldSafeName.Parse("east"), "east-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
            Adjacencies = [new WorldAdjacency(WorldSafeName.Parse("east-edge"), "east", "west-edge", Boundary(yaw: 90f))],
        };
        var east = Fixtures.BuildDocument() with {
            References = [new WorldReference(WorldSafeName.Parse("west-ref"), "west.world.json")],
            Destinations = [new WorldDestination(WorldSafeName.Parse("west"), "west-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
            Adjacencies = [new WorldAdjacency(WorldSafeName.Parse("west-edge"), "west", "east-edge", Boundary(yaw: -90f))],
        };

        return (west, east);
    }
    private static (WorldDefinition Source, WorldDefinition Left, WorldDefinition Right, WorldDefinition Corner) Corner() {
        var source = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("left-ref"), "left.world.json"),
                new WorldReference(WorldSafeName.Parse("right-ref"), "right.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-ref"), "corner.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("left"), "left-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("right"), "right-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("left-edge"), "left", "source-edge", Boundary(yaw: 90f)),
                new WorldAdjacency(WorldSafeName.Parse("right-edge"), "right", "source-edge", Boundary(yaw: 0f)),
            ],
        };
        var left = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("source-ref"), "source.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-ref"), "corner.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("source-edge"), "source", "left-edge", Boundary(yaw: -90f)),
                new WorldAdjacency(WorldSafeName.Parse("corner-edge"), "corner", "left-edge", Boundary(yaw: 0f)),
            ],
        };
        var right = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("source-ref"), "source.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-ref"), "corner.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("source-edge"), "source", "right-edge", Boundary(yaw: 180f)),
                new WorldAdjacency(WorldSafeName.Parse("corner-edge"), "corner", "right-edge", Boundary(yaw: 90f)),
            ],
        };
        var corner = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("left-ref"), "left.world.json"),
                new WorldReference(WorldSafeName.Parse("right-ref"), "right.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("left"), "left-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("right"), "right-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("left-edge"), "left", "corner-edge", Boundary(yaw: 180f)),
                new WorldAdjacency(WorldSafeName.Parse("right-edge"), "right", "corner-edge", Boundary(yaw: -90f)),
            ],
        };

        return (source, left, right, corner);
    }
    // Two neighbours that each name the same two candidate corner documents, in the same declared order, so
    // TrySharedCorner has a genuine second candidate to skip past rather than an accidental single match —
    // FromDefinition and FromAttestation views of these two documents must agree on which one wins.
    private static (WorldDefinition Left, WorldDefinition Right) FiveDocumentCollision() {
        var left = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("source-ref"), "source.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-a-ref"), "corner-a.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-b-ref"), "corner-b.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner-a"), "corner-a-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner-b"), "corner-b-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("left-back"), "source", "right-back", Boundary(yaw: -90f)),
                new WorldAdjacency(WorldSafeName.Parse("left-a"), "corner-a", "right-a", Boundary(yaw: 0f)),
                new WorldAdjacency(WorldSafeName.Parse("left-b"), "corner-b", "right-b", Boundary(yaw: 90f)),
            ],
        };
        var right = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("source-ref"), "source.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-a-ref"), "corner-a.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-b-ref"), "corner-b.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner-a"), "corner-a-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner-b"), "corner-b-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("right-back"), "source", "left-back", Boundary(yaw: 180f)),
                new WorldAdjacency(WorldSafeName.Parse("right-a"), "corner-a", "left-a", Boundary(yaw: 180f)),
                new WorldAdjacency(WorldSafeName.Parse("right-b"), "corner-b", "left-b", Boundary(yaw: -90f)),
            ],
        };

        return (left, right);
    }
    private static WorldNeighbourResolution VerifiedAttested(WorldDefinition definition, string document) {
        Assert.True(WorldCounterpartAttestation.TryCompose(definition: definition, document: document, attestation: out var attestation, reason: out var reason), reason);

        return WorldNeighbourResolution.VerifiedAttested(attestation: attestation!, subject: "subject");
    }
    private static WorldAdjacencyBoundary Boundary(float yaw, float pitch = 0f) => new(Center: Vector3.Zero, OutwardYawDegrees: yaw, OutwardPitchDegrees: pitch, Width: 8f, Height: 8f);
    private static FixedVector3 Fixed(double x, double y, double z) => new(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.FromDouble(value: y), Z: FixedQ4816.FromDouble(value: z));

    private sealed class Resolver : IWorldNeighbourResolver {
        private readonly IReadOnlyDictionary<string, WorldNeighbourResolution> m_resolutions;

        public Resolver(IReadOnlyDictionary<string, WorldDefinition> definitions) {
            var resolutions = new Dictionary<string, WorldNeighbourResolution>(comparer: StringComparer.Ordinal);

            foreach (var pair in definitions) {
                resolutions[pair.Key] = WorldNeighbourResolution.Resolved(pair.Value);
            }

            m_resolutions = resolutions;
        }
        public Resolver(IReadOnlyDictionary<string, WorldNeighbourResolution> resolutions) {
            m_resolutions = resolutions;
        }

        /// <summary>Every document name this resolver was asked for, in request order — the witness a positive
        /// control needs to prove a corner walk actually reached the resolver rather than skipping it silently.</summary>
        public List<string> Requests { get; } = [];

        public WorldNeighbourResolution Resolve(string document) {
            Requests.Add(item: document);

            return (m_resolutions.TryGetValue(
                key: document,
                value: out var resolution
            )
                ? resolution
                : WorldNeighbourResolution.Unavailable($"no '{document}'"));
        }
    }
}
