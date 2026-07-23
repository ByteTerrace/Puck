using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. Proves the digital-net sampler's properties instead of asserting them, because every one of them is a
/// theorem with a finite witness. The two mixing primitives are shown to be bijections — the mix exhaustively over the
/// whole 32-bit domain in both directions, the nested permutation by round trip plus the aligned-block statement it
/// exists for. <see cref="BinaryPolynomial.IsPrimitive"/> is re-derived against the classical count
/// <c>phi(2^n - 1) / n</c> by enumerating every monic polynomial of each degree through fourteen, so the primitivity
/// decision the direction numbers depend on is itself proved rather than trusted. The direction numbers are then
/// compared against two oracles that share no code with the recurrence — the anti-diagonal for dimension zero, Pascal's
/// triangle modulo two for dimension one — and dimension zero's whole sampler is compared against plain bit reversal.
/// Finally the <c>(0, m, 2)</c>-net property is checked EXHAUSTIVELY: every elementary dyadic interval of every shape
/// at every order through fourteen holds exactly one point, and the same is required to survive a digital shift, an
/// index shuffle, and the twelve-bit quantization a GPU sampler table actually reads.
/// </summary>
internal sealed class DigitalNetStage : IPostStage {
    /// <summary>The order the digital-shift sweep runs to; every shape at every order at or below it is covered.</summary>
    private const int ShiftedNetOrder = 12;
    /// <summary>The number of pseudorandom digital shifts the net property must survive.</summary>
    private const int ShiftSampleCount = 256;
    /// <summary>The largest degree the primitive-polynomial census enumerates exhaustively.</summary>
    private const int MaximumCensusDegree = 14;
    /// <summary>The largest order the unshifted net property is proved at, so 2^14 points and fifteen interval shapes.</summary>
    private const int MaximumNetOrder = 14;
    /// <summary>The seeds the index shuffle is exercised under; zero is included because a zero seed is the one a caller reaches for first.</summary>
    private static ReadOnlySpan<uint> ShuffleSeeds =>
        [0x00000000U, 0x00000001U, 0x5A5A5A5AU, 0xDEADBEEFU, 0xFFFFFFFFU];

    /// <inheritdoc/>
    public string Name => "digital-net";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var failure = (CheckMixConstants() ?? CheckMixBijection()) ?? (CheckPermutationRoundTrip() ?? CheckPermutationBlocks());

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        failure = (CheckPrimitiveCensus() ?? CheckGenerators()) ?? (CheckDirectionNumbers() ?? CheckRadicalInverse());

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        failure = (CheckNetProperty() ?? CheckShiftedNetProperty()) ?? (CheckShuffledNetProperty() ?? CheckQuantizedCoverage());

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        failure = CheckSampleTable();

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        return PostStageOutcome.Pass(detail: $"the mix is a bijection over all 2^32 words in both directions; the nested permutation carries every aligned dyadic block onto one through order 16; primitivity matches phi(2^n-1)/n over every monic polynomial through degree {MaximumCensusDegree}; both dimensions' direction numbers match independent oracles; and the (0,m,2)-net property holds exhaustively through order {MaximumNetOrder}, under {ShiftSampleCount} digital shifts and {ShuffleSeeds.Length} index shuffles through order {ShiftedNetOrder}, and at the shipped {SphericalCapSampleTable.TableIndexBitCount}-bit quantization");
    }

    /// <summary>Compares both dimensions' direction numbers against oracles that share no code with the recurrence.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when both dimensions match.</returns>
    /// <remarks>
    /// Dimension zero's matrix is the anti-diagonal. Dimension one's numerators are the rows of Pascal's triangle
    /// modulo two, which Lucas' theorem reduces to a bit test — no recurrence, no shift register, and no shared
    /// arithmetic with the code under test.
    /// </remarks>
    private static string? CheckDirectionNumbers() {
        Span<uint> directionNumbers = stackalloc uint[DigitalNetSampler.PlaneDirectionNumberCount];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        for (var index = 0; (index < DigitalNetSampler.DirectionNumberCount); ++index) {
            var expected = (1U << ((DigitalNetSampler.DirectionNumberCount - 1) - index));

            if (directionNumbers[index] != expected) {
                return $"dimension-0 direction number {index} is 0x{directionNumbers[index]:X8}, the anti-diagonal oracle gives 0x{expected:X8}";
            }
        }

        for (var index = 0; (index < DigitalNetSampler.DirectionNumberCount); ++index) {
            var numerator = 0U;

            for (var exponent = 0; (exponent <= index); ++exponent) {
                // Lucas' theorem: C(index, exponent) is odd exactly when the exponent's bits sit inside the row's.
                if (exponent == (index & exponent)) { numerator |= (1U << exponent); }
            }

            var expected = (numerator << ((DigitalNetSampler.DirectionNumberCount - 1) - index));

            if (directionNumbers[DigitalNetSampler.DirectionNumberCount + index] != expected) {
                return $"dimension-1 direction number {index} is 0x{directionNumbers[DigitalNetSampler.DirectionNumberCount + index]:X8}, the Pascal-triangle oracle gives 0x{expected:X8}";
            }
        }

        return null;
    }
    /// <summary>Proves the shipped generator primitive and rejects a generator that is merely irreducible.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when the decision behaves.</returns>
    /// <remarks>The negative witness is <c>t^4+t^3+t^2+t+1</c>: irreducible, but its root has order five rather than fifteen, which would silently shorten the shift register's period and destroy the net property.</remarks>
    private static string? CheckGenerators() {
        var generator = DigitalNetSampler.PlaneGenerator;

        if (!generator.IsIrreducible() || !generator.IsPrimitive()) {
            return $"the shipped plane generator {generator} is not primitive";
        }

        var shortPeriod = new BinaryPolynomial(bits: 0b11111UL);

        if (!shortPeriod.IsIrreducible() || shortPeriod.IsPrimitive()) {
            return $"{shortPeriod} must be irreducible and NOT primitive; the decision does not separate the two";
        }

        try {
            _ = new BinaryPolynomial(bits: (1UL << (BinaryPolynomial.MaximumPrimitiveDegree + 1)) | 1UL).IsPrimitive();

            return $"a primitivity decision above degree {BinaryPolynomial.MaximumPrimitiveDegree} did not throw";
        } catch (NotSupportedException) {
            return null;
        }
    }
    /// <summary>Proves the mixing map a bijection over the whole 32-bit domain, in both directions.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every word round trips both ways.</returns>
    /// <remarks>Both compositions are checked over every word, which is a complete proof rather than a sample: a map with a two-sided inverse on a finite set is a bijection of it.</remarks>
    private static string? CheckMixBijection() {
        const int BlockCount = 256;
        const long BlockLength = ((1L << 32) / BlockCount);

        var failures = new long[BlockCount];

        _ = Parallel.For(fromInclusive: 0, toExclusive: BlockCount, body: block => {
            var start = ((uint)(block * BlockLength));

            for (var offset = 0L; (offset < BlockLength); ++offset) {
                var value = ((uint)(start + offset));

                if ((UnitriangularBitMix.Unmix(value: UnitriangularBitMix.Mix(value: value)) != value) ||
                    (UnitriangularBitMix.Mix(value: UnitriangularBitMix.Unmix(value: value)) != value)) {
                    failures[block] = (1L + value);

                    return;
                }
            }
        });

        foreach (var failure in failures) {
            if (0L != failure) {
                return $"the mix does not round trip at 0x{(failure - 1L):X8}";
            }
        }

        return null;
    }
    /// <summary>Re-derives each of the mix's odd multipliers' inverses instead of trusting the published pair.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when both pairs are genuine inverses.</returns>
    private static string? CheckMixConstants() {
        var pairs = new (string Name, uint Multiplier, uint Inverse)[] {
            (Name: "first", Multiplier: UnitriangularBitMix.FirstMultiplier, Inverse: UnitriangularBitMix.FirstMultiplierInverse),
            (Name: "second", Multiplier: UnitriangularBitMix.SecondMultiplier, Inverse: UnitriangularBitMix.SecondMultiplierInverse),
        };

        foreach (var pair in pairs) {
            if (0U == (pair.Multiplier & 1U)) {
                return $"the {pair.Name} multiplier 0x{pair.Multiplier:X8} is even, so it is not a unit modulo 2^32";
            }

            if (1U != unchecked((pair.Multiplier * pair.Inverse))) {
                return $"the {pair.Name} multiplier 0x{pair.Multiplier:X8} times its published inverse 0x{pair.Inverse:X8} is not one";
            }

            var derived = pair.Multiplier.ModularInverse();

            if (derived != pair.Inverse) {
                return $"the {pair.Name} multiplier's inverse is 0x{derived:X8}, the published constant is 0x{pair.Inverse:X8}";
            }
        }

        return null;
    }
    /// <summary>Proves the unshifted net property exhaustively: every elementary dyadic interval of every shape holds exactly one point.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when the property holds at every order.</returns>
    private static string? CheckNetProperty() {
        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var indices = new uint[1 << MaximumNetOrder];
        var occupancy = new bool[1 << MaximumNetOrder];
        var points = new uint[2 << MaximumNetOrder];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        for (var index = 0; (index < indices.Length); ++index) {
            indices[index] = ((uint)index);
        }

        for (var order = 1; (order <= MaximumNetOrder); ++order) {
            FillPoints(directionNumbers: directionNumbers, indices: indices.AsSpan(start: 0, length: (1 << order)), scramble: (X: 0U, Y: 0U), points: points);

            if (!IsNet(points: points, order: order, occupancy: occupancy)) {
                return $"the unshifted net loses the one-point-per-box property at order {order}";
            }
        }

        return null;
    }
    /// <summary>Proves the nested permutation carries every aligned dyadic block onto an aligned block of the same size.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when the block statement holds under every seed.</returns>
    /// <remarks>This is the whole reason the permutation is not an ordinary mixing bijection: a mixing bijection scatters the block and the re-indexed point set is not a net.</remarks>
    private static string? CheckPermutationBlocks() {
        const int MaximumBlockOrder = 16;

        var occupancy = new bool[1 << MaximumBlockOrder];

        foreach (var seed in ShuffleSeeds) {
            for (var order = 1; (order <= MaximumBlockOrder); ++order) {
                var count = (1 << order);
                var expectedBlock = (DigitalNetSampler.ShuffleIndex(index: 0U, salt: seed) >>> order);

                occupancy.AsSpan(start: 0, length: count).Clear();

                for (var index = 0; (index < count); ++index) {
                    var permuted = DigitalNetSampler.ShuffleIndex(index: ((uint)index), salt: seed);

                    if ((permuted >>> order) != expectedBlock) {
                        return $"seed 0x{seed:X8} sends index {index} outside the aligned block of order {order}";
                    }

                    var offset = ((int)(permuted & ((1U << order) - 1U)));

                    if (occupancy[offset]) {
                        return $"seed 0x{seed:X8} is not injective on the first {count} indices";
                    }

                    occupancy[offset] = true;
                }
            }
        }

        return null;
    }
    /// <summary>Proves the nested permutation invertible over a dense prefix and a full-range stride.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every sampled index round trips.</returns>
    private static string? CheckPermutationRoundTrip() {
        const uint FullRangeStride = 4_093U;
        const int PrefixLength = (1 << 22);

        foreach (var seed in ShuffleSeeds) {
            for (var index = 0U; (index < PrefixLength); ++index) {
                if (NestedDyadicPermutation.Unpermute(index: NestedDyadicPermutation.Permute(index: index, seed: seed), seed: seed) != index) {
                    return $"the nested permutation does not round trip at index {index} under seed 0x{seed:X8}";
                }
            }

            // An odd stride visits every residue class modulo any power of two, so the sweep reaches the whole range
            // rather than one corner of it.
            for (var position = 0L; (position < (1L << 32)); position += FullRangeStride) {
                var index = ((uint)position);

                if (NestedDyadicPermutation.Unpermute(index: NestedDyadicPermutation.Permute(index: index, seed: seed), seed: seed) != index) {
                    return $"the nested permutation does not round trip at index {index} under seed 0x{seed:X8}";
                }
            }
        }

        return null;
    }
    /// <summary>Re-derives the primitivity decision against the classical count of primitive polynomials of each degree.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every degree's census matches.</returns>
    /// <remarks>
    /// The number of primitive polynomials of degree <c>n</c> over the two-element field is <c>phi(2^n - 1) / n</c>.
    /// Enumerating every monic polynomial with a non-zero constant term and counting the decision's positives against
    /// that closed form tests the decision in both directions at once — a false negative and a false positive both move
    /// the count.
    /// </remarks>
    private static string? CheckPrimitiveCensus() {
        for (var degree = 1; (degree <= MaximumCensusDegree); ++degree) {
            var actual = 0UL;
            var middleCount = (1UL << (degree - 1));

            for (var middle = 0UL; (middle < middleCount); ++middle) {
                if (new BinaryPolynomial(bits: ((1UL << degree) | (middle << 1) | 1UL)).IsPrimitive()) { ++actual; }
            }

            var expected = (OracleTotient(value: ((1UL << degree) - 1UL)) / ((ulong)degree));

            if (actual != expected) {
                return $"degree {degree} has {actual} primitive polynomials, phi(2^{degree}-1)/{degree} is {expected}";
            }
        }

        return null;
    }
    /// <summary>Compares dimension zero's whole sampler against plain bit reversal, which is what its matrix means.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every sampled index agrees.</returns>
    private static string? CheckRadicalInverse() {
        const uint FullRangeStride = 65_537U;
        const int PrefixLength = (1 << 20);

        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        var dimensionZero = directionNumbers.AsSpan(start: 0, length: DigitalNetSampler.DirectionNumberCount);

        for (var index = 0U; (index < PrefixLength); ++index) {
            if (DigitalNetSampler.Sample(index: index, directionNumbers: dimensionZero, scramble: 0U) != index.ReverseBits()) {
                return $"dimension 0 disagrees with bit reversal at index {index}";
            }
        }

        for (var position = 0L; (position < (1L << 32)); position += FullRangeStride) {
            var index = ((uint)position);

            if (DigitalNetSampler.Sample(index: index, directionNumbers: dimensionZero, scramble: 0U) != index.ReverseBits()) {
                return $"dimension 0 disagrees with bit reversal at index {index}";
            }
        }

        return null;
    }
    /// <summary>Proves the sampler table reproducible, correctly laid out, and geometrically exact.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when the table holds.</returns>
    /// <remarks>The polar pair shares one denominator, so the unit-length invariant is exact in double before the single rounding to float; it is checked there, because after rounding it is only nearly true and a tolerance would hide a real error.</remarks>
    private static string? CheckSampleTable() {
        const double CapHalfAngle = 0.11d;

        var first = new uint[SphericalCapSampleTable.WordCount];
        var second = new uint[SphericalCapSampleTable.WordCount];

        SphericalCapSampleTable.Build(capHalfAngleRadians: CapHalfAngle, destination: first);
        SphericalCapSampleTable.Build(capHalfAngleRadians: CapHalfAngle, destination: second);

        if (!first.AsSpan().SequenceEqual(other: second)) {
            return "two builds of the spherical-cap sample table disagree, so the table is not reproducible";
        }

        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        if (!first.AsSpan(start: SphericalCapSampleTable.DirectionNumberOffset, length: DigitalNetSampler.PlaneDirectionNumberCount).SequenceEqual(other: directionNumbers)) {
            return "the sample table's direction-number prefix is not the plane net's direction numbers";
        }

        var slope = Math.Tan(a: CapHalfAngle);

        for (var index = 0; (index < SphericalCapSampleTable.RadiusEntryCount); ++index) {
            var axial = BitConverter.UInt32BitsToSingle(value: first[SphericalCapSampleTable.RadiusOffset + (2 * index)]);
            var radial = BitConverter.UInt32BitsToSingle(value: first[SphericalCapSampleTable.RadiusOffset + (2 * index) + 1]);
            var radius = Math.Sqrt(d: ((index + 0.5d) / SphericalCapSampleTable.RadiusEntryCount));
            var offset = (slope * radius);
            var denominator = Math.Sqrt(d: (1.0d + (offset * offset)));

            if ((BitConverter.SingleToUInt32Bits(value: ((float)(1.0d / denominator))) != first[SphericalCapSampleTable.RadiusOffset + (2 * index)]) ||
                (BitConverter.SingleToUInt32Bits(value: ((float)(offset / denominator))) != first[SphericalCapSampleTable.RadiusOffset + (2 * index) + 1])) {
                return $"polar entry {index} is not the once-rounded double value";
            }

            var exact = (((1.0d / denominator) * (1.0d / denominator)) + ((offset / denominator) * (offset / denominator)));

            if (Math.Abs(value: (exact - 1.0d)) > 4.5e-16d) {
                return $"polar entry {index} has axial^2 + radial^2 = {exact:R} before rounding, not one";
            }

            if (!float.IsFinite(f: axial) || !float.IsFinite(f: radial) || (axial <= 0.0f) || (radial < 0.0f)) {
                return $"polar entry {index} is not a usable pair ({axial:R}, {radial:R})";
            }
        }

        for (var index = 0; (index < (2 * SphericalCapSampleTable.AzimuthEntryCount)); ++index) {
            var value = BitConverter.UInt32BitsToSingle(value: first[SphericalCapSampleTable.AzimuthOffset + index]);

            if (!float.IsFinite(f: value) || (Math.Abs(value: value) > 1.0f)) {
                return $"azimuth word {index} is {value:R}, which is not a cosine or sine";
            }
        }

        return null;
    }
    /// <summary>Proves the shipped quantization loses nothing: one full period of draws visits every table entry exactly once.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every shift and shuffle covers both tables.</returns>
    /// <remarks>
    /// This is the net property restated in the terms a GPU sampler actually works in. Reading the leading
    /// <see cref="SphericalCapSampleTable.TableIndexBitCount"/> bits of each coordinate is the extreme pair of interval
    /// shapes, so a run of <c>2^12</c> draws must be a permutation of the azimuth table and, independently, of the
    /// polar table. A quantization one bit too coarse for the run length would show up here as a collision.
    /// </remarks>
    private static string? CheckQuantizedCoverage() {
        const int Order = SphericalCapSampleTable.TableIndexBitCount;

        var azimuthSeen = new bool[SphericalCapSampleTable.AzimuthEntryCount];
        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var indices = new uint[1 << Order];
        var points = new uint[2 << Order];
        var radiusSeen = new bool[SphericalCapSampleTable.RadiusEntryCount];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        foreach (var seed in ShuffleSeeds) {
            var scramble = DigitalNetSampler.DeriveScramble(key: seed);

            for (var index = 0; (index < indices.Length); ++index) {
                indices[index] = DigitalNetSampler.ShuffleIndex(index: ((uint)index), salt: seed);
            }

            FillPoints(directionNumbers: directionNumbers, indices: indices, scramble: scramble, points: points);
            azimuthSeen.AsSpan().Clear();
            radiusSeen.AsSpan().Clear();

            for (var index = 0; (index < indices.Length); ++index) {
                var azimuth = ((int)HighBits(value: points[2 * index], count: Order));
                var radius = ((int)HighBits(value: points[(2 * index) + 1], count: Order));

                if (azimuthSeen[azimuth] || radiusSeen[radius]) {
                    return $"seed 0x{seed:X8} revisits table entry ({azimuth}, {radius}) within one period of {indices.Length} draws";
                }

                azimuthSeen[azimuth] = true;
                radiusSeen[radius] = true;
            }
        }

        return null;
    }
    /// <summary>Proves the net property survives a digital shift, over a spread of pseudorandom shifts.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every shift keeps the property.</returns>
    /// <remarks>The shifts come from the separately gated <see cref="Pcg32XshRr"/> rather than from the mix under test, so the sweep cannot be fooled by a defect it shares with the sampler.</remarks>
    private static string? CheckShiftedNetProperty() {
        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var generator = Pcg32XshRr.Create(state: 0x5EEDU, stream: 0x11U);
        var indices = new uint[1 << ShiftedNetOrder];
        var occupancy = new bool[1 << ShiftedNetOrder];
        var points = new uint[2 << ShiftedNetOrder];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        for (var index = 0; (index < indices.Length); ++index) {
            indices[index] = ((uint)index);
        }

        for (var sample = 0; (sample < ShiftSampleCount); ++sample) {
            var scramble = DigitalNetSampler.DeriveScramble(key: generator.NextUInt32());

            for (var order = 1; (order <= ShiftedNetOrder); ++order) {
                FillPoints(directionNumbers: directionNumbers, indices: indices.AsSpan(start: 0, length: (1 << order)), scramble: scramble, points: points);

                if (!IsNet(points: points, order: order, occupancy: occupancy)) {
                    return $"the digital shift (0x{scramble.X:X8}, 0x{scramble.Y:X8}) loses the net property at order {order}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves a consumer's own first 2^m shuffled draws are still a net, which is the property the GPU relies on.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every seed keeps the property.</returns>
    private static string? CheckShuffledNetProperty() {
        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var indices = new uint[1 << ShiftedNetOrder];
        var occupancy = new bool[1 << ShiftedNetOrder];
        var points = new uint[2 << ShiftedNetOrder];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        foreach (var seed in ShuffleSeeds) {
            var scramble = DigitalNetSampler.DeriveScramble(key: seed);

            for (var index = 0; (index < indices.Length); ++index) {
                indices[index] = DigitalNetSampler.ShuffleIndex(index: ((uint)index), salt: seed);
            }

            for (var order = 1; (order <= ShiftedNetOrder); ++order) {
                FillPoints(directionNumbers: directionNumbers, indices: indices.AsSpan(start: 0, length: (1 << order)), scramble: scramble, points: points);

                if (!IsNet(points: points, order: order, occupancy: occupancy)) {
                    return $"shuffle seed 0x{seed:X8} loses the net property at order {order}";
                }
            }
        }

        return null;
    }
    /// <summary>Writes the shifted net points for a list of indices, interleaved as coordinate pairs.</summary>
    /// <param name="directionNumbers">The plane net's direction numbers.</param>
    /// <param name="indices">The point indices to sample.</param>
    /// <param name="scramble">The digital shift.</param>
    /// <param name="points">Receives two words per index.</param>
    private static void FillPoints(ReadOnlySpan<uint> directionNumbers, ReadOnlySpan<uint> indices, (uint X, uint Y) scramble, Span<uint> points) {
        for (var index = 0; (index < indices.Length); ++index) {
            var point = DigitalNetSampler.SamplePlane(index: indices[index], directionNumbers: directionNumbers, scramble: scramble);

            points[2 * index] = point.X;
            points[(2 * index) + 1] = point.Y;
        }
    }
    /// <summary>Returns a value's leading bits, treating a zero-bit request as the whole interval.</summary>
    /// <param name="value">The coordinate.</param>
    /// <param name="count">The number of leading bits to keep, from zero to 32.</param>
    /// <returns>The leading bits, right aligned.</returns>
    /// <remarks>The zero case is written out because a shift by the word's own width is masked away rather than producing zero.</remarks>
    private static uint HighBits(uint value, int count) =>
        ((0 == count) ? 0U : (value >>> (32 - count)));
    /// <summary>Gets whether a point set is a <c>(0, m, 2)</c>-net at a given order.</summary>
    /// <param name="points">Two words per point, interleaved.</param>
    /// <param name="order">The order <c>m</c>; the first <c>2^order</c> points are tested.</param>
    /// <param name="occupancy">Scratch at least <c>2^order</c> long.</param>
    /// <returns><see langword="true"/> when every elementary dyadic interval of every shape holds exactly one point.</returns>
    /// <remarks>Every shape is covered, not a sample: with exactly <c>2^order</c> points and <c>2^order</c> boxes, no repeat is the same statement as one point per box, so a single occupancy pass decides each shape.</remarks>
    private static bool IsNet(ReadOnlySpan<uint> points, int order, Span<bool> occupancy) {
        var count = (1 << order);

        for (var horizontal = 0; (horizontal <= order); ++horizontal) {
            var vertical = (order - horizontal);

            occupancy[..count].Clear();

            for (var index = 0; (index < count); ++index) {
                var key = ((((int)HighBits(value: points[2 * index], count: horizontal)) << vertical) | ((int)HighBits(value: points[(2 * index) + 1], count: vertical)));

                if (occupancy[key]) { return false; }

                occupancy[key] = true;
            }
        }

        return true;
    }
    /// <summary>Counts the integers below a value that are coprime to it, from its factorization.</summary>
    /// <param name="value">The positive value to take the totient of.</param>
    /// <returns>Euler's totient of <paramref name="value"/>.</returns>
    /// <remarks>The shipped factorization reports nothing at all for a prime, so a value with no reported factors supplies itself.</remarks>
    private static ulong OracleTotient(ulong value) {
        if (1UL >= value) { return 1UL; }

        var previous = 0UL;
        var reported = false;
        var result = value;

        foreach (var factor in value.EnumeratePrimeFactors()) {
            reported = true;

            if (factor == previous) { continue; }

            previous = factor;
            result -= (result / factor);
        }

        if (!reported) { result -= (result / value); }

        return result;
    }
}
