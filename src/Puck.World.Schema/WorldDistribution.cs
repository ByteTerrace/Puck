using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>A closed spatial region vocabulary consumed by <see cref="WorldDistribution"/>.</summary>
[JsonDerivedType(typeof(WorldDistributionRegion.Disc), typeDiscriminator: "disc")]
[JsonDerivedType(typeof(WorldDistributionRegion.Points), typeDiscriminator: "points")]
[JsonDerivedType(typeof(WorldDistributionRegion.Lattice), typeDiscriminator: "lattice")]
[JsonDerivedType(typeof(WorldDistributionRegion.Noise), typeDiscriminator: "noise")]
[JsonDerivedType(typeof(WorldDistributionRegion.Scatter), typeDiscriminator: "scatter")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldDistributionRegion {
    private WorldDistributionRegion() { }

    /// <summary>A planar disc centered on the consumer's origin.</summary>
    /// <param name="Radius">The disc radius.</param>
    /// <param name="SampleCount">The radial fill count, or null to use the consumer's requested count.</param>
    public sealed record Disc(float Radius, int? SampleCount = null) : WorldDistributionRegion;
    /// <summary>A cycle of authored spawn poses, each expanded by a planar square.</summary>
    /// <param name="Names">The spawn-point names.</param>
    /// <param name="HalfExtent">The square half-extent on X and Z.</param>
    public sealed record Points(IReadOnlyList<string> Names, float HalfExtent) : WorldDistributionRegion;
    /// <summary>A finite two-axis lattice local to a placement.</summary>
    /// <param name="StepA">The first per-copy step.</param>
    /// <param name="CountA">The copy count along <paramref name="StepA"/>.</param>
    /// <param name="StepB">The second per-copy step.</param>
    /// <param name="CountB">The copy count along <paramref name="StepB"/>.</param>
    public sealed record Lattice(DocumentVector3 StepA, int CountA, DocumentVector3 StepB, int CountB) : WorldDistributionRegion;
    /// <summary>Deterministic hash-lattice fBm patch admission over a placement-local grid, centered on the
    /// placement — one instance at the center of every admitted cell. The placement twin of
    /// <see cref="WorldLatticeFill.Noise"/>: same fixed-point fBm, same seed fold against
    /// <c>generation.worldSeed</c>, same threshold semantics; here admission stamps a creation copy instead of
    /// writing a field value.</summary>
    /// <param name="CellSize">The local grid's cubic cell edge, world units.</param>
    /// <param name="Width">Cells along the placement's local +X.</param>
    /// <param name="Depth">Cells along the placement's local +Z.</param>
    /// <param name="Frequency">Noise-cell edge in lattice cells (see <see cref="WorldLatticeFill.Noise.Frequency"/>). At least 1.</param>
    /// <param name="Threshold">The patch admission level in [0, 1); higher = sparser patches.</param>
    /// <param name="Octaves">Octave count, 1..4.</param>
    /// <param name="Seed">The hash seed, folded with the world seed.</param>
    public sealed record Noise(float CellSize, int Width, int Depth, int Frequency, float Threshold = 0.5f, int Octaves = 3, uint Seed = 0u) : WorldDistributionRegion;
    /// <summary>One jittered instance per <paramref name="Spacing"/>-cell block over a placement-local grid,
    /// centered on the placement. The placement twin of <see cref="WorldLatticeFill.Scatter"/>: same integer PCG3D
    /// block jitter, same seed fold — every block materializes exactly the one jittered point (never a filled
    /// neighborhood), so the instance count is exact and seed-independent: ceil(Width/Spacing) x
    /// ceil(Depth/Spacing).</summary>
    /// <param name="CellSize">The local grid's cubic cell edge, world units.</param>
    /// <param name="Width">Cells along the placement's local +X.</param>
    /// <param name="Depth">Cells along the placement's local +Z.</param>
    /// <param name="Spacing">The scatter block edge in cells (at least 2).</param>
    /// <param name="Radius">The jitter inset in cells (at least 1; at most spacing/2 — a point never leaves its
    /// block, mirroring <see cref="WorldLatticeFill.Scatter.Radius"/>'s own bound).</param>
    /// <param name="Seed">The hash seed, folded with the world seed.</param>
    public sealed record Scatter(float CellSize, int Width, int Depth, int Spacing, int Radius = 1, uint Seed = 0u) : WorldDistributionRegion;
}
/// <summary>A spatial region composed with the deterministic sequence that fills it.</summary>
/// <param name="Region">The region to fill.</param>
/// <param name="Fill">The per-index fill sequence.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDistribution(WorldDistributionRegion Region, WorldSequence Fill) {
    /// <summary>Gets the inert distribution every world declaring no <c>population.distribution</c> resolves to —
    /// the smallest valid disc (the validator's positive-radius floor admits no zero disc here) with an inert fill
    /// sequence. Never read unless the document also authors simulated peers past its local/network seat count.</summary>
    public static WorldDistribution Default { get; } = new(
        Region: new WorldDistributionRegion.Disc(
            Radius: 0.01f,
            SampleCount: 1
        ),
        Fill: WorldSequence.AdditiveDefault
    );
}
/// <summary>The one-time fixed-point compilation of a population distribution.</summary>
/// <param name="Radius">The disc radius or point-square half-extent.</param>
/// <param name="SampleCount">The authored radial sample count, or zero to use the requested count.</param>
/// <param name="Points">Resolved spawn poses for a points region, or null for a disc.</param>
/// <param name="Fill">The authored deterministic fill sequence.</param>
public readonly record struct FixedWorldDistribution(FixedQ4816 Radius, int SampleCount, FixedSpawnPoint[]? Points, WorldSequence Fill) {
    /// <summary>Compiles a disc or points distribution against the definition's spawn poses.</summary>
    /// <param name="distribution">The authored distribution to compile.</param>
    /// <param name="spawnPoints">The definition's spawn points, resolved for a points region.</param>
    /// <returns>The fixed-point compiled distribution.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="distribution"/>'s region is neither a <see cref="WorldDistributionRegion.Disc"/> nor a <see cref="WorldDistributionRegion.Points"/> region.</exception>
    public static FixedWorldDistribution Compile(WorldDistribution distribution, IReadOnlyList<WorldSpawnPoint> spawnPoints) {
        return distribution.Region switch {
            WorldDistributionRegion.Disc disc => new FixedWorldDistribution(
            Radius: FixedQ4816.FromDouble(value: disc.Radius),
            SampleCount: (disc.SampleCount ?? 0),
            Points: null,
            Fill: distribution.Fill
        ),
            WorldDistributionRegion.Points points => new FixedWorldDistribution(
            Radius: FixedQ4816.FromDouble(value: points.HalfExtent),
            SampleCount: 0,
            Points: points.Names.Select(selector: name => {
                var point = WorldDefinitionRows.FindSpawnPoint(
                    id: name,
                    spawnPoints: spawnPoints
                )!.Value;

                return FixedSpawnPoint.Compile(point: in point);
            }).ToArray(),
            Fill: distribution.Fill
        ),
            _ => throw new InvalidOperationException(message: "A population distribution must use a disc or points region."),
        };
    }
}
