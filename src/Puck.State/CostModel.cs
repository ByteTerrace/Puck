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

/// <summary>Abstract service-rate policy. Its units do not identify a processor, clock, ISA, or executing host.</summary>
public sealed record CostModelProfile {
    /// <summary>Gets the abstract profile identifier.</summary>
    public string Name { get; }
    /// <summary>Gets abstract service cycles available per reference second.</summary>
    public long CyclesPerSecond { get; }
    /// <summary>Gets the numerator of the authored subsystem reservation.</summary>
    public long SubsystemShareNumerator { get; }
    /// <summary>Gets the denominator of the authored subsystem reservation.</summary>
    public long SubsystemShareDenominator { get; }
    /// <summary>Gets the shared engine time base.</summary>
    public long EngineTicksPerSecond => (long)FixedTickConversion.TicksPerSecond;

    /// <summary>Constructs a positive service rate and a reserved share in (0, 1].</summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The service rate or share is invalid.</exception>
    public CostModelProfile(string name, long cyclesPerSecond, long subsystemShareNumerator, long subsystemShareDenominator) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cyclesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subsystemShareNumerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subsystemShareDenominator);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subsystemShareNumerator, subsystemShareDenominator);
        Name = name;
        CyclesPerSecond = cyclesPerSecond;
        SubsystemShareNumerator = subsystemShareNumerator;
        SubsystemShareDenominator = subsystemShareDenominator;
    }

    /// <summary>Proposed portable service policy: three billion abstract cycles per second, half reserved for
    /// authored work. This is unit normalization and reservation policy, not a measured hardware frequency.</summary>
    public static CostModelProfile Portable { get; } = new("puck.portable64.v1", 3_000_000_000L, 1L, 2L);

    /// <summary>Returns the exact step period; rate zero has no recurring period.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is negative.</exception>
    /// <exception cref="ArgumentException">A positive rate does not divide the engine time base.</exception>
    public long StepPeriodEngineTicks(int rateHz) {
        ValidateRate(rateHz);
        return rateHz == 0 ? 0L : EngineTicksPerSecond / rateHz;
    }

    /// <summary>Returns floor(F * p / (q * r)) without saturating intermediate products. Rate zero grants no search allowance.</summary>
    /// <exception cref="ArgumentException">The rate is negative or does not divide the engine time base.</exception>
    public long AuthoredBudgetCycles(int rateHz) {
        ValidateRate(rateHz);
        return rateHz == 0 ? 0L : (long)(((Int128)CyclesPerSecond * SubsystemShareNumerator) / ((Int128)SubsystemShareDenominator * rateHz));
    }

    /// <summary>Returns ceil(C * H / F), rounded once in exact arithmetic.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The cycle count is negative.</exception>
    /// <exception cref="OverflowException">The resulting tick count does not fit in a signed 64-bit integer.</exception>
    public long ReferenceEngineTicks(long cycles) {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);
        return checked((long)(((Int128)cycles * EngineTicksPerSecond).CeilingDivide((Int128)CyclesPerSecond)));
    }

    /// <summary>Returns C * r / F as an exact rational with a widened numerator. Rate zero has no step fraction.</summary>
    /// <exception cref="ArgumentException">The cycle count or rate is negative, or a positive rate is unsupported.</exception>
    public (Int128 Numerator, long Denominator) ReferenceStepFraction(long cycles, int rateHz) {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);
        ValidateRate(rateHz);
        return rateHz == 0 ? (Int128.Zero, 1L) : ((Int128)cycles * rateHz, CyclesPerSecond);
    }

    /// <summary>Tests a known cost against the abstract allowance. Unknown/overflow costs and rate zero cannot
    /// certify a recurring deadline. Comparison with the divided allowance avoids a three-factor product overflow.</summary>
    public bool Admits(CostBound bound, int rateHz) {
        ValidateRate(rateHz);
        return rateHz > 0 && bound.IsKnown && bound.Cycles <= AuthoredBudgetCycles(rateHz);
    }

    private void ValidateRate(int rateHz) {
        ArgumentOutOfRangeException.ThrowIfNegative(rateHz);
        if (rateHz > 0 && EngineTicksPerSecond % rateHz != 0) {
            throw new ArgumentException("A positive simulation rate must divide the engine time base exactly.", nameof(rateHz));
        }
    }
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
