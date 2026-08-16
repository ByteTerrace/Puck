using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Proves the composite resolver's Resolved-beats-Attested-beats-Unavailable ordering, and that an attested-only
/// composition never routes an empty <see cref="WorldNeighbourResolution.Reason"/> into the Unavailable path.
/// </summary>
public sealed class WorldCompositeNeighbourResolverLawTests {
    [Fact]
    public void Composite_ReturnsAttested_WhenNoInnerResolverResolves() {
        var attestation = BuildAttestation(document: "neighbour");
        var composite = new WorldCompositeNeighbourResolver(resolvers: [
            new FixedResolver(outcome: WorldNeighbourResolution.Unavailable(reason: "first miss")),
            new FixedResolver(outcome: WorldNeighbourResolution.Attested(attestation: attestation)),
            new FixedResolver(outcome: WorldNeighbourResolution.Unavailable(reason: "third miss")),
        ]);

        var outcome = composite.Resolve(document: "neighbour");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Attested, actual: outcome.Kind);
        Assert.Same(expected: attestation, actual: outcome.Attestation);
    }
    [Fact]
    public void Composite_PrefersResolved_OverAnEarlierAttested() {
        var attestation = BuildAttestation(document: "neighbour");
        var definition = Fixtures.BuildDocument();
        var composite = new WorldCompositeNeighbourResolver(resolvers: [
            new FixedResolver(outcome: WorldNeighbourResolution.Attested(attestation: attestation)),
            new FixedResolver(outcome: WorldNeighbourResolution.Resolved(definition: definition)),
        ]);

        var outcome = composite.Resolve(document: "neighbour");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Resolved, actual: outcome.Kind);
        Assert.Same(expected: definition, actual: outcome.Definition);
    }
    [Fact]
    public void Composite_OfAttestedOnlyResolvers_DoesNotThrow() {
        var attestation = BuildAttestation(document: "neighbour");
        var composite = new WorldCompositeNeighbourResolver(resolvers: [
            new FixedResolver(outcome: WorldNeighbourResolution.Attested(attestation: attestation)),
        ]);

        var outcome = composite.Resolve(document: "neighbour");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Attested, actual: outcome.Kind);
        Assert.Same(expected: attestation, actual: outcome.Attestation);
    }
    [Fact]
    public void Composite_ReturnsTheFirstAttested_WhenTwoResolversAttest() {
        var firstAttestation = BuildAttestation(document: "first-neighbour");
        var secondAttestation = BuildAttestation(document: "second-neighbour");
        var composite = new WorldCompositeNeighbourResolver(resolvers: [
            new FixedResolver(outcome: WorldNeighbourResolution.Attested(attestation: firstAttestation)),
            new FixedResolver(outcome: WorldNeighbourResolution.Attested(attestation: secondAttestation)),
        ]);

        var outcome = composite.Resolve(document: "neighbour");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Attested, actual: outcome.Kind);
        Assert.Same(expected: firstAttestation, actual: outcome.Attestation);
    }
    [Fact]
    public void Composite_PrefersVerifiedAttested_OverAnEarlierAttested() {
        var unsigned = BuildAttestation(document: "same-owner-copy");
        var verified = BuildAttestation(document: "signed-claim");
        var composite = new WorldCompositeNeighbourResolver(resolvers: [
            new FixedResolver(outcome: WorldNeighbourResolution.Attested(attestation: unsigned)),
            new FixedResolver(outcome: WorldNeighbourResolution.VerifiedAttested(attestation: verified, subject: "owner")),
            new FixedResolver(outcome: WorldNeighbourResolution.Unavailable(reason: "third miss")),
        ]);

        var outcome = composite.Resolve(document: "neighbour");

        Assert.Equal(expected: WorldNeighbourResolutionKind.VerifiedAttested, actual: outcome.Kind);
        Assert.Same(expected: verified, actual: outcome.Attestation);
    }
    [Fact]
    public void Composite_NamesEveryMiss_WhenEveryResolverIsUnavailable() {
        var composite = new WorldCompositeNeighbourResolver(resolvers: [
            new FixedResolver(outcome: WorldNeighbourResolution.Unavailable(reason: "first miss")),
            new FixedResolver(outcome: WorldNeighbourResolution.Unavailable(reason: "second miss")),
        ]);

        var outcome = composite.Resolve(document: "neighbour");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Unavailable, actual: outcome.Kind);
        Assert.Equal(expected: "first miss; then second miss", actual: outcome.Reason);
    }

    private static WorldCounterpartAttestation BuildAttestation(string document) =>
        new(Document: document, Edges: [], Overlap: new WorldOverlapTerms(
            BodyReachRaw: 0L,
            HysteresisRaw: 0L,
            InteractionReachRaw: 0L,
            SettleDeadbandRaw: 0L,
            SimulationRateHz: 60,
            SpeedCeilingRaw: 0L
        ));

    private sealed class FixedResolver(WorldNeighbourResolution outcome) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) => outcome;
    }
}
