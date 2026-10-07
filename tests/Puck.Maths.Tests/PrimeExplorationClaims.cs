namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly int[] PrimeExplorationSegmentSizes = [1, 2, 31, 32_768];
    private static readonly PrimeSieveMode[] PrimeExplorationModes = [PrimeSieveMode.Automatic, PrimeSieveMode.Eratosthenes, PrimeSieveMode.Presieve, PrimeSieveMode.Windowed];
    private static readonly ulong[] PrimeExplorationBoundaryValues = [
        0, 1, 2, 3, 5, 7, 73, 193, 561, 1_105, 2_047, 3_215_031_751,
        341_550_071_728_321, 3_825_123_056_546_413_051,
        (4_294_967_291UL * 4_294_967_291UL),
        18_446_744_073_709_551_557, (ulong.MaxValue - 1), ulong.MaxValue,
    ];
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
        if (PrimeExtensions.IsPrime(value: value) != expected) {
            return $"machineword primality disagrees with the independent decision at {value}, expected {expected}";
        }
        return null;
    }

    /// <summary>Checks every sieve policy and segment size against ordinary integer-index sieving.</summary>
    public static string? PrimeExplorationEnumeration() {
        foreach (var mode in PrimeExplorationModes) {
            foreach (var segmentBytes in PrimeExplorationSegmentSizes) {
                var high = ((segmentBytes == 32_768) ? 100_000UL : 10_000UL);
                var failure = PrimeExplorationEnumerationWindow(high: high, low: 0, mode: mode, segmentBytes: segmentBytes);

                if (failure is not null) { return failure; }
                foreach (var (low, endpoint) in PrimeExplorationWindows) {
                    failure = PrimeExplorationEnumerationWindow(high: endpoint, low: low, mode: mode, segmentBytes: segmentBytes);
                    if (failure is not null) { return failure; }
                }
            }
            // Complete top-window sieves stream every uint base prime; the deep final-window laws own them.
            if (mode is PrimeSieveMode.Eratosthenes or PrimeSieveMode.Windowed) { continue; }
            var topFailure = PrimeExplorationEnumerationWindow(high: ulong.MaxValue, low: (ulong.MaxValue - 128), mode: mode, segmentBytes: 2);

            if (topFailure is not null) { return topFailure; }
        }
        foreach (var completeMode in new[] { PrimeSieveMode.Eratosthenes, PrimeSieveMode.Windowed }) {
            var upperBaseFailure = PrimeExplorationEnumerationWindow(high: 4_295_098_401, low: 4_295_098_337, mode: completeMode, segmentBytes: 2);

            if (upperBaseFailure is not null) { return upperBaseFailure; }
        }
        foreach (var (low, high) in PrimeExplorationWindows) {
            var expected = Oracles.PrimeExplorationPrimes(high: high, low: low);
            var actual = new List<ulong>();

            PrimeExploration.Enumerate(low, high, actual.Add);
            if (!actual.SequenceEqual(second: expected) || (PrimeExploration.Count(low, high) != ((ulong)expected.Count))) {
                return $"the public automatic enumeration or count differs in [{low},{high}]";
            }
        }
        return null;
    }

    private static string? PrimeExplorationEnumerationWindow(ulong low, ulong high, int segmentBytes, PrimeSieveMode mode) {
        var expected = Oracles.PrimeExplorationPrimes(high: high, low: low);
        var actual = new List<ulong>();

        PrimeExploration.Enumerate(high: high, low: low, mode: mode, onPrime: actual.Add, segmentBytes: segmentBytes);
        var count = PrimeExploration.Count(high: high, low: low, mode: mode, segmentBytes: segmentBytes);

        if (count != ((ulong)expected.Count)) { return $"count differs in [{low},{high}] with segment {segmentBytes}, {mode}; counts {count}/{expected.Count}"; }
        if (!actual.SequenceEqual(second: expected)) { return $"enumeration differs in [{low},{high}] with segment {segmentBytes}, {mode}; counts {actual.Count}/{expected.Count}"; }
        return null;
    }

    /// <summary>Checks pattern periods, vector tails, and small-prime restoration against an integer-index sieve.</summary>
    public static string? PrimeExplorationPatterns() {
        var reference = Oracles.PrimeSieve(inclusiveMaximum: 600_000);
        ReadOnlySpan<(int Low, int High)> windows = [(0, 600_000), (7, 163), (161, 193), (178_697, 200_003), (599_977, 600_000)];

        foreach (var (low, high) in windows) {
            var expected = new List<ulong>();

            for (var value = low; (value <= high); ++value) { if (reference[value]) { expected.Add(item: ((ulong)value)); } }
            foreach (var length in ((ReadOnlySpan<int>)[63, 64, 65, 4096, 32768])) {
                var actual = new List<ulong>();

                PrimeExploration.Enumerate(high: ((ulong)high), low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: length);
                if (!actual.SequenceEqual(second: expected)) { return $"periodic filter differs from integer-index sieve in [{low},{high}], {length} bytes"; }
                var count = PrimeExploration.Count(high: ((ulong)high), low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, segmentBytes: length);

                if (count != ((ulong)expected.Count)) { return $"periodic filter population count differs in [{low},{high}], {length} bytes"; }
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
            foreach (var length in ((ReadOnlySpan<int>)[16383, 16384, 16385, 32769, 49151, 1048576])) {
                var actual = new List<ulong>();

                PrimeExploration.Enumerate(high: ((ulong)high), low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: length);
                if (!actual.SequenceEqual(second: expected)) { return $"packet output differs in [{low},{high}], {length} bytes"; }
                var count = PrimeExploration.Count(high: ((ulong)high), low: ((ulong)low), mode: PrimeSieveMode.Eratosthenes, segmentBytes: length);

                if (count != ((ulong)expected.Count)) { return $"packet count differs in [{low},{high}], {length} bytes"; }
            }
        }
        // These bands exercise medium buckets at 32 KiB and three small-prime cache chunks at uint's top.
        foreach (var (low, high) in ((ReadOnlySpan<(uint Low, uint High)>)[(49_000_000, 50_000_000), ((uint.MaxValue - 4_000_000), uint.MaxValue)])) {
            var expected = Oracles.PrimeExplorationIntegerWindow(high: high, low: low);

            foreach (var length in ((ReadOnlySpan<int>)[32768, 1048576])) {
                var actual = new List<ulong>();

                PrimeExploration.Enumerate(high: high, low: low, mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: length);
                if (!actual.SequenceEqual(second: expected)) { return $"bucket output differs in [{low},{high}], {length} bytes"; }
                var count = PrimeExploration.Count(high: high, low: low, mode: PrimeSieveMode.Eratosthenes, segmentBytes: length);

                if (count != ((ulong)expected.Count)) { return $"bucket count differs in [{low},{high}], {length} bytes"; }
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
            var expected = Oracles.PrimeExplorationConstellations(high: 1_000, low: 0, offsets: offsets);
            var actual = new List<ulong>();

            constellation.Enumerate(high: 1_000, low: 0, visit: actual.Add);
            if (!actual.SequenceEqual(second: expected)) { return $"constellation [{string.Join(separator: ',', values: offsets)}] differs, including possible 2/3/5 exceptions"; }
            var topExpected = Oracles.PrimeExplorationConstellations(high: ulong.MaxValue, low: (ulong.MaxValue - 128), offsets: offsets);
            var topActual = new List<ulong>();

            constellation.Enumerate(high: ulong.MaxValue, low: (ulong.MaxValue - 128), visit: topActual.Add);
            if (!topActual.SequenceEqual(second: topExpected)) { return $"constellation [{string.Join(separator: ',', values: offsets)}] wraps a high endpoint"; }
        }
        return (Refuses(() => new PrimeConstellation(offsets: []), typeof(ArgumentException), "offsets", "empty constellation")
            ?? (Refuses(() => new PrimeConstellation(offsets: [1]), typeof(ArgumentException), "offsets", "nonzero first offset")
            ?? (Refuses(() => new PrimeConstellation(offsets: [0, 0]), typeof(ArgumentException), "offsets", "duplicate offsets")
            ?? Refuses(() => new PrimeConstellation(offsets: [0, 2, 1]), typeof(ArgumentException), "offsets", "descending offsets"))));
    }
    /// <summary>Pins validation before the empty-range return, including the internal policy and segment parameters.</summary>
    public static string? PrimeExplorationRefusals() {
        Action<ulong> callback = _ => { };
        var automatic = ((int)PrimeSieveMode.Automatic);
        var eratosthenes = ((int)PrimeSieveMode.Eratosthenes);
        var presieve = ((int)PrimeSieveMode.Presieve);
        var windowed = ((int)PrimeSieveMode.Windowed);

        if ((automatic != 0) || (eratosthenes != 1) || (presieve != 2) || (windowed != 3)) {
            return "sieve policy ordinals differ from their dispatch contract";
        }
        var countFailure = (Refuses(() => PrimeExploration.Count(2, 1, 0, PrimeSieveMode.Automatic), typeof(ArgumentOutOfRangeException), "segmentBytes", "zero count segment length")
            ?? (Refuses(() => PrimeExploration.Count(2, 1, -1, PrimeSieveMode.Automatic), typeof(ArgumentOutOfRangeException), "segmentBytes", "negative count segment length")
            ?? (Refuses(() => PrimeExploration.Count(2, 1, int.MaxValue, PrimeSieveMode.Automatic), typeof(ArgumentOutOfRangeException), "segmentBytes", "oversized count segment length")
            ?? Refuses(() => PrimeExploration.Count(high: 1, low: 2, mode: ((PrimeSieveMode)255), segmentBytes: 1), typeof(ArgumentOutOfRangeException), "mode", "invalid count mode"))));

        if (countFailure is not null) { return countFailure; }
        return (Refuses(() => PrimeExploration.Enumerate(2, 1, null!), typeof(ArgumentNullException), "onPrime", "null prime callback")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, null!, 1, PrimeSieveMode.Automatic), typeof(ArgumentNullException), "onPrime", "null prime callback under a policy")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, 0, PrimeSieveMode.Automatic), typeof(ArgumentOutOfRangeException), "segmentBytes", "zero segment length")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, -1, PrimeSieveMode.Automatic), typeof(ArgumentOutOfRangeException), "segmentBytes", "negative segment length")
            ?? (Refuses(() => PrimeExploration.Enumerate(2, 1, callback, int.MaxValue, PrimeSieveMode.Automatic), typeof(ArgumentOutOfRangeException), "segmentBytes", "segment length above Array.MaxLength")
            ?? Refuses(() => PrimeExploration.Enumerate(high: 1, low: 2, mode: ((PrimeSieveMode)255), onPrime: callback, segmentBytes: 1), typeof(ArgumentOutOfRangeException), "mode", "invalid sieve mode"))))));
    }
}
