using Puck.SignedDistance.Illumination;

namespace Puck.SignedDistance;

/// <summary>The presentation cache's allocation and update tier. Off owns no cache.</summary>
public enum SdfIndirectTier {
    /// <summary>No indirect cache.</summary>
    Off,
    /// <summary>The room and world lattices.</summary>
    Medium,
    /// <summary>The near, room and world lattices.</summary>
    High,
}
/// <summary>The residency cache's word layout and bounded schedule. The shader generator reads these values.</summary>
public sealed class SdfIndirectLayout {
    /// <summary>The greatest feedback depth supported by a cache tier, after its direct sweep.</summary>
    public const int MaximumBounces = 4;
    /// <summary>The probes and cells a brick owns.</summary>
    public const int ProbesPerBrick = 64;
    /// <summary>The level bits in a live brick record's final word; minus one still marks an absent brick.</summary>
    public const int BrickLevelMask = 255;
    /// <summary>A brick's cell partitions are admitted and precede every reader in the ordered submission.</summary>
    public const int BrickClassified = 256;
    /// <summary>A probe's position and class, traced-stratum and epoch word.</summary>
    public const int ProbeWords = 4;
    /// <summary>A cell's corner components, octahedral plane normal and plane offset.</summary>
    public const int CellWords = 3;
    /// <summary>A ray's distance, octahedral normal, full material identity and terminal/proof/launch state.</summary>
    public const int HitWords = 4;
    /// <summary>A cached proof's anchor, clearance, corner mask, exact key and publication frame.</summary>
    public const int ProofWords = 7;
    /// <summary>The bounded hash entries assigned to each classified cell.</summary>
    public const int ProofsPerCell = 8;
    /// <summary>The bits of one anchor-bin coordinate inside its cell.</summary>
    public const int ProofSlotBits = 3;
    /// <summary>The proof anchor bins along each cell axis.</summary>
    public const int ProofSlotsPerAxis = (1 << ProofSlotBits);
    /// <summary>The level bits below the exact anchor-bin coordinates in a proof key.</summary>
    public const int ProofKeyLevelBits = 2;
    /// <summary>The field evaluation allowance for a ray, including support seeking and its proof.</summary>
    public const int TraceSteps = 64;
    /// <summary>The additional allowance for a hit launch and its feedback proof.</summary>
    public const int FeedbackSteps = 40;
    /// <summary>The launch's share of the hit feedback allowance.</summary>
    public const int LaunchSteps = 8;
    /// <summary>The allowance for each directed partition segment.</summary>
    public const int SegmentSteps = 16;
    /// <summary>The probe class occupies the low two bits.</summary>
    public const int ClassMask = 3;
    /// <summary>The first traced-stratum bit.</summary>
    public const int TracedShift = 4;
    /// <summary>The first geometry epoch bit in a probe state.</summary>
    public const int EpochShift = 8;
    /// <summary>The ray state begins with the terminal kind.</summary>
    public const int KindMask = 3;
    /// <summary>The first bit of the successful feedback or continuation corner mask.</summary>
    public const int ProofMaskShift = 2;
    /// <summary>The first bit of the proof's level.</summary>
    public const int ProofLevelShift = 10;
    /// <summary>The first terminal-word bit of the launch height as a fraction of the level spacing.</summary>
    public const int LaunchHeightShift = 12;
    /// <summary>The number of bits representing the launched height.</summary>
    public const int LaunchHeightBits = 20;
    /// <summary>The maximum encoded launch-height fraction.</summary>
    public const int LaunchHeightMask = ((1 << LaunchHeightBits) - 1);
    /// <summary>The independent read and write generations of a finite lighting solve.</summary>
    public const int LightingGenerations = 2;
    /// <summary>The side of a stored irradiance map, including its octahedral fold border.</summary>
    public const int IrradianceEdge = IrradianceLattice.BorderedTexels;
    /// <summary>The stored irradiance texels per probe, including the octahedral fold border.</summary>
    public const int IrradianceTexels = (IrradianceLattice.BorderedTexels * IrradianceLattice.BorderedTexels);
    /// <summary>The independently stored source categories of each lighting value.</summary>
    public const int SourceCount = 5;
    /// <summary>The words in a lighting record: one nonnegative R11G11B10 value per source.</summary>
    public const int RadianceWords = SourceCount;
    /// <summary>The largest supported probe ray count, bounding a shade group's shared storage.</summary>
    public const int MaximumRaysPerProbe = 256;

    /// <summary>Creates the layout for a tier.</summary>
    /// <param name="tier">The requested cache tier.</param>
    /// <exception cref="ArgumentOutOfRangeException">The tier is undefined.</exception>
    public SdfIndirectLayout(SdfIndirectTier tier) {
        if (!Enum.IsDefined(value: tier)) { throw new ArgumentOutOfRangeException(paramName: nameof(tier)); }
        Tier = tier;
        var strata = ((tier == SdfIndirectTier.High) ? 4 : 2);

        Levels = tier switch {
            SdfIndirectTier.Off => [],
            SdfIndirectTier.Medium => [new(Name: "room", Radius: 36, Reach: 9, Spacing: 1.5, Strata: strata), new(Name: "world", Radius: 0, Reach: 0, Spacing: 4.5, Strata: strata)],
            _ => [new(Name: "near", Radius: 12, Reach: 3, Spacing: 0.5, Strata: strata), new(Name: "room", Radius: 36, Reach: 9, Spacing: 1.5, Strata: strata), new(Name: "world", Radius: 0, Reach: 0, Spacing: 4.5, Strata: strata)],
        };
        Pools = tier switch { SdfIndirectTier.Off => [], SdfIndirectTier.Medium => [192, 64], _ => [256, 192, 64] };
        BrickCapacity = Pools.Sum();
        ProbeCapacity = (BrickCapacity * ProbesPerBrick);
        RaysPerProbe = ((tier == SdfIndirectTier.Off) ? 0 : (strata * IrradianceLattice.RaysPerStratum));
        TraceBudget = tier switch { SdfIndirectTier.Off => 0, SdfIndirectTier.Medium => 128, _ => 512 };
        ClassifyBudget = tier switch { SdfIndirectTier.Off => 0, SdfIndirectTier.Medium => 4, _ => 8 };
        ShadeBudget = tier switch { SdfIndirectTier.Off => 0, SdfIndirectTier.Medium => 4096, _ => 8192 };
        ReceiverProofBudget = tier switch { SdfIndirectTier.Off => 0, SdfIndirectTier.Medium => 32768, _ => 65536 };
        BounceLimit = tier switch { SdfIndirectTier.Off => 0, SdfIndirectTier.Medium => 2, _ => MaximumBounces };
        ProofCapacity = (ProbeCapacity * ProofsPerCell);
        CellWordOffset = (ProbeCapacity * ProbeWords);
        HitWordOffset = (CellWordOffset + (ProbeCapacity * CellWords));
        ProofWordOffset = (HitWordOffset + ((ProbeCapacity * RaysPerProbe) * HitWords));
        RadianceProbeOffset = ((tier == SdfIndirectTier.Medium) ? (Pools[0] * ProbesPerBrick) : 0);
        RadianceProbeCapacity = (ProbeCapacity - RadianceProbeOffset);
        RadianceWordOffset = (ProofWordOffset + (ProofCapacity * ProofWords));
        RadianceGenerationWords = ((RadianceProbeCapacity * RaysPerProbe) * RadianceWords);
        IrradianceWordOffset = (RadianceWordOffset + (LightingGenerations * RadianceGenerationWords));
        IrradianceGenerationWords = ((ProbeCapacity * IrradianceTexels) * RadianceWords);
        PublicationWordOffset = (IrradianceWordOffset + (LightingGenerations * IrradianceGenerationWords));
        ReceiverProofWordOffset = (PublicationWordOffset + (LightingGenerations * ProbeCapacity));
        WordCount = (ReceiverProofWordOffset + ((tier == SdfIndirectTier.Off) ? 0 : 1));
    }

    /// <summary>Gets the selected tier.</summary>
    public SdfIndirectTier Tier { get; }
    /// <summary>Gets the lattices, finest first.</summary>
    public IReadOnlyList<IrradianceLevel> Levels { get; }
    /// <summary>Gets each level's brick allowance.</summary>
    public IReadOnlyList<int> Pools { get; }
    /// <summary>Gets the total brick slots.</summary>
    public int BrickCapacity { get; }
    /// <summary>Gets the total probe slots.</summary>
    public int ProbeCapacity { get; }
    /// <summary>Gets the ray records per probe.</summary>
    public int RaysPerProbe { get; }
    /// <summary>Gets the scheduled strata per frame.</summary>
    public int TraceBudget { get; }
    /// <summary>Gets the classified bricks per frame.</summary>
    public int ClassifyBudget { get; }
    /// <summary>Gets the probes one submitted batch shades at most.</summary>
    public int ShadeBudget { get; }
    /// <summary>Gets the new receiver proofs all views may request in one frame.</summary>
    public int ReceiverProofBudget { get; }
    /// <summary>Gets the maximum feedback sweeps after the direct sweep.</summary>
    public int BounceLimit { get; }
    /// <summary>Gets the proof hash slots.</summary>
    public int ProofCapacity { get; }
    /// <summary>Gets the first probe word.</summary>
    public int ProbeWordOffset => 0;
    /// <summary>Gets the first cell word.</summary>
    public int CellWordOffset { get; }
    /// <summary>Gets the first ray record word.</summary>
    public int HitWordOffset { get; }
    /// <summary>Gets the first proof word.</summary>
    public int ProofWordOffset { get; }
    /// <summary>Gets the first probe with persistent directional radiance. High retains every level for near-field
    /// receivers; Medium retains only the coarser level read by continuations.</summary>
    public int RadianceProbeOffset { get; }
    /// <summary>Gets the probes whose rays a continuation or High near-field receiver can read.</summary>
    public int RadianceProbeCapacity { get; }
    /// <summary>Gets the first stored ray radiance word.</summary>
    public int RadianceWordOffset { get; }
    /// <summary>Gets the words in one ray radiance generation.</summary>
    public int RadianceGenerationWords { get; }
    /// <summary>Gets the first irradiance texel word.</summary>
    public int IrradianceWordOffset { get; }
    /// <summary>Gets the words in one irradiance generation.</summary>
    public int IrradianceGenerationWords { get; }
    /// <summary>Gets the per-generation probe publication stamps, cleared with a replaced probe.</summary>
    public int PublicationWordOffset { get; }
    /// <summary>Gets the shared receiver-proof admission counter, reset before admitted views. Deferred counts belong to each view.</summary>
    public int ReceiverProofWordOffset { get; }
    /// <summary>Gets the total storage words.</summary>
    public int WordCount { get; }
    /// <summary>Gets the cache allocation's bytes, excluding host regions and descriptor storage.</summary>
    public ulong ByteLength => (((ulong)WordCount) * sizeof(uint));
    /// <summary>Gets the maximum counted trace evaluations in one frame, including hit gradients and feedback.</summary>
    public int TraceEvaluationCeiling => ((TraceBudget * IrradianceLattice.RaysPerStratum) * ((TraceSteps + 1) + FeedbackSteps));
    /// <summary>Gets the maximum counted classify evaluations in one frame.</summary>
    public int ClassifyEvaluationCeiling => (ClassifyBudget * ((((ProbesPerBrick * IrradianceLattice.CellSegments) * 2) * SegmentSteps) + 192));
}
