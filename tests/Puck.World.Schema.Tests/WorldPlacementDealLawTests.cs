using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The deal facet's document-side contract: the child id shape only the sweep mints, the offsets a
/// template deals (the same set and order a static distribution materializes), the instance count the dealt
/// row's capacity is bounded by, and the name sites the registry prefixes under an aliased import.</summary>
public sealed class WorldPlacementDealLawTests {
    private static WorldPlacement Template(WorldDistributionRegion region, WorldPlacementDeal? deal = null) => new(
        Id: "stores",
        PrototypeId: "store",
        Position: new DocumentVector3(value: Vector3.Zero),
        YawDegrees: 0f,
        Scale: 1f,
        Distribution: new WorldDistribution(Region: region, Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)),
        Deal: (deal ?? new WorldPlacementDeal(Row: "accounts"))
    );

    [Fact]
    public void AChildIdIsTheTemplateThenTheSeparatorThenTheKey() {
        Assert.Equal(expected: "stores/bytrcstp001", actual: WorldPlacementDeal.ChildId(template: "stores", key: "bytrcstp001"));
        Assert.True(condition: WorldPlacementDeal.IsChildId(id: "stores/bytrcstp001"));
        Assert.False(condition: WorldPlacementDeal.IsChildId(id: "stores"));
        Assert.True(condition: WorldPlacementDeal.IsChildOf(id: "stores/a", template: "stores", key: "a".AsSpan()));
        Assert.False(condition: WorldPlacementDeal.IsChildOf(id: "stores/ab", template: "stores", key: "a".AsSpan()));
        Assert.False(condition: WorldPlacementDeal.IsChildOf(id: "store/a", template: "stores", key: "a".AsSpan()));
        Assert.True(condition: WorldPlacementDeal.TrySplitChildId(id: "stores/a", template: out var template, key: out var key));
        Assert.Equal(expected: "stores", actual: template);
        Assert.Equal(expected: "a", actual: key);
        Assert.False(condition: WorldPlacementDeal.TrySplitChildId(id: "/a", template: out _, key: out _));
        Assert.False(condition: WorldPlacementDeal.TrySplitChildId(id: "stores/", template: out _, key: out _));
    }
    [Fact]
    public void IsChildRequiresADealtParentAndTheParentsOwnChildShape() {
        var dealt = Template(region: new WorldDistributionRegion.Lattice(StepA: new DocumentVector3(value: Vector3.UnitX), CountA: 2, StepB: new DocumentVector3(value: Vector3.UnitZ), CountB: 1));
        var plain = (dealt with { Deal = null });
        var child = new WorldPlacement(Id: "stores/a", PrototypeId: "store", Position: new DocumentVector3(value: Vector3.Zero), YawDegrees: 0f, Scale: 1f, Parent: "stores");

        Assert.True(condition: WorldPlacementDeal.IsChild(placement: child, parent: dealt));
        Assert.False(condition: WorldPlacementDeal.IsChild(placement: child, parent: plain));
        Assert.False(condition: WorldPlacementDeal.IsChild(placement: child, parent: null));
        Assert.False(condition: WorldPlacementDeal.IsChild(placement: (child with { Parent = "court" }), parent: dealt));
        Assert.False(condition: WorldPlacementDeal.IsChild(placement: (child with { Id = "shed/a" }), parent: dealt));
    }
    /// <summary>A lattice deals A-major, B-minor, the order <c>CreationStampLattice.ForEachFixedInstance</c> visits
    /// a static lattice's copies in, so offset k is where the k-th static copy would stand.</summary>
    [Fact]
    public void ALatticeDealsInAMajorBMinorOrder() {
        var template = Template(region: new WorldDistributionRegion.Lattice(
            StepA: new DocumentVector3(value: new Vector3(x: 2f, y: 0f, z: 0f)),
            CountA: 2,
            StepB: new DocumentVector3(value: new Vector3(x: 0f, y: 0f, z: 3f)),
            CountB: 3
        ));
        var offsets = WorldPlacementDeal.Offsets(template: template, worldSeed: 0UL);

        Assert.Equal(expected: 6, actual: offsets.Length);
        Assert.Equal(expected: 6, actual: WorldPlacementDeal.InstanceCount(template: template, worldSeed: 0UL));
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 0f), actual: offsets[0].ToVector3());
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 3f), actual: offsets[1].ToVector3());
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 6f), actual: offsets[2].ToVector3());
        Assert.Equal(expected: new Vector3(x: 2f, y: 0f, z: 0f), actual: offsets[3].ToVector3());
        Assert.Equal(expected: new Vector3(x: 2f, y: 0f, z: 6f), actual: offsets[5].ToVector3());
    }
    /// <summary>A scatter's dealt offsets are the sampled offsets a static scatter of the same row materializes,
    /// element for element, and its instance count is the seed-independent block count.</summary>
    [Fact]
    public void AScatterDealsTheSameOffsetsAStaticScatterMaterializes() {
        var template = Template(region: new WorldDistributionRegion.Scatter(CellSize: 1.6f, Width: 16, Depth: 16, Spacing: 4, Radius: 1, Seed: 7u));
        var dealt = WorldPlacementDeal.Offsets(template: template, worldSeed: 11UL);
        var sampled = WorldPlacementStamp.SampledFixedOffsetsFor(placement: template, worldSeed: 11UL);

        Assert.NotNull(@object: sampled);
        Assert.Equal(expected: 16, actual: dealt.Length);
        Assert.Equal(expected: 16, actual: WorldPlacementDeal.InstanceCount(template: template, worldSeed: 11UL));
        Assert.Equal<FixedVector3>(expected: sampled!, actual: dealt);
        Assert.NotEqual<FixedVector3>(expected: WorldPlacementDeal.Offsets(template: template, worldSeed: 12UL), actual: dealt);
    }
    [Fact]
    public void ADiscOrPointsRegionDealsNothing() {
        Assert.Empty(collection: WorldPlacementDeal.Offsets(template: Template(region: new WorldDistributionRegion.Disc(Radius: 2f)), worldSeed: 0UL));
        Assert.Equal(expected: 0, actual: WorldPlacementDeal.InstanceCount(template: Template(region: new WorldDistributionRegion.Disc(Radius: 2f)), worldSeed: 0UL));
    }
    [Fact]
    public void TheRegistryReachesBothDealRowSites() {
        var paths = WorldNameRegistry.Sites.Select(selector: static site => site.Path).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(expected: "placements.rows[].deal.row", collection: paths);
        Assert.Contains(expected: "placements.rows[].deal.variants.row", collection: paths);
    }
}
