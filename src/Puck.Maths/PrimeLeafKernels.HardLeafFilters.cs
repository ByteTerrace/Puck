using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

public static partial class PrimeLeafKernels {
    // Checks the one-vector-of-lanes contract every survivor filter shares, in constant time.
    private static void RequireHardLeafRun(ReadOnlySpan<ushort> factors, int coordinate, int last, uint prime,
        ReadOnlySpan<uint> divisors, ReadOnlySpan<short> negations, int lanes) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: coordinate, other: factors.Length, paramName: nameof(coordinate));
        ArgumentOutOfRangeException.ThrowIfLessThan(value: last, other: -1, paramName: nameof(last));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: last, other: coordinate, paramName: nameof(last));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: prime, other: ((uint)CombinatorialFactorMask), paramName: nameof(prime));
        ArgumentOutOfRangeException.ThrowIfLessThan(value: divisors.Length, other: lanes, paramName: nameof(divisors));
        ArgumentOutOfRangeException.ThrowIfLessThan(value: negations.Length, other: divisors.Length, paramName: nameof(negations));
    }

    // The leaf m=CombinatorialNumber(c) of a surviving coordinate c is written with a negation mask that is zero when
    // the leaf adds its count and all ones when it subtracts it. The sign bit stores mu, not the factor, and after
    // masking signed compares are exact: factors <=32767 and the caller's p<=sqrt(y)<32767.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FilterGourdonHardLeaves(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        // Instruction-set dispatch folds when the caller is compiled. Every rung writes the same leaves in the same
        // order; the laws run each rung the host executes on identical inputs. A 512-bit host without VBMI2 packs
        // with the 256-bit rung.
        Debug.Assert(((last >= -1) && (last <= coordinate) && (coordinate < factors.Length) && (negations.Length >= divisors.Length)));
        if (Vector512.IsHardwareAccelerated && Avx512Vbmi2.IsSupported) {
            return FilterGourdonHardLeavesCompressCore(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
                negations: negations, prime: prime, subtracted: ref subtracted);
        }
        if (Vector256.IsHardwareAccelerated && Avx2.IsSupported) {
            return FilterGourdonHardLeavesPack256Core(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
                negations: negations, prime: prime, subtracted: ref subtracted);
        }
        if (Vector128.IsHardwareAccelerated && Ssse3.IsSupported) {
            return FilterGourdonHardLeavesPack128Core(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
                negations: negations, prime: prime, subtracted: ref subtracted);
        }
        return FilterGourdonHardLeavesScalarCore(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
            negations: negations, prime: prime, subtracted: ref subtracted);
    }

    /// <summary>Lists, in descending order, the hard leaves among a run of thirty-wheel coordinates, with AVX-512 VBMI2.</summary>
    /// <param name="factors">The least-factor table: bit 15 of an entry holds the sign of the Moebius function, bits zero
    /// through fourteen the least prime factor, and zero marks a square-full number.</param>
    /// <param name="coordinate">The highest thirty-wheel coordinate examined, below the length of
    /// <paramref name="factors"/>; on return, the next one unexamined.</param>
    /// <param name="last">The coordinate below the lowest one examined; from -1 through <paramref name="coordinate"/>.</param>
    /// <param name="prime">The prime a leaf's least factor must exceed; below 32768.</param>
    /// <param name="divisors">Receives the leaves; at least 32 long, the lanes of one vector.</param>
    /// <param name="negations">Receives each leaf's negation mask; at least as long as <paramref name="divisors"/>.</param>
    /// <param name="subtracted">Incremented by the number of leaves that subtract their count.</param>
    /// <returns>The number of leaves written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="coordinate"/> is not below the length of
    /// <paramref name="factors"/>, <paramref name="last"/> is below -1 or above <paramref name="coordinate"/>,
    /// <paramref name="prime"/> is 32768 or more, <paramref name="divisors"/> is shorter than one vector's lanes, or
    /// <paramref name="negations"/> is shorter than <paramref name="divisors"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">The host lacks AVX-512 VBMI2.</exception>
    /// <remarks>
    /// <para>For each coordinate <c>c</c> in <c>(last, coordinate]</c> whose least factor exceeds <paramref name="prime"/>,
    /// writes the leaf <c>m</c>, the integer at <c>c</c>, to <paramref name="divisors"/> and its negation mask to
    /// <paramref name="negations"/> at the same position: zero when the leaf adds its count, all ones when it subtracts
    /// it. A call stops early only when another full vector might not fit, and then returns at least
    /// <c>divisors.Length</c> less one vector's lanes. Positions from the returned count up to one vector past it may be
    /// overwritten.</para>
    /// <para>Every rung lists the same leaves in the same order, so the law
    /// <c>prime-selection.gourdon-hard-leaf-filter-rungs</c> compares the rungs the host supports on identical
    /// inputs.</para>
    /// </remarks>
    public static int FilterGourdonHardLeavesCompress(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        PrimeKernelGuard.RequireInstructionSet(instructionSet: "AVX-512 VBMI2", supported: Avx512Vbmi2.IsSupported);
        RequireHardLeafRun(coordinate: coordinate, divisors: divisors, factors: factors, lanes: 32, last: last, negations: negations, prime: prime);
        return FilterGourdonHardLeavesCompressCore(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
            negations: negations, prime: prime, subtracted: ref subtracted);
    }

    // The 512-bit rung on AVX-512 VBMI2, same contract. Reversing the 32 factors puts lane j at coordinate current-j, so
    // compressing their packed offsets and signs lists the survivors in descending coordinate order. Each step after
    // the masked head lowers current by 32, four whole wheel cycles, so the leaf at distance j is
    // number(current)-offsets[j] with one offset table from the head on: offsets[j]=number(c)-number(c-j), at most
    // 4*30-6, so an offset fits the low byte and the complemented sign bit rides above it. Each vector reserves room
    // for 32 leaves and stores the upper sixteen divisors only when more than sixteen survive. Masked factors and p
    // are below 32768, so the unsigned compare is exact and yields the compress mask directly.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe int FilterGourdonHardLeavesCompressCore(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        // The pinned address only chooses the head's length, so that the following loads align; any length writes
        // the same leaves.
        fixed (ushort* factorStart = factors) {
            const int Lanes = 32;
            var factorMask = Vector512.Create(value: CombinatorialFactorMask);
            var threshold = Vector512.Create(value: ((ushort)prime));
            var reverse = (Vector512.Create(value: ((ushort)(Lanes - 1))) - Vector512<ushort>.Indices);
            ref readonly var source = ref MemoryMarshal.GetReference(span: factors);
            ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
            ref var negation = ref MemoryMarshal.GetReference(span: negations);
            var limit = (divisors.Length - Lanes);
            var count = 0;
            var negatives = 0;
            var current = coordinate;
            var offsets = GourdonHardLeafOffsets512(coordinate: current);
            var top = Vector512.Create(value: CombinatorialNumber(coordinate: current));

            // One masked head puts the following load ends on vector boundaries. No steady-state factor load crosses
            // a 64-byte line: 64-byte vectors align to 64, and 32-byte vectors align to 32. Short ranges keep their tail.
            var head = ((int)((((nuint)((factorStart + current) + 1)) & ((Lanes * sizeof(ushort)) - 1)) / sizeof(ushort)));

            if ((head != 0) && (current >= (last + Lanes))) {
                var reversed = Avx512BW.PermuteVar32x16(control: reverse,
                    left: Vector512.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source));
                var remaining = Vector512.LessThan(left: Vector512<ushort>.Indices, right: Vector512.Create(value: ((ushort)head)));

                count = StoreGourdonHardLeaves512(count: count, divisor: ref divisor, negation: ref negation, negatives: ref negatives,
                    offsets: offsets, reversed: reversed, survivors: Vector512.GreaterThan(left: reversed & factorMask, right: threshold) & remaining, top: top);
                current -= head;
                offsets = GourdonHardLeafOffsets512(coordinate: current);
                top = Vector512.Create(value: CombinatorialNumber(coordinate: current));
            }
            while ((current >= (last + Lanes)) && (count <= limit)) {
                var reversed = Avx512BW.PermuteVar32x16(control: reverse,
                    left: Vector512.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source));

                count = StoreGourdonHardLeaves512(count: count, divisor: ref divisor, negation: ref negation, negatives: ref negatives,
                    offsets: offsets, reversed: reversed, survivors: Vector512.GreaterThan(left: reversed & factorMask, right: threshold), top: top);
                current -= Lanes;
                top -= Vector512.Create(value: ((((uint)Lanes) / PrimeWheel30.ChannelCount) * PrimeWheel30.Modulus));
            }
            // Fewer than 32 coordinates remain. With room for a vector and a load that starts inside factors, one vector
            // takes them all, its lanes at distances current-last and beyond masked off; otherwise the scalar tail does.
            if ((current > last) && (count <= limit) && (current >= (Lanes - 1))) {
                var reversed = Avx512BW.PermuteVar32x16(control: reverse,
                    left: Vector512.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source));
                var remaining = Vector512.LessThan(left: Vector512<ushort>.Indices, right: Vector512.Create(value: ((ushort)(current - last))));

                count = StoreGourdonHardLeaves512(count: count, divisor: ref divisor, negation: ref negation, negatives: ref negatives,
                    offsets: offsets, reversed: reversed, survivors: Vector512.GreaterThan(left: reversed & factorMask, right: threshold) & remaining, top: top);
                current = last;
            }
            subtracted += negatives;
            return FilterGourdonHardLeafTail(coordinate: ref coordinate, count: count, current: current, divisors: divisors,
                factors: factors, lanes: Lanes, last: last, negations: negations, prime: prime, subtracted: ref subtracted);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ushort> GourdonHardLeafOffsets512(int coordinate) {
        // With a=c&7 and t=a-j, number(c)-number(c-j)=NumericResidues[a]-NumericResidues[t&7]-30*floor(t/8).
        var phase = coordinate & (PrimeWheel30.ChannelCount - 1);
        var residues = Vector128.WidenLower(source: Vector128.CreateScalarUnsafe(
            value: Unsafe.ReadUnaligned<ulong>(source: ref MemoryMarshal.GetReference(span: PrimeWheel30.NumericResidues))).AsByte()).AsInt16().ToVector256Unsafe().ToVector512Unsafe();
        var distance = (Vector512.Create(value: ((short)phase)) - Vector512<short>.Indices);

        return ((Vector512.Create(value: ((short)PrimeWheel30.NumericResidues[phase]))
            - Avx512BW.PermuteVar32x16(control: distance & Vector512.Create(value: ((short)(PrimeWheel30.ChannelCount - 1))), left: residues))
            - ((distance >> 3) * ((short)PrimeWheel30.Modulus))).AsUInt16();
    }
    // Writes the survivors of one reversed vector of 32 factors from position count, lane j's leaf top-offsets[j], and
    // returns the advanced count; negatives gains the survivors without the Moebius sign bit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StoreGourdonHardLeaves512(Vector512<ushort> reversed, Vector512<ushort> survivors, Vector512<ushort> offsets,
        Vector512<uint> top, ref uint divisor, ref short negation, int count, ref int negatives) {
        var packed = Avx512Vbmi2.Compress(mask: survivors, merge: Vector512<ushort>.Zero,
            value: offsets | (~reversed & Vector512.Create(value: CombinatorialNegativeMoebius)));
        var selected = survivors.ExtractMostSignificantBits();
        var length = BitOperations.PopCount(value: selected);
        var distances = packed & Vector512.Create(value: ((ushort)byte.MaxValue));

        (top - Vector512.WidenLower(source: distances)).StoreUnsafe(destination: ref divisor, elementOffset: ((nuint)count));
        // Most vectors have at most sixteen survivors. Their upper divisor store would only write discarded lanes.
        if (length > Vector512<uint>.Count) {
            (top - Vector512.WidenUpper(source: distances)).StoreUnsafe(destination: ref divisor,
                elementOffset: ((nuint)(count + Vector512<uint>.Count)));
        }
        (packed.AsInt16() >> 15).StoreUnsafe(destination: ref negation, elementOffset: ((nuint)count));
        negatives += BitOperations.PopCount(value: selected & ~reversed.ExtractMostSignificantBits());
        return (count + length);
    }

    /// <summary>Lists, in descending order, the hard leaves among a run of thirty-wheel coordinates, with AVX2.</summary>
    /// <param name="factors">The least-factor table: bit 15 of an entry holds the sign of the Moebius function, bits zero
    /// through fourteen the least prime factor, and zero marks a square-full number.</param>
    /// <param name="coordinate">The highest thirty-wheel coordinate examined, below the length of
    /// <paramref name="factors"/>; on return, the next one unexamined.</param>
    /// <param name="last">The coordinate below the lowest one examined; from -1 through <paramref name="coordinate"/>.</param>
    /// <param name="prime">The prime a leaf's least factor must exceed; below 32768.</param>
    /// <param name="divisors">Receives the leaves; at least 16 long, the lanes of one vector.</param>
    /// <param name="negations">Receives each leaf's negation mask; at least as long as <paramref name="divisors"/>.</param>
    /// <param name="subtracted">Incremented by the number of leaves that subtract their count.</param>
    /// <returns>The number of leaves written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The conditions of <see cref="FilterGourdonHardLeavesCompress"/>, with
    /// <paramref name="divisors"/> required to hold 16 lanes.</exception>
    /// <exception cref="PlatformNotSupportedException">The host lacks AVX2.</exception>
    /// <remarks>The 256-bit rung of <see cref="FilterGourdonHardLeavesCompress"/>, with the same contract.</remarks>
    public static int FilterGourdonHardLeavesPack256(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        PrimeKernelGuard.RequireInstructionSet(instructionSet: "AVX2", supported: Avx2.IsSupported);
        RequireHardLeafRun(coordinate: coordinate, divisors: divisors, factors: factors, lanes: 16, last: last, negations: negations, prime: prime);
        return FilterGourdonHardLeavesPack256Core(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
            negations: negations, prime: prime, subtracted: ref subtracted);
    }

    // The 256-bit rung on AVX2, same contract, in the shape of the VBMI2 rung with a byte shuffle for the compress.
    // Lane i of a vector of 16 factors lies at distance 15-i below current. Each lane carries its leaf's offset below
    // number(current) in its low byte and the complement of its factor's high byte, whose top bit is then set exactly
    // when the leaf subtracts its count, above it; one byte shuffle per eight lanes, from GourdonHardLeafTables.Packs,
    // lists the survivors from the highest lane down, so in descending coordinate order. Each step lowers current by
    // 16, two whole wheel cycles, so the offsets depend only on current's phase and fit a byte; the masked head that
    // aligns the loads changes the phase, so the offsets are reloaded after it. Every half stores eight leaves, of
    // which the survivors are kept, and count<=limit leaves room for them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe int FilterGourdonHardLeavesPack256Core(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        // The pinned address only chooses the head's length, so that the following loads align; any length writes
        // the same leaves.
        fixed (ushort* factorStart = factors) {
            const int Lanes = 16;
            var threshold = Vector256.Create(value: ((short)prime));
            ref readonly var source = ref MemoryMarshal.GetReference(span: factors);
            ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
            ref var negation = ref MemoryMarshal.GetReference(span: negations);
            var limit = ((nuint)(divisors.Length - Lanes));
            var count = ((nuint)0);
            var negatives = 0;
            var current = coordinate;
            var offsets = Vector256.LoadUnsafe(source: ref GourdonHardLeafTables.Offsets(phase: current & (PrimeWheel30.ChannelCount - 1)));
            var top = Vector256.Create(value: CombinatorialNumber(coordinate: current));
            ref var packs = ref GourdonHardLeafTables.Packs;

            // One masked head puts the following load ends on vector boundaries. No steady-state factor load crosses
            // a 64-byte line: 64-byte vectors align to 64, and 32-byte vectors align to 32. Short ranges keep their tail.
            var head = ((int)((((nuint)((factorStart + current) + 1)) & ((Lanes * sizeof(ushort)) - 1)) / sizeof(ushort)));

            if ((head != 0) && (current >= (last + Lanes))) {
                var kept = (0xFFFFU << (Lanes - head)) & 0xFFFFU;

                count = StoreGourdonHardLeaves256(count: count, divisor: ref divisor, factors: Vector256.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source),
                    keep: (kept & 0xFFU) | ((kept & 0xFF00U) << 8), negation: ref negation, negatives: ref negatives, offsets: offsets, packs: ref packs, threshold: threshold, top: top);
                current -= head;
                offsets = Vector256.LoadUnsafe(source: ref GourdonHardLeafTables.Offsets(phase: current & (PrimeWheel30.ChannelCount - 1)));
                top = Vector256.Create(value: CombinatorialNumber(coordinate: current));
            }
            while ((current >= (last + Lanes)) && (count <= limit)) {
                count = StoreGourdonHardLeaves256(count: count, divisor: ref divisor, factors: Vector256.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source),
                    keep: uint.MaxValue, negation: ref negation, negatives: ref negatives, offsets: offsets, packs: ref packs, threshold: threshold, top: top);
                current -= Lanes;
                top -= Vector256.Create(value: ((((uint)Lanes) / PrimeWheel30.ChannelCount) * PrimeWheel30.Modulus));
            }
            // Fewer than 16 coordinates remain. With room for a vector and a load that starts inside factors, one vector
            // takes them all, its lanes at distances current-last and beyond kept out of the mask; otherwise the scalar tail does.
            if ((current > last) && (count <= limit) && (current >= (Lanes - 1))) {
                var kept = (0xFFFFU << (Lanes - (current - last))) & 0xFFFFU;

                count = StoreGourdonHardLeaves256(count: count, divisor: ref divisor, factors: Vector256.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source),
                    keep: (kept & 0xFFU) | ((kept & 0xFF00U) << 8), negation: ref negation, negatives: ref negatives, offsets: offsets, packs: ref packs, threshold: threshold, top: top);
                current = last;
            }
            subtracted += negatives;
            return FilterGourdonHardLeafTail(coordinate: ref coordinate, count: ((int)count), current: current, divisors: divisors,
                factors: factors, lanes: Lanes, last: last, negations: negations, prime: prime, subtracted: ref subtracted);
        }
    }

    // Writes the survivors among one vector of 16 factors, kept by keep, from position count, the high half first, and
    // returns the advanced count; negatives gains the survivors without the Moebius sign bit. Packing the survivor
    // compare with the factors' saturated bytes gives one mask: bits 0-7 and 16-23 are the halves' survivors, bits
    // 8-15 and 24-31 their sign bits, and keep selects among the survivor bits in the same layout.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint StoreGourdonHardLeaves256(Vector256<ushort> factors, Vector256<ushort> offsets, Vector256<short> threshold, uint keep,
        Vector256<uint> top, ref byte packs, ref uint divisor, ref short negation, nuint count, ref int negatives) {
        var survivors = Vector256.GreaterThan(left: (factors & Vector256.Create(value: CombinatorialFactorMask)).AsInt16(), right: threshold);
        var bits = ((uint)Avx2.MoveMask(value: Avx2.PackSignedSaturate(left: survivors, right: factors.AsInt16())));
        var selected = (bits & 0x00FF_00FFU) & keep;
        var packed = (Vector256.AndNot(left: Vector256.Create(value: ((ushort)0xFF00)), right: factors) | offsets).AsByte();

        negatives += BitOperations.PopCount(value: selected & ~(bits >> 8));
        count = StoreGourdonHardLeafHalf256(count: count, divisor: ref divisor, negation: ref negation, packed: packed.GetUpper(),
            packs: ref packs, selected: (selected >> 16), top: top);
        return StoreGourdonHardLeafHalf256(count: count, divisor: ref divisor, negation: ref negation, packed: packed.GetLower(),
            packs: ref packs, selected: selected & 0xFFU, top: top);
    }
    // Writes the selected lanes of one packed half, eight lanes from 8-bit mask selected, from position count: each
    // leaf's number top-offset and its negation mask, the sign of its lane.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint StoreGourdonHardLeafHalf256(Vector128<byte> packed, uint selected, Vector256<uint> top, ref byte packs,
        ref uint divisor, ref short negation, nuint count) {
        var leaves = PackGourdonHardLeaves(packed: packed, packs: ref packs, selected: selected);

        Vector128.ShiftRightArithmetic(shiftCount: 15, vector: leaves).StoreUnsafe(destination: ref negation, elementOffset: count);
        (top - Vector256.WidenLower(source: (leaves.AsUInt16() & Vector128.Create(value: ((ushort)byte.MaxValue))).ToVector256Unsafe()))
            .StoreUnsafe(destination: ref divisor, elementOffset: count);
        return (count + ((nuint)BitOperations.PopCount(value: selected)));
    }

    /// <summary>Lists, in descending order, the hard leaves among a run of thirty-wheel coordinates, with SSSE3.</summary>
    /// <param name="factors">The least-factor table: bit 15 of an entry holds the sign of the Moebius function, bits zero
    /// through fourteen the least prime factor, and zero marks a square-full number.</param>
    /// <param name="coordinate">The highest thirty-wheel coordinate examined, below the length of
    /// <paramref name="factors"/>; on return, the next one unexamined.</param>
    /// <param name="last">The coordinate below the lowest one examined; from -1 through <paramref name="coordinate"/>.</param>
    /// <param name="prime">The prime a leaf's least factor must exceed; below 32768.</param>
    /// <param name="divisors">Receives the leaves; at least 8 long, the lanes of one vector.</param>
    /// <param name="negations">Receives each leaf's negation mask; at least as long as <paramref name="divisors"/>.</param>
    /// <param name="subtracted">Incremented by the number of leaves that subtract their count.</param>
    /// <returns>The number of leaves written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The conditions of <see cref="FilterGourdonHardLeavesCompress"/>, with
    /// <paramref name="divisors"/> required to hold 8 lanes.</exception>
    /// <exception cref="PlatformNotSupportedException">The host lacks SSSE3.</exception>
    /// <remarks>The 128-bit rung of <see cref="FilterGourdonHardLeavesCompress"/>, with the same contract.</remarks>
    public static int FilterGourdonHardLeavesPack128(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        PrimeKernelGuard.RequireInstructionSet(instructionSet: "SSSE3", supported: Ssse3.IsSupported);
        RequireHardLeafRun(coordinate: coordinate, divisors: divisors, factors: factors, lanes: 8, last: last, negations: negations, prime: prime);
        return FilterGourdonHardLeavesPack128Core(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
            negations: negations, prime: prime, subtracted: ref subtracted);
    }

    // The 128-bit rung on SSSE3, the 256-bit rung's method on one half: lane i of a vector of eight factors lies at
    // distance 7-i below current, and each step lowers current by eight, one wheel cycle.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FilterGourdonHardLeavesPack128Core(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        const int Lanes = 8;
        var threshold = Vector128.Create(value: ((short)prime));
        ref readonly var source = ref MemoryMarshal.GetReference(span: factors);
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var negation = ref MemoryMarshal.GetReference(span: negations);
        var limit = ((nuint)(divisors.Length - Lanes));
        var count = ((nuint)0);
        var negatives = 0;
        var current = coordinate;
        // The upper half of the 256-bit rung's offsets holds distances seven down to zero.
        var offsets = Vector128.LoadUnsafe(source: ref GourdonHardLeafTables.Offsets(phase: current & (PrimeWheel30.ChannelCount - 1)), elementOffset: Lanes);
        var top = Vector128.Create(value: CombinatorialNumber(coordinate: current));
        ref var packs = ref GourdonHardLeafTables.Packs;

        while ((current >= (last + Lanes)) && (count <= limit)) {
            count = StoreGourdonHardLeaves128(count: count, divisor: ref divisor, factors: Vector128.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source),
                keep: uint.MaxValue, negation: ref negation, negatives: ref negatives, offsets: offsets, packs: ref packs, threshold: threshold, top: top);
            current -= Lanes;
            top -= Vector128.Create(value: ((uint)PrimeWheel30.Modulus));
        }
        if ((current > last) && (count <= limit) && (current >= (Lanes - 1))) {
            count = StoreGourdonHardLeaves128(count: count, divisor: ref divisor, factors: Vector128.LoadUnsafe(elementOffset: ((nuint)((current - Lanes) + 1)), source: in source),
                keep: (0xFFU << (Lanes - (current - last))), negation: ref negation, negatives: ref negatives, offsets: offsets, packs: ref packs, threshold: threshold, top: top);
            current = last;
        }
        subtracted += negatives;
        return FilterGourdonHardLeafTail(coordinate: ref coordinate, count: ((int)count), current: current, divisors: divisors,
            factors: factors, lanes: Lanes, last: last, negations: negations, prime: prime, subtracted: ref subtracted);
    }

    // StoreGourdonHardLeaves256 for one vector of eight factors: bits 0-7 of the packed mask are the survivors and bits
    // 8-15 their sign bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint StoreGourdonHardLeaves128(Vector128<ushort> factors, Vector128<ushort> offsets, Vector128<short> threshold, uint keep,
        Vector128<uint> top, ref byte packs, ref uint divisor, ref short negation, nuint count, ref int negatives) {
        var survivors = Vector128.GreaterThan(left: (factors & Vector128.Create(value: CombinatorialFactorMask)).AsInt16(), right: threshold);
        var bits = ((uint)Sse2.MoveMask(value: Sse2.PackSignedSaturate(left: survivors, right: factors.AsInt16())));
        var selected = (bits & 0xFFU) & keep;
        var leaves = PackGourdonHardLeaves(packed: (Vector128.AndNot(left: Vector128.Create(value: ((ushort)0xFF00)), right: factors) | offsets).AsByte(),
            packs: ref packs, selected: selected);

        var (lower, upper) = Vector128.Widen(source: leaves.AsUInt16() & Vector128.Create(value: ((ushort)byte.MaxValue)));

        negatives += BitOperations.PopCount(value: selected & ~(bits >> 8));
        Vector128.ShiftRightArithmetic(shiftCount: 15, vector: leaves).StoreUnsafe(destination: ref negation, elementOffset: count);
        (top - lower).StoreUnsafe(destination: ref divisor, elementOffset: count);
        (top - upper).StoreUnsafe(destination: ref divisor, elementOffset: (count + ((nuint)Vector128<uint>.Count)));
        return (count + ((nuint)BitOperations.PopCount(value: selected)));
    }
    // Moves the sixteen-bit lanes of packed that the 8-bit mask selected selects to the front, the highest lane first,
    // with the control GourdonHardLeafTables.Packs holds for selected.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> PackGourdonHardLeaves(Vector128<byte> packed, ref byte packs, uint selected) =>
        Ssse3.Shuffle(mask: Vector128.LoadUnsafe(elementOffset: ((nuint)(selected * GourdonHardLeafTables.PackBytes)), source: ref packs), value: packed).AsInt16();

    // The pack rungs' tables, each 64-byte aligned so no entry spans two cache lines.
    private static class GourdonHardLeafTables {
        internal const int PackBytes = 16;

        private const int OffsetLanes = 16;

        // For each 8-bit lane mask, the byte-shuffle control that lists the set lanes' sixteen-bit elements from the
        // highest lane down, then zeros.
        private static readonly CacheAlignedArray<byte> PackTable = CreatePacks();
        // For each wheel phase a of a coordinate c, offsets[i]=number(c)-number(c-(15-i)) for the sixteen lanes i, each
        // below 60, so the leaf of lane i of a vector ending at c is number(c)-offsets[i].
        private static readonly CacheAlignedArray<ushort> OffsetTable = CreateOffsets();

        internal static ref byte Packs => ref PackTable.Reference;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref ushort Offsets(int phase) => ref Unsafe.Add(elementOffset: ((nuint)(phase * OffsetLanes)), source: ref OffsetTable.Reference);

        private static CacheAlignedArray<byte> CreatePacks() {
            var packs = new CacheAlignedArray<byte>(length: (256 * PackBytes));
            var span = packs.Span;

            span.Fill(value: 0x80);
            for (var mask = 0; (mask < 256); ++mask) {
                var position = (mask * PackBytes);

                for (var lane = 7; (lane >= 0); --lane) {
                    if ((mask & (1 << lane)) == 0) { continue; }
                    span[position++] = ((byte)(2 * lane));
                    span[position++] = ((byte)((2 * lane) + 1));
                }
            }
            return packs;
        }
        private static CacheAlignedArray<ushort> CreateOffsets() {
            var offsets = new CacheAlignedArray<ushort>(length: (PrimeWheel30.ChannelCount * OffsetLanes));
            var span = offsets.Span;

            for (var phase = 0; (phase < PrimeWheel30.ChannelCount); ++phase) {
                // Any coordinate of this phase at least 15 gives the same differences.
                var coordinate = ((2 * PrimeWheel30.ChannelCount) + phase);

                for (var lane = 0; (lane < OffsetLanes); ++lane) {
                    span[((phase * OffsetLanes) + lane)] = ((ushort)(CombinatorialNumber(coordinate: coordinate)
                        - CombinatorialNumber(coordinate: (coordinate - ((OffsetLanes - 1) - lane)))));
                }
            }
            return offsets;
        }
    }

    /// <summary>Lists, in descending order, the hard leaves among a run of thirty-wheel coordinates, on every host.</summary>
    /// <param name="factors">The least-factor table: bit 15 of an entry holds the sign of the Moebius function, bits zero
    /// through fourteen the least prime factor, and zero marks a square-full number.</param>
    /// <param name="coordinate">The highest thirty-wheel coordinate examined, below the length of
    /// <paramref name="factors"/>; on return, the next one unexamined.</param>
    /// <param name="last">The coordinate below the lowest one examined; from -1 through <paramref name="coordinate"/>.</param>
    /// <param name="prime">The prime a leaf's least factor must exceed; below 32768.</param>
    /// <param name="divisors">Receives the leaves; at least 4 long, the leaves of one step.</param>
    /// <param name="negations">Receives each leaf's negation mask; at least as long as <paramref name="divisors"/>.</param>
    /// <param name="subtracted">Incremented by the number of leaves that subtract their count.</param>
    /// <returns>The number of leaves written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The conditions of <see cref="FilterGourdonHardLeavesCompress"/>, with
    /// <paramref name="divisors"/> required to hold 4 leaves.</exception>
    /// <remarks>The scalar rung of <see cref="FilterGourdonHardLeavesCompress"/>, with the same contract; it needs no
    /// instruction set.</remarks>
    public static int FilterGourdonHardLeavesScalar(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        RequireHardLeafRun(coordinate: coordinate, divisors: divisors, factors: factors, lanes: 4, last: last, negations: negations, prime: prime);
        return FilterGourdonHardLeavesScalarCore(coordinate: ref coordinate, divisors: divisors, factors: factors, last: last,
            negations: negations, prime: prime, subtracted: ref subtracted);
    }

    // The scalar rung of FilterGourdonHardLeaves: four branch-free stores per step, same contract.
    internal static int FilterGourdonHardLeavesScalarCore(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted) {
        const int Lanes = 4;
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var negation = ref MemoryMarshal.GetReference(span: negations);
        var limit = (divisors.Length - Lanes);
        var count = 0;
        var current = coordinate;

        while ((current >= (last + Lanes)) && (count <= limit)) {
            for (var leaf = current; (leaf > (current - Lanes)); --leaf) {
                var factor = factors[leaf];

                count = StoreGourdonHardLeaf(count: count, divisor: ref divisor, factor: factor, leaf: leaf, negation: ref negation,
                    subtracted: ref subtracted, survives: (((factor & CombinatorialFactorMask) > prime) ? 1 : 0));
            }
            current -= Lanes;
        }
        return FilterGourdonHardLeafTail(coordinate: ref coordinate, count: count, current: current, divisors: divisors,
            factors: factors, lanes: Lanes, last: last, negations: negations, prime: prime, subtracted: ref subtracted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FilterGourdonHardLeafTail(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<uint> divisors, Span<short> negations, ref int subtracted, int count, int current, int lanes) {
        // A batch with room for one more vector stopped because fewer than one vector of
        // coordinates remains; all of them fit. A fuller batch leaves them for the next call.
        // Fewer than lanes coordinates remain, so the writes stay below count+lanes<=divisors.Length.
        if (count <= (divisors.Length - lanes)) {
            ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
            ref var negation = ref MemoryMarshal.GetReference(span: negations);

            Debug.Assert(((current - last) < lanes));
            for (; (current > last); --current) {
                var factor = factors[current];

                count = StoreGourdonHardLeaf(count: count, divisor: ref divisor, factor: factor, leaf: current, negation: ref negation,
                    subtracted: ref subtracted, survives: (((factor & CombinatorialFactorMask) > prime) ? 1 : 0));
            }
        }
        coordinate = current;
        return count;
    }
    // Writes leaf coordinate's number and negation mask at position count and returns count advanced by survives, zero
    // or one, without a branch: a leaf that does not survive is overwritten by the next one.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StoreGourdonHardLeaf(ref uint divisor, ref short negation, int count, int leaf, ushort factor, int survives,
        ref int subtracted) {
        var negate = (((factor & CombinatorialNegativeMoebius) >> 15) - 1);

        Unsafe.Add(elementOffset: count, source: ref divisor) = CombinatorialNumber(coordinate: leaf);
        Unsafe.Add(elementOffset: count, source: ref negation) = ((short)negate);
        subtracted += (-negate) & survives;
        return (count + survives);
    }
}
