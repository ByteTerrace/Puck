namespace Puck.Maths;

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
