using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? PowerOfTwoBitOracle() =>
        AtEveryIntegerWidth<PowerOfTwoBitOracleWidths>();
    public static string? LowMaskBitOracle() {
        var failure = AtEveryIntegerWidth<LowMaskBitOracleWidths>();

        if (failure is not null) { return failure; }

        // An unbounded carrier has no top: every non-negative count is a mask.
        for (var count = 0; (count <= 200); ++count) {
            var actual = count.LowMask<BigInteger>();

            if (actual != ((BigInteger.One << count) - 1)) { return $"BigInteger count={count} mask={actual}"; }
        }

        return null;
    }
    public static string? SmearBelowHighestSetBitBitOracle(long[] left, long[] right) {
        var raw = Raw128(
            high: left[0],
            low: left[1]
        );

        return (AtEveryIntegerWidth<SmearWidths>(raw: raw) ?? AtEveryIntegerWidth<SmearWidths>(raw: (raw >> ((int)(((ulong)right[0]) % 128UL)))));
    }
    public static string? AlignBitOracle(long[] left, long[] right) {
        var raw = Raw128(
            high: left[0],
            low: left[1]
        );
        var selector = ((ulong)right[0]);

        return AtEveryIntegerWidth<AlignWidths>(
            raw: raw,
            selector: selector
        );
    }
    public static string? ParallelBitDepositExtractBitOracle(long[] left, long[] right) {
        var value = Raw128(
            high: left[0],
            low: left[1]
        );
        var mask = Raw128(
            high: right[0],
            low: right[1]
        );

        // A dense random mask and a sparse one built from it, so both long and short scatters are swept.
        return (AtEveryIntegerWidth<ParallelBitWidths>(
            raw: value,
            selector: mask
        ) ?? AtEveryIntegerWidth<ParallelBitWidths>(
            raw: value,
            selector: mask & (mask >> 3) & (mask >> 7)
        ));
    }
    public static string? FastParallelBitsDecisionTable() {
        foreach (var (processor, vendor, family, hasBmi2, expected) in FastParallelBitsRows) {
            var actual = BitManipulation.IsParallelBitsFast(
                family: family,
                hasBmi2: hasBmi2,
                vendor: vendor
            );

            if (actual != expected) { return $"{processor}: vendor={vendor} family=0x{family:X} bmi2={hasBmi2} fast={actual}, expected {expected}"; }
        }

        if (!X86Base.IsSupported) {
            return (BitManipulation.HasFastParallelBits ? "a host that is not x86 reports fast PDEP/PEXT" : null);
        }

        // The host's own CPUID, decoded here character by character rather than through the subject's reader.
        var (_, ebx, ecx, edx) = X86Base.CpuId(
            functionId: 0,
            subFunctionId: 0
        );
        var signature = X86Base.CpuId(
            functionId: 1,
            subFunctionId: 0
        ).Eax;
        var hostVendor = string.Concat(
            str0: CpuIdChars(register: ebx),
            str1: CpuIdChars(register: edx),
            str2: CpuIdChars(register: ecx)
        );
        var hostFamily = (signature >> 8) & 0xF;

        if (15 == hostFamily) { hostFamily += (signature >> 20) & 0xFF; }

        var hostExpected = BitManipulation.IsParallelBitsFast(
            family: hostFamily,
            hasBmi2: Bmi2.IsSupported,
            vendor: hostVendor
        );

        return ((BitManipulation.HasFastParallelBits != hostExpected)
            ? $"host vendor={hostVendor} family=0x{hostFamily:X} bmi2={Bmi2.IsSupported}: HasFastParallelBits={BitManipulation.HasFastParallelBits}, the decision gives {hostExpected}"
            : null);
    }
    public static string? MortonPathsBitOracle(long[] left, long[] right) {
        var a = Raw128(
            high: left[0],
            low: left[1]
        );
        var b = Raw128(
            high: right[0],
            low: right[1]
        );
        var c = (a * 0x9E3779B97F4A7C15UL) ^ (b >> 5);

        return ((Pair<byte, ushort>(a: a, b: b) ?? (Pair<sbyte, short>(a: a, b: b) ?? (Pair<ushort, uint>(a: a, b: b) ?? (Pair<short, int>(a: a, b: b) ??
            (Pair<uint, ulong>(a: a, b: b) ?? (Pair<int, long>(a: a, b: b) ?? (Pair<ulong, UInt128>(a: a, b: b) ?? (Pair<long, Int128>(a: a, b: b) ??
            (Pair<uint, uint>(a: a, b: b) ?? (Pair<byte, ulong>(a: a, b: b) ?? (Pair<short, ulong>(a: a, b: b) ?? Pair<ulong, uint>(a: a, b: b)))))))))))) ??
            ((Unpair<ushort, byte>(value: a) ?? (Unpair<short, sbyte>(value: a) ?? (Unpair<uint, ushort>(value: a) ?? (Unpair<int, short>(value: a) ??
            (Unpair<ulong, uint>(value: a) ?? (Unpair<long, int>(value: a) ?? (Unpair<UInt128, ulong>(value: a) ?? (Unpair<Int128, long>(value: a) ??
            (Unpair<uint, uint>(value: a) ?? (Unpair<ushort, uint>(value: a) ?? (Unpair<ulong, byte>(value: a) ?? Unpair<uint, ulong>(value: b)))))))))))) ??
            ((Triple<byte, byte>(a: a, b: b, c: c) ?? (Triple<byte, uint>(a: a, b: b, c: c) ?? (Triple<sbyte, int>(a: a, b: b, c: c) ??
            (Triple<ushort, ulong>(a: a, b: b, c: c) ?? (Triple<short, long>(a: a, b: b, c: c) ?? (Triple<uint, ulong>(a: a, b: b, c: c) ??
            (Triple<int, long>(a: a, b: b, c: c) ?? (Triple<ulong, UInt128>(a: a, b: b, c: c) ?? (Triple<long, Int128>(a: a, b: b, c: c) ??
            Triple<ushort, ushort>(a: a, b: b, c: c)))))))))) ??
            (Untriple<byte, byte>(value: a) ?? (Untriple<ushort, ushort>(value: a) ?? (Untriple<uint, byte>(value: a) ?? (Untriple<ulong, uint>(value: a) ??
            (Untriple<long, int>(value: a) ?? (Untriple<ulong, ushort>(value: a) ?? (Untriple<UInt128, ulong>(value: a) ?? (Untriple<Int128, long>(value: b) ??
            Untriple<ulong, ulong>(value: b))))))))))));
    }
    public static string? BitKernelRefusals() {
        var failure = AtEveryIntegerWidth<BitKernelRefusalWidths>();

        if (failure is not null) { return failure; }

        return (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => (-1).NthPowerOfTwo<BigInteger>(), parameter: "exponent") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => (-1).LowMask<BigInteger>(), parameter: "count") ??
            (MagicConstantRefusal<NotSupportedException>(action: () => 0.NthFermatMask<BigInteger>()) ??
            (MagicConstantRefusal<NotSupportedException>(action: () => BigInteger.One.ParallelBitDeposit(mask: BigInteger.One)) ??
            (MagicConstantRefusal<NotSupportedException>(action: () => BigInteger.One.ParallelBitExtract(mask: BigInteger.One)) ??
            (MagicConstantRefusal<NotSupportedException>(action: () => BigInteger.One.SmearBelowHighestSetBit()) ??
            (MagicConstantRefusal<NotSupportedException>(action: () => BigInteger.One.BitwiseTriple<BigInteger, ulong>(second: BigInteger.One, third: BigInteger.One)) ??
            (MagicConstantRefusal<NotSupportedException>(action: () => 1UL.BitwiseTriple<ulong, BigInteger>(second: 1UL, third: 1UL)) ??
            (MagicConstantRefusal<NotSupportedException>(action: () => BigInteger.One.BitwiseUntriple<BigInteger, ulong>()) ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => BigInteger.One.AlignUp(alignment: 3), parameter: "alignment") ??
            AlignBigIntegerCheck()))))))))));
    }

    private static string? AlignBigIntegerCheck() {
        // An unbounded carrier rounds exactly, with no wrap at either end.
        var huge = ((BigInteger.One << 200) + 5);
        var alignment = (BigInteger.One << 70);

        if (huge.AlignDown(alignment: alignment) != (BigInteger.One << 200)) { return "BigInteger AlignDown"; }
        if (huge.AlignUp(alignment: alignment) != ((BigInteger.One << 200) + alignment)) { return "BigInteger AlignUp"; }
        if ((-huge).AlignDown(alignment: alignment) != (-((BigInteger.One << 200) + alignment))) { return "BigInteger negative AlignDown"; }

        return null;
    }
    private static UInt128 Raw128(long high, long low) =>
        (((UInt128)((ulong)high)) << 64) | ((ulong)low);
    private static int WidthOf<T>() => (Unsafe.SizeOf<T>() * 8);
    private static UInt128 BitsOf<T>(T value) where T : IBinaryInteger<T> =>
        UInt128.CreateTruncating(value: value) & Oracles.WordMask(width: WidthOf<T>());
    private static string? PowerOfTwoBitOracle<T>() where T : IBinaryInteger<T> {
        var bits = WidthOf<T>();

        for (var exponent = 0; (exponent < bits); ++exponent) {
            var actual = BitsOf(value: exponent.NthPowerOfTwo<T>());

            if (actual != (UInt128.One << exponent)) { return $"{typeof(T).Name} exponent={exponent} bits={actual:X}"; }
        }

        return null;
    }
    private static string? LowMaskBitOracle<T>() where T : IBinaryInteger<T> {
        var bits = WidthOf<T>();

        for (var count = 0; (count <= bits); ++count) {
            var actual = BitsOf(value: count.LowMask<T>());
            var expected = ((count == 0)
                ? UInt128.Zero
                : Oracles.WordMask(width: count));

            if (actual != expected) { return $"{typeof(T).Name} count={count} bits={actual:X}, expected={expected:X}"; }
        }

        return null;
    }
    private static string? SmearBitOracle<T>(UInt128 raw) where T : IBinaryInteger<T> {
        var value = T.CreateTruncating(value: raw);
        var actual = BitsOf(value: value.SmearBelowHighestSetBit());
        var expected = Oracles.SmearBelowHighestSetBit(
            bits: BitsOf(value: value),
            width: WidthOf<T>()
        );

        return ((actual != expected)
            ? $"{typeof(T).Name} value={BitsOf(value: value):X} smear={actual:X}, expected={expected:X}"
            : null);
    }
    private static string? AlignBitOracle<T>(UInt128 raw, ulong selector) where T : IBinaryInteger<T> {
        var bits = WidthOf<T>();
        var value = T.CreateTruncating(value: raw);
        // A signed carrier's top bit is its sign, so its largest positive power of two sits one below.
        var exponentCount = (bits - (T.IsNegative(value: T.AllBitsSet) ? 1 : 0));
        var exponent = ((int)(selector % ((ulong)exponentCount)));
        var alignment = (T.One << exponent);
        var mathematical = BigInteger.CreateTruncating(value: value);
        var down = BitsOf(value: value.AlignDown(alignment: alignment));
        var up = BitsOf(value: value.AlignUp(alignment: alignment));
        var expectedDown = Oracles.Align(
            alignment: (BigInteger.One << exponent),
            up: false,
            value: mathematical,
            width: bits
        );
        var expectedUp = Oracles.Align(
            alignment: (BigInteger.One << exponent),
            up: true,
            value: mathematical,
            width: bits
        );

        if (((BigInteger)down) != expectedDown) { return $"{typeof(T).Name} AlignDown value={mathematical} alignment=2^{exponent}: {down:X}, expected={expectedDown:X}"; }
        if (((BigInteger)up) != expectedUp) { return $"{typeof(T).Name} AlignUp value={mathematical} alignment=2^{exponent}: {up:X}, expected={expectedUp:X}"; }

        return null;
    }
    private static string? ParallelBitBitOracle<T>(UInt128 raw, UInt128 rawMask) where T : IBinaryInteger<T> {
        var bits = WidthOf<T>();
        var value = T.CreateTruncating(value: raw);
        var mask = T.CreateTruncating(value: rawMask);
        var expectedDeposit = Oracles.DepositBits(
            mask: BitsOf(value: mask),
            value: BitsOf(value: value),
            width: bits
        );
        var expectedExtract = Oracles.ExtractBits(
            mask: BitsOf(value: mask),
            value: BitsOf(value: value),
            width: bits
        );

        if (BitsOf(value: value.ParallelBitDeposit(mask: mask)) != expectedDeposit) { return $"{typeof(T).Name} ParallelBitDeposit value={BitsOf(value: value):X} mask={BitsOf(value: mask):X}"; }
        if (BitsOf(value: value.ParallelBitExtract(mask: mask)) != expectedExtract) { return $"{typeof(T).Name} ParallelBitExtract value={BitsOf(value: value):X} mask={BitsOf(value: mask):X}"; }
        if (BitsOf(value: BitKernelProbes<T>.DepositInSoftware(arg1: value, arg2: mask)) != expectedDeposit) { return $"{typeof(T).Name} software deposit value={BitsOf(value: value):X} mask={BitsOf(value: mask):X}"; }
        if (BitsOf(value: BitKernelProbes<T>.ExtractInSoftware(arg1: value, arg2: mask)) != expectedExtract) { return $"{typeof(T).Name} software extract value={BitsOf(value: value):X} mask={BitsOf(value: mask):X}"; }

        return null;
    }
    private static string? Pair<TInput, TResult>(UInt128 a, UInt128 b) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var x = TInput.CreateTruncating(value: a);
        var y = TInput.CreateTruncating(value: b);
        var expected = Oracles.Interleave(
            inputWidth: WidthOf<TInput>(),
            operands: [BitsOf(value: x), BitsOf(value: y)],
            resultWidth: WidthOf<TResult>()
        );
        var name = $"{typeof(TInput).Name}->{typeof(TResult).Name}";

        if (BitsOf(value: x.BitwisePair<TInput, TResult>(other: y)) != expected) { return $"BitwisePair {name} x={BitsOf(value: x):X} y={BitsOf(value: y):X}"; }
        if (BitsOf(value: MortonProbes<TInput, TResult>.PairBySwar(arg1: x, arg2: y)) != expected) { return $"BitwisePair SWAR {name} x={BitsOf(value: x):X} y={BitsOf(value: y):X}"; }

        return null;
    }
    private static string? Unpair<TInput, TResult>(UInt128 value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var z = TInput.CreateTruncating(value: value);
        var name = $"{typeof(TInput).Name}->{typeof(TResult).Name}";

        var (even, odd) = z.BitwiseUnpair<TInput, TResult>();
        var (swarEven, swarOdd) = MortonProbes<TInput, TResult>.UnpairBySwar(arg: z);

        for (var lane = 0; (lane < 2); ++lane) {
            var expected = Oracles.Deinterleave(
                inputWidth: WidthOf<TInput>(),
                lane: lane,
                resultWidth: WidthOf<TResult>(),
                value: BitsOf(value: z),
                ways: 2
            );

            if (BitsOf(value: ((lane == 0) ? even : odd)) != expected) { return $"BitwiseUnpair {name} lane={lane} z={BitsOf(value: z):X}"; }
            if (BitsOf(value: ((lane == 0) ? swarEven : swarOdd)) != expected) { return $"BitwiseUnpair SWAR {name} lane={lane} z={BitsOf(value: z):X}"; }
        }

        return null;
    }
    private static string? Triple<TInput, TResult>(UInt128 a, UInt128 b, UInt128 c) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var x = TInput.CreateTruncating(value: a);
        var y = TInput.CreateTruncating(value: b);
        var z = TInput.CreateTruncating(value: c);
        var expected = Oracles.Interleave(
            inputWidth: WidthOf<TInput>(),
            operands: [BitsOf(value: x), BitsOf(value: y), BitsOf(value: z)],
            resultWidth: WidthOf<TResult>()
        );
        var name = $"{typeof(TInput).Name}->{typeof(TResult).Name}";

        if (BitsOf(value: x.BitwiseTriple<TInput, TResult>(second: y, third: z)) != expected) { return $"BitwiseTriple {name} x={BitsOf(value: x):X} y={BitsOf(value: y):X} z={BitsOf(value: z):X}"; }
        if (BitsOf(value: MortonProbes<TInput, TResult>.TripleBySwar(arg1: x, arg2: y, arg3: z)) != expected) { return $"BitwiseTriple SWAR {name} x={BitsOf(value: x):X} y={BitsOf(value: y):X} z={BitsOf(value: z):X}"; }

        return null;
    }
    private static string? Untriple<TInput, TResult>(UInt128 value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var z = TInput.CreateTruncating(value: value);
        var name = $"{typeof(TInput).Name}->{typeof(TResult).Name}";

        var (first, middle, last) = z.BitwiseUntriple<TInput, TResult>();
        var (swarFirst, swarMiddle, swarLast) = MortonProbes<TInput, TResult>.UntripleBySwar(arg: z);
        TResult[] actual = [first, middle, last];
        TResult[] swar = [swarFirst, swarMiddle, swarLast];

        for (var lane = 0; (lane < 3); ++lane) {
            var expected = Oracles.Deinterleave(
                inputWidth: WidthOf<TInput>(),
                lane: lane,
                resultWidth: WidthOf<TResult>(),
                value: BitsOf(value: z),
                ways: 3
            );

            if (BitsOf(value: actual[lane]) != expected) { return $"BitwiseUntriple {name} lane={lane} z={BitsOf(value: z):X}"; }
            if (BitsOf(value: swar[lane]) != expected) { return $"BitwiseUntriple SWAR {name} lane={lane} z={BitsOf(value: z):X}"; }
        }

        return null;
    }
    private static string? BitKernelRefusals<T>() where T : IBinaryInteger<T> {
        var bits = WidthOf<T>();
        var name = typeof(T).Name;
        var two = (T.One + T.One);
        var three = (two + T.One);

        return (((MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => (-1).NthPowerOfTwo<T>(), parameter: "exponent") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => bits.NthPowerOfTwo<T>(), parameter: "exponent") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => (-1).NthFermatMask<T>(), parameter: "exponent") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => BitOperations.Log2(value: ((uint)bits)).NthFermatMask<T>(), parameter: "exponent") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => (-1).LowMask<T>(), parameter: "count") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => (bits + 1).LowMask<T>(), parameter: "count") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => T.One.AlignUp(alignment: T.Zero), parameter: "alignment") ??
            (MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => T.One.AlignDown(alignment: three), parameter: "alignment") ??
            // A signed carrier's top bit is its sign, so that one power of two is negative and refused.
            ((T.IsNegative(value: T.AllBitsSet)
                ? MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => T.One.AlignUp(alignment: (T.One << (bits - 1))), parameter: "alignment")
                : null) ??
            MagicConstantRefusal<ArgumentOutOfRangeException>(action: () => T.One.AlignDown(alignment: (T.AllBitsSet - T.One)), parameter: "alignment"))))))))))
            is { } failure)
            ? $"{name}: {failure}"
            : null);
    }

    // Families from the AMD and Intel CPUID documentation; the microcoded PDEP/PEXT of AMD families before 0x19 from
    // Agner Fog's instruction tables and uops.info measurements of Zen 1 and Zen 2.
    private static readonly (string Processor, string Vendor, int Family, bool HasBmi2, bool Expected)[] FastParallelBitsRows = [
        ("Excavator", "AuthenticAMD", 0x15, true, false),
        ("Zen 1", "AuthenticAMD", 0x17, true, false),
        ("Zen 2 (Steam Deck)", "AuthenticAMD", 0x17, true, false),
        ("AMD family 0x18", "AuthenticAMD", 0x18, true, false),
        ("Hygon Dhyana", "HygonGenuine", 0x18, true, false),
        ("Zen 3", "AuthenticAMD", 0x19, true, true),
        ("Zen 4", "AuthenticAMD", 0x19, true, true),
        ("Zen 5", "AuthenticAMD", 0x1A, true, true),
        ("Intel Haswell and later", "GenuineIntel", 0x6, true, true),
        ("Intel without BMI2", "GenuineIntel", 0x6, false, false),
        ("Zen 4 without BMI2 reported", "AuthenticAMD", 0x19, false, false),
    ];

    private static string CpuIdChars(int register) =>
        new(value: [((char)(register & 0xFF)), ((char)((register >> 8) & 0xFF)), ((char)((register >> 16) & 0xFF)), ((char)((register >>> 24) & 0xFF))]);

    private static class BitKernelProbes<T> where T : IBinaryInteger<T> {
        public static readonly Func<T, T, T> DepositInSoftware = PrivateKernel<Func<T, T, T>>(name: "ParallelBitDepositInSoftware", typeArguments: [typeof(T)]);
        public static readonly Func<T, T, T> ExtractInSoftware = PrivateKernel<Func<T, T, T>>(name: "ParallelBitExtractInSoftware", typeArguments: [typeof(T)]);
    }
    private static class MortonProbes<TInput, TResult> where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        public static readonly Func<TInput, TInput, TResult> PairBySwar = PrivateKernel<Func<TInput, TInput, TResult>>(name: "BitwisePairBySwar", typeArguments: [typeof(TInput), typeof(TResult)]);
        public static readonly Func<TInput, TInput, TInput, TResult> TripleBySwar = PrivateKernel<Func<TInput, TInput, TInput, TResult>>(name: "BitwiseTripleBySwar", typeArguments: [typeof(TInput), typeof(TResult)]);
        public static readonly Func<TInput, (TResult, TResult)> UnpairBySwar = PrivateKernel<Func<TInput, (TResult, TResult)>>(name: "BitwiseUnpairBySwar", typeArguments: [typeof(TInput), typeof(TResult)]);
        public static readonly Func<TInput, (TResult, TResult, TResult)> UntripleBySwar = PrivateKernel<Func<TInput, (TResult, TResult, TResult)>>(name: "BitwiseUntripleBySwar", typeArguments: [typeof(TInput), typeof(TResult)]);
    }

    // The portable kernels are private; resolving them directly holds them to the same oracle as the hardware path on
    // a host where BitManipulation.HasFastParallelBits holds.
    private static TDelegate PrivateKernel<TDelegate>(string name, Type[] typeArguments) where TDelegate : Delegate =>
        typeof(BinaryIntegerFunctions).GetMethod(
            bindingAttr: BindingFlags.Static | BindingFlags.NonPublic,
            name: name
        )!
            .MakeGenericMethod(typeArguments: typeArguments).CreateDelegate<TDelegate>();

    private interface IRawWidthClaim {
        static abstract string? At<T>(UInt128 raw, UInt128 selector) where T : IBinaryInteger<T>;
    }
    private readonly struct PowerOfTwoBitOracleWidths : IIntegerWidthClaim {
        public static string? At<T>() where T : IBinaryInteger<T> => PowerOfTwoBitOracle<T>();
    }
    private readonly struct LowMaskBitOracleWidths : IIntegerWidthClaim {
        public static string? At<T>() where T : IBinaryInteger<T> => LowMaskBitOracle<T>();
    }
    private readonly struct BitKernelRefusalWidths : IIntegerWidthClaim {
        public static string? At<T>() where T : IBinaryInteger<T> => BitKernelRefusals<T>();
    }
    private readonly struct SmearWidths : IRawWidthClaim {
        public static string? At<T>(UInt128 raw, UInt128 selector) where T : IBinaryInteger<T> => SmearBitOracle<T>(raw: raw);
    }
    private readonly struct AlignWidths : IRawWidthClaim {
        public static string? At<T>(UInt128 raw, UInt128 selector) where T : IBinaryInteger<T> => AlignBitOracle<T>(
            raw: raw,
            selector: ((ulong)selector)
        );
    }
    private readonly struct ParallelBitWidths : IRawWidthClaim {
        public static string? At<T>(UInt128 raw, UInt128 selector) where T : IBinaryInteger<T> => ParallelBitBitOracle<T>(
            raw: raw,
            rawMask: selector
        );
    }

    private static string? AtEveryIntegerWidth<TClaim>(UInt128 raw, UInt128 selector = default)
        where TClaim : IRawWidthClaim =>
        (TClaim.At<byte>(raw: raw, selector: selector) ?? (TClaim.At<sbyte>(raw: raw, selector: selector) ?? (TClaim.At<ushort>(raw: raw, selector: selector) ?? (TClaim.At<short>(raw: raw, selector: selector) ??
            (TClaim.At<uint>(raw: raw, selector: selector) ?? (TClaim.At<int>(raw: raw, selector: selector) ?? (TClaim.At<ulong>(raw: raw, selector: selector) ?? (TClaim.At<long>(raw: raw, selector: selector) ??
            (TClaim.At<UInt128>(raw: raw, selector: selector) ?? (TClaim.At<Int128>(raw: raw, selector: selector) ?? (TClaim.At<nuint>(raw: raw, selector: selector) ?? TClaim.At<nint>(raw: raw, selector: selector))))))))))));
}
