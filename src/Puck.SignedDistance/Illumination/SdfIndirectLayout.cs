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
    /// <summary>The probes and cells a brick owns.</summary>
    public const int ProbesPerBrick = 64;
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
        ProofCapacity = (ProbeCapacity * ProofsPerCell);
        CellWordOffset = (ProbeCapacity * ProbeWords);
        HitWordOffset = (CellWordOffset + (ProbeCapacity * CellWords));
        ProofWordOffset = (HitWordOffset + ((ProbeCapacity * RaysPerProbe) * HitWords));
        WordCount = (ProofWordOffset + (ProofCapacity * ProofWords));
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
    /// <summary>Gets the total storage words.</summary>
    public int WordCount { get; }
    /// <summary>Gets the cache allocation's bytes, excluding host regions and descriptor storage.</summary>
    public ulong ByteLength => (((ulong)WordCount) * sizeof(uint));
    /// <summary>Gets the maximum counted trace evaluations in one frame, including hit gradients and feedback.</summary>
    public int TraceEvaluationCeiling => ((TraceBudget * IrradianceLattice.RaysPerStratum) * ((TraceSteps + 1) + FeedbackSteps));
    /// <summary>Gets the maximum counted classify evaluations in one frame.</summary>
    public int ClassifyEvaluationCeiling => (ClassifyBudget * ((((ProbesPerBrick * IrradianceLattice.CellSegments) * 2) * SegmentSteps) + 192));
}
