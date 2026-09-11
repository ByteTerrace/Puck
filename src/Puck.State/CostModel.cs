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
        return Bound((Int128)starts * StartupCycles)
            + Bound((Int128)dependentAccesses * AdditionalLatencyCycles)
            + Bound(((Int128)bytesTransferred * BandwidthDenominator).CeilingDivide((Int128)BandwidthNumerator));
    }

    private static CostBound Bound(Int128 cycles) => cycles > long.MaxValue ? CostBound.Overflow : CostBound.Known((long)cycles);
}

/// <summary>Identity and service policy for the portable semantic cost model, whose coefficients remain uncalibrated.</summary>
public sealed class CostModel {
    /// <summary>Gets the model identifier.</summary>
    public string Id { get; }
    /// <summary>Gets the abstract service policy.</summary>
    public CostModelProfile ReferenceProfile { get; }
    /// <summary>Gets the evidence digest, or null until a real coefficient manifest has been established.</summary>
    public string? EvidenceDigest => null;

    private CostModel(string id, CostModelProfile referenceProfile) {
        Id = id;
        ReferenceProfile = referenceProfile;
    }

    /// <summary>Gets the uncalibrated portable model. No host discovery participates in its identity or prices.</summary>
    public static CostModel Default { get; } = new("puck.cost.portable.v1", CostModelProfile.Portable);

    /// <summary>Returns unresolved memory service until coefficients are substantiated. A proved empty operation costs zero.</summary>
    public static CostBound MemoryCycles(MemoryAccessClass accessClass, long starts, long dependentAccesses, long bytesTransferred) {
        ArgumentOutOfRangeException.ThrowIfNegative(starts);
        ArgumentOutOfRangeException.ThrowIfNegative(dependentAccesses);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesTransferred);
        if (!Enum.IsDefined(accessClass)) { return CostBound.Unmodeled("Unknown memory access class."); }
        return starts == 0 && dependentAccesses == 0 && bytesTransferred == 0
            ? CostBound.Zero
            : CostBound.Unmodeled($"Memory service for {accessClass} has no calibrated portable coefficients.");
    }
}
