namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly int[] PrimeExplorationSegmentSizes = [1, 2, 31, 32_768];
    private static readonly PrimeSieveStrategy[] PrimeExplorationStrategies = [PrimeSieveStrategy.WheelSteps, PrimeSieveStrategy.EightStreams,
        PrimeSieveStrategy.UnrolledPackets, PrimeSieveStrategy.CarriedPackets, PrimeSieveStrategy.CacheBlockedPackets, PrimeSieveStrategy.SpecializedPackets, PrimeSieveStrategy.PhaseSortedPackets, PrimeSieveStrategy.BucketPackets];
    private static readonly PrimeByteLayout[] PrimeExplorationLayouts = [PrimeByteLayout.Numeric, PrimeByteLayout.Algebraic];
    private static readonly PrimeSieveMode[] PrimeExplorationModes = [PrimeSieveMode.Automatic, PrimeSieveMode.Eratosthenes, PrimeSieveMode.Presieve];
    private static readonly ulong[] PrimeExplorationBoundaryValues = [
        0, 1, 2, 3, 5, 7, 73, 193, 561, 1_105, 2_047, 3_215_031_751,
        341_550_071_728_321, 3_825_123_056_546_413_051,
        (4_294_967_291UL * 4_294_967_291UL),
        18_446_744_073_709_551_557, (ulong.MaxValue - 1), ulong.MaxValue,
    ];
    private static readonly ulong[] PrimeExplorationBlocks = [0, 1, 2, uint.MaxValue, ((ulong.MaxValue / 30) - 1), (ulong.MaxValue / 30)];
    private static readonly (ulong Low, ulong High)[] PrimeExplorationWindows = [
        (0, 0), (0, 7), (1, 31), (2, 2), (3, 3), (5, 5), (7, 7),
        (24, 30), (29, 31), (30, 30), (47, 53), (961, 1_031),
        (99_911, 100_000), (31, 29),
    ];
    private static readonly ulong[][] PrimeExplorationOffsetSets = [
        [0], [0, 1], [0, 2], [0, 2, 4], [0, 2, 6], [0, 4, 6], [0, 30], [0, ulong.MaxValue],
    ];

    /// <summary>Pins the wheel tables, all products and mask actions, and wrapping rotation exponents.</summary>
    public static string? PrimeExplorationGroup() {
        ReadOnlySpan<byte> numeric = [1, 7, 11, 13, 17, 19, 23, 29];

        if (!PrimeWheel30.Residues.SequenceEqual(other: Oracles.PrimeExplorationResidues) ||
            !PrimeWheel30.NumericResidues.SequenceEqual(other: numeric) || (PrimeWheel30.NumericChannels.Length != 8)) {
            return "the algebraic or ascending residue table differs from its explicit definition";
        }
        for (var residue = 0; (residue <= byte.MaxValue); ++residue) {
            var expected = Oracles.PrimeExplorationChannel(residue: ((ulong)residue));
            var accepted = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)residue));

            if ((accepted != (expected >= 0)) || (channel != ((expected >= 0) ? expected : 0))) {
                return $"TryChannel({residue}) returned {accepted}, channel {channel}; expected {expected}";
            }
        }
        for (byte channel = 0; (channel < 8); ++channel) {
            if (PrimeWheel30.Residue(channel: channel) != Oracles.PrimeExplorationResidues[channel]) { return $"Residue({channel}) is wrong"; }
            var expectedIndex = numeric.IndexOf(value: Oracles.PrimeExplorationResidues[channel]);

            if ((PrimeWheel30.NumericIndex(channel: channel) != expectedIndex) ||
                (PrimeWheel30.ChannelAtNumericIndex(index: ((byte)expectedIndex)) != channel) ||
                (PrimeWheel30.NumericChannels[expectedIndex] != channel)) { return $"numeric position of channel {channel} is wrong"; }
            for (byte right = 0; (right < 8); ++right) {
                if (PrimeWheel30.Multiply(left: channel, right: right) != Oracles.PrimeExplorationProduct(left: channel, right: right)) {
                    return $"Multiply({channel},{right}) differs from ordinary residue multiplication";
                }
            }
            var inverse = PrimeWheel30.Inverse(channel: channel);

            if ((inverse >= 8) || (Oracles.PrimeExplorationProduct(left: channel, right: inverse) != 0)) { return $"Inverse({channel}) is wrong"; }
            for (var mask = 0; (mask <= byte.MaxValue); ++mask) {
                if (PrimeWheel30.PermuteMask(channel: channel, mask: ((byte)mask)) != Oracles.PrimeExplorationPermutation(mask: ((byte)mask), multiplier: channel)) {
                    return $"PermuteMask({mask},{channel}) differs from the direct residue action";
                }
            }
        }
        ReadOnlySpan<int> rotations = [int.MinValue, -8, -5, -4, -1, 0, 1, 2, 3, 4, 5, 7, 8, int.MaxValue];
        ReadOnlySpan<byte> powersOfSeven = [1, 7, 19, 13];

        foreach (var rotation in rotations) {
            foreach (var swap in new[] { false, true }) {
                var exponent = (((rotation % 4) + 4) % 4);
                var residue = ((powersOfSeven[exponent] * (swap ? 11 : 1)) % 30);
                var multiplier = ((byte)Oracles.PrimeExplorationChannel(residue: ((ulong)residue)));

                for (var mask = 0; (mask <= byte.MaxValue); ++mask) {
                    if (PrimeWheel30.PermuteMask(mask: ((byte)mask), rotation: rotation, swap: swap) != Oracles.PrimeExplorationPermutation(mask: ((byte)mask), multiplier: multiplier)) {
                        return $"PermuteMask({mask},{rotation},{swap}) differs from ordinary residue arithmetic";
                    }
                }
            }
        }
        foreach (var invalid in ((ReadOnlySpan<byte>)[8, 255])) {
            var failure = (Refuses(() => PrimeWheel30.Residue(channel: invalid), typeof(ArgumentOutOfRangeException), "channel", "Residue invalid channel")
                ?? (Refuses(() => PrimeWheel30.NumericIndex(channel: invalid), typeof(ArgumentOutOfRangeException), "channel", "NumericIndex invalid channel")
                ?? (Refuses(() => PrimeWheel30.ChannelAtNumericIndex(index: invalid), typeof(ArgumentOutOfRangeException), "index", "ChannelAtNumericIndex invalid index")
                ?? (Refuses(() => PrimeWheel30.Inverse(channel: invalid), typeof(ArgumentOutOfRangeException), "channel", "Inverse invalid channel")
                ?? (Refuses(() => PrimeWheel30.Multiply(left: invalid, right: 0), typeof(ArgumentOutOfRangeException), "left", "Multiply invalid left")
                ?? (Refuses(() => PrimeWheel30.Multiply(left: 0, right: invalid), typeof(ArgumentOutOfRangeException), "right", "Multiply invalid right")
                ?? Refuses(() => PrimeWheel30.PermuteMask(channel: invalid, mask: 255), typeof(ArgumentOutOfRangeException), "channel", "PermuteMask invalid channel")))))));

            if (failure is not null) { return failure; }
        }
        return null;
    }
    /// <summary>Pins full-width address boundaries, failure outputs, default identity, equality, and numerical order.</summary>
    public static string? PrimeExplorationCoordinates() {
        var empty = default(CandidateAddress);

        if ((empty.Packed != 0) || (empty.Block != 0) || (empty.Channel != 0) || (empty.Value != 1) ||
            empty.Equals(obj: ((object)1)) || empty.Equals(obj: null)) { return "the default address or object equality is wrong"; }
        foreach (var block in PrimeExplorationBlocks) {
            for (byte channel = 0; (channel < 8); ++channel) {
                var expectedValue = Oracles.PrimeExplorationValue(block: block, channel: channel);
                var expectedAccepted = (expectedValue <= ulong.MaxValue);
                var packed = (block << 3) | channel;
                var accepted = CandidateAddress.TryFromCoordinates(address: out var address, block: block, channel: channel);
                var packedAccepted = CandidateAddress.TryFromPacked(address: out var decoded, packed: packed);

                if ((accepted != expectedAccepted) || (packedAccepted != expectedAccepted)) { return $"coordinate acceptance is wrong at block {block}, channel {channel}"; }
                if (!accepted) {
                    if ((address != empty) || (decoded != empty)) { return $"failed coordinate left a nondefault output at block {block}, channel {channel}"; }
                    continue;
                }
                if ((address.Value != ((ulong)expectedValue)) || (address.Block != block) || (address.Channel != channel) || (address.Packed != packed) ||
                    (decoded != address) || !decoded.Equals(other: address) || !decoded.Equals(obj: ((object)address)) || (decoded.GetHashCode() != address.GetHashCode()) ||
                    !CandidateAddress.TryFromValue(address: out var fromValue, value: ((ulong)expectedValue)) || (fromValue != address)) {
                    return $"coordinate carriage failed at block {block}, channel {channel}";
                }
            }
        }
        foreach (var packed in ((ReadOnlySpan<ulong>)[(1UL << 63), ulong.MaxValue, (((ulong.MaxValue / 30) + 1) << 3)])) {
            if (CandidateAddress.TryFromPacked(address: out var address, packed: packed) || (address != empty)) { return $"invalid packed payload {packed} was accepted or left a nondefault output"; }
        }
        if (CandidateAddress.TryFromCoordinates(address: out var badBlock, block: ulong.MaxValue, channel: 0) || (badBlock != empty) ||
            CandidateAddress.TryFromCoordinates(address: out var badChannel, block: 0, channel: 8) || (badChannel != empty)) { return "invalid coordinate left a nondefault output or was accepted"; }
        for (var value = 0UL; (value <= 1_000); ++value) {
            var failure = PrimeExplorationCoordinateValue(value: value);

            if (failure is not null) { return failure; }
        }
        for (var offset = 0UL; (offset <= 128); ++offset) {
            var failure = PrimeExplorationCoordinateValue(value: (ulong.MaxValue - offset));

            if (failure is not null) { return failure; }
        }
        for (byte left = 0; (left < 8); ++left) {
            CandidateAddress.TryFromCoordinates(address: out var a, block: 1, channel: left);
            for (byte right = 0; (right < 8); ++right) {
                CandidateAddress.TryFromCoordinates(address: out var b, block: 1, channel: right);
                var expected = Oracles.PrimeExplorationResidues[left].CompareTo(value: Oracles.PrimeExplorationResidues[right]);

                if ((Math.Sign(value: a.CompareTo(other: b)) != Math.Sign(value: expected)) || ((a == b) != (left == right)) || ((a != b) != (left != right))) {
                    return $"address comparison follows packed rather than numerical order at channels {left},{right}";
                }
            }
        }
        return null;
    }

    private static string? PrimeExplorationCoordinateValue(ulong value) {
        var expectedChannel = Oracles.PrimeExplorationChannel(residue: (value % 30));
        var accepted = CandidateAddress.TryFromValue(address: out var address, value: value);

        if (accepted != (expectedChannel >= 0)) { return $"TryFromValue({value}) acceptance differs from the explicit residue definition"; }
        if (!accepted) { return ((address == default) ? null : $"TryFromValue({value}) failed but left a nondefault output"); }
        return (((address.Value == value) && (address.Block == (value / 30)) && (address.Channel == expectedChannel))
            ? null : $"TryFromValue({value}) carried the wrong coordinate");
    }

    /// <summary>Decides the entire small sieve band and independently rechecks the full-width witness ladder.</summary>
    public static string? PrimeExplorationPrimality() {
        for (var value = 0; (value < Oracles.PrimeExplorationSieve.Length); ++value) {
            var expected = Oracles.PrimeExplorationSieve[value];
            var failure = PrimeExplorationPrimalityValue(expected: expected, value: ((ulong)value));

            if (failure is not null) { return failure; }
        }
        foreach (var value in PrimeExplorationBoundaryValues) {
            var failure = PrimeExplorationPrimalityValue(value, Oracles.ExactPrimality(value: value));

            if (failure is not null) { return failure; }
        }
        if (PrimeExploration.IsPrimeCandidate(candidate: default)) { return "the default candidate address of one was reported prime"; }
        return null;
    }
    /// <summary>Rechecks full-width raw and forced-odd domain values against trial division and arbitrary-width rounds.</summary>
    public static string? PrimeExplorationPrimalitySweep(long[] left, long[] right) {
        foreach (var raw in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var drawn = unchecked((ulong)raw);

            foreach (var value in ((ReadOnlySpan<ulong>)[drawn, drawn | 1UL])) {
                var failure = PrimeExplorationPrimalityValue(value, Oracles.ExactPrimality(value: value));

                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static string? PrimeExplorationPrimalityValue(ulong value, bool expected) {
        if ((PrimeExploration.IsPrime(value: value) != expected) || (PrimeField64.IsPrime(value: value) != expected)) {
            return $"machineword primality disagrees with the independent decision at {value}, expected {expected}";
        }
        if (CandidateAddress.TryFromValue(address: out var address, value: value) && (PrimeExploration.IsPrimeCandidate(candidate: address) != expected)) {
            return $"IsPrimeCandidate disagrees with the independent decision at value {value}";
        }
        return null;
    }

    /// <summary>Checks every strategy, byte layout, and sieve mode against ordinary integer-index sieving.</summary>
    public static string? PrimeExplorationEnumeration() {
        foreach (var strategy in PrimeExplorationStrategies) {
            foreach (var layout in PrimeExplorationLayouts) {
                foreach (var mode in PrimeExplorationModes) {
                    foreach (var segmentBytes in PrimeExplorationSegmentSizes) {
                        var high = ((segmentBytes == 32_768) ? 100_000UL : 10_000UL);
                        var failure = PrimeExplorationEnumerationWindow(high: high, layout: layout, low: 0, mode: mode, segmentBytes: segmentBytes, strategy: strategy);

                        if (failure is not null) { return failure; }
                        foreach (var (low, endpoint) in PrimeExplorationWindows) {
                            failure = PrimeExplorationEnumerationWindow(high: endpoint, layout: layout, low: low, mode: mode, segmentBytes: segmentBytes, strategy: strategy);
                            if (failure is not null) { return failure; }
                        }
                    }
                    if (mode == PrimeSieveMode.Eratosthenes) { continue; }
                    var topFailure = PrimeExplorationEnumerationWindow(high: ulong.MaxValue, layout: layout, low: (ulong.MaxValue - 128), mode: mode, segmentBytes: 2, strategy: strategy);

                    if (topFailure is not null) { return topFailure; }
                }
                var upperBaseFailure = PrimeExplorationEnumerationWindow(high: 4_295_098_401, layout: layout, low: 4_295_098_337, mode: PrimeSieveMode.Eratosthenes, segmentBytes: 2, strategy: strategy);

                if (upperBaseFailure is not null) { return upperBaseFailure; }
            }
        }
        return null;
    }

    private static string? PrimeExplorationEnumerationWindow(ulong low, ulong high, int segmentBytes, PrimeSieveStrategy strategy, PrimeByteLayout layout, PrimeSieveMode mode) {
        var expected = Oracles.PrimeExplorationPrimes(high: high, low: low);

        foreach (var usePreSieve in ((ReadOnlySpan<bool>)[false, true])) {
            var actual = new List<ulong>();

            PrimeExploration.Enumerate(high: high, layout: layout, low: low, mode: mode, onPrime: actual.Add, segmentBytes: segmentBytes, strategy: strategy, usePreSieve: usePreSieve);
            var count = PrimeExploration.Count(high: high, layout: layout, low: low, mode: mode, segmentBytes: segmentBytes, strategy: strategy, usePreSieve: usePreSieve);

            if (count != ((ulong)expected.Count)) { return $"count differs in [{low},{high}] with segment {segmentBytes}, {strategy}/{layout}/{mode}, patterns={usePreSieve}; counts {count}/{expected.Count}"; }
            if (!actual.SequenceEqual(second: expected)) { return $"enumeration differs in [{low},{high}] with segment {segmentBytes}, {strategy}/{layout}/{mode}, patterns={usePreSieve}; counts {actual.Count}/{expected.Count}"; }
        }
        return null;
    }

    /// <summary>Checks pattern periods, vector tails, and small-prime restoration against an integer-index sieve.</summary>
    public static string? PrimeExplorationPatterns() {
        var reference = Oracles.PrimeSieve(inclusiveMaximum: 600_000);
        ReadOnlySpan<(int Low, int High)> windows = [(0, 600_000), (7, 163), (161, 193), (178_697, 200_003), (599_977, 600_000)];

        foreach (var (low, high) in windows) {
            var expected = new List<ulong>();

            for (var value = low; (value <= high); ++value) { if (reference[value]) { expected.Add(item: ((ulong)value)); } }
            foreach (var strategy in PrimeExplorationStrategies) {
                foreach (var layout in PrimeExplorationLayouts) {
                    foreach (var length in ((ReadOnlySpan<int>)[63, 64, 65, 4096, 32768])) {
                        var actual = new List<ulong>();

                        PrimeExploration.Enumerate(high: ((ulong)high), layout: layout, low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: length, strategy: strategy, usePreSieve: true);
                        if (!actual.SequenceEqual(second: expected)) { return $"periodic filter differs from integer-index sieve in [{low},{high}], {strategy}/{layout}/{length} bytes"; }
                        var count = PrimeExploration.Count(high: ((ulong)high), layout: layout, low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, segmentBytes: length, strategy: strategy, usePreSieve: true);

                        if (count != ((ulong)expected.Count)) { return $"periodic filter population count differs in [{low},{high}], {strategy}/{layout}/{length} bytes"; }
                    }
                }
            }
        }
        return null;
    }
    /// <summary>Checks packet alignment, activation, and carried cursors across complete and partial cache chunks.</summary>
    public static string? PrimeExplorationPackets() {
        var reference = Oracles.PrimeSieve(inclusiveMaximum: 2_000_000);
        ReadOnlySpan<(int Low, int High)> windows = [(0, 2_000_000), (491_477, 1_000_003), (983_027, 1_499_999)];

        foreach (var (low, high) in windows) {
            var expected = new List<ulong>();

            for (var value = low; (value <= high); ++value) { if (reference[value]) { expected.Add(item: ((ulong)value)); } }
            foreach (var strategy in ((ReadOnlySpan<PrimeSieveStrategy>)[PrimeSieveStrategy.UnrolledPackets, PrimeSieveStrategy.CarriedPackets, PrimeSieveStrategy.CacheBlockedPackets, PrimeSieveStrategy.SpecializedPackets, PrimeSieveStrategy.PhaseSortedPackets, PrimeSieveStrategy.BucketPackets])) {
                foreach (var layout in PrimeExplorationLayouts) {
                    foreach (var length in ((ReadOnlySpan<int>)[16383, 16384, 16385, 32769, 49151, 1048576])) {
                        foreach (var usePreSieve in ((ReadOnlySpan<bool>)[false, true])) {
                            var actual = new List<ulong>();

                            PrimeExploration.Enumerate(high: ((ulong)high), layout: layout, low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: length, strategy: strategy, usePreSieve: usePreSieve);
                            if (!actual.SequenceEqual(second: expected)) { return $"packet output differs in [{low},{high}], {strategy}/{layout}/{length}, patterns={usePreSieve}"; }
                            var count = PrimeExploration.Count(high: ((ulong)high), layout: layout, low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, segmentBytes: length, strategy: strategy, usePreSieve: usePreSieve);

                            if (count != ((ulong)expected.Count)) { return $"packet count differs in [{low},{high}], {strategy}/{layout}/{length}, patterns={usePreSieve}"; }
                        }
                    }
                }
            }
        }
        // These bands exercise medium buckets at 32 KiB and three small-prime cache chunks at uint's top.
        foreach (var (low, high) in ((ReadOnlySpan<(uint Low, uint High)>)[(49_000_000, 50_000_000), ((uint.MaxValue - 4_000_000), uint.MaxValue)])) {
            var expected = Oracles.PrimeExplorationIntegerWindow(high: high, low: low);

            foreach (var layout in PrimeExplorationLayouts) {
                foreach (var length in ((ReadOnlySpan<int>)[32768, 1048576])) {
                    foreach (var usePreSieve in ((ReadOnlySpan<bool>)[false, true])) {
                        var actual = new List<ulong>();

                        PrimeExploration.Enumerate(high: high, layout: layout, low: low, mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: length, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: usePreSieve);
                        if (!actual.SequenceEqual(second: expected)) { return $"bucket output differs in [{low},{high}], {layout}/{length}, patterns={usePreSieve}"; }
                        var count = PrimeExploration.Count(high: high, layout: layout, low: low, mode: PrimeSieveMode.Eratosthenes, segmentBytes: length, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: usePreSieve);

                        if (count != ((ulong)expected.Count)) { return $"bucket count differs in [{low},{high}], {layout}/{length}, patterns={usePreSieve}"; }
                    }
                }
            }
        }
        return null;
    }
    /// <summary>Checks masks and tuple output against naive exact primality, including small-prime exceptions and overflow.</summary>
    public static string? PrimeExplorationConstellations() {
        foreach (var offsets in PrimeExplorationOffsetSets) {
            var copiedInput = offsets.ToArray();
            var constellation = new PrimeConstellation(offsets: copiedInput);

            copiedInput[0] = 1;
            if (!constellation.Offsets.SequenceEqual(other: offsets) || (constellation.AdmissibleMask != Oracles.PrimeExplorationConstellationMask(offsets: offsets))) {
                return $"constellation [{string.Join(separator: ',', values: offsets)}] carries a wrong offset sequence or admissibility mask";
            }
            foreach (var layout in PrimeExplorationLayouts) {
                foreach (var strategy in PrimeExplorationStrategies) {
                    foreach (var mode in PrimeExplorationModes) {
                        var expected = Oracles.PrimeExplorationConstellations(high: 1_000, low: 0, offsets: offsets);
                        var actual = new List<ulong>();

                        constellation.Enumerate(high: 1_000, layout: layout, low: 0, mode: mode, segmentBytes: 2, strategy: strategy, visit: actual.Add);
                        if (!actual.SequenceEqual(second: expected)) { return $"constellation [{string.Join(separator: ',', values: offsets)}] differs at {strategy}/{layout}/{mode}, including possible 2/3/5 exceptions"; }
                    }
                }
            }
            var topExpected = Oracles.PrimeExplorationConstellations(high: ulong.MaxValue, low: (ulong.MaxValue - 128), offsets: offsets);
            var topActual = new List<ulong>();

            constellation.Enumerate(high: ulong.MaxValue, layout: PrimeByteLayout.Algebraic, low: (ulong.MaxValue - 128), mode: PrimeSieveMode.Presieve, segmentBytes: 1, strategy: PrimeSieveStrategy.EightStreams, visit: topActual.Add);
            if (!topActual.SequenceEqual(second: topExpected)) { return $"constellation [{string.Join(separator: ',', values: offsets)}] wraps a high endpoint"; }
        }
        return (Refuses(() => new PrimeConstellation(offsets: []), typeof(ArgumentException), "offsets", "empty constellation")
            ?? (Refuses(() => new PrimeConstellation(offsets: [1]), typeof(ArgumentException), "offsets", "nonzero first offset")
            ?? (Refuses(() => new PrimeConstellation(offsets: [0, 0]), typeof(ArgumentException), "offsets", "duplicate offsets")
            ?? Refuses(() => new PrimeConstellation(offsets: [0, 2, 1]), typeof(ArgumentException), "offsets", "descending offsets"))));
    }
    /// <summary>Checks all counts, gaps, and channel transitions from an independently decided ordered prime list.</summary>
    public static string? PrimeExplorationStatistics() {
        foreach (var (low, high) in PrimeExplorationWindows) {
            var expected = Oracles.PrimeExplorationStatistics(high: high, low: low);

            foreach (var layout in PrimeExplorationLayouts) {
                foreach (var strategy in PrimeExplorationStrategies) {
                    var actual = PrimeStatistics.Measure(high: high, layout: layout, low: low, mode: PrimeSieveMode.Eratosthenes, segmentBytes: 1, strategy: strategy);

                    if ((actual.Count != expected.Count) || (actual.SmallPrimeCount != expected.SmallCount) || (actual.SmallPrimeMask != expected.SmallMask) ||
                        !actual.ChannelCounts.SequenceEqual(other: expected.Channels) || !actual.TransitionCounts.SequenceEqual(other: expected.Transitions) ||
                        (actual.GapCounts.Count != expected.Gaps.Count)) { return $"statistics shape differs in [{low},{high}] at {strategy}/{layout}"; }
                    foreach (var (gap, count) in expected.Gaps) {
                        if (!actual.GapCounts.TryGetValue(key: gap, value: out var found) || (found != count)) { return $"gap {gap} count differs in [{low},{high}]"; }
                    }
                    for (byte from = 0; (from < 8); ++from) {
                        for (byte to = 0; (to < 8); ++to) {
                            if (actual.TransitionCount(from: from, to: to) != expected.Transitions[((from * 8) + to)]) { return $"transition {from}->{to} differs in [{low},{high}]"; }
                        }
                    }
                    var failure = (Refuses(() => actual.TransitionCount(from: 8, to: 0), typeof(ArgumentOutOfRangeException), "from", "invalid transition source")
                        ?? Refuses(() => actual.TransitionCount(from: 0, to: 8), typeof(ArgumentOutOfRangeException), "to", "invalid transition target"));

                    if (failure is not null) { return failure; }
                }
            }
        }
        return null;
    }
    /// <summary>Pins validation before the empty-range return, including every enumeration configuration parameter.</summary>
    public static string? PrimeExplorationRefusals() {
        Action<ulong> callback = _ => { };
        var wheelSteps = ((int)PrimeSieveStrategy.WheelSteps);
        var eightStreams = ((int)PrimeSieveStrategy.EightStreams);
        var unrolledPackets = ((int)PrimeSieveStrategy.UnrolledPackets);
        var carriedPackets = ((int)PrimeSieveStrategy.CarriedPackets);
        var cacheBlockedPackets = ((int)PrimeSieveStrategy.CacheBlockedPackets);
        var specializedPackets = ((int)PrimeSieveStrategy.SpecializedPackets);
        var phaseSortedPackets = ((int)PrimeSieveStrategy.PhaseSortedPackets);
        var bucketPackets = ((int)PrimeSieveStrategy.BucketPackets);
        var numeric = ((int)PrimeByteLayout.Numeric);
        var algebraic = ((int)PrimeByteLayout.Algebraic);
        var automatic = ((int)PrimeSieveMode.Automatic);
        var eratosthenes = ((int)PrimeSieveMode.Eratosthenes);
        var presieve = ((int)PrimeSieveMode.Presieve);

        if ((wheelSteps != 0) || (eightStreams != 1) || (unrolledPackets != 2) || (carriedPackets != 3) || (cacheBlockedPackets != 4) || (specializedPackets != 5) || (phaseSortedPackets != 6) || (bucketPackets != 7) || (numeric != 0) || (algebraic != 1) || (automatic != 0) || (eratosthenes != 1) || (presieve != 2)) {
            return "enumeration option ordinals differ from their dispatch contract";
        }
        var countFailure = (Refuses(() => PrimeExploration.Count(2, 1, 0), typeof(ArgumentOutOfRangeException), "segmentBytes", "zero count segment length")
            ?? (Refuses(() => PrimeExploration.Count(2, 1, -1), typeof(ArgumentOutOfRangeException), "segmentBytes", "negative count segment length")
            ?? (Refuses(() => PrimeExploration.Count(2, 1, int.MaxValue), typeof(ArgumentOutOfRangeException), "segmentBytes", "oversized count segment length")
            ?? (Refuses(() => PrimeExploration.Count(2, 1, 1, ((PrimeSieveStrategy)255)), typeof(ArgumentOutOfRangeException), "strategy", "invalid count strategy")
            ?? (Refuses(() => PrimeExploration.Count(2, 1, 1, PrimeSieveStrategy.EightStreams, ((PrimeByteLayout)255)), typeof(ArgumentOutOfRangeException), "layout", "invalid count layout")
            ?? Refuses(() => PrimeExploration.Count(high: 1, layout: PrimeByteLayout.Numeric, low: 2, mode: ((PrimeSieveMode)255), segmentBytes: 1, strategy: PrimeSieveStrategy.EightStreams), typeof(ArgumentOutOfRangeException), "mode", "invalid count mode"))))));

        if (countFailure is not null) { return countFailure; }
        return (Refuses(() => PrimeExploration.Enumerate(2, 1, null!), typeof(ArgumentNullException), "onPrime", "null prime callback")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, 0), typeof(ArgumentOutOfRangeException), "segmentBytes", "zero segment length")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, -1), typeof(ArgumentOutOfRangeException), "segmentBytes", "negative segment length")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, int.MaxValue), typeof(ArgumentOutOfRangeException), "segmentBytes", "segment length above Array.MaxLength")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, 1, ((PrimeSieveStrategy)255)), typeof(ArgumentOutOfRangeException), "strategy", "invalid sieve strategy")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, 1, PrimeSieveStrategy.WheelSteps, ((PrimeByteLayout)255)), typeof(ArgumentOutOfRangeException), "layout", "invalid byte layout")
            ?? Refuses(() => PrimeExploration.Enumerate(high: 1, layout: PrimeByteLayout.Numeric, low: 2, mode: ((PrimeSieveMode)255), onPrime: callback, segmentBytes: 1, strategy: PrimeSieveStrategy.WheelSteps), typeof(ArgumentOutOfRangeException), "mode", "invalid sieve mode")))))));
    }
}
