namespace Puck.Maths.Tests;

/// <summary>
/// Fast exact and structural digital-net claims in the law
/// suite. The declarations in <c>laws/post-digital-net.json</c> invoke these methods as Default-tier laws, so every
/// assertion participates in both the ordinary test gate and the mechanically generated public-member coverage
/// ledger. Every sweep here is a fixed, seeded computation -- no wall clock, no <see cref="Random"/>, no floating
/// point -- exactly as the law suite's house rules require.
/// </summary>
internal static class DigitalNetClaims {
    private static readonly uint[] ShuffleSeeds = [0x00000000U, 0x00000001U, 0x5A5A5A5AU, 0xDEADBEEFU, 0xFFFFFFFFU];

    /// <summary>Ports <c>DigitalNetStage.CheckNetProperty</c>: the unshifted (0, m, 2)-net property, exhaustively at
    /// every order 1 through 14 and every dyadic box shape.</summary>
    public static string? NetPropertyThroughOrderFourteenSurface() {
        const int MaximumOrder = 14;

        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var points = new uint[2 << MaximumOrder];
        var occupancy = new bool[1 << MaximumOrder];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        for (var order = 1; (order <= MaximumOrder); ++order) {
            var count = (1 << order);

            for (var index = 0; (index < count); ++index) {
                var point = DigitalNetSampler.SamplePlane(index: ((uint)index), directionNumbers: directionNumbers, scramble: (X: 0U, Y: 0U));

                points[2 * index] = point.X;
                points[(2 * index) + 1] = point.Y;
            }

            if (!IsPlaneNet(points: points.AsSpan(start: 0, length: (2 * count)), order: order, occupancy: occupancy.AsSpan(start: 0, length: count))) {
                return $"the unshifted (0, m, 2)-net property fails at order {order}";
            }
        }

        return null;
    }

    /// <summary>Ports <c>DigitalNetStage.CheckShiftedNetProperty</c> and <c>CheckShuffledNetProperty</c>: the net
    /// property surviving a spread of pseudorandom digital shifts, and every shuffled index block being itself a
    /// net.</summary>
    public static string? ShiftedAndShuffledBlocksAreNetsSurface() {
        const int Order = 12;
        const int ShiftSampleCount = 64;

        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var points = new uint[2 << Order];
        var occupancy = new bool[1 << Order];
        var indices = new uint[1 << Order];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        for (var index = 0; (index < indices.Length); ++index) {
            indices[index] = ((uint)index);
        }

        // The net property under a SPREAD of pseudorandom digital shifts, rather than the one fixed shift vector
        // sampling.digital-net-identities-and-net-property sweeps.
        var generator = Pcg32XshRr.Create(state: 0x5EEDUL, stream: 0x11UL);

        for (var sample = 0; (sample < ShiftSampleCount); ++sample) {
            var scramble = DigitalNetSampler.DeriveScramble(key: generator.NextUInt32());

            for (var order = 1; (order <= Order); ++order) {
                var count = (1 << order);

                for (var index = 0; (index < count); ++index) {
                    var point = DigitalNetSampler.SamplePlane(index: indices[index], directionNumbers: directionNumbers, scramble: scramble);

                    points[2 * index] = point.X;
                    points[(2 * index) + 1] = point.Y;
                }

                if (!IsPlaneNet(points: points.AsSpan(start: 0, length: (2 * count)), order: order, occupancy: occupancy.AsSpan(start: 0, length: count))) {
                    return $"the digital shift (0x{scramble.X:X8}, 0x{scramble.Y:X8}) loses the net property at order {order}";
                }
            }
        }

        // Every ShuffleIndex-permuted block of indices is ITSELF a net, unshifted -- the deeper structural fact the
        // aligned-block law alone does not state.
        var shuffled = new uint[1 << Order];

        foreach (var seed in ShuffleSeeds) {
            for (var order = 1; (order <= Order); ++order) {
                var count = (1 << order);

                for (var index = 0; (index < count); ++index) {
                    shuffled[index] = DigitalNetSampler.ShuffleIndex(index: ((uint)index), salt: seed);
                }

                for (var index = 0; (index < count); ++index) {
                    var point = DigitalNetSampler.SamplePlane(index: shuffled[index], directionNumbers: directionNumbers, scramble: (X: 0U, Y: 0U));

                    points[2 * index] = point.X;
                    points[(2 * index) + 1] = point.Y;
                }

                if (!IsPlaneNet(points: points.AsSpan(start: 0, length: (2 * count)), order: order, occupancy: occupancy.AsSpan(start: 0, length: count))) {
                    return $"shuffle seed 0x{seed:X8} loses the net property at order {order}";
                }
            }
        }

        return null;
    }

    /// <summary>Ports <c>DigitalNetStage.CheckRadicalInverse</c>'s full breadth: dimension zero against plain bit
    /// reversal over a dense prefix and a full-range odd-stride sweep, reaching direction numbers 12 through 31 that
    /// sampling.digital-net-identities-and-net-property's 4096-index sweep cannot.</summary>
    public static string? RadicalInverseFullRangeSurface() {
        const uint FullRangeStride = 65_537U;
        const int PrefixLength = (1 << 20);

        var directionNumbers = new uint[DigitalNetSampler.DirectionNumberCount];

        DigitalNetSampler.BuildBitReversalDirectionNumbers(destination: directionNumbers);

        for (var index = 0U; (index < PrefixLength); ++index) {
            var sampled = DigitalNetSampler.Sample(index: index, directionNumbers: directionNumbers, scramble: 0U);
            var reversed = ReverseBitsIndependently(value: index);

            if (sampled != reversed) { return $"the radical inverse of {index} is 0x{sampled:X8}, not the independently reversed 0x{reversed:X8}"; }
        }

        for (var position = 0L; (position < (1L << 32)); position += FullRangeStride) {
            var index = ((uint)position);
            var sampled = DigitalNetSampler.Sample(index: index, directionNumbers: directionNumbers, scramble: 0U);
            var reversed = ReverseBitsIndependently(value: index);

            if (sampled != reversed) { return $"the radical inverse of {index} is 0x{sampled:X8}, not the independently reversed 0x{reversed:X8}"; }
        }

        return null;
    }

    /// <summary>Ports <c>DigitalNetStage.CheckSampleTable</c>'s reproducibility check and
    /// <c>CheckQuantizedCoverage</c>: two builds agree bit for bit, and one full period of shuffled, shifted draws
    /// visits every azimuth entry -- and independently every radius entry -- of the cone direction table exactly
    /// once.</summary>
    public static string? ConeTableBuildPurityAndQuantizedCoverageSurface() {
        const double CapHalfAngle = 0.11d;

        var first = new uint[ConeDirectionTable.WordCount];
        var second = new uint[ConeDirectionTable.WordCount];

        ConeDirectionTable.Build(capHalfAngleRadians: CapHalfAngle, destination: first);
        ConeDirectionTable.Build(capHalfAngleRadians: CapHalfAngle, destination: second);

        if (!first.AsSpan().SequenceEqual(other: second)) {
            return "two builds of the spherical-cap sample table disagree, so Build is not pure";
        }

        var order = ConeDirectionTable.TableIndexBitCount;
        var directionNumbers = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var indices = new uint[1 << order];
        var points = new uint[2 << order];
        var azimuthSeen = new bool[ConeDirectionTable.AzimuthEntryCount];
        var radiusSeen = new bool[ConeDirectionTable.RadiusEntryCount];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

        foreach (var seed in ShuffleSeeds) {
            var scramble = DigitalNetSampler.DeriveScramble(key: seed);

            for (var index = 0; (index < indices.Length); ++index) {
                indices[index] = DigitalNetSampler.ShuffleIndex(index: ((uint)index), salt: seed);
            }

            for (var index = 0; (index < indices.Length); ++index) {
                var point = DigitalNetSampler.SamplePlane(index: indices[index], directionNumbers: directionNumbers, scramble: scramble);

                points[2 * index] = point.X;
                points[(2 * index) + 1] = point.Y;
            }

            Array.Clear(array: azimuthSeen);
            Array.Clear(array: radiusSeen);

            for (var index = 0; (index < indices.Length); ++index) {
                var azimuth = ((int)HighBits(value: points[2 * index], count: order));
                var radius = ((int)HighBits(value: points[(2 * index) + 1], count: order));

                if (azimuthSeen[azimuth] || radiusSeen[radius]) {
                    return $"seed 0x{seed:X8} revisits table entry ({azimuth}, {radius}) within one period of {indices.Length} draws";
                }

                azimuthSeen[azimuth] = true;
                radiusSeen[radius] = true;
            }
        }

        return null;
    }

    /// <summary>Gets whether a point set is a <c>(0, m, 2)</c>-net at a given order: every elementary dyadic
    /// interval of every shape holds exactly one point.</summary>
    private static bool IsPlaneNet(ReadOnlySpan<uint> points, int order, Span<bool> occupancy) {
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

    /// <summary>Returns a value's leading bits, right aligned; a zero-bit request is the whole interval, written out
    /// because a shift by the word's own width is masked away rather than producing zero.</summary>
    private static uint HighBits(uint value, int count) =>
        ((0 == count) ? 0U : (value >>> (32 - count)));

    /// <summary>Reverses a word's bits with its own loop, independent of any shipped reversal helper.</summary>
    private static uint ReverseBitsIndependently(uint value) {
        var reversed = 0U;

        for (var bit = 0; (bit < 32); ++bit) {
            reversed |= (((value >> bit) & 1U) << (31 - bit));
        }

        return reversed;
    }
}
