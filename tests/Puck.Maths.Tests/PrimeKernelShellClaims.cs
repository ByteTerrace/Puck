using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private delegate uint PrimeKernelCursorCall(ref PrimeLeafKernels.CombinatorialPrefixCursor cursor);

    private const uint PrimeKernelWidthLimit = (1U << 20);

    private static readonly (string Name, bool Supported, GourdonSegmentPositionRung Divide)[] PrimeKernelSegmentPositionRungs = [
        ("Vector512 halves", true, PrimeDivisionKernels.DivideGourdonSegmentPositions512<PrimeDivisionKernels.GourdonUnitsByHalves>),
        ("Vector512 fraction", Avx512Vbmi.IsSupported, PrimeDivisionKernels.DivideGourdonSegmentPositions512<PrimeDivisionKernels.GourdonUnitsByFraction>),
        ("Vector256", true, PrimeDivisionKernels.DivideGourdonSegmentPositions256),
        ("scalar", true, static (dividend, divisors, reciprocals, low, positions) => PrimeDivisionKernels.DivideGourdonSegmentPositionsScalar(
            dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals, start: 0)),
    ];
    // Each filter rung, the instruction set it needs and the lanes of one of its vectors.
    private static readonly (string Name, bool Supported, int Lanes, GourdonHardLeafFilter Filter)[] PrimeKernelFilterRungs = [
        ("VBMI2 compress", Avx512Vbmi2.IsSupported, 32, PrimeLeafKernels.FilterGourdonHardLeavesCompress),
        ("AVX2 pack", Avx2.IsSupported, 16, PrimeLeafKernels.FilterGourdonHardLeavesPack256),
        ("SSSE3 pack", Ssse3.IsSupported, 8, PrimeLeafKernels.FilterGourdonHardLeavesPack128),
        ("scalar", true, 4, PrimeLeafKernels.FilterGourdonHardLeavesScalar),
    ];

    /// <summary>Drives every public prime kernel shell with each violated precondition and checks the documented
    /// exception, naming the parameter, and that a host lacking a rung's instruction set refuses it while a host
    /// that has it answers.</summary>
    /// <returns>The first counterexample, or <see langword="null"/> when every shell refuses as documented.</returns>
    public static string? PrimeKernelShellRefusals() =>
        (PrimeDivisionShellRefusals()
        ?? (PrimeDivisionValueRefusals()
        ?? (PrimeLeafFilterShellRefusals()
        ?? (PrimeLeafSieveShellRefusals()
        ?? (PrimeLeafSieveSequenceRefusals()
        ?? (PrimeWordKernelShellRefusals()
        ?? PrimeCountingSeamRefusals()))))));

    // The division shells read every divisor once and name the first offender's index.
    private static string? PrimeDivisionValueRefusals() {
        var aoor = typeof(ArgumentOutOfRangeException);

        // A quotient reaches 2^48 for a divisor at or below dividend >> 48: with 2^60, 4096 refuses and 4097 is accepted.
        const ulong QuotientDividend = (1UL << 60);
        uint[] clean = [5_000_000, 6_000_000, 7_000_000, 8_000_000, 9_000_000, 10_000_000, 11_000_000, 12_000_000, 13_000_000];

        foreach (var (what, index, divisor) in new (string, int, uint)[] {
            ("a zero divisor at the first index", 0, 0U), ("a zero divisor", 4, 0U), ("a divisor reaching the quotient limit", 6, 4_096U),
            ("a divisor reaching the quotient limit at the last index", 8, 1U),
        }) {
            var divisors = ((uint[])clean.Clone());

            divisors[index] = divisor;
            foreach (var (name, divide) in GourdonQuotientRungs) {
                var failure = ExpectIndexedRefusal(what: $"{name} quotient rung with {what}", parameter: "divisors", index: index,
                    call: () => divide(QuotientDividend, divisors, new ulong[divisors.Length]));

                if (failure is not null) { return failure; }
            }
        }
        {
            var divisors = ((uint[])clean.Clone());

            divisors[6] = 4_097U;
            foreach (var (name, divide) in GourdonQuotientRungs) {
                var thrown = Thrown(action: () => divide(QuotientDividend, divisors, new ulong[divisors.Length]));

                if (thrown is not null) { return $"{name} quotient rung refused the divisor 4097, whose quotient stays below 2^48: {thrown.Message}"; }
            }
        }
        {
            // The scalar rung reads only from its start on.
            var divisors = ((uint[])clean.Clone());

            divisors[1] = 0U;
            var skipped = Thrown(action: () => PrimeDivisionKernels.DivideGourdonQuotientsScalar(dividend: QuotientDividend, divisors: divisors,
                quotients: new ulong[divisors.Length], start: 2));
            var reached = ExpectIndexedRefusal(what: "the scalar quotient rung reaching a zero divisor", parameter: "divisors", index: 1,
                call: () => PrimeDivisionKernels.DivideGourdonQuotientsScalar(dividend: QuotientDividend, divisors: divisors,
                    quotients: new ulong[divisors.Length], start: 1));

            if (skipped is not null) { return $"the scalar quotient rung refused a zero divisor before its start: {skipped.Message}"; }
            if (reached is not null) { return reached; }
        }

        // The window of a segment-position rung: 1 <= Q-low+1 < 2^28, divisors below 2^31, correctly rounded reciprocals.
        const ulong PositionDividend = (1UL << 40);
        uint[] positioned = [5_000, 6_000, 7_000, 8_000, 9_000, 10_000, 11_000, 12_000, 13_000];
        var inverses = positioned.Select(selector: static divisor => (1D / divisor)).ToArray();

        foreach (var (name, supported, divide) in PrimeKernelSegmentPositionRungs) {
            if (!supported) { continue; }
            foreach (var (what, index, divisor, type, parameter) in new (string, int, uint, Type, string)[] {
                ("a zero divisor", 2, 0U, aoor, "divisors"), ("a divisor of 2^31", 3, (1U << 31), aoor, "divisors"),
                ("a divisor of 2^32-1", 8, uint.MaxValue, aoor, "divisors"), ("a quotient at low + 2^28 - 1", 5, 4_096U, aoor, "divisors"),
            }) {
                var divisors = ((uint[])positioned.Clone());
                var reciprocals = ((double[])inverses.Clone());

                divisors[index] = divisor;
                reciprocals[index] = ((divisor == 0U) ? 0D : (1D / divisor));
                var failure = ExpectIndexedRefusal(what: $"{name} segment-position rung with {what}", parameter: parameter, index: index,
                    call: () => divide(PositionDividend, divisors, reciprocals, 0U, new ulong[divisors.Length]));

                if (failure is not null) { return failure; }
            }
            foreach (var index in new[] { 0, 4, 8 }) {
                var reciprocals = ((double[])inverses.Clone());

                reciprocals[index] = BitConverter.Int64BitsToDouble(value: (BitConverter.DoubleToInt64Bits(value: reciprocals[index]) + 1L));
                var thrown = Thrown(action: () => divide(PositionDividend, positioned, reciprocals, 0U, new ulong[positioned.Length]));

                if ((thrown is not ArgumentException { ParamName: "reciprocals" } refusal) || (thrown is ArgumentOutOfRangeException)
                    || !refusal.Message.Contains(value: $"reciprocals[{index}]")) {
                    return $"{name} segment-position rung with a reciprocal one ulp off at {index} must throw ArgumentException naming reciprocals[{index}]; it threw {(thrown?.GetType().Name ?? "nothing")}";
                }
            }

            // Q = 333 for 1000 / 3: a window starting at 333 holds it, one starting at 334 does not, and 4097 is the
            // first divisor of 2^40 whose quotient stays below the window's ceiling.
            var failureAtLow = (ExpectKernelAnswer(what: $"{name} segment-position rung with the quotient at low", supported: true,
                    valid: () => divide(1_000UL, [3U], [(1D / 3U)], 333U, new ulong[1]))
                ?? (ExpectKernelAnswer(what: $"{name} segment-position rung with the quotient at the last admitted offset", supported: true,
                    valid: () => divide(PositionDividend, [4_097U], [(1D / 4_097U)], 0U, new ulong[1]))
                ?? ExpectIndexedRefusal(what: $"{name} segment-position rung with the quotient below low", parameter: "divisors", index: 0,
                    call: () => divide(1_000UL, [3U], [(1D / 3U)], 334U, new ulong[1]))));

            if (failureAtLow is not null) { return failureAtLow; }
        }
        {
            var divisors = ((uint[])positioned.Clone());

            divisors[0] = 0U;
            var skipped = Thrown(action: () => PrimeDivisionKernels.DivideGourdonSegmentPositionsScalar(dividend: PositionDividend, divisors: divisors,
                low: 0U, positions: new ulong[divisors.Length], reciprocals: inverses, start: 1));

            return ((skipped is null) ? null : $"the scalar segment-position rung refused a zero divisor before its start: {skipped.Message}");
        }
    }
    // A refusal must be an ArgumentOutOfRangeException naming the parameter and the offending index.
    private static string? ExpectIndexedRefusal(string what, string parameter, int index, Action call) {
        var thrown = Thrown(action: call);

        return (((thrown is ArgumentOutOfRangeException argument) && (argument.ParamName == parameter) && argument.Message.Contains(value: $"{parameter}[{index}]"))
            ? null
            : $"{what} must throw ArgumentOutOfRangeException naming {parameter}[{index}]; it threw {(thrown?.GetType().Name ?? "nothing")}{((thrown is null) ? string.Empty : $": {thrown.Message}")}"
        );
    }
    private static string? PrimeCountingSeamRefusals() {
        // The quotient-counting root cap, (2^22-1)^2, below which no bound reaches the Gourdon counter.
        const ulong RootCapValue = 17_592_177_655_809UL;
        const ulong Bound = 100_000_000_000_000UL;
        var aoor = typeof(ArgumentOutOfRangeException);
        var token = CancellationToken.None;

        // Refusals precede cancellation, so withholding the bound check fails without starting a huge sieve.
        foreach (var (value, cutoff) in new (ulong, uint)[] {
            ((1UL << 49), 0U), (ulong.MaxValue, 0U), (ulong.MaxValue, 65_520U),
        }) {
            var failure = Refuses(action: () => _ = PrimeExploration.CountSemiprimes(value: value, cutoff: cutoff,
                cancellationToken: new CancellationToken(canceled: true)), type: aoor, parameterName: "cutoff",
                what: $"a semiprime cutoff {cutoff} at {value} admitting an out-of-domain leaf quotient");

            if (failure is not null) { return failure; }
        }
        foreach (var (value, cutoff) in new (ulong, uint)[] {
            ((1UL << 49), 2U), (ulong.MaxValue, 65_535U),
        }) {
            var failure = Refuses(action: () => _ = PrimeExploration.CountSemiprimes(value: value, cutoff: cutoff,
                cancellationToken: new CancellationToken(canceled: true)), type: typeof(OperationCanceledException), parameterName: null,
                what: $"an admissible semiprime cutoff {cutoff} at {value} reaching cancellation");

            if (failure is not null) { return failure; }
        }
        return (Refuses(action: () => _ = PrimeExtensions.CountCombinatorial64(value: RootCapValue, cutoff: 3_000_000U, cancellationToken: token), type: aoor,
                parameterName: "value", what: "an explicit-cutoff count at the quotient-counting root cap")
            ?? (Refuses(action: () => _ = PrimeExtensions.CountCombinatorial64(value: Bound, cutoff: 46_415U, cancellationToken: token), type: aoor,
                parameterName: "cutoff", what: "an explicit-cutoff count at the integer cube root")
            ?? (Refuses(action: () => _ = PrimeExtensions.CountCombinatorial64(value: Bound, cutoff: 10_000_000U, cancellationToken: token), type: aoor,
                parameterName: "cutoff", what: "an explicit-cutoff count at the integer square root")
            ?? (Refuses(action: () => _ = PrimeExtensions.CountCombinatorial64(value: 100_000_000_000_000_000UL, cutoff: ((1U << 27) + 1U), cancellationToken: token), type: aoor,
                parameterName: "cutoff", what: "an explicit-cutoff count past the table capacity")
            ?? (Refuses(action: () => _ = PrimeExploration.CountSemiprimes(value: uint.MaxValue, cutoff: 1U, cancellationToken: token), type: aoor,
                parameterName: "value", what: "a semiprime correction at the top of uint")
            ?? (Refuses(action: () => _ = PrimeExploration.CountSemiprimes(value: Bound, cutoff: 10_000_000U, cancellationToken: token), type: aoor,
                parameterName: "cutoff", what: "a semiprime correction at the integer square root")
            ?? (Refuses(action: () => _ = PrimeExploration.CountSemiprimes(value: Bound, cutoff: uint.MaxValue, cancellationToken: token), type: aoor,
                parameterName: "cutoff", what: "a semiprime correction past the integer square root")
            ?? ExpectKernelAnswer(what: "a semiprime correction at the square of a prime root", supported: true, valid: () => {
                if (PrimeExploration.CountSemiprimes(value: 4_295_098_369UL, cutoff: 1_625U, cancellationToken: token) == 0UL) { throw new InvalidOperationException(message: "no semiprimes"); }
            }))))))));
    }
    private static string? PrimeDivisionShellRefusals() {
        uint[] divisors = [3, 5, 7, 11, 13, 17, 19, 23, 29];
        var reciprocals = divisors.Select(selector: static divisor => (1D / divisor)).ToArray();
        var aoor = typeof(ArgumentOutOfRangeException);

        foreach (var (name, divide) in GourdonQuotientRungs) {
            var failure = Refuses(action: () => divide(1_000UL, divisors, new ulong[(divisors.Length - 1)]), type: aoor, parameterName: "quotients",
                what: $"{name} quotient rung with a short destination");

            if (failure is not null) { return failure; }
            failure = ExpectKernelAnswer(what: $"{name} quotient rung on valid input", supported: true, valid: () => divide(1_000UL, divisors, new ulong[divisors.Length]));
            if (failure is not null) { return failure; }
        }
        foreach (var start in new[] { -1, (divisors.Length + 1) }) {
            var failure = Refuses(action: () => PrimeDivisionKernels.DivideGourdonQuotientsScalar(dividend: 1_000UL, divisors: divisors,
                quotients: new ulong[divisors.Length], start: start), type: aoor, parameterName: "start", what: $"scalar quotient rung at start {start}");

            if (failure is not null) { return failure; }
        }
        foreach (var (name, supported, divide) in PrimeKernelSegmentPositionRungs) {
            var failure = ExpectKernelAnswer(what: $"{name} segment-position rung on valid input", supported: supported,
                valid: () => divide(1_000UL, divisors, reciprocals, 0U, new ulong[divisors.Length]));

            if (failure is not null) { return failure; }
            if (!supported) { continue; }
            failure = (Refuses(action: () => divide(1_000UL, divisors, reciprocals[..^1], 0U, new ulong[divisors.Length]), type: aoor,
                parameterName: "reciprocals", what: $"{name} segment-position rung with short reciprocals")
                ?? Refuses(action: () => divide(1_000UL, divisors, reciprocals, 0U, new ulong[(divisors.Length - 1)]), type: aoor,
                parameterName: "positions", what: $"{name} segment-position rung with a short destination"));
            if (failure is not null) { return failure; }
        }
        foreach (var start in new[] { -1, (divisors.Length + 1) }) {
            var failure = Refuses(action: () => PrimeDivisionKernels.DivideGourdonSegmentPositionsScalar(dividend: 1_000UL, divisors: divisors,
                low: 0U, positions: new ulong[divisors.Length], reciprocals: reciprocals, start: start), type: aoor, parameterName: "start",
                what: $"scalar segment-position rung at start {start}");

            if (failure is not null) { return failure; }
        }
        // The fraction lookup names the instruction set it lacks, rather than failing inside the permute.
        if (!Avx512Vbmi.IsSupported) {
            var unsupported = Thrown(action: () => PrimeDivisionKernels.DivideGourdonSegmentPositions512<PrimeDivisionKernels.GourdonUnitsByFraction>(
                dividend: 1_000UL, divisors: divisors, low: 0U, positions: new ulong[divisors.Length], reciprocals: reciprocals));

            if ((unsupported is not PlatformNotSupportedException { Message: var message }) || !message.Contains(value: "AVX-512 VBMI")) {
                return $"the VBMI fraction rung on a host without VBMI must name AVX-512 VBMI in a PlatformNotSupportedException; it threw {(unsupported?.GetType().Name ?? "nothing")}";
            }
        }
        if ((PrimeDivisionKernels.GourdonUnitsByHalves.IsSupported != true) || (PrimeDivisionKernels.GourdonUnitsByFraction.IsSupported != Avx512Vbmi.IsSupported)) {
            return "the units-below lookups must report the host's instruction-set support";
        }
        if ((PrimeDivisionKernels.GourdonUnitsByHalves.InstructionSet.Length == 0) || (PrimeDivisionKernels.GourdonUnitsByFraction.InstructionSet.Length == 0)) {
            return "the units-below lookups must name their instruction sets";
        }
        for (var divisor = 1UL; (divisor < 40UL); ++divisor) {
            var reciprocal = PrimeDivisionKernels.FloorReciprocal(divisor: divisor);
            var failure = (Refuses(action: () => PrimeDivisionKernels.DivideByFloorReciprocal(dividend: 100UL, divisor: divisor, reciprocal: (reciprocal - 1UL)),
                type: typeof(ArgumentException), parameterName: "reciprocal", what: $"floor-reciprocal division by {divisor} with a wrong reciprocal")
                ?? ExpectKernelAnswer(what: $"floor-reciprocal division by {divisor}", supported: true, valid: () => {
                    if (PrimeDivisionKernels.DivideByFloorReciprocal(dividend: 100UL, divisor: divisor, reciprocal: reciprocal) != (100UL / divisor)) {
                        throw new InvalidOperationException(message: "wrong quotient");
                    }
                }));

            if (failure is not null) { return failure; }
        }
        return (Refuses(action: () => PrimeDivisionKernels.DivideByFloorReciprocal(dividend: 1UL, divisor: 0UL, reciprocal: 0UL), type: aoor, parameterName: "divisor",
            what: "floor-reciprocal division by zero")
            ?? Refuses(action: () => _ = PrimeDivisionKernels.FloorReciprocal(divisor: 0UL), type: aoor, parameterName: "divisor",
                what: "a floor reciprocal of zero"));
    }
    private static string? PrimeLeafFilterShellRefusals() {
        var factors = Enumerable.Repeat(count: 64, element: ((ushort)0x7FFF)).ToArray();
        var aoor = typeof(ArgumentOutOfRangeException);

        foreach (var (name, supported, lanes, filter) in PrimeKernelFilterRungs) {
            string? Run(string what, string? parameter, int coordinate = 40, int last = -1, uint prime = 23U, int divisors = 128, int negations = 128) {
                Action call = () => {
                    var current = coordinate;
                    var subtracted = 0;

                    _ = filter(factors, ref current, last, prime, new uint[divisors], new short[negations], ref subtracted);
                };

                return ((parameter is null)
                    ? ExpectKernelAnswer(supported: supported, valid: call, what: $"{name} filter {what}")
                    : (supported ? Refuses(action: call, parameterName: parameter, type: aoor, what: $"{name} filter {what}") : null)
                );
            }

            var failure = (Run(what: "on valid input", parameter: null)
                ?? (Run(what: "with a coordinate past the table", parameter: "coordinate", coordinate: factors.Length)
                ?? (Run(what: "with a last below -1", parameter: "last", last: -2)
                ?? (Run(what: "with a last past the coordinate", parameter: "last", last: 41)
                ?? (Run(what: "with a prime of 32768", parameter: "prime", prime: 32_768U)
                ?? (Run(what: "with divisors shorter than one vector", parameter: "divisors", divisors: (lanes - 1), negations: (lanes - 1))
                ?? Run(what: "with short negations", parameter: "negations", negations: 127)))))));

            if (failure is not null) { return failure; }
            if (!supported) {
                // An absent instruction set refuses before any argument is read.
                failure = Refuses(action: () => {
                    var current = 40;
                    var subtracted = 0;

                    _ = filter(factors, ref current, -1, 23U, new uint[128], new short[128], ref subtracted);
                }, type: typeof(PlatformNotSupportedException), parameterName: null, what: $"{name} filter on a host without its instruction set");
                if (failure is not null) { return failure; }
            }
        }
        return null;
    }
    private static string? PrimeLeafSieveShellRefusals() {
        var primes = GourdonLeafSievePrimes;
        var primeCount = (primes.Length - 1);
        var aoor = typeof(ArgumentOutOfRangeException);
        var sieve = new PrimeLeafKernels.CombinatorialLeafSieve();
        var fresh = new PrimeLeafKernels.CombinatorialLeafSieve();
        var failure = (Refuses(action: () => fresh.CrossOff(index: PrimeLeafKernelsFirstIndex, prime: primes[PrimeLeafKernelsFirstIndex]), type: aoor, parameterName: "index",
                what: "a crossing before any Reset")
            ?? (Refuses(action: () => sieve.Reset(primeCount: primeCount, primes: null!, width: GourdonLeafSieveWidth), type: typeof(ArgumentNullException),
                parameterName: "primes", what: "Reset without primes")
            ?? (Refuses(action: () => sieve.Reset(primeCount: primeCount, primes: primes, width: (PrimeKernelWidthLimit + 1U)), type: aoor, parameterName: "width",
                what: "Reset beyond the width limit")
            ?? (Refuses(action: () => sieve.Reset(primeCount: -1, primes: primes, width: GourdonLeafSieveWidth), type: aoor, parameterName: "primeCount",
                what: "Reset with a negative prime count")
            ?? (Refuses(action: () => sieve.Reset(width: GourdonLeafSieveWidth, primes: primes, primeCount: primes.Length), type: aoor, parameterName: "primeCount",
                what: "Reset with a prime count past the list")
            ?? (Refuses(action: () => sieve.Initialize(low: 0UL, width: 0U), type: aoor, parameterName: "width", what: "Initialize with an empty window")
            ?? (Refuses(action: () => sieve.Initialize(low: 0UL, width: (PrimeKernelWidthLimit + 1U)), type: aoor, parameterName: "width",
                what: "Initialize beyond the width limit")
            ?? (Refuses(action: () => sieve.Initialize(low: 29UL, width: GourdonLeafSieveWidth), type: typeof(ArgumentException), parameterName: "low",
                what: "Initialize below a multiple of thirty")
            ?? Refuses(action: () => sieve.Initialize(low: 31UL, width: GourdonLeafSieveWidth), type: typeof(ArgumentException), parameterName: "low",
                what: "Initialize above a multiple of thirty")))))))));

        if (failure is not null) { return failure; }
        sieve.Reset(primeCount: primeCount, primes: primes, width: GourdonLeafSieveWidth);
        sieve.Initialize(low: 0UL, width: GourdonLeafSieveWidth);

        var crossings = new (string Name, Action<int, uint> Cross)[] {
            ("dispatch", (index, prime) => sieve.CrossOff(index: index, prime: prime)),
            ("Vector512", (index, prime) => sieve.CrossOff<VectorLanes512, Vector512<byte>>(index: index, prime: prime)),
            ("Vector256", (index, prime) => sieve.CrossOff<VectorLanes256, Vector256<byte>>(index: index, prime: prime)),
            ("Vector128", (index, prime) => sieve.CrossOff<VectorLanes128, Vector128<byte>>(index: index, prime: prime)),
        };

        foreach (var (name, cross) in crossings) {
            failure = (Refuses(action: () => cross((PrimeLeafKernelsFirstIndex - 1), 19U), type: aoor, parameterName: "index", what: $"{name} crossing below the ninth prime")
                ?? (Refuses(action: () => cross((primeCount + 1), 2U), type: aoor, parameterName: "index", what: $"{name} crossing past the prime count")
                ?? (Refuses(action: () => cross(-1, 2U), type: aoor, parameterName: "index", what: $"{name} crossing at a negative index")
                ?? Refuses(action: () => cross(PrimeLeafKernelsFirstIndex, (primes[PrimeLeafKernelsFirstIndex] + 2U)), type: typeof(ArgumentException), parameterName: "prime",
                    what: $"{name} crossing of a prime that is not the list's entry"))));
            if (failure is not null) { return failure; }
        }
        for (var index = PrimeLeafKernelsFirstIndex; (index < (PrimeLeafKernelsFirstIndex + 4)); ++index) {
            var thrown = Thrown(action: () => sieve.CrossOff(index: index, prime: primes[index]));

            if (thrown is not null) { return $"a valid crossing of prime {primes[index]} threw {thrown.GetType().Name}"; }
        }

        // A cursor over the 256-word window the sieve now holds.
        var words = ((int)(GourdonLeafSieveWidth / 240U));
        var last = ((nuint)((words * 240) - 1));

        foreach (var (what, window, supported) in new[] {
            ("AVX-512BW", PrimeLeafKernels.CombinatorialPrefixWindowKind.Avx512BW, Avx512BW.IsSupported),
            ("AVX2", PrimeLeafKernels.CombinatorialPrefixWindowKind.Avx2, Avx2.IsSupported),
            ("scalar", PrimeLeafKernels.CombinatorialPrefixWindowKind.Scalar, true),
        }) {
            var thrown = CursorThrown(sieve: sieve, result: out var counted, call: (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: 2_400U, window: window));

            if (supported) {
                var reference = CursorThrown(sieve: sieve, result: out var expected, call: (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: 2_400U));

                if ((thrown is not null) || (reference is not null) || (counted != expected)) {
                    return $"the {what} prefix window must count what the dispatching query counts, {expected}; it gave {counted} and threw {(thrown?.GetType().Name ?? "nothing")}";
                }
            } else if (thrown is not PlatformNotSupportedException) {
                return $"the {what} prefix window on a host without it must throw PlatformNotSupportedException; it threw {(thrown?.GetType().Name ?? "nothing")}";
            }
        }
        foreach (var (what, call, parameter) in new (string, PrimeKernelCursorCall, string)[] {
            ("a query past the last word", (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: (last + 1)), "offset"),
            ("a query past the last word through a window", (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: (last + 1),
                window: PrimeLeafKernels.CombinatorialPrefixWindowKind.Scalar), "offset"),
            ("a query in an earlier word", (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => { _ = cursor.Count(offset: 2_400U); return cursor.Count(offset: 720U); }, "offset"),
            ("an undefined window", (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: 0U, window: ((PrimeLeafKernels.CombinatorialPrefixWindowKind)99)), "window"),
        }) {
            var thrown = CursorThrown(call: call, result: out _, sieve: sieve);

            if ((thrown is not ArgumentOutOfRangeException argument) || (argument.ParamName != parameter)) {
                return $"{what} must throw ArgumentOutOfRangeException naming '{parameter}'; it threw {(thrown?.GetType().Name ?? "nothing")}";
            }
        }

        // A sieve that holds no window offers a cursor with no words to read.
        var unwindowed = CursorThrown(sieve: new PrimeLeafKernels.CombinatorialLeafSieve(), result: out _, call: (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: 0U));

        return (((unwindowed is ArgumentOutOfRangeException { ParamName: "offset" }))
            ? null
            : $"a query on a cursor over no window must throw ArgumentOutOfRangeException naming 'offset'; it threw {(unwindowed?.GetType().Name ?? "nothing")}"
        );
    }
    private static string? PrimeLeafSieveSequenceRefusals() {
        uint[] primes = [0, 2, 3, 5, 7, 11, 13, 17, 19, 23, 29];
        var argument = typeof(ArgumentException);

        foreach (var (name, cross) in GourdonLeafSieveRungs.Append(element: (
            "dispatch", ((GourdonLeafSieveCrossing)(static (sieve, index, prime) => sieve.CrossOff(index: index, prime: prime)))))) {
            var sieve = new PrimeLeafKernels.CombinatorialLeafSieve();

            sieve.Reset(primeCount: 10, primes: primes, width: 240U);
            var failure = (Refuses(action: () => cross(sieve, 9, 23U), type: argument, parameterName: "index", what: $"{name} crossing before Initialize")
                ?? Refuses(action: () => sieve.Initialize(low: 30UL, width: 240U), type: argument, parameterName: "low",
                    what: $"{name} first window starting at 30 with crossing states based at zero"));

            if (failure is not null) { return failure; }
            sieve.Initialize(low: 0UL, width: 240U);
            failure = Refuses(action: () => cross(sieve, 10, 29U), type: argument, parameterName: "index", what: $"{name} crossing skipping index nine");
            if (failure is not null) { return failure; }
            cross(sieve, 9, 23U);
            failure = (Refuses(action: () => cross(sieve, 9, 23U), type: argument, parameterName: "index", what: $"{name} repeated crossing")
                ?? Refuses(action: () => sieve.Initialize(low: 240UL, width: 240U), type: argument, parameterName: "low",
                    what: $"{name} next window before all crossings finish"));
            if (failure is not null) { return failure; }
            cross(sieve, 10, 29U);
            sieve.Initialize(low: 240UL, width: 30U);
            cross(sieve, 9, 23U);
            cross(sieve, 10, 29U);
            failure = Refuses(action: () => sieve.Initialize(low: 270UL, width: 240U), type: argument, parameterName: "low",
                what: $"{name} next window before the preceding partial word ends");
            if (failure is not null) { return failure; }
            sieve.Initialize(low: 480UL, width: 240U);
            sieve.Reset(primeCount: 9, primes: primes, width: 240U);
            var empty = CursorThrown(sieve: sieve, result: out _, call: (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor) => cursor.Count(offset: 0U));

            if ((sieve.TotalCount != 0U) || (empty is not ArgumentOutOfRangeException { ParamName: "offset" })) {
                return $"{name} Reset retained the previous window";
            }
            sieve.Initialize(low: 0UL, width: 240U);
            cross(sieve, 9, 23U);
            // Reuse shrinks both the list and the traversal; the next window needs only one crossing.
            sieve.Initialize(low: 240UL, width: 240U);
            cross(sieve, 9, 23U);
            foreach (var invalid in new[] { 0U, 19U, 30U }) {
                var invalidPrimes = ((uint[])primes.Clone());

                invalidPrimes[9] = invalid;
                sieve.Reset(primeCount: 9, primes: invalidPrimes, width: 240U);
                // Validate the divisor before window state, so withholding this guard cannot enter a zero-period loop.
                failure = Refuses(action: () => cross(sieve, 9, invalid), type: argument, parameterName: "prime",
                    what: $"{name} crossing invalid divisor {invalid}");
                if (failure is not null) { return failure; }
            }
        }
        return null;
    }
    private static string? PrimeWordKernelShellRefusals() {
        foreach (var (residue, offset) in new[] {
            PacketOffsets<PrimeSieveKernels.PacketResidue1>(), PacketOffsets<PrimeSieveKernels.PacketResidue7>(),
            PacketOffsets<PrimeSieveKernels.PacketResidue11>(), PacketOffsets<PrimeSieveKernels.PacketResidue13>(),
            PacketOffsets<PrimeSieveKernels.PacketResidue17>(), PacketOffsets<PrimeSieveKernels.PacketResidue19>(),
            PacketOffsets<PrimeSieveKernels.PacketResidue23>(), PacketOffsets<PrimeSieveKernels.PacketResidue29>(),
        }) {
            var limit = ((uint.MaxValue - residue) / 30U);
            var failure = (Refuses(action: () => _ = offset(0, 1U), type: typeof(ArgumentOutOfRangeException), parameterName: "phase",
                    what: $"packet residue {residue} at phase zero")
                ?? (Refuses(action: () => _ = offset(8, 1U), type: typeof(ArgumentOutOfRangeException), parameterName: "phase",
                    what: $"packet residue {residue} past the last phase")
                ?? Refuses(action: () => _ = offset(7, (limit + 1U)), type: typeof(ArgumentOutOfRangeException), parameterName: "quotient",
                    what: $"packet residue {residue} beyond the largest uint prime")));

            if (failure is not null) { return failure; }
            if (offset(7, limit) != (((((nint)28) * limit) + residue) - 1)) { return $"packet residue {residue} at the quotient bound gave the wrong offset"; }
        }
        var invalidResidue = Refuses(action: () => _ = PrimeSieveKernels.PacketOffset<PrimeKernelInvalidPacketResidue>(phase: 1, quotient: 0U),
            type: typeof(ArgumentException), parameterName: "TResidue", what: "a packet residue outside the wheel");

        if (invalidResidue is not null) { return invalidResidue; }
        foreach (var group in new[] { -1, PrimeSieveKernels.SmallPrimePatterns.Count }) {
            var failure = Refuses(action: () => _ = PrimeSieveKernels.SmallPrimePatterns.Period(group: group).Length,
                type: typeof(ArgumentOutOfRangeException), parameterName: "group", what: $"a presieve pattern at invalid group {group}");

            if (failure is not null) { return failure; }
        }
        return (Refuses(action: () => _ = PrimeKernels.CountOddPrimesBelow(value: 65_537U), type: typeof(ArgumentOutOfRangeException), parameterName: "value",
            what: "an odd-prime rank past the table")
        ?? (ExpectKernelAnswer(what: "the odd-prime rank at the table's end", supported: true, valid: () => {
            if (PrimeKernels.CountOddPrimesBelow(value: 65_536U) != 6_541) { throw new InvalidOperationException(message: "wrong rank"); }
        })
        ?? (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 3UL, residue: 1UL), type: typeof(ArgumentOutOfRangeException), parameterName: "magnitude",
            what: "a Selfridge symbol below the first magnitude")
        ?? (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 64UL, residue: 1UL), type: typeof(ArgumentOutOfRangeException), parameterName: "magnitude",
            what: "a Selfridge symbol at the table limit")
        ?? (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 6UL, residue: 1UL), type: typeof(ArgumentOutOfRangeException), parameterName: "magnitude",
            what: "a Selfridge symbol over an even magnitude")
        ?? (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 7UL, residue: 7UL), type: typeof(ArgumentOutOfRangeException), parameterName: "residue",
            what: "a Selfridge symbol of a residue that is not reduced")
        ?? ExpectKernelAnswer(what: "the Selfridge symbol (2/7)", supported: true, valid: () => {
            if (PrimeKernels.SelfridgeSymbol(magnitude: 7UL, residue: 2UL) != 1) { throw new InvalidOperationException(message: "wrong symbol"); }
        })))))));
    }

    private const int PrimeLeafKernelsFirstIndex = 9;

    private readonly struct PrimeKernelInvalidPacketResidue : PrimeSieveKernels.IPacketResidue {
        public static ulong Masks => 0UL;
        public static uint Residue => 30U;
    }

    private static Exception? Thrown(Action action) {
        try {
            action();
            return null;
        } catch (Exception thrown) {
            return thrown;
        }
    }
    // A kernel that needs an instruction set answers where the host has it and throws PlatformNotSupportedException
    // where it does not.
    private static string? ExpectKernelAnswer(string what, bool supported, Action valid) {
        var thrown = Thrown(action: valid);

        if (supported) {
            return ((thrown is null) ? null : $"{what} threw {thrown.GetType().Name}: {thrown.Message}");
        }

        return ((thrown is PlatformNotSupportedException) ? null : $"{what} on a host without its instruction set must throw PlatformNotSupportedException; it threw {(thrown?.GetType().Name ?? "nothing")}");
    }
    private static Exception? CursorThrown(PrimeLeafKernels.CombinatorialLeafSieve sieve, PrimeKernelCursorCall call, out uint result) {
        var cursor = sieve.StartPrefix();

        try {
            result = call(ref cursor);
            return null;
        } catch (Exception thrown) {
            result = 0U;
            return thrown;
        }
    }
}
