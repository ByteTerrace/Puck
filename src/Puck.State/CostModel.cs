using Puck.Maths;

namespace Puck.State;

/// <summary>Plan-derived memory service classes, independent of the executing host's cache state.</summary>
public enum MemoryAccessClass : byte {
    /// <summary>Contiguous block copy or zeroing.</summary>
    ContiguousCopyClear,
    /// <summary>Contiguous linear scan.</summary>
    ContiguousScan,
    /// <summary>Dependent access with a proved footprint and reuse bound.</summary>
    DependentIndirectResident,
    /// <summary>Dependent access without a residency proof.</summary>
    DependentIndirectGeneral,
}
/// <summary>Explicit memory service coefficients; constructing these does not establish calibration.</summary>
/// <param name="StartupCycles">Nonnegative setup cycles.</param>
/// <param name="AdditionalLatencyCycles">Nonnegative latency beyond the instruction baseline.</param>
/// <param name="BandwidthNumerator">Positive bytes transferred per denominator cycles.</param>
/// <param name="BandwidthDenominator">Positive cycle denominator of the transfer rate.</param>
public readonly record struct MemoryClassProfile(long StartupCycles, long AdditionalLatencyCycles, long BandwidthNumerator, long BandwidthDenominator) {
    private static CostBound Bound(Int128 cycles) => ((cycles > long.MaxValue)
        ? CostBound.Overflow
        : CostBound.Known(cycles: ((long)cycles))
    );

    /// <summary>Calculates exact service cycles, rounding transfer time upward once. Invalid coefficients/counts
    /// throw; an unrepresentable result retains overflow status.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count or latency is negative, or bandwidth is not positive.</exception>
    public CostBound Cycles(long starts, long dependentAccesses, long bytesTransferred) {
        ArgumentOutOfRangeException.ThrowIfNegative(starts);
        ArgumentOutOfRangeException.ThrowIfNegative(dependentAccesses);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesTransferred);
        ArgumentOutOfRangeException.ThrowIfNegative(StartupCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(AdditionalLatencyCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BandwidthNumerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BandwidthDenominator);
        return ((Bound(cycles: (((Int128)starts) * StartupCycles))
            + Bound(cycles: (((Int128)dependentAccesses) * AdditionalLatencyCycles)))
            + Bound(cycles: (((Int128)bytesTransferred) * BandwidthDenominator).CeilingDivide(divisor: ((Int128)BandwidthNumerator))));
    }
}
/// <summary>Identity and service policy for the portable semantic cost model, whose coefficients remain uncalibrated.</summary>
public sealed class CostModel {
    private static readonly IReadOnlyDictionary<MemoryAccessClass, MemoryClassEvidence> MemoryEvidence = ReferenceScheduleManifest.MemoryClasses.ToDictionary(
        keySelector: entry => entry.Class
    );

    /// <summary>Gets the uncalibrated portable model. No host discovery participates in its identity or prices.</summary>
    public static CostModel Default { get; } = new(
        id: "puck.cost.portable-model.v1",
        referenceProfile: CostModelProfile.Portable
    );

    /// <summary>Gets the digest of the evidence manifest this model's coefficients are read from.</summary>
    public string? EvidenceDigest => ReferenceScheduleManifest.Digest;
    /// <summary>Gets the model identifier.</summary>
    public string Id { get; }
    /// <summary>Gets the abstract service policy.</summary>
    public CostModelProfile ReferenceProfile { get; }

    private CostModel(string id, CostModelProfile referenceProfile) {
        Id = id;
        ReferenceProfile = referenceProfile;
    }

    /// <summary>Returns memory service from the evidence manifest when every coefficient is substantiated. A proved
    /// empty operation costs zero; incomplete evidence remains unresolved.</summary>
    public static CostBound MemoryCycles(MemoryAccessClass accessClass, long starts, long dependentAccesses, long bytesTransferred) {
        ArgumentOutOfRangeException.ThrowIfNegative(starts);
        ArgumentOutOfRangeException.ThrowIfNegative(dependentAccesses);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesTransferred);
        if (!Enum.IsDefined(value: accessClass)) { return CostBound.Unmodeled(reason: "Unknown memory access class."); }
        if ((starts == 0) && (dependentAccesses == 0) && (bytesTransferred == 0)) { return CostBound.Zero; }

        var evidence = MemoryEvidence[accessClass];

        if (!evidence.StartupCycles.IsKnown || !evidence.AdditionalLatencyCycles.IsKnown || (evidence.Bandwidth is not { } bandwidth)) {
            return CostBound.Unmodeled(reason: $"Memory service for {accessClass} has no calibrated portable coefficients.");
        }
        return new MemoryClassProfile(
            StartupCycles: evidence.StartupCycles.Cycles,
            AdditionalLatencyCycles: evidence.AdditionalLatencyCycles.Cycles,
            BandwidthNumerator: bandwidth.Numerator,
            BandwidthDenominator: bandwidth.Denominator
        ).Cycles(
            bytesTransferred: bytesTransferred,
            dependentAccesses: dependentAccesses,
            starts: starts
        );
    }
}
